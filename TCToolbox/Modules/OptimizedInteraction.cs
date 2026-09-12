using System;
using System.Collections.Generic;
using Dalamud.Bindings.ImGui;
using Dalamud.Hooking;
using Dalamud.Interface.Utility.Raii;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 解除互動限制：把遊戲阻擋互動的幾道<b>客端</b>檢查關掉——目標在視野外、被物件遮擋、
/// 位置過高過低、距離過遠、人物跳躍中、騎乘／低空飛行中。逐項開關。
/// 機制：hook 各檢查函式並回傳「通過」。不寫遊戲記憶體、不做 code patch、
/// <b>不自組封包也不攔封包</b>。
/// 參考 DailyRoutines OptimizedInteraction 設計重寫（API13、無 OmenTools 相依）。
/// </summary>
/// <remarks>
/// 每一支 detour 都先<b>原封不動呼叫 <c>Original</c></b>（參數照傳），再覆寫回傳值。
/// 這樣原函式的副作用（不論我們有沒有稽核到）全部照跑，我們只改變它的結論——
/// 「跳過原函式會不會漏掉什麼」這個問題因此根本不會發生。
/// detour 本身<b>不解參考任何參數</b>，就算參數型別推斷錯了也不會產生 AccessViolation。
/// <para>特徵碼掛不上時只記一行 Warning 並跳過該項，模組與外掛照常運作。</para>
/// <para><b>限制</b>：這些全部是<b>客端</b>檢查。伺服器有自己的一套判定，
/// 客端放行不代表伺服器會接受——可能出現「按了沒反應」而不是真的能互動。</para>
/// </remarks>
public sealed class OptimizedInteraction : TcModule
{
    public override string InternalName => "OptimizedInteraction";
    public override string DisplayName => "解除互動限制";

    public override string Description =>
        "把遊戲阻擋互動的客端檢查逐項關掉（視野外／被遮擋／位置過高過低／距離過遠／跳躍中／騎乘飛行中）。" +
        "作法是讓原本的判定照常執行、只覆寫它的結論，不寫記憶體、不改封包。" +
        "注意這些都是客端檢查，伺服器仍有自己的判定，放行不等於一定互動得到。";

    public override ModuleCategory Category => ModuleCategory.Misc;

    public override bool HasConfigUI => true;

    // ── 原生委派 ───────────────────────────────────────────────────────────────
    // 回傳布林的一律宣告成 byte：原生端只讀 al，用 byte 就不必去猜 bool 的封送寬度。
    // detour 不解參考參數，只是原樣轉交給 Original。

    private delegate byte IsObjectInViewRangeDelegate(nint targetSystem, nint gameObject);

    private delegate byte CameraObjectBlockedDelegate(nint targetSystem, nint camera, nint gameObject);

    private delegate byte CheckTargetPositionDelegate(
        nint eventFramework, nint source, nint target, ushort interactType, byte sendError);

    private delegate float CheckTargetDistanceDelegate(nint localPlayer, nint target);

    private delegate byte StateCheckDelegate(nint self);

    // ── 特徵碼（全部經離線掃描確認：唯一命中，且解出的位址就是上面註解列的那一支）──

    /// <summary>0x1405F6150；取自 FFXIVClientStructs 對 <c>TargetSystem.IsObjectInViewRange</c> 的宣告。</summary>
    private const string ViewRangeSignature = "48 85 D2 74 2C 4C 63 89";

    /// <summary>0x1405ED150；函式序言（DR 用的是呼叫點特徵碼，而且重複掛兩次）。</summary>
    private const string CameraBlockedSignature =
        "48 89 5C 24 ?? 48 89 74 24 ?? 57 48 83 EC ?? 49 8B 00 48 8B DA 48 8B F1 48 8D 54 24 ?? 49 8B C8 49 8B F8 FF 90";

    /// <summary>0x140B73680；函式序言。</summary>
    private const string TargetPositionSignature = "40 53 57 41 56 48 83 EC ?? 48 8B 02";

    /// <summary>
    /// 0x140856900；呼叫點特徵碼（<c>E8</c> 開頭，Dalamud 會跟隨到被呼叫的函式）。
    /// 這一支不能用序言特徵碼——主程式有四支序言相同的距離函式。
    /// </summary>
    private const string TargetDistanceSignature =
        "E8 ?? ?? ?? ?? 0F 2F 05 ?? ?? ?? ?? 76 ?? 48 8B 03 48 8B CB FF 50 ?? 48 8B C8 BA ?? ?? ?? ?? E8 ?? ?? ?? ?? EB";

    /// <summary>0x1416EE9A0；函式序言（整支只有 11 個位元組）。</summary>
    private const string Jumping0Signature = "83 B9 C0 00 00 00 00 0F 95 C0 C3";

    /// <summary>0x1416F1300；函式序言（整支只有 11 個位元組）。</summary>
    private const string Jumping1Signature = "48 8B 41 08 83 38 01 0F 94 C0 C3";

    /// <summary>0x14183B6E0；函式序言。</summary>
    private const string MountFlightSignature =
        "40 53 48 83 EC ?? 48 8D 99 ?? ?? ?? ?? 48 8B CB E8 ?? ?? ?? ?? 84 C0 75";

    // ── Hook 實體 ─────────────────────────────────────────────────────────────

    private Hook<IsObjectInViewRangeDelegate>? viewRangeHook;
    private Hook<CameraObjectBlockedDelegate>? cameraBlockedHook;
    private Hook<CheckTargetPositionDelegate>? targetPositionHook;
    private Hook<CheckTargetDistanceDelegate>? targetDistanceHook;
    private Hook<StateCheckDelegate>? jumping0Hook;
    private Hook<StateCheckDelegate>? jumping1Hook;
    private Hook<StateCheckDelegate>? mountFlightHook;

    /// <summary>特徵碼掛不上的項目；記住是為了不要每次同步都重掃、也不要洗版 log。</summary>
    private readonly HashSet<string> failedSignatures = [];

    private OptimizedInteractionConfig Config => Plugin.Instance.Config.OptimizedInteraction;

    protected override void OnEnable()
    {
        failedSignatures.Clear();
        SyncHooks();
    }

    protected override void OnDisable()
    {
        DisposeHook(ref viewRangeHook);
        DisposeHook(ref cameraBlockedHook);
        DisposeHook(ref targetPositionHook);
        DisposeHook(ref targetDistanceHook);
        DisposeHook(ref jumping0Hook);
        DisposeHook(ref jumping1Hook);
        DisposeHook(ref mountFlightHook);
        failedSignatures.Clear();
    }

    private static void DisposeHook<T>(ref Hook<T>? hook) where T : Delegate
    {
        hook?.Dispose();
        hook = null;
    }

    /// <summary>把每一項的「該不該掛」與實際狀態對齊。沒開的項目連 hook 都不會建立。</summary>
    private void SyncHooks()
    {
        Sync(Config.IgnoreViewRange, ref viewRangeHook, ViewRangeSignature,
             "目標在視野外", (IsObjectInViewRangeDelegate)ViewRangeDetour);

        Sync(Config.IgnoreCameraBlocked, ref cameraBlockedHook, CameraBlockedSignature,
             "目標被物件遮擋", (CameraObjectBlockedDelegate)CameraBlockedDetour);

        Sync(Config.IgnoreTargetPosition, ref targetPositionHook, TargetPositionSignature,
             "目標位置過高過低", (CheckTargetPositionDelegate)TargetPositionDetour);

        Sync(Config.IgnoreDistance, ref targetDistanceHook, TargetDistanceSignature,
             "目標距離過遠", (CheckTargetDistanceDelegate)TargetDistanceDetour);

        // 三支共用同一個「回傳 false」語意，但各自要呼叫自己那一支的 Original，
        // 所以不能共用一個 detour method group
        Sync(Config.IgnoreJumping, ref jumping0Hook, Jumping0Signature,
             "跳躍狀態判定 A", (StateCheckDelegate)Jumping0Detour);

        Sync(Config.IgnoreJumping, ref jumping1Hook, Jumping1Signature,
             "跳躍狀態判定 B", (StateCheckDelegate)Jumping1Detour);

        Sync(Config.IgnoreMountFlight, ref mountFlightHook, MountFlightSignature,
             "騎乘／低空飛行狀態判定", (StateCheckDelegate)MountFlightDetour);
    }

    private void Sync<T>(bool wanted, ref Hook<T>? hook, string signature, string label, T detour)
        where T : Delegate
    {
        if (!wanted)
        {
            DisposeHook(ref hook);
            return;
        }

        if (hook != null) return;
        if (failedSignatures.Contains(signature)) return;

        // 特徵碼掛不上是「這一項沒有」，不是「模組壞了」——絕不能讓例外往上冒到載入流程
        Hook<T>? created = null;
        try
        {
            if (!Svc.SigScanner.TryScanText(signature, out var address) || address == nint.Zero)
            {
                failedSignatures.Add(signature);
                Svc.Log.Warning($"[{InternalName}] 找不到「{label}」的特徵碼，這一項停用（其餘項目不受影響）");
                return;
            }

            created = Svc.Hooks.HookFromAddress(address, detour);

            // 先指派再 Enable：detour 要透過這個欄位呼叫 Original，
            // 若先 Enable，遊戲在指派完成前呼叫進來就會讀到 null
            hook = created;
            created.Enable();
            Svc.Log.Debug($"[{InternalName}] 已掛上「{label}」@ 0x{address:X}");
        }
        catch (Exception ex)
        {
            failedSignatures.Add(signature);
            // Enable() 半途失敗時 created 已經建立但還沒交給 hook，要自己收掉
            created?.Dispose();
            hook = null;
            Svc.Log.Warning(ex, $"[{InternalName}] 掛不上「{label}」，這一項停用（其餘項目不受影響）");
        }
    }

    // ── Detour ────────────────────────────────────────────────────────────────
    // 一律「先原樣呼叫 Original，再覆寫回傳值」。原函式的副作用照跑，我們只改結論。
    // detour 不解參考任何參數，所以參數型別推斷錯了也不會產生 AccessViolation。
    // Original 為 null 只可能發生在掛載競態的極短窗口，那時直接回傳強制值即可。

    /// <summary>1 ＝ 在視野範圍內。</summary>
    private byte ViewRangeDetour(nint targetSystem, nint gameObject)
    {
        viewRangeHook?.OriginalDisposeSafe(targetSystem, gameObject);
        return 1;
    }

    /// <summary>1 ＝ 鏡頭看得到、沒被擋住。</summary>
    private byte CameraBlockedDetour(nint targetSystem, nint camera, nint gameObject)
    {
        cameraBlockedHook?.OriginalDisposeSafe(targetSystem, camera, gameObject);
        return 1;
    }

    /// <summary>
    /// 1 ＝ 位置沒問題。這一支是七支裡唯一自己會送聊天錯誤訊息的，
    /// 所以呼叫 Original 時把 <c>sendError</c> 傳 0——其餘工作照做，只是不印那行字。
    /// </summary>
    private byte TargetPositionDetour(
        nint eventFramework, nint source, nint target, ushort interactType, byte sendError)
    {
        targetPositionHook?.OriginalDisposeSafe(eventFramework, source, target, interactType, 0);
        return 1;
    }

    /// <summary>0 公尺 ＝ 永遠在範圍內。</summary>
    private float TargetDistanceDetour(nint localPlayer, nint target)
    {
        targetDistanceHook?.OriginalDisposeSafe(localPlayer, target);
        return 0f;
    }

    /// <summary>0 ＝ 沒在跳躍。</summary>
    private byte Jumping0Detour(nint self)
    {
        jumping0Hook?.OriginalDisposeSafe(self);
        return 0;
    }

    /// <summary>0 ＝ 沒在跳躍。</summary>
    private byte Jumping1Detour(nint self)
    {
        jumping1Hook?.OriginalDisposeSafe(self);
        return 0;
    }

    /// <summary>0 ＝ 沒在騎乘／低空飛行。</summary>
    private byte MountFlightDetour(nint self)
    {
        mountFlightHook?.OriginalDisposeSafe(self);
        return 0;
    }

    // ── 設定 UI ───────────────────────────────────────────────────────────────

    public override void DrawConfig()
    {
        ImGui.TextDisabled("逐項開關，改完立刻生效（關掉的項目連 hook 都不會掛上去）。");
        ImGui.Spacing();

        DrawToggle(ref Config.IgnoreViewRange, "無視「目標處於視野之外」",
                   "背對目標、目標在畫面外時也能互動。");

        DrawToggle(ref Config.IgnoreCameraBlocked, "無視「目標被物件遮擋」",
                   "牆壁、柱子、其他玩家擋在中間時也能互動。");

        DrawToggle(ref Config.IgnoreTargetPosition, "無視「目標位置過高過低」",
                   "高低差過大時也能互動（這支同時管視野與跳躍的錯誤訊息）。");

        DrawToggle(ref Config.IgnoreDistance, "無視「距離太遠」",
                   "距離判定一律回傳 0 公尺。除了互動之外，交易與修理委託的離線判定也走同一支，" +
                   "所以走遠也不會自動中斷交易。");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextDisabled("以下兩項影響範圍比較大，預設關閉：");
        ImGui.Spacing();

        DrawToggle(ref Config.IgnoreJumping, "無視「跳躍中無法操作」",
                   "跳躍途中也能互動。⚠️ 這兩支狀態判定函式在遊戲裡各有 49／46 個呼叫端，" +
                   "涵蓋互動以外的系統，強制回報「沒在跳」可能讓其他地方的行為變得奇怪。");

        DrawToggle(ref Config.IgnoreMountFlight, "無視「騎乘／低空飛行中」",
                   "⚠️ 這支不在互動閘門上，而是在事件腳本的條件判定器裡（只有 2 個呼叫端），" +
                   "影響的是任務／事件腳本怎麼判斷你有沒有在騎乘飛行。不確定要不要開就別開。");

        if (failedSignatures.Count > 0)
        {
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.TextColored(new System.Numerics.Vector4(1f, 0.6f, 0.3f, 1f),
                              $"有 {failedSignatures.Count} 項的特徵碼掛不上（多半是遊戲改版），已自動跳過。");
            ImGui.SameLine();
            if (ImGui.Button("重試"))
            {
                failedSignatures.Clear();
                SyncHooks();
            }
        }
    }

    private void DrawToggle(ref bool value, string label, string help)
    {
        var v = value;
        if (ImGui.Checkbox(label, ref v))
        {
            value = v;
            Plugin.Instance.Config.Save();
            SyncHooks();
        }

        using (ImRaii.PushIndent())
        {
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
            ImGui.TextDisabled(help);
            ImGui.PopTextWrapPos();
        }
    }
}

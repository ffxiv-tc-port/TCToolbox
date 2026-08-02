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
/// <para><b>與 DR 原版的差異（三處，全部是離線反組譯逼出來的）</b></para>
/// <list type="number">
/// <item>
/// <b>DR 的 <c>InteractWithObject</c> hook 整支不移植。</b>那支 detour 會自己組
/// <c>EventStart</c> 封包送出去，踩本艦隊的封包紅線。少了它的副作用是：
/// 對乙太之光／特定物件的「自動下坐騎再互動」不會做，遇到要下坐騎的情境請自己下坐騎。
/// </item>
/// <item>
/// <b>DR 的 <c>InteractCheck0</c> hook 整支不移植——台服這支是死碼。</b>
/// DR 的特徵碼在台服<b>命中兩個不同函式的序言</b>（<c>0x1408E9E40</c> 與 <c>0x140B73A40</c>），
/// Dalamud 的 <c>ScanText</c> 只取第一個。實際檢查：
/// <list type="bullet">
/// <item><c>0x140B73A40</c> 才是互動用的那支（<c>cmp al,3</c>／<c>cmp al,7</c> ＝ EventNpc/EventObj，
/// 失敗時送 LogMessage 4510「正在共同騎乘坐騎，目前無法進行該操作。」），
/// 但它<b>全主程式零引用</b>——沒有 call、沒有 jmp、不在任何 vtable、沒有任何資料指標指向它。
/// 它的內容被<b>整段內聯</b>進唯一的呼叫者（互動總閘門 <c>0x140B6287D</c> 起，逐指令對得上）。
/// hook 它只會安靜地什麼都不發生。</item>
/// <item><c>0x1408E9E40</c>（Dalamud 實際會選到的那個）是<b>另一個系統的虛擬函式</b>
/// （vtable 槽位在 <c>0x142057888</c>），本體在處理一個 element size 0x68 的 vector。
/// 對它強制回傳 true 等於在不相干的函式上改控制流，可能崩潰。
/// </list>
/// 兩邊都不能 hook，所以整支拿掉。代價：騎乘雙人坐騎當乘客時仍然不能互動。
/// </item>
/// <item>
/// <b>DR 的 <c>CameraObjectBlocked</c> 與 <c>CheckCameraPosition</c> 在台服是同一支函式。</b>
/// 兩個特徵碼命中<b>同一個呼叫點</b> <c>0x1405F312B</c>（後者是前者的嚴格前綴），
/// 跟隨 <c>E8</c> 之後都解到 <c>0x1405ED150</c>。DR 會對同一位址掛兩個 Hook（行為未定義），
/// 這裡只留一支，並改用該函式的序言特徵碼（不再依賴呼叫點）。
/// </item>
/// </list>
///
/// <para><b>離線驗證過的 hook 目標（台服 7.20 ffxiv_dx11.exe，imageBase 0x140000000）</b></para>
/// <list type="bullet">
/// <item><c>0x1405F6150</c> <c>TargetSystem::IsObjectInViewRange</c>（特徵碼取自 CS 本身的宣告）
/// ——閘門失敗時送 LogMessage 1315「目標處於視野之外。」；11 個呼叫端。</item>
/// <item><c>0x1405ED150</c> 鏡頭遮擋判定——5 個呼叫端，全在 TargetSystem 群。</item>
/// <item><c>0x140B73680</c> 目標位置判定——5 個呼叫端，全在互動閘門群；
/// 自身會送 LogMessage 3180／3235／7748（「無法使用目標的功能」「視野之外」「跳躍中無法進行該操作」）。</item>
/// <item><c>0x140856900</c> 目標距離計算（回傳 float）——12 個呼叫端，全部是
/// <c>comiss</c> 比常數後送「距離太遠」類訊息（1310／10107／44）。
/// ⚠️ 主程式有四支序言幾乎相同的距離函式（0x140856280／0x140856590／0x140856900／0x141717B00），
/// 序言特徵碼分不開，所以這一支沿用 DR 的呼叫點特徵碼（唯一命中且解到 0x140856900，已離線核對）。</item>
/// <item><c>0x1416EE9A0</c>／<c>0x1416F1300</c> 跳躍狀態判定——就是互動閘門
/// <c>0x140B627FA</c>／<c>0x140B6280E</c> 連續呼叫的那兩支。
/// ⚠️ 這兩支各有 49／46 個呼叫端，遍及互動以外的系統，所以<b>預設關閉</b>。</item>
/// <item><c>0x14183B6E0</c> 騎乘／低空飛行狀態——只有 2 個呼叫端，且都在<b>事件腳本條件判定器</b>
/// 裡（不在互動閘門上），所以<b>預設關閉</b>。</item>
/// </list>
///
/// <para><b>安全性</b>：所有 detour 都<b>不解參考任何參數</b>，一律回傳常數，
/// 所以就算某支的參數型別推斷錯了也不會產生 AccessViolation。
/// 特徵碼掛不上時只記一行 Warning 並跳過該項，模組與外掛照常運作。</para>
///
/// <para><b>限制</b>：這些全部是<b>客端</b>檢查。伺服器有自己的一套判定，
/// 客端放行不代表伺服器會接受——可能出現「按了沒反應」而不是真的能互動。</para>
/// </remarks>
public sealed class OptimizedInteraction : TcModule
{
    public override string InternalName => "OptimizedInteraction";
    public override string DisplayName => "解除互動限制";

    public override string Description =>
        "把遊戲阻擋互動的客端檢查逐項關掉（視野外／被遮擋／位置過高過低／距離過遠／跳躍中／騎乘飛行中）。" +
        "只 hook 判定函式並回傳「通過」，不寫記憶體、不改封包。" +
        "注意這些都是客端檢查，伺服器仍有自己的判定，放行不等於一定互動得到。";

    public override bool HasConfigUI => true;

    // ── 原生委派 ───────────────────────────────────────────────────────────────
    // 回傳布林的一律宣告成 byte：原生端只讀 al，用 byte 就不必去猜 bool 的封送寬度。
    // 所有 detour 都不碰參數，參數只為了文件性而列出。

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

        Sync(Config.IgnoreJumping, ref jumping0Hook, Jumping0Signature,
             "跳躍狀態判定 A", (StateCheckDelegate)FalseDetour);

        Sync(Config.IgnoreJumping, ref jumping1Hook, Jumping1Signature,
             "跳躍狀態判定 B", (StateCheckDelegate)FalseDetour);

        Sync(Config.IgnoreMountFlight, ref mountFlightHook, MountFlightSignature,
             "騎乘／低空飛行狀態判定", (StateCheckDelegate)FalseDetour);
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
            created.Enable();
            hook = created;
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

    // ── Detour：一律回傳常數，完全不碰參數，所以不可能產生 AccessViolation ──────

    /// <summary>1 ＝ 在視野範圍內。</summary>
    private static byte ViewRangeDetour(nint targetSystem, nint gameObject) => 1;

    /// <summary>1 ＝ 鏡頭看得到、沒被擋住。</summary>
    private static byte CameraBlockedDetour(nint targetSystem, nint camera, nint gameObject) => 1;

    /// <summary>1 ＝ 位置沒問題。</summary>
    private static byte TargetPositionDetour(
        nint eventFramework, nint source, nint target, ushort interactType, byte sendError) => 1;

    /// <summary>0 公尺 ＝ 永遠在範圍內。</summary>
    private static float TargetDistanceDetour(nint localPlayer, nint target) => 0f;

    /// <summary>0 ＝ 沒有處於該狀態（跳躍／騎乘飛行）。</summary>
    private static byte FalseDetour(nint self) => 0;

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

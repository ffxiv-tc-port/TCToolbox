using System;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 任務受理視窗（<c>JournalAccept</c>）出現後自動按下「接受」。
/// 機制：每幀確認視窗狀態，取得 addon 上 id 44 的按鈕元件，按鈕本身是啟用狀態才複用它自己的
/// 事件送出點擊（<see cref="UiHelper.ClickButton"/>）。零 hook、零特徵碼、不寫記憶體。
/// </summary>
/// <remarks>
/// 🔴 <b>刻意不抄 DailyRoutines 的寫法</b>：DR 讀 <c>addon-&gt;AtkValues[261].UInt</c> 當任務 ID
/// 再 <c>Callback(3, id)</c>。<c>AtkValues</c> 是原生陣列<b>沒有邊界檢查</b>，261 這種裸索引
/// 只要遊戲改版縮短了值表就是任意記憶體讀取。
/// 這裡改用 TextAdvance 已經實機驗過的按鈕 id 44（<c>TextAdvance/Executors/ExecQuestAccept.cs</c>）。
/// </remarks>
public sealed unsafe class AutoQuestAccept : TcModule
{
    public override string InternalName => "AutoQuestAccept";
    public override string DisplayName => "自動接受任務";

    public override string Description =>
        "任務受理視窗出現後自動按下「接受」。走按鈕元件本身的點擊事件，不解析任務資料、不送封包。" +
        "⚠️ 開啟後連你只是想先看一下的任務也會被接下。TextAdvance 的「自動接受任務」開著時一律讓給它按，本模組不動作。";

    public override ModuleCategory Category => ModuleCategory.Misc;

    public override bool HasConfigUI => true;

    private const string AddonName = "JournalAccept";

    /// <summary>「接受」按鈕的元件 id（與 TextAdvance <c>ExecQuestAccept</c> 相同）。</summary>
    private const uint AcceptButtonId = 44;

    /// <summary>延遲的下限（毫秒）；設定值比它小時照它算。</summary>
    /// <remarks>TextAdvance 這類插件在視窗可以按的當下就按；至少晚這麼久，它按過的那一扇早已收掉，不會同一瞬間各按一次。</remarks>
    internal const int MinDelayMs = 500;

    /// <summary>實際生效的延遲：設定值不到 <see cref="MinDelayMs"/> 就用 <see cref="MinDelayMs"/>。不改寫存檔裡的值。</summary>
    internal static int EffectiveDelayMs(int configured) => Math.Max(configured, MinDelayMs);

    /// <summary>目前這扇視窗是什麼時候開始可以按的；視窗關閉時清空。</summary>
    private DateTime? visibleSince;

    /// <summary>這扇視窗已經判定交給 TextAdvance（或確認不了它會不會按），本模組不按。視窗關閉時清空。</summary>
    private bool yieldedThisWindow;

    private AutoQuestAcceptConfig Config => Plugin.Instance.Config.QuestAccept;

    protected override void OnEnable()
    {
        visibleSince = null;
        yieldedThisWindow = false;
        Svc.Framework.Update += OnUpdate;
    }

    protected override void OnDisable()
    {
        Svc.Framework.Update -= OnUpdate;
        visibleSince = null;
    }

    private void OnUpdate(IFramework framework)
    {
        var addon = UiHelper.GetAddon(AddonName);
        if (!UiHelper.IsReady(addon))
        {
            visibleSince = null;
            yieldedThisWindow = false;
            return;
        }

        if (yieldedThisWindow) return;

        // 用「視窗開著多久」計時，不用節流器——節流器第一次呼叫必定放行，
        // 拿它當延遲會變成「立刻按下去」，設定滑桿等於沒作用。
        visibleSince ??= DateTime.UtcNow;
        if (DateTime.UtcNow - visibleSince.Value < TimeSpan.FromMilliseconds(EffectiveDelayMs(Config.DelayMs))) return;

        // TextAdvance 也會按同一扇：它會按（或問不到）就整扇讓給它，不靠延遲錯開。
        var handling = TextAdvanceIpc.QueryQuestAcceptHandling(out var detail);
        if (handling is TextAdvanceIpc.RequestHandling.Active or TextAdvanceIpc.RequestHandling.Unknown)
        {
            yieldedThisWindow = true;
            if (Throttle.Pass("AutoQuestAccept-Yield", 60_000))
            {
                Svc.Log.Information(
                    $"[{InternalName}] 這扇任務受理視窗交給 TextAdvance（{detail}），本模組不按。" +
                    (handling == TextAdvanceIpc.RequestHandling.Unknown ? "問不到它會不會按，為了不兩邊各按一次而讓開。" : string.Empty));
            }

            return;
        }

        var button = addon->GetComponentButtonById(AcceptButtonId);
        if (button == null) return;

        // 🔴 不要在這裡先讀 button->IsEnabled。CS 的 AtkComponentButton.IsEnabled 是
        // `AtkComponentBase.OwnerNode->AtkResNode.NodeFlags.HasFlag(...)`——它解的是 +0xA8 的
        // OwnerNode（不是 +0xA0 的 AtkResNode），而且對它**零 null 檢查**，先讀等於自己開一個攔不到的存取違規口子。
        // 「能不能按」一律交給 UiHelper.ClickButton：它先驗 addon／button／OwnerNode 才讀 IsEnabled，回 false＝「現在按不動」。
        if (!UiHelper.ClickButton(addon, button)) return;

        // 按下去之後視窗就會關掉；萬一沒關（按鈕被遊戲重新啟用）也不要每幀重按。
        visibleSince = DateTime.UtcNow;

        if (Throttle.Pass("AutoQuestAccept-Log", 5_000))
            Svc.Log.Information($"[{InternalName}] 已自動接受任務（{AddonName} 按鈕 {AcceptButtonId}）。");
    }

    public override void DrawConfig()
    {
        ImGui.SetNextItemWidth(180f);
        // 顯示的是實際生效的值；存檔裡比下限小的舊值不主動改寫，使用者動了滑桿才存新值。
        var delay = EffectiveDelayMs(Config.DelayMs);
        if (ImGui.SliderInt("接受前的延遲（毫秒）", ref delay, MinDelayMs, 3_000))
        {
            Config.DelayMs = EffectiveDelayMs(delay);
            Plugin.Instance.Config.Save();
        }

        using (ImRaii.PushIndent())
        {
            ImGui.TextDisabled("視窗出現後至少等這麼久才按，讓你有機會先看清楚內容或自己關掉。");
            ImGui.TextDisabled(
                $"最少 {MinDelayMs} 毫秒：避免和其他會自動接任務的插件（例如 TextAdvance）在同一瞬間各按一次。");
        }
    }
}

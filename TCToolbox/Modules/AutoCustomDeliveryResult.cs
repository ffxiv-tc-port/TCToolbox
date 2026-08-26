using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Component.GUI;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 老主顧交易的結果視窗（<c>SatisfactionSupplyResult</c>）自動確認。
/// 機制：視窗開啟時對它送一次 callback（等同 <c>/callback SatisfactionSupplyResult true 1</c>），
/// 與按下視窗上的確認鈕同一條路徑。零 hook、零特徵碼、不寫記憶體。
/// </summary>
/// <remarks>
/// 📌 <b>這是 DailyRoutines <c>AutoQuestComplete</c> 缺的那一半</b>。DR 那個模組同時處理兩扇視窗：
/// <list type="bullet">
/// <item><c>JournalResult</c>（一般任務完成）—— <b>YesAlready 已經有了</b>
/// （<c>Features/JournalResult.cs</c>，設定在「Bothers → JournalResultComplete」），這裡不重複做。</item>
/// <item><c>SatisfactionSupplyResult</c>（老主顧交易結果）—— 全艦隊沒有任何外掛在處理，本模組補上。</item>
/// </list>
/// ⚠️ ECommons 有一個<b>從未被任何外掛使用</b>的 <c>AddonMaster.SatisfactionSupplyResult</c>，
/// 它把確認鈕標成元件 id 36。那個值沒有任何實機證據，所以這裡採用 DR 實際出貨的
/// <c>Callback(true, 1)</c>——那條路徑是有人在跑的。
/// </remarks>
public sealed unsafe class AutoCustomDeliveryResult : TcModule
{
    public override string InternalName => "AutoCustomDeliveryResult";
    public override string DisplayName => "自動確認老主顧交易結果";

    public override string Description =>
        "老主顧交易完成後跳出的結果視窗自動確認關閉。只針對這一扇視窗，其他確認視窗不受影響。" +
        "（一般任務的「完成」視窗請用 YesAlready 的 JournalResultComplete，本模組不重複處理。）";

    public override ModuleCategory Category => ModuleCategory.Company;

    public override bool HasConfigUI => true;

    private const string AddonName = "SatisfactionSupplyResult";

    private AutoCustomDeliveryResultConfig Config => Plugin.Instance.Config.CustomDeliveryResult;

    protected override void OnEnable()
    {
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, AddonName, OnAddon);

        // PostSetup 有可能在視窗還沒真的可以互動時就送出，補一條 PostDraw 當重試——
        // 兩者共用同一個節流器，所以最多每 DelayMs 送一次，不會變成每幀灌 callback。
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostDraw, AddonName, OnAddon);
    }

    protected override void OnDisable() => Svc.AddonLifecycle.UnregisterListener(OnAddon);

    private void OnAddon(AddonEvent type, AddonArgs args)
    {
        if (!Throttle.Pass("AutoCustomDeliveryResult-Confirm", System.Math.Max(200, Config.DelayMs))) return;

        var addon = (AtkUnitBase*)args.Addon.Address;
        if (!UiHelper.IsReady(addon)) return;

        UiHelper.FireCallback(addon, true, 1);

        if (Throttle.Pass("AutoCustomDeliveryResult-Log", 5_000))
            Svc.Log.Information($"[{InternalName}] 已自動確認老主顧交易結果視窗。");
    }

    public override void DrawConfig()
    {
        ImGui.SetNextItemWidth(180f);
        var delay = Config.DelayMs;
        if (ImGui.SliderInt("確認間隔（毫秒）", ref delay, 200, 3_000))
        {
            Config.DelayMs = delay;
            Plugin.Instance.Config.Save();
        }

        ImGui.TextDisabled("視窗沒有立刻關掉時的重送間隔，同時也是最短反應時間。");
    }
}

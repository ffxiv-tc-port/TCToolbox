using System;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 自動刷新招募板：「隊員招募」視窗開著的時候，每隔一段時間自動按一次更新。
/// 機制：呼叫 <c>AgentLookingForGroup.RequestListingsUpdate()</c>——與按下視窗上的「更新」鈕
/// 完全同一條路徑。零 hook、不寫記憶體、不做 patch。
/// 參考 DailyRoutines AutoRefreshPartyFinder 設計重寫（API13、無 OmenTools／KamiToolKit 相依）。
/// </summary>
/// <remarks>
/// <para>與 DR 原版的差異（這個模組會定期向伺服器發請求，所以刻意做得比 DR 保守）：</para>
/// <list type="bullet">
/// <item>DR 預設間隔 10 秒、下限 5 秒。這裡預設 <b>30 秒</b>、下限 <b>15 秒</b>——
/// 遊戲自己的更新鈕本來就有冷卻，沒有理由比它更積極。</item>
/// <item>DR 用 <c>System.Timers.Timer</c> 另一條執行緒計時再跳回主執行緒；這裡直接掛
/// <c>Framework.Update</c> 計時，不多開執行緒、也不會有 dispose 後還被回呼的問題。</item>
/// <item>DR 把倒數與設定畫成原生節點塞進遊戲視窗；這裡沿用本外掛既有作法用 ImGui 疊圖。</item>
/// <item>停止條件比 DR 多一項：除了招募板本身要開著、詳細視窗不能開著之外，
/// 招募條件視窗（<c>LookingForGroupCondition</c>）開著時也不刷新——
/// 你正在填條件的時候被刷掉清單很煩。</item>
/// </list>
/// </remarks>
public sealed unsafe class AutoRefreshPartyFinder : TcModule
{
    public override string InternalName => "AutoRefreshPartyFinder";
    public override string DisplayName => "自動刷新招募板";

    public override string Description =>
        "「隊員招募」視窗開著時，每隔一段時間自動按一次更新（間隔可調，預設 30 秒）。" +
        "關掉視窗、開啟某則招募的詳細內容、或正在編輯招募條件時都會暫停。";

    public override ModuleCategory Category => ModuleCategory.Combat;

    public override bool HasConfigUI => true;

    private const string PartyFinderAddon = "LookingForGroup";
    private const string DetailAddon = "LookingForGroupDetail";
    private const string ConditionAddon = "LookingForGroupCondition";

    /// <summary>下限刻意壓在遊戲自己更新鈕的冷卻之上。</summary>
    public const int MinIntervalSeconds = 15;

    public const int MaxIntervalSeconds = 600;

    private DateTime nextRefreshAt = DateTime.MaxValue;

    private AutoRefreshPartyFinderConfig Config => Plugin.Instance.Config.RefreshPartyFinder;

    protected override void OnEnable()
    {
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostRefresh, PartyFinderAddon, OnListRefreshed);
        Svc.Framework.Update += OnUpdate;
        Svc.PluginInterface.UiBuilder.Draw += DrawOverlay;
        ResetTimer();
    }

    protected override void OnDisable()
    {
        Svc.AddonLifecycle.UnregisterListener(OnListRefreshed);
        Svc.Framework.Update -= OnUpdate;
        Svc.PluginInterface.UiBuilder.Draw -= DrawOverlay;
        nextRefreshAt = DateTime.MaxValue;
    }

    private int IntervalSeconds => Math.Clamp(Config.IntervalSeconds, MinIntervalSeconds, MaxIntervalSeconds);

    private void ResetTimer() => nextRefreshAt = DateTime.UtcNow.AddSeconds(IntervalSeconds);

    /// <summary>清單自己更新過（手動按更新、或換分頁）就重新計時，不要疊在人家後面馬上又送一次。</summary>
    private void OnListRefreshed(AddonEvent type, AddonArgs args)
    {
        if (Config.OnlyWhenIdle) ResetTimer();
    }

    private bool CanRefresh()
    {
        if (!UiHelper.IsAddonReady(PartyFinderAddon)) return false;
        if (UiHelper.IsAddonReady(DetailAddon)) return false;
        if (UiHelper.IsAddonReady(ConditionAddon)) return false;
        return true;
    }

    private void OnUpdate(IFramework framework)
    {
        if (!CanRefresh())
        {
            // 視窗不在可刷新狀態就把倒數推回滿格，重新打開時不會立刻送出請求
            ResetTimer();
            return;
        }

        if (DateTime.UtcNow < nextRefreshAt) return;

        ResetTimer();

        try
        {
            var agent = AgentLookingForGroup.Instance();
            if (agent == null) return;

            agent->RequestListingsUpdate();
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, $"[{InternalName}] 送出招募清單更新失敗");
        }
    }

    private void DrawOverlay()
    {
        if (!Config.ShowCountdown) return;

        var addon = UiHelper.GetAddon(PartyFinderAddon);
        if (!UiHelper.IsReady(addon)) return;

        const ImGuiWindowFlags flags = ImGuiWindowFlags.AlwaysAutoResize |
                                       ImGuiWindowFlags.NoTitleBar |
                                       ImGuiWindowFlags.NoCollapse |
                                       ImGuiWindowFlags.NoScrollbar |
                                       ImGuiWindowFlags.NoSavedSettings;

        if (ImGui.Begin("###TCToolboxRefreshPartyFinder", flags))
        {
            ImGui.SetWindowPos(new System.Numerics.Vector2(
                                   addon->GetX() + 6, addon->GetY() - ImGui.GetWindowSize().Y - 4));

            if (!CanRefresh())
            {
                ImGui.TextDisabled("自動刷新：暫停中");
            }
            else
            {
                var remaining = (int)Math.Ceiling((nextRefreshAt - DateTime.UtcNow).TotalSeconds);
                if (remaining < 0) remaining = 0;
                ImGui.TextDisabled($"自動刷新：{remaining} 秒後");
            }

            ImGui.SameLine();
            if (ImGui.Button("立即刷新##pf"))
            {
                ResetTimer();
                var agent = AgentLookingForGroup.Instance();
                if (agent != null) agent->RequestListingsUpdate();
            }
        }

        ImGui.End();
    }

    public override void DrawConfig()
    {
        ImGui.SetNextItemWidth(200f);
        var interval = IntervalSeconds;
        if (ImGui.SliderInt("刷新間隔（秒）", ref interval, MinIntervalSeconds, MaxIntervalSeconds))
        {
            Config.IntervalSeconds = Math.Clamp(interval, MinIntervalSeconds, MaxIntervalSeconds);
            Plugin.Instance.Config.Save();
            ResetTimer();
        }

        using (ImRaii.PushIndent())
            ImGui.TextDisabled($"下限 {MinIntervalSeconds} 秒——遊戲自己的更新鈕就有冷卻，不要比它更積極。");

        var onlyIdle = Config.OnlyWhenIdle;
        if (ImGui.Checkbox("清單剛更新過就重新計時", ref onlyIdle))
        {
            Config.OnlyWhenIdle = onlyIdle;
            Plugin.Instance.Config.Save();
        }

        using (ImRaii.PushIndent())
            ImGui.TextDisabled("你自己按了更新、或切換分頁之後，倒數會重新算，不會馬上又送一次請求。");

        var showCountdown = Config.ShowCountdown;
        if (ImGui.Checkbox("在招募板上方顯示倒數", ref showCountdown))
        {
            Config.ShowCountdown = showCountdown;
            Plugin.Instance.Config.Save();
        }
    }
}

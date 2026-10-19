using System;
using System.Collections.Generic;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using TCToolbox.Core;
using TCToolbox.Windows;

namespace TCToolbox.Modules;

/// <summary>誰在控制：跑完了叫我 —— 背景輪詢那一格，有東西跑完就說一聲。</summary>
/// <remarks>
/// 🔴 <b>唯讀。</b>只問狀態端點，不叫任何外掛做事、不改任何別人的設定。
/// 🔴 <b>寧漏勿誤。</b>只認「本來有工作在跑、現在停了」這一種轉換，而且兩端都要是確定值；
/// 「有人壓著」變成「沒人壓著」不算——那多半只是使用者自己動了開關，我們分不出來。
/// ⚠️ <b>預設關</b>，開了之後每個來源也可以各自關掉：誤報會洗語音。
/// </remarks>
public sealed class FleetControlWatch : TcModule
{
    public override string InternalName => "FleetControlWatch";

    public override string DisplayName => "誰在控制：跑完了叫我";

    public override string Description =>
        "背景每隔幾秒問一次「現在是誰在控制」那一格（面板收著也照問），" +
        "看到導航或 AutoRetainer 的工作從「正在跑」變成「停了」就在聊天欄說一聲，" +
        "有裝塔塔露誇獎的話順便念一句。只認得出「工作跑完」這一種，" +
        "分不出是不是你自己按停的就不通知。唯讀，不會叫任何插件做事。";

    public override ModuleCategory Category => ModuleCategory.Misc;

    public override bool HasConfigUI => true;

    /// <summary>輪詢間隔的上下限（秒）。下限存在的理由是每一次輪詢都要打好幾支 IPC。</summary>
    public const int MinPollSeconds = 5;

    public const int MaxPollSeconds = 60;

    /// <summary>AutoRetainer 回報的「使用者關掉多角色模式」在輪詢間隔之外再認多久（毫秒）。</summary>
    /// <remarks>手動換角時多角色模式先關、登出登入跑完才變閒，要再等一次輪詢才看得到。</remarks>
    private const long AutoRetainerUserStopGraceMs = 60_000;

    /// <summary>
    /// 哪些來源值得通知，以及要念哪一個塔塔露情境。
    /// </summary>
    /// <remarks>
    /// 🔴 鍵引用 <see cref="FleetControlStatus"/> 的常數，不要在這裡重打字面值。
    /// 📌 只有這兩個來源的 Active 是「有工作在跑」；其餘幾列的 Active 是「有人壓著」，
    /// 它消失分不出是工作做完還是使用者自己解掉，所以不通知。
    /// </remarks>
    private static readonly Dictionary<string, string> PraiseCategories = new()
    {
        [FleetControlStatus.SubjectMovement] = TataruPraiseIpc.CategoryArrived,
        [FleetControlStatus.SubjectAutoRetainer] = TataruPraiseIpc.CategoryRetainer,
    };

    private FleetControlWatchConfig Config => Plugin.Instance.Config.FleetControlWatch;

    /// <summary>上一次看到的狀態（來源 → 狀態）。只有 framework 執行緒碰。</summary>
    private readonly Dictionary<string, SourceState> lastStates = [];

    /// <summary>這次啟用之後通知過幾次，純粹給面板顯示。</summary>
    private int notifyCount;

    private string lastNotifyText = string.Empty;

    /// <summary>上一次真的去問的時間。<c>0</c>＝還沒問過。</summary>
    private long lastPollAt;

    /// <summary>一個來源的上一次狀態。</summary>
    /// <param name="State">三態。</param>
    /// <param name="ActiveIsRunningWork">那個 Active 是「工作在跑」還是「有人壓著」。</param>
    /// <param name="Text">列上那句話，拿來寫進通知裡說明「本來是什麼」。</param>
    private readonly record struct SourceState(ControlState State, bool ActiveIsRunningWork, string Text);

    protected override void OnEnable()
    {
        lastStates.Clear();
        lastPollAt = 0;
        Svc.Framework.Update += OnFrameworkUpdate;
    }

    protected override void OnDisable()
    {
        Svc.Framework.Update -= OnFrameworkUpdate;
        lastStates.Clear();
    }

    /// <remarks>
    /// 🔴 整段包 <c>try</c>：這條路徑會打好幾個別的外掛的 IPC，而提供端擲出來的例外
    /// 在 framework 回呼裡沒有人接的話，本插件排在後面的 Update 處理器那一幀全部被跳過。
    /// </remarks>
    private void OnFrameworkUpdate(IFramework framework)
    {
        try
        {
            var now = Environment.TickCount64;
            var interval = Math.Clamp(Config.PollSeconds, MinPollSeconds, MaxPollSeconds) * 1_000L;
            if (lastPollAt != 0 && now - lastPollAt < interval) return;

            lastPollAt = now;
            Poll(interval);
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, $"[{InternalName}] 輪詢狀態時擲出例外，本次跳過。");
        }
    }

    private void Poll(long pollIntervalMs)
    {
        // 🔴 先拍下「是不是使用者自己剛剛停掉東西」，再去比對：急停之後接下來那幾秒的
        //    Active→Idle 幾乎全部是急停造成的，那是使用者主動停，不該說「跑完了」。
        var userStopped = RecentUserStop();

        foreach (var row in FleetControlStatus.Snapshot())
        {
            var current = new SourceState(row.State, row.ActiveIsRunningWork, row.Text);

            if (!lastStates.TryGetValue(row.Subject, out var previous))
            {
                // 第一次看到這個來源：只記下來。沒有「上一個狀態」就沒有轉換可談。
                lastStates[row.Subject] = current;
                continue;
            }

            lastStates[row.Subject] = current;

            if (!ShouldNotify(row.Subject, previous, current)) continue;

            // 只在真的要通知時才多問 AutoRetainer 一次：平常每輪輪詢不多打這支 IPC。
            var stoppedBy = userStopped.Length > 0 || row.Subject != FleetControlStatus.SubjectAutoRetainer
                ? userStopped
                : RecentAutoRetainerUserStop(pollIntervalMs);
            if (stoppedBy.Length > 0)
            {
                Svc.Log.Information(
                    $"[{InternalName}] {row.Subject} 從「{previous.Text}」變成「{current.Text}」，" +
                    $"但 {stoppedBy}，當成使用者主動停下，不通知。");
                continue;
            }

            // 每個來源自己的冷卻：同一件事在邊界上抖動時不要連續喊。
            var minutes = Math.Clamp(Config.MinIntervalMinutes, 1, 120);
            if (!Throttle.Pass($"FleetControlWatch-{row.Subject}", minutes * 60_000)) continue;

            Notify(row.Subject, previous, current);
        }
    }

    /// <summary>
    /// 這個轉換值不值得說一聲。
    /// </summary>
    /// <remarks>
    /// 🔴 三態比對<b>只在兩端都是確定值</b>時才成立：<see cref="ControlState.Unknown"/> 既不當
    /// Idle 也不當 Active，所以「Active→問不到→Idle」中間會斷掉，不會通知。那是刻意的。
    /// </remarks>
    private bool ShouldNotify(string subject, SourceState previous, SourceState current)
    {
        if (!PraiseCategories.ContainsKey(subject)) return false;
        if (Config.MutedSubjects.Contains(subject)) return false;

        return previous.State == ControlState.Active
               && previous.ActiveIsRunningWork
               && current.State == ControlState.Idle;
    }

    /// <summary>
    /// 最近有沒有「使用者自己把東西停下來」。
    /// </summary>
    /// <remarks>
    /// ⚠️ 這是近似值，不是真相：這裡只看得到本外掛自己的急停與停止移動
    /// （AutoRetainer 另由 <see cref="RecentAutoRetainerUserStop"/> 問它自己）。
    /// 🔑 所以判準是單向的——看得到就<b>不</b>通知，看不到並不代表「一定不是他停的」。
    /// </remarks>
    private string RecentUserStop()
    {
        if (NavStop.IsEnforcing) return "本插件正在強制停止移動";

        if (AutomationGate.TryGetEmergencyStopReason(out var reason)) return reason;

        var quiet = Math.Clamp(Config.EmergencyStopQuietSeconds, 0, 3_600);
        if (quiet == 0) return string.Empty;

        var last = AutomationGate.LastEmergencyStopUtc;
        if (last == DateTime.MinValue) return string.Empty;

        var elapsed = DateTime.UtcNow - last;
        return elapsed >= TimeSpan.Zero && elapsed.TotalSeconds < quiet
            ? $"{elapsed.TotalSeconds:F0} 秒前才執行過全艦隊急停"
            : string.Empty;
    }

    /// <summary>AutoRetainer 自己回報：多角色模式最近一次是使用者或別的插件關掉的，或你剛剛自己手動換了角色。</summary>
    /// <remarks>問不到（舊版 AutoRetainer 沒有這支端點）回空字串＝照舊通知。</remarks>
    private string RecentAutoRetainerUserStop(long pollIntervalMs)
    {
        if (!AutoRetainerIpc.TryGetMultiModeLastChange(out var enabled, out var cause, out var ago, out var error))
        {
            Svc.Log.Information($"[{InternalName}] 問不到 AutoRetainer 多角色模式最後一次是誰關的（{error}），照舊通知。");
            return string.Empty;
        }

        if (!enabled && ago >= 0 && ago <= pollIntervalMs + AutoRetainerUserStopGraceMs)
        {
            var byMultiMode = cause switch
            {
                AutoRetainerIpc.MultiModeCauseUser => $"AutoRetainer 回報 {ago / 1000} 秒前多角色模式是你自己關掉的",
                AutoRetainerIpc.MultiModeCauseOtherPlugin => $"AutoRetainer 回報 {ago / 1000} 秒前多角色模式是別的插件經 IPC 關掉的",
                _ => string.Empty,
            };
            if (byMultiMode.Length > 0) return byMultiMode;
        }

        return RecentAutoRetainerManualRelog(pollIntervalMs);
    }

    /// <summary>AutoRetainer 自己回報：最近一次手動換角是你自己（或經別的插件）叫的。</summary>
    /// <remarks>多角色模式本來就關著時手動換角不會留下開關紀錄，但換角任務一樣會讓它忙一下又閒下來。問不到回空字串＝照舊通知。</remarks>
    private string RecentAutoRetainerManualRelog(long pollIntervalMs)
    {
        if (!AutoRetainerIpc.TryGetLastManualRelog(out var ago, out var cause, out var error))
        {
            Svc.Log.Information($"[{InternalName}] 問不到 AutoRetainer 最後一次手動換角是誰叫的（{error}），照舊通知。");
            return string.Empty;
        }

        if (ago < 0 || ago > pollIntervalMs + AutoRetainerUserStopGraceMs) return string.Empty;

        // 別的插件經 IPC 叫的換角也算：會這樣叫的只有本插件的切換角色，每次都是使用者按的。
        return cause switch
        {
            AutoRetainerIpc.MultiModeCauseUser => $"AutoRetainer 回報 {ago / 1000} 秒前你自己手動換了角色",
            AutoRetainerIpc.MultiModeCauseOtherPlugin => $"AutoRetainer 回報 {ago / 1000} 秒前別的插件經 IPC 叫它換了角色",
            _ => string.Empty,
        };
    }

    private void Notify(string subject, SourceState previous, SourceState current)
    {
        notifyCount++;
        lastNotifyText = $"{subject}：{previous.Text} → {current.Text}";

        Svc.Log.Information($"[{InternalName}] 通知：{lastNotifyText}");
        Svc.Chat.Print($"[TC Toolbox] {subject} 跑完了（{previous.Text} → {current.Text}）。");

        if (PraiseCategories.TryGetValue(subject, out var category))
            TataruPraiseIpc.TryPraise(category, $"{InternalName}／{subject} 跑完了");
    }

    public override void DrawConfig()
    {
        var poll = Config.PollSeconds;
        ImGui.SetNextItemWidth(240f);
        if (ImGui.SliderInt("多久問一次（秒）##fleetControlWatchPoll", ref poll, MinPollSeconds, MaxPollSeconds))
        {
            Config.PollSeconds = poll;
            Plugin.Instance.Config.Save();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "每一次輪詢都要打好幾個插件的 IPC，所以有下限。\n" +
                "調短只會讓「跑完了」這句話早幾秒出現，不會讓它更準。");
        }

        var minutes = Config.MinIntervalMinutes;
        ImGui.SetNextItemWidth(240f);
        if (ImGui.SliderInt("同一個來源最多多久叫一次（分鐘）##fleetControlWatchInterval", ref minutes, 1, 120))
        {
            Config.MinIntervalMinutes = minutes;
            Plugin.Instance.Config.Save();
        }

        var quiet = Config.EmergencyStopQuietSeconds;
        ImGui.SetNextItemWidth(240f);
        if (ImGui.SliderInt("急停之後安靜幾秒##fleetControlWatchQuiet", ref quiet, 0, 600))
        {
            Config.EmergencyStopQuietSeconds = quiet;
            Plugin.Instance.Config.Save();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "你自己按了全艦隊急停之後，東西當然會停——那不是「跑完了」。\n" +
                "這段時間內看到的停止一律不通知（記錄裡還是寫得到）。\n" +
                "0＝不做這道過濾。");
        }

        ImGui.Separator();
        ImGui.TextDisabled("要通知的來源");

        foreach (var pair in PraiseCategories)
        {
            var on = !Config.MutedSubjects.Contains(pair.Key);
            if (ImGui.Checkbox($"{pair.Key}##fleetControlWatch{pair.Key}", ref on))
            {
                if (on) Config.MutedSubjects.Remove(pair.Key);
                else Config.MutedSubjects.Add(pair.Key);
                Plugin.Instance.Config.Save();
            }

            ImGui.SameLine();
            ImGui.TextDisabled($"（塔塔露情境「{pair.Value}」）");
        }

        ImGui.TextDisabled("其他幾列只問得到「有沒有人壓著」，刻意不通知。");
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "「vnavmesh 移動開關」「YesAlready 被誰壓著」「AutoHook 被壓著」這幾列，\n" +
                "Active 的意思是「有人壓著或開關被關掉」，它變回 Idle 常常只是你自己\n" +
                "把開關撥回去——那不是「跑完了」。分不出來就不通知，寧可漏掉。\n" +
                "另外「問不到」永遠不當成 Idle，所以「跑著→問不到→閒著」不會叫。");
        }

        ImGui.Separator();
        ImGui.TextDisabled(notifyCount == 0
            ? "這次啟用之後還沒有通知過。"
            : $"這次啟用之後通知過 {notifyCount} 次，最後一次：{lastNotifyText}");

        ImGui.Separator();
        ControlStatusPanel.Draw();
    }
}

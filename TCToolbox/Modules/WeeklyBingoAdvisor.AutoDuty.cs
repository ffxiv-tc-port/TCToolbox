using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface.Utility.Raii;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using TCToolbox.Core;
using static TCToolbox.Core.BingoAutoDutyRules;

namespace TCToolbox.Modules;

/// <summary>
/// 「用 AutoDuty 跑這格」（<see cref="WeeklyBingoAdvisorConfig.RunWithAutoDuty"/>，預設關）：
/// 「下一個印花貼哪一格」表多一欄，按一下就請 AutoDuty 用劇情輔助器跑那個副本一輪。
/// </summary>
/// <remarks>
/// 🔴 按鈕只記下目標（<see cref="pendingAutoDutyRun"/>），IPC 在下一次 <c>Framework.Update</c> 才打；Draw 只讀快照。
/// 🔴 模式、解除限制、輪數一律走 AutoDuty 的暫時覆寫層；端點不在或拒收就不跑，<b>刻意不退回 <c>SetConfig</c></b>
/// （那會永久改掉使用者的 AutoDuty 設定）。
/// 📌 覆寫是全域的（任何外掛 Pop 都會清掉），所以輪數另外用 <c>Run</c> 的參數給，不靠被覆寫的 <c>LoopTimes</c>。
/// </remarks>
public sealed unsafe partial class WeeklyBingoAdvisor
{
    /// <summary>每按一次只跑一輪。</summary>
    private const int AutoDutyLoops = 1;

    /// <summary>不做 AutoDuty 的開跑前／輪間／結束後動作（修裝、回旅館、結束指令等）：按一次只跑那一趟。</summary>
    private const bool AutoDutyBareMode = true;

    /// <summary>AutoDuty 的狀態多久問一次（只在選項與面板都開著時問）。</summary>
    private const int AutoDutyPollMs = 1_000;

    /// <summary>上一次按下去的結果在面板上留多久。</summary>
    private const long AutoDutyResultShowMs = 60_000;

    private const string AutoDutyPollKey = "WeeklyBingoAdvisor-AutoDutyPoll";

    private const string AutoDutyBlockedLogKey = "WeeklyBingoAdvisor-AutoDutyBlocked";

    private const int AutoDutyBlockedLogMs = 600_000;

    private const string AutoDutyRunTooltip =
        "用 NPC 隊友進去，不排任務搜尋器、不和路人組隊；每按一次只跑一次。\n"
        + "模式與輪數是暫時設定，AutoDuty 停下來就恢復你自己的設定；"
        + "也不做 AutoDuty 的開跑前／輪間／結束後動作（修裝、回旅店、結束指令等）。\n"
        + "副本要已經解鎖、平均品級要夠，否則 AutoDuty 會停在劇情輔助器視窗，按 AutoDuty 的停止就好。\n"
        + "⚠ 天書承不承認劇情輔助器通關，遊戲資料裡查不到："
        + "跑完聊天欄沒出現「完成了天書奇談中指定的任務」就是不算，請改用任務搜尋器。";

    private const string AutoDutyConfigTooltip =
        "開啟後，「下一個印花貼哪一格」表多一欄 AutoDuty。\n"
        + "只有這幾件事都成立時才出現「跑一輪」按鈕：這一格對得到副本（不是輪盤）、還沒完成、那個副本有劇情輔助器、"
        + "AutoDuty 有它的路徑檔、AutoDuty 目前停著。\n"
        + "「51-59級迷宮」這類等級區間的格子，會在符合的副本裡挑有劇情輔助器、AutoDuty 有路徑檔、"
        + "目前職業等級進得去的裡面平均品級要求最低的（同分挑等級低的），按鈕下面寫出挑的是哪個。\n"
        + "按一下就請 AutoDuty 用劇情輔助器（NPC 隊友）跑那個副本一輪；"
        + "不在副本裡、沒在排隊、不在小隊裡、也沒有別的自動化在動時才會開跑。\n"
        + "模式與輪數只是暫時設定，AutoDuty 停下來就恢復你自己的設定；"
        + "AutoDuty 版本太舊、不支援暫時設定時就不跑，不會去改你的 AutoDuty 設定。\n"
        + "⚠ 天書承不承認劇情輔助器通關，遊戲資料裡查不到："
        + "跑完聊天欄沒出現「完成了天書奇談中指定的任務」就是不算。";

    private static readonly Vector4 AutoDutyWarnColor = new(1f, 0.8f, 0.35f, 1f);

    /// <summary>按鈕按下去時記下的目標（下一次 Update 才執行）。FromBand＝等級區間格挑出來的副本。</summary>
    private readonly record struct PendingAutoDutyRun(int Slot, uint CfcId, string Name, bool FromBand);

    /// <summary>
    /// 某一格上一次輪詢的判定。Blocker 不是空字串＝按鈕在，但現在不能按；CfcId／Name＝按下去會跑的副本；
    /// Reason＝不給按鈕的原因；BandNote 不是空字串＝等級區間格，內容是挑選說明。
    /// </summary>
    private readonly record struct AutoDutyRowState(
        RunButtonVerdict Verdict, string Blocker, uint CfcId, string Name, string Reason, string BandNote);

    // ── 以下欄位只在遊戲主執行緒讀寫（Framework.Update 與 ImGui 的 Draw）──

    private PendingAutoDutyRun? pendingAutoDutyRun;

    /// <summary>這次啟用之後問過 AutoDuty 沒有。</summary>
    private bool autoDutyPolled;

    private AutoDutyState autoDutyState;

    /// <summary>格子編號 → 上一次輪詢的判定。整份換掉，不就地改。</summary>
    private IReadOnlyDictionary<int, AutoDutyRowState> autoDutyRows = new Dictionary<int, AutoDutyRowState>();

    private string autoDutyResult = string.Empty;

    private bool autoDutyResultIsError;

    private long autoDutyResultAt;

    private void ResetAutoDuty()
    {
        pendingAutoDutyRun = null;
        autoDutyPolled = false;
        autoDutyState = AutoDutyState.Unknown;
        autoDutyRows = new Dictionary<int, AutoDutyRowState>();
        autoDutyResult = string.Empty;
        autoDutyResultIsError = false;
        autoDutyResultAt = 0;
        Throttle.Reset(AutoDutyPollKey);
    }

    /// <summary>執行上一幀按下的「跑一輪」。只在 <c>Framework.Update</c> 呼叫。</summary>
    private void RunPendingAutoDuty()
    {
        if (pendingAutoDutyRun is not { } run) return;
        pendingAutoDutyRun = null;

        uint territory = 0;
        bool started;
        string outcome;
        try
        {
            started = TryStartAutoDutyRun(run, out territory, out outcome);
        }
        catch (Exception ex)
        {
            started = false;
            outcome = $"發生例外：{ex.Message}";
            Svc.Log.Information($"[{InternalName}] 請 AutoDuty 開跑時發生例外：{ex}");
        }

        // 要使用者回報的診斷：「按了之後 AutoDuty 為什麼沒動」只能從這一行回推。
        Svc.Log.Information(
            $"[{InternalName}] 用 AutoDuty 跑天書第 {run.Slot + 1} 格「{run.Name}」"
            + $"（CFC {run.CfcId}／區域 {territory}{(run.FromBand ? "，從等級區間挑出" : string.Empty)}，"
            + $"劇情輔助器 Support、{AutoDutyLoops} 輪、不做前後動作）："
            + $"{(started ? "已開跑" : "沒有開跑")}。{outcome}");

        autoDutyResult = started
            ? $"已請 AutoDuty 用劇情輔助器跑「{run.Name}」一輪。{outcome}"
            : $"沒有開跑「{run.Name}」：{outcome}";
        autoDutyResultIsError = !started;
        autoDutyResultAt = Environment.TickCount64;

        if (started)
            Svc.Chat.Print($"[TC Toolbox] {autoDutyResult}");
        else
            Svc.Chat.PrintError($"[TC Toolbox] {autoDutyResult}");

        Throttle.Reset(AutoDutyPollKey);
    }

    /// <summary>全部重新檢查一遍，都過了才暫時覆寫設定並請 AutoDuty 跑一輪。</summary>
    /// <returns>AutoDuty 真的開跑了才回 <see langword="true"/>；<paramref name="outcome"/> 是原因或補充說明。</returns>
    private bool TryStartAutoDutyRun(PendingAutoDutyRun run, out uint territory, out string outcome)
    {
        if (!TryCheckAutoDutyTarget(run, out territory, out outcome)) return false;

        var blocker = DescribeAutoDutyBlocker(run.CfcId);
        if (blocker.Length > 0)
        {
            outcome = $"現在不能開跑：{blocker}。";
            return false;
        }

        if (!AutoDutyIpc.TryIsStopped(out var stopped))
        {
            outcome = Describe(RunButtonVerdict.AutoDutyUnavailable);
            return false;
        }

        if (!stopped)
        {
            outcome = Describe(RunButtonVerdict.AutoDutyRunning);
            return false;
        }

        if (!AutoDutyIpc.TryContentHasPath(territory, out var hasPath))
        {
            outcome = Describe(RunButtonVerdict.PathUnknown);
            return false;
        }

        if (!hasPath)
        {
            outcome = Describe(RunButtonVerdict.NoPath);
            return false;
        }

        var pushed = AutoDutyIpc.PushOverrides(BuildAutoDutyOverrides());
        if (pushed != AutoDutyIpc.OverrideResult.Accepted)
        {
            outcome = pushed == AutoDutyIpc.OverrideResult.Rejected
                ? "AutoDuty 版本不支援暫時設定（它拒收了這組設定，原因在 AutoDuty 的 log），請更新或手動跑。"
                : "AutoDuty 版本不支援暫時設定，請更新或手動跑。";
            return false;
        }

        var runOk = AutoDutyIpc.TryRun(territory, AutoDutyLoops, AutoDutyBareMode, out var runError);

        // AutoDuty 在框架執行緒上就地執行 Run：回來時還停著＝沒開始，也就等不到它停止時自己的還原。
        // 問不到就不還原：還原是全域的，萬一它其實在跑，模式會退回使用者自己的（可能是一般模式）。
        if (!AutoDutyIpc.TryIsStopped(out var stillStopped))
        {
            outcome = "已把指令交給 AutoDuty，但之後問不到它有沒有開跑，請看 AutoDuty 的視窗。";
            return false;
        }

        if (stillStopped)
        {
            AutoDutyIpc.PopOverrides();
            outcome = runOk ? "AutoDuty 收下指令但沒有開跑（原因在 AutoDuty 的 log）。" : $"AutoDuty 沒有開跑：{runError}。";
            return false;
        }

        outcome = runOk ? string.Empty : $"（AutoDuty 回報了例外，但已經開跑：{runError}）";
        return true;
    }

    /// <summary>這一格還是不是按下去時的那個副本、那個副本還能不能用劇情輔助器跑。</summary>
    private bool TryCheckAutoDutyTarget(PendingAutoDutyRun run, out uint territory, out string reason)
    {
        territory = 0;

        if (!Config.RunWithAutoDuty)
        {
            reason = Describe(RunButtonVerdict.OptionOff);
            return false;
        }

        var playerState = PlayerState.Instance();
        if (playerState == null || !playerState->HasWeeklyBingoJournal)
        {
            reason = "讀不到天書。";
            return false;
        }

        if (playerState->GetWeeklyBingoTaskStatus(run.Slot) != PlayerState.WeeklyBingoTaskStatus.Open)
        {
            reason = Describe(RunButtonVerdict.AlreadyDone);
            return false;
        }

        var orderRowId = (uint)playerState->WeeklyBingoOrderData[run.Slot];
        if (!CellStillAccepts(orderRowId, run.CfcId))
        {
            reason = run.FromBand
                ? $"這一格已經不包含「{run.Name}」了，請看過面板再按一次。"
                : "這一格對應的副本和按下去時不一樣了，請看過面板再按一次。";
            return false;
        }

        if (!DutySupportData.HasSupportMode(run.CfcId))
        {
            reason = Describe(RunButtonVerdict.NoDutySupport);
            return false;
        }

        territory = DutySupportData.GetTerritoryType(run.CfcId);
        if (territory == 0)
        {
            reason = "查不到這個副本在哪個區域。";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// 這一格現在還算不算數：等級區間格要求副本仍在符合的清單裡，單一副本格要求解析結果仍是同一個副本。
    /// 區間格不要求它仍是「挑中的那個」——其餘各項另外針對這個副本重檢。
    /// </summary>
    private static bool CellStillAccepts(uint orderRowId, uint cfcId)
    {
        if (orderRowId == 0) return false;

        var band = WeeklyBingoDutyResolver.GetLevelBandCandidates(orderRowId);
        if (band.Count > 0) return band.Contains(cfcId);

        var target = WeeklyBingoDutyResolver.Resolve(orderRowId);
        return target.Kind == BingoTargetKind.Duty && target.Id == cfcId;
    }

    /// <summary>交給 AutoDuty 的暫時設定：劇情輔助器、不解除限制、一輪。</summary>
    /// <remarks>LoopTimes 一起覆寫，只是讓 <c>Run</c> 自己寫回的輪數落在被覆寫的欄位上（存檔寫使用者的值）。</remarks>
    private static Dictionary<string, string> BuildAutoDutyOverrides() => new()
    {
        ["Unsynced"] = "False",
        ["dutyModeEnum"] = "Support",
        ["LoopTimes"] = AutoDutyLoops.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>現在不能開跑的原因；可以開跑時回空字串。只在框架執行緒呼叫。</summary>
    private static string DescribeAutoDutyBlocker(uint cfcId)
    {
        var shared = DescribeSharedAutoDutyBlocker();
        return shared.Length > 0 ? shared : DescribeJobBlocker(cfcId);
    }

    /// <summary>與哪一格無關的原因：角色在忙、已經在副本裡或在排隊、在小隊裡、別的自動化在動。</summary>
    private static string DescribeSharedAutoDutyBlocker()
    {
        if (AutomationGate.TryGetBusyReason(out var busy)) return busy;

        if (Svc.Condition[ConditionFlag.BoundByDuty56] || Svc.Condition[ConditionFlag.BoundByDuty95]) return "副本中";

        if (Svc.Condition[ConditionFlag.InDutyQueue] || Svc.Condition[ConditionFlag.WaitingForDutyFinder])
            return "正在申請參加任務";

        // 與 AutoDuty 自己介面上的判準相同：劇情輔助器只能一個人進去。
        if (Svc.Party.PartyId > 0) return "在小隊裡（劇情輔助器只能一個人進入）";

        var questRunning = QuestionableIpc.TryIsRunning(out var running)
            ? running
            : PluginPresence.IsLoaded("Questionable") != false;
        return questRunning ? "Questionable 正在跑任務（或問不到它）" : string.Empty;
    }

    /// <summary>目前的職業能不能用劇情輔助器進這個副本；可以時回空字串。</summary>
    private static string DescribeJobBlocker(uint cfcId)
    {
        if (Svc.PlayerState.ClassJob.ValueNullable is not { } job) return "讀不到目前的職業";
        if (job.Role == 0) return "目前是製作或採集職業，劇情輔助器只能用戰鬥職業";
        if (job.IsLimitedJob) return $"{job.Name.ExtractText()}不能用劇情輔助器";

        var required = DutySupportData.GetRequiredLevel(cfcId);
        var level = Svc.PlayerState.Level;
        return level < required ? $"目前職業 {level} 級，這個副本要 {required} 級" : string.Empty;
    }

    /// <summary>每秒問一次 AutoDuty，把「下一個印花貼哪一格」每一列的判定整份換掉。只在 <c>Framework.Update</c> 呼叫。</summary>
    private void PollAutoDuty()
    {
        if (!Config.RunWithAutoDuty || !Throttle.Pass(AutoDutyPollKey, AutoDutyPollMs)) return;

        autoDutyState = !AutoDutyIpc.TryIsStopped(out var stopped)
            ? AutoDutyState.Unknown
            : stopped ? AutoDutyState.Stopped : AutoDutyState.Running;
        autoDutyPolled = true;

        var playerState = PlayerState.Instance();
        var rows = new Dictionary<int, AutoDutyRowState>(NextCell.Count);
        var paths = new Dictionary<uint, PathState>();
        string? shared = null;

        foreach (var row in NextCell)
        {
            var done = playerState == null
                       || playerState->GetWeeklyBingoTaskStatus(row.Slot) != PlayerState.WeeklyBingoTaskStatus.Open;
            var orderRowId = playerState == null ? 0u : (uint)playerState->WeeklyBingoOrderData[row.Slot];
            var band = orderRowId == 0 ? [] : WeeklyBingoDutyResolver.GetLevelBandCandidates(orderRowId);

            rows[row.Slot] = band.Count > 0
                ? PollBandRow(band, done, paths, ref shared)
                : PollSingleRow(row, done, paths, ref shared);
        }

        autoDutyRows = rows;

        // 按鈕在卻按不下去時留一行：萬一某個狀態一直擋著（例如開著天書本身就算「事件中」），從 log 就看得出來。
        if (!string.IsNullOrEmpty(shared) && Throttle.Pass(AutoDutyBlockedLogKey, AutoDutyBlockedLogMs))
            Svc.Log.Information($"[{InternalName}] 「跑一輪」按鈕現在按不下去：{shared}（10 分鐘內只記一次）。");
    }

    /// <summary>單一副本格：只看解析出來的那一個副本。</summary>
    private AutoDutyRowState PollSingleRow(
        BingoSuggestion row, bool done, Dictionary<uint, PathState> paths, ref string? shared)
    {
        var isDuty = row.Target.Kind == BingoTargetKind.Duty;
        var support = isDuty && DutySupportData.HasSupportMode(row.Target.Id);

        var path = PathState.Unknown;
        if (support && autoDutyState != AutoDutyState.Unknown)
            path = QueryAutoDutyPath(DutySupportData.GetTerritoryType(row.Target.Id), paths);

        var verdict = Evaluate(true, isDuty, done, support, autoDutyState, path);
        if (verdict != RunButtonVerdict.Show)
            return new AutoDutyRowState(verdict, string.Empty, 0, string.Empty, Describe(verdict), string.Empty);

        shared ??= DescribeSharedAutoDutyBlocker();
        var blocker = shared.Length > 0 ? shared : DescribeJobBlocker(row.Target.Id);
        return new AutoDutyRowState(verdict, blocker, row.Target.Id, row.Target.Name, string.Empty, string.Empty);
    }

    /// <summary>等級區間格：在符合的副本裡挑一個（挑法見 <see cref="EvaluateBand"/>）。</summary>
    private AutoDutyRowState PollBandRow(
        IReadOnlyList<uint> band, bool done, Dictionary<uint, PathState> paths, ref string? shared)
    {
        var count = band.Count;
        var support = new bool[count];
        var pathStates = new PathState[count];
        var levels = new int[count];
        var itemLevels = new int[count];
        for (var i = 0; i < count; i++)
        {
            support[i] = DutySupportData.HasSupportMode(band[i]);
            levels[i] = DutySupportData.GetRequiredLevel(band[i]);
            itemLevels[i] = DutySupportData.GetItemLevelRequired(band[i]);
            if (support[i] && !done && autoDutyState != AutoDutyState.Unknown)
                pathStates[i] = QueryAutoDutyPath(DutySupportData.GetTerritoryType(band[i]), paths);
        }

        var verdict = EvaluateBand(
            true, done, autoDutyState, support, pathStates, levels, itemLevels, Svc.PlayerState.Level, out var pick);
        if (verdict != RunButtonVerdict.Show)
            return new AutoDutyRowState(verdict, string.Empty, 0, string.Empty, DescribeBand(verdict, count), string.Empty);

        var cfcId = band[pick];
        var name = DutySupportData.GetDutyName(cfcId);
        var runnable = 0;
        for (var i = 0; i < count; i++)
        {
            if (support[i] && pathStates[i] == PathState.Has) runnable++;
        }

        var itemLevel = itemLevels[pick] > 0 ? $"平均品級要求 {itemLevels[pick]}" : "沒有平均品級要求";
        var note = $"這一格是等級區間：符合又有劇情輔助器、AutoDuty 有路徑檔的副本有 {runnable} 個，"
                   + "挑目前職業等級進得去的裡面平均品級要求最低的（同分挑等級低的），最不容易因為品級不夠卡住。\n"
                   + $"這次挑的是「{name}」（{levels[pick]} 級、{itemLevel}）；換職業或升級後可能換一個。";

        shared ??= DescribeSharedAutoDutyBlocker();
        var blocker = shared.Length > 0 ? shared : DescribeJobBlocker(cfcId);
        return new AutoDutyRowState(verdict, blocker, cfcId, name, string.Empty, note);
    }

    private static PathState QueryAutoDutyPath(uint territory, Dictionary<uint, PathState> cache)
    {
        if (territory == 0) return PathState.Missing;
        if (cache.TryGetValue(territory, out var known)) return known;

        var state = !AutoDutyIpc.TryContentHasPath(territory, out var has)
            ? PathState.Unknown
            : has ? PathState.Has : PathState.Missing;
        cache[territory] = state;
        return state;
    }

    private void DrawAutoDutyConfig()
    {
        var runWithAutoDuty = Config.RunWithAutoDuty;
        if (ImGui.Checkbox("「用 AutoDuty 跑這格」：建議表多一顆「跑一輪」按鈕##wtAutoDuty", ref runWithAutoDuty))
        {
            Config.RunWithAutoDuty = runWithAutoDuty;
            Plugin.Instance.Config.Save();
            ResetAutoDuty();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(AutoDutyConfigTooltip);

        using (ImRaii.PushIndent())
            ImGui.TextDisabled("預設關。需要另外安裝 AutoDuty；只有劇情輔助器進得去的副本才有按鈕，不會替你排任務搜尋器。");
    }

    /// <summary>「下一個印花貼哪一格」表上方的 AutoDuty 狀態與上一次的結果。只讀快照。</summary>
    private void DrawAutoDutyStatus()
    {
        using var indent = ImRaii.PushIndent();
        using var wrap = ImRaii.TextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);

        if (!autoDutyPolled)
            ImGui.TextDisabled("AutoDuty：？（還沒問過，一秒內會更新）");
        else if (autoDutyState == AutoDutyState.Unknown)
            ImGui.TextDisabled("AutoDuty：？ 問不到（沒安裝、沒載入，或版本太舊），不會出現「跑一輪」按鈕。");
        else if (autoDutyState == AutoDutyState.Running)
            ImGui.TextDisabled("AutoDuty 正在跑（或暫停中），停下來之後才會出現「跑一輪」按鈕。");

        if (autoDutyResult.Length == 0 || Environment.TickCount64 - autoDutyResultAt > AutoDutyResultShowMs) return;

        if (autoDutyResultIsError)
            ImGui.TextColored(AutoDutyWarnColor, autoDutyResult);
        else
            ImGui.TextUnformatted(autoDutyResult);
    }

    /// <summary>表格裡 AutoDuty 那一格：按鈕、按不下去的按鈕（附原因），或灰字「—」。只讀快照，不打 IPC。</summary>
    private void DrawAutoDutyCell(BingoSuggestion row, string id)
    {
        if (!autoDutyRows.TryGetValue(row.Slot, out var state))
        {
            ImGui.TextDisabled("?");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("還沒向 AutoDuty 問過這一格（一秒內會更新）。");
            return;
        }

        if (state.Verdict != RunButtonVerdict.Show)
        {
            ImGui.TextDisabled("—");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(state.Reason);
            return;
        }

        var fromBand = state.BandNote.Length > 0;
        var blocked = state.Blocker.Length > 0 || pendingAutoDutyRun != null;
        using (ImRaii.Disabled(blocked))
        {
            if (ImGui.SmallButton($"跑一輪{id}-ad-{row.Slot}"))
                pendingAutoDutyRun = new PendingAutoDutyRun(row.Slot, state.CfcId, state.Name, fromBand);
        }

        var hovered = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled);

        // 區間格跑的不一定是「會開啟」那欄的副本，所以實際會跑的名字直接畫在列上。
        if (fromBand)
        {
            using (ImRaii.TextWrapPos(0f))
                ImGui.TextDisabled(state.Name);
            hovered |= ImGui.IsItemHovered();
        }

        if (!hovered) return;

        if (!fromBand)
        {
            ImGui.SetTooltip(state.Blocker.Length > 0
                ? $"現在不能開跑：{state.Blocker}。"
                : $"請 AutoDuty 用劇情輔助器跑「{state.Name}」一輪。\n{AutoDutyRunTooltip}");
            return;
        }

        ImGui.SetTooltip(state.Blocker.Length > 0
            ? $"現在不能開跑「{state.Name}」：{state.Blocker}。\n{state.BandNote}"
            : $"請 AutoDuty 用劇情輔助器跑「{state.Name}」一輪。\n{state.BandNote}\n{AutoDutyRunTooltip}");
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Memory;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;
using TCToolbox.Core;
using GameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace TCToolbox.Modules;

/// <summary>
/// 國防聯軍分隊：在軍營裡帶到分隊軍曹／會議板，打開分隊任務清單或訓練清單後就停手；另外只讀記下派遣確認框上的預定歸隊時間。
/// </summary>
/// <remarks>
/// 🔴 只按兩樣東西：說話者相符的對話框、選單裡整句相等的那一項（中止項絕不會相等）。
/// 確認框、結算視窗、別的選項一律停手交還使用者；派遣確認框只讀不按。
/// 🔴 每一步重新取窗與物件（只存 BaseId 與領地編號），不跨幀保存任何原生指標；只在框架執行緒上動手。
/// 載入、登入、進區都不會自己開始：只有按鈕。
/// </remarks>
public sealed unsafe class SquadronRoutine : TcModule
{
    public override string InternalName => "SquadronRoutine";

    public override string DisplayName => "國防聯軍分隊：帶我到軍曹／會議板";

    public override string Description =>
        "在軍營裡按一下，自己走到分隊軍曹（或會議板）旁邊互動，打開分隊任務清單（或訓練清單）後就停下來，" +
        "任務、隊員與訓練都由你自己選。遇到確認框、結算視窗或任何沒預期的選項都會停手。" +
        "另外會只讀記下派遣確認框上的預定歸隊時間（不會替你按是或否），在這裡列出各角色何時歸隊。";

    public override ModuleCategory Category => ModuleCategory.Company;

    public override bool IsManualTrigger => true;

    public override bool HasConfigUI => true;

    private enum Goal
    {
        None = 0,
        Expedition = 1,
        Training = 2,
    }

    /// <summary>三座軍營：領地、分隊軍曹（ENpcResident）、會議板（EObjName）。台服 7.30 EXD 與 Level 表實查。</summary>
    private static readonly (uint Territory, uint Officer, uint Board)[] Barracks =
    [
        (534, 1016924, 2007150),
        (535, 1016987, 2007152),
        (536, 1016986, 2007151),
    ];

    private const string OfficerTextSheet = "custom/003/ComArmGcArmyOfficer_00342";
    private const uint OfficerMenuRow = 8;
    private const string TrainingTextSheet = "custom/003/ComArmGcArmyTraining_00344";
    private const uint TrainingMenuRow = 6;

    /// <summary>派遣確認框（「確定要開始…嗎？…預定歸隊時間：…」）。</summary>
    private const uint DeployConfirmAddonRow = 10522;

    /// <summary>派遣成功後的系統訊息「開始了…。」。</summary>
    private const uint MissionStartedLogRow = 3927;

    private const string ExpeditionAddon = "GcArmyExpedition";
    private const string TrainingAddon = "GcArmyTraining";
    private const string SelectStringAddon = "SelectString";

    /// <summary>出現就停手的其他視窗。</summary>
    private static readonly string[] UnexpectedAddons =
        ["GcArmyExpeditionResult", "GcArmyMemberList", "GcArmyCapture", "SelectOk"];

    /// <summary>上一個動作之後多久沒出現任何窗就停手（毫秒）。</summary>
    private const long NoWindowTimeoutMs = 8_000;

    /// <summary>問不到遊戲的互動距離時，退回用這個直線距離判斷（米）。</summary>
    private const float FallbackInteractDistance = 3f;

    private const float NavTolerance = 2.5f;
    private static readonly TimeSpan WalkTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan WalkNoProgress = TimeSpan.FromSeconds(6);
    private const float WalkProgressEpsilon = 0.4f;

    /// <summary>對話框最多推進幾頁；超過就當成不是我們預期的流程。</summary>
    private const int MaxTalkPages = 12;

    private static readonly TimeSpan ConfirmWindow = TimeSpan.FromSeconds(60);

    private sealed class Run
    {
        public Goal Goal;
        public uint Territory;
        public uint BaseId;
        public ObjectKind Kind;
        public string TargetName = string.Empty;
        public string MenuText = string.Empty;
        public string GoalAddon = string.Empty;
        public string GoalLabel = string.Empty;
        public bool AllowBlankSpeaker;

        public bool NavIssued;
        public DateTime WalkDeadline;
        public DateTime LastProgressAt;
        public float Closest = float.MaxValue;

        public DateTime LastActionAt;
        public SquadronMenuState Menu;
        public int TalkPages;
    }

    private readonly TaskQueue queue = new() { Owner = "SquadronRoutine" };

    private readonly YesAlreadyBotherLease talkLease = new("Talk");
    private readonly YesAlreadyBotherLease menuLease = new("SelectString");
    private readonly YesAlreadyBotherLease confirmLease = new("SelectYesno");
    private YesAlreadyIpc.BotherPause? loggedTalk, loggedMenu, loggedConfirm;

    private Run? run;

    // 遊戲資料錨點（啟用時讀）。讀不到的那一項按鈕停用。
    private string officerMenuText = string.Empty;
    private string trainingMenuText = string.Empty;
    private List<string> deployFragments = [];
    private string startedPrefix = string.Empty;
    private string startedSuffix = string.Empty;
    private string barracksNames = string.Empty;

    // 畫面請求：繪製路徑只設旗標，框架執行緒上才動手。
    private Goal startRequest;
    private bool stopRequest;
    private ulong? clearRequest;

    /// <summary>按鈕停用的理由（<c>null</c>＝可以按）；只在設定畫面開著時由框架執行緒更新。</summary>
    private string? blockedReason = "尚未檢查";
    private DateTime lastDrawUtc = DateTime.MinValue;
    private DateTime nextAvailabilityCheckUtc = DateTime.MinValue;

    private string lastSummary = string.Empty;

    /// <summary>確認框在 PostSetup 讀不到文字時，框架執行緒上補讀到這個時刻為止。</summary>
    private DateTime? yesnoRetryUntil;

    private (ulong Cid, string Mission, DateTime SeenUtc)? pendingStart;

    #region 只讀診斷的狀態（只在框架執行緒讀寫）

    private const string MemberListAddon = "GcArmyMemberList";
    private const long DiagPollMs = 200;
    private const int DiagMaxFailures = 5;
    private static readonly TimeSpan TrainingRefMaxAge = TimeSpan.FromMinutes(30);

    private SquadronTables diagTables = new();
    private readonly Dictionary<uint, string> diagClassNames = [];
    private readonly SquadronDiagGate expeditionGate = new(10);
    private readonly SquadronDiagGate memberGate = new(8);
    private readonly SquadronDiagGate trainingGate = new(3);
    private readonly SquadronDiagBudget diagBudget = new();
    private readonly Stopwatch diagClock = Stopwatch.StartNew();
    private readonly SquadronCell[] diagCells = new SquadronCell[SquadronDiag.MaxCells];
    private long nextDiagPollMs;
    private int? expeditionFirstFrameMission;
    private HashSet<int>? expeditionVariants;
    private string expeditionLastRuns = string.Empty;
    private readonly Dictionary<ulong, ((int P, int M, int T) Value, DateTime Utc)> naTraining = [];
    private readonly Dictionary<ulong, ((int P, int M, int T) Value, DateTime Utc)> windowTraining = [];
    private int diagFailures;
    private bool diagDisabled;

    #endregion

    private SquadronRoutineConfig Config => Plugin.Instance.Config.SquadronRoutine;

    protected override void OnEnable()
    {
        LoadAnchors();
        LoadDiagTables();
        NavStop.Acquire();
        queue.OnTimeout = step => EndRun($"步驟「{step}」逾時，已停手", false, true);
        Svc.Framework.Update += OnUpdate;
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, UiHelper.SelectYesnoAddonName, OnYesnoSetup);
        Svc.Chat.ChatMessage += OnChatMessage;
    }

    protected override void OnDisable()
    {
        Svc.Chat.ChatMessage -= OnChatMessage;
        Svc.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, UiHelper.SelectYesnoAddonName, OnYesnoSetup);
        Svc.Framework.Update -= OnUpdate;

        if (queue.IsBusy || run != null) EndRun("模組已停用", false, false);
        TextAdvanceLock.Release(InternalName);
        NavStop.Release();
        startRequest = Goal.None;
        stopRequest = false;
        yesnoRetryUntil = null;
        pendingStart = null;
        CloseDiagGates();
    }

    public override string? StopForFleetEmergency()
    {
        if (!queue.IsBusy) return null;

        var step = queue.CurrentStep;
        EndRun("全艦隊急停", false, false);
        return $"國防聯軍分隊（中止於「{step}」）";
    }

    #region 遊戲資料

    private void LoadAnchors()
    {
        officerMenuText = CustomText(OfficerTextSheet, OfficerMenuRow);
        trainingMenuText = CustomText(TrainingTextSheet, TrainingMenuRow);
        deployFragments = AddonPrompt.GetFragments(DeployConfirmAddonRow);

        var started = LogFragments(MissionStartedLogRow);
        (startedPrefix, startedSuffix) = started.Count == 2 ? (started[0], started[1]) : (string.Empty, string.Empty);

        var names = new List<string>();
        foreach (var (territory, _, _) in Barracks)
        {
            var name = Svc.Data.GetExcelSheet<TerritoryType>().GetRowOrDefault(territory)?.PlaceName.ValueNullable?.Name.ExtractText();
            if (!string.IsNullOrWhiteSpace(name)) names.Add(name);
        }

        barracksNames = string.Join("／", names);

        Svc.Log.Information(
            $"[{InternalName}] 遊戲資料：軍曹選項「{officerMenuText}」、會議板選項「{trainingMenuText}」、" +
            $"派遣確認框片段 {deployFragments.Count} 段、開始訊息片段 {started.Count} 段。");
    }

    private string CustomText(string sheetName, uint row)
    {
        try
        {
            var sheet = Svc.Data.GetExcelSheet<RawRow>(null, sheetName);
            return sheet.GetRowOrDefault(row)?.ReadStringColumn(1).ExtractText().Trim() ?? string.Empty;
        }
        catch (Exception ex)
        {
            Svc.Log.Information($"[{InternalName}] 讀不到文字表 {sheetName}：{ex.Message}");
            return string.Empty;
        }
    }

    /// <summary>LogMessage 某列去空白後的固定片段（同 <see cref="AddonPrompt.GetFragments"/> 的做法）。</summary>
    private static List<string> LogFragments(uint rowId)
    {
        var result = new List<string>();
        var row = Svc.Data.GetExcelSheet<LogMessage>().GetRowOrDefault(rowId);
        if (row == null) return result;

        foreach (var payload in row.Value.Text)
        {
            if (payload.Type != ReadOnlySePayloadType.Text) continue;

            var text = SquadronFlow.StripWhitespace(Encoding.UTF8.GetString(payload.Body.Span));
            if (text.Length > 0) result.Add(text);
        }

        return result;
    }

    private static string NpcName(uint id) =>
        Svc.Data.GetExcelSheet<ENpcResident>().GetRowOrDefault(id)?.Singular.ExtractText().Trim() ?? string.Empty;

    private static string EObjDisplayName(uint id) =>
        Svc.Data.GetExcelSheet<EObjName>().GetRowOrDefault(id)?.Singular.ExtractText().Trim() ?? string.Empty;

    private static bool TryGetBarracks(out (uint Territory, uint Officer, uint Board) barracks)
    {
        var territory = Svc.ClientState.TerritoryType;
        foreach (var entry in Barracks)
        {
            if (entry.Territory != territory) continue;
            barracks = entry;
            return true;
        }

        barracks = default;
        return false;
    }

    #endregion

    #region 每幀

    private void OnUpdate(IFramework framework)
    {
        if (stopRequest)
        {
            stopRequest = false;
            if (queue.IsBusy) EndRun("已按停止", false, true);
        }

        if (startRequest != Goal.None)
        {
            var goal = startRequest;
            startRequest = Goal.None;
            TryStart(goal);
        }

        if (clearRequest is { } cid)
        {
            clearRequest = null;
            if (Config.Returns.Remove(cid)) Plugin.Instance.Config.Save();
        }

        if (queue.IsBusy)
        {
            RenewLeases();
            TextAdvanceLock.Acquire(InternalName);
        }

        queue.Tick();

        if (!queue.IsBusy)
        {
            if (run != null) EndRun("流程已結束", false, false);
            TextAdvanceLock.Release(InternalName);
        }

        RetryYesnoRead();
        RefreshAvailability();
        RunDiagnostics();
    }

    /// <summary>設定畫面開著才更新按鈕狀態（要打好幾支 IPC，不在畫面關著時白問）。</summary>
    private void RefreshAvailability()
    {
        var now = DateTime.UtcNow;
        if (now - lastDrawUtc > TimeSpan.FromSeconds(2) || now < nextAvailabilityCheckUtc) return;

        nextAvailabilityCheckUtc = now.AddMilliseconds(500);
        blockedReason = queue.IsBusy ? "正在進行中" : GetStartBlockedReason();
    }

    #endregion

    #region 開始與結束

    /// <returns><c>null</c>＝可以開始；否則是一句給使用者看的理由。只在框架執行緒呼叫。</returns>
    private string? GetStartBlockedReason()
    {
        if (AutomationGate.GetManualBasicBlockedReason() is { } basic) return basic;

        if (!TryGetBarracks(out _))
            return $"要先進入軍營（{(barracksNames.Length > 0 ? barracksNames : "國防聯軍的軍營")}）才能用；傳送到軍營不在這個功能裡。";

        if (Svc.Condition[ConditionFlag.InCombat]) return "戰鬥中。";
        if (Svc.Condition[ConditionFlag.Casting]) return "正在詠唱。";
        if (Svc.Condition[ConditionFlag.Mounted]) return "騎乘中。";
        if (Svc.Condition[ConditionFlag.OccupiedInEvent] || Svc.Condition[ConditionFlag.OccupiedInQuestEvent] ||
            Svc.Condition[ConditionFlag.Occupied] || Svc.Condition[ConditionFlag.Occupied30] ||
            Svc.Condition[ConditionFlag.Occupied33] || Svc.Condition[ConditionFlag.Occupied38] ||
            Svc.Condition[ConditionFlag.Occupied39] || Svc.Condition[ConditionFlag.OccupiedSummoningBell])
            return "正在和別的東西互動，先把它結束。";

        foreach (var name in OpenWindowNames())
        {
            if (UiHelper.IsAnyAddonReady(name)) return $"先關掉開著的 {name} 視窗。";
        }

        if (NavStop.IsEnforcing) return "正在停止移動。";
        if (ExternalNav.TryGetActiveMover(out var mover)) return $"{mover} 正在移動角色。";

        if (AutoDutyIpc.TryIsStopped(out var stopped))
        {
            if (!stopped) return "AutoDuty 正在執行。";
        }
        else if (PluginPresence.IsLoaded("AutoDuty") != false)
        {
            return "問不到 AutoDuty 是不是停著。";
        }

        if (AutoRetainerIpc.TryGetIsBusy(out var arBusy) && arBusy) return "AutoRetainer 正在忙。";

        return null;
    }

    private static IEnumerable<string> OpenWindowNames()
    {
        yield return UiHelper.TalkAddonName;
        yield return SelectStringAddon;
        yield return UiHelper.SelectYesnoAddonName;
        yield return ExpeditionAddon;
        yield return TrainingAddon;
        foreach (var name in UnexpectedAddons) yield return name;
    }

    private void TryStart(Goal goal)
    {
        if (queue.IsBusy) return;

        if (GetStartBlockedReason() is { } reason)
        {
            lastSummary = $"沒有開始：{reason}";
            Svc.Chat.PrintError($"[TC Toolbox] 國防聯軍分隊：{reason}");
            return;
        }

        TryGetBarracks(out var barracks);
        var r = goal == Goal.Expedition
            ? new Run
            {
                Goal = goal, Territory = barracks.Territory, BaseId = barracks.Officer, Kind = ObjectKind.EventNpc,
                TargetName = NpcName(barracks.Officer), MenuText = officerMenuText,
                GoalAddon = ExpeditionAddon, GoalLabel = "分隊任務清單",
            }
            : new Run
            {
                Goal = goal, Territory = barracks.Territory, BaseId = barracks.Board, Kind = ObjectKind.EventObj,
                TargetName = EObjDisplayName(barracks.Board), MenuText = trainingMenuText,
                GoalAddon = TrainingAddon, GoalLabel = "訓練清單", AllowBlankSpeaker = true,
            };

        if (r.TargetName.Length == 0 || r.MenuText.Length == 0)
        {
            lastSummary = "沒有開始：讀不到遊戲資料裡的名稱或選項文字。";
            Svc.Chat.PrintError($"[TC Toolbox] 國防聯軍分隊：{lastSummary}");
            return;
        }

        run = r;
        talkLease.Reset();
        menuLease.Reset();
        confirmLease.Reset();

        queue.Enqueue($"走到{r.TargetName}旁", () => WalkStep(r), 30_000);
        queue.Enqueue($"和{r.TargetName}互動", () => InteractStep(r), 5_000);
        queue.Enqueue($"等{r.GoalLabel}打開", () => WaitStep(r), 30_000);

        Svc.Log.Information($"[{InternalName}] 開始：前往「{r.TargetName}」（領地 {r.Territory}），目標選項「{r.MenuText}」→ {r.GoalAddon}。");
    }

    /// <summary>收掉這一輪：放開 TextAdvance、收掉我們發起的移動、寫一行記錄。YesAlready 的暫停到期會自己恢復。</summary>
    private void EndRun(string summary, bool success, bool chat)
    {
        var r = run;
        var step = queue.CurrentStep;
        run = null;
        queue.Abort();
        TextAdvanceLock.Release(InternalName);
        talkLease.Reset();
        menuLease.Reset();
        confirmLease.Reset();

        if (r is { NavIssued: true } && (ExternalNav.IsVnavmeshPathRunning() || ExternalNav.IsVnavmeshPathfindInProgress()))
            NavStop.RequestStop();

        lastSummary = summary;
        Svc.Log.Information(
            $"[{InternalName}] {(success ? "完成" : "停手")}（步驟「{step ?? "無"}」）：{summary}" +
            (r == null ? string.Empty : $"；選單 {r.Menu}、對話 {r.TalkPages} 頁、{(r.NavIssued ? "有" : "沒有")}走路"));
        if (!chat) return;

        if (success)
            Svc.Chat.Print($"[TC Toolbox] {summary}");
        else
            Svc.Chat.PrintError($"[TC Toolbox] 國防聯軍分隊停手：{summary}");
    }

    private bool Stop(string reason)
    {
        EndRun(reason, false, true);
        return true;
    }

    #endregion

    #region YesAlready 讓位

    private void RenewLeases()
    {
        RenewLogged(talkLease, "Talk", ref loggedTalk);
        RenewLogged(menuLease, "SelectString", ref loggedMenu);
        RenewLogged(confirmLease, "SelectYesno", ref loggedConfirm);
    }

    private void RenewLogged(YesAlreadyBotherLease lease, string feature, ref YesAlreadyIpc.BotherPause? logged)
    {
        lease.Renew();
        if (logged == lease.Last) return;

        logged = lease.Last;
        Svc.Log.Information($"[{InternalName}] 請 YesAlready 暫停「{feature}」自動處理 → {lease.Last}。");
    }

    /// <summary>三種窗 YesAlready 都確定不會同時按（含 Alt 強制鍵）。</summary>
    private bool LeasesExclusive => talkLease.IsExclusive && menuLease.IsExclusive && confirmLease.IsExclusive;

    private string LeaseDetail =>
        $"確認不了 YesAlready 已經讓開（Talk={talkLease.Last}、SelectString={menuLease.Last}、SelectYesno={confirmLease.Last}），為了不兩邊各按一次就不動手";

    #endregion

    #region 步驟

    private bool? WalkStep(Run r)
    {
        if (!TryGetTarget(r, out var player, out var target, out var distance, out var problem)) return Stop(problem);

        var inRange = IsWithinGameInteractRange(player, target);
        if (inRange == true || (inRange == null && distance <= FallbackInteractDistance))
        {
            if (r.NavIssued && ExternalNav.IsVnavmeshPathRunning()) ExternalNav.TryStopMovement();
            return true;
        }

        var now = DateTime.UtcNow;
        if (!r.NavIssued)
        {
            if (!ExternalNav.IsVnavmeshReady())
                return Stop($"離{r.TargetName} {distance:F1} 米，而 vnavmesh 沒有就緒；請自己走到旁邊再按");
            if (!ExternalNav.TryFindNearestMeshPoint(target.Position, 5f, 5f, out _))
                return Stop($"{r.TargetName}附近沒有導航網格；請自己走到旁邊再按");
            if (!ExternalNav.TryMoveCloseTo(target.Position, false, NavTolerance, out _, DisplayName))
                return Stop("vnavmesh 沒有接下這次導航");

            r.NavIssued = true;
            r.WalkDeadline = now + WalkTimeout;
            r.LastProgressAt = now;
            r.Closest = distance;
            Svc.Log.Information($"[{InternalName}] 走向{r.TargetName}（{distance:F1} 米）。");
            return false;
        }

        if (ExternalNav.IsVnavmeshYieldingToInput()) return Stop("你自己移動了角色");

        if (ExternalNav.IsVnavmeshPathfindInProgress()) r.LastProgressAt = now;
        if (distance < r.Closest - WalkProgressEpsilon)
        {
            r.Closest = distance;
            r.LastProgressAt = now;
        }

        if (now >= r.WalkDeadline) return Stop($"走了 {WalkTimeout.TotalSeconds:F0} 秒還沒到{r.TargetName}旁（差 {distance:F1} 米）");
        if (now - r.LastProgressAt >= WalkNoProgress) return Stop($"卡住了，{WalkNoProgress.TotalSeconds:F0} 秒沒有再靠近{r.TargetName}（差 {distance:F1} 米）");
        if (!ExternalNav.IsVnavmeshPathRunning() && !ExternalNav.IsVnavmeshPathfindInProgress())
            return Stop($"vnavmesh 停下來了，但離{r.TargetName}還有 {distance:F1} 米");

        return false;
    }

    private bool? InteractStep(Run r)
    {
        if (!TryGetTarget(r, out var player, out var target, out var distance, out var problem)) return Stop(problem);

        var inRange = IsWithinGameInteractRange(player, target);
        if (inRange == false || (inRange == null && distance > FallbackInteractDistance))
            return Stop($"離{r.TargetName}太遠（{distance:F1} 米）");

        // 互動叫出的對話、選單、確認框 YesAlready 在出現當下就會按：先確定它讓開了才互動。
        if (!LeasesExclusive) return Stop(LeaseDetail);

        var targetSystem = TargetSystem.Instance();
        if (targetSystem == null) return Stop("讀不到目標系統");

        Svc.Targets.Target = target;
        targetSystem->InteractWithObject((GameObject*)target.Address, false);
        r.LastActionAt = DateTime.UtcNow;
        Svc.Log.Information($"[{InternalName}] 已和{r.TargetName}互動（{distance:F1} 米）。");
        return true;
    }

    private bool? WaitStep(Run r)
    {
        if (Svc.ClientState.TerritoryType != r.Territory) return Stop("已經離開軍營");

        // 每一扇窗都在這一幀重新取，判完就用、不留到下一幀。
        var menuAddon = UiHelper.GetAddon(SelectStringAddon);
        var menuReady = UiHelper.IsReady(menuAddon);
        if (!menuReady && r.Menu == SquadronMenuState.PressedWaitingClose) r.Menu = SquadronMenuState.PressedClosed;

        var yesno = UiHelper.FindReadyAddon(UiHelper.SelectYesnoAddonName);
        var talkAddon = UiHelper.GetAddon(UiHelper.TalkAddonName);
        var frame = new SquadronFrame(
            GoalReady: UiHelper.IsAddonReady(r.GoalAddon),
            YesnoText: yesno != null ? UiHelper.GetSelectYesnoText(yesno) : null,
            OtherWindow: UnexpectedAddons.FirstOrDefault(UiHelper.IsAnyAddonReady) ?? string.Empty,
            MenuEntries: menuReady ? UiHelper.GetSelectStringEntries(menuAddon) : null,
            TalkSpeaker: ReadTalkSpeaker(talkAddon),
            Menu: r.Menu,
            MsSinceLastAction: (long)(DateTime.UtcNow - r.LastActionAt).TotalMilliseconds);

        var decision = SquadronFlow.Decide(frame, r.MenuText, r.TargetName, r.AllowBlankSpeaker, NoWindowTimeoutMs);
        switch (decision.Kind)
        {
            case SquadronActionKind.Done:
                EndRun($"已打開{r.GoalLabel}，請自己選{(r.Goal == Goal.Expedition ? "任務與隊員" : "訓練")}。", true, true);
                return true;

            case SquadronActionKind.Stop:
                return Stop(decision.Reason);

            case SquadronActionKind.PressMenu:
                if (!LeasesExclusive) return Stop(LeaseDetail);

                Svc.Log.Information(
                    $"[{InternalName}] {r.TargetName}選單：標題「{UiHelper.ReadSelectStringPrompt(menuAddon).ReplaceLineEndings(" ")}」、" +
                    $"選項「{string.Join("、", frame.MenuEntries!)}」，按第 {decision.MenuIndex} 項「{r.MenuText}」。");
                if (UiHelper.TryFireCallback(menuAddon, true, decision.MenuIndex))
                {
                    r.Menu = SquadronMenuState.PressedWaitingClose;
                    r.LastActionAt = DateTime.UtcNow;
                }

                return false;

            case SquadronActionKind.AdvanceTalk:
                if (!LeasesExclusive) return Stop(LeaseDetail);
                if (!TextAdvanceLock.MayClickTalk || !UiHelper.ClickTalkIfOpen()) return false;

                r.LastActionAt = DateTime.UtcNow;
                if (++r.TalkPages > MaxTalkPages) return Stop($"對話超過 {MaxTalkPages} 頁還沒出現選單");

                Svc.Log.Information($"[{InternalName}] 推進對話第 {r.TalkPages} 頁（說話者「{frame.TalkSpeaker}」）。");
                return false;

            default:
                return false;
        }
    }

    /// <summary>每一步重查：玩家、領地、目標物件（依 BaseId 找最近的一個）。</summary>
    private static bool TryGetTarget(Run r, out IGameObject player, out IGameObject target, out float distance, out string problem)
    {
        player = null!;
        target = null!;
        distance = float.MaxValue;
        problem = string.Empty;

        if (Svc.ClientState.TerritoryType != r.Territory)
        {
            problem = "已經離開軍營";
            return false;
        }

        if (Svc.Objects.LocalPlayer is not { } me)
        {
            problem = "讀不到角色";
            return false;
        }

        IGameObject? best = null;
        foreach (var obj in Svc.Objects)
        {
            if (obj.ObjectKind != r.Kind || obj.BaseId != r.BaseId) continue;

            var d = Vector3.Distance(me.Position, obj.Position);
            if (d >= distance) continue;

            distance = d;
            best = obj;
        }

        if (best == null)
        {
            problem = $"附近找不到{r.TargetName}";
            return false;
        }

        player = me;
        target = best;
        return true;
    }

    /// <returns>遊戲的答案；<see langword="null"/>＝問不到（函式沒解析到，或單例還沒建立）。</returns>
    private static bool? IsWithinGameInteractRange(IGameObject player, IGameObject target)
    {
        if (EventFramework.MemberFunctionPointers.CheckInteractRange is null) return null;

        try
        {
            var framework = EventFramework.Instance();
            if (framework == null) return null;

            return framework->CheckInteractRange((GameObject*)player.Address, (GameObject*)target.Address, 1, false);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Talk 的說話者欄（已剝 payload）；對話框沒開回 <c>null</c>，名字欄隱藏或讀不到回空字串。</summary>
    private static string? ReadTalkSpeaker(AtkUnitBase* addon)
    {
        if (!UiHelper.IsReady(addon)) return null;

        var node = ((AddonTalk*)addon)->AtkTextNode220;
        if (node == null || !node->AtkResNode.IsVisible() || !node->NodeText.StringPtr.HasValue) return string.Empty;

        return MemoryHelper.ReadSeString(&node->NodeText).TextValue.Trim();
    }

    #endregion

    #region 派遣確認框（只讀）

    private void OnYesnoSetup(AddonEvent type, AddonArgs args)
    {
        try
        {
            if (!TryGetBarracks(out _)) return;

            var text = ReadYesnoTextAtSetup((AtkUnitBase*)args.Addon.Address);
            if (text.Length == 0)
            {
                yesnoRetryUntil = DateTime.UtcNow.AddSeconds(2);
                return;
            }

            HandleYesnoText(text);
        }
        catch (Exception ex)
        {
            if (Throttle.Pass($"{InternalName}-YesnoSetup", 60_000))
                Svc.Log.Information($"[{InternalName}] 讀確認框時發生例外（已忽略）：{ex.Message}");
        }
    }

    /// <summary>PostSetup 當下的讀法：視窗還不一定可見，所以不判 IsReady，只判兩道空指標閘門。</summary>
    private static string ReadYesnoTextAtSetup(AtkUnitBase* addon)
    {
        if (addon == null) return string.Empty;

        var node = ((AddonSelectYesno*)addon)->PromptText;
        if (node == null || !node->NodeText.StringPtr.HasValue) return string.Empty;

        return AddonPrompt.ReadSelectYesnoText(addon);
    }

    private void RetryYesnoRead()
    {
        if (yesnoRetryUntil is not { } until) return;

        if (DateTime.UtcNow > until || !TryGetBarracks(out _))
        {
            yesnoRetryUntil = null;
            return;
        }

        var addon = UiHelper.FindReadyAddon(UiHelper.SelectYesnoAddonName);
        if (addon == null) return;

        var text = UiHelper.GetSelectYesnoText(addon);
        if (text.Length == 0) return;

        yesnoRetryUntil = null;
        HandleYesnoText(text);
    }

    private void HandleYesnoText(string text)
    {
        if (!AddonPrompt.Matches(text, deployFragments)) return;

        if (!SquadronFlow.TryParseReturn(text, deployFragments, DateTime.Now, out var parsed))
        {
            if (Throttle.Pass($"{InternalName}-ParseFail", 60_000))
                Svc.Log.Information($"[{InternalName}] 看到派遣確認框，但解析不出歸隊時間：「{text.ReplaceLineEndings(" ")}」");
            return;
        }

        var cid = Svc.PlayerState.ContentId;
        if (cid == 0) return;

        var character = $"{Svc.PlayerState.CharacterName}＠{Svc.PlayerState.HomeWorld.ValueNullable?.Name.ExtractText()}";
        var now = DateTime.UtcNow;
        Config.Returns[cid] = new SquadronReturnRecord
        {
            Character = character,
            Mission = parsed.Mission,
            ReturnUtc = parsed.ReturnLocal.ToUniversalTime(),
            SeenUtc = now,
            Confirmed = false,
        };
        Plugin.Instance.Config.Save();
        pendingStart = (cid, parsed.Mission, now);

        Svc.Log.Information(
            $"[{InternalName}] 記下 {character}：{parsed.Mission}，預定歸隊 {parsed.ReturnLocal:yyyy/MM/dd HH:mm}（{parsed.Hours} 小時後，尚未看到開始訊息）。");
    }

    private void OnChatMessage(XivChatType type, int timestamp, ref SeString sender, ref SeString message, ref bool isHandled)
    {
        try
        {
            if (pendingStart is not { } pending || startedPrefix.Length == 0) return;

            if (DateTime.UtcNow - pending.SeenUtc > ConfirmWindow)
            {
                pendingStart = null;
                return;
            }

            var text = SquadronFlow.StripWhitespace(message.TextValue);
            if (text != startedPrefix + SquadronFlow.StripWhitespace(pending.Mission) + startedSuffix) return;

            pendingStart = null;
            if (!Config.Returns.TryGetValue(pending.Cid, out var record) || record.Mission != pending.Mission) return;

            record.Confirmed = true;
            Plugin.Instance.Config.Save();
            Svc.Log.Information($"[{InternalName}] 看到開始訊息，{record.Character} 的「{record.Mission}」已確認派出。");
        }
        catch (Exception ex)
        {
            if (Throttle.Pass($"{InternalName}-Chat", 60_000))
                Svc.Log.Information($"[{InternalName}] 處理聊天訊息時發生例外（已忽略）：{ex.Message}");
        }
    }

    #endregion

    #region 只讀診斷（分隊任務清單／編組名單／訓練窗）

    // 只讀：不送 callback、不掛 hook、不碰 AddonPressGuard。每次讀都在同一幀重新取窗並確認 ready，
    // 不跨幀保存任何原生指標；格數、型別、NumberArray Size 都先檢查，不符就只記一行。
    // 用途：把設計裡靜態推導、執行期沒驗過的版面與公式，在使用者照常操作時寫成「符合／不符」。

    private void LoadDiagTables()
    {
        try
        {
            diagTables = SquadronTableLoader.Build(
                Svc.Data.GetExcelSheet<GcArmyExpedition>(), Svc.Data.GetExcelSheet<GcArmyMemberGrow>());
            diagClassNames.Clear();
            foreach (var cj in diagTables.Grow.Keys)
            {
                var name = Svc.Data.GetExcelSheet<ClassJob>().GetRowOrDefault(cj)?.Name.ExtractText();
                diagClassNames[cj] = string.IsNullOrEmpty(name) ? $"職業{cj}" : name;
            }
        }
        catch (Exception ex)
        {
            diagTables = new SquadronTables();
            Svc.Log.Information($"[{InternalName}] 分隊診斷：讀不到分隊相關的表（{ex.Message}），只記原始值、不比對公式。");
        }

        var csIndex = (int)NumberArrayType.GcArmyExpedition;
        Svc.Log.Information(
            $"[{InternalName}] 分隊診斷：表 任務 {diagTables.Missions.Count} 筆、成長表 {diagTables.Grow.Count} 職業；" +
            $"NumberArray 用 {SquadronDiag.ExpeditionNumberArray}（CS 列舉值 {csIndex}{(csIndex == SquadronDiag.ExpeditionNumberArray ? "，一致" : "，不一致，以遊戲碼的 113 為準")}）。");
    }

    private string DiagClassName(uint classJob) =>
        diagClassNames.TryGetValue(classJob, out var name) ? name : $"職業{classJob}";

    private void CloseDiagGates()
    {
        expeditionGate.MarkClosed();
        memberGate.MarkClosed();
        trainingGate.MarkClosed();
    }

    /// <summary>同插件所有 Update 處理器共用一個 try/catch，所以這裡自己全包、不往外擲。</summary>
    private void RunDiagnostics()
    {
        if (diagDisabled) return;

        try
        {
            DiagnosticsTick();
        }
        catch (Exception ex)
        {
            if (++diagFailures >= DiagMaxFailures)
            {
                diagDisabled = true;
                Svc.Log.Information($"[{InternalName}] 分隊診斷發生例外 {diagFailures} 次，本次載入不再記錄：{ex}");
            }
            else if (Throttle.Pass($"{InternalName}-Diag", 60_000))
            {
                Svc.Log.Information($"[{InternalName}] 分隊診斷發生例外（已忽略，第 {diagFailures} 次）：{ex.Message}");
            }
        }
    }

    private void DiagnosticsTick()
    {
        if (!TryGetBarracks(out _))
        {
            CloseDiagGates();
            return;
        }

        var now = diagClock.ElapsedMilliseconds;
        var expedition = UiHelper.FindReadyAddon(ExpeditionAddon);
        var members = UiHelper.FindReadyAddon(MemberListAddon);
        var training = UiHelper.FindReadyAddon(TrainingAddon);

        if (expedition == null)
        {
            expeditionGate.MarkClosed();
        }
        else if (expeditionGate.MarkReady(now))
        {
            // 開窗這一幀的 [1]：和穩定後的值不同＝NumberArray 開窗瞬間還留著舊值。
            Span<int> first = stackalloc int[SquadronDiag.NumberArrayReadLength];
            expeditionFirstFrameMission = ReadExpeditionNa(first, out _, out _) > 1 ? first[1] : null;
            expeditionVariants = null;
            expeditionLastRuns = string.Empty;
        }

        if (members == null) memberGate.MarkClosed();
        else memberGate.MarkReady(now);

        if (training == null) trainingGate.MarkClosed();
        else trainingGate.MarkReady(now);

        if (now < nextDiagPollMs) return;
        nextDiagPollMs = now + DiagPollMs;

        if (expedition != null && expeditionGate.Settled(now)) DiagExpedition(expedition, now);
        if (members != null && memberGate.Settled(now)) DiagMemberList(members, now);
        if (training != null && trainingGate.Settled(now)) DiagTraining(training, now);
    }

    /// <summary>NumberArray 113 抄進 <paramref name="buffer"/>（長度先截到 Size）；回抄了幾格，0＝讀不到。</summary>
    private static int ReadExpeditionNa(Span<int> buffer, out int size, out string problem)
    {
        size = -1;
        problem = string.Empty;

        var atk = RaptureAtkModule.Instance();
        if (atk == null)
        {
            problem = "RaptureAtkModule 取不到";
            return 0;
        }

        ref var holder = ref atk->AtkModule.AtkArrayDataHolder;
        if (!SquadronDiag.IsPlausiblePointer((nint)holder.NumberArrays) || SquadronDiag.ExpeditionNumberArray >= holder.NumberArrayCount)
        {
            problem = $"NumberArrays 不可用（共 {holder.NumberArrayCount} 個）";
            return 0;
        }

        var array = holder.NumberArrays[SquadronDiag.ExpeditionNumberArray];
        if (!SquadronDiag.IsPlausiblePointer((nint)array) || !SquadronDiag.IsPlausiblePointer((nint)array->IntArray))
        {
            problem = $"NumberArray {SquadronDiag.ExpeditionNumberArray} 是空的";
            return 0;
        }

        size = array->AtkArrayData.Size;
        return SquadronDiag.CopyInts(array->IntArray, size, buffer);
    }

    /// <param name="addon">本幀取得、已判過 ready 的窗。</param>
    private bool TryReadCells(AtkUnitBase* addon, out int count, out string problem)
    {
        count = addon->AtkValuesCount;
        problem = string.Empty;
        if (count == 0 || !SquadronDiag.IsPlausiblePointer((nint)addon->AtkValues))
        {
            problem = $"AtkValues 是空的（格數 {count}）";
            return false;
        }

        if (count > diagCells.Length)
        {
            problem = $"AtkValues 格數 {count} 超過讀取上限 {diagCells.Length}";
            return false;
        }

        SquadronDiag.ReadCells((byte*)addon->AtkValues, count, diagCells);
        return true;
    }

    /// <returns>可以記這一筆；預算剛用完時順便記一行說明。</returns>
    private bool OfferDiag(SquadronDiagGate gate, ulong key, long now)
    {
        if (gate.Offer(key, now, diagBudget, out var exhausted))
        {
            if (gate.LastForced)
                Svc.Log.Information($"[{InternalName}] 分隊診斷：開窗 {SquadronDiagGate.ForceFirstMs / 1000} 秒內值一直在變，下面照當下的值記一次。");
            return true;
        }

        if (exhausted)
            Svc.Log.Information($"[{InternalName}] 分隊診斷：{SquadronDiagBudget.WindowMs / 3_600_000} 小時內已記 {SquadronDiagBudget.MaxRecords} 筆，先不再記。");
        return false;
    }

    private static ulong ProblemKey(string problem) => SquadronDiag.Hash([problem.GetHashCode(), problem.Length]);

    private void DiagExpedition(AtkUnitBase* addon, long now)
    {
        Span<int> na = stackalloc int[SquadronDiag.NumberArrayReadLength];
        var naLen = ReadExpeditionNa(na, out var size, out var naProblem);
        var hasCells = TryReadCells(addon, out var count, out var cellProblem);
        var cells = hasCells ? new ReadOnlySpan<SquadronCell>(diagCells, 0, count) : default;

        // [0] 是遊戲每幀寫入的軍票數，不算進「值變了沒」。
        var key = SquadronDiag.Hash(cells, SquadronDiag.Hash(na[Math.Min(1, naLen)..naLen]));
        if (!hasCells) key ^= ProblemKey(cellProblem);
        if (naLen == 0) key ^= ProblemKey(naProblem);
        if (!OfferDiag(expeditionGate, key, now)) return;

        var v = new SquadronVerdict();
        SquadronExpeditionList? list = null;
        if (hasCells)
        {
            list = SquadronDiag.CheckExpeditionList(cells, diagTables, expeditionGate.LoggedThisOpen == 1, v, out var listDetail);
            var runs = SquadronDiag.TypeRuns(cells);
            var runsText = runs == expeditionLastRuns ? string.Empty : $"；型別序列（{SquadronDiag.TypeLegend}）{runs}";
            expeditionLastRuns = runs;
            Svc.Log.Information($"[{InternalName}] 分隊診斷｜任務清單 AtkValues：{listDetail}{runsText}");
        }
        else
        {
            v.Fail(cellProblem);
        }

        SquadronExpeditionNa? read = null;
        if (naLen == 0)
            v.Fail(naProblem);
        else
            read = SquadronDiag.ParseExpeditionNa(na[..naLen], size, v);

        var cid = Svc.PlayerState.ContentId;
        var title = "任務清單";
        if (read != null)
        {
            SquadronDiag.CheckExpeditionNa(read, diagTables, list, ref expeditionVariants, TrainingRef(windowTraining, cid), v);
            Svc.Log.Information(
                $"[{InternalName}] 分隊診斷｜任務清單 NA{SquadronDiag.ExpeditionNumberArray}：{SquadronDiag.DescribeExpeditionNa(read, diagTables, expeditionFirstFrameMission)}");
            if (cid != 0) naTraining[cid] = (read.Training, DateTime.UtcNow);
            title = $"任務清單 #{read.MissionId}";
        }

        Svc.Log.Information($"[{InternalName}] 分隊診斷結論｜{v.Format(title)}");
    }

    private void DiagMemberList(AtkUnitBase* addon, long now)
    {
        var hasCells = TryReadCells(addon, out var count, out var cellProblem);
        var cells = hasCells ? new ReadOnlySpan<SquadronCell>(diagCells, 0, count) : default;
        var key = hasCells ? SquadronDiag.Hash(cells) : ProblemKey(cellProblem);
        if (!OfferDiag(memberGate, key, now)) return;

        var v = new SquadronVerdict();
        if (!hasCells)
        {
            v.Fail(cellProblem);
            Svc.Log.Information($"[{InternalName}] 分隊診斷結論｜{v.Format("編組名單")}");
            return;
        }

        // 同一幀的 NumberArray 113：名單換人時遊戲同步重算隊伍，拿來對選中的人。
        Span<int> na = stackalloc int[SquadronDiag.NumberArrayReadLength];
        var naLen = ReadExpeditionNa(na, out var size, out _);
        var read = naLen > 0 && UiHelper.IsAddonReady(ExpeditionAddon)
            ? SquadronDiag.ParseExpeditionNa(na[..naLen], size, new SquadronVerdict())
            : null;

        SquadronDiag.CheckMemberList(cells, diagTables, read, DiagClassName, v, out var detail);
        Svc.Log.Information(
            $"[{InternalName}] 分隊診斷｜編組名單 AtkValues：{detail}" +
            (memberGate.LoggedThisOpen == 1 ? $"；型別序列（{SquadronDiag.TypeLegend}）{SquadronDiag.TypeRuns(cells)}" : string.Empty));
        Svc.Log.Information($"[{InternalName}] 分隊診斷結論｜{v.Format("編組名單")}");
    }

    private void DiagTraining(AtkUnitBase* addon, long now)
    {
        var hasCells = TryReadCells(addon, out var count, out var cellProblem);
        var cells = hasCells ? new ReadOnlySpan<SquadronCell>(diagCells, 0, count) : default;

        // 只看模式、目前三項與上限：滑鼠移到各訓練上只改預覽格，不另記。
        ulong key;
        if (!hasCells) key = ProblemKey(cellProblem);
        else
        {
            Span<int> picked = stackalloc int[6];
            ReadOnlySpan<int> indexes = [0, 13, 15, 17, 21, count];
            for (var i = 0; i < indexes.Length; i++)
                picked[i] = indexes[i] < count ? (int)cells[indexes[i]].Value ^ (cells[indexes[i]].Type << 24) : indexes[i];
            key = SquadronDiag.Hash(picked);
        }

        if (!OfferDiag(trainingGate, key, now)) return;

        var v = new SquadronVerdict();
        if (!hasCells)
        {
            v.Fail(cellProblem);
            Svc.Log.Information($"[{InternalName}] 分隊診斷結論｜{v.Format("訓練窗")}");
            return;
        }

        var cid = Svc.PlayerState.ContentId;
        var read = SquadronDiag.CheckTraining(cells, TrainingRef(naTraining, cid), v, out var detail);
        if (read.Complete && cid != 0) windowTraining[cid] = (read.Current, DateTime.UtcNow);

        Svc.Log.Information(
            $"[{InternalName}] 分隊診斷｜訓練窗 AtkValues：{detail}" +
            (trainingGate.LoggedThisOpen == 1 ? $"；型別序列（{SquadronDiag.TypeLegend}）{SquadronDiag.TypeRuns(cells)}" : string.Empty));
        Svc.Log.Information($"[{InternalName}] 分隊診斷結論｜{v.Format("訓練窗")}");
    }

    private static (int P, int M, int T)? TrainingRef(Dictionary<ulong, ((int P, int M, int T) Value, DateTime Utc)> source, ulong cid) =>
        cid != 0 && source.TryGetValue(cid, out var e) && DateTime.UtcNow - e.Utc <= TrainingRefMaxAge ? e.Value : null;

    #endregion

    #region 畫面

    public override void DrawConfig()
    {
        lastDrawUtc = DateTime.UtcNow;

        if (queue.IsBusy)
        {
            ImGui.TextUnformatted($"執行中：{queue.CurrentStep}");
            ImGui.SameLine();
            if (ImGui.Button("停止##squadronStop")) stopRequest = true;
        }
        else
        {
            DrawStartButton(Goal.Expedition, "帶我到分隊軍曹並打開分隊任務清單###squadronOfficer",
                "走到分隊軍曹旁邊互動，只推進軍曹的對話，選單裡只按「確認分隊任務」（整句相同才按），\n" +
                "分隊任務清單一打開就停手，任務與隊員由你自己選。");
            DrawStartButton(Goal.Training, "帶我到會議板並打開訓練清單###squadronBoard",
                "走到會議板旁邊互動，選單裡只按「打開訓練選單」（整句相同才按），訓練清單一打開就停手。");
        }

        if (lastSummary.Length > 0) ImGui.TextDisabled($"上次結果：{lastSummary}");

        ImGui.Separator();
        DrawReturns();
    }

    private void DrawStartButton(Goal goal, string label, string tooltip)
    {
        var reason = blockedReason;
        var clicked = false;
        using (ImRaii.Disabled(reason != null))
        {
            clicked = ImGui.Button(label);
        }

        // 說明框畫在停用區段之外才會全亮；停用時改說「為什麼不能按」。
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(reason == null ? tooltip : $"現在不能按：{reason}\n\n{tooltip}");

        if (clicked && reason == null) startRequest = goal;
    }

    private void DrawReturns()
    {
        ImGui.TextUnformatted("各角色預定歸隊時間");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("在軍營按下派遣、出現確認框時只讀記下（不會替你按是或否）。\n灰色帶「？」＝看到確認框但還沒看到「開始了…」訊息，可能按了否。");

        if (Config.Returns.Count == 0)
        {
            ImGui.TextDisabled("還沒有紀錄。");
            return;
        }

        var now = DateTime.UtcNow;
        foreach (var (cid, record) in Config.Returns.OrderBy(x => x.Value.ReturnUtc))
        {
            var remaining = record.ReturnUtc - now;
            var when = remaining <= TimeSpan.Zero
                ? "已可歸隊"
                : $"還有 {(int)remaining.TotalHours} 小時 {remaining.Minutes} 分";
            var line = $"{record.Character}　{record.Mission}　{record.ReturnUtc.ToLocalTime():MM/dd HH:mm}（{when}）";

            if (record.Confirmed)
                ImGui.TextUnformatted(line);
            else
                ImGui.TextDisabled($"？{line}");

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"確認框出現於 {record.SeenUtc.ToLocalTime():MM/dd HH:mm}" + (record.Confirmed ? "，之後看到了開始訊息。" : "，之後沒看到開始訊息（可能按了否）。"));

            ImGui.SameLine();
            if (ImGui.SmallButton($"清除##squadronClear{cid}")) clearRequest = cid;
        }
    }

    #endregion
}

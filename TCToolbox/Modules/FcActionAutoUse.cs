using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Memory;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;
using TCToolbox.Core;
using BattleChara = FFXIVClientStructs.FFXIV.Client.Game.Character.BattleChara;
using GameControl = FFXIVClientStructs.FFXIV.Client.Game.Control.Control;
using ValueType = FFXIVClientStructs.FFXIV.Component.GUI.ValueType;

namespace TCToolbox.Modules;

/// <summary>
/// 登入後替勾選的角色開公會特效（只用庫存，不購買）；附手動「現在開」。
/// </summary>
/// <remarks>
/// 契約：只在框架執行緒上動作；不跨幀保存任何視窗或物件指標，每幀重查。
/// 自動只在「剛登入、AutoRetainer 多角色模式關著且不忙」時跑，任何一項問不到都當成忙。
/// 確認框按之前先請 YesAlready 暫停 SelectYesno，請不動就不按；文字逐字對上才按「是」。
/// </remarks>
public sealed unsafe class FcActionAutoUse : TcModule
{
    public const string ModuleInternalName = "FcActionAutoUse";

    public override string InternalName => ModuleInternalName;

    public override string DisplayName => "公會特效自動使用";

    public override string Description =>
        "每個角色勾選要維持的公會特效。登入後（AutoRetainer 多角色模式關著、也沒在作業時）" +
        "身上沒有那個特效就自動從公會的特效庫存裡使用；也可以按「現在開」手動跑一次。" +
        "只用庫存，不花公會戰績購買。會記下各角色的特效庫存，剩不多時在聊天提醒；" +
        "另可開啟「到期時自動續開」。";

    public override ModuleCategory Category => ModuleCategory.Company;

    public override bool HasConfigUI => true;

    private const string LogTag = "[FcActionAutoUse]";
    private const string CompanyAddon = "FreeCompany";
    private const string ActionAddon = "FreeCompanyAction";
    private const string MenuAddon = "ContextMenu";
    private const string ConfirmAddon = "SelectYesno";
    private const string OpenCompanyCommand = "/freecompanycmd";

    /// <summary>「使用特效」。</summary>
    private const uint UseActionAddonRow = 2817;

    /// <summary>「確定要使用「&lt;CompanyAction 名稱&gt;」特效嗎？」。</summary>
    private const uint ConfirmAddonRow = 2877;

    private const uint UseFailedLogRow = 1918;
    private const uint NoPermissionLogRow = 1919;
    private const uint LimitReachedLogRow = 1920;
    private const uint SameKindLogRow = 1921;

    /// <summary>「沒有使用公會特效的許可權。」。</summary>
    private const uint NoPermissionAddonRow = 3425;

    /// <summary>公會視窗切到「特效」分頁的 callback 值（照 AutoRetainer 上游）。</summary>
    private const uint ActionTabValue = 4;

    private const int LoginSettleMs = 8_000;
    private const int GateWindowMs = 60_000;
    private const int GateRecheckMs = 1_000;
    private const int ArRecheckMs = 500;

    /// <summary>身上狀態與特效分頁的輪詢間隔；不需要每幀。</summary>
    private const int PollMs = 1_000;

    private const int RenewRecheckMs = 2_000;

    /// <summary>特效持續 24 小時；這段時間內按過「是」的，自動那條路不再開（狀態看不到也一樣）。</summary>
    private const int RecentUseHours = 23;

    private static readonly string[] ConflictingDialogs = [ConfirmAddon, MenuAddon, "SelectString"];

    private FcActionAutoUseConfig Config => Plugin.Instance.Config.FcActionAutoUse;

    private readonly YesAlreadyBotherLease confirmLease = new(ConfirmAddon);
    private readonly FcObservation observation = new();
    private readonly Dictionary<uint, bool> statusRowIsFcBuff = [];

    private bool armed;
    private long readySince;
    private long gateStartedAt;
    private long lastGateAt;
    private string lastGateReason = string.Empty;

    private FcActionSequence? sequence;
    private string runLabel = string.Empty;
    private DateTime runStartedUtc;
    private long lastArCheckAt;
    private string lastResult = string.Empty;
    private int recordedStableReads;

    private List<FcRejectionTemplate> rejectionTemplates = [];
    private string lastRejection = string.Empty;
    private string lastRejectionDetail = string.Empty;

    private readonly FcStatusWatch statusWatch = new();
    private long lastPollAt;
    private ulong sessionCid;
    private long sessionReadySince;
    private bool sessionReminderChecked;
    private readonly HashSet<uint> remindedThisLogin = [];

    private long manualTabSince = -1;
    private string? manualTabLastSignature;
    private string? manualTabRecordedSignature;
    private bool manualTabInvalidLogged;

    /// <summary>判定到期、等著續開的狀態編號。換角、登出、登入、停用模組都清掉。</summary>
    private readonly HashSet<uint> pendingRenew = [];
    private long lastRenewCheckAt;
    private string lastRenewReason = string.Empty;

    public override ModuleNotice? RowNotice => ContextMenuLayout.KnownUnavailable
        ? new ModuleNotice(ModuleNoticeLevel.Warning, "這個遊戲版本無法使用，已停用",
            $"{ContextMenuLayout.FailureReason}：選「使用特效」要知道右鍵選單項目從第幾格開始，讀錯一格會點到隔壁的選項。")
        : null;

    protected override void OnEnable()
    {
        // 整個流程都靠在右鍵選單裡選「使用特效」，推不出版面就整個模組不啟用。
        if (!ContextMenuLayout.TryGetFirstEntryIndex(out _))
        {
            ModuleFailureNotice.Queue(InternalName, DisplayName, ContextMenuLayout.FailureReason, "不會點選任何選單");
            throw new InvalidOperationException($"不啟用：{ContextMenuLayout.FailureReason}");
        }

        Svc.ClientState.Login += OnLogin;
        Svc.ClientState.Logout += OnLogout;
        Svc.Framework.Update += OnUpdate;
        Svc.Chat.ChatMessage += OnChatMessage;
        Svc.Toasts.ErrorToast += OnErrorToast;

        // 刻意不在這裡補跑：啟用模組或熱載入時人已經在遊戲裡，那不是「登入」。
    }

    protected override void OnDisable()
    {
        Svc.ClientState.Login -= OnLogin;
        Svc.ClientState.Logout -= OnLogout;
        Svc.Framework.Update -= OnUpdate;
        Svc.Chat.ChatMessage -= OnChatMessage;
        Svc.Toasts.ErrorToast -= OnErrorToast;

        if (sequence != null)
            Log($"模組停用，中止「{runLabel}」（停在「{sequence.StepName}」；已開的視窗留給你）");

        sequence = null;
        DisarmLogin();
        ResetSession("模組停用");
        confirmLease.Reset();
    }

    private void OnLogin()
    {
        DisarmLogin();
        ResetSession("登入");
        armed = true;
        Log("收到登入事件，等角色就緒後檢查要不要開公會特效");
    }

    private void OnLogout(int type, int code)
    {
        if (armed || sequence != null)
            Log(sequence != null ? $"登出，中止「{runLabel}」（停在「{sequence.StepName}」）" : "登出，取消這次登入的檢查");

        sequence = null;
        DisarmLogin();
        ResetSession("登出");
    }

    /// <summary>「這次登入」的狀態全部歸零：狀態追蹤、等待中的續開、這次登入已提醒過的。</summary>
    private void ResetSession(string reason)
    {
        if (pendingRenew.Count > 0)
            Log($"{reason}，取消等待中的到期續開：{StatusNames(pendingRenew)}");

        pendingRenew.Clear();
        lastRenewReason = string.Empty;
        statusWatch.Clear();
        remindedThisLogin.Clear();
        sessionReminderChecked = false;
        sessionReadySince = 0;
        sessionCid = 0;
        ResetManualTab();
    }

    private void DisarmLogin()
    {
        armed = false;
        readySince = 0;
        gateStartedAt = 0;
        lastGateAt = 0;
        lastGateReason = string.Empty;
    }

    private void OnUpdate(IFramework framework)
    {
        var now = Environment.TickCount64;
        if (now - lastPollAt >= PollMs)
        {
            lastPollAt = now;
            try
            {
                Poll(now);
            }
            catch (Exception ex)
            {
                if (Throttle.Pass("FcActionAutoUse-Poll", 60_000))
                    Svc.Log.Warning($"{LogTag} 每秒檢查出錯（{ex.GetType().Name}：{ex.Message}），這一輪略過");
            }
        }

        if (sequence != null)
        {
            TickSequence();
            return;
        }

        if (armed)
        {
            TickLoginGate();
            return;
        }

        if (pendingRenew.Count > 0) TickRenewalSafely(now);
    }

    #region 登入後的判斷

    private void TickLoginGate()
    {
        var now = Environment.TickCount64;
        if (!Svc.ClientState.IsLoggedIn || Svc.Objects.LocalPlayer == null
            || Svc.Condition[ConditionFlag.BetweenAreas] || Svc.Condition[ConditionFlag.BetweenAreas51])
        {
            readySince = 0;
            return;
        }

        if (readySince == 0)
        {
            readySince = now;
            return;
        }

        if (now - readySince < LoginSettleMs) return;
        if (gateStartedAt == 0) gateStartedAt = now;
        if (now - lastGateAt < GateRecheckMs) return;
        lastGateAt = now;

        var verdict = Evaluate(manual: false, out var reason, out var targets);
        switch (verdict)
        {
            case FcGateVerdict.Go:
                DisarmLogin();
                Start(targets, "登入自動開");
                return;

            case FcGateVerdict.Stop:
                DisarmLogin();
                lastResult = $"登入後不開：{reason}";
                Log($"登入檢查：不開——{reason}");
                return;
        }

        if (reason != lastGateReason)
        {
            lastGateReason = reason;
            Log($"登入檢查：先等——{reason}");
        }

        if (now - gateStartedAt > GateWindowMs)
        {
            DisarmLogin();
            lastResult = $"登入後不開：等了 {GateWindowMs / 1000} 秒仍是「{reason}」";
            Log($"登入檢查：等了 {GateWindowMs / 1000} 秒條件仍不齊（最後一次：{reason}），這次登入不開");
        }
    }

    /// <summary>把遊戲與 AutoRetainer 的現況整理成 <see cref="FcGateInput"/> 交給 <see cref="FcActionGate"/>。</summary>
    private FcGateVerdict Evaluate(bool manual, out string reason, out List<FcTarget> targets)
    {
        targets = [];
        var cid = Svc.PlayerState.ContentId;
        var chosen = cid != 0 && Config.ActionsByCharacter.TryGetValue(cid, out var list) && list != null ? list.Distinct().ToList() : [];

        var statuses = ReadStatuses();
        var unknown = new List<string>();
        var present = new List<string>();
        var recent = new List<string>();
        foreach (var actionId in chosen)
        {
            var name = ActionName(actionId);
            if (!FcActionCatalog.TryGetStatus(actionId, out var statusId) || !IsFcBuffStatus(statusId) || name.Length == 0)
            {
                unknown.Add($"{actionId}");
                continue;
            }

            if (statuses != null && statuses.Contains(statusId))
            {
                present.Add(name);
                continue;
            }

            if (!manual && UsedRecently(cid, actionId, out var hoursAgo))
            {
                recent.Add($"{name}（{hoursAgo:F1} 小時前）");
                continue;
            }

            targets.Add(new FcTarget(actionId, name, statusId));
        }

        var player = Svc.Objects.LocalPlayer;
        var input = new FcGateInput(
            Manual: manual,
            CheckedCount: chosen.Count,
            UnknownStatusCount: unknown.Count,
            MissingCount: statuses == null ? -1 : targets.Count,
            BusyReason: AutomationGate.TryGetBusyReason(out var busy) ? busy : null,
            MultiMode: QueryMultiMode(),
            ArBusy: QueryArBusy(),
            InFreeCompany: player == null ? null : player.CompanyTag.TextValue.Length > 0,
            InHomeWorld: Svc.PlayerState.IsLoaded
                ? Svc.PlayerState.CurrentWorld.RowId == Svc.PlayerState.HomeWorld.RowId
                : null,
            OpenDialog: ConflictingDialogs.FirstOrDefault(UiHelper.IsAddonReady));

        var verdict = FcActionGate.Evaluate(input, out reason);
        if (unknown.Count > 0) reason += $"（判不出狀態而略過的特效：{string.Join("、", unknown)}）";
        if (present.Count > 0 && verdict != FcGateVerdict.Wait) reason += $"（身上已有：{string.Join("、", present)}）";
        if (recent.Count > 0)
            reason += $"（{RecentUseHours} 小時內按過「是」、身上卻看不到狀態，自動不重開：{string.Join("、", recent)}；手動可以）";
        return verdict;
    }

    private static FcMultiMode QueryMultiMode() =>
        AutoRetainerIpc.GetMultiModeState(out _) switch
        {
            AutoRetainerIpc.MultiModeState.On => FcMultiMode.On,
            AutoRetainerIpc.MultiModeState.Off => FcMultiMode.Off,
            AutoRetainerIpc.MultiModeState.NotInstalled => FcMultiMode.NotInstalled,
            _ => FcMultiMode.Unknown,
        };

    /// <summary>兩支忙碌端點的聯集（涵蓋範圍不同）；任一支問不到回 <see langword="null"/>。</summary>
    private static bool? QueryArBusy()
    {
        try
        {
            if (!AutoRetainerIpc.TryGetIsBusy(out var taskBusy)) return null;
            if (!AutoRetainerIpc.TryGetSchedulerRunning(out var schedulerBusy)) return null;
            return taskBusy || schedulerBusy;
        }
        catch (Exception ex)
        {
            if (Throttle.Pass("FcActionAutoUse-ArBusy", 60_000))
                Log($"問 AutoRetainer 忙不忙時出錯（{ex.GetType().Name}：{ex.Message}），視為忙");
            return null;
        }
    }

    #endregion

    #region 流程

    private void RunManually()
    {
        if (sequence != null) return;

        var verdict = Evaluate(manual: true, out var reason, out var targets);
        if (verdict != FcGateVerdict.Go)
        {
            lastResult = $"手動：不開——{reason}";
            Log($"手動「現在開」：不開——{reason}");
            return;
        }

        DisarmLogin();
        Start(targets, "手動現在開");
    }

    private void Start(List<FcTarget> targets, string label)
    {
        if (!TryGetConfirmTemplate(out var prefix, out var suffix, out var problem))
        {
            lastResult = $"{label}：不開——{problem}";
            Log($"{label}：不開——{problem}");
            return;
        }

        var useText = AddonText(UseActionAddonRow);
        if (useText.Length == 0)
        {
            lastResult = $"{label}：不開——讀不到 Addon {UseActionAddonRow}（使用特效）";
            Log(lastResult);
            return;
        }

        runLabel = label;
        runStartedUtc = DateTime.UtcNow;
        lastArCheckAt = 0;
        recordedStableReads = 0;
        confirmLease.Reset();
        var now = Environment.TickCount64;
        var companyWasOpen = UiHelper.IsAddonReady(CompanyAddon);
        var templateProblems = new List<string>();
        rejectionTemplates = LoadRejectionTemplates(templateProblems);
        Log($"{label}：開始（失敗訊息樣板 {rejectionTemplates.Count} 則"
            + (templateProblems.Count > 0 ? $"；不比對：{string.Join("；", templateProblems)}" : string.Empty) + "）");
        sequence = new FcActionSequence(targets, useText, prefix, suffix, companyWasOpen, now, Log);
    }

    private void TickSequence()
    {
        var run = sequence!;
        var now = Environment.TickCount64;

        if (!Svc.ClientState.IsLoggedIn || Svc.Objects.LocalPlayer == null)
        {
            Log($"「{runLabel}」中止：角色不在了（停在「{run.StepName}」）");
            sequence = null;
            return;
        }

        if (AutomationGate.LastEmergencyStopUtc > runStartedUtc)
            run.Abort("收到全艦隊急停", now);
        else if (Svc.Condition[ConditionFlag.BetweenAreas] || Svc.Condition[ConditionFlag.BetweenAreas51])
            run.Abort("開始讀取區域", now);
        else if (Svc.Condition[ConditionFlag.InCombat] || Svc.Condition[ConditionFlag.BoundByDuty])
            run.Abort("進入戰鬥或副本", now);
        else if (now - lastArCheckAt >= ArRecheckMs)
        {
            lastArCheckAt = now;
            if (QueryMultiMode() == FcMultiMode.On) run.Abort("AutoRetainer 多角色模式開了", now);
            else if (QueryArBusy() == true) run.Abort("AutoRetainer 開始作業", now);
        }

        Observe();
        var command = run.Tick(now, observation);
        if (run.StableStockReads != recordedStableReads && run.LastStableStock is { Valid: true } stable)
        {
            recordedStableReads = run.StableStockReads;
            RecordStock(Svc.PlayerState.ContentId, stable, "流程讀到");
        }

        Execute(run, command, now);

        if (command.Kind == FcCommandKind.Finished)
        {
            lastResult = $"{runLabel}：開了 {Join(run.Used)}；沒開成 {Join(run.Failed)}"
                         + (run.StopReason.Length > 0 ? $"；{run.StopReason}" : string.Empty);
            if (run.Used.Count > 0 || run.Failed.Count > 0 || run.StopReason.Length > 0)
                Svc.Chat.Print($"[TC Toolbox] 公會特效：{lastResult}");
            sequence = null;
        }
    }

    private void Observe()
    {
        var o = observation;
        o.CompanyReady = UiHelper.IsAddonReady(CompanyAddon);
        o.ActionReady = UiHelper.IsAddonReady(ActionAddon);
        o.Stock = o.ActionReady ? ReadStock() : null;

        var menu = UiHelper.GetAddon(MenuAddon);
        o.MenuReady = UiHelper.IsReady(menu);
        o.MenuEntries = o.MenuReady ? ReadMenuEntries(menu) : Array.Empty<string>();

        var confirm = UiHelper.GetAddon(ConfirmAddon);
        o.ConfirmReady = UiHelper.IsReady(confirm);
        o.ConfirmText = o.ConfirmReady ? UiHelper.GetSelectYesnoText(confirm) : string.Empty;

        o.Statuses = (IReadOnlyCollection<uint>?)ReadStatuses() ?? Array.Empty<uint>();
    }

    private void Execute(FcActionSequence run, FcCommand command, long now)
    {
        switch (command.Kind)
        {
            case FcCommandKind.OpenCompanyWindow:
                if (!ChatSender.ExecuteCommand(OpenCompanyCommand))
                    run.Abort($"{OpenCompanyCommand} 送不出去", now);
                return;

            case FcCommandKind.OpenActionTab:
            {
                var company = UiHelper.GetAddon(CompanyAddon);
                if (UiHelper.IsReady(company)) UiHelper.TryFireCallback(company, true, 0, ActionTabValue);
                return;
            }

            case FcCommandKind.SelectStockRow:
            {
                var action = UiHelper.GetAddon(ActionAddon);
                if (!UiHelper.IsReady(action) || !UiHelper.TryFireCallback(action, true, 1, (uint)command.Index))
                    run.Rejected("點庫存列沒送出去", now);
                return;
            }

            case FcCommandKind.SelectMenuEntry:
            {
                if (!confirmLease.Renew())
                {
                    run.Abort($"YesAlready 讓不開確認框（{confirmLease.Last}），不選「使用特效」", now);
                    return;
                }

                Log($"已請 YesAlready 暫停確認框（{confirmLease.Last}）");
                var menu = UiHelper.GetAddon(MenuAddon);
                if (!UiHelper.IsReady(menu) || !UiHelper.TryFireCallback(menu, true, 0, command.Index, 0))
                    run.Rejected("選單項目沒送出去", now);
                return;
            }

            case FcCommandKind.ClickYes:
                if (!confirmLease.IsExclusive)
                {
                    run.Abort($"按「是」前確認不了 YesAlready 已讓開（{confirmLease.Last}），不按，確認框留給你", now);
                    return;
                }

                if (!UiHelper.ClickSelectYesnoYes())
                {
                    run.Rejected("「是」沒按下去", now);
                    return;
                }

                RecordUse(run.Current.ActionId);
                return;

            case FcCommandKind.ClickNo:
                if (!confirmLease.IsExclusive)
                {
                    Log($"確認不了 YesAlready 已讓開（{confirmLease.Last}），「否」也不按，確認框留給你");
                    return;
                }

                UiHelper.ClickSelectYesnoNo();
                return;

            case FcCommandKind.CloseMenu:
                CloseAddon(MenuAddon);
                return;

            case FcCommandKind.CloseCompanyWindow:
                CloseAddon(CompanyAddon);
                return;
        }
    }

    private static void CloseAddon(string name)
    {
        var addon = UiHelper.GetAddon(name);
        if (!UiHelper.IsReady(addon)) return;
        if (!AddonPressGuard.TryBeginPress(name, addon, "close")) return;
        addon->Close(true);
    }

    private bool UsedRecently(ulong cid, uint actionId, out double hoursAgo)
    {
        hoursAgo = 0;
        if (!Config.LastUsedUtc.TryGetValue(cid, out var byAction) || byAction == null) return false;
        if (!byAction.TryGetValue(actionId, out var at)) return false;

        hoursAgo = (DateTime.UtcNow - at).TotalHours;
        return hoursAgo >= 0 && hoursAgo < RecentUseHours;
    }

    private void RecordUse(uint actionId)
    {
        var cid = Svc.PlayerState.ContentId;
        if (cid == 0) return;

        if (!Config.LastUsedUtc.TryGetValue(cid, out var byAction) || byAction == null)
        {
            byAction = [];
            Config.LastUsedUtc[cid] = byAction;
        }

        byAction[actionId] = DateTime.UtcNow;
        Plugin.Instance.Config.Save();
    }

    #endregion

    #region 庫存記帳、提醒、到期續開

    /// <summary>每秒一次：追蹤勾選特效的狀態、順手記下使用者自己打開的特效分頁、登入後提醒一次低庫存。</summary>
    /// <remarks>本機玩家不在就把狀態追蹤歸零（之後重新從「現在看到的」起算，消失判斷只會變保守）。</remarks>
    private void Poll(long now)
    {
        var cid = Svc.PlayerState.ContentId;
        var player = GameControl.GetLocalPlayer();
        if (!Svc.ClientState.IsLoggedIn || cid == 0 || player == null)
        {
            statusWatch.Clear();
            sessionReadySince = 0;
            ResetManualTab();
            return;
        }

        if (cid != sessionCid)
        {
            if (sessionCid != 0) ResetSession("換了角色");
            sessionCid = cid;
        }

        var settled = !Svc.Condition[ConditionFlag.BetweenAreas] && !Svc.Condition[ConditionFlag.BetweenAreas51];
        if (sessionReadySince == 0 && settled) sessionReadySince = now;

        WatchStatuses(cid, player, settled, now);
        if (sequence == null) WatchManualActionTab(cid, now);

        if (!sessionReminderChecked && sessionReadySince != 0 && now - sessionReadySince >= LoginSettleMs)
        {
            sessionReminderChecked = true;
            CheckReminders(cid, "登入後");
        }
    }

    private void WatchStatuses(ulong cid, BattleChara* player, bool settled, long now)
    {
        statusWatch.BeginMark();
        if (Config.ActionsByCharacter.TryGetValue(cid, out var chosen) && chosen != null)
        {
            foreach (var actionId in chosen)
            {
                if (FcActionCatalog.TryGetStatus(actionId, out var sid)) statusWatch.Mark(sid);
            }
        }

        statusWatch.Sweep();
        if (statusWatch.Count == 0) return;

        var nowUtc = DateTime.UtcNow;
        for (var i = 0; i < statusWatch.Count; i++)
        {
            var sid = statusWatch.StatusAt(i);
            var present = TryFindStatus(player, sid, out var remaining);
            var evt = statusWatch.Observe(sid, present, remaining, settled, now, nowUtc);
            if (evt.Kind != FcWatchEventKind.None) OnStatusEvent(cid, evt, nowUtc);
        }
    }

    /// <summary>同一幀內讀完；不保存指標。</summary>
    private static bool TryFindStatus(BattleChara* player, uint statusId, out float remaining)
    {
        remaining = 0;
        foreach (ref readonly var status in player->StatusManager.Status)
        {
            if (status.StatusId != statusId) continue;
            remaining = status.RemainingTime;
            return true;
        }

        return false;
    }

    private void OnStatusEvent(ulong cid, in FcWatchEvent evt, DateTime nowUtc)
    {
        var name = StatusName(evt.StatusId);
        if (evt.Kind == FcWatchEventKind.Appeared)
        {
            if (pendingRenew.Remove(evt.StatusId))
                Log($"「{name}」又出現在身上，取消等待中的到期續開");

            if (!evt.AfterConfirmedAbsence) return;
            if (!FcExpiryJudge.ShouldAdoptObservedStart(KnownStartUtc(cid, evt.StatusId, out _), nowUtc)) return;

            SetObservedStart(cid, evt.StatusId, nowUtc);
            Log($"「{name}」（狀態 {evt.StatusId}）在確定沒有之後出現，記下這個時間當開始時間（到期判斷用）");
            return;
        }

        var start = KnownStartUtc(cid, evt.StatusId, out var source);
        var verdict = FcExpiryJudge.Judge(evt, start, out var why);
        var startText = start is { } s ? $"{s.ToLocalTime():MM-dd HH:mm}（{source}）" : "不知道";
        string next;
        if (verdict != FcExpiryVerdict.Expired) next = "不續開";
        else if (!Config.RenewOnExpiry) next = "「到期時自動續開」沒開，不續開";
        else
        {
            next = "等條件齊了續開一次";
            pendingRenew.Add(evt.StatusId);
            lastRenewReason = string.Empty;
            lastRenewCheckAt = 0;
        }

        Log($"「{name}」（狀態 {evt.StatusId}）從身上消失：最後看到的剩餘時間 {evt.LastRemaining:F1} 秒"
            + $"（之後 {evt.SecondsSinceLastSample:F0} 秒就看不到；剩餘時間{(evt.RemainingCounts ? "有" : "沒有")}在倒數）；"
            + $"開始時間 {startText}；判定：{VerdictText(verdict)}——{why}；{next}");
    }

    private static string VerdictText(FcExpiryVerdict v) => v switch
    {
        FcExpiryVerdict.Expired => "到期",
        FcExpiryVerdict.NotExpired => "不是到期",
        _ => "判不出",
    };

    /// <summary>已知最近一次開始的時間：本插件按「是」的時間與看到它出現的時間取較晚的。</summary>
    private DateTime? KnownStartUtc(ulong cid, uint statusId, out string source)
    {
        DateTime? best = null;
        source = "不知道";
        if (Config.LastUsedUtc.TryGetValue(cid, out var used) && used != null)
        {
            foreach (var (actionId, at) in used)
            {
                if (!FcActionCatalog.TryGetStatus(actionId, out var sid) || sid != statusId) continue;
                if (best != null && at <= best) continue;
                best = at;
                source = "本插件按「是」";
            }
        }

        if (Config.ObservedStartUtc.TryGetValue(cid, out var seen) && seen != null
            && seen.TryGetValue(statusId, out var seenAt) && (best == null || seenAt > best))
        {
            best = seenAt;
            source = "看到它出現";
        }

        return best;
    }

    private void SetObservedStart(ulong cid, uint statusId, DateTime utc)
    {
        if (!Config.ObservedStartUtc.TryGetValue(cid, out var byStatus) || byStatus == null)
        {
            byStatus = [];
            Config.ObservedStartUtc[cid] = byStatus;
        }

        byStatus[statusId] = utc;
        Plugin.Instance.Config.Save();
    }

    private void TickRenewalSafely(long now)
    {
        try
        {
            TickRenewal(now);
        }
        catch (Exception ex)
        {
            Svc.Log.Warning($"{LogTag} 到期續開判斷出錯（{ex.GetType().Name}：{ex.Message}），取消這次續開：{StatusNames(pendingRenew)}");
            pendingRenew.Clear();
        }
    }

    /// <summary>
    /// 走和登入同一道閘門（<see cref="Evaluate"/>，含 23 小時保護與「閒著」判斷），只開判定到期的那幾個。
    /// 閘門不過就留著等；每個到期事件最多開一次流程，不論成敗都不再重試。
    /// </summary>
    private void TickRenewal(long now)
    {
        if (now - lastRenewCheckAt < RenewRecheckMs) return;
        lastRenewCheckAt = now;

        if (!Config.RenewOnExpiry)
        {
            Log($"「到期時自動續開」被關掉，取消等待中的：{StatusNames(pendingRenew)}");
            pendingRenew.Clear();
            return;
        }

        var cid = Svc.PlayerState.ContentId;
        if (cid == 0 || cid != sessionCid) return;

        var verdict = Evaluate(manual: false, out var reason, out var targets);
        targets.RemoveAll(t => !pendingRenew.Contains(t.StatusId));
        if (targets.Count == 0)
        {
            Log($"到期續開：取消 {StatusNames(pendingRenew)}——已經在身上、23 小時內按過「是」或取消了勾選"
                + (reason.Length > 0 ? $"（{reason}）" : string.Empty));
            pendingRenew.Clear();
            return;
        }

        if (StockRecord(cid) is { } record)
        {
            var empty = targets.Where(t => CountOf(record, t.ActionId) <= 0).ToList();
            foreach (var t in empty)
            {
                targets.Remove(t);
                if (targets.Any(x => x.StatusId == t.StatusId)) continue;

                pendingRenew.Remove(t.StatusId);
                var msg = $"「{t.Name}」到期了，但庫存記錄裡沒有（{DescribeAge(record.ReadUtc)}讀到），沒有續開；" +
                          "補充後開一次特效分頁或按「現在開」。";
                Svc.Chat.Print($"[TC Toolbox] 公會特效：{msg}");
                Log($"到期續開：{msg}");
            }

            if (targets.Count == 0)
            {
                pendingRenew.Clear();
                return;
            }
        }

        if (verdict == FcGateVerdict.Go)
        {
            pendingRenew.Clear();
            lastRenewReason = string.Empty;
            Start(targets, "到期續開");
            return;
        }

        var hold = reason.Length > 0 ? reason : "條件還不齊";
        if (hold == lastRenewReason) return;
        lastRenewReason = hold;
        Log($"到期續開：先等——{hold}");
    }

    /// <summary>使用者（或別的插件）開著特效分頁時順手記庫存：等沉澱、連兩次讀數一樣才記，同一次開著只記一次。</summary>
    /// <remarks>讀法和流程裡完全相同（同一個陣列、同一個解析），沒有另外猜原生版面。</remarks>
    private void WatchManualActionTab(ulong cid, long now)
    {
        if (!UiHelper.IsAddonReady(ActionAddon))
        {
            ResetManualTab();
            return;
        }

        if (manualTabSince < 0)
        {
            manualTabSince = now;
            return;
        }

        if (now - manualTabSince < FcActionSequence.StockSettleMs) return;

        var stock = ReadStock();
        if (!stock.Valid)
        {
            manualTabLastSignature = null;
            if (!manualTabInvalidLogged)
            {
                manualTabInvalidLogged = true;
                Log($"看到特效分頁開著，但庫存讀數{stock.Describe()}，不記");
            }

            return;
        }

        var signature = stock.Describe();
        if (signature != manualTabLastSignature)
        {
            manualTabLastSignature = signature;
            return;
        }

        if (signature == manualTabRecordedSignature) return;
        manualTabRecordedSignature = signature;
        Log($"看到特效分頁開著：庫存 {stock.Actions.Count} 筆：{signature}（原始：{stock.DescribeRaw()}）");
        RecordStock(cid, stock, "看到特效分頁");
    }

    private void ResetManualTab()
    {
        manualTabSince = -1;
        manualTabLastSignature = null;
        manualTabRecordedSignature = null;
        manualTabInvalidLogged = false;
    }

    private void RecordStock(ulong cid, FcStockRead stock, string source)
    {
        if (cid == 0 || !stock.Valid) return;

        var counts = FcStockLedger.CountByAction(stock.Actions);
        if (!Config.StockByCharacter.TryGetValue(cid, out var record) || record == null)
        {
            record = new FcStockRecord();
            Config.StockByCharacter[cid] = record;
        }

        record.Counts = counts;
        record.ReadUtc = DateTime.UtcNow;
        Plugin.Instance.Config.Save();
        CheckReminders(cid, source);
    }

    /// <summary>勾選的特效庫存 ≤ 門檻就在聊天提醒；每次登入每種最多一次。</summary>
    private void CheckReminders(ulong cid, string when)
    {
        var threshold = Config.LowStockThreshold;
        if (threshold <= 0) return;
        if (!Config.ActionsByCharacter.TryGetValue(cid, out var chosen) || chosen == null || chosen.Count == 0) return;
        if (StockRecord(cid) is not { } record) return;

        foreach (var actionId in FcStockLedger.DueReminders(chosen, record.Counts, threshold, remindedThisLogin))
        {
            remindedThisLogin.Add(actionId);
            var name = ActionName(actionId) is { Length: > 0 } n ? n : actionId.ToString();
            var msg = $"「{name}」庫存剩 {CountOf(record, actionId)} 個（約，{DescribeAge(record.ReadUtc)}讀到），記得用公會戰績補充。";
            Svc.Chat.Print($"[TC Toolbox] 公會特效：{msg}");
            Log($"庫存提醒（{when}，門檻 {threshold}）：{msg}");
        }
    }

    private FcStockRecord? StockRecord(ulong cid) =>
        cid != 0 && Config.StockByCharacter.TryGetValue(cid, out var record) ? record : null;

    private static int CountOf(FcStockRecord record, uint actionId) =>
        record.Counts != null && record.Counts.TryGetValue(actionId, out var n) ? n : 0;

    private static string DescribeAge(DateTime utc)
    {
        var age = DateTime.UtcNow - utc;
        if (age < TimeSpan.Zero) return "剛才";
        if (age.TotalMinutes < 1) return "剛才";
        if (age.TotalHours < 1) return $"{age.TotalMinutes:F0} 分鐘前";
        if (age.TotalHours < 48) return $"{age.TotalHours:F0} 小時前";
        return $"{age.TotalDays:F0} 天前";
    }

    private static readonly Dictionary<uint, string> StatusNamesById = [];

    private static string StatusName(uint statusId)
    {
        if (StatusNamesById.TryGetValue(statusId, out var cached)) return cached;

        var name = Svc.Data.GetExcelSheet<Status>().GetRowOrDefault(statusId)?.Name.ExtractText() ?? string.Empty;
        if (name.Length == 0) name = $"狀態 {statusId}";
        StatusNamesById[statusId] = name;
        return name;
    }

    private static string StatusNames(IEnumerable<uint> statusIds) => string.Join("、", statusIds.Select(StatusName));

    #endregion

    #region 遊戲資料

    /// <summary>公會特效視窗的庫存清單。陣列的 <c>Size</c> 先截過才複製，讀錯陣列也不會越界。</summary>
    private static FcStockRead ReadStock()
    {
        var atk = RaptureAtkModule.Instance();
        if (atk == null) return FcStockRead.Invalid("RaptureAtkModule 取不到");

        ref var holder = ref atk->AtkModule.AtkArrayDataHolder;
        const int index = (int)NumberArrayType.FreeCompanyAction;
        if (holder.NumberArrays == null || index >= holder.NumberArrayCount)
            return FcStockRead.Invalid($"NumberArray 只有 {holder.NumberArrayCount} 個");

        var array = holder.NumberArrays[index];
        if (array == null || array->IntArray == null)
            return FcStockRead.Invalid($"NumberArray {index} 是空的");

        var length = Math.Min(array->AtkArrayData.Size, FcActionStock.MaxReadLength);
        if (length <= 0) return FcStockRead.Invalid($"NumberArray {index} 長度 {array->AtkArrayData.Size}");

        Span<int> copy = stackalloc int[length];
        for (var i = 0; i < length; i++) copy[i] = array->IntArray[i];
        return FcActionStock.Parse(copy);
    }

    /// <summary>右鍵選單的項目文字（<c>[0]</c>＝筆數、<c>[第一項+i]</c>＝第 i 筆）；讀不到的項目留空字串，索引照舊對齊。</summary>
    /// <remarks>推不出版面時回空清單：序列看到的是「選單沒有這一項」，不會選。</remarks>
    private static IReadOnlyList<string> ReadMenuEntries(AtkUnitBase* menu)
    {
        if (!ContextMenuLayout.TryGetFirstEntryIndex(out var firstEntryIndex)) return Array.Empty<string>();
        if (menu == null || menu->AtkValues == null) return Array.Empty<string>();

        var values = menu->AtkValuesSpan;
        if (values.Length < firstEntryIndex) return Array.Empty<string>();

        var count = (int)values[0].UInt;
        if (count <= 0 || values.Length - firstEntryIndex < count) return Array.Empty<string>();

        var result = new string[count];
        for (var i = 0; i < count; i++)
        {
            var value = values[firstEntryIndex + i];
            result[i] = value.Type is ValueType.String or ValueType.ManagedString or ValueType.String8 && value.String.Value != null
                ? MemoryHelper.ReadSeStringNullTerminated((nint)value.String.Value).TextValue
                : string.Empty;
        }

        return result;
    }

    /// <summary>身上的狀態編號；角色不在時回 <see langword="null"/>（不是「沒有」）。</summary>
    private static HashSet<uint>? ReadStatuses()
    {
        var player = Svc.Objects.LocalPlayer;
        if (player == null) return null;

        var set = new HashSet<uint>();
        foreach (var status in player.StatusList)
        {
            if (status != null && status.StatusId != 0) set.Add(status.StatusId);
        }

        return set;
    }

    private bool IsFcBuffStatus(uint statusId)
    {
        if (statusRowIsFcBuff.TryGetValue(statusId, out var cached)) return cached;

        var ok = Svc.Data.GetExcelSheet<Status>().GetRowOrDefault(statusId) is { IsFcBuff: true };
        statusRowIsFcBuff[statusId] = ok;
        if (!ok) Log($"Status {statusId} 在這個用戶端不是公會特效狀態（IsFcBuff=false 或沒有這列），對應的特效不自動開");
        return ok;
    }

    private static readonly Dictionary<uint, string> ActionNames = [];

    /// <summary>CompanyAction 名稱（設定畫面每幀都要，查一次就記住）；沒有這列回空字串。</summary>
    private static string ActionName(uint actionId)
    {
        if (ActionNames.TryGetValue(actionId, out var cached)) return cached;

        var name = Svc.Data.GetExcelSheet<CompanyAction>().GetRowOrDefault(actionId)?.Name.ExtractText() ?? string.Empty;
        ActionNames[actionId] = name;
        return name;
    }

    private static string AddonText(uint rowId) =>
        Svc.Data.GetExcelSheet<Addon>().GetRowOrDefault(rowId)?.Text.ExtractText() ?? string.Empty;

    private void OnChatMessage(XivChatType type, int timestamp, ref SeString sender, ref SeString message, ref bool isHandled)
    {
        if (sequence != null) HandleGameMessage(message, $"聊天類型 {(int)type}");
    }

    private void OnErrorToast(ref SeString message, ref bool isHandled)
    {
        if (sequence != null) HandleGameMessage(message, "畫面錯誤提示");
    }

    /// <summary>只在流程跑著時比對；只讀，不攔、不改訊息。</summary>
    private void HandleGameMessage(SeString message, string channel)
    {
        try
        {
            var run = sequence;
            if (run == null || rejectionTemplates.Count == 0) return;

            var text = message.TextValue;
            var kind = FcActionText.ClassifyRejection(text, rejectionTemplates, out var hit);
            if (kind == FcUseRejection.None) return;

            var label = FcActionText.DescribeRejection(kind);
            var detail = $"{hit.Source}，{channel}";
            Log($"收到遊戲訊息「{FcActionText.Clean(text)}」＝{label}（{detail}）");
            run.GameRejected(kind, detail, Environment.TickCount64);

            var who = Svc.PlayerState.CharacterName;
            lastRejection = $"{DateTime.Now:MM/dd HH:mm}{(who.Length > 0 ? $" {who}" : string.Empty)}：{label}";
            lastRejectionDetail = $"遊戲原文：{FcActionText.Clean(text)}\n出處：{detail}";
        }
        catch (Exception ex)
        {
            if (Throttle.Pass("FcActionAutoUse-GameMessage", 60_000))
                Svc.Log.Warning($"{LogTag} 比對遊戲訊息出錯（{ex.GetType().Name}：{ex.Message}），這則略過");
        }
    }

    /// <summary>使用特效被拒時遊戲會回的整句；讀不出來的那則不比對（只少一種精確原因，流程照舊靠逾時收尾）。</summary>
    private static List<FcRejectionTemplate> LoadRejectionTemplates(List<string> problems)
    {
        var result = new List<FcRejectionTemplate>();

        void Add(FcUseRejection kind, string source, string text, string problem)
        {
            if (text.Length > 0) result.Add(new FcRejectionTemplate(kind, text, source));
            else problems.Add($"{source}（{problem}）");
        }

        foreach (var (kind, row) in new[]
                 {
                     (FcUseRejection.UseFailed, UseFailedLogRow),
                     (FcUseRejection.LimitReached, LimitReachedLogRow),
                     (FcUseRejection.SameKindActive, SameKindLogRow),
                     (FcUseRejection.NoPermission, NoPermissionLogRow),
                 })
        {
            var text = RenderLogMessage(row, out var problem);
            Add(kind, $"LogMessage {row}", text, problem);
        }

        Add(FcUseRejection.NoPermission, $"Addon {NoPermissionAddonRow}", AddonText(NoPermissionAddonRow).Trim(), "讀不到");
        return result;
    }

    private static string RenderLogMessage(uint rowId, out string problem)
    {
        var row = Svc.Data.GetExcelSheet<LogMessage>().GetRowOrDefault(rowId);
        if (row == null)
        {
            problem = "讀不到";
            return string.Empty;
        }

        return FcRejectionRender.Render(row.Value.Text,
            id => Svc.Data.GetExcelSheet<FCAuthority>().GetRowOrDefault(id)?.Name.ExtractText() ?? string.Empty,
            out problem);
    }

    /// <summary>Addon 2877 拆成 placeholder 前後兩段；形狀不是「文字＋巨集＋文字」就不用（fail-closed）。</summary>
    private static bool TryGetConfirmTemplate(out string prefix, out string suffix, out string problem)
    {
        prefix = suffix = problem = string.Empty;
        var row = Svc.Data.GetExcelSheet<Addon>().GetRowOrDefault(ConfirmAddonRow);
        if (row == null)
        {
            problem = $"讀不到 Addon {ConfirmAddonRow}（確認框句型）";
            return false;
        }

        var texts = new List<string>();
        foreach (var payload in row.Value.Text)
        {
            if (payload.Type == ReadOnlySePayloadType.Text)
                texts.Add(Encoding.UTF8.GetString(payload.Body.Span));
        }

        if (texts.Count != 2)
        {
            problem = $"Addon {ConfirmAddonRow} 的句型有 {texts.Count} 段文字，預期 2 段：[{string.Join("｜", texts)}]";
            return false;
        }

        prefix = texts[0];
        suffix = texts[1];
        return true;
    }

    #endregion

    #region 設定畫面

    public override void DrawConfig()
    {
        var cid = Svc.PlayerState.ContentId;
        if (cid == 0 || !Svc.ClientState.IsLoggedIn)
        {
            ImGui.TextDisabled("登入角色後才能設定這個角色要維持哪些特效。");
            DrawOtherCharacters(0);
            return;
        }

        var label = $"{Svc.PlayerState.CharacterName}＠{Svc.PlayerState.HomeWorld.ValueNullable?.Name.ExtractText()}";
        ImGui.TextUnformatted($"目前角色：{label}");
        ImGui.TextDisabled("勾選要維持的特效（同一種特效的不同等級共用同一個狀態，勾一個就好）：");

        var statuses = ReadStatuses();
        Config.ActionsByCharacter.TryGetValue(cid, out var chosen);
        var changed = false;
        var record = StockRecord(cid);
        DrawStockHeader(record);

        using (var table = ImRaii.Table("##fcActions", FcActionCatalog.TierCount + 1, ImGuiTableFlags.SizingStretchProp))
        {
            if (table)
            {
                foreach (var first in FcActionCatalog.FirstTierActions)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    if (FcActionCatalog.TryGetStatus(first, out var statusId))
                    {
                        var on = statuses?.Contains(statusId);
                        if (on == true) ImGui.TextColored(new System.Numerics.Vector4(0.4f, 0.9f, 0.4f, 1f), "生效中");
                        else ImGui.TextDisabled(on == null ? "?" : "—");
                    }

                    for (var tier = 0; tier < FcActionCatalog.TierCount; tier++)
                    {
                        ImGui.TableNextColumn();
                        var actionId = FcActionCatalog.ActionOf(first, tier);
                        if (actionId == 0) continue;

                        var name = ActionName(actionId);
                        var isChosen = chosen?.Contains(actionId) == true;
                        if (ImGui.Checkbox($"{(name.Length > 0 ? name : actionId.ToString())}##fca{actionId}", ref isChosen))
                        {
                            chosen = SetChosen(cid, label, actionId, isChosen);
                            changed = true;
                        }

                        DrawStockCount(record, actionId, isChosen);
                    }
                }
            }
        }

        if (changed) Plugin.Instance.Config.Save();

        ImGui.Spacing();
        var running = sequence != null;
        using (ImRaii.Disabled(running))
        {
            if (ImGui.Button("現在開")) RunManually();
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("對目前角色勾選、但身上還沒有的特效，從公會庫存裡使用一次。\n" +
                             "AutoRetainer 正在作業或多角色模式開著時不會動作。");

        ImGui.SameLine();
        if (running) ImGui.TextDisabled($"執行中：{sequence!.StepName}");
        else if (lastResult.Length > 0) ImGui.TextDisabled(lastResult);

        if (lastRejection.Length > 0)
        {
            ImGui.TextDisabled($"上次被遊戲拒絕：{lastRejection}");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(lastRejectionDetail);
        }

        DrawUpkeepSettings();
        DrawOtherCharacters(cid);
    }

    private void DrawStockHeader(FcStockRecord? record)
    {
        if (record == null)
        {
            ImGui.TextColored(new System.Numerics.Vector4(0.9f, 0.75f, 0.3f, 1f), "庫存：未讀過");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("開一次公會視窗的「特效」分頁（停一兩秒），或按「現在開」，就會記下這個角色的庫存。");
            return;
        }

        ImGui.TextDisabled($"庫存（約）：{DescribeAge(record.ReadUtc)}讀到");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(StockTooltip(record));
    }

    private void DrawStockCount(FcStockRecord? record, uint actionId, bool chosen)
    {
        if (record == null)
        {
            if (!chosen) return;
            ImGui.SameLine();
            ImGui.TextDisabled("未讀過");
            return;
        }

        var n = CountOf(record, actionId);
        if (n == 0 && !chosen) return;

        ImGui.SameLine();
        var text = $"剩 {n} 個（約）";
        var threshold = Config.LowStockThreshold;
        if (chosen && threshold > 0 && n <= threshold)
            ImGui.TextColored(new System.Numerics.Vector4(0.9f, 0.6f, 0.3f, 1f), text);
        else
            ImGui.TextDisabled(text);

        if (ImGui.IsItemHovered()) ImGui.SetTooltip(StockTooltip(record));
    }

    private static string StockTooltip(FcStockRecord record) =>
        $"讀取時間：{record.ReadUtc.ToLocalTime():yyyy-MM-dd HH:mm}（{DescribeAge(record.ReadUtc)}）\n" +
        "之後在補給負責人那裡補充的不會算進來，開一次特效分頁就會更新。\n" +
        "數量照庫存清單的筆數算，還沒在實機確認每一份是不是各佔一筆，所以標「約」。";

    private void DrawUpkeepSettings()
    {
        ImGui.Spacing();
        var renew = Config.RenewOnExpiry;
        if (ImGui.Checkbox("特效到期時自動續開（只用庫存）##fcaRenew", ref renew))
        {
            Config.RenewOnExpiry = renew;
            Plugin.Instance.Config.Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("勾選要維持的特效在遊戲中途到期、從身上消失時，等你閒下來\n" +
                             "（不在戰鬥、對話、製作、採集、過場、讀取區域，AutoRetainer 也沒在作業）再從庫存開一次。\n" +
                             "只有確定是到期才開：換區閃一下、自己取消、判不出來的都不開。\n" +
                             "庫存記錄是 0 就不開，改在聊天提醒。每次到期最多試一次；23 小時內按過「是」的不再開。");

        if (pendingRenew.Count > 0)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"等待續開：{StatusNames(pendingRenew)}");
            if (lastRenewReason.Length > 0 && ImGui.IsItemHovered()) ImGui.SetTooltip($"先等：{lastRenewReason}");
        }

        var threshold = Config.LowStockThreshold;
        ImGui.SetNextItemWidth(UiScale.Px(100f));
        if (ImGui.InputInt("庫存剩幾個以下就提醒##fcaLow", ref threshold))
        {
            Config.LowStockThreshold = Math.Clamp(threshold, 0, 15);
            Plugin.Instance.Config.Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("勾選的特效庫存（約）剩這麼多個以下時，每次登入在聊天提醒一次。0＝不提醒。");
    }

    private List<uint> SetChosen(ulong cid, string label, uint actionId, bool on)
    {
        if (!Config.ActionsByCharacter.TryGetValue(cid, out var list))
        {
            list = [];
            Config.ActionsByCharacter[cid] = list;
        }

        if (on && !list.Contains(actionId)) list.Add(actionId);
        if (!on) list.Remove(actionId);

        Config.CharacterNames[cid] = label;
        return list;
    }

    private void DrawOtherCharacters(ulong currentCid)
    {
        var others = Config.ActionsByCharacter.Where(x => x.Key != currentCid && x.Value.Count > 0).ToList();
        if (others.Count == 0) return;

        ImGui.Spacing();
        ImGui.TextDisabled("其他角色：");
        ulong? remove = null;
        foreach (var (cid, actions) in others)
        {
            var name = Config.CharacterNames.TryGetValue(cid, out var n) ? n : $"{cid:X}";
            var record = StockRecord(cid);
            ImGui.BulletText($"{name}：{string.Join("、", actions.Select(a => $"{(ActionName(a) is { Length: > 0 } s ? s : a.ToString())}" +
                                                                               (record == null ? "（未讀過）" : $"（剩 {CountOf(record, a)} 個，約）")))}");
            if (record != null && ImGui.IsItemHovered()) ImGui.SetTooltip(StockTooltip(record));
            ImGui.SameLine();
            if (ImGui.SmallButton($"清除##fcaClear{cid}")) remove = cid;
        }

        if (remove is { } key)
        {
            Config.ActionsByCharacter.Remove(key);
            Config.CharacterNames.Remove(key);
            Plugin.Instance.Config.Save();
        }
    }

    #endregion

    private static string Join(List<string> names) => names.Count == 0 ? "（無）" : string.Join("、", names);

    private static void Log(string message) => Svc.Log.Information($"{LogTag} {message}");
}

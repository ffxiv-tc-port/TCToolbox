using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Memory;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;
using TCToolbox.Core;
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
        "只用庫存，不花公會戰績購買。";

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

    /// <summary>公會視窗切到「特效」分頁的 callback 值（照 AutoRetainer 上游）。</summary>
    private const uint ActionTabValue = 4;

    private const int LoginSettleMs = 8_000;
    private const int GateWindowMs = 60_000;
    private const int GateRecheckMs = 1_000;
    private const int ArRecheckMs = 500;

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

        // 刻意不在這裡補跑：啟用模組或熱載入時人已經在遊戲裡，那不是「登入」。
    }

    protected override void OnDisable()
    {
        Svc.ClientState.Login -= OnLogin;
        Svc.ClientState.Logout -= OnLogout;
        Svc.Framework.Update -= OnUpdate;

        if (sequence != null)
            Log($"模組停用，中止「{runLabel}」（停在「{sequence.StepName}」；已開的視窗留給你）");

        sequence = null;
        DisarmLogin();
        confirmLease.Reset();
    }

    private void OnLogin()
    {
        DisarmLogin();
        armed = true;
        Log("收到登入事件，等角色就緒後檢查要不要開公會特效");
    }

    private void OnLogout(int type, int code)
    {
        if (armed || sequence != null)
            Log(sequence != null ? $"登出，中止「{runLabel}」（停在「{sequence.StepName}」）" : "登出，取消這次登入的檢查");

        sequence = null;
        DisarmLogin();
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
        if (sequence != null)
        {
            TickSequence();
            return;
        }

        if (armed) TickLoginGate();
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
        confirmLease.Reset();
        var now = Environment.TickCount64;
        var companyWasOpen = UiHelper.IsAddonReady(CompanyAddon);
        Log($"{label}：開始");
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

        DrawOtherCharacters(cid);
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
            ImGui.BulletText($"{name}：{string.Join("、", actions.Select(a => ActionName(a) is { Length: > 0 } s ? s : a.ToString()))}");
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

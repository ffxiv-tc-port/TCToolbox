using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.Text;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Memory;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using TCToolbox.Core;
using GameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;
using ValueType = FFXIVClientStructs.FFXIV.Component.GUI.ValueType;

namespace TCToolbox.Modules;

// 本檔假設與契約：「本角色一鍵」＝傳送 → 進軍營 → 派遣（沿用 v2a 步驟）→ 會議板綜合訓練。
// 傳送交給 Lifestream 傳送面板的我的最愛（含使用者的自訂落點）；進門確認框交給使用者的 YesAlready 規則，本插件不按。
// 每一步都在同一幀重新取窗與物件、判 ready 再讀再送，送出一律走 UiHelper.TryFireCallback（AddonPressGuard）；
// 只存 BaseId、領地編號與旗標，不跨幀保存任何原生指標。訓練窗只在 [0]＝0 且七顆鈕文字依序等於表時才送 (12, 6)。
public sealed unsafe partial class SquadronRoutine
{
    /// <summary>城裡離入口這麼近（米）就不傳送、直接走過去。</summary>
    private const float DoorNearMeters = 60f;

    private static readonly TimeSpan DoorNoResponse = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DoorYesnoUnanswered = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ExpeditionCloseWait = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan TrainYesnoWait = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan TrainStartedWait = TimeSpan.FromSeconds(10);

    /// <summary>訓練確認框（「開始<訓練名>。」）；附上限警告的 10580 刻意不認。</summary>
    private const uint TrainingConfirmAddonRow = 10579;

    /// <summary>「開始了<訓練名>。」。</summary>
    private const uint TrainingStartedLogRow = 3928;

    /// <summary>不能訓練的系統訊息：任務中、訓練中、目前無法、已達當天上限。</summary>
    private static readonly uint[] TrainingFailureLogRows = [3924, 3926, 3952, 3953];

    /// <summary>一鍵這一輪的狀態（只存數值、字串與旗標）。</summary>
    private sealed class OneKeyRun(SquadronHome home, string cityName, uint officer, string officerName, uint board, string boardName)
    {
        public readonly SquadronHome Home = home;
        public readonly string CityName = cityName;
        public readonly uint Officer = officer;
        public readonly string OfficerName = officerName;
        public readonly uint Board = board;
        public readonly string BoardName = boardName;

        public bool TravelSent;
        public bool TravelDone;
        public bool TravelLegacy;
        public bool SeenBusy;
        public DateTime TravelAt;

        public bool DoorInteracted;
        public bool DoorZoning;
        public DateTime DoorAt;
        public DateTime? DoorYesnoSince;

        public string DeploySummary = string.Empty;
        public DateTime? CloseWaitSince;

        public bool TrainSent;
        public DateTime TrainAt;
        public int YesnoSetups;
        public YesAlreadyIpc.BotherPause? YesnoPause;
        public bool YesnoSeen;
        public bool TrainConfirmPressed;
        public DateTime ConfirmAt;
        public SquadronTrainingMessage TrainOutcome;
        public string TrainMessage = string.Empty;
        public string LastSystemMessage = string.Empty;
    }

    private string? oneKeyBlockedReason = "尚未檢查";

    // 遊戲資料錨點（啟用時讀）。
    private readonly List<string> trainingNames = [];
    private string comprehensiveName = string.Empty;
    private List<string> trainingConfirmFragments = [];
    private List<string> trainingStartedFragments = [];
    private readonly List<string> trainingFailures = [];

    private bool InBarracksOf(OneKeyRun o) =>
        Svc.ClientState.TerritoryType == o.Home.Barracks && !Zoning();

    private static bool Zoning() =>
        Svc.Condition[ConditionFlag.BetweenAreas] || Svc.Condition[ConditionFlag.BetweenAreas51] || Svc.Objects.LocalPlayer == null;

    #region 遊戲資料

    private void LoadOneKeyAnchors()
    {
        trainingNames.Clear();
        try
        {
            var sheet = Svc.Data.GetExcelSheet<GcArmyTraining>();
            for (uint row = 1; row <= SquadronOneKey.ButtonCount; row++)
                trainingNames.Add(sheet.GetRowOrDefault(row)?.Name.ExtractText().Trim() ?? string.Empty);
        }
        catch (Exception ex)
        {
            trainingNames.Clear();
            Svc.Log.Information($"[{InternalName}] 讀不到訓練表：{ex.Message}");
        }

        comprehensiveName = trainingNames.Count == SquadronOneKey.ButtonCount
            ? trainingNames[SquadronOneKey.ComprehensiveButton]
            : string.Empty;
        trainingConfirmFragments = AddonPrompt.GetFragments(TrainingConfirmAddonRow);
        trainingStartedFragments = LogFragments(TrainingStartedLogRow);
        trainingFailures.Clear();
        foreach (var row in TrainingFailureLogRows)
        {
            var text = string.Concat(LogFragments(row));
            if (text.Length > 0) trainingFailures.Add(text);
        }

        Svc.Log.Information(
            $"[{InternalName}] 一鍵遊戲資料：訓練 {string.Join("、", trainingNames)}（綜合訓練＝「{comprehensiveName}」）、" +
            $"訓練確認框片段 {string.Join("…", trainingConfirmFragments)}、開始訊息片段 {trainingStartedFragments.Count} 段、失敗訊息 {trainingFailures.Count} 則。");
    }

    private bool TrainingAnchorsReady =>
        trainingNames.Count == SquadronOneKey.ButtonCount && !trainingNames.Exists(string.IsNullOrEmpty) &&
        trainingConfirmFragments.Count == 2 && trainingStartedFragments.Count == 2;

    private static string TerritoryName(uint territory) =>
        Svc.Data.GetExcelSheet<TerritoryType>().GetRowOrDefault(territory)?.PlaceName.ValueNullable?.Name.ExtractText() is { Length: > 0 } name
            ? name
            : $"區域 {territory}";

    #endregion

    #region 開始

    /// <returns><c>null</c>＝可以開始；否則是一句理由。只在框架執行緒呼叫。</returns>
    private string? OneKeyBlockedReason()
    {
        if (!Config.OneKeyEnabled) return "要先勾選啟用「本角色一鍵」。";
        if (GetStartBlockedReason(false) is { } basic) return basic;
        if (AutoDeployBlockedReason(false) is { } deploy) return deploy;
        if (!Config.OneKeyStopAtTrainingList && !TrainingAnchorsReady)
            return "讀不到訓練名稱或訓練確認框的文字樣板，無法核對訓練窗。";
        if (!SquadronOneKey.TryGetHome(Svc.PlayerState.GrandCompany.RowId, out _)) return "這隻角色沒有加入國防聯軍。";
        return null;
    }

    private void TryStartOneKey()
    {
        if (OneKeyBlockedReason() is { } reason)
        {
            lastSummary = $"沒有開始：{reason}";
            Svc.Chat.PrintError($"[TC Toolbox] 國防聯軍分隊：{reason}");
            return;
        }

        SquadronOneKey.TryGetHome(Svc.PlayerState.GrandCompany.RowId, out var home);
        var (officer, board) = (0u, 0u);
        foreach (var (territory, o, b) in Barracks)
        {
            if (territory == home.Barracks) (officer, board) = (o, b);
        }

        var one = new OneKeyRun(home, TerritoryName(home.CityTerritory), officer, NpcName(officer), board, EObjDisplayName(board));
        var doorName = EObjDisplayName(home.DoorObject);
        if (doorName.Length == 0 || one.OfficerName.Length == 0 || one.BoardName.Length == 0 ||
            officerMenuText.Length == 0 || trainingMenuText.Length == 0)
        {
            lastSummary = "沒有開始：讀不到遊戲資料裡的名稱或選項文字。";
            Svc.Chat.PrintError($"[TC Toolbox] 國防聯軍分隊：{lastSummary}");
            return;
        }

        var r = new Run
        {
            Goal = Goal.OneKey, Territory = home.CityTerritory, BaseId = home.DoorObject, Kind = ObjectKind.EventObj,
            TargetName = doorName, PlaceLabel = one.CityName, LeasesOn = false, StopAtGoal = false,
            Auto = new AutoRun(diagTables), One = one,
        };

        run = r;
        talkLease.Reset();
        menuLease.Reset();
        confirmLease.Reset();

        queue.Enqueue($"傳送到{one.CityName}", () => TravelStep(r), 150_000);
        queue.Enqueue($"走到{doorName}旁", () => WalkStepResult(r), 30_000);
        queue.Enqueue("進入軍營", () => DoorStep(r), 45_000);
        queue.Enqueue($"準備去找{one.OfficerName}", () => EnterBarracksLeg(r, officer, ObjectKind.EventNpc, one.OfficerName,
            officerMenuText, ExpeditionAddon, "分隊任務清單", false, false), 5_000);
        queue.Enqueue($"走到{one.OfficerName}旁", () => WalkStep(r), 30_000);
        queue.Enqueue($"和{one.OfficerName}互動", () => InteractStep(r), 5_000);
        queue.Enqueue("等分隊任務清單打開", () => WaitStep(r), 30_000);
        queue.Enqueue("選今天的任務", () => PickStep(r), 60_000);
        queue.Enqueue("按任務開始", () => DeployStep(r), 10_000);
        queue.Enqueue("確認派遣", () => ConfirmStep(r), 10_000);
        queue.Enqueue("等開始訊息", () => StartedStep(r), 15_000);
        queue.Enqueue("等分隊任務清單收起", () => ExpeditionClosedStep(r), 15_000);
        var stopAtList = Config.OneKeyStopAtTrainingList;
        queue.Enqueue($"準備去{one.BoardName}", () => EnterBarracksLeg(r, board, ObjectKind.EventObj, one.BoardName,
            trainingMenuText, TrainingAddon, "訓練清單", true, stopAtList), 5_000);
        queue.Enqueue($"走到{one.BoardName}旁", () => WalkStep(r), 30_000);
        queue.Enqueue($"和{one.BoardName}互動", () => InteractStep(r), 5_000);
        queue.Enqueue("等訓練清單打開", () => WaitStep(r), 30_000);
        if (!stopAtList)
        {
            queue.Enqueue("選綜合訓練", () => TrainPickStep(r), 10_000);
            queue.Enqueue("確認綜合訓練", () => TrainConfirmStep(r), 10_000);
            queue.Enqueue("等訓練開始訊息", () => TrainStartedStep(r), 15_000);
        }

        Svc.Log.Information(
            $"[{InternalName}] 一鍵｜開始：國防聯軍 {home.GrandCompany}，傳送面板目的地 {home.FavoriteId}（{one.CityName}）→ {doorName} → " +
            $"軍營（區域 {home.Barracks}）→ {one.OfficerName} 派遣 → {one.BoardName} {(stopAtList ? "打開訓練清單" : "綜合訓練")}。");
    }

    #endregion

    #region 傳送與進門

    private bool? TravelStep(Run r)
    {
        var o = r.One!;
        var now = DateTime.UtcNow;

        if (!o.TravelSent)
        {
            if (InBarracksOf(o))
            {
                o.TravelDone = true;
                Svc.Log.Information($"[{InternalName}] 一鍵｜已經在軍營裡，略過傳送與進門。");
                return true;
            }

            if (Svc.ClientState.TerritoryType == o.Home.CityTerritory && TryGetTarget(r, out _, out _, out var near, out _) && near <= DoorNearMeters)
            {
                o.TravelDone = true;
                Svc.Log.Information($"[{InternalName}] 一鍵｜已經在{r.TargetName}附近（{near:F1} 米），不傳送、直接走過去。");
                return true;
            }

            if (!LifestreamTravel.TryTeleportToFavorite(o.Home.FavoriteId, 0, out var result, out var legacy))
                return Stop("叫不到 Lifestream 的傳送面板（沒有安裝、沒有載入，或版本太舊）");

            o.TravelLegacy = legacy;
            var destination = $"{o.CityName}的城內傳送點 {o.Home.FavoriteId}";
            switch (result)
            {
                case 0:
                    o.TravelSent = true;
                    o.TravelAt = now;
                    Svc.Log.Information($"[{InternalName}] 一鍵｜請 Lifestream 傳送到我的最愛 {o.Home.FavoriteId}（{o.CityName}）：已排入{(legacy ? "（舊版端點，只知道有排入）" : string.Empty)}。");
                    return false;
                case 1:
                    o.TravelDone = true;
                    Svc.Log.Information($"[{InternalName}] 一鍵｜Lifestream 說已經在目的地，直接走向入口。");
                    return true;
                default:
                    return Stop(legacy
                        ? "Lifestream（舊版）沒有排入傳送：目的地可能不在傳送面板的我的最愛、它正在忙，或角色現在不能動；更新 Lifestream 可以看到確切原因"
                        : $"Lifestream 沒有排入傳送：{SquadronOneKey.DescribeQueueResult(result, destination)}");
            }
        }

        bool? busy = LifestreamTravel.TryGetIsBusy(out var b) ? b : null;
        if (busy == true) o.SeenBusy = true;

        var frame = new SquadronTravelFrame(busy, Zoning(), Svc.ClientState.TerritoryType, o.SeenBusy, (long)(now - o.TravelAt).TotalMilliseconds);
        var decision = SquadronOneKey.DecideTravel(frame, o.Home.CityTerritory, o.CityName);
        switch (decision.Kind)
        {
            case SquadronTravelKind.Arrived:
                o.TravelDone = true;
                Svc.Log.Information($"[{InternalName}] 一鍵｜Lifestream 傳送完成，已在{o.CityName}（{(now - o.TravelAt).TotalSeconds:F0} 秒）。");
                return true;
            case SquadronTravelKind.Stop:
                return Stop(decision.Reason);
            default:
                return false;
        }
    }

    /// <summary><see cref="WalkStep"/> 的結果；已在軍營裡（之前就在、或使用者自己進去了）就略過。</summary>
    private bool? WalkStepResult(Run r) => InBarracksOf(r.One!) ? true : WalkStep(r);

    /// <summary>和入口互動一次，之後等進入軍營；確認框交給 YesAlready，太久沒人按、或沒反應就停。</summary>
    private bool? DoorStep(Run r)
    {
        var o = r.One!;
        var now = DateTime.UtcNow;

        if (InBarracksOf(o))
        {
            Svc.Log.Information($"[{InternalName}] 一鍵｜已進入軍營（區域 {o.Home.Barracks}）。");
            return true;
        }

        if (Zoning())
        {
            o.DoorZoning = true;
            return false;
        }

        if (o.DoorZoning) return Stop($"進門後到了別的地方（區域 {Svc.ClientState.TerritoryType}），不是軍營");

        if (!o.DoorInteracted)
        {
            if (!TryGetTarget(r, out var player, out var target, out var distance, out var problem)) return Stop(problem);

            var inRange = IsWithinGameInteractRange(player, target);
            if (inRange == false || (inRange == null && distance > FallbackInteractDistance))
                return Stop($"離{r.TargetName}太遠（{distance:F1} 米）");
            if (UiHelper.IsAnyAddonReady(UiHelper.SelectYesnoAddonName)) return Stop("和入口互動前已經有確認框開著");

            var targetSystem = TargetSystem.Instance();
            if (targetSystem == null) return Stop("讀不到目標系統");

            Svc.Targets.Target = target;
            targetSystem->InteractWithObject((GameObject*)target.Address, false);
            o.DoorInteracted = true;
            o.DoorAt = now;
            Svc.Log.Information($"[{InternalName}] 一鍵｜已和{r.TargetName}互動（{distance:F1} 米），進門確認框交給 YesAlready 的規則。");
            return false;
        }

        var yesno = UiHelper.FindReadyAddon(UiHelper.SelectYesnoAddonName);
        if (yesno != null)
        {
            o.DoorYesnoSince ??= now;
            if (now - o.DoorYesnoSince.Value > DoorYesnoUnanswered)
                return Stop($"進軍營的確認框「{UiHelper.GetSelectYesnoText(yesno).ReplaceLineEndings(" ")}」{DoorYesnoUnanswered.TotalSeconds:F0} 秒沒有人按" +
                            "（這一步交給你的 YesAlready 規則）；請自己按「是」，進去後再按一次一鍵");
            return false;
        }

        o.DoorYesnoSince = null;
        if (now - o.DoorAt > DoorNoResponse && !Svc.Condition[ConditionFlag.OccupiedInQuestEvent] && !Svc.Condition[ConditionFlag.OccupiedInEvent])
            return Stop($"和{r.TargetName}互動後 {DoorNoResponse.TotalSeconds:F0} 秒沒有出現確認框，也沒有進入軍營");

        return false;
    }

    /// <summary>停手時收回我們請 Lifestream 做的那一趟（它還在跑才收）。</summary>
    private void AbortOurTravel()
    {
        if (!LifestreamTravel.TryGetIsBusy(out var busy) || !busy) return;

        var sent = LifestreamTravel.TryAbort();
        Svc.Log.Information($"[{InternalName}] 一鍵｜停手時 Lifestream 還在傳送，已請它中止（{(sent ? "已送出" : "送不到")}）。");
    }

    #endregion

    #region 軍營裡

    /// <summary>換到軍營裡的下一個目標（軍曹或會議板），從這裡起請 YesAlready 讓開、鎖住 TextAdvance。</summary>
    private bool? EnterBarracksLeg(Run r, uint baseId, ObjectKind kind, string name, string menuText, string goalAddon, string goalLabel,
                                   bool allowBlankSpeaker, bool stopAtGoal)
    {
        var o = r.One!;
        if (!InBarracksOf(o)) return Stop("不在軍營裡");

        r.Territory = o.Home.Barracks;
        r.PlaceLabel = "軍營";
        r.BaseId = baseId;
        r.Kind = kind;
        r.TargetName = name;
        r.MenuText = menuText;
        r.GoalAddon = goalAddon;
        r.GoalLabel = goalLabel;
        r.AllowBlankSpeaker = allowBlankSpeaker;
        r.StopAtGoal = stopAtGoal;
        r.NavIssued = false;
        r.Closest = float.MaxValue;
        r.Menu = SquadronMenuState.NotPressed;
        r.TalkPages = 0;
        r.LastActionAt = DateTime.UtcNow;
        r.LeasesOn = true;
        Svc.Log.Information($"[{InternalName}] 一鍵｜下一站：{name}（選項「{menuText}」→ {goalAddon}）。");
        return true;
    }

    /// <summary>按了派遣「是」之後遊戲會自己收起任務清單；收起了才去會議板。</summary>
    private bool? ExpeditionClosedStep(Run r)
    {
        var o = r.One!;
        var now = DateTime.UtcNow;
        o.CloseWaitSince ??= now;

        if (UnexpectedWindow(true) is { } window) return Stop(window);
        if (!UiHelper.IsAnyAddonReady(ExpeditionAddon) && !UiHelper.IsAnyAddonReady(UiHelper.SelectYesnoAddonName)) return true;

        if (now - o.CloseWaitSince.Value > ExpeditionCloseWait)
            return Stop($"{o.DeploySummary}但分隊任務清單 {ExpeditionCloseWait.TotalSeconds:F0} 秒沒有收起；訓練請關掉清單後用「帶我到會議板」自己做");

        return false;
    }

    /// <summary>AtkValues[2..8]（七顆訓練鈕的文字）。<paramref name="addon"/> 必須是本幀取得、已判過 ready、且 <see cref="TryReadCells"/> 剛確認過 AtkValues 的窗。</summary>
    private static string?[] ReadTrainingLabels(AtkUnitBase* addon, int count)
    {
        var labels = new string?[SquadronOneKey.ButtonCount];
        var values = addon->AtkValues;
        for (var i = 0; i < labels.Length; i++)
        {
            var index = SquadronOneKey.FirstLabelCell + i;
            if (index >= count) continue;

            var v = values[index];
            if (v.Type is not (ValueType.String or ValueType.String8 or ValueType.ManagedString) || v.String.Value == null) continue;

            labels[i] = MemoryHelper.ReadSeStringNullTerminated((nint)v.String.Value).TextValue;
        }

        return labels;
    }

    private bool? TrainPickStep(Run r)
    {
        var o = r.One!;
        if (!InBarracksOf(o)) return Stop("已經離開軍營");

        if (!o.TrainSent)
        {
            if (UnexpectedWindow(false) is { } window) return Stop(window);

            var addon = UiHelper.FindReadyAddon(TrainingAddon);
            if (addon == null) return Stop("訓練清單被關掉了");
            if (!TryReadCells(addon, out var count, out var cellProblem)) return Stop($"讀不到訓練窗：{cellProblem}");

            var labels = ReadTrainingLabels(addon, count);
            if (!SquadronOneKey.CheckTrainingList(new ReadOnlySpan<SquadronCell>(diagCells, 0, count), labels, trainingNames, out var mode, out var problem))
                return Stop($"{o.DeploySummary}訓練這一步不動手（模式 {mode}）：{problem}；訓練請自己選（之後可用「帶我到會議板並打開訓練清單」）");

            // 綜合訓練會叫出確認框，YesAlready（含 Alt 強制鍵）要先確定讓開。
            if (!LeasesExclusive) return Stop(LeaseDetail);
            if (!UiHelper.TryFireCallback(addon, true, 12, SquadronOneKey.ComprehensiveButton)) return false;

            o.TrainSent = true;
            o.TrainAt = DateTime.UtcNow;
            Svc.Log.Information(
                $"[{InternalName}] 一鍵｜訓練窗模式 0、七顆鈕依序「{string.Join("、", labels)}」與表相符，送出 (12, {SquadronOneKey.ComprehensiveButton})：{comprehensiveName}（這一輪只送這一次）。");
            return false;
        }

        if (UnexpectedWindow(true) is { } other) return Stop(other);
        if (UiHelper.IsAnyAddonReady(UiHelper.SelectYesnoAddonName)) return true;

        if (DateTime.UtcNow - o.TrainAt > TrainYesnoWait)
            return Stop($"按了{comprehensiveName}，{TrainYesnoWait.TotalSeconds:F0} 秒內沒有出現確認框" +
                        (o.LastSystemMessage.Length > 0 ? $"（系統訊息：{o.LastSystemMessage}）" : string.Empty));

        return false;
    }

    private bool? TrainConfirmStep(Run r)
    {
        var o = r.One!;
        if (!InBarracksOf(o)) return Stop("已經離開軍營");
        if (UnexpectedWindow(true) is { } window) return Stop(window);
        if (o.YesnoSetups > 1) return Stop($"按了{comprehensiveName}後出現了不只一個確認框，不按");

        var yesno = UiHelper.FindReadyAddon(UiHelper.SelectYesnoAddonName);
        if (yesno == null) return o.YesnoSeen ? Stop("確認框收掉了（可能你自己按了是或否）") : false;

        o.YesnoSeen = true;
        if (o.YesnoPause is not { } pause || pause == YesAlreadyIpc.BotherPause.Failed)
            return Stop($"確認框出現的那一刻確認不了 YesAlready 已經讓開（{o.YesnoPause?.ToString() ?? "沒看到這扇確認框的 PreSetup"}），不按");
        if (!confirmLease.IsExclusive) return Stop(LeaseDetail);

        var text = UiHelper.GetSelectYesnoText(yesno);
        if (text.Length == 0 || AddonPrompt.LooksMidUpdate(text)) return false;

        if (!SquadronOneKey.TrainingConfirmMatches(text, trainingConfirmFragments, comprehensiveName, out var why))
            return Stop($"確認框對不上（{why}），不按：「{text.ReplaceLineEndings(" ")}」");

        if (!UiHelper.TryFireCallback(yesno, true, 0)) return false;

        o.TrainConfirmPressed = true;
        o.ConfirmAt = DateTime.UtcNow;
        Svc.Log.Information($"[{InternalName}] 一鍵｜確認框「{text.ReplaceLineEndings(" ")}」相符（YesAlready：{pause}），已按「是」。");
        return true;
    }

    private bool? TrainStartedStep(Run r)
    {
        var o = r.One!;
        switch (o.TrainOutcome)
        {
            case SquadronTrainingMessage.Started:
                EndRun($"{o.DeploySummary}已開始{comprehensiveName}。", true, true);
                return true;
            case SquadronTrainingMessage.Failed:
                return Stop($"按了「是」，遊戲回「{o.TrainMessage}」");
        }

        if (DateTime.UtcNow - o.ConfirmAt > TrainStartedWait)
            return Stop($"按了「是」，但 {TrainStartedWait.TotalSeconds:F0} 秒內沒看到「開始了{comprehensiveName}。」訊息；請看一下訓練有沒有開始");

        return false;
    }

    /// <summary>訓練確認框的 PreSetup：同派遣確認框，這一刻請 YesAlready 讓開並記下結果；確認不了就不按。</summary>
    private void OnTrainingYesnoPreSetup(OneKeyRun o)
    {
        try
        {
            o.YesnoSetups++;
            confirmLease.RenewNow();
            o.YesnoPause = confirmLease.Last;
            Svc.Log.Information($"[{InternalName}] 一鍵｜訓練確認框 PreSetup（第 {o.YesnoSetups} 扇）：請 YesAlready 暫停「SelectYesno」→ {confirmLease.Last}。");
        }
        catch (Exception ex)
        {
            o.YesnoPause = YesAlreadyIpc.BotherPause.Failed;
            Svc.Log.Information($"[{InternalName}] 一鍵｜訓練確認框 PreSetup 時請 YesAlready 讓開失敗（{ex.Message}），這扇不按。");
        }
    }

    private void OnTrainingChat(OneKeyRun o, XivChatType type, string text)
    {
        if (!o.TrainConfirmPressed)
        {
            if ((XivChatType)((int)type & 0x7F) is XivChatType.SystemMessage or XivChatType.SystemError or XivChatType.ErrorMessage)
                o.LastSystemMessage = text;
            return;
        }

        var kind = SquadronOneKey.ClassifyTrainingMessage(text, trainingStartedFragments, comprehensiveName, trainingFailures);
        if (kind == SquadronTrainingMessage.Other) return;

        o.TrainOutcome = kind;
        o.TrainMessage = text;
    }

    #endregion

    #region 畫面

    private const string OneKeyTooltip =
        "依序做完這隻角色今天的分隊：\n" +
        "1. 用 Lifestream 傳送面板的我的最愛傳送到軍營入口那一站（含你設的自訂落點；已在入口附近或軍營裡就略過）。\n" +
        "2. 走到軍營入口互動；進門確認框由你的 YesAlready 規則按，這裡不按。\n" +
        "3. 和「替我選今天的任務並派遣」一樣選任務並派遣。\n" +
        "4. 走到會議板打開訓練清單，按綜合訓練，確認框整句是「開始綜合訓練。」才按「是」。\n" +
        "任何一步比不中、逾時或出現沒預期的視窗都會停下來，原因寫在下方「上次結果」與記錄裡。";

    private void DrawOneKeyButton() =>
        DrawButtonCore(Goal.OneKey, "本角色一鍵：傳送→進軍營→派遣→綜合訓練###squadronOneKey", OneKeyTooltip, oneKeyBlockedReason);

    private void DrawOneKeySettings()
    {
        var enabled = Config.OneKeyEnabled;
        if (ImGui.Checkbox("啟用「本角色一鍵：傳送→進軍營→派遣→綜合訓練」按鈕##squadronOneKeyToggle", ref enabled))
        {
            Config.OneKeyEnabled = enabled;
            Plugin.Instance.Config.Save();
        }

        ImGui.TextDisabled("需要 Lifestream，傳送面板的我的最愛裡要有軍營入口那一站（黑渦團＝冒險者行會前）。");
        if (!enabled) return;

        using var indent = ImRaii.PushIndent();
        var stopAtList = Config.OneKeyStopAtTrainingList;
        if (ImGui.Checkbox("訓練那一步只打開訓練清單，讓我自己選##squadronOneKeyStopAtList", ref stopAtList))
        {
            Config.OneKeyStopAtTrainingList = stopAtList;
            Plugin.Instance.Config.Save();
        }
    }

    #endregion
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using TCToolbox.Core;
using GameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace TCToolbox.Modules;

/// <summary>
/// 合建交納的<b>連續推進</b>：一個階段交完，自動「推進工程進展」再交下一個階段，直到所有階段交完。
/// </summary>
/// <remarks>
/// 🔴 合建設備選單的第 3 項是「中止製作」：一律照選項文字找索引、標題也要相符，比對不到就什麼都不按、整趟停下。
/// 🔴 唯一會按的窗是合建設備選單（SelectString）。推進沒有確認框；推進過場裡的「是／否」是跳過過場的提示，不碰。
/// 📌 最後的「完成」與回收成品在 AutoFCWSDeliver.Finish.cs，另有開關。⚠️ 只在框架執行緒上跑；原生指標每一步重新取，不跨幀保存。
/// </remarks>
public sealed unsafe partial class AutoFCWSDeliver
{
    private AutoFCWSDeliverConfig Config => Plugin.Instance.Config.FcwsDeliver;

    /// <summary>單趟最多推進幾個階段（保險絲；合建專案實際只有個位數階段）。</summary>
    private const int MaxPhasesPerRun = 12;

    /// <summary>推進的過場結束後，先等合建設備選單自己出現這麼久，才重新和設備互動（毫秒）。</summary>
    private const int ReopenGraceMs = 2_500;

    /// <summary>重新互動之後等選單的上限，過了再試一次（毫秒）。</summary>
    private const int ReinteractRetryMs = 6_000;

    /// <summary>素材交完之後，等合建視窗自己收起、合建設備選單出現的上限（毫秒）。</summary>
    private const int PhaseEndMenuWaitMs = 6_000;

    /// <summary>合建設備的 EObjName 列：同名的其他列一起當作合建設備。</summary>
    private const uint StationSeedEObj = 2011588;

    /// <summary>合建設備的事件文字表：選單標題、各選項，以及中止與回收的確認文字都在這裡。</summary>
    private const string StationTextSheet = "custom/001/CmnDefCompanyManufactory_00150";

    /// <summary>YesAlready 的「SelectString」自動選項：連續推進期間每 3 秒續暫停 10 秒，結束後它自己恢復。</summary>
    private const string SelectStringPauseKey = "AutoFCWSDeliver-YesAlreadyPause";

    private const string LoopTooltip =
        "一個階段的素材交完後，合建設備的選單會自己跳出來：自動選「推進工程進展」，\n"
        + "推進的過場結束後再選「交納素材」交下一個階段，直到所有階段交完為止。\n"
        + "選項一律照文字比對、選單標題也要相符，比對不到就停下來（絕不會去按「中止製作」）。\n"
        + "過場裡的「要跳過這段過場動畫嗎？」不會自動按（交給你或 TextAdvance）；\n"
        + "連續推進期間會請 YesAlready 暫停選單的自動選項，結束後十秒內自己恢復。\n"
        + "最後的「完成」與回收成品要另外勾「完成並回收成品」才會自動做；缺料、沒有推進權限、按「停止」或全艦隊急停都會停下。";

    // 選單錨點（全部從遊戲資料讀）。任何一個讀不到，連續推進整個不動作。
    private string anchorContribute = string.Empty;
    private string anchorAdvance = string.Empty;
    private string anchorNotebook = string.Empty;
    private string anchorMenuTitle = string.Empty;
    private string anchorAbort = string.Empty;
    private string anchorSkipPrompt = string.Empty;

    /// <summary>合建設備的 EObj 編號；第一次需要重新互動時才建。</summary>
    private HashSet<uint>? stationDataIds;

    private int phasesAdvanced;

    /// <summary>推進之後選單又是「推進工程進展」的次數（第二次就判定推進沒生效）。</summary>
    private int advanceRepeats;

    /// <summary>這個階段開始交納時的 <see cref="deliveredCount"/>，算「本階段交了幾項」用。</summary>
    private int phaseStartDelivered;

    /// <summary>目前這個連續推進等待步驟的起點（每個步驟開頭重設）。</summary>
    private DateTime? loopWaitStartedAt;

    private DateTime? cutsceneEndedAt;
    private DateTime? reinteractAt;
    private int reinteractAttempts;
    private bool sawAdvanceCutscene;
    private bool reopenLogged;
    private bool cutscenePromptLogged;

    private YesAlreadyIpc.BotherPause selectStringPause;
    private DateTime? selectStringPausedAt;
    private bool selectStringPauseLogged;

    /// <summary>合建視窗關著時，浮動控制列停在哪裡（最後一次貼著合建視窗的位置）。</summary>
    private Vector2? floatingOverlayPos;

    private bool LoopAnchorsReady =>
        anchorContribute.Length > 0 && anchorAdvance.Length > 0 && anchorNotebook.Length > 0 &&
        anchorMenuTitle.Length > 0 && anchorAbort.Length > 0 && anchorSkipPrompt.Length > 0;

    /// <summary>這一趟要不要在階段交完時接著推進。每次判斷都讀設定：中途取消勾選＝交完這個階段就停。</summary>
    private bool LoopEnabled => Config.ContinuousPhases && LoopAnchorsReady;

    /// <summary>交納（含連續推進）正在跑嗎。只在主執行緒讀。</summary>
    public bool IsBusy => queue.IsBusy;

    /// <summary>目前步驟名；沒在跑是空字串。</summary>
    public string CurrentStepName => queue.CurrentStep ?? string.Empty;

    public override bool HasConfigUI => true;

    public override void DrawConfig()
    {
        var loop = Config.ContinuousPhases;
        if (ImGui.Checkbox("連續推進：交完一個階段自動「推進工程進展」，直到所有階段交完##fcwsLoopConfig", ref loop))
        {
            Config.ContinuousPhases = loop;
            Plugin.Instance.Config.Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(LoopTooltip);

        DrawFinishCheckbox("最後完成並回收成品：所有階段交完後，會自動按「完成」並回收成品##fcwsFinishConfig");

        ImGui.TextDisabled("在合建素材交納視窗上按「開始」後生效；沒勾「最後完成並回收成品」時，最後的「完成」與回收成品留給你手動。");
    }

    /// <summary>「最後完成並回收成品」勾選框；沒勾「連續推進」時灰掉，說明框畫在停用區段之外（才會全亮）。</summary>
    private void DrawFinishCheckbox(string label)
    {
        using (Dalamud.Interface.Utility.Raii.ImRaii.Disabled(!Config.ContinuousPhases))
        {
            var finish = Config.CompleteAndCollect;
            if (ImGui.Checkbox(label, ref finish))
            {
                Config.CompleteAndCollect = finish;
                Plugin.Instance.Config.Save();
            }
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(FinishTooltip);
    }

    /// <summary>全艦隊急停用：立刻清掉佇列，之後不再送任何事件。</summary>
    public void StopForEmergency()
    {
        if (!queue.IsBusy) return;

        Svc.Log.Information(
            $"[AutoFCWSDeliver] 全艦隊急停：中止於「{queue.CurrentStep}」" +
            $"（本趟推進 {phasesAdvanced} 個階段、交納 {deliveredCount} 項）。");
        queue.Abort();
    }

    private void ResolveLoopAnchors()
    {
        anchorContribute = FcAuthorityName(64);
        anchorAdvance = FcAuthorityName(65);
        anchorNotebook = AddonText(6622);
        anchorSkipPrompt = AddonText(281);
        anchorMenuTitle = StationText(0);
        anchorAbort = StationText(3);
        stationDataIds = null;
        ResolveFinishAnchors();
    }

    private static string FcAuthorityName(uint row) =>
        Svc.Data.GetExcelSheet<FCAuthority>().GetRowOrDefault(row)?.Name.ExtractText().Trim() ?? string.Empty;

    private static string AddonText(uint row) =>
        Svc.Data.GetExcelSheet<Addon>().GetRowOrDefault(row)?.Text.ExtractText().Trim() ?? string.Empty;

    /// <summary>合建設備事件文字表第 <paramref name="row"/> 列的文字（第 1 欄）；讀不到回空字串。</summary>
    private static string StationText(uint row)
    {
        try
        {
            var sheet = Svc.Data.GetExcelSheet<RawRow>(null, StationTextSheet);
            return sheet.GetRowOrDefault(row)?.ReadStringColumn(1).ExtractText().Trim() ?? string.Empty;
        }
        catch (Exception ex)
        {
            Svc.Log.Information($"[AutoFCWSDeliver] 讀不到合建設備的文字表 {StationTextSheet}：{ex.Message}");
            return string.Empty;
        }
    }

    private void ResetLoopState()
    {
        phasesAdvanced = 0;
        advanceRepeats = 0;
        phaseStartDelivered = 0;
        loopWaitStartedAt = null;
        cutsceneEndedAt = null;
        reinteractAt = null;
        reinteractAttempts = 0;
        sawAdvanceCutscene = false;
        reopenLogged = false;
        cutscenePromptLogged = false;
        selectStringPause = YesAlreadyIpc.BotherPause.Failed;
        selectStringPausedAt = null;
        selectStringPauseLogged = false;
        Throttle.Reset(SelectStringPauseKey);
        ResetFinishState();
    }

    /// <summary>開始時勾著「連續推進」才呼叫：寫一行錨點，讀不到就說出來。</summary>
    private void LogLoopStart()
    {
        if (!LoopAnchorsReady)
        {
            Svc.Chat.PrintError("[TC Toolbox] 讀不到合建設備選單的文字，這一趟只交目前這個階段，不會自動推進。");
            Svc.Log.Information(
                $"[AutoFCWSDeliver] 連續推進的錨點讀不齊（標題「{anchorMenuTitle}」交納「{anchorContribute}」推進「{anchorAdvance}」" +
                $"筆記「{anchorNotebook}」中止「{anchorAbort}」跳過過場「{anchorSkipPrompt}」），這一趟不推進。");
            return;
        }

        Svc.Log.Information(
            $"[AutoFCWSDeliver] 連續推進開始：選單標題「{anchorMenuTitle}」、交納「{anchorContribute}」、推進「{anchorAdvance}」、" +
            $"必含「{anchorNotebook}」、永不選「{anchorAbort}」、不碰標題含「{anchorSkipPrompt}」的選單。");

        if (Config.CompleteAndCollect) LogFinishStart();
    }

    /// <summary>連續推進期間每 3 秒請 YesAlready 把「SelectString」自動選項再暫停 10 秒。</summary>
    /// <remarks>合建設備選單一出現（PostSetup）YesAlready 就會照使用者的清單動手，所以暫停必須在選單出現之前就生效。</remarks>
    private void KeepSelectStringExclusive()
    {
        if (!queue.IsBusy || !LoopEnabled) return;
        if (!Throttle.Pass(SelectStringPauseKey, 3_000)) return;

        var result = YesAlreadyIpc.PauseBother("SelectString", 10_000);
        if (result != selectStringPause || !selectStringPauseLogged)
        {
            selectStringPauseLogged = true;
            Svc.Log.Information(
                $"[AutoFCWSDeliver] 連續推進：請 YesAlready 暫停「SelectString」自動選項 → {result}（每 3 秒續 10 秒，結束後自己恢復）。");
        }

        selectStringPause = result;
        selectStringPausedAt = DateTime.UtcNow;
    }

    /// <summary>按合建設備選單之前：確認沒有別的外掛會同時按同一扇。</summary>
    /// <remarks>
    /// TextAdvance 只按標題含「要跳過這段過場動畫嗎？」的選單，碰不到這一扇。碰得到的三個：
    /// YesAlready 的選單清單（已暫停）、DailyRoutines 的合建模組（照位置按第 0 項，載入就不按）、
    /// Questionable（跑任務時會照任務設定回答同樣標題「要做什麼？」的選單，它在跑就不按）。
    /// </remarks>
    private bool TryConfirmExclusive(out string reason)
    {
        if (PluginPresence.IsLoaded("DailyRoutines") != false)
        {
            reason = "DailyRoutines 已載入（或問不到）：它的合建模組會照位置按合建設備選單，為了不兩邊同時按，連續推進不按。";
            return false;
        }

        var questionableBusy = QuestionableIpc.TryIsRunning(out var questRunning)
            ? questRunning
            : PluginPresence.IsLoaded("Questionable") != false;
        if (questionableBusy)
        {
            reason = "Questionable 正在跑（或問不到）：它可能照任務設定回答同標題的選單，為了不兩邊同時按，連續推進不按。";
            return false;
        }

        var fresh = selectStringPausedAt is { } at && (DateTime.UtcNow - at).TotalMilliseconds < 8_000;
        if (fresh && selectStringPause is YesAlreadyIpc.BotherPause.Paused
                or YesAlreadyIpc.BotherPause.NotActive or YesAlreadyIpc.BotherPause.NotLoaded)
        {
            reason = string.Empty;
            return true;
        }

        reason = $"沒辦法確認 YesAlready 的「SelectString」自動選項已經暫停（{selectStringPause}），為了不兩邊同時按，連續推進不按。";
        return false;
    }

    /// <summary>合建設備的選單現在開著嗎（在公會工坊內、標題與選項都對得上）。</summary>
    private bool TryReadStationMenu(out AtkUnitBase* addon, out List<string> entries, out StationMenuKind kind, out int index)
    {
        addon = null;
        entries = [];
        kind = StationMenuKind.NotStationMenu;
        index = -1;

        if (GetWorkshopTerritory() == null) return false;

        var menu = UiHelper.FindReadyAddon("SelectString");
        if (menu == null) return false;

        var list = UiHelper.GetSelectStringEntries(menu);
        var prompt = UiHelper.ReadSelectStringPrompt(menu);
        if (UiHelper.LooksMidUpdate(list) || AddonPrompt.LooksMidUpdate(prompt)) return false;

        var found = StationMenuLogic.Classify(
            list, prompt, anchorMenuTitle, anchorSkipPrompt, anchorNotebook, anchorContribute, anchorAdvance, anchorAbort, out var at);
        if (found is StationMenuKind.NotStationMenu or StationMenuKind.SkipCutscenePrompt) return false;

        addon = menu;
        entries = list;
        kind = found;
        index = at;
        return true;
    }

    private bool IsStationMenuOpen() => TryReadStationMenu(out _, out _, out _, out _);

    /// <summary>現在開著的 SelectString 長什麼樣子（標題＋選項），停下來時寫進訊息，看得出為什麼沒認出來。</summary>
    private static string DescribeOpenSelectString()
    {
        var menu = UiHelper.FindReadyAddon("SelectString");
        if (menu == null) return "（沒有開著的選單）";

        return $"（開著的選單：標題「{UiHelper.ReadSelectStringPrompt(menu)}」、選項 {string.Join("、", UiHelper.GetSelectStringEntries(menu))}）";
    }

    /// <summary>照合建設備選單的狀態決定下一步；回傳值就是步驟的回傳值。</summary>
    /// <param name="afterAdvance">剛推進完（預期看到「交納素材」）；否則是階段剛交完（預期看到「推進工程進展」）。</param>
    private bool? ActOnStationMenu(AtkUnitBase* addon, List<string> entries, StationMenuKind kind, int index, bool afterAdvance)
    {
        switch (kind)
        {
            case StationMenuKind.Advance:
            {
                if (phasesAdvanced >= MaxPhasesPerRun)
                    return StopLoop($"已經推進 {phasesAdvanced} 個階段，達到單趟上限，先停下來。", error: true);

                if (afterAdvance && advanceRepeats >= 1)
                    return StopLoop("推進之後選單還是「推進工程進展」，推進似乎沒有生效。", error: true);

                if (!TryConfirmExclusive(out var why)) return StopLoop(why, error: true);

                // 守衛擋下＝這一扇剛按過，下一幀再判。
                if (!UiHelper.TryFireCallback(addon, true, index)) return false;

                if (afterAdvance) advanceRepeats++;
                phasesAdvanced++;
                Svc.Log.Information(
                    $"[AutoFCWSDeliver] 連續推進：第 {phasesAdvanced} 個階段交完" +
                    $"（本階段交納 {deliveredCount - phaseStartDelivered} 項），選擇「{entries[index]}」。");
                EnqueueAdvanceSteps();
                return true;
            }

            case StationMenuKind.Contribute when afterAdvance:
            {
                if (!TryConfirmExclusive(out var why)) return StopLoop(why, error: true);
                if (!UiHelper.TryFireCallback(addon, true, index)) return false;

                advanceRepeats = 0;
                Svc.Log.Information($"[AutoFCWSDeliver] 連續推進：選擇「{entries[index]}」，開始交下一個階段。");
                EnqueueOpenMaterials();
                return true;
            }

            case StationMenuKind.Contribute:
                return StopLoop("合建視窗關了，但合建設備選單上還是「交納素材」，這個階段可能還沒交完，先停下來。", error: true);

            default:
                if (TryActOnFinishMenu(addon, entries, out var finished)) return finished;

                return StopLoop(
                    $"合建設備選單上沒有「{anchorAdvance}」（選單：{string.Join("、", entries)}）。" +
                    "如果第一項是「完成…」，代表所有階段都交完了，請手動完成並回收成品；否則可能是沒有公會的推進權限。",
                    error: false);
        }
    }

    /// <summary>合建視窗上沒有可交的素材了（連續推進才會走到）：交完了就等合建設備選單，缺料就停。</summary>
    private bool? OnPhaseListEmpty(AtkUnitBase* addon)
    {
        var rows = ParseRows(addon);
        var completed = rows.Count(r => r.Completed);
        var deliverable = rows.Count(r => !r.Completed && r.Owned >= r.Required);

        if (StationMenuLogic.ClassifyPhase(rows.Count, completed, deliverable) == PhaseMaterialState.Short)
        {
            return StopLoop(
                $"這個階段還有 {rows.Count - completed} 種素材數量不夠，無法推進（可以按「缺料加進製作清單」）。",
                error: false);
        }

        EnqueuePhaseEnd();
        return true;
    }

    private void EnqueuePhaseEnd()
    {
        loopWaitStartedAt = null;
        queue.Enqueue("等待合建設備選單", WaitStationMenuAfterPhase, PhaseEndMenuWaitMs + 6_000);
    }

    /// <summary>階段交完：遊戲會自己收起合建視窗、打開合建設備選單。</summary>
    private bool? WaitStationMenuAfterPhase()
    {
        loopWaitStartedAt ??= DateTime.UtcNow;

        if (TryReadStationMenu(out var addon, out var entries, out var kind, out var index))
            return ActOnStationMenu(addon, entries, kind, index, afterAdvance: false);

        if ((DateTime.UtcNow - loopWaitStartedAt.Value).TotalMilliseconds < PhaseEndMenuWaitMs) return false;

        return StopLoop(
            UiHelper.IsReady(UiHelper.GetAddon("SubmarinePartsMenu"))
                ? "素材都交完了，但合建視窗沒有自己收起來，先停下來。"
                : $"合建視窗收起後沒有認出合建設備的選單，先停下來{DescribeOpenSelectString()}。",
            error: true);
    }

    private void EnqueueAdvanceSteps()
    {
        loopWaitStartedAt = null;
        cutsceneEndedAt = null;
        reinteractAt = null;
        reinteractAttempts = 0;
        sawAdvanceCutscene = false;
        reopenLogged = false;
        cutscenePromptLogged = false;

        queue.Enqueue("等待推進過場結束", WaitAdvanceCutscene, 90_000);
        queue.Enqueue("等待合建設備選單重新出現", WaitStationMenuAfterAdvance, 25_000);
    }

    /// <summary>
    /// 推進沒有確認框（合建設備的文字表裡只有中止與回收兩組「是／否」），選了就直接播推進的過場。
    /// 過場裡冒出來的「是／否」是跳過過場的提示：交給使用者或 TextAdvance，本模組只記一行、不按。
    /// </summary>
    private bool? WaitAdvanceCutscene()
    {
        if (GetWorkshopTerritory() == null)
            return StopLoop("推進途中離開了公會工坊。", error: true);

        loopWaitStartedAt ??= DateTime.UtcNow;
        NoteSelectStringInCutscene();

        var inScene = Svc.Condition[ConditionFlag.OccupiedInCutSceneEvent] ||
                      Svc.Condition[ConditionFlag.Occupied33] ||
                      Svc.Condition[ConditionFlag.WatchingCutscene] ||
                      Svc.Condition[ConditionFlag.BetweenAreas];
        if (inScene)
        {
            sawAdvanceCutscene = true;
            return false;
        }

        // 選了推進到過場開始大約 0.4 秒；等不到過場也照樣往下，由選單判斷推進有沒有成功。
        if (!sawAdvanceCutscene && (DateTime.UtcNow - loopWaitStartedAt.Value).TotalMilliseconds < 5_000)
            return false;

        cutsceneEndedAt = DateTime.UtcNow;
        return true;
    }

    /// <summary>推進過場裡出現的 SelectString 只記一次標題與選項（不按），之後才有資料收緊判準。</summary>
    private void NoteSelectStringInCutscene()
    {
        if (cutscenePromptLogged) return;

        var menu = UiHelper.FindReadyAddon("SelectString");
        if (menu == null) return;

        var entries = UiHelper.GetSelectStringEntries(menu);
        var prompt = UiHelper.ReadSelectStringPrompt(menu);
        if (UiHelper.LooksMidUpdate(entries) || AddonPrompt.LooksMidUpdate(prompt)) return;

        cutscenePromptLogged = true;
        var kind = StationMenuLogic.Classify(
            entries, prompt, anchorMenuTitle, anchorSkipPrompt, anchorNotebook, anchorContribute, anchorAdvance, anchorAbort, out _);
        Svc.Log.Information(
            $"[AutoFCWSDeliver] 連續推進：推進的過場裡出現選單「{prompt}」（選項：{string.Join("、", entries)}；判定 {kind}），本模組不按。");
    }

    /// <summary>推進完：先等選單自己出現，等不到才重新和合建設備互動（最多兩次）。</summary>
    private bool? WaitStationMenuAfterAdvance()
    {
        cutsceneEndedAt ??= DateTime.UtcNow;
        var sinceEnd = (DateTime.UtcNow - cutsceneEndedAt.Value).TotalMilliseconds;

        if (TryReadStationMenu(out var addon, out var entries, out var kind, out var index))
        {
            if (!reopenLogged)
            {
                reopenLogged = true;
                Svc.Log.Information(reinteractAttempts == 0
                    ? $"[AutoFCWSDeliver] 連續推進：推進完成，合建設備選單自己重新出現（過場結束後 {sinceEnd:F0} 毫秒）。"
                    : $"[AutoFCWSDeliver] 連續推進：推進完成，重新互動後合建設備選單出現（過場結束後 {sinceEnd:F0} 毫秒）。");
            }

            return ActOnStationMenu(addon, entries, kind, index, afterAdvance: true);
        }

        // 合建視窗已經開了（例如使用者自己點了「交納素材」）：直接接著交。
        if (UiHelper.IsReady(UiHelper.GetAddon("SubmarinePartsMenu")))
        {
            Svc.Log.Information("[AutoFCWSDeliver] 連續推進：推進完成後合建視窗已經開著，直接開始交下一個階段。");
            BeginPhaseDelivery();
            return true;
        }

        if (sinceEnd < ReopenGraceMs) return false;
        if (reinteractAt is { } at && (DateTime.UtcNow - at).TotalMilliseconds < ReinteractRetryMs) return false;
        if (reinteractAttempts >= 2)
            return StopLoop($"重新和合建設備互動兩次都沒有認出選單{DescribeOpenSelectString()}。", error: true);

        if (!Throttle.Pass("AutoFCWSDeliver-LoopGate", 250)) return false;
        if (AutomationGate.TryGetBusyReason(out var busy))
        {
            if (Throttle.Pass("AutoFCWSDeliver-LoopBusy", 3_000))
                Svc.Log.Information($"[AutoFCWSDeliver] 連續推進：等角色空下來再和合建設備互動（{busy}）。");
            return false;
        }

        if (!TryInteractWithStation(out var detail))
            return StopLoop(detail, error: true);

        reinteractAttempts++;
        reinteractAt = DateTime.UtcNow;
        Svc.Log.Information(
            $"[AutoFCWSDeliver] 連續推進：選單沒有自己重新出現，第 {reinteractAttempts} 次重新和合建設備互動（{detail}）。");
        return false;
    }

    private void EnqueueOpenMaterials()
    {
        queue.Enqueue("等待合建視窗", () =>
        {
            if (!UiHelper.IsReady(UiHelper.GetAddon("SubmarinePartsMenu"))) return false;

            BeginPhaseDelivery();
            return true;
        }, 10_000);
    }

    /// <summary>新的階段從頭交：每個階段自己的零進展保險絲與項數上限。</summary>
    private void BeginPhaseDelivery()
    {
        phaseStartDelivered = deliveredCount;
        menuGoneSince = null;
        lastParseFingerprint = null;
        noProgressRounds = 0;
        enqueuedItems = 0;
        EnqueueNextItem();
    }

    /// <summary>找最近的合建設備，遊戲說互動得到才互動。</summary>
    /// <remarks>
    /// 🔴 <c>InteractWithObject</c> 的回傳值沒有語意，不拿來判斷；成敗看選單有沒有打開。
    /// 距離與高低差問遊戲自己的 <c>CheckInteractRange</c>，問不到就不互動。
    /// </remarks>
    /// <param name="detail">成功時是距離；失敗時是可以直接給使用者看的原因。</param>
    private bool TryInteractWithStation(out string detail)
    {
        var player = Svc.Objects.LocalPlayer;
        if (player == null)
        {
            detail = "讀不到角色。";
            return false;
        }

        var ids = GetStationDataIds();
        IGameObject? station = null;
        var distance = float.MaxValue;
        foreach (var obj in Svc.Objects)
        {
            if (obj.ObjectKind != ObjectKind.EventObj || !ids.Contains(obj.BaseId)) continue;

            var d = Vector3.Distance(player.Position, obj.Position);
            if (d >= distance) continue;

            distance = d;
            station = obj;
        }

        if (station == null)
        {
            detail = "附近找不到合建設備，請站到合建設備旁邊再開始。";
            return false;
        }

        var inRange = IsWithinGameInteractRange(player, station);
        if (inRange == null)
        {
            detail = "問不到遊戲的互動距離判定，不重新互動。";
            return false;
        }

        if (inRange == false)
        {
            detail = $"離合建設備太遠（{distance:F1} 米），請走近一點再開始。";
            return false;
        }

        var targetSystem = TargetSystem.Instance();
        if (targetSystem == null)
        {
            detail = "讀不到目標系統。";
            return false;
        }

        Svc.Targets.Target = station;
        targetSystem->InteractWithObject((GameObject*)station.Address, false);
        detail = $"距離 {distance:F1} 米";
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

    /// <summary>與 <see cref="StationSeedEObj"/> 同名的所有 EObj 編號（工房大小不同，設備的編號也不同）。</summary>
    private HashSet<uint> GetStationDataIds()
    {
        if (stationDataIds != null) return stationDataIds;

        var sheet = Svc.Data.GetExcelSheet<EObjName>();
        var name = sheet.GetRowOrDefault(StationSeedEObj)?.Singular.ExtractText() ?? string.Empty;
        var ids = new HashSet<uint>();
        if (name.Length > 0)
        {
            foreach (var row in sheet)
            {
                if (row.Singular.ExtractText() == name) ids.Add(row.RowId);
            }
        }

        stationDataIds = ids;
        Svc.Log.Information($"[AutoFCWSDeliver] 連續推進：合建設備「{name}」的 EObj 編號＝{string.Join("、", ids)}。");
        return ids;
    }

    /// <summary>連續推進停下來：聊天欄一句、記錄一行，佇列整條收掉。</summary>
    /// <returns><paramref name="error"/> 時回 <see langword="null"/>（中止）；否則是正常收工，回 <see cref="TaskQueue.Finish"/>。</returns>
    private bool? StopLoop(string reason, bool error)
    {
        var summary = $"（本趟推進 {phasesAdvanced} 個階段、交納 {deliveredCount} 項）";
        if (error) Svc.Chat.PrintError($"[TC Toolbox] 連續推進已停止：{reason}{summary}");
        else Svc.Chat.Print($"[TC Toolbox] 連續推進結束：{reason}{summary}");

        Svc.Log.Information($"[AutoFCWSDeliver] 連續推進停止：{reason}{summary}");
        return error ? null : queue.Finish();
    }

    /// <summary>SelectString 的分類。</summary>
    internal enum StationMenuKind
    {
        /// <summary>不是合建設備的選單（或錨點讀不到）。零值刻意給這個：判不出來就什麼都不按。</summary>
        NotStationMenu = 0,

        Contribute,

        Advance,

        /// <summary>是合建設備的選單，但沒有唯一的「交納素材」或「推進工程進展」（完成、沒權限、沒專案…）。</summary>
        Other,

        /// <summary>跳過過場的「是／否」提示（TextAdvance 會自動按的那一扇）。本模組永遠不按。</summary>
        SkipCutscenePrompt,
    }

    /// <summary>這一階段合建視窗上素材的狀態。</summary>
    internal enum PhaseMaterialState
    {
        Unknown = 0,
        HasDeliverable,
        AllDelivered,
        Short,
    }

    /// <summary>連續推進的判定，全部是只吃字串與數字的純函式（不碰遊戲），可以離線測。</summary>
    internal static class StationMenuLogic
    {
        /// <summary>分類 SelectString，並給出要按的那一項的索引。索引一律照文字找，不照位置。</summary>
        /// <param name="prompt">選單標題；必須等於 <paramref name="title"/> 才算合建設備選單。</param>
        /// <param name="index">要按的項；分類不是 Contribute／Advance 時為 -1。</param>
        internal static StationMenuKind Classify(
            IReadOnlyList<string> entries, string prompt, string title, string skipPrompt,
            string notebook, string contribute, string advance, string abort, out int index)
        {
            index = -1;
            if (title.Length == 0 || skipPrompt.Length == 0 || notebook.Length == 0 ||
                contribute.Length == 0 || advance.Length == 0 || abort.Length == 0)
                return StationMenuKind.NotStationMenu;

            var head = prompt.Trim();
            if (head.Contains(skipPrompt, StringComparison.Ordinal)) return StationMenuKind.SkipCutscenePrompt;
            if (head != title) return StationMenuKind.NotStationMenu;

            var hasNotebook = false;
            var ambiguous = false;
            var contributeAt = -1;
            var advanceAt = -1;
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i].Trim();
                if (entry == notebook)
                {
                    hasNotebook = true;
                }
                else if (IsOption(entry, contribute))
                {
                    ambiguous |= contributeAt >= 0;
                    contributeAt = i;
                }
                else if (IsOption(entry, advance))
                {
                    ambiguous |= advanceAt >= 0;
                    advanceAt = i;
                }
            }

            if (!hasNotebook) return StationMenuKind.NotStationMenu;

            // 同一種出現兩次，或兩種同時出現：看不懂的選單一律不按。
            if (ambiguous || (contributeAt >= 0 && advanceAt >= 0)) return StationMenuKind.Other;

            var chosen = advanceAt >= 0 ? advanceAt : contributeAt;
            if (chosen < 0) return StationMenuKind.Other;

            // 前綴比對本來就不會選到「中止製作」；這裡再擋一次，讓這條保證不依賴錨點的內容。
            if (entries[chosen].Trim() == abort) return StationMenuKind.Other;

            index = chosen;
            return advanceAt >= 0 ? StationMenuKind.Advance : StationMenuKind.Contribute;
        }

        /// <summary>選項是不是「名稱」本身，或「名稱（品質：#/#）」這種帶括號後綴的形狀。</summary>
        internal static bool IsOption(string entry, string name) =>
            name.Length > 0 && entry.StartsWith(name, StringComparison.Ordinal) &&
            (entry.Length == name.Length || entry[name.Length] is '（' or '(');

        /// <summary>由合建視窗的列數判斷這一階段的素材狀態。</summary>
        internal static PhaseMaterialState ClassifyPhase(int rows, int completed, int deliverable) =>
            rows <= 0 ? PhaseMaterialState.Unknown
            : deliverable > 0 ? PhaseMaterialState.HasDeliverable
            : completed >= rows ? PhaseMaterialState.AllDelivered
            : PhaseMaterialState.Short;
    }
}

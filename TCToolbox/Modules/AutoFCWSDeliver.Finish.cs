using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel;
using Lumina.Text.ReadOnly;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 合建的<b>最後完成並回收</b>：所有階段交完、選單第一項變成「完成〔成品〕（品質：#/100）」時，
/// 選「完成」→ 等過場 → 重新互動選「領取道具」→ 回收確認框按「是」。
/// </summary>
/// <remarks>
/// 🔴 選項與確認框一律照合建設備事件文字表的完整樣板比對（成品名、品質數字是唯一的空格），比對不到就停下，
/// 不照位置按；回收確認框的成品名還要和「完成」那一項的一致。按確認框之前先請 YesAlready 暫停 SelectYesno。
/// ⚠️ 只在框架執行緒上跑；原生指標每一步重新取，不跨幀保存。
/// </remarks>
public sealed unsafe partial class AutoFCWSDeliver
{
    /// <summary>完成的過場最長等多久（毫秒）；實機約 4～9 秒。</summary>
    private const int FinishCutsceneLimitMs = 60_000;

    /// <summary>選了「完成」之後等不到過場開始，最多等這麼久就往下看選單（毫秒）。</summary>
    private const int FinishNoCutsceneGraceMs = 5_000;

    /// <summary>選了「領取道具」之後等回收確認框的上限（毫秒）。</summary>
    private const int CollectConfirmWaitMs = 5_000;

    /// <summary>按下回收確認框之後，等合建設備選單換成「沒有領取道具」的上限（毫秒）。</summary>
    private const int CollectDoneWaitMs = 6_000;

    /// <summary>按下確認框之後，選單仍是「領取道具」或「完成」要過這麼久才算數（毫秒）。</summary>
    private const int CollectSettleMs = 1_500;

    private const int StationRowCollect = 4;
    private const int StationRowComplete = 6;
    private const int StationRowCollectConfirm = 12;

    private const string FinishTooltip =
        "要同時勾「連續推進」才會生效；預設關閉。\n"
        + "所有階段都交完、合建設備選單第一項變成「完成〔成品〕（品質：#/100）」時：\n"
        + "自動選這一項（照完整文字比對，絕不會按到「中止製作」），等完成的過場結束後，\n"
        + "重新和合建設備互動、選「領取道具」，在「確定要從公會合建裝置中回收〔成品〕嗎？」按「是」。\n"
        + "按確認框之前會先請 YesAlready 暫停確認框的自動回答，確認不了就不按、停下來交給你。\n"
        + "任何一步文字對不上或等太久都會停下，並在 log 記一行看到了什麼。\n"
        + "回收後合建設備選單會留著，下一件要做什麼由你從「公會合建筆記」選。";

    private IReadOnlyList<string> templateComplete = [];
    private IReadOnlyList<string> templateCollectConfirm = [];
    private string anchorCollect = string.Empty;

    private readonly YesAlreadyBotherLease collectConfirmLease = new("SelectYesno");
    private YesAlreadyIpc.BotherPause? loggedCollectPause;

    /// <summary>「完成」那一項上的成品名；回收確認框要對到同一個名字。</summary>
    private string finishItemName = string.Empty;

    private DateTime? finishWaitStartedAt;
    private bool finishSawCutscene;
    private string lastUnknownFinishPrompt = string.Empty;

    private bool FinishAnchorsReady =>
        FinishLogic.IsUsableTemplate(templateComplete) && FinishLogic.IsUsableTemplate(templateCollectConfirm) &&
        anchorCollect.Length > 0;

    /// <summary>這一趟要不要自動完成並回收。每次判斷都讀設定：中途取消勾選＝下一步之前停下。</summary>
    private bool FinishEnabled => Config.CompleteAndCollect && LoopEnabled && FinishAnchorsReady;

    private void ResolveFinishAnchors()
    {
        templateComplete = StationTemplate(StationRowComplete);
        templateCollectConfirm = StationTemplate(StationRowCollectConfirm);
        anchorCollect = StationText(StationRowCollect);
    }

    /// <summary>合建設備事件文字表某一列拆成樣板片段；讀不到回空清單。</summary>
    private static IReadOnlyList<string> StationTemplate(uint row)
    {
        try
        {
            var sheet = Svc.Data.GetExcelSheet<RawRow>(null, StationTextSheet);
            var text = sheet.GetRowOrDefault(row)?.ReadStringColumn(1);
            return text is { } seString ? FinishLogic.TemplateFromSeString(seString) : [];
        }
        catch (Exception ex)
        {
            Svc.Log.Information($"[AutoFCWSDeliver] 讀不到合建設備的文字表 {StationTextSheet} 第 {row} 列：{ex.Message}");
            return [];
        }
    }

    private void ResetFinishState()
    {
        finishItemName = string.Empty;
        finishWaitStartedAt = null;
        finishSawCutscene = false;
        lastUnknownFinishPrompt = string.Empty;
        loggedCollectPause = null;
        collectConfirmLease.Reset();
    }

    /// <summary>開始時勾著「最後完成並回收」才呼叫：寫一行判準，讀不到就說出來。</summary>
    private void LogFinishStart()
    {
        var describe =
            $"完成樣板「{FinishLogic.DescribeTemplate(templateComplete)}」、領取「{anchorCollect}」、" +
            $"回收確認框樣板「{FinishLogic.DescribeTemplate(templateCollectConfirm)}」";
        if (!FinishAnchorsReady)
        {
            Svc.Chat.PrintError("[TC Toolbox] 讀不到合建設備「完成／領取道具」的文字，這一趟不會自動完成並回收。");
            Svc.Log.Information($"[AutoFCWSDeliver] 最後完成並回收的判準讀不齊（{describe}），這一趟不自動完成。");
            return;
        }

        Svc.Log.Information($"[AutoFCWSDeliver] 最後完成並回收開啟：{describe}。");
    }

    /// <summary>選單不是交納也不是推進時（連續推進的 default 分支）：比對得到「完成」才接手。</summary>
    /// <returns><see langword="false"/>＝沒接手，呼叫端照舊停下；<see langword="true"/> 時 <paramref name="result"/> 就是步驟的回傳值。</returns>
    private bool TryActOnFinishMenu(AtkUnitBase* addon, List<string> entries, out bool? result)
    {
        result = null;
        if (!FinishEnabled) return false;

        var kind = FinishLogic.ClassifyMenu(
            entries, anchorNotebook, anchorAbort, templateComplete, anchorCollect, out var index, out var itemName);
        if (kind != FinishMenuKind.Complete)
        {
            Svc.Log.Information(
                $"[AutoFCWSDeliver] 最後完成：合建設備選單沒有比對到「完成」那一項（判定 {kind}；選單：{string.Join("、", entries)}；" +
                $"樣板「{FinishLogic.DescribeTemplate(templateComplete)}」），不自動完成。");
            return false;
        }

        if (!TryConfirmExclusive(out var why))
        {
            result = StopLoop(why, error: true);
            return true;
        }

        result = false;
        if (!UiHelper.TryFireCallback(addon, true, index)) return true;

        finishItemName = itemName;
        Svc.Log.Information(
            $"[AutoFCWSDeliver] 最後完成：第 {phasesAdvanced + 1} 個階段交完（本階段交納 {deliveredCount - phaseStartDelivered} 項），" +
            $"選擇「{entries[index]}」（成品「{itemName}」；選單：{string.Join("、", entries)}），等完成的過場。");
        EnqueueFinishSteps();
        result = true;
        return true;
    }

    private void EnqueueFinishSteps()
    {
        finishWaitStartedAt = null;
        finishSawCutscene = false;
        cutsceneEndedAt = null;
        reinteractAt = null;
        reinteractAttempts = 0;
        reopenLogged = false;
        cutscenePromptLogged = false;

        queue.Enqueue("等待完成的過場結束", WaitFinishCutscene, FinishCutsceneLimitMs + 15_000);
        queue.Enqueue("等待合建設備選單（領取道具）", WaitCollectMenu, 30_000);
    }

    private bool? WaitFinishCutscene()
    {
        if (GetWorkshopTerritory() == null) return StopLoop("完成途中離開了公會工坊。", error: true);

        finishWaitStartedAt ??= DateTime.UtcNow;
        var elapsed = (DateTime.UtcNow - finishWaitStartedAt.Value).TotalMilliseconds;
        NoteSelectStringInCutscene();

        var inScene = Svc.Condition[ConditionFlag.OccupiedInCutSceneEvent] ||
                      Svc.Condition[ConditionFlag.Occupied33] ||
                      Svc.Condition[ConditionFlag.WatchingCutscene] ||
                      Svc.Condition[ConditionFlag.BetweenAreas];
        if (inScene && !finishSawCutscene)
            Svc.Log.Information($"[AutoFCWSDeliver] 最後完成：完成的過場開始（選了「完成」後 {elapsed:F0} 毫秒）。");
        finishSawCutscene |= inScene;

        switch (FinishLogic.DecideCutscene(inScene, finishSawCutscene, elapsed))
        {
            case CutsceneWait.TooLong:
                return StopLoop($"完成的過場超過 {FinishCutsceneLimitMs / 1000} 秒還沒結束，先停下來；請手動回收成品。", error: true);
            case CutsceneWait.Ended:
                Svc.Log.Information($"[AutoFCWSDeliver] 最後完成：完成的過場結束（選了「完成」後 {elapsed:F0} 毫秒）。");
                cutsceneEndedAt = DateTime.UtcNow;
                return true;
            case CutsceneWait.NoCutscene:
                Svc.Log.Information(
                    $"[AutoFCWSDeliver] 最後完成：選了「完成」後 {FinishNoCutsceneGraceMs} 毫秒沒有看到過場，照樣往下看選單。");
                cutsceneEndedAt = DateTime.UtcNow;
                return true;
            default:
                return false;
        }
    }

    /// <summary>過場後先等選單自己出現，等不到才重新和合建設備互動（最多兩次）；認出「領取道具」就選它。</summary>
    private bool? WaitCollectMenu()
    {
        if (GetWorkshopTerritory() == null) return StopLoop("完成途中離開了公會工坊。", error: true);

        cutsceneEndedAt ??= DateTime.UtcNow;
        var sinceEnd = (DateTime.UtcNow - cutsceneEndedAt.Value).TotalMilliseconds;

        if (TryReadStationMenu(out var addon, out var entries, out var stationKind, out _))
        {
            if (!reopenLogged)
            {
                reopenLogged = true;
                Svc.Log.Information(
                    $"[AutoFCWSDeliver] 最後完成：合建設備選單出現（{(reinteractAttempts == 0 ? "自己出現" : $"第 {reinteractAttempts} 次重新互動後")}，" +
                    $"過場結束後 {sinceEnd:F0} 毫秒；判定 {stationKind}；選單：{string.Join("、", entries)}）。");
            }

            if (stationKind != StationMenuKind.Other)
                return StopLoop($"完成之後合建設備選單不是預期的樣子（選單：{string.Join("、", entries)}），先停下來。", error: true);

            var kind = FinishLogic.ClassifyMenu(
                entries, anchorNotebook, anchorAbort, templateComplete, anchorCollect, out var index, out _);
            switch (kind)
            {
                case FinishMenuKind.Collect:
                    return SelectCollect(addon, entries, index);
                case FinishMenuKind.Complete:
                    return StopLoop($"選了「完成」之後選單上還是「完成…」，完成似乎沒有生效（選單：{string.Join("、", entries)}）。", error: true);
                default:
                    return StopLoop(
                        $"完成之後合建設備選單上沒有「{anchorCollect}」（判定 {kind}；選單：{string.Join("、", entries)}），" +
                        "成品可能已經直接放進背包，或需要手動回收。", error: false);
            }
        }

        double? sinceInteract = reinteractAt is { } at ? (DateTime.UtcNow - at).TotalMilliseconds : null;
        switch (FinishLogic.DecideReopen(sinceEnd, sinceInteract, reinteractAttempts))
        {
            case ReopenAction.GiveUp:
                return StopLoop($"完成之後重新和合建設備互動兩次都沒有認出選單{DescribeOpenSelectString()}，請手動回收成品。", error: true);
            case ReopenAction.Interact:
                break;
            default:
                return false;
        }

        if (!Throttle.Pass("AutoFCWSDeliver-FinishGate", 250)) return false;
        if (AutomationGate.TryGetBusyReason(out var busy))
        {
            if (Throttle.Pass("AutoFCWSDeliver-FinishBusy", 3_000))
                Svc.Log.Information($"[AutoFCWSDeliver] 最後完成：等角色空下來再和合建設備互動（{busy}）{DescribeOpenSelectString()}。");
            return false;
        }

        if (!TryInteractWithStation(out var detail)) return StopLoop(detail, error: true);

        reinteractAttempts++;
        reinteractAt = DateTime.UtcNow;
        Svc.Log.Information(
            $"[AutoFCWSDeliver] 最後完成：選單沒有自己出現，第 {reinteractAttempts} 次重新和合建設備互動（{detail}）。");
        return false;
    }

    private bool? SelectCollect(AtkUnitBase* addon, List<string> entries, int index)
    {
        if (!FinishEnabled) return StopLoop("「最後完成並回收」已取消勾選，不選「領取道具」。", error: false);
        if (!TryConfirmExclusive(out var why)) return StopLoop(why, error: true);

        // 確認框一出現 YesAlready 就會照它的清單動手，所以暫停要在選「領取道具」之前生效。
        if (!RenewCollectConfirmPause())
        {
            return StopLoop(
                $"沒辦法確認 YesAlready 的確認框自動回答已經暫停（{collectConfirmLease.Last}），不選「{anchorCollect}」；請手動回收成品。",
                error: true);
        }

        if (!UiHelper.TryFireCallback(addon, true, index)) return false;

        finishWaitStartedAt = null;
        lastUnknownFinishPrompt = string.Empty;
        Svc.Log.Information(
            $"[AutoFCWSDeliver] 最後完成：選擇「{entries[index]}」，等回收確認框（預期「{FinishLogic.FillTemplate(templateCollectConfirm, finishItemName)}」）。");
        queue.Enqueue("等待回收確認框", WaitCollectConfirm, CollectConfirmWaitMs + 10_000);
        queue.Enqueue("等待回收完成", WaitCollectDone, CollectDoneWaitMs + 10_000);
        return true;
    }

    private bool RenewCollectConfirmPause()
    {
        var ok = collectConfirmLease.Renew();
        if (loggedCollectPause == collectConfirmLease.Last) return ok;

        loggedCollectPause = collectConfirmLease.Last;
        Svc.Log.Information(
            $"[AutoFCWSDeliver] 最後完成：請 YesAlready 暫停「SelectYesno」自動回答 → {collectConfirmLease.Last}" +
            $"（每 {YesAlreadyBotherLease.RenewAfterMs / 1000} 秒續 {YesAlreadyBotherLease.PauseMs / 1000} 秒，結束後它自己恢復）。");
        return ok;
    }

    /// <summary>回收確認框：成品名與「完成」那一項一致、YesAlready 已暫停，才按「是」。</summary>
    private bool? WaitCollectConfirm()
    {
        if (GetWorkshopTerritory() == null) return StopLoop("回收途中離開了公會工坊。", error: true);

        finishWaitStartedAt ??= DateTime.UtcNow;
        var elapsed = (DateTime.UtcNow - finishWaitStartedAt.Value).TotalMilliseconds;
        RenewCollectConfirmPause();

        var yesno = UiHelper.FindReadyAddon(UiHelper.SelectYesnoAddonName);
        var prompt = yesno == null ? string.Empty : ReadPrompt(yesno);
        var promptKind = yesno == null
            ? CollectPromptKind.None
            : FinishLogic.ClassifyPrompt(prompt, templateCollectConfirm, finishItemName);

        switch (FinishLogic.DecideConfirm(promptKind, collectConfirmLease.IsExclusive, IsCollectedMenuOpen(), elapsed))
        {
            case ConfirmAction.Press:
                if (!UiHelper.TryFireCallback(yesno, true, 0)) return false;

                finishWaitStartedAt = null;
                Svc.Log.Information(
                    $"[AutoFCWSDeliver] 最後完成：回收確認框按下「是」（出現後 {elapsed:F0} 毫秒內）：「{prompt}」。");
                return true;
            case ConfirmAction.NotExclusive:
                return StopLoop(
                    $"回收確認框出現了，但沒辦法確認 YesAlready 已經暫停（{collectConfirmLease.Last}），不按；確認框留給你：「{prompt}」。",
                    error: true);
            case ConfirmAction.OtherItem:
                return StopLoop(
                    $"回收確認框的成品和「完成」那一項對不上（預期「{finishItemName}」），不按：「{prompt}」。", error: true);
            case ConfirmAction.DoneByOther:
                Svc.Log.Information("[AutoFCWSDeliver] 最後完成：本模組按下之前回收確認框就不見了、選單已經沒有「領取道具」（由別人或手動按掉）。");
                finishWaitStartedAt = null;
                return true;
            case ConfirmAction.Timeout:
                return StopLoop(
                    $"選了「{anchorCollect}」之後 {CollectConfirmWaitMs} 毫秒沒有等到回收確認框" +
                    $"（SelectYesno：{UiHelper.DescribeAddonInstances(UiHelper.SelectYesnoAddonName)}" +
                    $"{(prompt.Length > 0 ? $"；最後看到「{prompt}」" : string.Empty)}），請手動回收成品。",
                    error: true);
        }

        if (promptKind == CollectPromptKind.Unknown && prompt != lastUnknownFinishPrompt)
        {
            lastUnknownFinishPrompt = prompt;
            Svc.Log.Information(
                $"[AutoFCWSDeliver] 最後完成：出現不認得的確認框，本模組不按：「{prompt}」" +
                $"（預期「{FinishLogic.FillTemplate(templateCollectConfirm, finishItemName)}」）。");
        }

        return false;
    }

    /// <summary>按下回收確認框之後：等選單換成沒有「領取道具」的樣子才算回收完成。</summary>
    private bool? WaitCollectDone()
    {
        if (GetWorkshopTerritory() == null) return StopLoop("回收途中離開了公會工坊。", error: true);

        finishWaitStartedAt ??= DateTime.UtcNow;
        var elapsed = (DateTime.UtcNow - finishWaitStartedAt.Value).TotalMilliseconds;

        var yesnoOpen = UiHelper.IsAnyAddonReady(UiHelper.SelectYesnoAddonName);
        FinishMenuKind? menu = null;
        var entries = new List<string>();
        if (TryReadStationMenu(out _, out entries, out var stationKind, out _))
        {
            menu = stationKind == StationMenuKind.Other
                ? FinishLogic.ClassifyMenu(entries, anchorNotebook, anchorAbort, templateComplete, anchorCollect, out _, out _)
                : FinishMenuKind.Ambiguous;
        }

        switch (FinishLogic.DecideDone(yesnoOpen, menu, elapsed))
        {
            case DoneAction.Done:
                Svc.Chat.Print(
                    $"[TC Toolbox] 合建完成並回收了「{finishItemName}」（本趟推進 {phasesAdvanced} 個階段、交納 {deliveredCount} 項）。");
                Svc.Log.Information(
                    $"[AutoFCWSDeliver] 最後完成：回收完成「{finishItemName}」（按下確認後 {elapsed:F0} 毫秒選單換成：{string.Join("、", entries)}）；" +
                    $"本趟推進 {phasesAdvanced} 個階段、交納 {deliveredCount} 項。");
                return true;
            case DoneAction.StillCollect:
                return StopLoop(
                    $"按下回收確認之後選單上還有「{anchorCollect}」（選單：{string.Join("、", entries)}），背包可能放不下，請手動處理。",
                    error: true);
            case DoneAction.Unexpected:
                return StopLoop($"按下回收確認之後選單不是預期的樣子（選單：{string.Join("、", entries)}），先停下來。", error: true);
            case DoneAction.Timeout:
                return StopLoop(
                    $"按下回收確認之後 {CollectDoneWaitMs} 毫秒沒有看到合建設備選單換成回收後的樣子" +
                    $"（確認框{(yesnoOpen ? "還開著" : "已關")}{DescribeOpenSelectString()}），沒辦法確認有沒有回收成功，請看一下背包。",
                    error: true);
            default:
                return false;
        }
    }

    private bool IsCollectedMenuOpen() =>
        TryReadStationMenu(out _, out var entries, out var kind, out _) && kind == StationMenuKind.Other &&
        FinishLogic.ClassifyMenu(entries, anchorNotebook, anchorAbort, templateComplete, anchorCollect, out _, out _) ==
        FinishMenuKind.Collected;

    /// <summary>合建設備選單在「最後完成」這段的分類。零值刻意給「不是」：判不出來就什麼都不按。</summary>
    internal enum FinishMenuKind
    {
        NotFinishMenu = 0,

        /// <summary>有唯一一項完整比對到「完成〔成品〕（品質：#/100）」。</summary>
        Complete,

        /// <summary>有唯一一項是「領取道具」。</summary>
        Collect,

        /// <summary>是合建設備選單，但「完成」「領取道具」「中止製作」都沒有（回收完的樣子）。</summary>
        Collected,

        /// <summary>同一種出現兩次、兩種同時出現、「完成」那一項含「中止製作」，或只剩「中止製作」：一律不按。</summary>
        Ambiguous,
    }

    internal enum CollectPromptKind
    {
        None = 0,
        Unreadable,
        Match,
        OtherItem,
        Unknown,
    }

    internal enum CutsceneWait
    {
        WaitForStart = 0,
        InScene,
        Ended,
        NoCutscene,
        TooLong,
    }

    internal enum ReopenAction
    {
        Wait = 0,
        Interact,
        GiveUp,
    }

    internal enum ConfirmAction
    {
        Wait = 0,
        Press,
        NotExclusive,
        OtherItem,
        DoneByOther,
        Timeout,
    }

    internal enum DoneAction
    {
        Wait = 0,
        Done,
        StillCollect,
        Unexpected,
        Timeout,
    }

    /// <summary>最後完成並回收的判定，全部是只吃字串與數字的純函式（不碰遊戲），可以離線測。</summary>
    internal static class FinishLogic
    {
        /// <summary>比對前兩邊都去掉換行（表裡是 NewLine 巨集、畫面上是字面換行，也有反過來的）與前後空白。</summary>
        internal static string Normalize(string text) => (text ?? string.Empty).ReplaceLineEndings(string.Empty).Trim();

        /// <summary>把表裡的字串拆成固定片段；相鄰兩段之間是一個巨集（成品名、數字…）留下的空格。</summary>
        internal static List<string> TemplateFromSeString(ReadOnlySeString text)
        {
            var literals = new List<string>();
            var current = new StringBuilder();
            foreach (var payload in text)
            {
                if (payload.Type == ReadOnlySePayloadType.Text)
                {
                    current.Append(Encoding.UTF8.GetString(payload.Body.Span));
                    continue;
                }

                literals.Add(current.ToString().ReplaceLineEndings(string.Empty));
                current.Clear();
            }

            literals.Add(current.ToString().ReplaceLineEndings(string.Empty));
            if (literals.Count > 0)
            {
                literals[0] = literals[0].TrimStart();
                literals[^1] = literals[^1].TrimEnd();
            }

            return literals;
        }

        /// <summary>頭尾都有固定文字、至少一個空格的樣板才能用（否則空格可以吞掉任何字串）。</summary>
        internal static bool IsUsableTemplate(IReadOnlyList<string>? literals) =>
            literals is { Count: >= 2 } && literals[0].Length > 0 && literals[^1].Length > 0;

        /// <summary>整串文字是否恰好是樣板（固定片段逐字相等、空格可為任意字）；命中時給出每個空格的內容。</summary>
        internal static bool TryMatchTemplate(string text, IReadOnlyList<string> literals, out List<string> holes)
        {
            holes = [];
            if (!IsUsableTemplate(literals)) return false;

            var escaped = new string[literals.Count];
            for (var i = 0; i < literals.Count; i++) escaped[i] = Regex.Escape(literals[i]);

            var match = Regex.Match(
                Normalize(text), "^" + string.Join("(.*?)", escaped) + "$",
                RegexOptions.CultureInvariant | RegexOptions.Singleline);
            if (!match.Success) return false;

            for (var g = 1; g < match.Groups.Count; g++) holes.Add(match.Groups[g].Value);
            return true;
        }

        /// <summary>樣板第一個空格填 <paramref name="first"/>、其餘填「#」，給 log 看的預期文字。</summary>
        internal static string FillTemplate(IReadOnlyList<string> literals, string first)
        {
            if (literals.Count == 0) return string.Empty;

            var sb = new StringBuilder(literals[0]);
            for (var i = 1; i < literals.Count; i++) sb.Append(i == 1 ? first : "#").Append(literals[i]);
            return sb.ToString();
        }

        internal static string DescribeTemplate(IReadOnlyList<string> literals) =>
            literals.Count == 0 ? "（讀不到）" : string.Join("〔〕", literals);

        /// <summary>分類合建設備選單（標題已由 <see cref="StationMenuLogic.Classify"/> 驗過），並給出要按的那一項。</summary>
        /// <param name="index">要按的項；分類不是 Complete／Collect 時為 -1。</param>
        /// <param name="itemName">「完成」那一項上的成品名；不是 Complete 時為空字串。</param>
        internal static FinishMenuKind ClassifyMenu(
            IReadOnlyList<string> entries, string notebook, string abort, IReadOnlyList<string> completeTemplate,
            string collect, out int index, out string itemName)
        {
            index = -1;
            itemName = string.Empty;
            if (notebook.Length == 0 || abort.Length == 0 || collect.Length == 0 || !IsUsableTemplate(completeTemplate))
                return FinishMenuKind.NotFinishMenu;

            var hasNotebook = false;
            var hasAbort = false;
            var duplicate = false;
            var completeAt = -1;
            var collectAt = -1;
            var name = string.Empty;
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = Normalize(entries[i]);
                if (entry == notebook)
                {
                    hasNotebook = true;
                }
                else if (entry == abort)
                {
                    hasAbort = true;
                }
                else if (entry == collect)
                {
                    duplicate |= collectAt >= 0;
                    collectAt = i;
                }
                else if (TryMatchTemplate(entry, completeTemplate, out var holes) && holes[0].Length > 0)
                {
                    duplicate |= completeAt >= 0;
                    completeAt = i;
                    name = holes[0];
                }
            }

            if (!hasNotebook) return FinishMenuKind.NotFinishMenu;
            if (duplicate || (completeAt >= 0 && collectAt >= 0)) return FinishMenuKind.Ambiguous;

            if (completeAt >= 0)
            {
                // 樣板本身就不會比對到「中止製作」；這裡再擋一次，讓這條保證不依賴樣板的內容。
                if (Normalize(entries[completeAt]).Contains(abort, StringComparison.Ordinal)) return FinishMenuKind.Ambiguous;

                index = completeAt;
                itemName = name;
                return FinishMenuKind.Complete;
            }

            if (collectAt >= 0)
            {
                index = collectAt;
                return FinishMenuKind.Collect;
            }

            // 還有「中止製作」＝專案還在，不是回收完的樣子。
            return hasAbort ? FinishMenuKind.Ambiguous : FinishMenuKind.Collected;
        }

        /// <summary>這扇確認框是不是「從合建裝置回收〔<paramref name="itemName"/>〕」的那一扇。</summary>
        internal static CollectPromptKind ClassifyPrompt(string prompt, IReadOnlyList<string> confirmTemplate, string itemName)
        {
            if (string.IsNullOrWhiteSpace(prompt) || AddonPrompt.LooksMidUpdate(prompt)) return CollectPromptKind.Unreadable;
            if (itemName.Length == 0 || !TryMatchTemplate(prompt, confirmTemplate, out var holes) || holes.Count != 1)
                return CollectPromptKind.Unknown;

            return holes[0] == itemName ? CollectPromptKind.Match : CollectPromptKind.OtherItem;
        }

        internal static CutsceneWait DecideCutscene(bool inScene, bool sawScene, double elapsedMs)
        {
            if (inScene) return elapsedMs >= FinishCutsceneLimitMs ? CutsceneWait.TooLong : CutsceneWait.InScene;
            if (sawScene) return CutsceneWait.Ended;
            return elapsedMs < FinishNoCutsceneGraceMs ? CutsceneWait.WaitForStart : CutsceneWait.NoCutscene;
        }

        /// <param name="sinceInteractMs">距上一次重新互動多久；還沒互動過是 <see langword="null"/>。</param>
        internal static ReopenAction DecideReopen(double sinceEndMs, double? sinceInteractMs, int attempts)
        {
            if (sinceEndMs < ReopenGraceMs) return ReopenAction.Wait;
            if (sinceInteractMs is { } since && since < ReinteractRetryMs) return ReopenAction.Wait;
            return attempts >= 2 ? ReopenAction.GiveUp : ReopenAction.Interact;
        }

        /// <param name="collectedMenuOpen">合建設備選單已經是回收完的樣子（確認框被別人按掉了）。</param>
        internal static ConfirmAction DecideConfirm(
            CollectPromptKind prompt, bool exclusive, bool collectedMenuOpen, double elapsedMs)
        {
            switch (prompt)
            {
                case CollectPromptKind.Match:
                    return exclusive ? ConfirmAction.Press : ConfirmAction.NotExclusive;
                case CollectPromptKind.OtherItem:
                    return ConfirmAction.OtherItem;
            }

            if (collectedMenuOpen) return ConfirmAction.DoneByOther;
            return elapsedMs >= CollectConfirmWaitMs ? ConfirmAction.Timeout : ConfirmAction.Wait;
        }

        /// <param name="menu">合建設備選單的分類；選單沒開是 <see langword="null"/>。</param>
        internal static DoneAction DecideDone(bool yesnoOpen, FinishMenuKind? menu, double elapsedMs)
        {
            if (!yesnoOpen && menu == FinishMenuKind.Collected) return DoneAction.Done;

            if (!yesnoOpen && menu is { } kind && elapsedMs >= CollectSettleMs)
            {
                if (kind == FinishMenuKind.Collect) return DoneAction.StillCollect;
                return DoneAction.Unexpected;
            }

            return elapsedMs >= CollectDoneWaitMs ? DoneAction.Timeout : DoneAction.Wait;
        }
    }
}

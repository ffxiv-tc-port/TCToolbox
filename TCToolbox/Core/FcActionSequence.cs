using System;
using System.Collections.Generic;
using System.Linq;

namespace TCToolbox.Core;

// 本檔只用 System：特效→狀態對照、庫存陣列解析、確認框比對、開關判斷、UI 步驟狀態機。
// 遊戲端的讀取與按鈕全部在 FcActionAutoUse 模組裡；這裡的東西離線就能整套重播。

/// <summary>公會特效（CompanyAction）與身上狀態（Status）的對照。</summary>
internal static class FcActionCatalog
{
    /// <summary>同一種特效相鄰兩級的列號差：第一級 1～17、II 31～47、III 61～77。</summary>
    public const uint TierOffset = 30;

    public const int TierCount = 3;

    // 台服 Status 表標 IsFcBuff 的只有 17 列，三級共用同一個狀態。
    // 1080／1078 那一族（沒有「公會特效：」前綴、IsFcBuff=false）是道具給的，不算。
    private static readonly Dictionary<uint, uint> StatusByFirstTier = new()
    {
        [1] = 365, [2] = 367, [3] = 368, [4] = 366, [5] = 413, [6] = 414,
        [7] = 353, [8] = 354, [9] = 355, [10] = 356, [11] = 357, [12] = 361,
        [13] = 360, [14] = 362, [15] = 2932, [16] = 364, [17] = 902,
    };

    /// <summary>台服沒有 III 的兩種（CompanyAction 67、75 不存在）。</summary>
    private static readonly HashSet<uint> NoThirdTier = [7, 15];

    public static IEnumerable<uint> FirstTierActions => StatusByFirstTier.Keys.OrderBy(x => x);

    /// <summary>第 <paramref name="tier"/> 級（0 起算）的列號；這一級不存在回 0。</summary>
    public static uint ActionOf(uint firstTier, int tier) =>
        tier is < 0 or >= TierCount || !StatusByFirstTier.ContainsKey(firstTier) || (tier == 2 && NoThirdTier.Contains(firstTier))
            ? 0
            : firstTier + TierOffset * (uint)tier;

    public static bool IsKnownAction(uint actionId) => TryGetStatus(actionId, out _);

    /// <summary>回 <see langword="false"/>＝不認得這個特效，對應的狀態判不出來。</summary>
    public static bool TryGetStatus(uint actionId, out uint statusId)
    {
        statusId = 0;
        if (actionId == 0) return false;

        var tier = (int)((actionId - 1) / TierOffset);
        var first = actionId - TierOffset * (uint)tier;
        return ActionOf(first, tier) == actionId && StatusByFirstTier.TryGetValue(first, out statusId);
    }
}

/// <summary>讀「公會特效」視窗那個 NumberArray 的結果。</summary>
/// <param name="Valid"><see langword="false"/>＝形狀不對（可能讀錯陣列），不能拿來按。</param>
internal readonly record struct FcStockRead(bool Valid, IReadOnlyList<uint> Actions, string Problem)
{
    public static FcStockRead Invalid(string problem) => new(false, Array.Empty<uint>(), problem);

    public string Describe() => Valid ? $"[{string.Join(",", Actions)}]" : $"無效（{Problem}）";
}

internal static class FcActionStock
{
    /// <summary>筆數在 <c>[11]</c>，第 i 筆特效編號在 <c>[12+3i]</c>（照 AutoRetainer 上游的讀法）。</summary>
    public const int CountIndex = 11;

    public const int FirstIndex = 12;

    public const int Stride = 3;

    /// <summary>庫存筆數的合理上限；超過就當成讀錯陣列。</summary>
    public const int MaxCount = 64;

    /// <summary>呼叫端從原生陣列最多複製這麼多格（還要再跟陣列自己的 Size 取小）。</summary>
    public const int MaxReadLength = FirstIndex + Stride * MaxCount;

    /// <param name="values">已經按陣列 <c>Size</c> 截過的複本；長度就是可讀範圍。</param>
    public static FcStockRead Parse(ReadOnlySpan<int> values)
    {
        if (values.Length <= CountIndex)
            return FcStockRead.Invalid($"陣列只有 {values.Length} 格，讀不到筆數");

        var count = values[CountIndex];
        if (count < 0 || count > MaxCount)
            return FcStockRead.Invalid($"筆數是 {count}，不合理");

        if (count > 0 && FirstIndex + Stride * (count - 1) >= values.Length)
            return FcStockRead.Invalid($"筆數 {count} 超出陣列長度 {values.Length}");

        var actions = new List<uint>(count);
        for (var i = 0; i < count; i++)
        {
            var raw = values[FirstIndex + Stride * i];
            if (raw <= 0 || !FcActionCatalog.IsKnownAction((uint)raw))
                return FcStockRead.Invalid($"第 {i} 筆是 {raw}，不是認得的公會特效");
            actions.Add((uint)raw);
        }

        return new FcStockRead(true, actions, string.Empty);
    }
}

/// <summary>確認框文字對預期的比對結果。</summary>
internal enum FcConfirmMatch
{
    /// <summary>不是「確定要使用「…」特效嗎？」這個句型。零值＝不碰。</summary>
    NotOurs = 0,

    /// <summary>句型對、特效名不對（庫存列對錯了）。</summary>
    OtherAction,

    Exact,
}

internal static class FcActionText
{
    public static string Clean(string? text) => (text ?? string.Empty).ReplaceLineEndings(string.Empty);

    /// <param name="prefix">Addon 2877 placeholder 前的文字（「確定要使用「」）。</param>
    /// <param name="suffix">Addon 2877 placeholder 後的文字（「」特效嗎？」）。</param>
    public static FcConfirmMatch MatchConfirm(string? prompt, string prefix, string suffix, string actionName, out string shownName)
    {
        shownName = string.Empty;
        var text = Clean(prompt);
        prefix = Clean(prefix);
        suffix = Clean(suffix);
        if (text.Length == 0 || prefix.Length == 0 || suffix.Length == 0 || Clean(actionName).Length == 0)
            return FcConfirmMatch.NotOurs;

        if (!text.StartsWith(prefix, StringComparison.Ordinal) || !text.EndsWith(suffix, StringComparison.Ordinal)
            || text.Length <= prefix.Length + suffix.Length)
            return FcConfirmMatch.NotOurs;

        shownName = text.Substring(prefix.Length, text.Length - prefix.Length - suffix.Length);
        return string.Equals(shownName, Clean(actionName), StringComparison.Ordinal)
            ? FcConfirmMatch.Exact
            : FcConfirmMatch.OtherAction;
    }

    /// <summary>右鍵選單裡逐字等於 <paramref name="wanted"/> 的那一項；沒有回 -1。</summary>
    public static int FindMenuEntry(IReadOnlyList<string> entries, string wanted)
    {
        var target = Clean(wanted).Trim();
        if (target.Length == 0) return -1;

        for (var i = 0; i < entries.Count; i++)
        {
            if (string.Equals(Clean(entries[i]).Trim(), target, StringComparison.Ordinal)) return i;
        }

        return -1;
    }
}

internal enum FcMultiMode
{
    /// <summary>AutoRetainer 在，但問不到。零值＝不知道。</summary>
    Unknown = 0,
    NotInstalled,
    Off,
    On,
}

internal enum FcGateVerdict
{
    /// <summary>現在不動，稍後再判（只有自動那條路會用到）。零值＝不動作。</summary>
    Wait = 0,
    Stop,
    Go,
}

/// <param name="MissingCount">勾了、認得狀態、身上沒有的特效數；-1＝角色資料還沒就緒。</param>
/// <param name="BusyReason">遊戲狀態不適合動手的理由；<see langword="null"/>＝沒有。</param>
/// <param name="ArBusy">AutoRetainer 忙不忙；<see langword="null"/>＝IPC 問不到。</param>
/// <param name="OpenDialog">已經開著、會跟我們搶的視窗名；<see langword="null"/>＝沒有。</param>
internal readonly record struct FcGateInput(
    bool Manual,
    int CheckedCount,
    int UnknownStatusCount,
    int MissingCount,
    string? BusyReason,
    FcMultiMode MultiMode,
    bool? ArBusy,
    bool? InFreeCompany,
    bool? InHomeWorld,
    string? OpenDialog);

internal static class FcActionGate
{
    /// <remarks>
    /// 自動：條件不齊就 <see cref="FcGateVerdict.Wait"/>（呼叫端有總時限），確定不會成立的直接 <see cref="FcGateVerdict.Stop"/>。
    /// 手動：不等，任何一項不成立就停；只放寬「AutoRetainer 沒裝或問不到」這一項。
    /// </remarks>
    public static FcGateVerdict Evaluate(in FcGateInput x, out string reason)
    {
        if (x.CheckedCount == 0)
            return Stop("這個角色沒有勾選任何公會特效", out reason);

        if (x.CheckedCount == x.UnknownStatusCount)
            return Stop("勾選的特效全部判不出對應的狀態，不開", out reason);

        if (x.MultiMode == FcMultiMode.On)
            return Stop("AutoRetainer 多角色模式開著", out reason);

        if (x.MissingCount == 0)
            return Stop("勾選的特效身上都已經有了", out reason);

        if (x.InFreeCompany == false)
            return Stop("這個角色不在公會裡", out reason);

        if (x.InHomeWorld == false)
            return Stop("不在原始伺服器", out reason);

        if (x.ArBusy == true)
            return Hold(x.Manual, "AutoRetainer 正在作業", out reason);

        if (x.BusyReason != null)
            return Hold(x.Manual, x.BusyReason, out reason);

        if (x.OpenDialog != null)
            return Hold(x.Manual, $"「{x.OpenDialog}」視窗開著", out reason);

        if (!x.Manual)
        {
            if (x.MultiMode is FcMultiMode.Unknown or FcMultiMode.NotInstalled)
                return Wait($"AutoRetainer 多角色模式問不到（{x.MultiMode}），視為忙", out reason);

            if (x.ArBusy == null)
                return Wait("AutoRetainer 忙不忙問不到，視為忙", out reason);
        }

        if (x.MissingCount < 0 || x.InFreeCompany == null || x.InHomeWorld == null)
            return Hold(x.Manual, "角色資料還沒就緒", out reason);

        reason = string.Empty;
        return FcGateVerdict.Go;
    }

    private static FcGateVerdict Stop(string text, out string reason)
    {
        reason = text;
        return FcGateVerdict.Stop;
    }

    private static FcGateVerdict Wait(string text, out string reason)
    {
        reason = text;
        return FcGateVerdict.Wait;
    }

    private static FcGateVerdict Hold(bool manual, string text, out string reason)
    {
        reason = text;
        return manual ? FcGateVerdict.Stop : FcGateVerdict.Wait;
    }
}

internal readonly record struct FcTarget(uint ActionId, string Name, uint StatusId);

internal enum FcCommandKind
{
    None = 0,
    OpenCompanyWindow,
    OpenActionTab,
    SelectStockRow,
    SelectMenuEntry,
    CloseMenu,
    ClickYes,
    ClickNo,
    CloseCompanyWindow,
    Finished,
}

internal readonly record struct FcCommand(FcCommandKind Kind, int Index = -1)
{
    public static readonly FcCommand None = new(FcCommandKind.None);
}

/// <summary>這一幀看到的東西。只在跑流程時由模組逐幀讀出來。</summary>
internal sealed class FcObservation
{
    public bool CompanyReady;
    public bool ActionReady;

    /// <summary><see langword="null"/>＝這一幀沒讀（特效視窗沒開）。</summary>
    public FcStockRead? Stock;

    public bool MenuReady;
    public IReadOnlyList<string> MenuEntries = Array.Empty<string>();
    public bool ConfirmReady;
    public string ConfirmText = string.Empty;
    public IReadOnlyCollection<uint> Statuses = Array.Empty<uint>();
}

/// <summary>
/// 開公會視窗→特效分頁→逐一「庫存列→使用特效→確認框」→關視窗。每一步都有時限，逾時就收尾。
/// </summary>
/// <remarks>
/// 契約：只在框架執行緒上呼叫；<see cref="Tick"/> 回的指令由呼叫端在同一幀執行。
/// 收尾只關選單與自己開的公會視窗，<b>絕不碰確認框</b>（不是我們的就留給使用者）。
/// </remarks>
internal sealed class FcActionSequence
{
    public const int CompanyWindowTimeoutMs = 6_000;
    public const int CompanyCommandRetryMs = 3_000;
    public const int ActionTabTimeoutMs = 6_000;
    public const int ActionTabRetryMs = 1_500;
    public const int StockTimeoutMs = 5_000;
    /// <summary>同一列連點兩次要隔超過 AddonPressGuard 的終結逃生口（90 幀）：沉澱＋穩定期合起來要大於它。</summary>
    public const int StockSettleMs = 1_500;
    public const int StockStableGapMs = 300;
    public const int MenuTimeoutMs = 4_000;
    public const int ConfirmTimeoutMs = 4_000;
    public const int StatusTimeoutMs = 8_000;
    public const int ClosingTimeoutMs = 3_000;
    public const int CloseRetryMs = 1_000;
    public const int RejectRetryMs = 250;
    public const int MaxRejections = 12;

    private enum Phase
    {
        OpeningCompany,
        OpeningActionTab,
        ReadingStock,
        WaitingMenu,
        WaitingConfirm,
        WaitingStatus,
        Closing,
        Done,
    }

    private readonly List<FcTarget> pending;
    private readonly string useMenuText;
    private readonly string confirmPrefix;
    private readonly string confirmSuffix;
    private readonly Action<string> log;
    private readonly bool companyWasOpen;

    private Phase phase;
    private long phaseStartedAt;
    private long lastSentAt = long.MinValue;
    private int sends;
    private FcTarget current;
    private int currentRow = -1;

    private string? lastStockSignature;
    private long lastStockReadAt;
    private long actionReadySince = -1;
    private bool finishedReported;
    private int rejections;
    private long holdUntil = long.MinValue;

    public List<string> Used { get; } = [];

    public List<string> Failed { get; } = [];

    public string StopReason { get; private set; } = string.Empty;

    /// <summary>正在處理的那一個（還沒選過任何庫存列時是預設值）。</summary>
    public FcTarget Current => current;

    public bool IsDone => phase == Phase.Done;

    public string StepName => phase switch
    {
        Phase.OpeningCompany => "開公會視窗",
        Phase.OpeningActionTab => "切到特效分頁",
        Phase.ReadingStock => "讀庫存",
        Phase.WaitingMenu => $"等「{current.Name}」的選單",
        Phase.WaitingConfirm => $"等「{current.Name}」的確認框",
        Phase.WaitingStatus => $"等「{current.Name}」生效",
        Phase.Closing => "收尾",
        _ => "完成",
    };

    /// <param name="useMenuText">Addon 2817（「使用特效」）。</param>
    /// <param name="companyWasOpen">開始時公會視窗已經開著＝收尾不關它。</param>
    public FcActionSequence(
        IEnumerable<FcTarget> targets, string useMenuText, string confirmPrefix, string confirmSuffix,
        bool companyWasOpen, long nowMs, Action<string> log)
    {
        pending = targets.ToList();
        this.useMenuText = useMenuText;
        this.confirmPrefix = confirmPrefix;
        this.confirmSuffix = confirmSuffix;
        this.companyWasOpen = companyWasOpen;
        this.log = log;
        Enter(Phase.OpeningCompany, nowMs);
        log($"開始：要開 {string.Join("、", pending.Select(t => $"{t.Name}（{t.ActionId}，狀態 {t.StatusId}）"))}"
            + (companyWasOpen ? "；公會視窗本來就開著，收尾不關它" : string.Empty));
    }

    /// <summary>外部要求停（急停、AutoRetainer 忙起來、YesAlready 讓不開、換區…）。冪等。</summary>
    public void Abort(string reason, long nowMs)
    {
        if (phase is Phase.Closing or Phase.Done) return;
        Stop($"中止於「{StepName}」：{reason}", nowMs);
    }

    /// <summary>上一個指令這一幀沒送出去（守衛擋下、視窗剛好不在）：退回前一步，隔一下再送。</summary>
    public void Rejected(string reason, long nowMs)
    {
        if (phase is Phase.Closing or Phase.Done) return;

        if (++rejections > MaxRejections)
        {
            Stop($"{reason}，重試 {MaxRejections} 次仍送不出去", nowMs);
            return;
        }

        if (rejections == 1) log($"{reason}，稍後重送");

        switch (phase)
        {
            case Phase.WaitingMenu:
                pending.Insert(0, current);
                Enter(Phase.ReadingStock, nowMs);
                break;
            case Phase.WaitingConfirm:
                Enter(Phase.WaitingMenu, nowMs);
                break;
            case Phase.WaitingStatus:
                Enter(Phase.WaitingConfirm, nowMs);
                break;
        }

        holdUntil = nowMs + RejectRetryMs;
    }

    public FcCommand Tick(long nowMs, FcObservation obs)
    {
        if (nowMs < holdUntil && phase != Phase.Closing && phase != Phase.Done) return FcCommand.None;

        var elapsed = nowMs - phaseStartedAt;
        switch (phase)
        {
            case Phase.OpeningCompany:
                if (obs.CompanyReady)
                {
                    log("公會視窗已開");
                    Enter(Phase.OpeningActionTab, nowMs);
                    return FcCommand.None;
                }

                if (elapsed > CompanyWindowTimeoutMs)
                    return Stop($"公會視窗 {CompanyWindowTimeoutMs / 1000} 秒內沒開", nowMs);

                if (sends < 2 && SinceSent(nowMs) >= CompanyCommandRetryMs)
                {
                    MarkSent(nowMs);
                    log($"送出 /freecompanycmd 開公會視窗（第 {sends} 次）");
                    return new FcCommand(FcCommandKind.OpenCompanyWindow);
                }

                return FcCommand.None;

            case Phase.OpeningActionTab:
                if (obs.ActionReady)
                {
                    log("特效分頁已開");
                    actionReadySince = nowMs;
                    Enter(Phase.ReadingStock, nowMs);
                    return FcCommand.None;
                }

                if (elapsed > ActionTabTimeoutMs)
                    return Stop($"特效分頁 {ActionTabTimeoutMs / 1000} 秒內沒開", nowMs);

                if (obs.CompanyReady && SinceSent(nowMs) >= ActionTabRetryMs)
                {
                    MarkSent(nowMs);
                    if (sends == 1) log("點公會視窗的特效分頁");
                    return new FcCommand(FcCommandKind.OpenActionTab);
                }

                return FcCommand.None;

            case Phase.ReadingStock:
                return TickReadingStock(nowMs, elapsed, obs);

            case Phase.WaitingMenu:
                return TickWaitingMenu(nowMs, elapsed, obs);

            case Phase.WaitingConfirm:
                return TickWaitingConfirm(nowMs, elapsed, obs);

            case Phase.WaitingStatus:
                if (obs.Statuses.Contains(current.StatusId))
                {
                    log($"身上出現狀態 {current.StatusId}：「{current.Name}」已生效");
                    Used.Add(current.Name);
                    ResetStock();
                    Enter(Phase.ReadingStock, nowMs);
                    return FcCommand.None;
                }

                if (elapsed > StatusTimeoutMs)
                {
                    Failed.Add(current.Name);
                    return Stop(
                        $"按了「是」，{StatusTimeoutMs / 1000} 秒內身上沒出現狀態 {current.StatusId}"
                        + "（可能被遊戲拒絕，例如同時生效的特效已滿）；不重試，整輪停止", nowMs);
                }

                return FcCommand.None;

            case Phase.Closing:
                return TickClosing(nowMs, elapsed, obs);

            default:
                if (finishedReported) return FcCommand.None;
                finishedReported = true;
                return new FcCommand(FcCommandKind.Finished);
        }
    }

    private FcCommand TickReadingStock(long nowMs, long elapsed, FcObservation obs)
    {
        if (!obs.ActionReady)
        {
            actionReadySince = -1;
            return elapsed > StockTimeoutMs ? Stop("特效分頁關掉了", nowMs) : FcCommand.None;
        }

        if (actionReadySince < 0) actionReadySince = nowMs;

        if (elapsed > StockTimeoutMs)
        {
            var what = obs.Stock is { } s ? s.Describe() : "讀不到";
            return Stop($"庫存 {StockTimeoutMs / 1000} 秒內沒有穩定的讀數（最後一次：{what}）", nowMs);
        }

        if (nowMs - actionReadySince < StockSettleMs || obs.Stock is not { } stock) return FcCommand.None;

        if (!stock.Valid)
        {
            // 形狀不對不會自己變好；等到逾時再停，讓 log 留下最後一次的讀數。
            lastStockSignature = null;
            return FcCommand.None;
        }

        var signature = stock.Describe();
        if (lastStockSignature != signature)
        {
            lastStockSignature = signature;
            lastStockReadAt = nowMs;
            return FcCommand.None;
        }

        if (nowMs - lastStockReadAt < StockStableGapMs) return FcCommand.None;

        log($"庫存 {stock.Actions.Count} 筆：{signature}");

        while (pending.Count > 0)
        {
            var next = pending[0];
            if (obs.Statuses.Contains(next.StatusId))
            {
                log($"「{next.Name}」身上已有狀態 {next.StatusId}，略過");
                pending.RemoveAt(0);
                continue;
            }

            var row = IndexOf(stock.Actions, next.ActionId);
            if (row < 0)
            {
                log($"庫存裡沒有「{next.Name}」（購買不做），略過");
                Failed.Add(next.Name);
                pending.RemoveAt(0);
                continue;
            }

            if (obs.MenuReady || obs.ConfirmReady)
                return FcCommand.None;

            pending.RemoveAt(0);
            current = next;
            currentRow = row;
            log($"點庫存第 {row} 列「{next.Name}」");
            Enter(Phase.WaitingMenu, nowMs);
            return new FcCommand(FcCommandKind.SelectStockRow, row);
        }

        return Stop("清單處理完", nowMs, quiet: true);
    }

    private FcCommand TickWaitingMenu(long nowMs, long elapsed, FcObservation obs)
    {
        if (obs.MenuReady && obs.MenuEntries.Count > 0)
        {
            var index = FcActionText.FindMenuEntry(obs.MenuEntries, useMenuText);
            if (index < 0)
            {
                log($"「{current.Name}」的選單裡沒有「{useMenuText}」：[{string.Join("｜", obs.MenuEntries)}]；關選單、換下一個");
                Failed.Add(current.Name);
                ResetStock();
                Enter(Phase.ReadingStock, nowMs);
                return new FcCommand(FcCommandKind.CloseMenu);
            }

            log($"選單第 {index} 項「{useMenuText}」");
            Enter(Phase.WaitingConfirm, nowMs);
            return new FcCommand(FcCommandKind.SelectMenuEntry, index);
        }

        if (elapsed > MenuTimeoutMs)
        {
            Failed.Add(current.Name);
            return Stop($"點了庫存第 {currentRow} 列，{MenuTimeoutMs / 1000} 秒內沒出現選單", nowMs);
        }

        return FcCommand.None;
    }

    private FcCommand TickWaitingConfirm(long nowMs, long elapsed, FcObservation obs)
    {
        if (obs.ConfirmReady && obs.ConfirmText.Length > 0 && !obs.ConfirmText.Contains((char)0xFFFD))
        {
            var match = FcActionText.MatchConfirm(obs.ConfirmText, confirmPrefix, confirmSuffix, current.Name, out var shown);
            switch (match)
            {
                case FcConfirmMatch.Exact:
                    log($"確認框「{FcActionText.Clean(obs.ConfirmText)}」對上，按「是」");
                    Enter(Phase.WaitingStatus, nowMs);
                    return new FcCommand(FcCommandKind.ClickYes);

                case FcConfirmMatch.OtherAction:
                    Failed.Add(current.Name);
                    Stop($"確認框要用的是「{shown}」不是「{current.Name}」：庫存列對不上，按「否」，整輪停止", nowMs);
                    return new FcCommand(FcCommandKind.ClickNo);

                default:
                    Failed.Add(current.Name);
                    return Stop($"確認框文字不是預期的句型：「{FcActionText.Clean(obs.ConfirmText)}」，不碰它", nowMs);
            }
        }

        if (elapsed > ConfirmTimeoutMs)
        {
            Failed.Add(current.Name);
            return Stop($"選了「{useMenuText}」，{ConfirmTimeoutMs / 1000} 秒內沒出現確認框", nowMs);
        }

        return FcCommand.None;
    }

    private FcCommand TickClosing(long nowMs, long elapsed, FcObservation obs)
    {
        var wantMenuClosed = obs.MenuReady;
        var wantCompanyClosed = !companyWasOpen && obs.CompanyReady;
        if (!wantMenuClosed && !wantCompanyClosed)
            return Finish(nowMs, string.Empty);

        if (elapsed > ClosingTimeoutMs)
            return Finish(nowMs, "；視窗沒關掉，留給你");

        if (SinceSent(nowMs) < CloseRetryMs) return FcCommand.None;
        MarkSent(nowMs);
        return new FcCommand(wantMenuClosed ? FcCommandKind.CloseMenu : FcCommandKind.CloseCompanyWindow);
    }

    private FcCommand Finish(long nowMs, string note)
    {
        Enter(Phase.Done, nowMs);
        log($"結束：開了 {Describe(Used)}；沒開成 {Describe(Failed)}"
            + (StopReason.Length > 0 ? $"；停止原因：{StopReason}" : string.Empty) + note);
        finishedReported = true;
        return new FcCommand(FcCommandKind.Finished);
    }

    private FcCommand Stop(string reason, long nowMs, bool quiet = false)
    {
        if (!quiet)
        {
            StopReason = reason;
            log($"停止：{reason}");
        }

        Enter(Phase.Closing, nowMs);
        lastSentAt = long.MinValue;
        return FcCommand.None;
    }

    private void Enter(Phase next, long nowMs)
    {
        phase = next;
        phaseStartedAt = nowMs;
        sends = 0;
        lastSentAt = long.MinValue;
    }

    /// <summary>距上次送出幾毫秒；還沒送過＝<see cref="long.MaxValue"/>（直接相減會溢位）。</summary>
    private long SinceSent(long nowMs) => lastSentAt == long.MinValue ? long.MaxValue : nowMs - lastSentAt;

    private void MarkSent(long nowMs)
    {
        lastSentAt = nowMs;
        sends++;
    }

    private void ResetStock()
    {
        lastStockSignature = null;
        actionReadySince = -1;
    }

    private static int IndexOf(IReadOnlyList<uint> list, uint value)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i] == value) return i;
        }

        return -1;
    }

    private static string Describe(List<string> names) => names.Count == 0 ? "（無）" : string.Join("、", names);
}

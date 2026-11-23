using System;
using System.Collections.Generic;
using System.Linq;

namespace TCToolbox.Core;

// 本檔假設與契約：「替我選今天的任務並派遣」的純邏輯，不碰 Dalamud 與原生指標（離線探針直接編譯本檔）。
// 輸入一律是呼叫端在同一幀抄好的快照（SquadronDiag.ReadCells／ParseExpeditionNa）；版面、範圍、交叉驗證
// 任何一項不符都回一句停手理由，不猜、不重試。成功率分級用遊戲寫在 NumberArray 的 [14]，表只拿來驗它。

/// <summary>任務清單的一列（本頁列索引、任務 id、可不可接）。</summary>
internal readonly record struct SquadronListRow(int Row, uint Id, bool Available);

/// <summary>任務清單一次嚴格讀取的結果。</summary>
internal sealed class SquadronListRead
{
    public int Tab;

    /// <summary>分頁數；只有開窗後第一次讀才讀（切分頁後那一格不保證）；沒讀是 -1。</summary>
    public int TabCount = -1;

    public readonly List<SquadronListRow> Rows = [];
}

/// <summary>選中某一列後從 NumberArray 113 讀到、並通過交叉驗證的值。</summary>
internal sealed record SquadronRowRead(
    uint Id, string Name, int Tab, int Row, int Level, bool Important, int Tier, int Percent, uint Experience,
    bool LevelBlocked, int SealsHeld, int SealsNeeded, int Met, (int P, int M, int T) Need, (int P, int M, int T) Total)
{
    public bool SealsShort => SealsHeld < SealsNeeded;

    /// <summary>使用者規則：一般任務至少兩項達標（分級 ≥3），重要任務三項全達標（分級 4）；等級與軍票都要夠。</summary>
    public bool Eligible => !LevelBlocked && !SealsShort && (Important ? Tier == 4 : Tier >= 3);

    public string Describe() =>
        $"#{Id}「{Name}」Lv{Level}{(Important ? "（重要任務）" : string.Empty)}：需求 {Need.P}/{Need.M}/{Need.T}、" +
        $"隊伍 {Total.P}/{Total.M}/{Total.T}、達標 {Met} 項（{Percent}%，分級 {Tier}）、經驗 {Experience}、" +
        $"軍票 {SealsNeeded}／持有 {SealsHeld}{(LevelBlocked ? "、隊員等級不足" : string.Empty)} → " +
        (Eligible ? "符合條件" : $"不選（{WhyNot()}）");

    private string WhyNot()
    {
        if (LevelBlocked) return "隊員等級不足";
        if (SealsShort) return "軍票不足";
        return Important ? "重要任務要三項全達標" : "達標不到兩項";
    }
}

internal enum SquadronScanKind
{
    Wait = 0,

    /// <summary>送 (11, Arg) 切分頁。</summary>
    SelectTab = 1,

    /// <summary>送 (12, Arg) 選本頁第 Arg 列。</summary>
    SelectRow = 2,

    /// <summary>選定、而且清單上現在選中的就是它（<see cref="SquadronAutoScan.Picked"/>）。</summary>
    Done = 3,

    Stop = 4,
}

internal readonly record struct SquadronScanAction(SquadronScanKind Kind, int Arg, string Reason)
{
    public static SquadronScanAction Wait() => new(SquadronScanKind.Wait, -1, string.Empty);

    public static SquadronScanAction Stop(string reason) => new(SquadronScanKind.Stop, -1, reason);
}

internal static class SquadronAutoPick
{
    /// <summary>v2a 只看簡單、普通兩個分頁（特殊任務分頁是 v2c）。</summary>
    public const int CandidateTabs = 2;

    public static bool IsImportant(SquadronMission m) => m.PercentBase == 0;

    /// <summary>嚴格讀任務清單：格數、型別、範圍、表類型＝分頁+1，任何一處不符回 <c>null</c>。</summary>
    /// <param name="readTabCount">本次開窗第一次讀：[1] 分頁數只有這時可信。</param>
    public static SquadronListRead? ReadList(ReadOnlySpan<SquadronCell> c, SquadronTables t, bool readTabCount, out string problem)
    {
        problem = string.Empty;
        if (c.Length != SquadronDiag.ExpeditionCells)
        {
            problem = $"任務清單格數 {c.Length}，不是 {SquadronDiag.ExpeditionCells}";
            return null;
        }

        var r = new SquadronListRead();
        if (!IntCell(c, 0, 0, 2, out r.Tab, ref problem)) return null;
        if (readTabCount)
        {
            if (!IntCell(c, 1, 1, 3, out r.TabCount, ref problem)) return null;
            if (r.Tab >= r.TabCount)
            {
                problem = $"[0]分頁 {r.Tab} 不小於 [1]分頁數 {r.TabCount}";
                return null;
            }
        }

        if (!IntCell(c, 6, 0, SquadronDiag.MaxListRows, out var rows, ref problem)) return null;
        if (rows > 0 && 9 + 4 * (rows - 1) >= c.Length)
        {
            problem = $"[6]列數 {rows} 超出格數";
            return null;
        }

        for (var i = 0; i < rows; i++)
        {
            var a = c[8 + 4 * i];
            if (a.Type != SquadronDiag.TypeBool)
            {
                problem = $"第 {i} 列可用格 [{8 + 4 * i}] 型別是 {SquadronDiag.TypeName(a.Type)}，不是 Bool";
                return null;
            }

            if (!IntCell(c, 9 + 4 * i, 1, SquadronDiag.MaxMissionId, out var id, ref problem)) return null;
            if (!t.Missions.TryGetValue((uint)id, out var m))
            {
                problem = $"第 {i} 列 #{id} 不在任務表內";
                return null;
            }

            if (m.Type != r.Tab + 1)
            {
                problem = $"第 {i} 列 #{id} 表類型 {m.Type}，不等於分頁 {r.Tab}+1";
                return null;
            }

            r.Rows.Add(new SquadronListRow(i, (uint)id, a.Value != 0));
        }

        return r;
    }

    private static bool IntCell(ReadOnlySpan<SquadronCell> c, int i, int min, int max, out int value, ref string problem)
    {
        value = 0;
        var cell = c[i];
        if (cell.Type != SquadronDiag.TypeInt)
        {
            problem = $"[{i}] 型別是 {SquadronDiag.TypeName(cell.Type)}，不是 Int";
            return false;
        }

        if (cell.Value < min || cell.Value > max)
        {
            problem = $"[{i}]={cell.Value} 不在 {min}..{max}";
            return false;
        }

        value = (int)cell.Value;
        return true;
    }

    /// <summary>
    /// 選中 <paramref name="expectedId"/> 之後讀到的 NumberArray 113，對表交叉驗證：需求是表中某一版、
    /// 各列推出的每日版本一致、[14] 分級等於用需求與合計算出的分級、經驗／軍票／等級不足旗標與表相符、在隊恰好 4 人。
    /// </summary>
    /// <param name="variants">本輪至今各列推得的每日版本交集；第一列傳 <c>null</c>。</param>
    public static SquadronRowRead? ReadRow(
        SquadronExpeditionNa? na, uint expectedId, int tab, int row, SquadronTables t, ref HashSet<int>? variants, out string problem)
    {
        problem = string.Empty;
        if (na == null)
        {
            problem = "讀不到 NumberArray 113";
            return null;
        }

        if (na.MissionId != expectedId)
        {
            problem = $"NA[1]=#{na.MissionId}，不是要讀的 #{expectedId}";
            return null;
        }

        if (!t.Missions.TryGetValue(expectedId, out var m))
        {
            problem = $"#{expectedId} 不在任務表內";
            return null;
        }

        var team = na.InTeam.ToList();
        if (!na.InTeamContiguous || team.Count != 4)
        {
            problem = $"隊伍不是 4 人（在隊 {team.Count} 人）；這個功能不替你換隊員";
            return null;
        }

        var cands = SquadronDiag.MatchVariants(m, na.Need[0], na.Need[1], na.Need[2]);
        if (cands.Count == 0)
        {
            problem = $"#{m.Id} 的需求 {na.Need[0]}/{na.Need[1]}/{na.Need[2]} 不等於表中任一版本";
            return null;
        }

        var now = variants == null ? new HashSet<int>(cands) : new HashSet<int>(variants.Intersect(cands));
        if (now.Count == 0)
        {
            problem = $"每日版本前後不一致（先前 v∈{{{string.Join(",", variants!.Order())}}}、#{m.Id} v∈{{{string.Join(",", cands)}}}）";
            return null;
        }

        if (na.Tier is < 0 or > 4)
        {
            problem = $"[14]分級={na.Tier} 不在 0..4";
            return null;
        }

        var percents = now.Select(v => SquadronDiag.Percent(m, v, na.Total[0], na.Total[1], na.Total[2])).Distinct().ToList();
        var match = percents.Where(p => SquadronDiag.Tier(p) == na.Tier).ToList();
        if (match.Count == 0)
        {
            problem = $"#{m.Id} 的 [14]分級={na.Tier}，用需求與合計算出的是 {string.Join("或", percents.Select(SquadronDiag.Tier).Distinct())}";
            return null;
        }

        var maxLevel = team.Max(x => x.Level);
        var blocked = maxLevel < m.Level ? 1 : 0;
        if (na.LevelBlocked != blocked || na.Experience != m.Experience || na.Seals != m.Seals)
        {
            problem = $"#{m.Id} 的 [2]等級不足={na.LevelBlocked}／[3]經驗={na.Experience}／[7]軍票={na.Seals}，" +
                      $"表與隊員算出的是 {blocked}／{m.Experience}／{m.Seals}";
            return null;
        }

        variants = now;
        var need = (na.Need[0], na.Need[1], na.Need[2]);
        var total = (na.Total[0], na.Total[1], na.Total[2]);
        var met = (total.Item1 >= need.Item1 ? 1 : 0) + (total.Item2 >= need.Item2 ? 1 : 0) + (total.Item3 >= need.Item3 ? 1 : 0);
        return new SquadronRowRead(
            m.Id, m.Name, tab, row, m.Level, IsImportant(m), na.Tier, match[0], m.Experience,
            blocked == 1, na.Raw[0], na.Seals, met, need, total);
    }

    /// <summary>
    /// 使用者規則：重要任務三項全達標就選它（目標任務）；否則一般任務裡至少兩項達標、等級最高的；
    /// 同級時分級高者、再經驗多者、再 id 小者。
    /// </summary>
    public static SquadronRowRead? Choose(IEnumerable<SquadronRowRead> reads) =>
        reads.Where(r => r.Eligible)
             .OrderByDescending(r => r.Important)
             .ThenByDescending(r => r.Level)
             .ThenByDescending(r => r.Tier)
             .ThenByDescending(r => r.Experience)
             .ThenBy(r => r.Id)
             .FirstOrDefault();

    /// <summary>一句話說明為什麼選它（寫進 log）。</summary>
    public static string Reason(SquadronRowRead pick, IReadOnlyList<SquadronRowRead> reads)
    {
        if (pick.Important) return "重要任務三項全達標，優先於一般任務";

        var sameLevel = reads.Where(r => r.Eligible && !r.Important && r.Level == pick.Level && r.Id != pick.Id).ToList();
        var head = $"符合條件（至少兩項達標）的一般任務裡等級最高（Lv{pick.Level}）";
        if (sameLevel.Count == 0) return head;

        return head + $"；同級還有 {string.Join("、", sameLevel.Select(r => $"#{r.Id}（分級 {r.Tier}、經驗 {r.Experience}）"))}，" +
               "依分級高、經驗多、id 小選它";
    }

    /// <summary>一頁裡要讀的列：可接的列，重要任務先，再依等級高到低、id 小到大。</summary>
    public static List<SquadronListRow> ScanOrder(SquadronListRead list, SquadronTables t) =>
        list.Rows.Where(r => r.Available && t.Missions.ContainsKey(r.Id))
            .OrderByDescending(r => IsImportant(t.Missions[r.Id]))
            .ThenByDescending(r => t.Missions[r.Id].Level)
            .ThenBy(r => r.Id)
            .ToList();

    /// <summary>
    /// 這一頁還要不要往下讀：重要任務已選得出就停；一般任務已有候選、下一列等級更低就停（同級要讀完才比得出分級與經驗）。
    /// </summary>
    public static bool ScanTabDone(IReadOnlyList<SquadronRowRead> tabReads, SquadronListRow? next, SquadronTables t)
    {
        if (next is not { } n) return true;

        var best = Choose(tabReads);
        if (best == null) return false;
        if (best.Important) return true;

        return t.Missions[n.Id].Level < best.Level && !IsImportant(t.Missions[n.Id]);
    }

    /// <summary>依序含有全部片段（比對前兩邊都去掉空白）。</summary>
    public static bool ContainsInOrder(string text, IReadOnlyList<string> fragments)
    {
        if (fragments.Count == 0 || string.IsNullOrEmpty(text)) return false;

        var hay = SquadronFlow.StripWhitespace(text);
        var at = 0;
        foreach (var f in fragments)
        {
            var hit = hay.IndexOf(f, at, StringComparison.Ordinal);
            if (hit < 0) return false;
            at = hit + f.Length;
        }

        return true;
    }

    /// <summary>
    /// 派遣確認框白名單：依序含有 Addon 10522 的全部固定片段、解析得出歸隊時間，
    /// 而且「確定要開始…嗎？」中間的任務名整句等於選定的任務名。
    /// </summary>
    public static bool ConfirmMatches(string text, IReadOnlyList<string> fragments, string expectedMission, DateTime nowLocal, out string problem)
    {
        problem = string.Empty;
        if (expectedMission.Trim().Length == 0)
        {
            problem = "沒有選定的任務名";
            return false;
        }

        if (!ContainsInOrder(text, fragments))
        {
            problem = "不是派遣確認框（固定片段對不上）";
            return false;
        }

        if (!SquadronFlow.TryParseReturn(text, fragments, nowLocal, out var parsed))
        {
            problem = "解析不出任務名或預定歸隊時間";
            return false;
        }

        if (SquadronFlow.NormalizeEntry(parsed.Mission) != SquadronFlow.NormalizeEntry(expectedMission))
        {
            problem = $"確認框上的任務是「{parsed.Mission}」，不是選定的「{expectedMission}」";
            return false;
        }

        return true;
    }
}

/// <summary>
/// 任務清單打開之後的掃描狀態機：每幀交給它本幀的清單與 NumberArray 快照，它回「送什麼／等／完成／停手」。
/// </summary>
/// <remarks>
/// 讀一列之前必須親眼看到 NA[1] 在本輪換成那一列的 id（開窗前、上一個角色留下的值都不算），之後再連兩幀不變才讀。
/// 呼叫端真的送出後要呼叫 <see cref="Sent"/>；沒送出（守衛擋下）下一幀會拿到同一個動作。
/// </remarks>
internal sealed class SquadronAutoScan(SquadronTables tables)
{
    public const long SettleMs = 500;
    public const long TabWaitMs = 3_000;
    public const long RowWaitMs = 2_000;
    public const long UnsentLimitMs = 2_000;
    public const int MaxSends = 40;

    private enum Phase
    {
        Open = 0,
        Scan = 1,
        Final = 2,
        Done = 3,
    }

    private enum PendingKind
    {
        None = 0,
        Tab = 1,
        Row = 2,
    }

    private Phase phase;
    private long? openedMs;
    private int tabCount = -1;
    private readonly List<int> tabsLeft = [];
    private int scanTab = -1;
    private List<SquadronListRow> order = [];
    private int orderIndex;
    private readonly List<SquadronRowRead> reads = [];
    private readonly List<SquadronRowRead> tabReads = [];
    private HashSet<int>? variants;

    private PendingKind pending;
    private int pendingArg;
    private uint pendingWant;
    private long pendingSince;
    private long? sentAt;
    private int sends;

    /// <summary>為了看到換值而先繞去別列時，繞完要回來的那一列。</summary>
    private (int Row, uint Id)? detour;

    private uint? lastSeenId;
    private uint? freshId;
    private ulong lastHash;
    private int stableTicks;

    /// <summary>呼叫端每幀取走、寫成 Information 的決策紀錄。</summary>
    public List<string> Log { get; } = [];

    public SquadronRowRead? Picked { get; private set; }

    public IReadOnlyList<SquadronRowRead> Reads => reads;

    public int Sends => sends;

    /// <summary>這一幀要不要連 [1] 分頁數一起讀（只有開窗後第一次讀）。</summary>
    public bool WantsTabCount => phase == Phase.Open;

    public void Sent(long nowMs)
    {
        sentAt = nowMs;
        sends++;
    }

    public SquadronScanAction Tick(SquadronListRead? list, string listProblem, SquadronExpeditionNa? na, string naProblem, long nowMs)
    {
        ObserveNa(na);

        if (sends > MaxSends) return SquadronScanAction.Stop($"已經送了 {sends} 次操作還沒選定，停手");

        switch (phase)
        {
            case Phase.Open:
                return OnOpen(list, listProblem, nowMs);
            case Phase.Scan:
            case Phase.Final:
                return OnPending(list, listProblem, na, naProblem, nowMs);
            default:
                return new SquadronScanAction(SquadronScanKind.Done, -1, string.Empty);
        }
    }

    /// <summary>NA[1] 在本輪換值＝遊戲剛重寫過這一組；連續不變的幀數用來確認寫完。</summary>
    private void ObserveNa(SquadronExpeditionNa? na)
    {
        if (na == null)
        {
            stableTicks = 0;
            return;
        }

        if (lastSeenId is { } prev && prev != na.MissionId) freshId = na.MissionId;
        lastSeenId = na.MissionId;

        var hash = SquadronDiag.Hash(na.Raw.AsSpan(1, Math.Min(na.Raw.Length, SquadronDiag.ExpeditionNaMinSize) - 1));
        stableTicks = hash == lastHash ? stableTicks + 1 : 0;
        lastHash = hash;
    }

    private SquadronScanAction OnOpen(SquadronListRead? list, string listProblem, long nowMs)
    {
        openedMs ??= nowMs;
        if (nowMs - openedMs.Value < SettleMs) return SquadronScanAction.Wait();

        if (list == null) return SquadronScanAction.Stop($"任務清單版面不符：{listProblem}");

        tabCount = list.TabCount;
        for (var t = Math.Min(tabCount, SquadronAutoPick.CandidateTabs) - 1; t >= 0; t--) tabsLeft.Add(t);
        Log.Add($"任務清單：目前分頁 {list.Tab}、分頁數 {tabCount}、本頁 {list.Rows.Count} 列；要看的分頁（依序）{string.Join("、", tabsLeft)}");
        phase = Phase.Scan;
        return NextTab(list, nowMs);
    }

    private SquadronScanAction NextTab(SquadronListRead list, long nowMs)
    {
        if (tabsLeft.Count == 0) return Finish(list, nowMs);

        scanTab = tabsLeft[0];
        tabsLeft.RemoveAt(0);
        tabReads.Clear();
        if (list.Tab != scanTab) return Begin(PendingKind.Tab, scanTab, 0, nowMs);

        return StartTab(list, nowMs);
    }

    private SquadronScanAction StartTab(SquadronListRead list, long nowMs)
    {
        order = SquadronAutoPick.ScanOrder(list, tables);
        orderIndex = 0;
        var skipped = list.Rows.Where(r => !r.Available).Select(r => $"#{r.Id}");
        Log.Add($"分頁 {scanTab} 候選列（讀的順序）：{(order.Count == 0 ? "無" : string.Join("、", order.Select(DescribeRow)))}" +
                (skipped.Any() ? $"；不可接 {string.Join("、", skipped)}" : string.Empty));
        return NextRow(list, nowMs);
    }

    private string DescribeRow(SquadronListRow r)
    {
        var m = tables.Missions[r.Id];
        return $"#{r.Id}「{m.Name}」Lv{m.Level}{(SquadronAutoPick.IsImportant(m) ? "（重要）" : string.Empty)}";
    }

    private SquadronScanAction NextRow(SquadronListRead list, long nowMs)
    {
        SquadronListRow? next = orderIndex < order.Count ? order[orderIndex] : null;
        if (SquadronAutoPick.ScanTabDone(tabReads, next, tables))
        {
            if (orderIndex < order.Count) Log.Add($"分頁 {scanTab} 剩下的列不會比已讀到的更好，不再讀");
            if (SquadronAutoPick.Choose(reads) != null) return Finish(list, nowMs);

            return NextTab(list, nowMs);
        }

        var row = next!.Value;
        return Begin(PendingKind.Row, row.Row, row.Id, nowMs);
    }

    private SquadronScanAction Begin(PendingKind kind, int arg, uint want, long nowMs)
    {
        pending = kind;
        pendingArg = arg;
        pendingWant = want;
        pendingSince = nowMs;
        sentAt = null;

        // 下一幀才決定送不送：目標可能已經選中（不必送），或要先繞去別列。
        return SquadronScanAction.Wait();
    }

    private SquadronScanAction Emit() => pending switch
    {
        PendingKind.Tab => new SquadronScanAction(SquadronScanKind.SelectTab, pendingArg, string.Empty),
        PendingKind.Row => new SquadronScanAction(SquadronScanKind.SelectRow, pendingArg, string.Empty),
        _ => SquadronScanAction.Wait(),
    };

    private SquadronScanAction OnPending(SquadronListRead? list, string listProblem, SquadronExpeditionNa? na, string naProblem, long nowMs)
    {
        if (pending == PendingKind.Tab) return OnTab(list, listProblem, nowMs);

        return OnRow(list, listProblem, na, naProblem, nowMs);
    }

    private SquadronScanAction OnTab(SquadronListRead? list, string listProblem, long nowMs)
    {
        if (sentAt == null)
        {
            if (nowMs - pendingSince > UnsentLimitMs) return SquadronScanAction.Stop($"切到分頁 {pendingArg} 的操作一直送不出去");
            return Emit();
        }

        if (list != null && list.Tab == pendingArg)
        {
            pending = PendingKind.None;
            if (phase == Phase.Final) return SelectFinal(list, nowMs);

            return StartTab(list, nowMs);
        }

        if (nowMs - sentAt.Value > TabWaitMs)
            return SquadronScanAction.Stop($"切到分頁 {pendingArg} 後 {TabWaitMs / 1000} 秒清單沒有換頁" +
                                           (list == null ? $"（{listProblem}）" : $"（目前分頁 {list.Tab}）"));

        return SquadronScanAction.Wait();
    }

    private SquadronScanAction OnRow(SquadronListRead? list, string listProblem, SquadronExpeditionNa? na, string naProblem, long nowMs)
    {
        if (list == null)
        {
            return nowMs - (sentAt ?? pendingSince) > RowWaitMs
                ? SquadronScanAction.Stop($"任務清單版面不符：{listProblem}")
                : SquadronScanAction.Wait();
        }

        if (list.Tab != (phase == Phase.Final ? Picked!.Tab : scanTab))
            return SquadronScanAction.Stop($"清單被切到分頁 {list.Tab}（不是我們切的）");

        if (na != null && na.MissionId == pendingWant && freshId == pendingWant)
        {
            if (stableTicks < 1) return SquadronScanAction.Wait();

            pending = PendingKind.None;
            if (detour is { } back)
            {
                detour = null;
                return Begin(PendingKind.Row, back.Row, back.Id, nowMs);
            }

            return phase == Phase.Final ? VerifyFinal(na) : ReadCurrent(list, na, nowMs);
        }

        if (sentAt == null)
        {
            if (nowMs - pendingSince > UnsentLimitMs) return SquadronScanAction.Stop($"選第 {pendingArg} 列的操作一直送不出去");

            // NA[1] 已經是目標卻沒看到它在本輪換值：先選別列再回來，才知道讀到的是新值。
            if (na != null && na.MissionId == pendingWant)
            {
                var other = list.Rows.Where(r => r.Id != pendingWant).OrderByDescending(r => r.Available).FirstOrDefault();
                if (other.Id == 0) return SquadronScanAction.Stop($"這一頁只有 #{pendingWant} 一列，看不出 NumberArray 有沒有換成新值");

                Log.Add($"清單上已選中 #{pendingWant} 但本輪沒看過它換值，先選 #{other.Id} 再選回來");
                var target = (pendingArg, pendingWant);
                pendingArg = other.Row;
                pendingWant = other.Id;
                detour = target;
            }

            return Emit();
        }

        if (nowMs - sentAt.Value > RowWaitMs)
            return SquadronScanAction.Stop($"選第 {pendingArg} 列（#{pendingWant}）後 {RowWaitMs / 1000} 秒 NumberArray 還沒換成它" +
                                           (na == null ? $"（{naProblem}）" : $"（NA[1]=#{na.MissionId}）"));

        return SquadronScanAction.Wait();
    }

    private SquadronScanAction ReadCurrent(SquadronListRead list, SquadronExpeditionNa na, long nowMs)
    {
        var row = order[orderIndex];
        var read = SquadronAutoPick.ReadRow(na, row.Id, scanTab, row.Row, tables, ref variants, out var problem);
        if (read == null) return SquadronScanAction.Stop($"交叉驗證不符：{problem}");

        reads.Add(read);
        tabReads.Add(read);
        Log.Add($"讀到 {read.Describe()}");
        orderIndex++;
        return NextRow(list, nowMs);
    }

    private SquadronScanAction Finish(SquadronListRead list, long nowMs)
    {
        var pick = SquadronAutoPick.Choose(reads);
        if (pick == null)
        {
            var why = reads.Count == 0 ? "兩個分頁都沒有可接的任務" : $"讀過的 {reads.Count} 個任務沒有一個符合條件";
            return SquadronScanAction.Stop($"今天用現有隊員沒有至少兩項達標的任務（{why}）");
        }

        Picked = pick;
        phase = Phase.Final;
        Log.Add($"選定 #{pick.Id}「{pick.Name}」Lv{pick.Level}：{SquadronAutoPick.Reason(pick, reads)}");
        if (list.Tab != pick.Tab) return Begin(PendingKind.Tab, pick.Tab, 0, nowMs);

        return SelectFinal(list, nowMs);
    }

    private SquadronScanAction SelectFinal(SquadronListRead list, long nowMs)
    {
        var row = list.Rows.FirstOrDefault(r => r.Id == Picked!.Id);
        if (row.Id == 0 || row.Row != Picked!.Row) return SquadronScanAction.Stop($"清單上找不到選定的 #{Picked!.Id}（或列號變了）");

        return Begin(PendingKind.Row, row.Row, row.Id, nowMs);
    }

    private SquadronScanAction VerifyFinal(SquadronExpeditionNa na)
    {
        var again = SquadronAutoPick.ReadRow(na, Picked!.Id, Picked.Tab, Picked.Row, tables, ref variants, out var problem);
        if (again == null) return SquadronScanAction.Stop($"選回 #{Picked.Id} 後交叉驗證不符：{problem}");
        if (!again.Eligible || again.Tier != Picked.Tier || again.Total != Picked.Total)
            return SquadronScanAction.Stop($"選回 #{Picked.Id} 後的值和剛才讀的不同（剛才 {Picked.Describe()}；現在 {again.Describe()}）");

        Picked = again;
        phase = Phase.Done;
        return new SquadronScanAction(SquadronScanKind.Done, -1, string.Empty);
    }
}

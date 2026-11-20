using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace TCToolbox.Core;

// 本檔假設與契約：國防聯軍分隊的只讀診斷（v2-0），純邏輯，不碰 Dalamud（離線探針直接編譯本檔）。
// 原生記憶體只經過 ReadCells／CopyInts：長度一律先截到呼叫端給的上界；AtkValue 只讀型別欄與
// Int／UInt／Bool 的聯合欄，字串等其他型別的格只記型別、永不解參考。
// 預期值（格數、索引意義、公式）是從台服執行檔靜態推導來的；這裡只把推導寫成「符合／不符」，
// 任何一項不符都只是寫進 log，不會讓呼叫端做任何事。

/// <summary>AtkValue 一格的快照。<see cref="HasValue"/> 只有 Int／UInt／Bool 才會是 true。</summary>
internal readonly record struct SquadronCell(int Type, bool HasValue, long Value);

/// <summary>任務某一個每日版本的需求、各項達標加的百分比與獎勵。</summary>
internal sealed record SquadronVariant(
    int Physical, int Mental, int Tactical,
    int PctPhysical, int PctMental, int PctTactical, int PctAllMet,
    uint RewardItem, int RewardQuantity);

internal sealed record SquadronMission(
    uint Id, string Name, int Level, int Type, int PercentBase, uint Experience, int Seals, SquadronVariant[] Variants);

/// <summary>比對用的表。讀不到表時是空的，比對改成「無法比」而不是「不符」。</summary>
internal sealed class SquadronTables
{
    public Dictionary<uint, SquadronMission> Missions { get; } = [];

    /// <summary>ClassJob → 各等級的基礎三項（索引＝等級；60 級那組來自 Lumina 的 Unknown1..3）。</summary>
    public Dictionary<uint, (int P, int M, int T)[]> Grow { get; } = [];

    public bool HasMissions => Missions.Count > 0;

    public bool HasGrow => Grow.Count > 0;

    /// <summary>遊戲查成長表時等級上限 60。</summary>
    public static int ClampLevel(int level) => Math.Clamp(level, 0, 60);

    /// <summary>該等級各職業三項合計的最小值；表裡沒有這一級回 <c>null</c>。</summary>
    public int? MinLevelSum(int level)
    {
        var lv = ClampLevel(level);
        int? best = null;
        foreach (var rows in Grow.Values)
        {
            if (lv >= rows.Length) continue;
            var s = rows[lv].P + rows[lv].M + rows[lv].T;
            if (best == null || s < best) best = s;
        }

        return best;
    }

    public List<uint> ClassesWithTriple(int level, int p, int m, int t)
    {
        var lv = ClampLevel(level);
        var result = new List<uint>();
        foreach (var (cj, rows) in Grow)
        {
            if (lv < rows.Length && rows[lv] == (p, m, t)) result.Add(cj);
        }

        result.Sort();
        return result;
    }
}

/// <summary>收集一次讀取的比對結果，最後壓成一行結論。</summary>
internal sealed class SquadronVerdict
{
    private readonly List<string> ok = [];
    private readonly List<string> bad = [];

    public int OkCount => ok.Count;

    public IReadOnlyList<string> Bad => bad;

    public void Pass(string what) => ok.Add(what);

    public void Fail(string what) => bad.Add(what);

    public void Equal(string what, long expected, long actual)
    {
        if (expected == actual) ok.Add($"{what}={actual}");
        else bad.Add($"{what} 預期 {expected}、實際 {actual}");
    }

    public string Format(string title) =>
        bad.Count == 0
            ? $"{title}：全部符合（{ok.Count} 項）：{string.Join("、", ok)}"
            : $"{title}：不符 {bad.Count} 項：{string.Join("；", bad)}｜符合 {ok.Count} 項：{string.Join("、", ok)}";
}

/// <remarks>遊戲把在隊的人從第 0 格起連續寫、旗標至少帶 bit0；不足 4 人時只把其餘格的旗標寫 0，
/// 那些格的其他 6 個值是上一次留下的殘值，不能算進隊伍。</remarks>
internal readonly record struct SquadronNaMember(int Flags, int Icon, int Level, int LevelState, int P, int M, int T)
{
    public bool Filled => Flags != 0;
}

/// <summary>NumberArray 113（GcArmyExpedition）的一次快照。</summary>
internal sealed class SquadronExpeditionNa
{
    public int Size;
    public int[] Raw = [];
    public uint MissionId;
    public int LevelBlocked, Experience, RewardItem, RewardValue, RewardQuantity, Seals, Tier, Refresh;
    public readonly int[] Need = new int[3];
    public readonly int[] Total = new int[3];
    public readonly SquadronNaMember[] Members = new SquadronNaMember[4];

    /// <summary>在隊的人（旗標非 0 的格）。</summary>
    public IEnumerable<SquadronNaMember> InTeam => Members.Where(m => m.Filled);

    /// <summary>在隊的人都在空格之前（遊戲的寫法）；不成立時推得的訓練值不可信。</summary>
    public bool InTeamContiguous => Members.SkipWhile(m => m.Filled).All(m => !m.Filled);

    /// <summary>隊伍合計減掉在隊各人的三項（含吉兆）＝推得的訓練值。</summary>
    public (int P, int M, int T) Training =>
        (Total[0] - InTeam.Sum(m => m.P), Total[1] - InTeam.Sum(m => m.M), Total[2] - InTeam.Sum(m => m.T));
}

internal sealed class SquadronExpeditionList
{
    public int Tab = -1, TabCount = -1, Rows = -1;
    public readonly List<(uint Id, bool Available)> Entries = [];
}

internal readonly record struct SquadronListMember(int Flags, int Icon, int Level, long LevelState, int P, int M, int T)
{
    public bool Selected => (Flags & 0x2) != 0;
}

internal sealed class SquadronTrainingRead
{
    public int Mode = -1, Cap = -1;

    /// <summary>[13]..[18]：目前／預覽 交錯排列（體能、心智、戰術）。</summary>
    public readonly int[] Values = new int[6];

    public bool Complete;

    public (int P, int M, int T) Current => (Values[0], Values[2], Values[4]);
}

internal static unsafe class SquadronDiag
{
    public const int TypeUndefined = 0x0;
    public const int TypeBool = 0x2;
    public const int TypeInt = 0x3;
    public const int TypeUInt = 0x5;

    /// <summary>CS <c>AtkValue</c>：大小 0x10，型別在 +0x0（4 bytes），聯合欄在 +0x8。</summary>
    public const int AtkValueStride = 0x10;

    public const int MaxCells = 512;
    public const int ExpeditionNumberArray = 113;
    public const int NumberArrayReadLength = 64;
    public const int ExpeditionNaMinSize = 44;
    public const int ExpeditionCells = 216;
    public const int MemberListCells = 133;
    public const int TrainingCells = 80;
    public const int MaxListRows = 50;
    public const int MaxMissionId = 34;
    public static readonly int[] TrainingCaps = [120, 200, 280, 400];

    public static bool IsPlausiblePointer(nint p) => (ulong)p > 0x10000 && (ulong)p <= 0x00007FFFFFFFFFFF;

    #region 原生讀取（只有這兩支碰指標）

    /// <summary>讀 <c>min(count, dst.Length)</c> 格；回實際讀了幾格。</summary>
    /// <remarks>呼叫端保證 <paramref name="values"/> 是本幀、已判過 ready 的窗的 AtkValues，<paramref name="count"/> 是它的 AtkValuesCount。</remarks>
    public static int ReadCells(byte* values, int count, Span<SquadronCell> dst)
    {
        var n = Math.Min(Math.Max(count, 0), dst.Length);
        for (var i = 0; i < n; i++)
        {
            var cell = values + (long)i * AtkValueStride;
            var type = *(int*)cell;
            dst[i] = type switch
            {
                TypeInt => new SquadronCell(type, true, *(int*)(cell + 8)),
                TypeUInt => new SquadronCell(type, true, *(uint*)(cell + 8)),
                TypeBool => new SquadronCell(type, true, *(cell + 8)),
                _ => new SquadronCell(type, false, 0),
            };
        }

        return n;
    }

    /// <summary>抄 <c>min(size, dst.Length)</c> 個整數；<paramref name="size"/> 是陣列自己報的 Size。</summary>
    public static int CopyInts(int* src, int size, Span<int> dst)
    {
        var n = Math.Min(Math.Max(size, 0), dst.Length);
        for (var i = 0; i < n; i++) dst[i] = src[i];
        return n;
    }

    #endregion

    #region 公式

    /// <summary>成功率：底＋每項達標加成＋三項全達標加成，上限 100；比較是 16 位元無號的 &gt;=。</summary>
    public static int Percent(SquadronMission m, int v, int p, int mm, int t)
    {
        var x = m.Variants[v];
        var pct = m.PercentBase;
        var met = 0;
        if ((ushort)p >= x.Physical) { pct += x.PctPhysical; met++; }
        if ((ushort)mm >= x.Mental) { pct += x.PctMental; met++; }
        if ((ushort)t >= x.Tactical) { pct += x.PctTactical; met++; }
        if (met == 3) pct += x.PctAllMet;
        return Math.Min(pct, 100);
    }

    public static int Tier(int percent) => percent >= 100 ? 4 : percent >= 60 ? 3 : percent >= 40 ? 2 : percent >= 20 ? 1 : 0;

    public static List<int> MatchVariants(SquadronMission m, int p, int mm, int t)
    {
        var result = new List<int>();
        for (var v = 0; v < m.Variants.Length; v++)
        {
            var x = m.Variants[v];
            if (x.Physical == p && x.Mental == mm && x.Tactical == t) result.Add(v);
        }

        return result;
    }

    #endregion

    #region 型別摘要

    public static string TypeName(int t) => t switch
    {
        0x0 => "Undefined",
        0x1 => "Null",
        0x2 => "Bool",
        0x3 => "Int",
        0x4 => "Int64",
        0x5 => "UInt",
        0x6 => "UInt64",
        0x7 => "Float",
        0x8 => "String",
        0x9 => "WideString",
        0xA => "String8",
        0xB => "Vector",
        0xC => "Pointer",
        0xD => "AtkValues",
        0x28 => "ManagedString",
        0x2B => "ManagedVector",
        _ => $"0x{t:X}",
    };

    private static char TypeLetter(int t) => t switch
    {
        0x0 => '_',
        0x1 => 'N',
        0x2 => 'B',
        0x3 => 'I',
        0x5 => 'U',
        0x7 => 'F',
        0x8 or 0x9 or 0xA or 0x28 => 'S',
        _ => '?',
    };

    public const string TypeLegend = "_=Undefined N=Null B=Bool I=Int U=UInt F=Float S=字串 ?=其他";

    public static string TypeHistogram(ReadOnlySpan<SquadronCell> cells)
    {
        var counts = new SortedDictionary<int, int>();
        foreach (var c in cells) counts[c.Type] = counts.GetValueOrDefault(c.Type) + 1;
        return string.Join(" ", counts.Select(kv => $"{TypeName(kv.Key)}×{kv.Value}"));
    }

    /// <summary>型別序列的連續段壓縮，例：<c>I3 _2 B1</c>。</summary>
    public static string TypeRuns(ReadOnlySpan<SquadronCell> cells)
    {
        var sb = new StringBuilder();
        var i = 0;
        while (i < cells.Length)
        {
            var letter = TypeLetter(cells[i].Type);
            var j = i;
            while (j < cells.Length && TypeLetter(cells[j].Type) == letter) j++;
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(letter).Append(j - i);
            i = j;
        }

        return sb.ToString();
    }

    public static ulong Hash(ReadOnlySpan<int> values, ulong h = 14695981039346656037UL)
    {
        foreach (var v in values)
        {
            h ^= (uint)v;
            h *= 1099511628211UL;
        }

        return h;
    }

    public static ulong Hash(ReadOnlySpan<SquadronCell> cells, ulong h = 14695981039346656037UL)
    {
        foreach (var c in cells)
        {
            h = (h ^ (uint)c.Type) * 1099511628211UL;
            h = (h ^ (ulong)c.Value) * 1099511628211UL;
        }

        return h;
    }

    #endregion

    #region 格子讀取輔助

    /// <summary>取第 <paramref name="i"/> 格的數值；型別不是 <paramref name="want"/> 記進 <paramref name="typeBad"/>，非數值型別不讀。</summary>
    private static bool Want(ReadOnlySpan<SquadronCell> c, int i, int want, List<string> typeBad, out long value)
    {
        value = 0;
        if (i < 0 || i >= c.Length)
        {
            typeBad.Add($"[{i}] 超出格數 {c.Length}");
            return false;
        }

        var cell = c[i];
        if (cell.Type != want) typeBad.Add($"[{i}] 預期 {TypeName(want)}、實際 {TypeName(cell.Type)}");
        if (!cell.HasValue) return false;

        value = cell.Value;
        return true;
    }

    private static void SummarizeTypes(SquadronVerdict v, int checkedCells, List<string> typeBad)
    {
        if (typeBad.Count == 0)
        {
            v.Pass($"型別 {checkedCells} 格全對");
            return;
        }

        var shown = string.Join("、", typeBad.Take(6));
        v.Fail($"型別 {typeBad.Count} 處不符：{shown}{(typeBad.Count > 6 ? $"…另 {typeBad.Count - 6} 處" : string.Empty)}");
    }

    private static void CompareTraining(string what, (int P, int M, int T) actual, (int P, int M, int T)? expected, SquadronVerdict v)
    {
        if (expected is not { } e) return;

        if (e == actual)
            v.Pass($"{what} {actual.P}/{actual.M}/{actual.T}");
        else
            v.Fail($"{what} 預期 {e.P}/{e.M}/{e.T}、實際 {actual.P}/{actual.M}/{actual.T}（之間做過訓練、或全隊吉兆只算進合計時會不同）");
    }

    #endregion

    #region 分隊任務清單（GcArmyExpedition）

    /// <param name="firstRead">本次開窗第一次讀：切分頁後 [1]～[5] 會變 Undefined，只有第一次要求 [1] 是 Int。</param>
    public static SquadronExpeditionList CheckExpeditionList(
        ReadOnlySpan<SquadronCell> c, SquadronTables t, bool firstRead, SquadronVerdict v, out string detail)
    {
        var r = new SquadronExpeditionList();
        var typeBad = new List<string>();
        var checkedCells = 0;
        v.Equal("格數", ExpeditionCells, c.Length);

        if (Want(c, 0, TypeInt, typeBad, out var tab)) r.Tab = (int)tab;
        checkedCells++;

        var tabNote = string.Empty;
        if (firstRead)
        {
            if (Want(c, 1, TypeInt, typeBad, out var tabs)) r.TabCount = (int)tabs;
            checkedCells++;
        }
        else if (c.Length > 1)
        {
            tabNote = $"（[1] 現在是 {TypeName(c[1].Type)}）";
        }

        if (Want(c, 6, TypeInt, typeBad, out var rows)) r.Rows = (int)rows;
        checkedCells++;

        var rowBad = new List<string>();
        if (r.Rows < 0 || r.Rows > MaxListRows || (r.Rows > 0 && 9 + 4 * (r.Rows - 1) >= c.Length))
        {
            v.Fail($"[6]列數={r.Rows} 超出範圍（格數 {c.Length}、上限 {MaxListRows}），不讀各列");
        }
        else
        {
            for (var i = 0; i < r.Rows; i++)
            {
                var hasAvail = Want(c, 8 + 4 * i, TypeBool, typeBad, out var avail);
                var hasId = Want(c, 9 + 4 * i, TypeInt, typeBad, out var id);
                checkedCells += 2;
                if (!hasId) continue;

                r.Entries.Add(((uint)id, hasAvail && avail != 0));
                if (id < 1 || id > MaxMissionId)
                {
                    rowBad.Add($"第 {i} 列 id={id} 不在 1..{MaxMissionId}");
                    continue;
                }

                if (!t.HasMissions) continue;
                if (!t.Missions.TryGetValue((uint)id, out var m))
                    rowBad.Add($"第 {i} 列 id={id} 不在表內");
                else if (r.Tab >= 0 && m.Type != r.Tab + 1)
                    rowBad.Add($"第 {i} 列 #{id} 表類型 {m.Type}、分頁 {r.Tab}（預期類型＝分頁+1）");
            }

            if (rowBad.Count == 0)
                v.Pass(t.HasMissions ? $"{r.Entries.Count} 列 id 都在表內且類型＝分頁+1" : $"{r.Entries.Count} 列 id 都在 1..{MaxMissionId}（表讀不到，沒比類型）");
            else
                v.Fail($"清單列 {rowBad.Count} 處不符：{string.Join("、", rowBad.Take(5))}{(rowBad.Count > 5 ? "…" : string.Empty)}");
        }

        SummarizeTypes(v, checkedCells, typeBad);

        var list = string.Join(" ", r.Entries.Select(e => $"#{e.Id}{(e.Available ? string.Empty : "(不可)")}"));
        detail = $"格數 {c.Length}；型別 {TypeHistogram(c)}；[0]分頁={r.Tab} [1]分頁數={(firstRead ? r.TabCount.ToString() : "不讀" + tabNote)} [6]列數={r.Rows}；列 {list}";
        return r;
    }

    /// <summary>Size 不到 <see cref="ExpeditionNaMinSize"/> 就只記一句並回 <c>null</c>，一格都不讀。</summary>
    public static SquadronExpeditionNa? ParseExpeditionNa(ReadOnlySpan<int> na, int size, SquadronVerdict v)
    {
        if (size < ExpeditionNaMinSize || na.Length < ExpeditionNaMinSize)
        {
            v.Fail($"NumberArray {ExpeditionNumberArray} Size 只有 {size}（需要 ≥{ExpeditionNaMinSize}），不讀");
            return null;
        }

        v.Pass($"NA Size={size}");
        var n = new SquadronExpeditionNa
        {
            Size = size,
            Raw = na.ToArray(),
            MissionId = (uint)na[1],
            LevelBlocked = na[2],
            Experience = na[3],
            RewardItem = na[4],
            RewardValue = na[5],
            RewardQuantity = na[6],
            Seals = na[7],
            Tier = na[14],
            Refresh = na[15],
        };
        for (var i = 0; i < 3; i++)
        {
            n.Need[i] = na[8 + i];
            n.Total[i] = na[11 + i];
        }

        for (var k = 0; k < 4; k++)
        {
            var b = 16 + 7 * k;
            n.Members[k] = new SquadronNaMember(na[b], na[b + 1], na[b + 2], na[b + 3], na[b + 4], na[b + 5], na[b + 6]);
        }

        return n;
    }

    /// <param name="sessionVariants">本次開窗至今各列推得的版本交集（第一次傳 null）；各列推出的版本必須相同。</param>
    /// <param name="trainingWindow">同一角色在訓練窗讀到的目前訓練值（沒讀過傳 null）。</param>
    public static void CheckExpeditionNa(
        SquadronExpeditionNa n, SquadronTables t, SquadronExpeditionList? list, ref HashSet<int>? sessionVariants,
        (int P, int M, int T)? trainingWindow, SquadronVerdict v)
    {
        var maxLevel = n.InTeam.Select(m => m.Level).DefaultIfEmpty(0).Max();

        if (list != null && list.Entries.Count > 0)
        {
            var row = list.Entries.FindIndex(e => e.Id == n.MissionId);
            if (row >= 0) v.Pass($"[1]=#{n.MissionId} 是清單第 {row} 列");
            else v.Fail($"[1]=#{n.MissionId} 不在目前清單（{string.Join(" ", list.Entries.Select(e => e.Id))}）");
        }

        if (!t.HasMissions)
        {
            v.Fail("讀不到 GcArmyExpedition 表，需求／成功率無法比");
        }
        else if (!t.Missions.TryGetValue(n.MissionId, out var m))
        {
            v.Fail($"[1]={n.MissionId} 不在 GcArmyExpedition 表內");
        }
        else
        {
            v.Equal("[2]等級不足", maxLevel < m.Level ? 1 : 0, n.LevelBlocked);
            v.Equal("[3]經驗值", m.Experience, n.Experience);
            v.Equal("[7]需要軍票", m.Seals, n.Seals);

            var cands = MatchVariants(m, n.Need[0], n.Need[1], n.Need[2]);
            if (cands.Count == 0)
            {
                var all = string.Join(" ", m.Variants.Select((x, i) => $"v{i}={x.Physical}/{x.Mental}/{x.Tactical}"));
                v.Fail($"[8..10]需求 {n.Need[0]}/{n.Need[1]}/{n.Need[2]} 不等於表中任一版本（{all}）");
            }
            else
            {
                v.Pass($"[8..10]需求＝表第 {string.Join(",", cands)} 版");

                var before = sessionVariants;
                var now = before == null ? new HashSet<int>(cands) : new HashSet<int>(before.Intersect(cands));
                if (now.Count == 0)
                {
                    v.Fail($"每日版本前後不一致：先前 v∈{{{string.Join(",", before!.Order())}}}、這列 v∈{{{string.Join(",", cands)}}}");
                }
                else
                {
                    if (before != null) v.Pass($"每日版本與先前一致 v∈{{{string.Join(",", now.Order())}}}");
                    sessionVariants = now;
                }

                var tiers = cands.Select(x => Tier(Percent(m, x, n.Total[0], n.Total[1], n.Total[2]))).Distinct().ToList();
                if (tiers.Count == 1)
                    v.Equal($"[14]成功率分級（{Percent(m, cands[0], n.Total[0], n.Total[1], n.Total[2])}%）", tiers[0], n.Tier);
                else if (tiers.Contains(n.Tier))
                    v.Pass($"[14]成功率分級={n.Tier}（版本不唯一，可能值 {string.Join(",", tiers)}）");
                else
                    v.Fail($"[14]成功率分級 預期 {string.Join("或", tiers)}、實際 {n.Tier}");

                var rewards = cands.Select(x => (m.Variants[x].RewardItem, m.Variants[x].RewardQuantity)).Distinct().ToList();
                if (rewards.Count == 1)
                {
                    v.Equal("[4]獎勵道具", rewards[0].RewardItem, n.RewardItem);

                    // 沒有獎勵道具時遊戲只寫 [4]=0，[5][6] 留著前一個任務的值，比了只會誤報。
                    if (rewards[0].RewardItem != 0)
                        v.Equal("[6]獎勵數量", rewards[0].RewardQuantity, n.RewardQuantity);
                }
            }
        }

        var inTeam = n.InTeam.Count();
        if (n.InTeamContiguous) v.Pass($"在隊 {inTeam} 人");
        else v.Fail($"在隊的人不連續（旗標 {string.Join(",", n.Members.Select(m => $"0x{m.Flags:X}"))}）");

        if (t.HasGrow)
        {
            var memberBad = new List<string>();
            var compared = 0;
            for (var k = 0; k < 4; k++)
            {
                var mem = n.Members[k];
                if (!mem.Filled || mem.Level <= 0) continue;
                if (t.MinLevelSum(mem.Level) is not { } floor) continue;

                compared++;
                var sum = mem.P + mem.M + mem.T;
                if (sum < floor) memberBad.Add($"第 {k} 名 Lv{mem.Level} 三項合計 {sum} 小於表中同級 {floor}");
            }

            if (memberBad.Count == 0) v.Pass($"{compared} 名隊員三項合計 ≥ 表中同級（吉兆只會加）");
            else v.Fail(string.Join("、", memberBad));
        }

        var tr = n.Training;
        if (tr.P >= 0 && tr.M >= 0 && tr.T >= 0) v.Pass($"推得訓練值 {tr.P}/{tr.M}/{tr.T} ≥0");
        else v.Fail($"推得訓練值 {tr.P}/{tr.M}/{tr.T} 有負數（[11..13] 不是「訓練值＋在隊各人三項」）");

        CompareTraining("推得訓練值＝訓練窗", tr, trainingWindow, v);
    }

    public static string DescribeExpeditionNa(SquadronExpeditionNa n, SquadronTables t, int? firstFrameMissionId)
    {
        var name = t.Missions.TryGetValue(n.MissionId, out var m) ? $"「{m.Name}」Lv{m.Level}" : "（表內沒有）";
        var members = string.Join("｜", n.Members.Select((x, k) => x.Filled
            ? $"{k}:旗標0x{x.Flags:X} 圖示{x.Icon} Lv{x.Level} 狀態{x.LevelState} {x.P}/{x.M}/{x.T}"
            : $"{k}:空（旗標 0，其餘是殘值）"));
        var reward = n.RewardItem != 0
            ? $"{n.RewardItem}/{n.RewardValue}/×{n.RewardQuantity}"
            : $"無（[5][6] 殘值 {n.RewardValue}/×{n.RewardQuantity}）";
        var tr = n.Training;
        return $"Size={n.Size}；開窗第一幀 [1]={(firstFrameMissionId?.ToString() ?? "沒讀到")}；" +
               $"[0]軍票={n.Raw[0]} [1]任務=#{n.MissionId}{name} [2]等級不足={n.LevelBlocked} [3]經驗={n.Experience} " +
               $"[4..6]獎勵={reward} [7]需軍票={n.Seals} " +
               $"[8..10]需求={n.Need[0]}/{n.Need[1]}/{n.Need[2]} [11..13]合計={n.Total[0]}/{n.Total[1]}/{n.Total[2]} " +
               $"[14]分級={n.Tier} [15]={n.Refresh}；隊員 {members}；推得訓練值={tr.P}/{tr.M}/{tr.T}；" +
               $"原始[0..{n.Raw.Length - 1}]={string.Join(",", n.Raw)}";
    }

    #endregion

    #region 編組名單（GcArmyMemberList）

    public static List<SquadronListMember> CheckMemberList(
        ReadOnlySpan<SquadronCell> c, SquadronTables t, SquadronExpeditionNa? na, Func<uint, string> className,
        SquadronVerdict v, out string detail)
    {
        var result = new List<SquadronListMember>();
        var typeBad = new List<string>();
        var checkedCells = 1;
        v.Equal("格數", MemberListCells, c.Length);

        // 人數格遊戲是先把型別改成 UInt 再寫；其餘數值格是 Int。
        if (!Want(c, 4, TypeUInt, typeBad, out var count) || count < 0 || 5 + 15 * count > c.Length)
        {
            SummarizeTypes(v, checkedCells, typeBad);
            v.Fail($"[4]人數={count} 讀不到或超出格數 {c.Length}，不讀各人");
            detail = $"格數 {c.Length}；型別 {TypeHistogram(c)}；[4]人數={count}（不讀）";
            return result;
        }

        var parts = new List<string>();
        var growBad = new List<string>();
        for (var k = 0; k < count; k++)
        {
            var b = 15 * k;
            Want(c, 5 + b, TypeInt, typeBad, out var flags);
            Want(c, 9 + b, TypeInt, typeBad, out var icon);
            Want(c, 10 + b, TypeInt, typeBad, out var level);
            Want(c, 11 + b, TypeUInt, typeBad, out var state);
            Want(c, 13 + b, TypeInt, typeBad, out var p);
            Want(c, 14 + b, TypeInt, typeBad, out var m);
            Want(c, 15 + b, TypeInt, typeBad, out var tt);
            checkedCells += 7;

            var mem = new SquadronListMember((int)flags, (int)icon, (int)level, state, (int)p, (int)m, (int)tt);
            result.Add(mem);

            var cls = string.Empty;
            if (t.HasGrow)
            {
                var hits = t.ClassesWithTriple(mem.Level, mem.P, mem.M, mem.T);
                if (hits.Count > 0)
                    cls = string.Join("/", hits.Select(className));
                else
                    growBad.Add($"第 {k} 人 Lv{mem.Level} {mem.P}/{mem.M}/{mem.T} 不在成長表該等級（同級最小合計 {t.MinLevelSum(mem.Level)?.ToString() ?? "?"}）");
            }

            parts.Add($"{k}:旗標0x{mem.Flags:X}{(mem.Selected ? "(選中)" : string.Empty)} 圖示{mem.Icon} Lv{mem.Level} 狀態{mem.LevelState} {mem.P}/{mem.M}/{mem.T}{(cls.Length > 0 ? " " + cls : string.Empty)}");
        }

        SummarizeTypes(v, checkedCells, typeBad);

        if (t.HasGrow)
        {
            if (growBad.Count == 0) v.Pass($"{count} 人基礎三項＝成長表（職業×等級）");
            else v.Fail(string.Join("、", growBad));
        }

        var selected = result.Where(x => x.Selected).ToList();
        if (selected.Count <= 4) v.Pass($"已選中（旗標 0x2）{selected.Count} 人 ≤4");
        else v.Fail($"已選中（旗標 0x2）{selected.Count} 人，超過 4");

        var naTeam = na?.InTeam.ToList();
        if (naTeam != null && selected.Count > 0 && selected.Count == naTeam.Count)
        {
            var n = selected.Count;
            var listLevels = string.Join(",", selected.Select(x => x.Level).Order());
            var naLevels = string.Join(",", naTeam.Select(x => x.Level).Order());
            if (listLevels == naLevels) v.Pass($"選中 {n} 人等級＝NA 隊員等級（{listLevels}）");
            else v.Fail($"選中 {n} 人等級 {listLevels}、NA 隊員等級 {naLevels}");

            var baseSum = (selected.Sum(x => x.P), selected.Sum(x => x.M), selected.Sum(x => x.T));
            var naSum = (naTeam.Sum(x => x.P), naTeam.Sum(x => x.M), naTeam.Sum(x => x.T));
            if (baseSum.Item1 <= naSum.Item1 && baseSum.Item2 <= naSum.Item2 && baseSum.Item3 <= naSum.Item3)
                v.Pass($"選中 {n} 人基礎合計 {baseSum.Item1}/{baseSum.Item2}/{baseSum.Item3} ≤ NA 隊員合計 {naSum.Item1}/{naSum.Item2}/{naSum.Item3}");
            else
                v.Fail($"選中 {n} 人基礎合計 {baseSum.Item1}/{baseSum.Item2}/{baseSum.Item3} 大於 NA 隊員合計 {naSum.Item1}/{naSum.Item2}/{naSum.Item3}");
        }
        else if (naTeam != null && selected.Count > 0)
        {
            v.Fail($"選中 {selected.Count} 人、NA 在隊 {naTeam.Count} 人");
        }

        detail = $"格數 {c.Length}；型別 {TypeHistogram(c)}；[4]人數={count}；{string.Join("｜", parts)}";
        return result;
    }

    #endregion

    #region 訓練（GcArmyTraining）

    public static SquadronTrainingRead CheckTraining(
        ReadOnlySpan<SquadronCell> c, (int P, int M, int T)? naTraining, SquadronVerdict v, out string detail)
    {
        var r = new SquadronTrainingRead();
        var typeBad = new List<string>();
        v.Equal("格數", TrainingCells, c.Length);

        var ok = Want(c, 0, TypeInt, typeBad, out var mode);
        r.Mode = (int)mode;
        for (var i = 0; i < 6; i++)
        {
            ok &= Want(c, 13 + i, TypeInt, typeBad, out var x);
            r.Values[i] = (int)x;
        }

        ok &= Want(c, 21, TypeInt, typeBad, out var cap);
        r.Cap = (int)cap;
        r.Complete = ok;
        SummarizeTypes(v, 8, typeBad);

        if (ok)
        {
            if (r.Mode == 0)
            {
                v.Equal("[14]預覽體能＝[13]", r.Values[0], r.Values[1]);
                v.Equal("[16]預覽心智＝[15]", r.Values[2], r.Values[3]);
                v.Equal("[18]預覽戰術＝[17]", r.Values[4], r.Values[5]);
            }

            if (Array.IndexOf(TrainingCaps, r.Cap) >= 0) v.Pass($"[21]上限={r.Cap}∈{{{string.Join(",", TrainingCaps)}}}");
            else v.Fail($"[21]上限={r.Cap} 不在 {{{string.Join(",", TrainingCaps)}}}");

            var cur = r.Current;
            var sum = cur.P + cur.M + cur.T;
            if (sum <= r.Cap) v.Pass($"目前三項合計 {sum} ≤ 上限");
            else v.Fail($"目前三項合計 {sum} 超過上限 {r.Cap}");

            CompareTraining("訓練窗＝NA 推得訓練值", cur, naTraining, v);
        }

        detail = $"格數 {c.Length}；型別 {TypeHistogram(c)}；[0]模式={r.Mode} " +
                 $"[13..18]體能 {r.Values[0]}→{r.Values[1]}、心智 {r.Values[2]}→{r.Values[3]}、戰術 {r.Values[4]}→{r.Values[5]} [21]上限={r.Cap}";
        return r;
    }

    #endregion
}

/// <summary>診斷記錄的總量上限：滾動視窗內最多幾筆（每筆＝細節＋結論幾行）。</summary>
internal sealed class SquadronDiagBudget
{
    public const int MaxRecords = 60;
    public const long WindowMs = 6 * 60 * 60 * 1000L;

    private long windowStart = long.MinValue;
    private int used;
    private bool exhaustedReported;

    /// <returns>可以記；用完時第一次回 <c>false</c> 並把 <paramref name="reportExhausted"/> 設成 true（只一次）。</returns>
    public bool TryTake(long nowMs, out bool reportExhausted)
    {
        reportExhausted = false;
        if (windowStart == long.MinValue || nowMs - windowStart >= WindowMs)
        {
            windowStart = nowMs;
            used = 0;
            exhaustedReported = false;
        }

        if (used < MaxRecords)
        {
            used++;
            return true;
        }

        if (!exhaustedReported)
        {
            exhaustedReported = true;
            reportExhausted = true;
        }

        return false;
    }
}

/// <summary>一扇窗的記錄閘門：開窗後先等數值穩定，同一組值只記一次，每次開窗有上限。</summary>
internal sealed class SquadronDiagGate(int maxPerOpen)
{
    /// <summary>開窗後先等這麼久再讀（遊戲在開窗後 1～2 幀才把新值寫進 NumberArray）。</summary>
    public const long SettleMs = 500;

    /// <summary>同一組值要連續看到這麼久才記（使用者連點列時不逐列洗版）。</summary>
    public const long StableMs = 300;

    /// <summary>開窗這麼久都沒有一組值穩定下來（某格每幀在變），就照當下的值強制記第一筆。</summary>
    public const long ForceFirstMs = 3000;

    private bool open;
    private long openedAt;
    private int loggedThisOpen;
    private ulong? lastLogged;
    private ulong pending;
    private bool hasPending;

    public bool IsOpen => open;

    /// <summary>本次開窗至今記了幾筆；0＝下一筆是開窗後第一筆。</summary>
    public int LoggedThisOpen => loggedThisOpen;

    /// <summary>剛通過的那一筆是值還在變、被 <see cref="ForceFirstMs"/> 強制記的。</summary>
    public bool LastForced { get; private set; }

    private long pendingSince;

    /// <returns>這一刻是不是剛開窗（呼叫端可趁這一幀抄下開窗瞬間的值）。</returns>
    public bool MarkReady(long nowMs)
    {
        if (open) return false;

        open = true;
        openedAt = nowMs;
        loggedThisOpen = 0;
        lastLogged = null;
        hasPending = false;
        return true;
    }

    public void MarkClosed() => open = false;

    public bool Settled(long nowMs) => open && nowMs - openedAt >= SettleMs;

    public bool Offer(ulong key, long nowMs, SquadronDiagBudget budget, out bool reportExhausted)
    {
        reportExhausted = false;
        if (!open) return false;

        if (!hasPending || key != pending)
        {
            pending = key;
            pendingSince = nowMs;
            hasPending = true;
        }

        var stable = nowMs - pendingSince >= StableMs;
        var forced = !stable && loggedThisOpen == 0 && nowMs - openedAt >= ForceFirstMs;
        if (!(stable || forced) || lastLogged == key) return false;

        lastLogged = key;
        LastForced = forced;
        if (loggedThisOpen >= maxPerOpen) return false;
        if (!budget.TryTake(nowMs, out reportExhausted)) return false;

        loggedThisOpen++;
        return true;
    }
}

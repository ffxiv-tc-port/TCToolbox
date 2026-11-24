using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TCToolbox.Core;

// 本檔假設與契約：
// - 純資料與純函式，不碰 Dalamud、不碰原生記憶體（離線探針直接編這一份）。
// - 版面＝台服 7.30 前線結算處理函式讀的那份緩衝區；人數是 +0x24 的一個位元組，玩家從 +0x58 起每 0x60 一筆。
// - 語意沒驗過的欄位一律留原始值（Unknown*／HeaderHex），名稱只是推測。

/// <summary>前線結算緩衝區的版面常數與讀取界線。</summary>
public static class FrontlineResultLayout
{
    public const int MatchLengthOffset = 0x10;
    public const int Unknown14Offset = 0x14;
    public const int WolfMarksOffset = 0x16;
    public const int SeriesXpOffset = 0x18;
    public const int Unknown1AOffset = 0x1A;
    public const int JobXpOffset = 0x1C;
    public const int Unknown20Offset = 0x20;
    public const int PlayerCountOffset = 0x24;
    public const int TeamStatsOffset = 0x28;
    public const int TeamStride = 0x0E;
    public const int TeamCount = 3;
    public const int HeaderSize = 0x58;
    public const int PlayerStride = 0x60;
    public const int MaxPlayers = 72;
    public const int NameOffset = 0x32;
    public const int NameLength = 32;

    /// <summary>人數 0 或超過 72 都不讀（後者＝版面已變或命中了別的函式）。</summary>
    public static bool IsPlausibleCount(int count) => count is >= 1 and <= MaxPlayers;

    /// <summary>人數合理時要複製的位元組數；上限 0x58＋72×0x60。</summary>
    public static int BufferLength(int count) => HeaderSize + Math.Clamp(count, 0, MaxPlayers) * PlayerStride;
}

/// <summary>擷取當下由框架執行緒抄下的情境（解析在背景跑，不能再回頭問遊戲）。</summary>
public sealed record FrontlineCaptureContext(
    DateTimeOffset CapturedLocal,
    uint TerritoryId,
    uint ContentFinderConditionId,
    string MapName,
    ulong LocalContentId,
    string LocalName,
    uint LocalWorldId,
    string PluginVersion);

public sealed class FrontlineHeader
{
    public uint MatchLengthSeconds { get; set; }
    public ushort Unknown14 { get; set; }
    public ushort WolfMarks { get; set; }
    public ushort SeriesXp { get; set; }
    public ushort Unknown1A { get; set; }
    public uint JobXp { get; set; }
    public byte Unknown20 { get; set; }
    public byte Unknown21 { get; set; }
    public byte Unknown22 { get; set; }
    public byte Unknown23 { get; set; }
    public byte PlayerCount { get; set; }

    /// <summary>標頭 0x00..0x58 原樣（十六進位）：不含任何玩家資料，留著日後對欄位。</summary>
    public string HeaderHex { get; set; } = string.Empty;
}

public sealed class FrontlineTeamResult
{
    /// <summary>0＝黑渦團、1＝雙蛇黨、2＝不滅隊。</summary>
    public int Team { get; set; }

    /// <summary>原始名次值（0 起算，0＝第一名）。</summary>
    public int Placement { get; set; }

    public int OccupationPoints { get; set; }
    public int KillPoints { get; set; }
    public int DronePoints { get; set; }
    public int IcePoints { get; set; }
    public int TotalPoints { get; set; }
    public int KoPointLosses { get; set; }
}

public sealed class FrontlinePlayerResult
{
    public int Index { get; set; }
    public string Name { get; set; } = string.Empty;
    public int WorldId { get; set; }
    public int ClassJobId { get; set; }
    public int Team { get; set; }
    public int Alliance { get; set; }
    public int Kills { get; set; }
    public int Deaths { get; set; }
    public int Assists { get; set; }
    public uint DamageDealt { get; set; }
    public uint DamageToOther { get; set; }
    public uint DamageTaken { get; set; }
    public uint HpRestored { get; set; }

    /// <summary>+0x20：推測＝被治療量；結算處理函式本身不讀它。</summary>
    public uint Unknown1 { get; set; }

    public uint Occupations { get; set; }

    /// <summary>+0x28 原始值。</summary>
    public ushort Unknown28 { get; set; }
}

/// <summary>一場前線的完整紀錄（就是寫進 JSON 的那份）。</summary>
public sealed class FrontlineMatchRecord
{
    public const int CurrentSchema = 1;

    public int Schema { get; set; } = CurrentSchema;
    public string PluginVersion { get; set; } = string.Empty;
    public DateTimeOffset CapturedLocal { get; set; }
    public uint TerritoryId { get; set; }
    public uint ContentFinderConditionId { get; set; }
    public string MapName { get; set; } = string.Empty;
    public FrontlineHeader Header { get; set; } = new();
    public List<FrontlineTeamResult> Teams { get; set; } = [];
    public List<FrontlinePlayerResult> Players { get; set; } = [];

    /// <summary>自己在 <see cref="Players"/> 的索引；-1＝找不到。</summary>
    public int SelfIndex { get; set; } = -1;

    /// <summary><c>ContentId</c>／<c>NameWorld</c>／<c>None</c>。</summary>
    public string SelfMatchedBy { get; set; } = "None";

    /// <summary>同場 <c>FrontlineRecord</c> 視窗的 AtkValues 旁檔（檔名；沒有＝null）。</summary>
    public string? AddonFile { get; set; }

    [JsonIgnore]
    public FrontlinePlayerResult? Self => SelfIndex >= 0 && SelfIndex < Players.Count ? Players[SelfIndex] : null;

    /// <summary>自己隊伍的名次（1 起算）；自己找不到或隊伍編號不合理＝null。</summary>
    [JsonIgnore]
    public int? SelfPlacement
    {
        get
        {
            if (Self is not { } self) return null;
            foreach (var team in Teams)
            {
                if (team.Team == self.Team) return team.Placement is >= 0 and < FrontlineResultLayout.TeamCount ? team.Placement + 1 : null;
            }

            return null;
        }
    }
}

public static class FrontlineResultParser
{
    /// <summary>解析一份已複製出來的結算緩衝區。</summary>
    /// <exception cref="ArgumentException">人數不合理或緩衝區比人數需要的短。</exception>
    public static FrontlineMatchRecord Parse(ReadOnlySpan<byte> raw, FrontlineCaptureContext ctx)
    {
        if (raw.Length <= FrontlineResultLayout.PlayerCountOffset)
            throw new ArgumentException($"緩衝區只有 {raw.Length} 位元組，讀不到人數。", nameof(raw));

        int count = raw[FrontlineResultLayout.PlayerCountOffset];
        if (!FrontlineResultLayout.IsPlausibleCount(count))
            throw new ArgumentException($"人數 {count} 不合理。", nameof(raw));

        var need = FrontlineResultLayout.BufferLength(count);
        if (raw.Length < need)
            throw new ArgumentException($"人數 {count} 需要 {need} 位元組，只有 {raw.Length}。", nameof(raw));

        var record = new FrontlineMatchRecord
        {
            PluginVersion = ctx.PluginVersion,
            CapturedLocal = ctx.CapturedLocal,
            TerritoryId = ctx.TerritoryId,
            ContentFinderConditionId = ctx.ContentFinderConditionId,
            MapName = ctx.MapName,
            Header = new FrontlineHeader
            {
                MatchLengthSeconds = U32(raw, FrontlineResultLayout.MatchLengthOffset),
                Unknown14 = U16(raw, FrontlineResultLayout.Unknown14Offset),
                WolfMarks = U16(raw, FrontlineResultLayout.WolfMarksOffset),
                SeriesXp = U16(raw, FrontlineResultLayout.SeriesXpOffset),
                Unknown1A = U16(raw, FrontlineResultLayout.Unknown1AOffset),
                JobXp = U32(raw, FrontlineResultLayout.JobXpOffset),
                Unknown20 = raw[0x20],
                Unknown21 = raw[0x21],
                Unknown22 = raw[0x22],
                Unknown23 = raw[0x23],
                PlayerCount = (byte)count,
                HeaderHex = Convert.ToHexString(raw[..FrontlineResultLayout.HeaderSize]),
            },
        };

        for (var t = 0; t < FrontlineResultLayout.TeamCount; t++)
        {
            var o = FrontlineResultLayout.TeamStatsOffset + t * FrontlineResultLayout.TeamStride;
            record.Teams.Add(new FrontlineTeamResult
            {
                Team = t,
                Placement = raw[o],
                OccupationPoints = U16(raw, o + 0x2),
                KillPoints = U16(raw, o + 0x4),
                DronePoints = U16(raw, o + 0x6),
                IcePoints = U16(raw, o + 0x8),
                TotalPoints = U16(raw, o + 0xA),
                KoPointLosses = U16(raw, o + 0xC),
            });
        }

        var byName = -1;
        for (var i = 0; i < count; i++)
        {
            var p = raw.Slice(FrontlineResultLayout.HeaderSize + i * FrontlineResultLayout.PlayerStride, FrontlineResultLayout.PlayerStride);
            var player = new FrontlinePlayerResult
            {
                Index = i,
                DamageDealt = U32(p, 0x10),
                DamageToOther = U32(p, 0x14),
                DamageTaken = U32(p, 0x18),
                HpRestored = U32(p, 0x1C),
                Unknown1 = U32(p, 0x20),
                Occupations = U32(p, 0x24),
                Unknown28 = U16(p, 0x28),
                WorldId = U16(p, 0x2A),
                ClassJobId = p[0x2C],
                Kills = p[0x2D],
                Deaths = p[0x2E],
                Team = p[0x2F],
                Alliance = p[0x30],
                Assists = p[0x31],
                Name = ReadName(p.Slice(FrontlineResultLayout.NameOffset, FrontlineResultLayout.NameLength)),
            };
            record.Players.Add(player);

            var contentId = BinaryPrimitives.ReadUInt64LittleEndian(p[0x08..]);
            if (record.SelfIndex < 0 && ctx.LocalContentId != 0 && contentId == ctx.LocalContentId)
            {
                record.SelfIndex = i;
                record.SelfMatchedBy = "ContentId";
            }

            if (byName < 0 && ctx.LocalName.Length > 0 && player.Name == ctx.LocalName && (uint)player.WorldId == ctx.LocalWorldId)
                byName = i;
        }

        if (record.SelfIndex < 0 && byName >= 0)
        {
            record.SelfIndex = byName;
            record.SelfMatchedBy = "NameWorld";
        }

        return record;
    }

    private static ushort U16(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadUInt16LittleEndian(s[o..]);

    private static uint U32(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadUInt32LittleEndian(s[o..]);

    private static string ReadName(ReadOnlySpan<byte> s)
    {
        var end = s.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end < 0 ? s : s[..end]);
    }
}

public static class FrontlineRecordJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static byte[] Serialize(FrontlineMatchRecord record) => JsonSerializer.SerializeToUtf8Bytes(record, Options);

    public static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);

    public static FrontlineMatchRecord? Deserialize(ReadOnlySpan<byte> utf8) => JsonSerializer.Deserialize<FrontlineMatchRecord>(utf8, Options);

    /// <summary>紀錄檔名主幹：<c>yyyyMMdd-HHmmss_&lt;territory&gt;</c>。</summary>
    public static string Stem(DateTimeOffset local, uint territoryId) =>
        $"{local.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}_{territoryId}";

    /// <summary>旁檔副檔名；報表載入時要排除。</summary>
    public const string AddonSuffix = ".addon.json";
}

/// <summary>隊伍名稱與顏色索引（官方繁中，取自 GrandCompany 表 1..3）。</summary>
public static class FrontlineTeams
{
    public static string Name(int team) => team switch
    {
        0 => "黑渦團",
        1 => "雙蛇黨",
        2 => "不滅隊",
        _ => $"隊伍{team}",
    };
}

/// <summary>每場一行的 log 摘要：不含任何角色名。</summary>
public static class FrontlineSummary
{
    public static string Line(FrontlineMatchRecord r, Func<int, string> jobAbbr, string fileName)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"前線結算：{(r.MapName.Length > 0 ? r.MapName : "?")}（地圖 {r.TerritoryId}）");
        sb.Append("　名次 ").Append(r.SelfPlacement is { } place ? $"{place}/3" : "?");
        sb.Append("　分數 ");
        sb.Append(string.Join("・", r.Teams.OrderBy(t => t.Team).Select(t => $"{FrontlineTeams.Name(t.Team)} {t.TotalPoints}")));
        if (r.Self is { } s)
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"　自己 {jobAbbr(s.ClassJobId)} K/D/A {s.Kills}/{s.Deaths}/{s.Assists} 傷害 {s.DamageDealt:N0} 承受 {s.DamageTaken:N0} 治療 {s.HpRestored:N0}");
        }
        else
        {
            sb.Append("　自己 ?（名單裡找不到）");
        }

        var len = r.Header.MatchLengthSeconds;
        sb.Append(CultureInfo.InvariantCulture, $"　時長 {len / 60}:{len % 60:00}　人數 {r.Header.PlayerCount}　檔案 {fileName}");
        return sb.ToString();
    }
}

/// <summary>依某個鍵（地圖、職業）彙總自己的成績。每個平均值帶自己的樣本數。</summary>
public sealed class FrontlineAggregateRow
{
    public string Key { get; init; } = string.Empty;

    /// <summary>落在這一列的場次（含自己找不到的）。</summary>
    public int Matches { get; set; }

    /// <summary>名次已知的場次。</summary>
    public int Placed { get; set; }

    public int FirstPlaces { get; set; }
    public long PlacementSum { get; set; }

    /// <summary>自己的計分列已知的場次。</summary>
    public int WithSelf { get; set; }

    public long Kills { get; set; }
    public long Deaths { get; set; }
    public long Assists { get; set; }
    public long Damage { get; set; }
    public long Taken { get; set; }
    public long Healed { get; set; }

    public double? FirstRate => Placed > 0 ? (double)FirstPlaces / Placed : null;
    public double? AvgPlacement => Placed > 0 ? (double)PlacementSum / Placed : null;
    public double? Avg(long sum) => WithSelf > 0 ? (double)sum / WithSelf : null;
}

public static class FrontlineStats
{
    /// <summary><paramref name="key"/> 回 null 的場次不計入任何一列。</summary>
    public static List<FrontlineAggregateRow> Aggregate(IEnumerable<FrontlineMatchRecord> records, Func<FrontlineMatchRecord, string?> key)
    {
        var rows = new Dictionary<string, FrontlineAggregateRow>(StringComparer.Ordinal);
        foreach (var r in records)
        {
            var k = key(r);
            if (k == null) continue;
            if (!rows.TryGetValue(k, out var row)) rows[k] = row = new FrontlineAggregateRow { Key = k };

            row.Matches++;
            if (r.SelfPlacement is { } place)
            {
                row.Placed++;
                row.PlacementSum += place;
                if (place == 1) row.FirstPlaces++;
            }

            if (r.Self is not { } s) continue;
            row.WithSelf++;
            row.Kills += s.Kills;
            row.Deaths += s.Deaths;
            row.Assists += s.Assists;
            row.Damage += s.DamageDealt;
            row.Taken += s.DamageTaken;
            row.Healed += s.HpRestored;
        }

        return rows.Values.OrderByDescending(r => r.Matches).ThenBy(r => r.Key, StringComparer.Ordinal).ToList();
    }
}

public static class FrontlineCsv
{
    public static string Matches(IEnumerable<FrontlineMatchRecord> records, Func<int, string> jobAbbr)
    {
        var sb = new StringBuilder();
        sb.Append("時間,地圖,地圖編號,副本編號,時長秒,人數,名次,黑渦團,雙蛇黨,不滅隊,自己職業,擊倒,死亡,協助,傷害,對非玩家傷害,承受,治療,占領\r\n");
        foreach (var r in records)
        {
            var s = r.Self;
            Row(sb,
                r.CapturedLocal.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), r.MapName, r.TerritoryId, r.ContentFinderConditionId,
                r.Header.MatchLengthSeconds, r.Header.PlayerCount, r.SelfPlacement?.ToString(CultureInfo.InvariantCulture) ?? "",
                TeamTotal(r, 0), TeamTotal(r, 1), TeamTotal(r, 2),
                s == null ? "" : jobAbbr(s.ClassJobId), s?.Kills, s?.Deaths, s?.Assists,
                s?.DamageDealt, s?.DamageToOther, s?.DamageTaken, s?.HpRestored, s?.Occupations);
        }

        return sb.ToString();
    }

    public static string Scoreboard(FrontlineMatchRecord r, Func<int, string> jobAbbr, Func<int, string> worldName)
    {
        var sb = new StringBuilder();
        sb.Append("自己,隊伍,小隊,名字,伺服器,職業,擊倒,死亡,協助,傷害,對非玩家傷害,承受,治療,未知1,占領\r\n");
        foreach (var p in r.Players)
        {
            Row(sb, p.Index == r.SelfIndex ? "是" : "", FrontlineTeams.Name(p.Team), p.Alliance, p.Name, worldName(p.WorldId), jobAbbr(p.ClassJobId),
                p.Kills, p.Deaths, p.Assists, p.DamageDealt, p.DamageToOther, p.DamageTaken, p.HpRestored, p.Unknown1, p.Occupations);
        }

        return sb.ToString();
    }

    private static string TeamTotal(FrontlineMatchRecord r, int team)
    {
        foreach (var t in r.Teams)
        {
            if (t.Team == team) return t.TotalPoints.ToString(CultureInfo.InvariantCulture);
        }

        return "";
    }

    private static void Row(StringBuilder sb, params object?[] cells)
    {
        for (var i = 0; i < cells.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(Escape(Convert.ToString(cells[i], CultureInfo.InvariantCulture) ?? ""));
        }

        sb.Append("\r\n");
    }

    /// <summary>含逗號、引號、換行，或開頭是公式字元的格子一律加引號（後者前面再補單引號，避免試算表當公式）。</summary>
    public static string Escape(string cell)
    {
        if (cell.Length > 0 && cell[0] is '=' or '+' or '-' or '@') cell = "'" + cell;
        return cell.IndexOfAny([',', '"', '\r', '\n']) < 0 ? cell : "\"" + cell.Replace("\"", "\"\"") + "\"";
    }
}

/// <summary>結算視窗的 AtkValues 快照（旁檔內容）：對照 hook 拿到的數值與畫面上顯示的。</summary>
public sealed class FrontlineAddonSnapshot
{
    public int Schema { get; set; } = 1;
    public DateTimeOffset CapturedLocal { get; set; }
    public uint TerritoryId { get; set; }
    public string AddonName { get; set; } = string.Empty;
    public uint DeclaredCount { get; set; }

    /// <summary>同場紀錄檔名（結算視窗比紀錄晚開時才填得到；早開的話由紀錄的 <c>addonFile</c> 反指）。</summary>
    public string? RecordFile { get; set; }

    public List<FrontlineAddonValue> Values { get; set; } = [];
}

public sealed class FrontlineAddonValue
{
    public int Index { get; set; }
    public int Type { get; set; }
    public string Value { get; set; } = string.Empty;
}

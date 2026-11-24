using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Lumina.Excel.Sheets;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>報表資料的一次性快照：背景載入後整份換掉，Draw 只讀。</summary>
internal sealed class FrontlineReportSnapshot
{
    public static readonly FrontlineReportSnapshot Empty = new();

    public List<FrontlineMatchRecord> Records { get; init; } = [];
    public List<string> RecordFiles { get; init; } = [];
    public int Unreadable { get; init; }
    public string? UnreadableExample { get; init; }
    public List<FrontlineAggregateRow> ByMap { get; init; } = [];
    public List<FrontlineAggregateRow> ByJob { get; init; } = [];
    public bool Loaded { get; init; }
}

public sealed partial class PvpFrontlineRecords
{
    private static readonly Vector4 WarnColor = new(1f, 0.6f, 0.3f, 1f);
    private static readonly Vector4 SelfRowBg = new(0.95f, 0.85f, 0.3f, 0.22f);

    private static readonly string[] ScoreHeaders =
        ["隊伍", "小隊", "名字", "伺服器", "職業", "擊倒", "死亡", "協助", "傷害", "對非玩家", "承受", "治療", "未知1", "占領"];

    private volatile FrontlineReportSnapshot report = FrontlineReportSnapshot.Empty;
    private FrontlineReportSnapshot? drawnReport;
    private int reportLoading;
    private int selectedRecord;
    private int scoreSortColumn = -1;
    private bool scoreSortDescending;
    private FrontlineMatchRecord? scoreSortFor;
    private List<FrontlinePlayerResult> scoreSorted = [];
    private string exportMessage = string.Empty;
    private readonly Dictionary<int, string> worldNames = [];

    /// <summary>已有一次載入在跑就回 false（呼叫端要自己留著「還要再載一次」）。</summary>
    internal bool RequestReload()
    {
        if (Interlocked.Exchange(ref reportLoading, 1) == 1) return false;

        var dir = recordsDir;
        var jobs = jobAbbr;
        _ = Task.Run(() =>
        {
            try
            {
                report = LoadReport(dir, jobs);
            }
            catch (Exception ex)
            {
                Svc.Log.Warning(ex, $"[{InternalName}] 載入前線紀錄失敗。");
                report = new FrontlineReportSnapshot { Loaded = true, Unreadable = 1, UnreadableExample = $"整個資料夾讀不到：{ex.Message}" };
            }
            finally
            {
                Volatile.Write(ref reportLoading, 0);
            }
        });
        return true;
    }

    private static FrontlineReportSnapshot LoadReport(string dir, IReadOnlyDictionary<int, string> jobs)
    {
        var loaded = new List<(FrontlineMatchRecord Record, string File)>();
        var unreadable = 0;
        string? example = null;

        if (Directory.Exists(dir))
        {
            foreach (var path in Directory.EnumerateFiles(dir, "*.json", SearchOption.AllDirectories))
            {
                if (path.EndsWith(FrontlineRecordJson.AddonSuffix, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var record = FrontlineRecordJson.Deserialize(File.ReadAllBytes(path));
                    if (record == null) throw new InvalidDataException("內容是 null");
                    loaded.Add((record, Path.GetFileName(path)));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
                {
                    unreadable++;
                    example ??= $"{Path.GetFileName(path)}：{ex.Message}";
                }
            }
        }

        loaded.Sort((a, b) => b.Record.CapturedLocal.CompareTo(a.Record.CapturedLocal));
        var records = loaded.Select(x => x.Record).ToList();
        return new FrontlineReportSnapshot
        {
            Records = records,
            RecordFiles = loaded.Select(x => x.File).ToList(),
            Unreadable = unreadable,
            UnreadableExample = example,
            ByMap = FrontlineStats.Aggregate(records, r => r.MapName.Length > 0 ? r.MapName : $"地圖 {r.TerritoryId}"),
            ByJob = FrontlineStats.Aggregate(records, r => r.Self is { } s ? JobAbbr(jobs, s.ClassJobId) : null),
            Loaded = true,
        };
    }

    internal void DrawReport(bool dirty)
    {
        if ((dirty || !report.Loaded) && !RequestReload() && dirty) window?.MarkDirty();

        var snap = report;
        if (!ReferenceEquals(snap, drawnReport))
        {
            // 新紀錄插在最前面：換快照時照檔名找回原本選的那場，找不到就看最新一場。
            var previous = drawnReport is { } d && selectedRecord < d.RecordFiles.Count ? d.RecordFiles[selectedRecord] : null;
            selectedRecord = previous == null ? 0 : Math.Max(0, snap.RecordFiles.IndexOf(previous));
            drawnReport = snap;
        }
        DrawReportHeader(snap);
        ImGui.Separator();

        if (!snap.Loaded)
        {
            ImGui.TextDisabled("讀取中…");
            return;
        }

        if (snap.Records.Count == 0)
        {
            ImGui.TextDisabled("還沒有紀錄。模組開著時打完一場紛爭前線就會出現。");
            return;
        }

        using var bar = Dalamud.Interface.Utility.Raii.ImRaii.TabBar("##pvpFrontlineTabs");
        if (!bar) return;

        using (var tab = Dalamud.Interface.Utility.Raii.ImRaii.TabItem("計分板"))
        {
            if (tab) DrawScoreboardTab(snap);
        }

        using (var tab = Dalamud.Interface.Utility.Raii.ImRaii.TabItem("依地圖"))
        {
            if (tab) DrawAggregate("##pvpByMap", "地圖", snap.ByMap);
        }

        using (var tab = Dalamud.Interface.Utility.Raii.ImRaii.TabItem("依職業"))
        {
            if (tab)
            {
                ImGui.TextDisabled("以自己那一場用的職業分組；名單裡找不到自己的場次不計。");
                DrawAggregate("##pvpByJob", "職業", snap.ByJob);
            }
        }
    }

    private void DrawReportHeader(FrontlineReportSnapshot snap)
    {
        if (!HookResolved)
        {
            ImGui.TextColored(WarnColor, "這版遊戲找不到結算函式，新場次不會被記錄。");
        }

        ImGui.TextUnformatted(snap.Loaded ? $"共 {snap.Records.Count} 場" : "共 ? 場");
        Tip($"紀錄位置：{recordsDir}");
        if (snap.Unreadable > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(WarnColor, $"另有 {snap.Unreadable} 份讀不到");
            Tip(snap.UnreadableExample ?? string.Empty);
        }

        ImGui.SameLine();
        var loading = Volatile.Read(ref reportLoading) == 1;
        using (Dalamud.Interface.Utility.Raii.ImRaii.Disabled(loading))
        {
            if (ImGui.Button("重新整理")) RequestReload();
        }

        Tip(loading ? "讀取中，請稍候。" : "重新讀取紀錄資料夾。");

        ImGui.SameLine();
        using (Dalamud.Interface.Utility.Raii.ImRaii.Disabled(snap.Records.Count == 0))
        {
            if (ImGui.Button("匯出全部場次 CSV"))
                Export("frontline-matches", FrontlineCsv.Matches(snap.Records, JobAbbr));
        }

        Tip(snap.Records.Count == 0 ? "還沒有紀錄可匯出。" : "每場一列：時間、地圖、名次、三隊分數與自己的成績。存成 UTF-8（含 BOM）。");

        if (exportMessage.Length > 0)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(exportMessage);
        }
    }

    private void Export(string prefix, string csv)
    {
        try
        {
            var dir = Path.Combine(recordsDir, "exports");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"{prefix}-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
            File.WriteAllText(path, csv, new UTF8Encoding(true));
            exportMessage = $"已匯出：{path}";
        }
        catch (Exception ex)
        {
            exportMessage = $"匯出失敗：{ex.Message}";
        }
    }

    private void DrawScoreboardTab(FrontlineReportSnapshot snap)
    {
        if (selectedRecord >= snap.Records.Count) selectedRecord = 0;
        var r = snap.Records[selectedRecord];

        ImGui.SetNextItemWidth(UiScale.Px(360));
        using (var combo = Dalamud.Interface.Utility.Raii.ImRaii.Combo("##pvpMatch", MatchLabel(r)))
        {
            if (combo)
            {
                for (var i = 0; i < snap.Records.Count; i++)
                {
                    if (ImGui.Selectable($"{MatchLabel(snap.Records[i])}##m{i}", i == selectedRecord))
                        selectedRecord = i;
                }
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("匯出這場計分板 CSV"))
            Export($"frontline-scoreboard-{snap.RecordFiles[selectedRecord].Replace(".json", string.Empty)}",
                FrontlineCsv.Scoreboard(r, JobAbbr, WorldName));

        var len = r.Header.MatchLengthSeconds;
        ImGui.TextUnformatted($"{r.MapName}　{r.CapturedLocal:yyyy-MM-dd HH:mm}　時長 {len / 60}:{len % 60:00}　人數 {r.Header.PlayerCount}");
        ImGui.SameLine();
        if (r.SelfPlacement is { } place)
        {
            ImGui.TextUnformatted($"　自己第 {place} 名");
        }
        else
        {
            ImGui.TextDisabled("　自己名次 ?");
            Tip(r.Self == null ? "名單裡找不到自己（ContentId 與名字＋伺服器都對不上）。" : "自己的隊伍編號對不上三隊之一。");
        }

        foreach (var t in r.Teams.OrderBy(t => t.Placement))
        {
            ImGui.TextColored(TeamColor(t.Team), $"第 {t.Placement + 1} 名 {FrontlineTeams.Name(t.Team)}　{t.TotalPoints}");
            Tip($"占領 {t.OccupationPoints}　擊倒 {t.KillPoints}　無人機 {t.DronePoints}　冰 {t.IcePoints}　被擊倒扣分 {t.KoPointLosses}");
            ImGui.SameLine();
        }

        ImGui.NewLine();
        DrawScoreTable(r);
    }

    private void DrawScoreTable(FrontlineMatchRecord r)
    {
        const ImGuiTableFlags flags = ImGuiTableFlags.Sortable | ImGuiTableFlags.SortTristate | ImGuiTableFlags.Borders
                                      | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable
                                      | ImGuiTableFlags.SizingFixedFit;
        if (!ImGui.BeginTable("##pvpScore", ScoreHeaders.Length, flags)) return;

        try
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            for (var i = 0; i < ScoreHeaders.Length; i++)
            {
                var colFlags = i >= 5 ? ImGuiTableColumnFlags.PreferSortDescending : ImGuiTableColumnFlags.None;
                if (i == 2) colFlags |= ImGuiTableColumnFlags.WidthStretch;
                ImGui.TableSetupColumn(ScoreHeaders[i], colFlags);
            }

            ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
            for (var i = 0; i < ScoreHeaders.Length; i++)
            {
                if (!ImGui.TableSetColumnIndex(i)) continue;
                ImGui.TableHeader(ScoreHeaders[i]);
                if (i == 12) Tip("結算資料 +0x20 的原始值，推測是被治療量（未驗證）；遊戲的結算處理函式本身不讀這一欄。");
                if (i == 9) Tip("對非玩家（據點、無人機、冰等）造成的傷害。");
            }

            SortScoreboard(r);

            foreach (var p in scoreSorted)
            {
                ImGui.TableNextRow();
                if (p.Index == r.SelfIndex)
                    ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg1, ImGui.GetColorU32(SelfRowBg));

                var color = TeamColor(p.Team);
                Cell(0, FrontlineTeams.Name(p.Team), color);
                Cell(1, p.Alliance.ToString(CultureInfo.InvariantCulture), color);
                Cell(2, p.Index == r.SelfIndex ? $"{p.Name}（自己）" : p.Name, color);
                Cell(3, WorldName(p.WorldId), color);
                Cell(4, JobAbbr(p.ClassJobId), color);
                Cell(5, p.Kills.ToString(CultureInfo.InvariantCulture), color);
                Cell(6, p.Deaths.ToString(CultureInfo.InvariantCulture), color);
                Cell(7, p.Assists.ToString(CultureInfo.InvariantCulture), color);
                Cell(8, p.DamageDealt.ToString("N0", CultureInfo.InvariantCulture), color);
                Cell(9, p.DamageToOther.ToString("N0", CultureInfo.InvariantCulture), color);
                Cell(10, p.DamageTaken.ToString("N0", CultureInfo.InvariantCulture), color);
                Cell(11, p.HpRestored.ToString("N0", CultureInfo.InvariantCulture), color);
                Cell(12, p.Unknown1.ToString("N0", CultureInfo.InvariantCulture), color);
                Cell(13, p.Occupations.ToString(CultureInfo.InvariantCulture), color);
            }
        }
        finally
        {
            ImGui.EndTable();
        }
    }

    private void SortScoreboard(FrontlineMatchRecord r)
    {
        var specs = ImGui.TableGetSortSpecs();
        var dirty = specs.SpecsDirty || !ReferenceEquals(r, scoreSortFor);
        if (!dirty) return;

        if (specs.SpecsDirty)
        {
            if (specs.SpecsCount > 0)
            {
                scoreSortColumn = specs.Specs.ColumnIndex;
                scoreSortDescending = specs.Specs.SortDirection == ImGuiSortDirection.Descending;
            }
            else
            {
                scoreSortColumn = -1;
            }

            specs.SpecsDirty = false;
        }

        scoreSortFor = r;
        var col = scoreSortColumn;
        var desc = scoreSortDescending;
        Func<FrontlinePlayerResult, IComparable> key = col switch
        {
            0 => p => p.Team,
            1 => p => p.Alliance,
            2 => p => p.Name,
            3 => p => WorldName(p.WorldId),
            4 => p => JobAbbr(p.ClassJobId),
            5 => p => p.Kills,
            6 => p => p.Deaths,
            7 => p => p.Assists,
            8 => p => p.DamageDealt,
            9 => p => p.DamageToOther,
            10 => p => p.DamageTaken,
            11 => p => p.HpRestored,
            12 => p => p.Unknown1,
            13 => p => p.Occupations,
            _ => p => p.Index,
        };

        var list = new List<FrontlinePlayerResult>(r.Players);
        list.Sort((a, b) =>
        {
            var c = key(a).CompareTo(key(b));
            if (desc) c = -c;
            return c != 0 ? c : a.Index.CompareTo(b.Index);
        });
        scoreSorted = list;
    }

    private static void DrawAggregate(string id, string keyHeader, List<FrontlineAggregateRow> rows)
    {
        if (rows.Count == 0)
        {
            ImGui.TextDisabled("沒有可彙總的場次。");
            return;
        }

        string[] headers = [keyHeader, "場次", "第一名率", "平均名次", "擊倒", "死亡", "協助", "傷害", "承受", "治療"];
        const ImGuiTableFlags flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY
                                      | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingFixedFit;
        if (!ImGui.BeginTable(id, headers.Length, flags)) return;

        try
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            foreach (var h in headers) ImGui.TableSetupColumn(h);
            ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
            for (var i = 0; i < headers.Length; i++)
            {
                if (!ImGui.TableSetColumnIndex(i)) continue;
                ImGui.TableHeader(headers[i]);
                if (i == 2 || i == 3) Tip("括號內＝自己名次已知的場次數。");
                if (i >= 4) Tip("每場平均；括號內＝名單裡找得到自己的場次數。");
            }

            foreach (var row in rows)
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.TextUnformatted(row.Key);
                ImGui.TableSetColumnIndex(1);
                ImGui.TextUnformatted(row.Matches.ToString(CultureInfo.InvariantCulture));
                Stat(2, row.FirstRate, "P0", row.Placed);
                Stat(3, row.AvgPlacement, "0.00", row.Placed);
                Stat(4, row.Avg(row.Kills), "0.0", row.WithSelf);
                Stat(5, row.Avg(row.Deaths), "0.0", row.WithSelf);
                Stat(6, row.Avg(row.Assists), "0.0", row.WithSelf);
                Stat(7, row.Avg(row.Damage), "N0", row.WithSelf);
                Stat(8, row.Avg(row.Taken), "N0", row.WithSelf);
                Stat(9, row.Avg(row.Healed), "N0", row.WithSelf);
            }
        }
        finally
        {
            ImGui.EndTable();
        }
    }

    private static void Stat(int column, double? value, string format, int samples)
    {
        if (!ImGui.TableSetColumnIndex(column)) return;
        if (value is not { } v)
        {
            ImGui.TextDisabled("? (0)");
            return;
        }

        ImGui.TextUnformatted($"{v.ToString(format, CultureInfo.InvariantCulture)} ({samples})");
    }

    private static void Cell(int column, string text, Vector4 color)
    {
        if (!ImGui.TableSetColumnIndex(column)) return;
        ImGui.TextColored(color, text);
    }

    private string MatchLabel(FrontlineMatchRecord r) =>
        $"{r.CapturedLocal:MM-dd HH:mm} {r.MapName} {(r.SelfPlacement is { } p ? $"第 {p} 名" : "名次 ?")}";

    private string WorldName(int id)
    {
        if (worldNames.TryGetValue(id, out var name)) return name;
        name = Svc.Data.GetExcelSheet<World>().GetRowOrDefault((uint)id)?.Name.ExtractText() ?? string.Empty;
        if (name.Length == 0) name = $"?{id}";
        worldNames[id] = name;
        return name;
    }

    /// <summary>遊戲裡的三軍代表色：黑渦團紅、雙蛇黨黃、不滅隊藍。</summary>
    private static Vector4 TeamColor(int team) => team switch
    {
        0 => new Vector4(1f, 0.45f, 0.45f, 1f),
        1 => new Vector4(1f, 0.85f, 0.35f, 1f),
        2 => new Vector4(0.5f, 0.7f, 1f, 1f),
        _ => new Vector4(0.7f, 0.7f, 0.7f, 1f),
    };

    private static void Tip(string text)
    {
        if (text.Length > 0 && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(text);
    }
}

/// <summary>「前線戰績」報表視窗。由模組啟用時掛上、停用時拆掉。</summary>
public sealed class PvpFrontlineRecordsWindow : Window
{
    private readonly PvpFrontlineRecords module;
    private int dirty = 1;

    public PvpFrontlineRecordsWindow(PvpFrontlineRecords module)
        : base("前線戰績###TCToolboxPvpFrontlineRecords")
    {
        this.module = module;
    }

    /// <summary>任何執行緒都可呼叫：下次繪製時重新載入。</summary>
    public void MarkDirty() => Volatile.Write(ref dirty, 1);

    public override void OnOpen() => MarkDirty();

    public override void PreDraw()
    {
        SizeConstraints = UiScale.MinWindowSize(560, 320);
        base.PreDraw();
    }

    public override void Draw() => module.DrawReport(Interlocked.Exchange(ref dirty, 0) == 1);
}

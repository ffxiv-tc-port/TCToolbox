using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace TCToolbox.Core;

// 本檔假設與契約：
// - 純檔案邏輯，不碰任何 Dalamud 服務，任何執行緒都能呼叫（離線探針直接連結本檔測）。
// - 相對路徑一律以 '/' 分隔，大小寫照檔案系統原樣保存；比對規則一律忽略大小寫。
// - 備份＝一個 zip＋同名 .json 清單；清單最後寫，所以「沒有清單的 zip」＝沒寫完。
// - 對來源目錄只讀；唯一會寫來源目錄的是 RestoreFile（呼叫端負責先確認所屬插件沒載入）。

/// <summary>單一檔案在一次掃描中的判定。</summary>
public enum ConfigFileStatus
{
    /// <summary>沒有問題（或不是設定類檔案，不檢查）。零值刻意是這個：清單缺欄位時當成沒問題，不製造假警報。</summary>
    Ok = 0,

    /// <summary>開頭全是 NUL（非正常關機的典型樣子）。</summary>
    Nul = 1,

    /// <summary>長度 0。</summary>
    Empty = 2,

    /// <summary>JSON 結構不完整（截斷、括號不成對、夾 NUL）。</summary>
    BadJson = 3,

    /// <summary>比最近一次完好時小很多，可能被重置成預設值。</summary>
    Shrunk = 4,

    /// <summary>有上面任一問題，但使用者說過「這是我自己改的」且檔案沒再變過。</summary>
    Acknowledged = 5,
}

/// <summary>一條排除規則（設定頁照這份清單列出排除了什麼）。</summary>
public sealed record ConfigBackupExclusion(string Pattern, string Reason);

/// <summary>清單裡的一個檔案。</summary>
public sealed class ConfigBackupFileEntry
{
    public string Path { get; set; } = string.Empty;
    public long Size { get; set; }
    public long WriteUtcTicks { get; set; }
    public ConfigFileStatus Status { get; set; }

    /// <summary>判 <see cref="ConfigFileStatus.Shrunk"/> 時拿來比的大小（0＝沒有參考）。</summary>
    public long ReferenceSize { get; set; }
}

/// <summary>沒放進備份的檔案與原因。</summary>
public sealed class ConfigBackupSkip
{
    public string Path { get; set; } = string.Empty;
    public long Size { get; set; }
    public string Reason { get; set; } = string.Empty;
}

/// <summary>每條排除規則這次擋掉多少。</summary>
public sealed class ConfigBackupExcludedTotal
{
    public string Pattern { get; set; } = string.Empty;
    public int Files { get; set; }
    public long Bytes { get; set; }
}

/// <summary>一份備份的清單（存成 zip 旁的 .json）。</summary>
public sealed class ConfigBackupManifest
{
    public int Version { get; set; } = 1;
    public long CreatedUtcTicks { get; set; }
    public string Trigger { get; set; } = string.Empty;
    public string ZipName { get; set; } = string.Empty;
    public long ZipSize { get; set; }
    public List<ConfigBackupFileEntry> Files { get; set; } = [];
    public List<ConfigBackupSkip> Skipped { get; set; } = [];
    public List<ConfigBackupExcludedTotal> Excluded { get; set; } = [];

    /// <summary>沒有任何未確認的問題檔。最新一份乾淨的備份永遠不被輪替刪掉。</summary>
    public bool IsClean => Files.All(f => !ConfigBackupEngine.IsProblem(f.Status));

    public DateTime CreatedUtc => new(CreatedUtcTicks, DateTimeKind.Utc);
}

/// <summary>使用者確認過「這是我自己改的」那一刻的檔案狀態。</summary>
public sealed class ConfigBackupAck
{
    public long Size { get; set; }
    public long WriteUtcTicks { get; set; }
}

/// <summary>掃描發現的一個問題檔，附上能拿來還原的最近一份完好備份。</summary>
public sealed record ConfigBackupIssue(
    string Path,
    string Owner,
    ConfigFileStatus Status,
    long Size,
    long ReferenceSize,
    long WriteUtcTicks,
    string? GoodZipName,
    DateTime? GoodBackupUtc);

/// <summary>一次掃描（可順便寫備份）的結果。</summary>
public sealed record ConfigBackupScanResult(
    DateTime FinishedUtc,
    ConfigBackupManifest Manifest,
    IReadOnlyList<ConfigBackupIssue> Issues,
    bool WroteBackup,
    string? Error);

/// <summary>輪替的結果。</summary>
public sealed record ConfigBackupRotation(int Kept, int Deleted, string? PinnedZip, long TotalBytes);

public sealed class ConfigBackupEngine
{
    /// <summary>單檔上限。實測最大的設定類檔約 5.3 MB（AutoHook），超過這個的都是資料庫或錄影。</summary>
    public const long DefaultMaxFileBytes = 20L * 1024 * 1024;

    /// <summary>一份備份收錄的原始大小上限。實測排除後約 80 MB。</summary>
    public const long DefaultMaxTotalBytes = 256L * 1024 * 1024;

    /// <summary>判「縮小」的最小參考大小：更小的檔清空一兩筆就會掉一大半。</summary>
    public const long ShrinkMinReferenceBytes = 4 * 1024;

    /// <summary>縮到參考大小的幾分之一以下才算（實測自己編輯的最大縮幅約剩 48%）。</summary>
    public const double ShrinkRatio = 0.20;

    public const string ZipPrefix = "cfgbak-";
    public const string SavedBeforeRestoreDir = "restore-saved";

    private const int HeadProbeBytes = 4096;

    /// <summary>排除規則。順序就是設定頁上的順序。</summary>
    public static readonly ConfigBackupExclusion[] Exclusions =
    [
        new("*/replays/", "戰鬥錄影（BossModReborn 約 2.4 GB）"),
        new("*cache/", "快取：導航網格（vnavmesh meshcache 約 1.2 GB）、內建瀏覽器、語音等，可重建"),
        new("*/logs/、*/log/、*.log", "記錄檔"),
        new("Crossingway/dependencies/", "Crossingway 下載的瀏覽器元件（約 380 MB），可重新下載"),
        new("HuntHelper/Images/", "HuntHelper 下載的地圖圖片，可重新下載"),
        new("ChatTwo/chat-sqlite.db*", "ChatTwo 聊天紀錄資料庫（約 1 GB，不是設定）"),
        new("*.dll、*.exe、*.pak、*.pdb", "程式元件"),
        new("*.wav、*.mp3、*.ogg", "音訊快取"),
        new("*.tmp、*.lock、*-shm、*-journal", "暫存檔、鎖定檔、SQLite 暫存索引"),
    ];

    private static readonly string[] ExcludedExtensions =
        [".log", ".dll", ".exe", ".pak", ".pdb", ".wav", ".mp3", ".ogg", ".tmp", ".lock"];

    private static readonly string[] TextLikeExtensions =
        [".json", ".jsonl", ".yaml", ".yml", ".xml", ".ini", ".toml", ".txt", ".csv", ".lua", ".cfg", ".nfo"];

    private static readonly string[] ShrinkCheckedExtensions = [".json", ".yaml", ".yml"];

    private static readonly JsonSerializerOptions ManifestJson = new() { WriteIndented = false };

    public string SourceDir { get; }
    public string BackupDir { get; }
    public long MaxFileBytes { get; init; } = DefaultMaxFileBytes;
    public long MaxTotalBytes { get; init; } = DefaultMaxTotalBytes;

    public ConfigBackupEngine(string sourceDir, string backupDir)
    {
        SourceDir = Path.GetFullPath(sourceDir);
        BackupDir = Path.GetFullPath(backupDir);
    }

    /// <summary>備份位置落在來源目錄裡（會把自己備份進去）就是設定錯誤。</summary>
    public static bool IsInside(string child, string parent)
    {
        var c = Path.GetFullPath(child).TrimEnd('\\', '/') + "\\";
        var p = Path.GetFullPath(parent).TrimEnd('\\', '/') + "\\";
        return c.StartsWith(p, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsProblem(ConfigFileStatus s) =>
        s is ConfigFileStatus.Nul or ConfigFileStatus.Empty or ConfigFileStatus.BadJson or ConfigFileStatus.Shrunk;

    /// <summary>還原按鈕能不能按；回傳擋下的原因（<c>null</c>＝可以）。</summary>
    /// <param name="plugin">同名的已安裝插件；<c>null</c>＝沒有或列舉失敗，兩者都不准（無法確認沒人在用這個檔）。</param>
    public static string? RestoreBlocker(bool hasGoodBackup, (string Name, bool Loaded)? plugin)
    {
        if (!hasGoodBackup) return "沒有完好的備份可還原。";
        if (plugin is not { } p)
            return "認不出這個檔屬於哪個已安裝的插件，無法確認它沒在用；請關掉遊戲後從備份資料夾手動取出。";
        return p.Loaded ? $"「{p.Name}」載入中，請先停用再還原。" : null;
    }

    /// <summary>相對路徑屬於哪個插件：Dalamud 的慣例是 <c>&lt;InternalName&gt;.json</c> 與 <c>&lt;InternalName&gt;/</c>。</summary>
    public static string OwnerOf(string relPath)
    {
        var slash = relPath.IndexOf('/');
        if (slash >= 0) return relPath[..slash];
        return relPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? relPath[..^5] : relPath;
    }

    /// <summary>回傳擋下這個路徑的規則（<see cref="Exclusions"/> 的 Pattern）；<c>null</c>＝不排除。</summary>
    public static string? ExclusionFor(string relPath, bool isDirectory)
    {
        var segments = relPath.Split('/');
        var dirCount = isDirectory ? segments.Length : segments.Length - 1;
        for (var i = 0; i < dirCount; i++)
        {
            var seg = segments[i];
            if (seg.Equals("replays", StringComparison.OrdinalIgnoreCase)) return Exclusions[0].Pattern;
            if (seg.EndsWith("cache", StringComparison.OrdinalIgnoreCase)) return Exclusions[1].Pattern;
            if (seg.Equals("logs", StringComparison.OrdinalIgnoreCase) || seg.Equals("log", StringComparison.OrdinalIgnoreCase))
                return Exclusions[2].Pattern;
        }

        if (StartsWithPath(relPath, "Crossingway/dependencies")) return Exclusions[3].Pattern;
        if (StartsWithPath(relPath, "HuntHelper/Images")) return Exclusions[4].Pattern;
        if (isDirectory) return null;

        if (relPath.StartsWith("ChatTwo/chat-sqlite.db", StringComparison.OrdinalIgnoreCase)) return Exclusions[5].Pattern;

        var ext = Path.GetExtension(relPath).ToLowerInvariant();
        var extIndex = Array.IndexOf(ExcludedExtensions, ext);
        if (extIndex >= 0)
        {
            return ext switch
            {
                ".log" => Exclusions[2].Pattern,
                ".dll" or ".exe" or ".pak" or ".pdb" => Exclusions[6].Pattern,
                ".wav" or ".mp3" or ".ogg" => Exclusions[7].Pattern,
                _ => Exclusions[8].Pattern,
            };
        }

        if (relPath.EndsWith("-shm", StringComparison.OrdinalIgnoreCase) ||
            relPath.EndsWith("-journal", StringComparison.OrdinalIgnoreCase))
            return Exclusions[8].Pattern;

        return null;
    }

    private static bool StartsWithPath(string relPath, string prefix) =>
        relPath.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
        relPath.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 判一個檔的內容。<paramref name="referenceSize"/>＝最近一次完好時的大小（0＝沒有）。
    /// </summary>
    /// <remarks>只檢查文字類設定檔；資料庫、圖片等二進位檔一律 Ok（全 0 開頭對它們可能是正常的）。</remarks>
    public static ConfigFileStatus Classify(string relPath, ReadOnlySpan<byte> data, long referenceSize)
    {
        var ext = Path.GetExtension(relPath).ToLowerInvariant();
        if (Array.IndexOf(TextLikeExtensions, ext) < 0) return ConfigFileStatus.Ok;

        var shrinkChecked = Array.IndexOf(ShrinkCheckedExtensions, ext) >= 0;
        if (data.Length == 0)
            return shrinkChecked ? ConfigFileStatus.Empty : ConfigFileStatus.Ok;

        var head = data[..Math.Min(HeadProbeBytes, data.Length)];
        if (head.IndexOfAnyExcept((byte)0) < 0) return ConfigFileStatus.Nul;

        if (ext == ".json" && !LooksBinary(data) && !IsStructurallyCompleteJson(data)) return ConfigFileStatus.BadJson;

        if (shrinkChecked && IsShrunk(data.Length, referenceSize)) return ConfigFileStatus.Shrunk;

        return ConfigFileStatus.Ok;
    }

    /// <summary>
    /// 副檔名是 .json、內容卻不是文字 JSON（GatherBuddyReborn 把 MessagePack 存成 .json）：第一個非空白位元組
    /// 既不是 NUL、也不是 JSON 值或註解的開頭。開頭是 NUL 不算二進位，照舊交給結構檢查判成受損。
    /// </summary>
    public static bool LooksBinary(ReadOnlySpan<byte> d)
    {
        var i = d.Length >= 3 && d[0] == 0xEF && d[1] == 0xBB && d[2] == 0xBF ? 3 : 0;
        while (i < d.Length && d[i] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') i++;
        if (i >= d.Length || d[i] == 0) return false;
        return d[i] is not ((byte)'{' or (byte)'[' or (byte)'"' or (byte)'-' or (>= (byte)'0' and <= (byte)'9')
            or (byte)'t' or (byte)'f' or (byte)'n' or (byte)'N' or (byte)'I' or (byte)'/');
    }

    public static bool IsShrunk(long size, long referenceSize) =>
        referenceSize >= ShrinkMinReferenceBytes &&
        size < referenceSize * ShrinkRatio &&
        referenceSize - size >= 3 * 1024;

    /// <summary>
    /// JSON 結構完整性檢查（不是完整解析）：括號成對、字串有收尾、根值之後只剩空白、沒有 NUL。
    /// </summary>
    /// <remarks>
    /// 刻意不用 System.Text.Json 解析：Newtonsoft 寫出的 NaN／Infinity、註解、尾逗號在它眼裡都是錯，
    /// 會把正常的設定報成受損。非正常關機的損壞形狀是截斷與 NUL，這裡只抓這兩種。
    /// </remarks>
    public static bool IsStructurallyCompleteJson(ReadOnlySpan<byte> d)
    {
        var i = 0;
        if (d.Length >= 3 && d[0] == 0xEF && d[1] == 0xBB && d[2] == 0xBF) i = 3;
        else if (d.Length >= 2 && ((d[0] == 0xFF && d[1] == 0xFE) || (d[0] == 0xFE && d[1] == 0xFF))) return true;

        var stack = new List<byte>(16);
        var rootDone = false;
        var sawAnything = false;

        while (i < d.Length)
        {
            var c = d[i];
            if (c == 0) return false;

            if (c is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            {
                i++;
                continue;
            }

            if (c == '/' && i + 1 < d.Length && (d[i + 1] == '/' || d[i + 1] == '*'))
            {
                if (!SkipComment(d, ref i)) return false;
                continue;
            }

            if (rootDone) return false;
            sawAnything = true;

            switch (c)
            {
                case (byte)'"':
                    if (!SkipString(d, ref i)) return false;
                    if (stack.Count == 0) rootDone = true;
                    continue;
                case (byte)'{':
                    stack.Add((byte)'}');
                    break;
                case (byte)'[':
                    stack.Add((byte)']');
                    break;
                case (byte)'}':
                case (byte)']':
                    if (stack.Count == 0 || stack[^1] != c) return false;
                    stack.RemoveAt(stack.Count - 1);
                    if (stack.Count == 0) rootDone = true;
                    break;
                default:
                    if (stack.Count == 0)
                    {
                        // 根是純量（數字、true、null…）：吃到分隔字元為止。
                        while (i < d.Length && d[i] is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or 0))
                            i++;
                        rootDone = true;
                        continue;
                    }

                    break;
            }

            i++;
        }

        return sawAnything && stack.Count == 0;
    }

    private static bool SkipString(ReadOnlySpan<byte> d, ref int i)
    {
        i++;
        while (i < d.Length)
        {
            var b = d[i];
            if (b == 0) return false;
            if (b == '\\')
            {
                i += 2;
                continue;
            }

            i++;
            if (b == '"') return true;
        }

        return false;
    }

    private static bool SkipComment(ReadOnlySpan<byte> d, ref int i)
    {
        if (d[i + 1] == '/')
        {
            while (i < d.Length && d[i] != '\n') i++;
            return true;
        }

        i += 2;
        while (i + 1 < d.Length)
        {
            if (d[i] == '*' && d[i + 1] == '/')
            {
                i += 2;
                return true;
            }

            i++;
        }

        return false;
    }

    /// <summary>讀回所有寫完的清單（新到舊）。讀不了的清單跳過並記在 <paramref name="problems"/>。</summary>
    public List<ConfigBackupManifest> LoadManifests(List<string>? problems = null)
    {
        var result = new List<ConfigBackupManifest>();
        if (!Directory.Exists(BackupDir)) return result;

        foreach (var path in Directory.EnumerateFiles(BackupDir, ZipPrefix + "*.json"))
        {
            try
            {
                var bytes = File.ReadAllBytes(path);
                var manifest = JsonSerializer.Deserialize<ConfigBackupManifest>(bytes, ManifestJson);
                if (manifest == null || manifest.CreatedUtcTicks <= 0) continue;
                if (!File.Exists(System.IO.Path.Combine(BackupDir, manifest.ZipName))) continue;
                result.Add(manifest);
            }
            catch (Exception ex)
            {
                problems?.Add($"{System.IO.Path.GetFileName(path)}：{ex.Message}");
            }
        }

        result.Sort((a, b) => b.CreatedUtcTicks.CompareTo(a.CreatedUtcTicks));
        return result;
    }

    /// <summary>
    /// 掃描來源目錄；<paramref name="writeZip"/> 為真時同一趟把檔案寫進新的備份。
    /// </summary>
    /// <param name="acks">使用者確認過的檔案（相對路徑 → 確認當下的大小與修改時間）。呼叫端給快照，這裡只讀。</param>
    public ConfigBackupScanResult Scan(
        bool writeZip, string trigger, IReadOnlyDictionary<string, ConfigBackupAck> acks,
        DateTime nowUtc, CancellationToken token)
    {
        var history = LoadManifests();
        var reference = BuildReference(history);

        var manifest = new ConfigBackupManifest { CreatedUtcTicks = nowUtc.Ticks, Trigger = trigger };
        var excluded = new Dictionary<string, ConfigBackupExcludedTotal>();
        var candidates = new List<(string Rel, FileInfo Info)>();
        CollectFiles(new DirectoryInfo(SourceDir), string.Empty, candidates, excluded, manifest.Skipped);
        manifest.Excluded = Exclusions
            .Select(e => excluded.TryGetValue(e.Pattern, out var t) ? t : new ConfigBackupExcludedTotal { Pattern = e.Pattern })
            .ToList();

        string? tmpZip = null;
        FileStream? zipStream = null;
        ZipArchive? zip = null;
        var wrote = false;
        try
        {
            if (writeZip)
            {
                Directory.CreateDirectory(BackupDir);
                var stamp = nowUtc.ToLocalTime().ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
                manifest.ZipName = $"{ZipPrefix}{stamp}-{trigger}.zip";
                tmpZip = System.IO.Path.Combine(BackupDir, manifest.ZipName + ".tmp");
                zipStream = new FileStream(tmpZip, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                zip = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true);
            }

            long total = 0;
            foreach (var (rel, info) in candidates)
            {
                token.ThrowIfCancellationRequested();

                if (info.Length > MaxFileBytes)
                {
                    manifest.Skipped.Add(new ConfigBackupSkip { Path = rel, Size = info.Length, Reason = "單檔超過上限" });
                    continue;
                }

                if (total + info.Length > MaxTotalBytes)
                {
                    manifest.Skipped.Add(new ConfigBackupSkip { Path = rel, Size = info.Length, Reason = "整份超過上限" });
                    continue;
                }

                byte[] data;
                DateTime writeUtc;
                try
                {
                    // 對方可能正開著這個檔：只要它允許讀就讀，不允許就跳過，絕不搶鎖。
                    using var fs = new FileStream(info.FullName, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    data = ReadAll(fs, MaxFileBytes);
                    writeUtc = File.GetLastWriteTimeUtc(info.FullName);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    manifest.Skipped.Add(new ConfigBackupSkip
                    {
                        Path = rel, Size = info.Length,
                        Reason = ex is UnauthorizedAccessException ? "沒有讀取權限" : "檔案被鎖住（可能正在寫入）",
                    });
                    continue;
                }

                if (data.Length > MaxFileBytes)
                {
                    manifest.Skipped.Add(new ConfigBackupSkip { Path = rel, Size = data.Length, Reason = "單檔超過上限" });
                    continue;
                }

                reference.TryGetValue(rel, out var refSize);
                var status = Classify(rel, data, refSize);
                if (IsProblem(status) && acks.TryGetValue(rel, out var ack) &&
                    ack.Size == data.Length && ack.WriteUtcTicks == writeUtc.Ticks)
                    status = ConfigFileStatus.Acknowledged;

                manifest.Files.Add(new ConfigBackupFileEntry
                {
                    Path = rel, Size = data.Length, WriteUtcTicks = writeUtc.Ticks, Status = status, ReferenceSize = refSize,
                });
                total += data.Length;

                if (zip != null)
                {
                    var entry = zip.CreateEntry(rel, CompressionLevel.Optimal);
                    if (writeUtc.Year is >= 1980 and < 2107) entry.LastWriteTime = writeUtc.ToLocalTime();
                    using var es = entry.Open();
                    es.Write(data, 0, data.Length);
                }
            }

            if (zip != null)
            {
                zip.Dispose();
                zip = null;
                zipStream!.Flush(true);
                manifest.ZipSize = zipStream.Length;
                zipStream.Dispose();
                zipStream = null;

                var finalZip = System.IO.Path.Combine(BackupDir, manifest.ZipName);
                File.Move(tmpZip!, finalZip);
                tmpZip = null;
                WriteManifest(manifest);
                wrote = true;
            }
        }
        finally
        {
            zip?.Dispose();
            zipStream?.Dispose();
            if (tmpZip != null) TryDelete(tmpZip);
        }

        var issues = BuildIssues(manifest, history);
        return new ConfigBackupScanResult(DateTime.UtcNow, manifest, issues, wrote, null);
    }

    private static byte[] ReadAll(FileStream fs, long cap)
    {
        using var ms = new MemoryStream(fs.CanSeek ? (int)Math.Min(fs.Length, cap + 1) : 0);
        var buffer = new byte[81920];
        int n;
        while ((n = fs.Read(buffer, 0, buffer.Length)) > 0)
        {
            ms.Write(buffer, 0, n);
            if (ms.Length > cap) break;
        }

        return ms.ToArray();
    }

    private void WriteManifest(ConfigBackupManifest manifest)
    {
        var path = System.IO.Path.Combine(BackupDir, System.IO.Path.ChangeExtension(manifest.ZipName, ".json"));
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(fs, manifest, ManifestJson);
            fs.Flush(true);
        }

        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>每個檔「最近一次完好」時的大小（判縮小用）。</summary>
    private static Dictionary<string, long> BuildReference(List<ConfigBackupManifest> newestFirst)
    {
        var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in newestFirst)
        {
            foreach (var f in m.Files)
            {
                if (IsProblem(f.Status)) continue;
                map.TryAdd(f.Path, f.Size);
            }
        }

        return map;
    }

    private static List<ConfigBackupIssue> BuildIssues(ConfigBackupManifest current, List<ConfigBackupManifest> history)
    {
        var issues = new List<ConfigBackupIssue>();
        foreach (var f in current.Files)
        {
            if (!IsProblem(f.Status)) continue;

            ConfigBackupManifest? good = null;
            foreach (var m in history)
            {
                if (m.Files.Any(x => string.Equals(x.Path, f.Path, StringComparison.OrdinalIgnoreCase) && !IsProblem(x.Status)))
                {
                    good = m;
                    break;
                }
            }

            issues.Add(new ConfigBackupIssue(f.Path, OwnerOf(f.Path), f.Status, f.Size, f.ReferenceSize, f.WriteUtcTicks,
                good?.ZipName, good?.CreatedUtc));
        }

        return issues;
    }

    private void CollectFiles(
        DirectoryInfo dir, string relDir, List<(string, FileInfo)> files,
        Dictionary<string, ConfigBackupExcludedTotal> excluded, List<ConfigBackupSkip> skipped)
    {
        IEnumerable<FileSystemInfo> children;
        try
        {
            children = dir.EnumerateFileSystemInfos().ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            skipped.Add(new ConfigBackupSkip { Path = relDir + "/", Reason = "資料夾讀不到" });
            return;
        }

        foreach (var child in children)
        {
            var rel = relDir.Length == 0 ? child.Name : relDir + "/" + child.Name;
            var isDir = child is DirectoryInfo;

            // 目錄連結可能指回自己或別處，不跟進去。
            if ((child.Attributes & FileAttributes.ReparsePoint) != 0) continue;

            var rule = ExclusionFor(rel, isDir);
            if (rule != null)
            {
                if (!excluded.TryGetValue(rule, out var t)) excluded[rule] = t = new ConfigBackupExcludedTotal { Pattern = rule };
                if (isDir) AddDirectorySize((DirectoryInfo)child, t);
                else
                {
                    t.Files++;
                    t.Bytes += ((FileInfo)child).Length;
                }

                continue;
            }

            if (isDir)
            {
                if (SameDir(child.FullName, BackupDir)) continue;
                CollectFiles((DirectoryInfo)child, rel, files, excluded, skipped);
            }
            else files.Add((rel, (FileInfo)child));
        }
    }

    private static bool SameDir(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'),
            StringComparison.OrdinalIgnoreCase);

    private static void AddDirectorySize(DirectoryInfo dir, ConfigBackupExcludedTotal total)
    {
        try
        {
            foreach (var f in dir.EnumerateFiles("*", new EnumerationOptions
                     {
                         RecurseSubdirectories = true, IgnoreInaccessible = true,
                         AttributesToSkip = FileAttributes.ReparsePoint,
                     }))
            {
                total.Files++;
                total.Bytes += f.Length;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// 輪替：留最新 <paramref name="keepRecent"/> 份、最近 <paramref name="keepDays"/> 天每天最新的一份，
    /// 以及最新一份乾淨的（永遠不刪）。沒寫完的殘檔超過一小時才清。
    /// </summary>
    public ConfigBackupRotation Rotate(int keepRecent, int keepDays, DateTime nowUtc)
    {
        var manifests = LoadManifests();
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var m in manifests.Take(Math.Max(1, keepRecent))) keep.Add(m.ZipName);

        var cutoffDay = nowUtc.ToLocalTime().Date.AddDays(-(Math.Max(1, keepDays) - 1));
        var seenDays = new HashSet<DateTime>();
        foreach (var m in manifests)
        {
            var day = m.CreatedUtc.ToLocalTime().Date;
            if (day < cutoffDay) continue;
            if (seenDays.Add(day)) keep.Add(m.ZipName);
        }

        var pinned = manifests.FirstOrDefault(m => m.IsClean)?.ZipName;
        if (pinned != null) keep.Add(pinned);

        var deleted = 0;
        foreach (var m in manifests)
        {
            if (keep.Contains(m.ZipName)) continue;
            TryDelete(System.IO.Path.Combine(BackupDir, m.ZipName));
            TryDelete(System.IO.Path.Combine(BackupDir, System.IO.Path.ChangeExtension(m.ZipName, ".json")));
            deleted++;
        }

        long totalBytes = 0;
        if (Directory.Exists(BackupDir))
        {
            // 沒有清單的 zip 不刪：清單可能是被非正常關機弄壞的，zip 本身仍可手動解開。
            foreach (var f in new DirectoryInfo(BackupDir).EnumerateFiles(ZipPrefix + "*"))
            {
                if (f.Name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                {
                    if (nowUtc - f.LastWriteTimeUtc > TimeSpan.FromHours(1)) TryDelete(f.FullName);
                    continue;
                }

                totalBytes += f.Length;
            }
        }

        return new ConfigBackupRotation(keep.Count, deleted, pinned, totalBytes);
    }

    /// <summary>
    /// 從指定備份還原一個檔。現有的檔先另存到 <see cref="SavedBeforeRestoreDir"/>。
    /// </summary>
    /// <returns>另存的路徑（原本沒有這個檔時為空字串）。失敗擲例外，訊息給人看。</returns>
    /// <remarks>🔴 呼叫端負責先確認所屬插件沒有載入；這裡無從得知。</remarks>
    public string RestoreFile(string zipName, string relPath, DateTime nowUtc)
    {
        if (zipName.Contains('/') || zipName.Contains('\\') || !zipName.StartsWith(ZipPrefix, StringComparison.Ordinal))
            throw new InvalidOperationException("備份檔名不合法");

        var target = Path.GetFullPath(Path.Combine(SourceDir, relPath));
        if (!IsInside(target, SourceDir) || SameDir(target, SourceDir))
            throw new InvalidOperationException("還原路徑不在插件設定資料夾裡");

        byte[] data;
        using (var zip = ZipFile.OpenRead(Path.Combine(BackupDir, zipName)))
        {
            var entry = zip.GetEntry(relPath) ?? throw new FileNotFoundException("備份裡沒有這個檔", relPath);
            using var es = entry.Open();
            using var ms = new MemoryStream();
            es.CopyTo(ms);
            data = ms.ToArray();
            if (data.Length != entry.Length) throw new InvalidDataException("備份內容長度對不上");
        }

        if (IsProblem(Classify(relPath, data, 0)))
            throw new InvalidDataException("備份裡的這份也不完整，沒有還原");

        var saved = string.Empty;
        if (File.Exists(target))
        {
            var stamp = nowUtc.ToLocalTime().ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
            saved = Path.Combine(BackupDir, SavedBeforeRestoreDir, stamp, relPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(saved)!);
            File.Copy(target, saved, overwrite: false);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var tmp = target + ".tcbak-restore.tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(data, 0, data.Length);
            fs.Flush(true);
        }

        File.Move(tmp, target, overwrite: true);
        return saved;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

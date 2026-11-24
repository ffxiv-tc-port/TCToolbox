using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 插件設定自動備份：載入時與每天一次把 pluginConfigs 壓成 zip，同時檢查設定檔有沒有受損，引導使用者還原。
/// </summary>
/// <remarks>
/// 🔴 執行緒：所有檔案 I/O 都在 <see cref="Task.Run(Action)"/> 裡、整段 try/catch；框架執行緒只排工作、讀快照、畫 UI。
/// 🔴 不自動還原（＝改使用者設定）。還原只在所屬插件沒載入時准按：載入中的插件關閉時會把記憶體裡的設定整份寫回。
/// </remarks>
public sealed class PluginConfigBackup : TcModule
{
    /// <summary>存檔用識別名。<see cref="Configuration"/> 的一次性預設啟用要引用它，所以是常數。</summary>
    public const string ModuleInternalName = "PluginConfigBackup";

    /// <summary>給人看的備份位置（不展開成含帳號名的實際路徑，log 也用這個）。</summary>
    private const string BackupDirDisplay = @"%LOCALAPPDATA%\TCToolbox\PluginConfigBackups";

    /// <summary>載入後等多久才掃：讓同時載入的插件先把各自的設定讀完、該重寫的重寫完。</summary>
    private const int LoadDelayMs = 20_000;

    private const int DailyHours = 24;
    private const int RetryAfterFailureMinutes = 60;

    public override string InternalName => ModuleInternalName;

    public override string DisplayName => "插件設定自動備份";

    public override string Description =>
        "每次載入與每天一次，把所有插件的設定檔壓成 zip，存在插件設定資料夾以外（" + BackupDirDisplay + "）。" +
        "同時檢查設定檔有沒有變成全 NUL、空檔、JSON 不完整，或比上次完好時小很多（可能被重置成預設值）；" +
        "發現就在這裡列出、聊天提示一次。還原要你自己按，而且只在該插件停用時能按。" +
        "戰鬥錄影、導航網格快取、記錄檔等大型可重建的資料不備份。";

    public override ModuleCategory Category => ModuleCategory.Misc;

    public override bool HasConfigUI => true;

    private PluginConfigBackupConfig Config => Plugin.Instance.Config.PluginConfigBackup;

    private int KeepRecent => Math.Clamp(Config.KeepRecent, 3, 30);

    private int KeepDays => Math.Clamp(Config.KeepDays, 1, 60);

    /// <summary>主視窗頂端要顯示的一行（<c>null</c>＝沒事）。任何執行緒寫、Draw 讀。</summary>
    public static string? BannerText => banner;

    private static volatile string? banner;

    private ConfigBackupEngine? engine;
    private string? setupError;
    private CancellationTokenSource? cts;

    /// <summary>每次啟用加一；背景工作帶著它，回來時對不上就丟掉結果。</summary>
    private int generation;

    /// <summary>1＝有背景工作在跑。同時只跑一件。</summary>
    private int busy;

    private volatile ConfigBackupScanResult? lastScan;
    private volatile ConfigBackupRotation? lastRotation;
    private volatile string? lastError;
    private volatile string? lastActionMessage;
    private long lastBackupUtcTicks;

    /// <summary>背景失敗只記一次 log（每次啟用重置）。</summary>
    private int failureLogged;

    /// <summary>背景寫、框架執行緒在登入狀態下印出後清掉。同一組問題檔一場只提示一次。</summary>
    private volatile string? pendingChat;
    private volatile string chatSignature = string.Empty;

    // ↓ 只在框架執行緒讀寫。
    private long nextTickMs;
    private long loadScanAtMs;
    private bool loadScanPending;
    private DateTime nextDailyAttemptUtc;

    private Dictionary<string, (string Name, bool Loaded)> pluginSnapshot = new(StringComparer.OrdinalIgnoreCase);
    private long pluginSnapshotAtMs;

    protected override void OnEnable()
    {
        Interlocked.Increment(ref generation);
        lastScan = null;
        lastRotation = null;
        lastError = null;
        lastActionMessage = null;
        failureLogged = 0;
        Interlocked.Exchange(ref lastBackupUtcTicks, 0);
        banner = null;
        pendingChat = null;
        chatSignature = string.Empty;

        engine = CreateEngine(out setupError);
        if (engine == null)
            Svc.Log.Warning($"[{ModuleInternalName}] 無法啟用備份：{setupError}");

        cts = new CancellationTokenSource();
        loadScanPending = true;
        loadScanAtMs = Environment.TickCount64 + LoadDelayMs;
        nextDailyAttemptUtc = DateTime.MinValue;
        Svc.Framework.Update += OnUpdate;
    }

    protected override void OnDisable()
    {
        Svc.Framework.Update -= OnUpdate;
        Interlocked.Increment(ref generation);
        Interlocked.Exchange(ref cts, null)?.Cancel();
        engine = null;
        banner = null;
        pendingChat = null;
    }

    private static ConfigBackupEngine? CreateEngine(out string? error)
    {
        error = null;
        var source = Svc.PluginInterface.ConfigDirectory.Parent?.FullName;
        if (string.IsNullOrEmpty(source) || !Directory.Exists(source))
        {
            error = "找不到插件設定資料夾";
            return null;
        }

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(local))
        {
            error = "取不到 %LOCALAPPDATA%";
            return null;
        }

        var backupDir = Path.Combine(local, "TCToolbox", "PluginConfigBackups");
        if (ConfigBackupEngine.IsInside(backupDir, source) || ConfigBackupEngine.IsInside(source, backupDir))
        {
            error = "備份位置和插件設定資料夾重疊";
            return null;
        }

        return new ConfigBackupEngine(source, backupDir);
    }

    private void OnUpdate(IFramework framework)
    {
        var now = Environment.TickCount64;
        if (now < nextTickMs) return;
        nextTickMs = now + 1_000;

        if (pendingChat is { } chat && Svc.ClientState.IsLoggedIn)
        {
            pendingChat = null;
            Svc.Chat.Print(chat);
        }

        if (engine == null) return;

        if (loadScanPending)
        {
            if (now < loadScanAtMs) return;
            if (!StartWork(Work.Backup, "load")) return;
            loadScanPending = false;
            nextDailyAttemptUtc = DateTime.UtcNow.AddMinutes(RetryAfterFailureMinutes);
            return;
        }

        var nowUtc = DateTime.UtcNow;
        if (nowUtc < nextDailyAttemptUtc) return;

        // last == 0＝載入那次失敗了：照失敗重試的間隔再試，不等一整天。
        var last = Interlocked.Read(ref lastBackupUtcTicks);
        if (last != 0 && nowUtc - new DateTime(last, DateTimeKind.Utc) < TimeSpan.FromHours(DailyHours)) return;

        nextDailyAttemptUtc = nowUtc.AddMinutes(RetryAfterFailureMinutes);
        StartWork(Work.Backup, "daily");
    }

    private enum Work
    {
        Backup,
        ScanOnly,
        Restore,
    }

    /// <summary>框架執行緒：排一件背景工作。已有工作在跑就回 false（呼叫端下一秒再試）。</summary>
    private bool StartWork(Work kind, string trigger, ConfigBackupIssue? restoreTarget = null)
    {
        var eng = engine;
        var tokenSource = cts;
        if (eng == null || tokenSource == null) return false;
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) return false;

        // 設定只在框架執行緒碰：這裡抄一份快照帶進背景。
        // 逐筆用索引子抄：大小寫不同的重複鍵用建構子抄會擲例外，而這裡在 Framework.Update 上。
        var acks = new Dictionary<string, ConfigBackupAck>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, ack) in Config.Acknowledged ?? []) acks[path] = ack;
        var keepRecent = KeepRecent;
        var keepDays = KeepDays;
        var gen = Volatile.Read(ref generation);
        var token = tokenSource.Token;

        _ = Task.Run(() => RunWork(eng, kind, trigger, restoreTarget, acks, keepRecent, keepDays, gen, token));
        return true;
    }

    /// <summary>背景執行緒。🔴 整段 try/catch：執行緒池上漏出的例外會讓遊戲結束。只用 <c>Svc.Log</c>（執行緒安全）。</summary>
    private void RunWork(
        ConfigBackupEngine eng, Work kind, string trigger, ConfigBackupIssue? restoreTarget,
        Dictionary<string, ConfigBackupAck> acks, int keepRecent, int keepDays, int gen, CancellationToken token)
    {
        try
        {
            if (kind == Work.Restore && restoreTarget != null)
            {
                var saved = eng.RestoreFile(restoreTarget.GoodZipName!, restoreTarget.Path, DateTime.UtcNow);
                var msg = $"已從 {FormatLocal(restoreTarget.GoodBackupUtc)} 的備份還原 {restoreTarget.Path}"
                          + (saved.Length > 0 ? "（原本的檔已另存到備份資料夾的 restore-saved）" : string.Empty);
                Svc.Log.Information($"[{ModuleInternalName}] {msg}");
                if (gen == Volatile.Read(ref generation)) lastActionMessage = msg;
            }

            var result = eng.Scan(kind == Work.Backup, trigger, acks, DateTime.UtcNow, token);
            var rotation = result.WroteBackup ? eng.Rotate(keepRecent, keepDays, DateTime.UtcNow) : lastRotation;

            if (gen != Volatile.Read(ref generation)) return;

            lastScan = result;
            lastRotation = rotation;
            lastError = null;
            if (result.WroteBackup)
                Interlocked.Exchange(ref lastBackupUtcTicks, result.Manifest.CreatedUtcTicks);
            else if (Interlocked.Read(ref lastBackupUtcTicks) == 0)
                Interlocked.Exchange(ref lastBackupUtcTicks, NewestBackupTicks(eng));

            LogResult(result, rotation);
            PublishIssues(result.Issues);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            var text = $"{ex.GetType().Name}：{Sanitize(eng, ex.Message)}";
            if (gen == Volatile.Read(ref generation))
            {
                lastError = kind == Work.Restore ? $"還原失敗：{text}" : $"備份失敗：{text}";
                if (kind == Work.Restore) lastActionMessage = lastError;
            }

            if (Interlocked.Exchange(ref failureLogged, 1) == 0)
                Svc.Log.Warning($"[{ModuleInternalName}] {(kind == Work.Restore ? "還原" : "備份")}失敗（之後同類失敗不再記）：{text}");
        }
        finally
        {
            Interlocked.Exchange(ref busy, 0);
        }
    }

    private static long NewestBackupTicks(ConfigBackupEngine eng)
    {
        var newest = eng.LoadManifests().FirstOrDefault();
        return newest?.CreatedUtcTicks ?? 0;
    }

    /// <summary>例外訊息可能帶完整路徑（含帳號名），寫進 log／畫面前換成代稱。</summary>
    private static string Sanitize(ConfigBackupEngine eng, string message)
    {
        var s = message.Replace(eng.BackupDir, BackupDirDisplay, StringComparison.OrdinalIgnoreCase)
            .Replace(eng.SourceDir, "<pluginConfigs>", StringComparison.OrdinalIgnoreCase);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(profile) ? s : s.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
    }

    private static void LogResult(ConfigBackupScanResult result, ConfigBackupRotation? rotation)
    {
        var m = result.Manifest;
        var raw = m.Files.Sum(f => f.Size);
        var excluded = m.Excluded.Sum(e => e.Bytes);
        var head = result.WroteBackup
            ? $"備份完成（{TriggerText(m.Trigger)}）：{m.Files.Count} 個檔 {FormatBytes(raw)} → zip {FormatBytes(m.ZipSize)}"
            : $"重新檢查：{m.Files.Count} 個檔 {FormatBytes(raw)}";
        Svc.Log.Information(
            $"[{ModuleInternalName}] {head}；排除 {FormatBytes(excluded)}；略過 {m.Skipped.Count} 個；問題檔 {result.Issues.Count} 個"
            + (rotation != null ? $"；保留 {rotation.Kept} 份共 {FormatBytes(rotation.TotalBytes)}、本次刪 {rotation.Deleted} 份" : string.Empty)
            + $"；位置 {BackupDirDisplay}");

        foreach (var s in m.Skipped.Take(20))
            Svc.Log.Information($"[{ModuleInternalName}] 略過 {s.Path}（{FormatBytes(s.Size)}）：{s.Reason}");
        if (m.Skipped.Count > 20)
            Svc.Log.Information($"[{ModuleInternalName}] 另有 {m.Skipped.Count - 20} 個略過的檔沒列出");

        foreach (var i in result.Issues)
            Svc.Log.Information(
                $"[{ModuleInternalName}] 問題檔 {i.Path}：{StatusText(i)}；最近完好備份 {FormatLocal(i.GoodBackupUtc)}");
    }

    /// <summary>背景執行緒：更新頂端提示、需要時排一次聊天提示。</summary>
    private void PublishIssues(IReadOnlyList<ConfigBackupIssue> issues)
    {
        if (issues.Count == 0)
        {
            banner = null;
            return;
        }

        var owners = issues.Select(i => i.Owner).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var names = string.Join("、", owners.Take(5)) + (owners.Count > 5 ? $" 等 {owners.Count} 個插件" : string.Empty);
        banner = $"插件設定可能受損：{names}（{issues.Count} 個檔）。到「介面 · 雜項」→「插件設定自動備份」查看與還原。";

        var signature = string.Join("|", issues.Select(i => $"{i.Path}:{(int)i.Status}").OrderBy(x => x, StringComparer.Ordinal));
        // 背景工作同時只有一件（busy），這裡不會並行。
        if (signature == chatSignature) return;
        chatSignature = signature;

        pendingChat = $"[TC Toolbox] 偵測到 {issues.Count} 個插件設定檔可能受損（{names}）。"
                      + $"輸入 {Plugin.Command} 到「介面 · 雜項」→「插件設定自動備份」查看與還原。";
    }

    public override ModuleNotice? RowNotice
    {
        get
        {
            if (!IsEnabled) return null;
            if (setupError != null)
                return new ModuleNotice(ModuleNoticeLevel.Warning, "無法備份", setupError);

            var scan = lastScan;
            if (scan is { Issues.Count: > 0 })
                return new ModuleNotice(ModuleNoticeLevel.Warning, $"{scan.Issues.Count} 個設定檔可能受損",
                    "展開設定查看是哪些檔、最近一份完好備份是什麼時候。");
            if (lastError is { } err)
                return new ModuleNotice(ModuleNoticeLevel.Warning, "上次備份失敗", err);
            if (scan == null)
                return new ModuleNotice(ModuleNoticeLevel.Unknown, "尚未檢查", "載入後約 20 秒做第一次備份與檢查。");
            return null;
        }
    }

    private static readonly Vector4 WarnColor = new(1f, 0.65f, 0.25f, 1f);
    private static readonly Vector4 OkColor = new(0.55f, 0.85f, 0.55f, 1f);

    public override void DrawConfig()
    {
        var eng = engine;
        if (eng == null)
        {
            ImGui.TextColored(WarnColor, $"無法備份：{setupError}");
            return;
        }

        DrawStatus(eng);
        ImGui.Separator();
        DrawIssues();
        ImGui.Separator();
        DrawRetention();
        DrawExclusions();
    }

    private void DrawStatus(ConfigBackupEngine eng)
    {
        ImGui.TextUnformatted($"備份位置：{BackupDirDisplay}");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(eng.BackupDir);
        ImGui.SameLine();
        if (ImGui.SmallButton("開啟資料夾##cfgbakOpen"))
        {
            try
            {
                Directory.CreateDirectory(eng.BackupDir);
                Util.OpenLink(eng.BackupDir);
            }
            catch (Exception ex)
            {
                lastActionMessage = $"開啟資料夾失敗：{Sanitize(eng, ex.Message)}";
            }
        }

        var running = Volatile.Read(ref busy) == 1;
        var scan = lastScan;
        var rotation = lastRotation;
        var last = Interlocked.Read(ref lastBackupUtcTicks);

        if (running) ImGui.TextDisabled("備份／檢查中…");
        else if (last == 0) ImGui.TextDisabled(loadScanPending ? "尚未備份（載入後約 20 秒開始）。" : "尚未備份。");
        else
        {
            var text = $"上次備份：{FormatLocal(new DateTime(last, DateTimeKind.Utc))}";
            if (scan is { WroteBackup: true } s)
            {
                text += $"（{TriggerText(s.Manifest.Trigger)}）· {s.Manifest.Files.Count} 個檔 "
                        + $"{FormatBytes(s.Manifest.Files.Sum(f => f.Size))} → zip {FormatBytes(s.Manifest.ZipSize)}";
            }

            ImGui.TextUnformatted(text);
        }

        if (rotation != null)
        {
            ImGui.TextDisabled($"目前保留 {rotation.Kept} 份，共 {FormatBytes(rotation.TotalBytes)}。");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(rotation.PinnedZip != null
                    ? $"最新一份沒有問題的備份（{rotation.PinnedZip}）永遠不會被輪替刪掉。"
                    : "還沒有任何一份「沒有問題」的備份；目前的備份都照一般規則輪替。");
        }

        if (lastError is { } err) ImGui.TextColored(WarnColor, err);

        using (ImRaii.Disabled(running))
        {
            if (ImGui.Button("立刻備份##cfgbakNow")) StartWork(Work.Backup, "manual");
            ImGui.SameLine();
            if (ImGui.Button("重新檢查##cfgbakRescan")) StartWork(Work.ScanOnly, "scan");
        }

        if (lastActionMessage is { } msg) ImGui.TextWrapped(msg);
    }

    private void DrawIssues()
    {
        var scan = lastScan;
        if (scan == null)
        {
            ImGui.TextDisabled("尚未檢查。");
            return;
        }

        if (scan.Issues.Count == 0)
        {
            ImGui.TextColored(OkColor, $"已檢查 {scan.Manifest.Files.Count} 個檔，沒有發現受損或被重置的設定。");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"檢查時間：{FormatLocal(scan.FinishedUtc)}");
            return;
        }

        ImGui.TextColored(WarnColor, $"{scan.Issues.Count} 個設定檔可能受損：");
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
        ImGui.TextDisabled("還原只在該插件停用時能按：插件關閉時會把記憶體裡的設定整份寫回檔案，" +
                           "載入中還原會在它關閉那一刻被靜默蓋掉。請先到插件安裝器停用它，還原後再啟用。" +
                           "還原前會把現在的檔另存到備份資料夾的 restore-saved。");
        ImGui.PopTextWrapPos();

        RefreshPluginSnapshot();
        var running = Volatile.Read(ref busy) == 1;

        if (!ImGui.BeginTable("##cfgbakIssues", 4,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.SizingStretchProp))
            return;

        try
        {
            ImGui.TableSetupColumn("插件 / 檔案", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("狀況", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("最近完好備份", ImGuiTableColumnFlags.WidthFixed, UiScale.Px(110f));
            ImGui.TableSetupColumn("動作", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableHeadersRow();

            for (var idx = 0; idx < scan.Issues.Count; idx++)
            {
                var issue = scan.Issues[idx];
                using var id = ImRaii.PushId(idx);
                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                (string Name, bool Loaded)? plugin =
                    pluginSnapshot.TryGetValue(issue.Owner, out var p) ? p : null;
                ImGui.TextUnformatted(plugin?.Name ?? issue.Owner);
                ImGui.TextDisabled(issue.Path);

                ImGui.TableNextColumn();
                ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
                ImGui.TextUnformatted(StatusText(issue));
                ImGui.PopTextWrapPos();

                ImGui.TableNextColumn();
                if (issue.GoodBackupUtc is { } goodUtc) ImGui.TextUnformatted(FormatLocal(goodUtc));
                else ImGui.TextDisabled("沒有");

                ImGui.TableNextColumn();
                var blocker = ConfigBackupEngine.RestoreBlocker(issue.GoodZipName != null, plugin);

                using (ImRaii.Disabled(blocker != null || running))
                {
                    if (ImGui.SmallButton("還原")) Restore(issue);
                }

                ImGui.SameLine();
                using (ImRaii.Disabled(running))
                {
                    if (ImGui.SmallButton("這是我自己改的")) Acknowledge(issue);
                }

                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip("不再對這個檔的現狀提醒，直到它再被修改。之後的備份也會把它當成完好。");

                if (blocker != null)
                {
                    ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
                    ImGui.TextColored(WarnColor, blocker);
                    ImGui.PopTextWrapPos();
                }
            }
        }
        finally
        {
            ImGui.EndTable();
        }
    }

    /// <summary>框架執行緒：按下還原。🔴 當下重新列舉一次「有沒有載入」，不信畫面上的快取。</summary>
    private void Restore(ConfigBackupIssue issue)
    {
        if (ConfigBackupEngine.RestoreBlocker(issue.GoodZipName != null, QueryPlugin(issue.Owner)) is { } blocker)
        {
            lastActionMessage = $"沒有還原：{blocker}";
            return;
        }

        if (!StartWork(Work.Restore, "scan", issue))
            lastActionMessage = "有備份／檢查正在進行，請稍候再按一次。";
    }

    private void Acknowledge(ConfigBackupIssue issue)
    {
        Config.Acknowledged ??= [];
        Config.Acknowledged[issue.Path] = new ConfigBackupAck { Size = issue.Size, WriteUtcTicks = issue.WriteUtcTicks };
        Plugin.Instance.Config.Save();
        Svc.Log.Information($"[{ModuleInternalName}] 使用者確認 {issue.Path} 的現狀是自己改的（{StatusText(issue)}）");
        StartWork(Work.ScanOnly, "scan");
    }

    /// <returns><see langword="null"/>＝沒有同名的已安裝插件，或列舉失敗（兩者都不准還原）。</returns>
    private static (string Name, bool Loaded)? QueryPlugin(string internalName)
    {
        try
        {
            foreach (var p in Svc.PluginInterface.InstalledPlugins)
            {
                if (string.Equals(p.InternalName, internalName, StringComparison.OrdinalIgnoreCase))
                    return (p.Name, p.IsLoaded);
            }
        }
        catch (Exception ex)
        {
            if (Throttle.Pass($"{ModuleInternalName}-Plugins", 60_000))
                Svc.Log.Information($"[{ModuleInternalName}] 列舉已安裝插件失敗：{ex.Message}");
        }

        return null;
    }

    /// <summary>畫面用的插件清單，一秒最多重抓一次（Draw 路徑上不每幀列舉）。</summary>
    private void RefreshPluginSnapshot()
    {
        var now = Environment.TickCount64;
        if (now - pluginSnapshotAtMs < 1_000) return;
        pluginSnapshotAtMs = now;

        var map = new Dictionary<string, (string, bool)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var p in Svc.PluginInterface.InstalledPlugins)
                map[p.InternalName] = (p.Name, p.IsLoaded);
        }
        catch (Exception ex)
        {
            if (Throttle.Pass($"{ModuleInternalName}-Plugins", 60_000))
                Svc.Log.Information($"[{ModuleInternalName}] 列舉已安裝插件失敗：{ex.Message}");
        }

        pluginSnapshot = map;
    }

    private void DrawRetention()
    {
        var recent = KeepRecent;
        if (ImGui.SliderInt("至少保留最新幾份##cfgbakKeepRecent", ref recent, 3, 30))
        {
            Config.KeepRecent = recent;
            Plugin.Instance.Config.Save();
        }

        var days = KeepDays;
        if (ImGui.SliderInt("最近幾天每天留一份##cfgbakKeepDays", ref days, 1, 60))
        {
            Config.KeepDays = days;
            Plugin.Instance.Config.Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("輪替在每次備份後進行。另外，最新一份沒有問題的備份永遠不刪。");
    }

    private void DrawExclusions()
    {
        if (!ImGui.TreeNodeEx("沒有備份的東西##cfgbakExcluded")) return;

        try
        {
            ImGui.TextDisabled($"單檔超過 {FormatBytes(ConfigBackupEngine.DefaultMaxFileBytes)} 不收；"
                               + $"一份備份最多收 {FormatBytes(ConfigBackupEngine.DefaultMaxTotalBytes)}（原始大小）。");

            var manifest = lastScan?.Manifest;
            foreach (var rule in ConfigBackupEngine.Exclusions)
            {
                var total = manifest?.Excluded.FirstOrDefault(e => e.Pattern == rule.Pattern);
                var size = total == null ? "?" : total.Files == 0 ? "本次沒有" : $"{total.Files} 個檔 {FormatBytes(total.Bytes)}";
                ImGui.BulletText($"{rule.Pattern}：{rule.Reason}（{size}）");
            }

            if (manifest == null || manifest.Skipped.Count == 0) return;

            ImGui.Spacing();
            ImGui.TextUnformatted($"上次略過 {manifest.Skipped.Count} 個檔：");
            foreach (var s in manifest.Skipped.Take(50))
                ImGui.BulletText($"{s.Path}（{FormatBytes(s.Size)}）：{s.Reason}");
            if (manifest.Skipped.Count > 50)
                ImGui.TextDisabled($"…另有 {manifest.Skipped.Count - 50} 個，完整清單在備份資料夾的清單檔裡。");
        }
        finally
        {
            ImGui.TreePop();
        }
    }

    private static string StatusText(ConfigBackupIssue i) => i.Status switch
    {
        ConfigFileStatus.Nul => "開頭全是 NUL（寫到一半被中斷，常見於非正常關機）",
        ConfigFileStatus.Empty => "變成空檔",
        ConfigFileStatus.BadJson => "JSON 不完整（被截斷或夾雜 NUL）",
        ConfigFileStatus.Shrunk =>
            $"比最近一次完好時小很多（{FormatBytes(i.ReferenceSize)} → {FormatBytes(i.Size)}），可能被重置成預設值",
        _ => i.Status.ToString(),
    };

    private static string TriggerText(string trigger) => trigger switch
    {
        "load" => "載入時",
        "daily" => "每日",
        "manual" => "手動",
        _ => trigger,
    };

    private static string FormatLocal(DateTime? utc) =>
        utc is { } t ? t.ToLocalTime().ToString("MM-dd HH:mm") : "沒有";

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024d * 1024 * 1024):0.0} GB",
        >= 1024L * 1024 => $"{bytes / (1024d * 1024):0.0} MB",
        >= 1024 => $"{bytes / 1024d:0.0} KB",
        _ => $"{bytes} B",
    };
}

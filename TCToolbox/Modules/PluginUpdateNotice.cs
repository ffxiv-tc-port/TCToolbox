using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 台服外掛可更新提示：定期比對台服外掛庫與已安裝版本，有新版時在伺服器資訊列顯示數量。
/// </summary>
/// <remarks>
/// 🔴 <b>純顯示</b>：只 GET <see cref="FeedUrl"/> 這一個位址，不下載、不安裝、不呼叫更新 API。
/// 🔴 <b>執行緒</b>：抓取與解析在背景（只碰純資料）；與已安裝清單的比對一律在框架執行緒。
/// ⚠️ 一次都沒抓成功過時列上顯示 <c>?</c> 而不是 0——畫成 0 等於宣稱「沒有更新」，那是反話。
/// </remarks>
public sealed class PluginUpdateNotice : TcModule
{
    /// <summary>存檔用識別名。<see cref="Configuration"/> 的一次性預設啟用要引用它，所以是常數。</summary>
    public const string ModuleInternalName = "PluginUpdateNotice";

    /// <summary>🔴 本模組唯一准連的位址。</summary>
    private const string FeedUrl =
        "https://raw.githubusercontent.com/ffxiv-tc-port/DalamudPluginsTC/main/repo.json";

    /// <summary>檢查間隔的可設定範圍（分鐘）。</summary>
    public const int MinIntervalMinutes = 30;

    /// <summary>檢查間隔的可設定範圍（分鐘）。</summary>
    public const int MaxIntervalMinutes = 180;

    /// <summary>連續失敗時第一次重試的間隔（分鐘），之後每次翻倍。</summary>
    private const int FailureRetryBaseMinutes = 5;

    /// <summary>tooltip 裡最多列幾個（超過的以「…等共 N 個」帶過）。</summary>
    private const int TooltipPreviewCount = 12;

    private const int HttpTimeoutSeconds = 20;

    /// <summary>401／403／404 的簡短原因；配套說明是 <see cref="TokenHintDetail"/>。</summary>
    private const string TokenHintReason = "需要 GitHub 權杖";

    private const string TokenHintDetail =
        "台服插件庫已轉為私有：請在 Dalamud 設定的「試驗性功能」分頁填入有讀取權限的 GitHub 存取權杖。";

    private const int FeedSizeLimitBytes = 8 * 1024 * 1024;

    /// <summary>資訊列圖示：警告三角（與貨幣上限警示同一個語氣——有事要處理）。</summary>
    private const BitmapFontIcon DtrIcon = BitmapFontIcon.Warning;

    public override string InternalName => ModuleInternalName;

    public override string DisplayName => "台服插件可更新提示";

    public override string Description =>
        "定期比對台服插件庫與已安裝的版本，有新版時在伺服器資訊列顯示數量，滑鼠移上列出是哪幾個、" +
        "從哪一版到哪一版（停用中的插件另列、不計入數量）。點一下開啟 Dalamud 的插件安裝器。" +
        "純顯示：不會自動下載、不會自動安裝，也不會碰插件庫以外的任何網路位址。";

    public override ModuleCategory Category => ModuleCategory.Misc;

    public override bool HasConfigUI => true;

    private PluginUpdateNoticeConfig Config => Plugin.Instance.Config.PluginUpdateNotice;

    private int IntervalMinutes =>
        Math.Clamp(Config.IntervalMinutes, MinIntervalMinutes, MaxIntervalMinutes);

    private readonly record struct FeedEntry(string DisplayName, Version Version);

    /// <summary>可更新的插件目前的狀態，決定計不計入數字、要不要加註。</summary>
    private enum LoadKind
    {
        /// <summary>已載入。計入。</summary>
        Loaded,

        /// <summary>啟用中但沒載入（多半是載入失敗）。計入：更新正好可能修好它。</summary>
        LoadFailed,

        /// <summary>沒載入、也讀不到是否停用。照舊計入，寧可多報。</summary>
        NotLoadedUnknown,

        /// <summary>停用中。安裝器的「全部更新」與自動更新都跳過它，不計入。</summary>
        Disabled,
    }

    private readonly record struct PendingUpdate(
        string InternalName, string DisplayName, Version Installed, Version Available, LoadKind Kind);

    /// <summary>背景抓取的結果。<c>Entries</c> 為 null＝這次失敗，原因在 <c>Error</c>。</summary>
    /// <remarks>🔴 發布之後不再變動——這是它可以跨執行緒傳遞的唯一理由。</remarks>
    private sealed record FeedResult(Dictionary<string, FeedEntry>? Entries, string? Error);

    /// <summary>
    /// 模組對外呈現的整個狀態。框架執行緒寫、繪製路徑讀，靠參考指派的原子性＋內容不可變。
    /// </summary>
    /// <param name="Pending">上次<b>成功</b>比對的結果，停用中的除外；從未成功時是空清單。</param>
    /// <param name="Inactive">有新版但停用中的插件；不計入列上的數字。</param>
    /// <param name="EverSucceeded">有沒有成功過至少一次——false 時列上要顯示「?」。</param>
    /// <param name="LastSuccessUtc">從未成功時是 <c>default</c>。</param>
    /// <param name="LastError">上次嘗試的失敗原因；null＝上次是成功的。</param>
    private sealed record NoticeState(
        List<PendingUpdate> Pending,
        List<PendingUpdate> Inactive,
        bool EverSucceeded,
        int FeedCount,
        DateTime LastSuccessUtc,
        DateTime LastAttemptUtc,
        string? LastError);

    private static readonly List<PendingUpdate> NoPending = [];

    /// <summary>目前狀態。null＝還沒嘗試過任何一次（這時候沒有話要說，條目藏起來）。</summary>
    private volatile NoticeState? state;

    /// <summary>背景抓完丟過來的結果；框架執行緒用 <c>Interlocked.Exchange</c> 取走。</summary>
    private FeedResult? incomingFeed;

    /// <summary>0＝閒置，1＝有一次抓取還沒回來。</summary>
    private int fetchInFlight;

    private IDtrBarEntry? dtrEntry;
    private CancellationTokenSource? cts;
    private HttpClient? http;

    /// <summary>下次該去問的時間。<b>只在框架執行緒讀寫。</b></summary>
    private DateTime nextCheckUtc;

    /// <summary>連續失敗次數，決定退避長度。<b>只在框架執行緒讀寫。</b></summary>
    private int failureStreak;

    /// <summary>上次成功抓到的插件庫，給「插件載入狀態變了」時重新比對用。<b>只在框架執行緒讀寫。</b></summary>
    private Dictionary<string, FeedEntry>? lastFeed;

    /// <summary>
    /// 有插件載入／卸載過、要用 <see cref="lastFeed"/> 重新比對。
    /// 事件可能從任何執行緒來，所以那邊只設旗標，比對留給框架執行緒。
    /// </summary>
    private volatile bool recompareRequested;

    protected override void OnEnable()
    {
        state = null;
        incomingFeed = null;
        fetchInFlight = 0;
        failureStreak = 0;
        nextCheckUtc = DateTime.UtcNow;
        lastFeed = null;
        recompareRequested = false;

        cts = new CancellationTokenSource();

        // 🔴 外掛庫的 repo 是私有的：要帶上使用者在 Dalamud 設定的 GitHub 權杖才讀得到。
        //    這個工廠回的是新實例、由呼叫端負責釋放，所以逾時與大小上限照樣設在它身上。
        http = Svc.PluginInterface.CreateGitHubHttpClient();
        http.Timeout = TimeSpan.FromSeconds(HttpTimeoutSeconds);

        // repo.json 現在約 200 KB；上界只是不讓對面的意外回應無上限地配置記憶體。
        http.MaxResponseContentBufferSize = FeedSizeLimitBytes;

        dtrEntry = Svc.DtrBar.Get("TC Toolbox 台服插件更新");
        dtrEntry.Shown = false;
        dtrEntry.Text = new SeString(new IconPayload(DtrIcon), new TextPayload("台服插件更新 ?"));
        dtrEntry.Tooltip = "TC Toolbox — 台服插件可更新提示";
        // 安裝器是 Dalamud 的視窗，左鍵只開不關。
        dtrEntry.OnClick = ev =>
        {
            if (ev.ClickType != MouseClickType.Left) Plugin.Instance.ToggleMainWindow();
            else OpenInstaller();
        };

        Svc.ClientState.Login += OnLogin;
        Svc.PluginInterface.ActivePluginsChanged += OnActivePluginsChanged;
        Svc.Framework.Update += OnUpdate;
    }

    protected override void OnDisable()
    {
        Svc.Framework.Update -= OnUpdate;
        Svc.PluginInterface.ActivePluginsChanged -= OnActivePluginsChanged;
        Svc.ClientState.Login -= OnLogin;

        // 先取消再收 HttpClient：進行中的請求會擲 OperationCanceled／ObjectDisposed，
        // 兩種都由抓取工作自己吞掉（見 RunFetch）。
        Interlocked.Exchange(ref cts, null)?.Cancel();
        Interlocked.Exchange(ref http, null)?.Dispose();

        dtrEntry?.Remove();
        dtrEntry = null;

        state = null;
        incomingFeed = null;
        fetchInFlight = 0;
        lastFeed = null;
        recompareRequested = false;
    }

    /// <summary>「沿用上次比對結果」那一行這一場寫過了沒；只在 <see cref="OnLogin"/>（框架執行緒）讀寫。</summary>
    private bool reuseLogged;

    /// <summary>任何執行緒：只記下「要重新比對」。</summary>
    private void OnActivePluginsChanged(IActivePluginsChangedEventArgs args) => recompareRequested = true;

    /// <summary>
    /// 登入時讓提示盡快出現：上次成功比對還在一個檢查間隔之內就直接用快取，否則排一次立刻檢查。
    /// 換角每一兩分鐘就進來一次，無條件重排等於每次登入都去打一次外掛庫。
    /// </summary>
    private void OnLogin()
    {
        var current = state;
        var now = DateTime.UtcNow;

        if (current is { EverSucceeded: true } &&
            now - current.LastSuccessUtc < TimeSpan.FromMinutes(IntervalMinutes))
        {
            var age = (int)(now - current.LastSuccessUtc).TotalMinutes;
            // 換角每一兩分鐘就進來一次：插件載入期間只寫第一次。
            if (!reuseLogged)
            {
                reuseLogged = true;
                Svc.Log.Debug(
                    $"[{ModuleInternalName}] 沿用 {age} 分鐘前的比對結果，這次登入不重新讀取插件庫（之後同樣沿用不再寫）。");
            }
            UpdateDtr();
            return;
        }

        nextCheckUtc = now;
    }

    private void OnUpdate(IFramework framework)
    {
        // 外掛庫一天才動幾次，每秒看一眼綽綽有餘；其餘的幀完全不做事。
        if (!Throttle.Pass("PluginUpdateNotice-Tick", 1_000)) return;

        ConsumeFeedResult();
        if (recompareRequested) RecompareWithLastFeed();

        if (DateTime.UtcNow >= nextCheckUtc) StartFetch();
    }

    /// <summary>排一次背景抓取（已經有一次在路上就不排）。</summary>
    private void StartFetch()
    {
        var client = http;
        var tokenSource = cts;
        if (client == null || tokenSource == null) return;

        if (Interlocked.CompareExchange(ref fetchInFlight, 1, 0) != 0) return;

        // 🔴 先把下次排程推開：回應遲到（或永遠不回來）時才不會每秒重排一次。
        nextCheckUtc = DateTime.UtcNow.AddMinutes(IntervalMinutes);

        var token = tokenSource.Token;
        _ = Task.Run(() => RunFetch(client, token), token);
    }

    /// <summary>背景執行緒：抓 + 解析。<b>不碰任何 Dalamud 服務</b>（<c>Svc.Log</c> 是 Serilog，執行緒安全）。</summary>
    private async Task RunFetch(HttpClient client, CancellationToken token)
    {
        try
        {
            using var response = await client.GetAsync(FeedUrl, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var payload = await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
            Interlocked.Exchange(ref incomingFeed, new FeedResult(ParseFeed(payload), null));
        }
        // 🔴 判準是「我們的 token 被取消了」，不是例外型別：HttpClient 的 Timeout 也擲
        //    OperationCanceledException，只看型別會把逾時當成停用而不回報結果——那樣
        //    fetchInFlight 永遠停在 1，模組從此靜默不再檢查。
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException
                                   && token.IsCancellationRequested)
        {
            // 停用／卸載途中，不是錯誤，也不要回報結果（模組已經不看了）。
        }
        catch (Exception ex)
        {
            Svc.Log.Information($"[{ModuleInternalName}] 取得台服插件庫失敗：{ex.GetType().Name} {ex.Message}");
            Interlocked.Exchange(ref incomingFeed, new FeedResult(null, Describe(ex)));
        }
    }

    /// <summary>把 <c>repo.json</c> 解成「InternalName → 版本」。</summary>
    /// <remarks>
    /// 🔴 比對一律 <see cref="StringComparer.Ordinal"/>：feed 裡的 <c>GatherbuddyReborn</c>
    /// 是小寫 b，那是刻意的，忽略大小寫會讓它對上錯的東西。
    /// 單筆格式不對只跳過那一筆，不讓整次抓取失敗。
    /// </remarks>
    private static Dictionary<string, FeedEntry> ParseFeed(byte[] payload)
    {
        var json = new ReadOnlyMemory<byte>(payload);

        // 現在的 repo.json 沒有 BOM，但它是腳本產生的；哪天多了 BOM 會讓 JsonDocument 直接擲例外。
        if (json.Length >= 3 && json.Span[0] == 0xEF && json.Span[1] == 0xBB && json.Span[2] == 0xBF)
            json = json[3..];

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new FormatException("插件庫最上層不是陣列");

        var entries = new Dictionary<string, FeedEntry>(StringComparer.Ordinal);
        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object) continue;

            if (!element.TryGetProperty("InternalName", out var nameNode) ||
                nameNode.ValueKind != JsonValueKind.String) continue;

            var internalName = nameNode.GetString();
            if (string.IsNullOrEmpty(internalName)) continue;

            if (!element.TryGetProperty("AssemblyVersion", out var versionNode) ||
                versionNode.ValueKind != JsonValueKind.String ||
                !Version.TryParse(versionNode.GetString(), out var version)) continue;

            var displayName = element.TryGetProperty("Name", out var displayNode) &&
                              displayNode.ValueKind == JsonValueKind.String
                ? displayNode.GetString()
                : null;

            entries[internalName] = new FeedEntry(
                string.IsNullOrEmpty(displayName) ? internalName : displayName, version);
        }

        return entries;
    }

    /// <summary>框架執行緒：收下背景結果、重排下次時間、更新資訊列。</summary>
    private void ConsumeFeedResult()
    {
        var result = Interlocked.Exchange(ref incomingFeed, null);
        if (result == null) return;

        Interlocked.Exchange(ref fetchInFlight, 0);
        var now = DateTime.UtcNow;
        var previous = state;

        if (result.Entries == null)
        {
            failureStreak++;

            // 退避上限就是正常間隔——再長就失去「比平常早一點重試」的意義了。
            var backoff = Math.Min(
                FailureRetryBaseMinutes << Math.Min(failureStreak - 1, 8), IntervalMinutes);
            nextCheckUtc = now.AddMinutes(backoff);

            state = new NoticeState(
                previous?.Pending ?? NoPending,
                previous?.Inactive ?? NoPending,
                previous?.EverSucceeded ?? false,
                previous?.FeedCount ?? 0,
                previous?.LastSuccessUtc ?? default,
                now,
                result.Error ?? "未知錯誤");
        }
        else
        {
            failureStreak = 0;
            nextCheckUtc = now.AddMinutes(IntervalMinutes);
            lastFeed = result.Entries;
            recompareRequested = false;
            var (active, inactive) = ComparePending(result.Entries);
            state = new NoticeState(active, inactive, true, result.Entries.Count, now, now, null);
            LogResult(state, $"插件庫 {state.FeedCount} 筆比對完成");
        }

        UpdateDtr();
    }

    /// <summary>
    /// 框架執行緒：插件載入／卸載之後，用上次抓到的插件庫重新分「可更新／停用中」。
    /// 不重新抓網路；結果沒變就什麼都不做。
    /// </summary>
    private void RecompareWithLastFeed()
    {
        recompareRequested = false;

        var current = state;
        var feed = lastFeed;
        if (current is not { EverSucceeded: true } || feed == null) return;

        var (active, inactive) = ComparePending(feed);
        if (SameList(active, current.Pending) && SameList(inactive, current.Inactive)) return;

        state = current with { Pending = active, Inactive = inactive };
        LogResult(state, "插件載入狀態有變，重新比對");
        UpdateDtr();
    }

    private static bool SameList(List<PendingUpdate> a, List<PendingUpdate> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
            if (a[i] != b[i]) return false;
        return true;
    }

    /// <summary>
    /// 把外掛庫與已安裝清單比出「可更新」的那些，停用中的另外放。<b>只在框架執行緒呼叫。</b>
    /// </summary>
    /// <remarks>
    /// 安裝器的「全部更新」與自動更新都跳過停用中的插件，計入數字只會讓使用者按了卻什麼都沒更新。
    /// 載入失敗的仍然計入——更新常常正好修好它。
    /// </remarks>
    private static (List<PendingUpdate> Active, List<PendingUpdate> Inactive) ComparePending(
        Dictionary<string, FeedEntry> feed)
    {
        var pending = new List<PendingUpdate>();
        var inactive = new List<PendingUpdate>();

        foreach (var plugin in Svc.PluginInterface.InstalledPlugins)
        {
            // 開發版是從本機目錄載入的，不走外掛庫更新，版號比對對它沒有意義。
            if (plugin.IsDev) continue;

            // 🔴 型別宣告是非可空，但值來自反序列化的 manifest：真的缺這個鍵時會是 null，
            //    而漏掉這一行的代價是 Draw 路徑上一個 NullReference。
            var installed = plugin.Version;
            if (installed is null) continue;

            if (!feed.TryGetValue(plugin.InternalName, out var entry)) continue;
            if (installed >= entry.Version) continue;

            // 只有「沒載入」才需要問是否停用，所以反射只碰到少數幾個插件。
            var kind = plugin.IsLoaded
                ? LoadKind.Loaded
                : WantStateReader.IsWanted(plugin) switch
                {
                    true => LoadKind.LoadFailed,
                    false => LoadKind.Disabled,
                    null => LoadKind.NotLoadedUnknown,
                };

            (kind == LoadKind.Disabled ? inactive : pending).Add(new PendingUpdate(
                plugin.InternalName, entry.DisplayName, installed, entry.Version, kind));
        }

        pending.Sort((a, b) => string.CompareOrdinal(a.InternalName, b.InternalName));
        inactive.Sort((a, b) => string.CompareOrdinal(a.InternalName, b.InternalName));
        return (pending, inactive);
    }

    /// <summary>
    /// 讀 Dalamud 內部 <c>LocalPlugin.IsWantedByAnyProfile</c>——安裝器「全部更新」與自動更新過濾用的就是它，
    /// 公開的 <see cref="IExposedPlugin"/> 沒有給。<b>只在框架執行緒呼叫。</b>
    /// </summary>
    /// <remarks>
    /// 欄位用型別找（實作是主建構子捕捉的 <c>LocalPlugin</c>），不綁編譯器產生的名字。
    /// 讀不到回 null，呼叫端照舊計入；失敗原因只寫一次 Information。
    /// </remarks>
    private static class WantStateReader
    {
        private const string LocalPluginTypeName = "Dalamud.Plugin.Internal.Types.LocalPlugin";

        private static Type? resolvedFor;
        private static FieldInfo? localPluginField;
        private static PropertyInfo? wantedProperty;
        private static bool failureLogged;

        public static bool? IsWanted(IExposedPlugin plugin)
        {
            try
            {
                var type = plugin.GetType();
                if (type != resolvedFor) Resolve(type);

                if (localPluginField == null || wantedProperty == null)
                    return Fail($"{type.FullName} 裡找不到 LocalPlugin 欄位或它的 IsWantedByAnyProfile");

                var localPlugin = localPluginField.GetValue(plugin);
                if (localPlugin == null)
                    return Fail($"{plugin.InternalName} 的 LocalPlugin 欄位是 null");

                return wantedProperty.GetValue(localPlugin) is bool wanted
                    ? wanted
                    : Fail($"{plugin.InternalName} 的 IsWantedByAnyProfile 不是 bool");
            }
            catch (Exception ex)
            {
                var cause = ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
                return Fail($"{plugin.InternalName}：{cause.GetType().Name} {cause.Message}");
            }
        }

        private static void Resolve(Type type)
        {
            resolvedFor = type;
            localPluginField = null;
            wantedProperty = null;

            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (field.FieldType.FullName != LocalPluginTypeName) continue;
                localPluginField = field;
                break;
            }

            var property = localPluginField?.FieldType.GetProperty(
                "IsWantedByAnyProfile", BindingFlags.Instance | BindingFlags.Public);
            wantedProperty = property is { CanRead: true } && property.PropertyType == typeof(bool) ? property : null;
        }

        private static bool? Fail(string reason)
        {
            if (!failureLogged)
            {
                failureLogged = true;
                Svc.Log.Information(
                    $"[{ModuleInternalName}] 讀不到插件是否停用（{reason}）；沒載入的插件一律照舊計入可更新數字。");
            }

            return null;
        }
    }

    /// <summary>
    /// 要使用者回報的診斷；量很小（每次抓取一行，重新比對只在結果有變時）。
    /// </summary>
    private static void LogResult(NoticeState result, string trigger)
    {
        var inactiveSuffix = result.Inactive.Count == 0
            ? string.Empty
            : $"；另有 {result.Inactive.Count} 個停用中的插件有新版（不計入）——{JoinNames(result.Inactive)}";

        if (result.Pending.Count == 0)
        {
            Svc.Log.Information(
                $"[{ModuleInternalName}] {trigger}：" +
                (result.Inactive.Count == 0 ? "沒有可更新的插件。" : "啟用中的插件沒有可更新的") +
                inactiveSuffix);
            return;
        }

        // 寫 Debug：插件載入狀態一變就重比一次，量大；有可更新時伺服器資訊列與提示本身就看得到。
        Svc.Log.Debug(
            $"[{ModuleInternalName}] {trigger}：可更新 {result.Pending.Count} 個——"
            + JoinNames(result.Pending) + inactiveSuffix);
    }

    private static string JoinNames(List<PendingUpdate> list)
    {
        var names = new List<string>(list.Count);
        foreach (var p in list) names.Add($"{p.InternalName} {p.Installed}→{p.Available}{KindLogTag(p.Kind)}");
        return string.Join("、", names);
    }

    private static string KindLogTag(LoadKind kind) => kind switch
    {
        LoadKind.LoadFailed => "(啟用中但沒載入)",
        LoadKind.NotLoadedUnknown => "(沒載入、讀不到是否停用)",
        _ => string.Empty,
    };

    /// <summary>計入數字但沒載入的插件，在清單上加的註記（tooltip 與設定畫面共用）。</summary>
    private static string KindNote(LoadKind kind) => kind switch
    {
        LoadKind.LoadFailed => "沒有載入（多半是載入失敗），更新可能修好",
        LoadKind.NotLoadedUnknown => "沒有載入（讀不到是否停用）",
        _ => string.Empty,
    };

    /// <summary>停用中那一組的說明（tooltip 與設定畫面共用）。</summary>
    private const string InactiveNote =
        "停用中，安裝器的「全部更新」與自動更新都會跳過；要更新請先啟用它。";

    private void UpdateDtr()
    {
        if (dtrEntry == null) return;

        var current = state;

        // 還沒問過任何一次：沒有話要說。
        if (current == null)
        {
            dtrEntry.Shown = false;
            return;
        }

        // 🔴 一次都沒成功過＝「不知道有沒有更新」，這必須在列上看得見。
        if (!current.EverSucceeded)
        {
            dtrEntry.Shown = true;
            dtrEntry.Text = new SeString(new IconPayload(DtrIcon), new TextPayload("台服插件更新 ?"));
            dtrEntry.Tooltip = BuildTooltip(current);
            return;
        }

        // 已知沒事——可以安心藏起來，不占資訊列版面。
        if (current.Pending.Count == 0)
        {
            dtrEntry.Shown = false;
            return;
        }

        dtrEntry.Shown = true;
        dtrEntry.Text = new SeString(
            new IconPayload(DtrIcon), new TextPayload($"台服插件可更新 {current.Pending.Count}"));
        dtrEntry.Tooltip = BuildTooltip(current);
    }

    private static string BuildTooltip(NoticeState current)
    {
        var sb = new StringBuilder("TC Toolbox — 台服插件可更新提示");

        if (!current.EverSucceeded)
        {
            sb.Append("\n\n還沒成功讀到台服插件庫，目前無法判斷有沒有可更新的插件。");
        }
        else if (current.Pending.Count == 0)
        {
            sb.Append(current.Inactive.Count == 0
                ? "\n\n已安裝的台服插件都是最新版。"
                : "\n\n啟用中的台服插件都是最新版。");
        }
        else
        {
            AppendPreview(sb, current.Pending);
        }

        if (current.Inactive.Count > 0)
        {
            sb.Append($"\n\n停用中、不計入數字的 {current.Inactive.Count} 個：");
            AppendPreview(sb, current.Inactive);
            sb.Append($"\n{InactiveNote}");
        }

        sb.Append($"\n\n上次更新：{FormatLocal(current.LastAttemptUtc)}");
        if (current.LastError != null) sb.Append($"（取得失敗：{current.LastError}）");
        if (current.LastError == TokenHintReason) sb.Append($"\n{TokenHintDetail}");

        sb.Append("\n左鍵：開啟 Dalamud 插件安裝器（可更新的插件）\n右鍵：開啟／關閉 TC Toolbox 設定");
        return sb.ToString();
    }

    private static void AppendPreview(StringBuilder sb, List<PendingUpdate> list)
    {
        var shown = Math.Min(list.Count, TooltipPreviewCount);
        for (var i = 0; i < shown; i++)
        {
            var p = list[i];
            sb.Append($"\n{p.DisplayName}（{p.InternalName}）：{p.Installed} → {p.Available}");

            var note = KindNote(p.Kind);
            if (note.Length > 0) sb.Append($"　⚠ {note}");
        }

        if (list.Count > shown)
            sb.Append($"\n…等共 {list.Count} 個");
    }

    private static string FormatLocal(DateTime utc) => utc == default
        ? "（尚未成功）"
        : utc.ToLocalTime().ToString("MM/dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>簡短的失敗原因（給 tooltip 用，要短到能塞進一行）。</summary>
    private static string Describe(Exception ex) => ex switch
    {
        OperationCanceledException => "逾時",

        // 沒權杖（或權杖沒權限）時 raw 端點回的是 404 而不是 401。
        HttpRequestException
        {
            StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                        or HttpStatusCode.NotFound,
        } => TokenHintReason,

        HttpRequestException { StatusCode: { } status } => $"HTTP {(int)status}",
        HttpRequestException => "連線失敗",
        JsonException => "插件庫格式無法解析",
        FormatException => "插件庫格式無法解析",
        _ => ex.GetType().Name,
    };

    /// <summary>
    /// 🔴 這是本模組唯一會做的「動作」：把 Dalamud 的外掛安裝器開到「可更新」頁。
    /// 不下載、不安裝——要不要更新由使用者在那個畫面自己決定。
    /// </summary>
    private static void OpenInstaller() =>
        Svc.PluginInterface.OpenPluginInstallerTo(PluginInstallerOpenKind.UpdateablePlugins);

    public override void DrawConfig()
    {
        var interval = Config.IntervalMinutes;
        if (ImGui.SliderInt("檢查間隔（分鐘）##pluginUpdateInterval",
                ref interval, MinIntervalMinutes, MaxIntervalMinutes))
        {
            Config.IntervalMinutes = Math.Clamp(interval, MinIntervalMinutes, MaxIntervalMinutes);
            Plugin.Instance.Config.Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("每隔這段時間問一次；登入時若上次的結果已經超過這段時間才會重問，否則沿用上次的結果。\n取得失敗時會自動退避重試，不會一直重打。");

        if (ImGui.Button("立刻檢查一次##pluginUpdateCheckNow"))
        {
            failureStreak = 0;
            nextCheckUtc = DateTime.UtcNow;
        }

        ImGui.SameLine();
        if (ImGui.Button("開啟插件安裝器##pluginUpdateOpenInstaller")) OpenInstaller();

        ImGui.Separator();

        var current = state;
        if (current == null)
        {
            ImGui.TextDisabled("尚未檢查。");
            return;
        }

        ImGui.TextDisabled($"上次更新：{FormatLocal(current.LastAttemptUtc)}"
            + (current.LastError != null ? $"（取得失敗：{current.LastError}）" : string.Empty));

        if (current.LastError == TokenHintReason)
            ImGui.TextWrapped(TokenHintDetail);

        if (!current.EverSucceeded)
        {
            ImGui.TextColored(new System.Numerics.Vector4(0.7f, 0.7f, 0.7f, 1f),
                "還沒成功讀到台服插件庫，目前無法判斷有沒有可更新的插件。");
            return;
        }

        if (current.Pending.Count == 0)
        {
            ImGui.TextUnformatted(current.Inactive.Count == 0
                ? $"已比對插件庫 {current.FeedCount} 筆：已安裝的台服插件都是最新版。"
                : $"已比對插件庫 {current.FeedCount} 筆：啟用中的台服插件都是最新版。");
            DrawInactive(current.Inactive);
            return;
        }

        ImGui.TextUnformatted($"已比對插件庫 {current.FeedCount} 筆：可更新 {current.Pending.Count} 個。");

        if (ImGui.BeginTable("##pluginUpdateList", 3,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.SizingStretchProp))
        {
            DrawPendingRows(current.Pending);
            ImGui.EndTable();
        }

        DrawInactive(current.Inactive);
    }

    /// <summary>停用中的插件：灰字另列，不進上面的表。</summary>
    private static void DrawInactive(List<PendingUpdate> inactive)
    {
        if (inactive.Count == 0) return;

        ImGui.Spacing();
        ImGui.TextDisabled($"另有 {inactive.Count} 個停用中的插件有新版，不計入上面的數字：");
        foreach (var p in inactive)
            ImGui.TextDisabled($"  {p.DisplayName}（{p.InternalName}）：{p.Installed} → {p.Available}");
        ImGui.TextDisabled(InactiveNote);
    }

    private static void DrawPendingRows(List<PendingUpdate> pending)
    {
        ImGui.TableSetupColumn("插件", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("已安裝", ImGuiTableColumnFlags.WidthFixed, 110f);
        ImGui.TableSetupColumn("可更新到", ImGuiTableColumnFlags.WidthFixed, 110f);
        ImGui.TableHeadersRow();

        foreach (var p in pending)
        {
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(p.DisplayName);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(p.InternalName);

            var note = KindNote(p.Kind);
            if (note.Length > 0)
            {
                ImGui.SameLine();
                ImGui.TextColored(new System.Numerics.Vector4(1f, 0.75f, 0.3f, 1f), "（未載入）");
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(note);
            }

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(p.Installed.ToString());

            ImGui.TableNextColumn();
            ImGui.TextColored(new System.Numerics.Vector4(0.6f, 1f, 0.6f, 1f), p.Available.ToString());
        }
    }
}

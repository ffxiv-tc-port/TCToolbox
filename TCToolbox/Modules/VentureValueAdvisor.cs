using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Command;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using TCToolbox.Core;
using TCToolbox.Windows;

namespace TCToolbox.Modules;

/// <summary>
/// 僱員探險收益：每位僱員目前的探險與最賺的指定探險，用家世界行情估每趟／每小時價值與賣得完係數。
/// </summary>
/// <remarks>
/// 本檔假設與契約：
/// 🔴 帶回數量一律問 AutoRetainer（<see cref="AutoRetainerVentureIpc"/>），這裡不重算；只讀，不呼叫 SetVenture 或任何寫入端點，換探險由使用者自己在 AutoRetainer 加進計畫。
/// 🔴 IPC 只在 <c>Framework.Update</c> 打（一個角色一幀）；Draw 只讀算好的欄位、只設待辦旗標。對方回的都是複本，不跨幀持有對方物件。
/// 🔴 查價只在使用者按下時送（<see cref="UniversalisAggregated"/>），沒有背景輪詢；網址只有世界與物品編號。
/// 本檔欄位只在遊戲主執行緒讀寫。
/// </remarks>
public sealed partial class VentureValueAdvisor : TcModule
{
    public const string Command = "/tcventure";

    /// <summary>AutoRetainer 探險瀏覽器「查詢價格」呼叫的端點；只在本模組啟用時註冊（對方用 HasFunction 判斷模組開著沒）。</summary>
    public const string ShowItemEndpoint = "TCToolbox.VentureValue.ShowItem";

    /// <summary>Universalis 自己快取約 5 分鐘：這段時間內重查同一件拿到的是同一份資料，所以不重送。</summary>
    private const long RefetchAfterMs = 5 * 60 * 1000;

    private const long ProbeIntervalMs = 30_000;

    private const string NeedAutoRetainerText = "需要 AutoRetainer（探險數量介面第 1 版）";

    public override string InternalName => "VentureValueAdvisor";

    public override string DisplayName => "僱員探險收益";

    public override string Description =>
        "列出每個角色每位僱員目前的探險、最賺的指定探險，以及每趟／每小時能換到多少 Gil 與「賣得完係數」（家世界每日成交量夠不夠消化一趟的數量）。"
        + "帶回數量由 AutoRetainer 計算（需要它的探險數量介面），行情按「查價」才向 Universalis 查家世界的最近成交均價與最低掛單，取低者扣 5% 稅；只送物品與世界編號。"
        + "只顯示、不替你換探險：要換請自己在 AutoRetainer 加進探險計畫。"
        + $"指令 {Command} 開啟獨立視窗；AutoRetainer 探險瀏覽器的「查詢價格」也會開到這裡。";

    public override ModuleCategory Category => ModuleCategory.Company;

    public override bool HasConfigUI => true;

    /// <summary>開著只會顯示；查價也要按了才送。</summary>
    public override bool IsManualTrigger => true;

    private static VentureValueAdvisorConfig Config => Plugin.Instance.Config.VentureValueAdvisor;

    private enum ArState
    {
        NotChecked = 0,
        NotLoaded,
        NoEndpoint,
        Ready,
    }

    private sealed record YieldRow(uint VentureId, uint ItemId, int Minutes, bool Available, int Amount, int Tier, int Stat, int NextStat, int NextAmount, bool UsesPerception);

    private sealed record RetainerRow(string Name, uint Job, int Level, uint VentureId, bool HasVenture, long VentureEndsAt, int Stat, YieldRow[]? Yields);

    /// <param name="WorldId">0＝世界名在世界表裡對不上唯一一列，這個角色不查價。</param>
    /// <param name="Retainers">null＝AutoRetainer 沒回答這個角色的僱員。</param>
    private sealed record CharacterRow(ulong Cid, string Name, string World, uint WorldId, RetainerRow[]? Retainers);

    /// <param name="Market">null＝沒查過，或 Universalis 不認得這件物品（看 <paramref name="NoData"/>）。</param>
    /// <param name="FetchedAtMs">0＝上次查失敗，下次照樣重查。</param>
    private sealed record PriceEntry(WorldItemMarket? Market, long FetchedAtMs, bool NoData);

    private sealed record VentureEval(YieldRow Yield, string ItemName, VentureValuation Value, WorldItemMarket? Market, bool NoData);

    private sealed class RetainerView
    {
        public required string Key { get; init; }
        public required int Order { get; init; }
        public required CharacterRow Character { get; init; }
        public RetainerRow? Retainer { get; init; }
        public string Note { get; init; } = string.Empty;
        public string CurrentLabel { get; init; } = string.Empty;
        public VentureEval? Current { get; init; }
        public List<VentureEval> Ranked { get; init; } = [];
        public VentureEval? Best => Ranked.Count > 0 ? Ranked[0] : null;
    }

    private VentureValueWindow? window;
    private UniversalisAggregated? fetcher;
    private ICallGateProvider<uint, bool>? showItemProvider;

    private ArState arState;
    private long nextProbeAt;
    private int arVersion;
    private string arDetail = string.Empty;

    private bool pendingLoad;
    private bool loading;
    private readonly Queue<(ulong CID, string Name, string World)> loadQueue = new();
    private List<CharacterRow> building = [];
    private List<CharacterRow> roster = [];
    private string loadSummary = string.Empty;

    private readonly Dictionary<(uint World, uint Item), PriceEntry> prices = [];
    private bool pendingFetchAll;
    private uint pendingFetchItem;
    private bool fetchActive;
    private int fetchOkItems;
    private int fetchNoDataItems;
    private int fetchFailedBatches;
    private string fetchSummary = string.Empty;
    private string lastFetchError = string.Empty;

    private readonly List<RetainerView> views = [];
    private bool viewsDirty;
    private uint focusItemId;

    private Dictionary<string, List<World>>? worldsByName;
    private readonly Dictionary<uint, string> itemNameCache = [];
    private readonly Dictionary<uint, string> jobNameCache = [];

    public override ModuleNotice? RowNotice
    {
        get
        {
            if (!IsEnabled) return null;
            return arState is ArState.NotLoaded or ArState.NoEndpoint
                ? new ModuleNotice(ModuleNoticeLevel.Warning, NeedAutoRetainerText, arDetail)
                : null;
        }
    }

    protected override void OnEnable()
    {
        ResetState();
        fetcher = new UniversalisAggregated();

        window = new VentureValueWindow(this);
        Plugin.Instance.WindowSystem.AddWindow(window);

        Svc.Commands.AddHandler(Command, new CommandInfo((_, _) => ToggleWindow())
        {
            HelpMessage = "開啟「僱員探險收益」視窗",
        });

        showItemProvider = Svc.PluginInterface.GetIpcProvider<uint, bool>(ShowItemEndpoint);
        showItemProvider.RegisterFunc(itemId => IpcFrameworkGate.Get(ShowItemEndpoint, () => ShowItem(itemId), false));

        Svc.Framework.Update += OnUpdate;
    }

    protected override void OnDisable()
    {
        Svc.Framework.Update -= OnUpdate;

        showItemProvider?.UnregisterFunc();
        showItemProvider = null;

        Svc.Commands.RemoveHandler(Command);

        if (window != null)
        {
            // 外掛卸載時 Plugin.Dispose 先 RemoveAllWindows() 才停用模組，對不在清單裡的視窗 RemoveWindow 會擲例外。
            if (Plugin.Instance.WindowSystem.Windows.Contains(window))
                Plugin.Instance.WindowSystem.RemoveWindow(window);

            window.IsOpen = false;
            window = null;
        }

        fetcher?.Dispose();
        fetcher = null;
        ResetState();
    }

    private void ResetState()
    {
        arState = ArState.NotChecked;
        nextProbeAt = 0;
        arVersion = 0;
        arDetail = string.Empty;
        pendingLoad = false;
        loading = false;
        loadQueue.Clear();
        building = [];
        roster = [];
        loadSummary = string.Empty;
        prices.Clear();
        pendingFetchAll = false;
        pendingFetchItem = 0;
        fetchActive = false;
        fetchSummary = string.Empty;
        lastFetchError = string.Empty;
        views.Clear();
        viewsDirty = false;
        focusItemId = 0;
        worldsByName = null;
        itemNameCache.Clear();
        jobNameCache.Clear();
        ResetSort();
    }

    private void ToggleWindow()
    {
        if (window != null) window.IsOpen = !window.IsOpen;
    }

    /// <summary>窗打開時，還沒讀過僱員就讀一次（只讀 AutoRetainer，不查價）。</summary>
    internal void OnWindowOpened()
    {
        if (roster.Count == 0 && !loading) pendingLoad = true;
    }

    /// <summary>IPC 本體（主執行緒）：打開視窗並定位到這件物品。</summary>
    private bool ShowItem(uint itemId)
    {
        if (window == null || itemId == 0) return false;
        focusItemId = itemId;
        window.IsOpen = true;
        window.BringToFront();
        if (roster.Count == 0 && !loading) pendingLoad = true;
        Svc.Log.Information($"[VentureValue] AutoRetainer 要求定位到物品 {itemId}");
        return true;
    }

    private void OnUpdate(IFramework framework)
    {
        // 還沒確認可用時每 30 秒問一次版本：模組列上的「需要 AutoRetainer」不必等打開視窗，AutoRetainer 晚載入也會自己消失。
        if (arState != ArState.Ready && !loading && Environment.TickCount64 >= nextProbeAt)
        {
            nextProbeAt = Environment.TickCount64 + ProbeIntervalMs;
            ProbeAutoRetainer();
        }

        if (pendingLoad && !loading) StartLoad();
        else if (loading) LoadStep();

        if (pendingFetchAll || pendingFetchItem != 0)
        {
            var only = pendingFetchItem;
            pendingFetchAll = false;
            pendingFetchItem = 0;
            StartFetch(only);
        }

        DrainFetch();

        if (viewsDirty)
        {
            viewsDirty = false;
            RebuildViews();
        }
    }

    // ── 讀 AutoRetainer ─────────────────────────────────────────────

    /// <returns>AutoRetainer 在、而且探險數量介面版本夠。</returns>
    private bool ProbeAutoRetainer()
    {
        if (PluginPresence.IsLoaded("AutoRetainer") == false)
        {
            SetArState(ArState.NotLoaded, 0, "沒有偵測到 AutoRetainer（未安裝或未載入）。");
            return false;
        }

        if (!AutoRetainerVentureIpc.TryGetApiVersion(out var version, out var error))
        {
            SetArState(ArState.NoEndpoint, 0, $"AutoRetainer 沒有探險數量介面（{error}）：請更新 AutoRetainer。");
            return false;
        }

        if (version < AutoRetainerVentureIpc.RequiredApiVersion)
        {
            SetArState(ArState.NoEndpoint, version, $"AutoRetainer 的探險數量介面是第 {version} 版，本模組需要第 {AutoRetainerVentureIpc.RequiredApiVersion} 版以上。");
            return false;
        }

        SetArState(ArState.Ready, version, $"AutoRetainer 探險數量介面第 {version} 版。");
        return true;
    }

    private void StartLoad()
    {
        pendingLoad = false;
        if (!ProbeAutoRetainer()) return;

        var characters = AutoRetainerVentureIpc.GetCharacters(out var error);
        if (characters == null)
        {
            loadSummary = "AutoRetainer 這次沒有回答角色清單，請再按一次「重新讀取」。";
            Svc.Log.Information($"[VentureValue] 讀取角色清單失敗：{(error.Length > 0 ? error : "對方回 null（逾時或讀取失敗）")}");
            return;
        }

        loadQueue.Clear();
        foreach (var c in characters) loadQueue.Enqueue(c);
        building = [];
        loading = true;
        loadSummary = "讀取中…";
    }

    private void SetArState(ArState state, int version, string detail)
    {
        if (state != arState || version != arVersion)
            Svc.Log.Information($"[VentureValue] AutoRetainer 狀態：{state}（{detail}）");

        arState = state;
        arVersion = version;
        arDetail = detail;
    }

    /// <summary>一幀讀一個角色：AutoRetainer 判可接與否要走訪採集表，一次全讀會卡一下。</summary>
    private void LoadStep()
    {
        if (loadQueue.Count > 0)
        {
            var (cid, name, world) = loadQueue.Dequeue();
            var worldId = ResolveWorld(world, building.Count + 1);
            var retainers = AutoRetainerVentureIpc.GetRetainers(cid, out var error);
            RetainerRow[]? rows = null;
            if (retainers == null)
            {
                Svc.Log.Information($"[VentureValue] 第 {building.Count + 1} 個角色讀不到僱員：{(error.Length > 0 ? error : "對方回 null")}");
            }
            else
            {
                rows = new RetainerRow[retainers.Length];
                for (var i = 0; i < retainers.Length; i++)
                {
                    var r = retainers[i];
                    var yields = AutoRetainerVentureIpc.GetYields(cid, r.Name, null, out _);
                    var stat = IsGatherer(r.Job) ? r.Perception : r.Ilvl;
                    rows[i] = new RetainerRow(r.Name, r.Job, r.Level, r.VentureID, r.HasVenture, r.VentureEndsAt, stat,
                        yields?.Select(y => new YieldRow(y.VentureID, y.ItemID, y.Minutes, y.Available, y.Amount, y.Tier, y.Stat,
                            y.NextStat, y.NextAmount, y.UsesPerception)).ToArray());
                }
            }

            building.Add(new CharacterRow(cid, name ?? string.Empty, world ?? string.Empty, worldId, rows));
            if (loadQueue.Count > 0) return;
        }

        loading = false;
        roster = building;
        building = [];
        viewsDirty = true;

        var retainerCount = roster.Sum(c => c.Retainers?.Length ?? 0);
        var unknownStat = roster.Sum(c => c.Retainers?.Count(r => r.Stat < 0) ?? 0);
        var noYields = roster.Sum(c => c.Retainers?.Count(r => r.Yields == null) ?? 0);
        var noWorld = roster.Count(c => c.WorldId == 0);
        var noRetainers = roster.Count(c => c.Retainers == null);
        loadSummary = $"{roster.Count} 個角色、{retainerCount} 位僱員";
        Svc.Log.Information(
            $"[VentureValue] 讀到 {roster.Count} 個角色、{retainerCount} 位僱員；屬性不知道 {unknownStat} 位、讀不到探險 {noYields} 位、"
            + $"讀不到僱員 {noRetainers} 個角色、世界對不上 {noWorld} 個角色");
    }

    /// <summary>世界名→世界表列號。同名不只一列時取與目前角色家世界同一資料中心的那列，還是分不出就回 0（不查價）。</summary>
    private uint ResolveWorld(string? name, int ordinal)
    {
        if (string.IsNullOrEmpty(name)) return 0;

        worldsByName ??= BuildWorldIndex();
        if (!worldsByName.TryGetValue(name, out var candidates) || candidates.Count == 0)
        {
            Svc.Log.Information($"[VentureValue] 第 {ordinal} 個角色的世界「{name}」在世界表裡找不到，這個角色不查價");
            return 0;
        }

        if (candidates.Count == 1) return candidates[0].RowId;

        if (Svc.PlayerState.IsLoaded && Svc.PlayerState.HomeWorld.ValueNullable is { } home)
        {
            var sameDc = candidates.Where(w => w.DataCenter.RowId == home.DataCenter.RowId).ToList();
            if (sameDc.Count == 1) return sameDc[0].RowId;
        }

        Svc.Log.Information($"[VentureValue] 第 {ordinal} 個角色的世界「{name}」在世界表裡有 {candidates.Count} 列、分不出是哪一個（登入角色後再讀一次），這個角色不查價");
        return 0;
    }

    private static Dictionary<string, List<World>> BuildWorldIndex()
    {
        var index = new Dictionary<string, List<World>>(StringComparer.Ordinal);
        foreach (var w in Svc.Data.GetExcelSheet<World>())
        {
            var n = w.Name.ExtractText();
            if (string.IsNullOrEmpty(n)) continue;
            if (!index.TryGetValue(n, out var list)) index[n] = list = [];
            list.Add(w);
        }
        return index;
    }

    // ── 查價 ────────────────────────────────────────────────────────

    /// <param name="onlyItem">0＝所有僱員可接的探險與目前的探險；非 0＝只查這一件（定位用）。</param>
    private void StartFetch(uint onlyItem)
    {
        if (fetcher == null || fetcher.IsFetching || loading) return;

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var work = new Dictionary<uint, HashSet<uint>>();
        var fresh = new HashSet<(uint, uint)>();
        var noWorld = 0;
        foreach (var c in roster)
        {
            if (c.Retainers == null) continue;
            if (c.WorldId == 0)
            {
                noWorld++;
                continue;
            }

            foreach (var r in c.Retainers)
            {
                if (r.Yields == null) continue;
                foreach (var y in r.Yields)
                {
                    if (y.ItemId == 0) continue;
                    var wanted = onlyItem != 0 ? y.ItemId == onlyItem : y.Available || (r.HasVenture && y.VentureId == r.VentureId);
                    if (!wanted) continue;
                    if (prices.TryGetValue((c.WorldId, y.ItemId), out var e) && e.FetchedAtMs > 0 && now - e.FetchedAtMs < RefetchAfterMs)
                    {
                        fresh.Add((c.WorldId, y.ItemId));
                        continue;
                    }
                    if (!work.TryGetValue(c.WorldId, out var set)) work[c.WorldId] = set = [];
                    set.Add(y.ItemId);
                }
            }
        }

        var itemCount = work.Values.Sum(s => s.Count);
        if (itemCount == 0)
        {
            fetchSummary = fresh.Count > 0 ? $"都在 5 分鐘內查過（{fresh.Count} 件），沒有重查" : "沒有要查的物品";
            Svc.Log.Information($"[VentureValue] 沒有送出查價：{fetchSummary}；世界對不上而略過 {noWorld} 個角色");
            return;
        }

        var list = work.Select(kv => (kv.Key, kv.Value.OrderBy(i => i).ToArray())).ToList();
        if (!fetcher.TryStart(list)) return;

        fetchActive = true;
        fetchOkItems = 0;
        fetchNoDataItems = 0;
        fetchFailedBatches = 0;
        lastFetchError = string.Empty;
        Svc.Log.Information(
            $"[VentureValue] 查價開始：{list.Count} 個世界、{itemCount} 件物品、{fetcher.Progress.Total} 次請求；"
            + $"5 分鐘內查過而略過 {fresh.Count} 件、世界對不上而略過 {noWorld} 個角色"
            + (onlyItem != 0 ? $"（只查物品 {onlyItem}）" : string.Empty));
    }

    private void DrainFetch()
    {
        if (fetcher == null) return;

        // 先看旗標再取：背景先入佇列才清旗標，所以這裡看到「沒在跑」時結果已經全在佇列裡。
        var stillFetching = fetcher.IsFetching;
        while (fetcher.TryTake(out var batch))
        {
            viewsDirty = true;
            if (batch.Error != null)
            {
                fetchFailedBatches++;
                lastFetchError = batch.Error;
                foreach (var item in batch.Requested)
                {
                    if (!prices.ContainsKey((batch.WorldId, item))) prices[(batch.WorldId, item)] = new PriceEntry(null, 0, false);
                }
                Svc.Log.Information($"[VentureValue] 世界 {batch.WorldId}：{batch.Requested.Length} 件查價失敗（{batch.Error}）");
                continue;
            }

            foreach (var (item, market) in batch.Items)
                prices[(batch.WorldId, item)] = new PriceEntry(market, batch.FetchedAtUnixMs, false);
            foreach (var item in batch.Failed)
                prices[(batch.WorldId, item)] = new PriceEntry(null, batch.FetchedAtUnixMs, true);

            fetchOkItems += batch.Items.Count;
            fetchNoDataItems += batch.Failed.Count;
        }

        if (!fetchActive || stillFetching) return;

        fetchActive = false;
        fetchSummary = $"{DateTime.Now:HH:mm} 查價：{fetchOkItems} 件有結果"
                       + (fetchNoDataItems > 0 ? $"、{fetchNoDataItems} 件 Universalis 查無" : string.Empty)
                       + (fetchFailedBatches > 0 ? $"、{fetchFailedBatches} 次請求失敗" : string.Empty);
        Svc.Log.Information(
            $"[VentureValue] 查價完成：{fetchOkItems} 件有結果、{fetchNoDataItems} 件 Universalis 查無、{fetchFailedBatches} 次請求失敗"
            + (lastFetchError.Length > 0 ? $"（最後一次錯誤：{lastFetchError}）" : string.Empty));
    }

    // ── 計算 ────────────────────────────────────────────────────────

    private void RebuildViews()
    {
        views.Clear();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var staleMs = Math.Max(1, Config.StaleHours) * 3_600_000L;
        var order = 0;

        foreach (var c in roster)
        {
            if (c.Retainers == null)
            {
                views.Add(new RetainerView { Key = $"{c.Cid:X}", Order = order++, Character = c, Note = "AutoRetainer 沒有回答這個角色的僱員" });
                continue;
            }

            foreach (var r in c.Retainers)
            {
                var evals = new List<VentureEval>();
                VentureEval? current = null;
                if (r.Yields != null)
                {
                    foreach (var y in r.Yields)
                    {
                        if (y.ItemId == 0 || y.Amount <= 0) continue;
                        var isCurrent = r.HasVenture && y.VentureId == r.VentureId;
                        if (!y.Available && !isCurrent) continue;
                        var e = Evaluate(c, y, now, staleMs);
                        if (y.Available) evals.Add(e);
                        if (isCurrent) current = e;
                    }
                }

                evals.Sort((a, b) => VentureValueMath.CompareBestFirst(a.Value, b.Value));
                views.Add(new RetainerView
                {
                    Key = $"{c.Cid:X}/{r.Name}",
                    Order = order++,
                    Character = c,
                    Retainer = r,
                    Note = r.Yields == null ? "AutoRetainer 沒有回答這位僱員的探險" : string.Empty,
                    Current = current,
                    CurrentLabel = CurrentVentureLabel(r, current),
                    Ranked = evals,
                });
            }
        }

        ApplySort();
    }

    private VentureEval Evaluate(CharacterRow c, YieldRow y, long now, long staleMs)
    {
        PriceEntry? entry = null;
        if (c.WorldId != 0) prices.TryGetValue((c.WorldId, y.ItemId), out entry);
        var market = entry?.Market;
        var value = VentureValueMath.Evaluate(y.Amount, y.Stat < 0, y.Minutes, market, VentureValueMath.TaxRate, now, staleMs);
        return new VentureEval(y, ItemName(y.ItemId), value, market, entry?.NoData == true);
    }

    private string CurrentVentureLabel(RetainerRow r, VentureEval? current)
    {
        if (!r.HasVenture || r.VentureId == 0) return "沒有派遣";
        if (current != null) return current.ItemName;
        if (Svc.Data.GetExcelSheet<RetainerTask>().GetRowOrDefault(r.VentureId) is not { } task) return $"#{r.VentureId}";
        if (task.IsRandom)
        {
            var name = Svc.Data.GetExcelSheet<RetainerTaskRandom>().GetRowOrDefault(task.Task.RowId)?.Name.ExtractText();
            return string.IsNullOrEmpty(name) ? $"#{r.VentureId}（隨機帶回，估不出）" : $"{name}（隨機帶回，估不出）";
        }

        var itemId = Svc.Data.GetExcelSheet<RetainerTaskNormal>().GetRowOrDefault(task.Task.RowId)?.Item.RowId ?? 0;
        return itemId == 0 ? $"#{r.VentureId}" : $"{ItemName(itemId)}（估不出）";
    }

    /// <summary>採掘師、園藝師、捕魚人（ClassJob 16～18）的探險數量看鑑別力，其餘看平均物品等級；與 AutoRetainer 的 IsDoL 同一組。</summary>
    private static bool IsGatherer(uint job) => job is 16 or 17 or 18;

    private string ItemName(uint itemId)
    {
        if (!itemNameCache.TryGetValue(itemId, out var name))
            itemNameCache[itemId] = name = ItemNames.Get(itemId);
        return name;
    }

    private string JobName(uint job)
    {
        if (!jobNameCache.TryGetValue(job, out var name))
        {
            name = Svc.Data.GetExcelSheet<ClassJob>().GetRowOrDefault(job)?.Abbreviation.ExtractText() ?? string.Empty;
            if (string.IsNullOrEmpty(name)) name = $"#{job}";
            jobNameCache[job] = name;
        }
        return name;
    }
}

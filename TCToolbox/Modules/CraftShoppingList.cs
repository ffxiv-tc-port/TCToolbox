using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using TCToolbox.Core;
using TCToolbox.Windows;

namespace TCToolbox.Modules;

/// <summary>
/// 製作清單缺料：AllaganTools 的製作清單要做什麼 → 缺哪些材料 → 哪裡買 → 一鍵走到商人。
/// </summary>
/// <remarks>
/// <para>
/// 🔴🔴 <b>紅線</b>：①<b>零採購、零掛單、零市場查詢</b>——價格只顯示 Marketbuddy
/// <b>已經看過</b>的快取，我們不送任何市場請求。②會讓角色移動的只有「前往」那一顆，
/// <b>使用者親手按下才動、一次只去一個地方、絕不自己接續下一個目的地</b>。
/// ③本模組<b>預設關閉</b>（TCToolbox 全體政策）。
/// </para>
/// <para>
/// 🔑 <b>為什麼「缺料」要自己算。</b><c>AllaganTools.GetCraftItems</c> 回的是<b>成品</b>
/// （它的實作只收 <c>IsOutputItem</c>），而 Artisan 沒有「清單裡有什麼」的端點
/// ⇒ 材料只能自己從 Lumina <c>Recipe</c> 表展開（<see cref="RecipeMaterials"/>），
/// 持有量再逐一向 <c>AllaganTools.ItemCount</c> 問。
/// </para>
/// <para>
/// 🔴 <b>持有量是「HQ 與 NQ 合計」。</b>對方的 <c>ItemCount</c> 不看品質旗標，
/// 而製作材料本來就不分品質都能用。缺口＝需要 − 合計持有，夾在 0 以上。
/// </para>
/// <para>
/// 🔑 <b>Draw 路徑上一律不做 IPC。</b>持有量查詢對 AllaganTools 而言是對「所有已知道具欄
/// 的每一格」做一次全掃，而一次重新整理要問「材料數 × 擁有者數」次
/// ⇒ 全部切在 <c>Framework.Update</c> 裡分幀做（有進度顯示），Draw 只讀算好的欄位。
/// </para>
/// <para>
/// ⚠️ <b>刻意不接 <see cref="AutomationGate"/>。</b>那道閘門是給「自己會醒過來的迴圈」用的；
/// 這個模組的每一個動作都是使用者當下按的，擋下來只會變成「按了沒反應」。
/// 會移動的按鈕另外用 <see cref="ExternalNav.TryGetActiveMover"/> 擋住「別人正在帶著角色走」。
/// </para>
/// </remarks>
public sealed class CraftShoppingList : TcModule
{
    public const string Command = "/tcshop";

    /// <summary>一次重新整理最多列出幾種材料。</summary>
    /// <remarks>
    /// 🔴 <b>這不是效能上限，是「不要把畫面變成沒有意義的長條」的上限。</b>
    /// 開了遞迴展開之後，一張大清單很容易展出上千種材料，而那份清單沒有人看得完。
    /// 撞到上限時會在畫面上說出來（<see cref="rowLimitHit"/>），<b>不是靜默截斷</b>。
    /// </remarks>
    private const int MaxRows = 400;

    /// <summary>每一幀最多送幾次持有量查詢。</summary>
    /// <remarks>
    /// 📌 每一次都是對方的一輪 LINQ 全掃。32 次在一般容量下是幾毫秒，
    /// 而 300 種材料 × 12 個擁有者＝3600 次會分成大約 110 幀（不到兩秒）跑完。
    /// </remarks>
    private const int CountCallsPerFrame = 32;

    /// <summary>每一幀最多查幾列的「哪裡買」。</summary>
    private const int SourceRowsPerFrame = 8;

    /// <summary>顯示用狀態的輪詢間隔（毫秒）。</summary>
    private const long PollIntervalMs = 250;

    /// <summary>面板多久沒被畫出來就停止輪詢（毫秒）。</summary>
    private const long IdleStopMs = 2_000;

    /// <summary>二次確認的有效時間（毫秒）。過了自動解除，避免誤觸留在武裝狀態。</summary>
    private const long ConfirmWindowMs = 5_000;

    /// <summary>水晶／碎晶／晶簇的 <c>ItemUICategory</c> 列號。</summary>
    /// <remarks>
    /// ✅ 2026-09-12 以台服 EXD dump 實證：<c>ItemUICategory</c> #59 的名稱逐字是「水晶」，
    /// 而 <c>Item</c> #2 火之碎晶／#8 火之水晶／#14 火之晶簇的 <c>ItemUICategory</c> 都是 59。
    /// ⚠️ 寫死的列號在台服一律先驗過再用——這一個驗了，不要照抄到別的類別去。
    /// </remarks>
    private const uint CrystalItemUiCategory = 59;

    /// <summary>展開／統計／查來源的進度階段。</summary>
    private enum Stage
    {
        /// <summary>沒有在跑。</summary>
        Idle = 0,

        /// <summary>正在逐一問持有量。</summary>
        Counting = 1,

        /// <summary>正在逐一查「哪裡買」與價格。</summary>
        Sourcing = 2,

        /// <summary>跑完了，畫面上的數字是完整的。</summary>
        Done = 3,
    }

    /// <summary>表上的一列。</summary>
    private sealed class MaterialRow
    {
        public uint ItemId;

        public string Name = string.Empty;

        public bool IsCrystal;

        /// <summary>總共需要幾個。</summary>
        public long Required;

        /// <summary>目前持有幾個（<see cref="OwnedKnown"/> 為 false 時這個值沒有意義）。</summary>
        public long Owned;

        /// <summary>持有量問得出來嗎。</summary>
        /// <remarks>
        /// 🔴 <b>初值是 false，也就是「不知道」。</b>畫面上要畫灰色的「?」而不是 0——
        /// 0 的意思是「你一個都沒有」，那是一句完全不同的話，而且會害使用者去買他已經有的東西。
        /// </remarks>
        public bool OwnedKnown;

        /// <summary>哪些成品要用到它，各要幾個（tooltip 用）。</summary>
        public readonly Dictionary<string, long> NeededBy = [];

        /// <summary>這一筆是在展開的第幾層算出來的（0＝成品的直接材料）。</summary>
        public int MaxDepth;

        /// <summary>這一列查過「哪裡買」了嗎（不缺的列刻意不查）。</summary>
        public bool Sourced;

        public VendorLookupStatus VendorStatus;

        public string VendorReason = string.Empty;

        public List<VendorLocationEntry> Vendors = [];

        /// <summary>挑出來當「前往」目標的那一個商人（<c>null</c>＝沒有可去的地方）。</summary>
        public VendorLocationEntry? BestVendor;

        /// <summary>
        /// 來源欄上那一行字，<b>在查詢當下就算好</b>。
        /// </summary>
        /// <remarks>
        /// 📌 為什麼要快取：算它要查 <c>TerritoryType</c> → <c>PlaceName</c> 兩張表，
        /// 而 Draw 是<b>每幀</b>對<b>每一列</b>都跑一次（ImGui 的表格沒有裁剪器，
        /// 捲到看不見的列也照樣走一輪）。查表本身很便宜，但沒有理由每秒做兩萬四千次。
        /// </remarks>
        public string SourceLabel = string.Empty;

        public MarketSnapshotEntry? Market;

        /// <summary>還缺幾個（<see cref="OwnedKnown"/> 為 false 時回 -1＝不知道）。</summary>
        public long Gap => OwnedKnown ? Math.Max(0, Required - Owned) : -1;
    }

    public override string InternalName => "CraftShoppingList";

    public override string DisplayName => "製作清單缺料與採買";

    public override string Description =>
        "挑一張 AllaganTools 的製作清單，一張表看完「每種材料要幾個、身上（含僱員）有幾個、還缺幾個、" +
        "哪個 NPC 商人賣、市場上看過的最低價」，並可以按一下讓 Lifestream 帶你走到那個商人面前。" +
        "材料是從遊戲配方表自己展開的（預設只展開一層），價格只顯示 Marketbuddy 已經看過的資料——" +
        "不會去查市場、不會買任何東西。移動一律要你親手按下，一次只去一個地方。" +
        $"指令 {Command} 開啟獨立視窗。";

    public override ModuleCategory Category => ModuleCategory.Inventory;

    public override bool HasConfigUI => true;

    /// <summary>開著也不會自己動——它只在你按下去的那一刻做一次事。</summary>
    public override bool IsManualTrigger => true;

    private static CraftShoppingListConfig Config => Plugin.Instance.Config.CraftShoppingList;

    private CraftShoppingListWindow? window;

    // ── 以下欄位只在遊戲主執行緒讀寫（Framework.Update 與 ImGui 的 Draw 都是主執行緒）──

    private readonly List<MaterialRow> rows = [];

    private readonly Dictionary<uint, int> rowIndex = [];

    /// <summary>AllaganTools 回報的製作清單（key → 顯示名）。</summary>
    private readonly Dictionary<string, string> craftLists = [];

    /// <summary>下拉選單用的 key 順序（<see cref="craftLists"/> 的鍵，固定順序）。</summary>
    private readonly List<string> craftListKeys = [];

    /// <summary>有道具欄的擁有者（角色本人＋僱員＋部隊／宅邸）。</summary>
    private readonly List<ulong> owners = [];

    private bool ownersKnown;

    private Stage stage;

    private int countCursor;

    private int ownerCursor;

    private int sourceCursor;

    /// <summary>這一輪展開了幾個成品。</summary>
    private int outputCount;

    /// <summary>這一輪有幾個成品查不到配方（它們自己被當成要買的東西列出來）。</summary>
    private int noRecipeCount;

    /// <summary>展開到了深度上限，底下還有沒展開的中間材料。</summary>
    private bool expandTruncated;

    /// <summary>材料種類撞到 <see cref="MaxRows"/> 上限。</summary>
    private bool rowLimitHit;

    private bool marketAvailable;

    private string marketReason = string.Empty;

    /// <summary>最近一次失敗的原因（空字串＝沒有問題）。</summary>
    private string lastError = string.Empty;

    /// <summary>最近一次算完的時間（<c>MinValue</c>＝還沒算過）。</summary>
    private DateTime lastRefreshAt = DateTime.MinValue;

    private bool lifestreamAvailable;

    private bool someoneIsMoving;

    private string mover = string.Empty;

    private long lastUiTick;

    private long lastPollTick;

    /// <summary>下一幀要開始重新整理。</summary>
    private bool pendingRefresh;

    /// <summary>下一幀要送出「前往」的那一列的道具編號（0＝沒有）。</summary>
    private uint pendingNavItemId;

    /// <summary>下一幀要送出中止。</summary>
    private bool pendingAbort;

    /// <summary>目前被「按第一次」武裝起來的按鈕；空字串＝沒有。</summary>
    private string confirmKey = string.Empty;

    private long confirmUntil;

    private string lastActionText = string.Empty;

    private bool lastActionOk;

    /// <summary>已經輪詢過至少一次（<see cref="lifestreamAvailable"/> 之類的欄位才有意義）。</summary>
    /// <remarks>
    /// 🔴 沒有這個旗標的話，面板打開的第一幀會誠懇地說謊：
    /// <see cref="lifestreamAvailable"/> 的初值是 false，於是每次開面板都會先閃一下
    /// 「未偵測到 Lifestream」，而那只是「還沒查」。
    /// </remarks>
    private bool polledOnce;

    /// <summary>
    /// 模組列上直接顯示：這張清單還缺幾種材料。
    /// </summary>
    /// <remarks>
    /// 🔑 <b>「不知道」要在列上看得見。</b>還沒算過時寫「尚未統計」，
    /// AllaganTools 不在時寫「未偵測到 AllaganTools」——都不是 0。
    /// </remarks>
    public override ModuleNotice? RowNotice
    {
        get
        {
            if (!IsEnabled) return null;

            lastUiTick = Environment.TickCount64;

            if (stage == Stage.Counting)
            {
                return new ModuleNotice(
                    ModuleNoticeLevel.Unknown,
                    $"統計持有量 {countCursor}/{rows.Count}",
                    "正在逐一向 AllaganTools 問每一種材料在每一個道具欄裡有幾個。\n"
                    + "這件事被切成好幾幀做，所以遊戲不會卡住。");
            }

            if (stage == Stage.Sourcing)
            {
                return new ModuleNotice(
                    ModuleNoticeLevel.Unknown,
                    $"查詢來源 {sourceCursor}/{rows.Count}",
                    "正在逐一向 Item Vendor Location 問「哪個 NPC 賣這件東西」，\n"
                    + "並向 Marketbuddy 問它看過的最低價（不會去查市場）。");
            }

            // 🔴 <b>只有「什麼都沒算出來」才算無法統計。</b>「擁有者清單問不到」是非致命的
            //    ——需求量照樣算得出來，只有持有量變成「不知道」。把那種情況寫成「無法統計」
            //    會讓使用者以為整張表都是壞的，而表裡其實有完整的需求數字。
            if (!string.IsNullOrEmpty(lastError) && lastRefreshAt == DateTime.MinValue)
                return new ModuleNotice(ModuleNoticeLevel.Warning, "無法統計", lastError);

            if (lastRefreshAt == DateTime.MinValue)
            {
                return new ModuleNotice(
                    ModuleNoticeLevel.Unknown,
                    "尚未統計",
                    $"開啟面板（{Command}）或在設定裡按「重新統計」之後才會有數字。\n"
                    + "這個模組不會自己去算——它只在你按下去的那一刻做一次事。");
            }

            var missing = 0;
            var unknown = 0;
            foreach (var row in rows)
            {
                if (!row.OwnedKnown) unknown++;
                else if (row.Gap > 0) missing++;
            }

            var text = unknown > 0
                ? $"缺 {missing} 種，{unknown} 種不確定"
                : $"缺 {missing} 種";

            return new ModuleNotice(ModuleNoticeLevel.Unknown, text, BuildSummaryTooltip());
        }
    }

    protected override void OnEnable()
    {
        Svc.Commands.AddHandler(Command, new CommandInfo(OnCommand)
        {
            HelpMessage = "開啟「製作清單缺料與採買」面板",
        });

        window = new CraftShoppingListWindow(this);
        Plugin.Instance.WindowSystem.AddWindow(window);

        // 🔴 登記共用的「停下本外掛發起的移動」設施：它註冊 /tcstop 並掛上補送看門狗。
        //    沒有登記就直接呼叫 NavStop.RequestStop 的話，補送窗口會被打開<b>而沒有人去關它</b>
        //    （看門狗根本沒訂閱）⇒ NavStop.IsEnforcing 永遠是 true ⇒ AutomationGate 把
        //    所有無人值守迴圈永久擋住，而且完全沒有徵兆。
        NavStop.Acquire();

        ResetState();

        Svc.Framework.Update += OnUpdate;
    }

    protected override void OnDisable()
    {
        Svc.Framework.Update -= OnUpdate;

        // 📌 這個順序（先停再放）是 NavStop 的標準用法：Release 會讓補送窗口活過最後一次
        //    登出，所以「關掉模組」不會留下一個幾秒後自己走起來的角色。
        if (someoneIsMoving)
            NavStop.RequestStop();

        NavStop.Release();

        if (window != null)
        {
            // 🔴 WindowSystem.RemoveWindow 對「不在清單裡」的視窗會擲 ArgumentException，
            //    而外掛整個卸載時 Plugin.Dispose 是先 RemoveAllWindows() 再 module.Disable()。
            if (Plugin.Instance.WindowSystem.Windows.Contains(window))
                Plugin.Instance.WindowSystem.RemoveWindow(window);

            window.IsOpen = false;
            window = null;
        }

        Svc.Commands.RemoveHandler(Command);

        ResetState();
    }

    private void OnCommand(string command, string arguments)
    {
        if (window == null) return;

        // 指令打開面板的那一刻先讓輪詢醒過來，免得視窗開起來是一片「還不知道」。
        lastUiTick = Environment.TickCount64;
        window.IsOpen = true;
    }

    private void ResetState()
    {
        rows.Clear();
        rowIndex.Clear();
        craftLists.Clear();
        craftListKeys.Clear();
        owners.Clear();
        ownersKnown = false;
        stage = Stage.Idle;
        countCursor = 0;
        ownerCursor = 0;
        sourceCursor = 0;
        outputCount = 0;
        noRecipeCount = 0;
        expandTruncated = false;
        rowLimitHit = false;
        marketAvailable = false;
        marketReason = string.Empty;
        lastError = string.Empty;
        lastRefreshAt = DateTime.MinValue;
        lifestreamAvailable = false;
        someoneIsMoving = false;
        mover = string.Empty;
        lastPollTick = 0;
        pendingRefresh = false;
        pendingNavItemId = 0;
        pendingAbort = false;
        confirmKey = string.Empty;
        confirmUntil = 0;
        lastActionText = string.Empty;
        lastActionOk = false;
        polledOnce = false;
    }

    private void OnUpdate(IFramework framework)
    {
        // 🔴 使用者按下的動作永遠優先，而且不受「有沒有人在看」影響：他可能按完就把視窗關掉了。
        FlushPending();

        // 已經開始的統計要跑完，同理不受「有沒有人在看」影響——半套的數字比沒有數字更糟。
        AdvanceStage();

        var now = Environment.TickCount64;
        if (now - lastUiTick > IdleStopMs) return;
        if (now - lastPollTick < PollIntervalMs) return;
        lastPollTick = now;

        Poll();
    }

    private void Poll()
    {
        lifestreamAvailable = ExternalNav.IsLifestreamAvailable();
        someoneIsMoving = ExternalNav.TryGetActiveMover(out mover);

        // 製作清單的下拉選單：使用者可能在遊戲裡剛建了一張，所以跟著輪詢刷新。
        // ⚠️ GetCraftLists 只是走訪對方記憶體裡的清單集合，與 GetFilterItems 那種「整份重算」
        //    完全不同等級的成本，所以放在這裡是安全的。
        if (AllaganToolsIpc.TryGetCraftLists(out var lists, out _))
        {
            if (!SameKeys(lists))
            {
                craftLists.Clear();
                craftListKeys.Clear();
                foreach (var pair in lists)
                {
                    craftLists[pair.Key] = pair.Value;
                    craftListKeys.Add(pair.Key);
                }

                craftListKeys.Sort(StringComparer.Ordinal);
            }
        }
        else
        {
            craftLists.Clear();
            craftListKeys.Clear();
        }

        polledOnce = true;
    }

    private bool SameKeys(Dictionary<string, string> lists)
    {
        if (lists.Count != craftLists.Count) return false;

        foreach (var pair in lists)
        {
            if (!craftLists.TryGetValue(pair.Key, out var name)) return false;
            if (!string.Equals(name, pair.Value, StringComparison.Ordinal)) return false;
        }

        return true;
    }

    /// <summary>把使用者按下的那一個動作真的送出去。</summary>
    private void FlushPending()
    {
        if (pendingAbort)
        {
            pendingAbort = false;

            // 兩件事都做：Lifestream 的行程要中止，而它交給 vnavmesh 的最後一段要靠
            // NavStop 的補送窗口蓋住「路徑還在背景計算」那幾秒。
            var sent = LifestreamTravel.TryAbort();
            NavStop.RequestStop();

            Report(sent,
                sent
                    ? "已要求 Lifestream 中止行程，並持續補送停止移動。"
                    : "Lifestream 未安裝／未載入；已補送停止移動。");
        }

        if (pendingRefresh)
        {
            pendingRefresh = false;
            BeginRefresh();
        }

        if (pendingNavItemId != 0)
        {
            var itemId = pendingNavItemId;
            pendingNavItemId = 0;
            SendNavigation(itemId);
        }
    }

    /// <summary>
    /// 真的把「帶我去這個商人」送出去。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>一次只送一個目的地，而且不追蹤、不重試、不接續下一個。</b>
    /// 「自動跑完整份採買清單」會讓角色在使用者沒看著的時候連續移動好幾分鐘，
    /// 那是無人值守的自動化 —— 不做。
    /// </remarks>
    private void SendNavigation(uint itemId)
    {
        if (!rowIndex.TryGetValue(itemId, out var idx) || idx >= rows.Count)
        {
            Report(false, "那一列已經不在清單上了（清單在你按下去之後重新算過），沒有前往。");
            return;
        }

        var row = rows[idx];
        var vendor = row.BestVendor;

        if (vendor == null || !vendor.HasLocation || vendor.TerritoryTypeId == 0)
        {
            Report(false, $"「{row.Name}」沒有已知位置的商人，沒有前往。");
            return;
        }

        // 🔴 再確認一次「別人正在移動」：按下去與真的送出之間隔了一幀，
        //    而使用者按的時候別的外掛可能剛好開始帶著角色走。
        if (ExternalNav.TryGetActiveMover(out var who))
        {
            Report(false, $"{who} 正在帶著角色移動，沒有前往（先讓它停下來，或用 /tcstopall）。");
            return;
        }

        // 🔴 座標是<b>世界座標</b>（提供端的 WorldX／WorldZ），不是地圖座標。
        //    兩者差一個縮放與位移，傳錯不會有錯誤訊息 —— 角色只是被帶到那張圖上不相干的地方。
        if (!ExternalNav.TryGoToMapPoint(
                vendor.TerritoryTypeId, vendor.WorldX, vendor.WorldZ, Config.AllowFlying, out var accepted))
        {
            Report(false, $"呼叫失敗：Lifestream 未安裝或未載入，沒有前往「{DescribeVendorShort(vendor)}」。");
            return;
        }

        if (accepted)
        {
            Report(true, $"已請 Lifestream 帶你去「{DescribeVendorShort(vendor)}」買「{row.Name}」。");
            return;
        }

        // ⚠️ accepted == false 是正常結果不是錯誤（Lifestream 正在忙、角色不可互動、
        //    vnavmesh 沒載入、該區沒有已解鎖的乙太之光……）。該做的是說一聲，不是重試。
        Report(false,
            $"Lifestream 沒有接下這次請求（「{DescribeVendorShort(vendor)}」）。"
            + "常見原因：它正在忙、角色現在不可互動、沒裝 vnavmesh，或那一區你還沒解鎖乙太之光。");
    }

    private void Report(bool ok, string text)
    {
        lastActionOk = ok;
        lastActionText = text;

        Svc.Chat.Print($"[TC Toolbox] {text}");

        // 使用者回報用（LogLevel 1 收得到）。
        Svc.Log.Information($"[{InternalName}] {text}");
    }

    // ── 統計 ─────────────────────────────────────────────────────────────────

    /// <summary>使用者按下「重新統計」。</summary>
    /// <remarks>📌 只記旗標，真正的工作在下一次 <c>Framework.Update</c> 開始（Draw 路徑上不做 IPC）。</remarks>
    public void RequestRefresh() => pendingRefresh = true;

    /// <summary>目前設定要用哪一張製作清單（key 或名稱）。</summary>
    public string SelectedList =>
        Config.UseCustomListName ? Config.CustomListName.Trim() : Config.CraftListKey;

    private void BeginRefresh()
    {
        rows.Clear();
        rowIndex.Clear();
        owners.Clear();
        ownersKnown = false;
        stage = Stage.Idle;
        countCursor = 0;
        ownerCursor = 0;
        sourceCursor = 0;
        outputCount = 0;
        noRecipeCount = 0;
        expandTruncated = false;
        rowLimitHit = false;
        lastError = string.Empty;
        lastRefreshAt = DateTime.MinValue;

        var key = SelectedList;
        if (string.IsNullOrEmpty(key))
        {
            lastError = "還沒選製作清單。請在上面挑一張，或勾「直接輸入清單名稱」自己打。";
            return;
        }

        if (!AllaganToolsIpc.TryGetCraftOutputs(key, out var outputs, out var reason))
        {
            lastError = reason;
            return;
        }

        outputCount = outputs.Count;

        foreach (var pair in outputs)
        {
            if (pair.Value == 0) continue;

            var outputName = ItemNames.Get(pair.Key);
            var result = RecipeMaterials.Expand(
                pair.Key, pair.Value, Config.ExpandIntermediates,
                (materialId, need, depth) => AddNeed(materialId, need, depth, outputName));

            switch (result)
            {
                case RecipeExpandResult.NoRecipe:
                    // 這個「成品」其實沒有配方 —— 它自己就是要去買／去採的東西。
                    noRecipeCount++;
                    AddNeed(pair.Key, pair.Value, 0, outputName);
                    break;

                case RecipeExpandResult.Truncated:
                    expandTruncated = true;
                    break;
            }
        }

        // 持有量的擁有者清單：問一次就好，接下來每一種材料都對這份清單走一輪。
        ownersKnown = AllaganToolsIpc.TryGetOwnerIds(out var ownerIds, out var ownerReason);
        if (ownersKnown)
        {
            owners.AddRange(ownerIds);

            // ⚠️ 查得出來但一個都沒有＝AllaganTools 還沒掃到任何道具欄。
            //    這種情況下每一列的持有量都會是 0，而那個 0 是假的 —— 要當成「不知道」。
            if (owners.Count == 0)
            {
                ownersKnown = false;
                lastError = "AllaganTools 還沒掃到任何道具欄（開一次背包與僱員道具欄讓它建立記錄），"
                            + "所以這次的持有量一律顯示「不知道」。";
            }
        }
        else
        {
            lastError = ownerReason;
        }

        marketAvailable = MarketPriceIpc.IsAvailable(out marketReason);

        if (rows.Count == 0)
        {
            stage = Stage.Done;
            lastRefreshAt = DateTime.Now;
            Svc.Log.Information(
                $"[{InternalName}] 清單「{key}」展開後沒有任何材料（成品 {outputCount} 種）。");
            return;
        }

        stage = Stage.Counting;

        Svc.Log.Information(
            $"[{InternalName}] 開始統計清單「{key}」：成品 {outputCount} 種、材料 {rows.Count} 種、"
            + $"擁有者 {owners.Count} 個（遞迴展開={Config.ExpandIntermediates}）。");
    }

    private void AddNeed(uint materialId, long need, int depth, string outputName)
    {
        if (materialId == 0 || need <= 0) return;

        if (!rowIndex.TryGetValue(materialId, out var idx))
        {
            if (rows.Count >= MaxRows)
            {
                rowLimitHit = true;
                return;
            }

            var row = new MaterialRow
            {
                ItemId = materialId,
                Name = ItemNames.Get(materialId),
                IsCrystal = IsCrystal(materialId),
            };

            idx = rows.Count;
            rows.Add(row);
            rowIndex[materialId] = idx;
        }

        var target = rows[idx];
        target.Required += need;
        target.MaxDepth = Math.Max(target.MaxDepth, depth);
        target.NeededBy[outputName] = target.NeededBy.TryGetValue(outputName, out var had) ? had + need : need;
    }

    private void AdvanceStage()
    {
        switch (stage)
        {
            case Stage.Counting:
                AdvanceCounting();
                break;

            case Stage.Sourcing:
                AdvanceSourcing();
                break;
        }
    }

    private void AdvanceCounting()
    {
        var budget = CountCallsPerFrame;

        while (countCursor < rows.Count)
        {
            var row = rows[countCursor];

            if (!ownersKnown)
            {
                // 擁有者清單問不到 ⇒ 每一列都是「不知道」，不做任何查詢。
                row.OwnedKnown = false;
                countCursor++;
                continue;
            }

            var failed = false;

            while (ownerCursor < owners.Count)
            {
                if (budget <= 0) return;

                if (!AllaganToolsIpc.TryGetItemCount(row.ItemId, owners[ownerCursor], out var count))
                {
                    // 🔴 中途失敗就整列作廢：只加總一半的持有量會產生一個「看起來很合理」
                    //    的錯誤數字，而那會讓使用者買錯數量。寧可顯示「不知道」。
                    failed = true;
                    break;
                }

                row.Owned += count;
                ownerCursor++;
                budget--;
            }

            row.OwnedKnown = !failed;
            if (failed) row.Owned = 0;

            countCursor++;
            ownerCursor = 0;
        }

        SortRows();
        stage = Stage.Sourcing;
        sourceCursor = 0;
    }

    private void AdvanceSourcing()
    {
        var done = 0;

        while (sourceCursor < rows.Count)
        {
            if (done >= SourceRowsPerFrame) return;

            var row = rows[sourceCursor];
            sourceCursor++;

            // 📌 不缺的列刻意不查來源：那是純粹省下來的 IPC，而畫面上那一格會寫「不缺」
            //    而不是假裝查過了。
            if (row.OwnedKnown && row.Gap <= 0) continue;

            row.VendorStatus = VendorLookupIpc.TryGetVendors(row.ItemId, out var vendors, out var reason);
            row.Vendors = vendors;
            row.VendorReason = reason;
            row.BestVendor = PickBestVendor(vendors);
            row.SourceLabel = BuildSourceLabel(row);

            if (marketAvailable)
                row.Market = MarketPriceIpc.TryGet(row.ItemId);

            row.Sourced = true;
            done++;
        }

        stage = Stage.Done;
        lastRefreshAt = DateTime.Now;

        var missing = 0;
        foreach (var row in rows)
        {
            if (row.OwnedKnown && row.Gap > 0) missing++;
        }

        Svc.Log.Information(
            $"[{InternalName}] 統計完成：材料 {rows.Count} 種、缺 {missing} 種"
            + (rowLimitHit ? $"（已達 {MaxRows} 種上限，清單不完整）" : string.Empty)
            + (expandTruncated ? "（展開到深度上限，底下還有中間材料沒展開）" : string.Empty));
    }

    /// <summary>
    /// 排序：缺的排前面，同樣缺的照缺口多寡，再照名稱。
    /// </summary>
    /// <remarks>
    /// 🔑 <b>「不知道」排在最前面。</b>它是最需要使用者注意的一類（數字不可信），
    /// 藏在清單尾端等於沒說。
    /// </remarks>
    private void SortRows()
    {
        rows.Sort((a, b) =>
        {
            var ra = RankOf(a);
            var rb = RankOf(b);
            if (ra != rb) return ra - rb;

            if (a.Gap != b.Gap) return b.Gap.CompareTo(a.Gap);
            if (a.Required != b.Required) return b.Required.CompareTo(a.Required);

            return string.CompareOrdinal(a.Name, b.Name);
        });

        // 🔴 排完一定要重建索引：「前往」是靠道具編號找回那一列的。
        rowIndex.Clear();
        for (var i = 0; i < rows.Count; i++)
            rowIndex[rows[i].ItemId] = i;
    }

    private static int RankOf(MaterialRow row)
    {
        if (!row.OwnedKnown) return 0;
        return row.Gap > 0 ? 1 : 2;
    }

    /// <summary>
    /// 從一堆商人裡挑一個當「前往」的目標。
    /// </summary>
    /// <remarks>
    /// 📌 判準依序是：①有位置 ②用 Gil 買得到（<c>SourceType == "GilShop"</c>）
    /// ③清單裡的第一個。
    /// <para>
    /// 🔑 <b>為什麼優先 GilShop</b>：其他管道（<c>SpecialShop</c>／<c>Achievement</c>／
    /// <c>QuestReward</c>）多半不是「走過去就買得到」——把角色帶到一個他其實買不了的地方
    /// 是最沒有價值的一趟。完整清單在 tooltip 與 IVL 自己的視窗裡，沒有資訊被藏起來。
    /// </para>
    /// <para>
    /// ⚠️ 這是<b>顯示與捷徑</b>用的選擇，不是「最便宜」或「最近」。要精挑請按「商人清單」
    /// 開 IVL 自己的視窗（它有篩選）。
    /// </para>
    /// </remarks>
    private static VendorLocationEntry? PickBestVendor(List<VendorLocationEntry> vendors)
    {
        VendorLocationEntry? fallback = null;

        foreach (var vendor in vendors)
        {
            if (!vendor.HasLocation || vendor.TerritoryTypeId == 0) continue;

            if (string.Equals(vendor.SourceType, "GilShop", StringComparison.Ordinal))
                return vendor;

            fallback ??= vendor;
        }

        return fallback;
    }

    private static bool IsCrystal(uint itemId)
    {
        var item = Svc.Data.GetExcelSheet<Item>().GetRowOrDefault(itemId);
        return item != null && item.Value.ItemUICategory.RowId == CrystalItemUiCategory;
    }

    private static string ZoneName(uint territoryTypeId)
    {
        if (territoryTypeId == 0) return string.Empty;

        var name = Svc.Data.GetExcelSheet<TerritoryType>()
                      .GetRowOrDefault(territoryTypeId)?.PlaceName.ValueNullable?.Name.ExtractText()
                   ?? string.Empty;

        // 🔑 台服對未開放內容會保留列但名稱是空字串 —— 回區域編號而不是空白，
        //    使用者至少看得出「有這個區域但我查不到它的名字」。
        return string.IsNullOrEmpty(name) ? $"區域 #{territoryTypeId}" : name;
    }

    private static string DescribeVendorShort(VendorLocationEntry vendor)
    {
        var npc = string.IsNullOrEmpty(vendor.NpcName) ? $"NPC #{vendor.NpcId}" : vendor.NpcName;
        var zone = ZoneName(vendor.TerritoryTypeId);

        return string.IsNullOrEmpty(zone) ? npc : $"{npc}／{zone}";
    }

    // ── UI ───────────────────────────────────────────────────────────────────

    private static readonly Vector4 UnknownColor = new(0.68f, 0.68f, 0.68f, 1f);
    private static readonly Vector4 MissingColor = new(1f, 0.65f, 0.25f, 1f);
    private static readonly Vector4 OkColor = new(0.45f, 0.85f, 0.5f, 1f);
    private static readonly Vector4 FailColor = new(1f, 0.45f, 0.45f, 1f);

    public override void DrawConfig()
    {
        lastUiTick = Environment.TickCount64;

        if (ImGui.Button($"開啟獨立視窗##{InternalName}-Open") && window != null)
            window.IsOpen = true;

        ImGui.SameLine();
        ImGui.TextDisabled($"或用指令 {Command}");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawOptions();
    }

    private void DrawOptions()
    {
        var expand = Config.ExpandIntermediates;
        if (ImGui.Checkbox($"連中間材料一起往下展開##{InternalName}-Expand", ref expand))
        {
            Config.ExpandIntermediates = expand;
            Plugin.Instance.Config.Save();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "關（預設）＝只展開一層：中間材料（例如「青銅錠」）原樣列出來，\n"
                + "  因為多數人本來就打算自己做它，清單也短得看得完。\n"
                + "開＝往下展開到最底層的原料（礦石、碎晶……），最多 "
                + $"{RecipeMaterials.MaxDepth} 層。\n"
                + "⚠️ 開了之後材料種類會暴增，而且它假設「所有中間材料都自己做」——\n"
                + "  你打算買現成中間材料的話，這個數字會高估。");
        }

        var onlyMissing = Config.OnlyShowMissing;
        if (ImGui.Checkbox($"只顯示缺料##{InternalName}-OnlyMissing", ref onlyMissing))
        {
            Config.OnlyShowMissing = onlyMissing;
            Plugin.Instance.Config.Save();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "只留下「還缺」與「不知道有幾個」的材料。\n"
                + "📌 「不知道」永遠留著 —— 那正是最需要你看一眼的一類。");
        }

        ImGui.SameLine();

        var hideCrystals = Config.HideCrystals;
        if (ImGui.Checkbox($"隱藏水晶類##{InternalName}-HideCrystals", ref hideCrystals))
        {
            Config.HideCrystals = hideCrystals;
            Plugin.Instance.Config.Save();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "把碎晶／水晶／晶簇從表上藏起來（它們在配方表裡與其他材料同格，一定會被展開出來）。\n"
                + "⚠️ 藏起來不代表不缺 —— 它們只是很少需要特地去買。");
        }

        var fly = Config.AllowFlying;
        if (ImGui.Checkbox($"允許用飛行坐騎跑最後一段##{InternalName}-Fly", ref fly))
        {
            Config.AllowFlying = fly;
            Plugin.Instance.Config.Save();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "交給 Lifestream 判斷：不能飛或起飛失敗時它自己退回地面路線。\n"
                + "商人幾乎都在城裡，所以這一格多數時候沒有差別。");
        }

        var confirm = Config.ConfirmTravel;
        if (ImGui.Checkbox($"「前往」要按兩次##{InternalName}-Confirm", ref confirm))
        {
            Config.ConfirmTravel = confirm;
            Plugin.Instance.Config.Save();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "預設開啟。表上每一列都有一顆「前往」，彼此只隔幾個像素，\n"
                + "而按錯的代價是角色被傳到別的城市去。\n"
                + "📌 「中止」永遠是按一次就生效（它只往安全方向走）。");
        }
    }

    /// <summary>
    /// 面板本體。獨立視窗與（未來若有需要）設定區共用這一份。
    /// </summary>
    /// <remarks>
    /// 🔴 這是 ImGui 的 Draw 路徑：<b>不做任何 IPC、不碰原生記憶體、不得擲例外</b>。
    /// 這裡只讀已經在 <c>Framework.Update</c> 裡算好的欄位。
    /// </remarks>
    public void DrawPanel()
    {
        lastUiTick = Environment.TickCount64;

        DrawListPicker();
        ImGui.Spacing();
        DrawToolbar();
        ImGui.Spacing();
        DrawStatusLine();
        ImGui.Separator();
        ImGui.Spacing();
        DrawTable();
    }

    private void DrawListPicker()
    {
        var useCustom = Config.UseCustomListName;

        if (!useCustom)
        {
            var current = Config.CraftListKey;
            var label = craftLists.TryGetValue(current, out var name)
                ? name
                : string.IsNullOrEmpty(current) ? "（未選擇）" : $"（清單已不存在：{current}）";

            ImGui.SetNextItemWidth(280f);
            using (var combo = ImRaii.Combo($"製作清單##{InternalName}-List", label))
            {
                if (combo)
                {
                    foreach (var key in craftListKeys)
                    {
                        var selected = string.Equals(key, current, StringComparison.Ordinal);

                        // 🔴 id 一定要用 key 而不是顯示名：AllaganTools 不阻止使用者把兩張清單
                        //    取成同一個名字，而同名的 Selectable 在 ImGui 眼裡是同一個元件
                        //    ⇒ 第二張點下去<b>完全沒有反應</b>，而且不會有任何錯誤訊息。
                        if (!ImGui.Selectable($"{craftLists[key]}##list-{key}", selected)) continue;

                        Config.CraftListKey = key;
                        Plugin.Instance.Config.Save();
                        pendingRefresh = true;
                    }
                }
            }

            if (craftListKeys.Count == 0)
            {
                ImGui.SameLine();
                ImGui.TextColored(UnknownColor, "（一張都沒有）");

                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip(
                        "AllaganTools 沒有回報任何製作清單。兩種可能：\n"
                        + "① 你還沒建製作清單。\n"
                        + "② 你只用它內建的那張「預設製作清單」—— 對方的端點刻意把它排除在外。\n"
                        + "第二種情況請勾下面的「直接輸入清單名稱」，把清單名字打進去（名稱或 key 都收）。");
                }
            }
        }
        else
        {
            var custom = Config.CustomListName;
            ImGui.SetNextItemWidth(280f);
            if (ImGui.InputText($"清單名稱或 key##{InternalName}-Custom", ref custom, 128))
            {
                Config.CustomListName = custom;
                Plugin.Instance.Config.Save();
            }
        }

        var useCustomFlag = useCustom;
        if (ImGui.Checkbox($"直接輸入清單名稱##{InternalName}-UseCustom", ref useCustomFlag))
        {
            Config.UseCustomListName = useCustomFlag;
            Plugin.Instance.Config.Save();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "下拉選單列不出「預設製作清單」（那是 AllaganTools 端刻意排除的），\n"
                + "所以留這一條後路：名稱或 key 都收。");
        }
    }

    private void DrawToolbar()
    {
        var busy = stage is Stage.Counting or Stage.Sourcing;

        using (ImRaii.Disabled(busy))
        {
            if (ImGui.Button($"重新統計##{InternalName}-Refresh"))
                RequestRefresh();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(busy
                ? "正在統計中，等它跑完。"
                : "重新向 AllaganTools 問一次清單內容與持有量，並重查來源與價格。\n"
                  + "這個模組不會自己去算 —— 數字永遠是你上一次按這顆的結果。");
        }

        ImGui.SameLine();

        // 🔴 「中止」永遠可以按：它只往安全方向走。
        if (ImGui.Button($"中止移動##{InternalName}-Abort"))
            pendingAbort = true;

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "叫 Lifestream 中止行程，並持續補送停止移動（蓋過「路徑還在背景計算」那幾秒）。\n"
                + $"也可以用指令 {NavStop.Command}，或用 /tcstopall 把所有會自己動的外掛一起停下來。");
        }

        ImGui.SameLine();
        DrawOptionsInline();
    }

    private void DrawOptionsInline()
    {
        var onlyMissing = Config.OnlyShowMissing;
        if (ImGui.Checkbox($"只顯示缺料##{InternalName}-OnlyMissing2", ref onlyMissing))
        {
            Config.OnlyShowMissing = onlyMissing;
            Plugin.Instance.Config.Save();
        }

        ImGui.SameLine();

        var expand = Config.ExpandIntermediates;
        if (ImGui.Checkbox($"展開中間材料##{InternalName}-Expand2", ref expand))
        {
            Config.ExpandIntermediates = expand;
            Plugin.Instance.Config.Save();
            pendingRefresh = true;
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("改了會立刻重新統計。詳細說明在主視窗的模組設定裡。");
    }

    private void DrawStatusLine()
    {
        if (stage == Stage.Counting)
        {
            ImGui.TextColored(UnknownColor, $"正在統計持有量……{countCursor}/{rows.Count}");
            return;
        }

        if (stage == Stage.Sourcing)
        {
            ImGui.TextColored(UnknownColor, $"正在查詢哪裡買……{sourceCursor}/{rows.Count}");
            return;
        }

        if (!string.IsNullOrEmpty(lastError))
        {
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
            ImGui.TextColored(MissingColor, lastError);
            ImGui.PopTextWrapPos();
        }

        if (lastRefreshAt == DateTime.MinValue)
        {
            if (string.IsNullOrEmpty(lastError))
                ImGui.TextColored(UnknownColor, "還沒統計過。按「重新統計」。");
        }
        else
        {
            var missing = 0;
            var unknown = 0;
            foreach (var row in rows)
            {
                if (!row.OwnedKnown) unknown++;
                else if (row.Gap > 0) missing++;
            }

            ImGui.TextUnformatted(
                $"成品 {outputCount} 種／材料 {rows.Count} 種／缺 {missing} 種"
                + (unknown > 0 ? $"／不確定 {unknown} 種" : string.Empty));

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(BuildSummaryTooltip());
        }

        if (rowLimitHit)
        {
            ImGui.TextColored(MissingColor, $"⚠ 材料種類超過 {MaxRows} 種，清單不完整。");
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    $"只列出前 {MaxRows} 種。多半是因為「展開中間材料」開著而清單很大。\n"
                    + "把它關掉，或把製作清單拆小一點。");
            }
        }

        if (expandTruncated)
        {
            ImGui.TextColored(UnknownColor, "⚠ 有材料展開到深度上限就停了。");
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    $"遞迴展開最多 {RecipeMaterials.MaxDepth} 層。到達上限的那幾筆是以\n"
                    + "「中間材料」的身分列在表上，也就是它們底下還有沒算進來的原料。");
            }
        }

        if (!string.IsNullOrEmpty(lastActionText))
        {
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
            ImGui.TextColored(lastActionOk ? OkColor : MissingColor, lastActionText);
            ImGui.PopTextWrapPos();
        }

        if (someoneIsMoving)
        {
            ImGui.TextColored(MissingColor, $"{mover} 正在帶著角色移動 —— 「前往」按鈕暫時停用。");
        }
        else if (polledOnce && !lifestreamAvailable)
        {
            ImGui.TextColored(UnknownColor, "未偵測到 Lifestream —— 「前往」按鈕停用。");
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    "「前往」是由 Lifestream 執行的（跨區自己傳送、最後一段交給 vnavmesh）。\n"
                    + "沒有它的話這個模組還是能算缺料、查商人與價格，只是沒辦法帶你過去。");
            }
        }
    }

    private string BuildSummaryTooltip()
    {
        var text = $"清單：{(string.IsNullOrEmpty(SelectedList) ? "（未選擇）" : SelectedList)}\n";

        text += lastRefreshAt == DateTime.MinValue
            ? "上次統計：還沒統計過\n"
            : $"上次統計：{lastRefreshAt:MM-dd HH:mm:ss}\n";

        text += Config.ExpandIntermediates
            ? $"展開方式：遞迴到最多 {RecipeMaterials.MaxDepth} 層\n"
            : "展開方式：只展開一層（中間材料原樣列出）\n";

        text += ownersKnown
            ? $"持有量統計範圍：{owners.Count} 個道具欄擁有者（角色本人＋僱員＋部隊／宅邸，HQ 與 NQ 合計）\n"
            : "持有量統計範圍：問不到，所有列都顯示「不知道」\n";

        text += marketAvailable
            ? "價格來源：Marketbuddy 已經看過的掛單快取（不會去查市場）\n"
            : $"價格來源：不可用（{marketReason}）\n";

        if (noRecipeCount > 0)
            text += $"⚠ 有 {noRecipeCount} 個成品查不到配方，它們自己被當成要取得的東西列出來。\n";

        return text.TrimEnd('\n');
    }

    private void DrawTable()
    {
        if (rows.Count == 0)
        {
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
            ImGui.TextDisabled(
                lastRefreshAt == DateTime.MinValue
                    ? "還沒有資料。挑一張製作清單，然後按「重新統計」。"
                    : "這張清單展開之後沒有任何材料（清單是空的，或裡面的東西都沒有配方）。");
            ImGui.PopTextWrapPos();
            return;
        }

        using var table = ImRaii.Table(
            $"{InternalName}-table", 7,
            ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY);
        if (!table) return;

        ImGui.TableSetupColumn("材料", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("需要", ImGuiTableColumnFlags.WidthFixed, 54f);
        ImGui.TableSetupColumn("持有", ImGuiTableColumnFlags.WidthFixed, 54f);
        ImGui.TableSetupColumn("缺口", ImGuiTableColumnFlags.WidthFixed, 54f);
        ImGui.TableSetupColumn("來源", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("價格", ImGuiTableColumnFlags.WidthFixed, 84f);
        ImGui.TableSetupColumn("操作", ImGuiTableColumnFlags.WidthFixed, 128f);
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();

        var hidden = 0;

        foreach (var row in rows)
        {
            // 🔑 「不知道」永遠不被篩掉：它是最需要使用者注意的一類。
            if (Config.OnlyShowMissing && row.OwnedKnown && row.Gap <= 0)
            {
                hidden++;
                continue;
            }

            if (Config.HideCrystals && row.IsCrystal)
            {
                hidden++;
                continue;
            }

            DrawRow(row);
        }

        if (hidden <= 0) return;

        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextDisabled($"（另有 {hidden} 列被篩選條件隱藏）");
    }

    private void DrawRow(MaterialRow row)
    {
        using var id = ImRaii.PushId((int)row.ItemId);

        ImGui.TableNextRow();

        // ① 材料
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(row.Name);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(BuildMaterialTooltip(row));

        // ② 需要
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(row.Required.ToString());

        // ③ 持有
        ImGui.TableNextColumn();
        if (row.OwnedKnown)
        {
            ImGui.TextUnformatted(row.Owned.ToString());
        }
        else
        {
            // 🔑 「不知道」畫成灰色的 ?，絕不畫 0 —— 0 的意思是「一個都沒有」。
            ImGui.TextColored(UnknownColor, "?");
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    "問不到持有量。可能是 AllaganTools 沒裝／沒初始化完畢，\n"
                    + "或它還沒掃到任何道具欄。\n"
                    + "這一格不是 0 —— 是「我不知道」。");
            }
        }

        // ④ 缺口
        ImGui.TableNextColumn();
        if (!row.OwnedKnown)
        {
            ImGui.TextColored(UnknownColor, "?");
        }
        else if (row.Gap > 0)
        {
            ImGui.TextColored(MissingColor, row.Gap.ToString());
        }
        else
        {
            ImGui.TextColored(OkColor, "0");
        }

        // ⑤ 來源
        ImGui.TableNextColumn();
        DrawSourceCell(row);

        // ⑥ 價格
        ImGui.TableNextColumn();
        DrawPriceCell(row);

        // ⑦ 操作
        ImGui.TableNextColumn();
        DrawActionCell(row);
    }

    private string BuildMaterialTooltip(MaterialRow row)
    {
        var text = $"道具編號 #{row.ItemId}\n";

        text += row.MaxDepth == 0
            ? "展開層數：成品的直接材料\n"
            : $"展開層數：第 {row.MaxDepth + 1} 層（中間材料的材料）\n";

        if (row.IsCrystal)
            text += "分類：水晶／碎晶／晶簇\n";

        if (RecipeMaterials.HasRecipe(row.ItemId))
        {
            var count = RecipeMaterials.RecipeCount(row.ItemId);
            text += $"這件東西自己也有配方（{count} 個）—— 可以做，也可以買。\n";
        }

        if (row.NeededBy.Count > 0)
        {
            text += "\n哪些成品要用到它：\n";
            foreach (var pair in row.NeededBy)
                text += $"  · {pair.Key} 需要 {pair.Value} 個\n";
        }

        if (row.OwnedKnown)
        {
            text += $"\n持有 {row.Owned} 個（{owners.Count} 個道具欄擁有者合計，HQ 與 NQ 一起算）";
        }

        return text.TrimEnd('\n');
    }

    private void DrawSourceCell(MaterialRow row)
    {
        if (!row.Sourced)
        {
            ImGui.TextDisabled("—");
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(row.OwnedKnown && row.Gap <= 0
                    ? "不缺這一項，所以沒有去查哪裡買。"
                    : "還沒查到這一列（統計還在進行中）。");
            }

            return;
        }

        switch (row.VendorStatus)
        {
            case VendorLookupStatus.Unavailable:
                // 🔑 「問不到」與「沒有商人賣」是兩句不同的話，不能畫成同一個樣子。
                ImGui.TextColored(UnknownColor, "?");
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(row.VendorReason);
                return;

            case VendorLookupStatus.NoVendor:
                ImGui.TextColored(UnknownColor, "未查到");
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip(
                        "Item Vendor Location 沒有這件東西的商人資料。\n"
                        + "通常代表它只能自己採集／製作，或只在市場佈告板上流通。");
                }

                return;
        }

        if (row.Vendors.Count == 0)
        {
            ImGui.TextColored(UnknownColor, "未查到");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Item Vendor Location 認得這件東西，但沒有可回報的商人。");
            return;
        }

        if (row.BestVendor == null)
        {
            // 有商人資料但沒有任何一個查得到位置。
            ImGui.TextColored(UnknownColor, row.SourceLabel);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(BuildVendorTooltip(row));
            return;
        }

        ImGui.TextUnformatted(row.SourceLabel);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(BuildVendorTooltip(row));
    }

    /// <summary>來源欄那一行字（在查詢當下算一次，之後 Draw 只讀它）。</summary>
    private static string BuildSourceLabel(MaterialRow row)
    {
        if (row.BestVendor is { } best) return DescribeVendorShort(best);

        if (row.Vendors.Count == 0) return string.Empty;

        var first = row.Vendors[0];
        var npc = string.IsNullOrEmpty(first.NpcName) ? $"NPC #{first.NpcId}" : first.NpcName;

        return $"{npc}（位置未知）";
    }

    private static string BuildVendorTooltip(MaterialRow row)
    {
        var text = $"Item Vendor Location 回報 {row.Vendors.Count} 個管道：\n";

        var shown = 0;
        foreach (var vendor in row.Vendors)
        {
            if (shown >= 12)
            {
                text += $"  …另外還有 {row.Vendors.Count - shown} 個（按「商人清單」看完整的）\n";
                break;
            }

            shown++;

            var npc = string.IsNullOrEmpty(vendor.NpcName) ? $"NPC #{vendor.NpcId}" : vendor.NpcName;
            text += $"  · {npc}";

            if (!string.IsNullOrEmpty(vendor.ShopName))
                text += $"／{vendor.ShopName}";

            if (vendor.HasLocation)
            {
                text += $"／{ZoneName(vendor.TerritoryTypeId)}";

                // 🔑 座標算不出來時寫「座標未知」而不是 (0, 0)。
                text += vendor.MapCoordinatesKnown
                    ? $" ({vendor.MapX:0.0}, {vendor.MapY:0.0})"
                    : " （座標未知）";
            }
            else
            {
                text += "／位置未知";
            }

            if (!string.IsNullOrEmpty(vendor.SourceType))
                text += $"　[{vendor.SourceType}]";

            // 🔴 這一欄是從對方的 JSON 反序列化來的，理論上可能是 null
            //    （Newtonsoft 對 "Costs": null 會把初始式的空清單換成 null）。
            //    而這裡是 ImGui 的 Draw 路徑 —— 擲一次例外整扇視窗就換成錯誤面板。
            if (vendor.Costs is { Count: > 0 } costs)
            {
                text += "　代價：";
                for (var i = 0; i < costs.Count; i++)
                {
                    if (i > 0) text += "、";
                    text += $"{costs[i].Amount} × {costs[i].CurrencyName}";
                }
            }

            text += "\n";
        }

        text += "\n「前往」去的是清單裡第一個有位置、而且用 Gil 買得到的那一個；";
        text += "\n其他管道（特殊商店、成就、任務獎勵）不一定走過去就買得到。";

        return text;
    }

    private void DrawPriceCell(MaterialRow row)
    {
        if (!marketAvailable)
        {
            // 🔑 沒有查價來源時畫 ?，不畫 0。
            ImGui.TextColored(UnknownColor, "?");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(marketReason + "\n（沒有它一樣算得出缺料，只是看不到價格。）");
            return;
        }

        if (!row.Sourced)
        {
            ImGui.TextDisabled("—");
            return;
        }

        var snapshot = row.Market;
        if (snapshot == null)
        {
            ImGui.TextColored(UnknownColor, "?");
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    "Marketbuddy 的快取裡沒有這件東西。\n"
                    + "那份快取是被動累積的 —— 你自己在市場佈告板上看過它之後才會有。\n"
                    + "🔴 本模組不會替你去查市場（那是明確的紅線）。");
            }

            return;
        }

        var nq = snapshot.LowestPriceNq;
        var hq = snapshot.LowestPriceHq;
        var best = nq != 0 && hq != 0 ? Math.Min(nq, hq) : nq != 0 ? nq : hq;

        if (best == 0)
        {
            if (snapshot.ConfirmedEmpty)
            {
                ImGui.TextColored(UnknownColor, "無人販售");
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(BuildPriceTooltip(snapshot) + "\nMarketbuddy 確認過當時沒有人在賣。");
                return;
            }

            ImGui.TextColored(UnknownColor, "?");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(BuildPriceTooltip(snapshot) + "\n兩種品質都沒有掛單價，但也沒有確認過「真的沒人賣」。");
            return;
        }

        ImGui.TextUnformatted(best.ToString("N0"));
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(BuildPriceTooltip(snapshot));
    }

    private static string BuildPriceTooltip(MarketSnapshotEntry snapshot)
    {
        var text = "Marketbuddy 看過的最低單價（不含手續費）：\n";

        text += snapshot.LowestPriceNq != 0
            ? $"  普通品 {snapshot.LowestPriceNq:N0} Gil（第一頁 {snapshot.ListingCountNq} 筆）\n"
            : "  普通品：沒有掛單\n";

        text += snapshot.LowestPriceHq != 0
            ? $"  高品質 {snapshot.LowestPriceHq:N0} Gil（第一頁 {snapshot.ListingCountHq} 筆）\n"
            : "  高品質：沒有掛單\n";

        // ⚠️ 筆數是「第一頁」的筆數，不是總掛單數（提供端的註解逐字寫著要當「至少 N 件」讀）。
        text += "  筆數只算第一頁，實際可能更多。\n";

        if (snapshot.ObservedAtUnixMs > 0)
        {
            var observed = DateTimeOffset.FromUnixTimeMilliseconds(snapshot.ObservedAtUnixMs).ToLocalTime();
            text += $"看到的時間：{observed:MM-dd HH:mm}\n";
        }

        text += "🔴 只顯示、不採購。本模組不會送任何市場查詢，也不會幫你買任何東西。";

        return text;
    }

    private void DrawActionCell(MaterialRow row)
    {
        var best = row.BestVendor;
        var canGo = best != null && best.HasLocation && best.TerritoryTypeId != 0
                    && lifestreamAvailable && !someoneIsMoving;

        var armed = IsArmed(NavKey(row.ItemId));
        var label = armed ? "確定前往" : "前往";

        using (ImRaii.Disabled(!canGo))
        {
            if (ImGui.Button($"{label}##{InternalName}-go-{row.ItemId}"))
                OnNavButton(row);
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(BuildNavTooltip(row, best, canGo, armed));

        ImGui.SameLine();

        if (ImGui.Button($"商人清單##{InternalName}-ivl-{row.ItemId}"))
        {
            if (!VendorLookupIpc.TryOpenVendorWindow(row.ItemId))
                Report(false, "沒有偵測到 Item Vendor Location，開不了它的商人清單視窗。");
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "開 Item Vendor Location 自己的結果視窗（完整清單、篩選、右鍵選單都在那裡）。\n"
                + "📌 刻意不在這裡重做一份 —— 它已經處理好「一件東西好幾家店賣」這類情況。");
        }
    }

    private string BuildNavTooltip(MaterialRow row, VendorLocationEntry? best, bool canGo, bool armed)
    {
        if (best == null || !best.HasLocation || best.TerritoryTypeId == 0)
        {
            return row.Sourced
                ? "沒有已知位置的商人可以去。\n按「商人清單」看看 Item Vendor Location 還知道什麼。"
                : "還沒查到這一列的來源。";
        }

        var text = $"去 {DescribeVendorShort(best)} 買「{row.Name}」";

        if (best.MapCoordinatesKnown)
            text += $"（地圖座標 {best.MapX:0.0}, {best.MapY:0.0}）";

        text += "\n";
        text += Config.AllowFlying
            ? "跨區的部分由 Lifestream 自己傳送，最後一段走路或飛行。\n"
            : "跨區的部分由 Lifestream 自己傳送，最後一段走路。\n";

        text += "🔴 一次只去這一個地方，不會自己接著去下一個。\n";

        if (!lifestreamAvailable)
            text += "⚠ 現在停用：未偵測到 Lifestream。\n";

        if (someoneIsMoving)
            text += $"⚠ 現在停用：{mover} 正在帶著角色移動。\n";

        if (canGo && armed)
            text += "再按一次就出發（五秒內）。\n";
        else if (canGo && Config.ConfirmTravel)
            text += "按第一次只是武裝，再按一次才真的出發。\n";

        return text.TrimEnd('\n');
    }

    private void OnNavButton(MaterialRow row)
    {
        var key = NavKey(row.ItemId);

        if (!Config.ConfirmTravel)
        {
            pendingNavItemId = row.ItemId;
            return;
        }

        if (IsArmed(key))
        {
            confirmKey = string.Empty;
            confirmUntil = 0;
            pendingNavItemId = row.ItemId;
            return;
        }

        confirmKey = key;
        confirmUntil = Environment.TickCount64 + ConfirmWindowMs;
    }

    private static string NavKey(uint itemId) => $"go-{itemId}";

    /// <summary>這一顆按鈕現在是「按第一次、等確認」的狀態嗎。</summary>
    /// <remarks>
    /// 📌 武裝狀態有五秒有效期，過了自動解除 —— 誤觸之後不會留一顆隨時會出發的按鈕在那裡。
    /// </remarks>
    private bool IsArmed(string key) =>
        string.Equals(confirmKey, key, StringComparison.Ordinal)
        && Environment.TickCount64 < confirmUntil;
}

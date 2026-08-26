using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Memory;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using TCToolbox.Core;
using CsUIState = FFXIVClientStructs.FFXIV.Client.Game.UI.UIState;
using ValueType = FFXIVClientStructs.FFXIV.Component.GUI.ValueType;

namespace TCToolbox.Modules;

/// <summary>
/// 幻卡回收清單：列出背包裡「幻卡列表已經收錄、所以是多的」九宮幻卡，以及各自回收得到多少金碟幣；
/// 在遊戲的「幻卡回收」視窗開著時可以幫你送出一次回收，但遊戲自己的確認框一律由你親自按。
/// </summary>
/// <remarks>
/// <para>
/// 🔴🔴 <b>永遠不會替你按下回收確認框。</b>送出回收之後跳出來的是<b>遊戲自己的</b>
/// 「回收數量／回收可獲得金碟幣」對話框（<c>ShopCardDialog</c>），本模組<b>完全不碰它</b>
/// —— 數量與「回收」鈕一律由你自己按。上游 DailyRoutines 的 <c>AutoSellCards</c> 會
/// 自動把數量拉到上限、自動按掉那個對話框，並且<b>掛著迴圈把整袋卡一路回收完</b>，
/// <b>那三件事都刻意不做</b>：這裡是「按一次、送出一張、你確認一張」。
/// </para>
/// <para>
/// ⚠️ <b>YesAlready 有一個會替你按掉這個對話框的開關。</b>它的 Bothers 分頁裡有一項
/// <c>ShopCardDialog</c>（說明是「Automatically confirm selling Triple Triad cards in the saucer.」，
/// <b>預設關閉</b>），勾起來之後它會把數量拉到上限並直接按下「回收」。
/// 本模組<b>不去對抗它</b>（不暫停、不改它的設定）—— 但那等於本模組刻意保留的那層人工確認消失了，
/// 所以下面的設定 UI 會在偵測到 YesAlready 正在運作時提醒一句。
/// </para>
/// <para>
/// 🔑 <b>「這張是不是多的」用的是遊戲自己的兩份資料互相校驗，校驗不過就顯示「?」而不是猜。</b>
/// 幻卡列表的收錄狀態存在 <c>UIState</c> 的位元遮罩裡；同一個結構裡另外有一個遊戲自己維護的
/// 收錄總數。本模組把位元遮罩的 popcount 拿去跟那個總數比對，<b>對不上就整個判定為「不知道」</b>
/// ——列上顯示灰字提示、清單裡每一列畫「?」、回收按鈕鎖住。
/// 這是為了讓「台服的結構偏移跟 FFXIVClientStructs 對不上」這個無法離線證明的假設<b>不會靜默給錯答案</b>：
/// 偏移錯了的話兩份資料幾乎不可能剛好一致。
/// </para>
/// <para>
/// 🔴 <b>不保存任何原生指標。</b>背包每次掃描重新向 <c>InventoryManager</c> 取容器；
/// 位元遮罩的 <c>BitArray</c> 只在單一函式內存在（它內部包著裸指標，<b>絕不放進欄位</b>）；
/// 回收流程的每一步都重新用名字解析 addon，不跨幀留 <c>AtkUnitBase*</c>。
/// </para>
/// <para>
/// ⚠️ <b>送出回收用的呼叫參數移植自上游，台服沒有離線驗證過。</b>失敗形式是「遊戲沒有跳出確認框」
/// ——這時流程會逾時中止並把當下的 <c>AtkValues</c> 傾印到記錄（<c>Information</c> 級）供回報，
/// <b>不會有任何東西被回收</b>（真正的回收發生在你按下確認框那一刻）。
/// </para>
/// </remarks>
public sealed unsafe class TriadCardRecycle : TcModule
{
    public override string InternalName => "TriadCardRecycle";

    public override string DisplayName => "幻卡回收清單";

    public override string Description =>
        "列出背包裡「幻卡列表已經收錄、所以是多的」九宮幻卡，以及各自回收可得多少金碟幣。" +
        "在遊戲的「幻卡回收」視窗開著時可以按一下幫你送出一張，" +
        "但遊戲自己的回收確認框永遠由你親自按——不自動確認、不掛迴圈連續回收。";

    public override ModuleCategory Category => ModuleCategory.Inventory;

    /// <summary>開著不去操作它，遊戲行為完全不變（只讀背包與資料表，不掛任何 hook）。</summary>
    public override bool IsManualTrigger => true;

    public override bool HasConfigUI => true;

    #region 常數

    /// <summary>
    /// 九宮幻卡道具的 <c>ItemAction</c> 類型。
    /// </summary>
    /// <remarks>
    /// 📌 台服 7.20 EXD 離線核對：<c>ItemAction</c> 表裡類型 3357 共 439 列、<c>Data[0]</c> 是
    /// 1~439 連號，正好對上 439 個「九宮幻卡：ｘｘ」道具（例：道具 9772「九宮幻卡：渡渡鳥」
    /// → <c>Data[0]</c>＝1 →<c>TripleTriadCard</c> 第 1 列「渡渡鳥」）。
    /// <b>程式不寫死這 439 筆，只寫死類型號</b>——對照表每次啟用時從資料表現算，
    /// 算出 0 筆就把整個模組鎖住（見 <see cref="EnsureLookups"/>）。
    /// </remarks>
    private const ushort TriadCardItemActionType = 3357;

    /// <summary>「幻卡回收」視窗。</summary>
    /// <remarks>📌 台服執行檔離線核對：這個字串在 <c>ffxiv_dx11.exe</c> 裡存在（1 次）。</remarks>
    private const string RecycleAddon = "TripleTriadCoinExchange";

    /// <summary>回收確認框。<b>本模組只讀它在不在，永遠不對它送任何東西。</b></summary>
    /// <remarks>📌 台服執行檔離線核對：這個字串在 <c>ffxiv_dx11.exe</c> 裡存在（2 次）。</remarks>
    private const string ConfirmAddon = "ShopCardDialog";

    /// <summary>「幻卡回收」在 <c>Addon</c> 表的列號（台服 7.20 離線核對＝「幻卡回收」）。</summary>
    private const uint AddonRowRecycleTitle = 9510;

    /// <summary>「已收錄」在 <c>Addon</c> 表的列號（台服 7.20 離線核對，是回收視窗的欄位標題）。</summary>
    private const uint AddonRowRecorded = 9515;

    /// <summary>「沒有能夠回收的卡片。」在 <c>Addon</c> 表的列號（台服 7.20 離線核對）。</summary>
    private const uint AddonRowNothingToRecycle = 9516;

    /// <summary>
    /// 回收視窗上「還剩幾張可回收」的 <c>AtkValue</c> 索引。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>移植自上游、台服沒有驗證過，所以只拿來顯示、不拿來當任何判斷的依據。</b>
    /// 讀到的型別不是 <see cref="ValueType.Int"/> 就當作「不知道」畫成「?」——
    /// 畫成 0 會讓人以為真的沒有卡可以回收。
    /// </remarks>
    private const int RemainingCountValueIndex = 1;

    /// <summary>傾印診斷時最多看幾個 <c>AtkValue</c>（上游用到的最大索引是 204，這裡留餘裕）。</summary>
    private const int MaxDumpValues = 220;

    /// <summary>背包重掃間隔（毫秒）。</summary>
    private const int RescanIntervalMs = 2_000;

    private const string ScanThrottleKey = "TriadCardRecycle.Scan";

    private const string YesAlreadyThrottleKey = "TriadCardRecycle.YesAlready";

    /// <summary>等確認框那一步的名字。逾時處理要靠它分辨「是這一步逾時」。</summary>
    private const string WaitConfirmStep = "等待遊戲跳出回收確認框";

    /// <summary>只掃主背包四袋。</summary>
    private static readonly InventoryType[] PlayerBags =
        [InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4];

    /// <summary>鞍袋四格。只用來數「還有幾張放在這裡」，不會去動它。</summary>
    private static readonly InventoryType[] SaddleBags =
    [
        InventoryType.SaddleBag1, InventoryType.SaddleBag2,
        InventoryType.PremiumSaddleBag1, InventoryType.PremiumSaddleBag2,
    ];

    #endregion

    #region 狀態

    /// <summary>幻卡列表收錄狀態的可信度。</summary>
    /// <remarks>
    /// 🔴 <b>零值必須是「不知道」</b>：任何忘了指派、或還沒掃過的路徑都應該落在「不知道」，
    /// 而不是落在「已驗證」——後者會讓 UI 用一份沒被校驗過的資料畫出斬釘截鐵的「已收錄」。
    /// </remarks>
    private enum AlbumState
    {
        Unknown = 0,
        Verified = 1,
    }

    /// <summary>背包裡的一張幻卡（當幀快照，不留指標）。</summary>
    private readonly record struct BagCard(
        InventoryType Container,
        short Slot,
        uint ItemId,
        ushort CardId,
        string Name,
        int Quantity,
        int MgpEach,
        bool Recorded);

    private TriadCardRecycleConfig Config => Plugin.Instance.Config.TriadCardRecycle;

    private readonly TaskQueue queue = new() { DefaultTimeoutMs = 10_000 };

    /// <summary>道具 id → <c>TripleTriadCard</c> 列號。<c>null</c>＝還沒建過。</summary>
    private Dictionary<uint, ushort>? itemToCard;

    /// <summary>
    /// 對照表建到空的、還可以再試幾次。
    /// </summary>
    /// <remarks>
    /// 🔴 存在的理由：資料表在剛載入時可能暫時取不到，如果第一次建成空的就永久快取起來，
    /// 這個模組會**在沒有任何錯誤訊息的情況下永遠列不出東西**，而且重開遊戲才會好。
    /// 反過來如果無限重試，客戶端真的沒有這批資料時就會每 2 秒掃一次整張道具表。
    /// ⇒ 給有限次數，用完就認了並且留一行記錄。
    /// </remarks>
    private int lookupRetriesLeft = 5;

    /// <summary><c>TripleTriadCard</c> 列號 → 回收可得金碟幣（<c>TripleTriadCardResident.SaleValue</c>）。</summary>
    private Dictionary<ushort, int> cardMgp = [];

    private readonly List<BagCard> bagCards = [];

    private AlbumState album = AlbumState.Unknown;

    private int albumPopCount;

    private ulong albumReportedCount;

    /// <summary>鞍袋裡的幻卡張數；<c>-1</c>＝鞍袋沒載入，也就是<b>不知道</b>（不是 0）。</summary>
    private int saddleBagCards = -1;

    /// <summary>上一次寫進記錄的校驗狀態，用來只在狀態變化時寫一行。</summary>
    private AlbumState? loggedAlbum;

    /// <summary>遊戲自己的「幻卡回收」用語（讀不到就是空字串，UI 退回內建字面值）。</summary>
    private string recycleTitle = string.Empty;

    private string recordedLabel = string.Empty;

    private string lastResult = string.Empty;

    private bool lastResultIsProblem;

    /// <summary>YesAlready 目前在不在（<c>null</c>＝沒裝／問不到）。只影響 UI 上的一句提醒。</summary>
    private bool? yesAlreadyActive;

    #endregion

    #region 生命週期

    protected override void OnEnable()
    {
        var addons = Svc.Data.GetExcelSheet<Addon>();
        recycleTitle = addons?.GetRowOrDefault(AddonRowRecycleTitle)?.Text.ExtractText().Trim() ?? string.Empty;
        recordedLabel = addons?.GetRowOrDefault(AddonRowRecorded)?.Text.ExtractText().Trim() ?? string.Empty;
        var nothingLabel = addons?.GetRowOrDefault(AddonRowNothingToRecycle)?.Text.ExtractText().Trim() ?? string.Empty;

        // 這三個字串是「這個客戶端認得幻卡回收這件事」的佐證，出問題時第一個要看的就是它們。
        Svc.Log.Information(
            $"[{InternalName}] 遊戲自己的用語：Addon#{AddonRowRecycleTitle}＝" +
            $"「{(recycleTitle.Length > 0 ? recycleTitle : "（空）")}」、" +
            $"Addon#{AddonRowRecorded}＝「{(recordedLabel.Length > 0 ? recordedLabel : "（空）")}」、" +
            $"Addon#{AddonRowNothingToRecycle}＝「{(nothingLabel.Length > 0 ? nothingLabel : "（空）")}」。");

        queue.OnTimeout = OnStepTimeout;

        Svc.Framework.Update += OnUpdate;
    }

    protected override void OnDisable()
    {
        Svc.Framework.Update -= OnUpdate;
        queue.Abort();
        bagCards.Clear();
        album = AlbumState.Unknown;
        loggedAlbum = null;
        saddleBagCards = -1;
        // 重新啟用時對照表也重新建一次（含重試額度）——不然「重開模組試試看」對這條路徑完全無效。
        itemToCard = null;
        lookupRetriesLeft = 5;
        Throttle.Reset(ScanThrottleKey);
        Throttle.Reset(YesAlreadyThrottleKey);
    }

    private void OnUpdate(IFramework framework)
    {
        queue.Tick();

        if (!Throttle.Pass(ScanThrottleKey, RescanIntervalMs)) return;

        try
        {
            Rescan();
        }
        catch (Exception ex)
        {
            // 掃描失敗就維持上一次的快照，不要讓每幀的 Framework.Update 一直炸。
            Svc.Log.Warning(ex, $"[{InternalName}] 掃描失敗（保留上一次的結果）");
        }
    }

    #endregion

    #region 列上提示

    /// <remarks>
    /// 🔴 這個屬性在 ImGui 的 Draw 路徑上被讀，<b>只能讀已經算好的欄位</b>，不做任何掃描、不擲例外。
    /// <para>
    /// 📌 刻意<b>只有</b>「不知道」這一種提示：「背包裡有 N 張多的卡」不是問題，
    /// 拿橘字警告去講它會讓人對這個提示欄位失去信任。而「收錄狀態讀不出來」必須在列上看得見，
    /// 因為那正是本模組整份清單失去意義的情況。
    /// </para>
    /// </remarks>
    public override ModuleNotice? RowNotice
    {
        get
        {
            if (!IsEnabled) return null;
            if (!Svc.ClientState.IsLoggedIn) return null;
            if (album == AlbumState.Verified) return null;

            return new ModuleNotice(
                ModuleNoticeLevel.Unknown,
                "幻卡列表收錄狀態未知",
                "判斷「這張卡是不是多的」要看幻卡列表有沒有收錄它，而這份資料沒有通過自我校驗"
                + "（位元遮罩算出來的收錄張數對不上遊戲自己回報的總數）。\n"
                + "所以清單上每一張都會畫「?」——這是刻意的，"
                + "把讀不到的東西畫成「未收錄」會讓你以為那張卡不能回收。\n"
                + "（回收按鈕不受影響：它送出的是遊戲自己選定的那一張，"
                + "而且一定會跳出遊戲的確認框讓你過目。）\n"
                + "剛登入還沒讀到資料時也會是這個狀態，等一下再看。");
        }
    }

    #endregion

    #region 掃描

    /// <summary>建立道具→卡片與卡片→金碟幣兩張對照表（只建一次）。</summary>
    private void EnsureLookups()
    {
        if (itemToCard != null) return;

        var map = new Dictionary<uint, ushort>();
        var mgp = new Dictionary<ushort, int>();

        var items = Svc.Data.GetExcelSheet<Item>();
        if (items != null)
        {
            foreach (var item in items)
            {
                if (item.ItemAction.ValueNullable is not { } action) continue;
                if (action.Type != TriadCardItemActionType) continue;

                var cardId = (ushort)action.Data[0];
                if (cardId == 0) continue;

                map[item.RowId] = cardId;
            }
        }

        var residents = Svc.Data.GetExcelSheet<TripleTriadCardResident>();
        if (residents != null)
        {
            foreach (var resident in residents)
                mgp[(ushort)resident.RowId] = (int)resident.SaleValue;
        }

        if (map.Count == 0 && lookupRetriesLeft > 0)
        {
            lookupRetriesLeft--;
            Svc.Log.Information(
                $"[{InternalName}] 幻卡對照表建出來是空的（道具表可能還沒載好），"
                + $"下次掃描時再試一次（還剩 {lookupRetriesLeft} 次）。");
            return;
        }

        itemToCard = map;
        cardMgp = mgp;

        Svc.Log.Information(
            $"[{InternalName}] 幻卡對照表建好：{map.Count} 種幻卡道具、{mgp.Count} 張卡查得到回收價"
            + (map.Count == 0
                ? $"——**是 0**。重試次數已用完，這個客戶端查不到 ItemAction 類型 {TriadCardItemActionType} 的道具，模組維持空清單。"
                : "。"));
    }

    private void Rescan()
    {
        if (!Svc.ClientState.IsLoggedIn)
        {
            bagCards.Clear();
            album = AlbumState.Unknown;
            saddleBagCards = -1;
            return;
        }

        EnsureLookups();
        RefreshAlbumState();
        ScanBags();
    }

    /// <summary>
    /// 讀幻卡列表的收錄位元遮罩，並拿遊戲自己的收錄總數<b>校驗</b>它。
    /// </summary>
    /// <remarks>
    /// 🔴 <c>UIState.Instance()</c> 是 <c>[StaticAddress(…)]</c> 且<b>沒有</b> <c>isPointer</c>，
    /// 產生器產出的是「解不出來就擲 <c>InvalidOperationException</c>、否則回非 null」——
    /// 所以這裡<b>刻意不寫 null 檢查</b>，寫了是死碼，反而讓人以為已經防過了。
    /// <para>
    /// 🔑 校驗的意義：位元遮罩與總數是同一份資料的兩種表示，<b>結構偏移正確時必然一致</b>。
    /// 台服的偏移萬一與 FFXIVClientStructs 對不上，讀到的是別的欄位，兩者幾乎不可能剛好吻合
    /// ⇒ 校驗失敗 ⇒ 整個模組退化成「不知道」。額外要求總數 &gt; 0，是因為結構還沒建好時
    /// 兩邊都是 0，<b>0 == 0 會讓校驗變成空砲</b>。
    /// </para>
    /// </remarks>
    private void RefreshAlbumState()
    {
        var uiState = CsUIState.Instance();

        // 🔴 BitArray 內部包著裸指標：只在這個函式與 ScanBags 內當區域變數用，絕不存進欄位。
        var bits = uiState->UnlockedTripleTriadCardsBitArray;

        albumPopCount = bits.PopCount;
        albumReportedCount = uiState->UnlockedTripleTriadCardsCount;

        album = albumPopCount > 0 && (ulong)albumPopCount == albumReportedCount
            ? AlbumState.Verified
            : AlbumState.Unknown;

        if (loggedAlbum == album) return;
        loggedAlbum = album;

        Svc.Log.Information(
            $"[{InternalName}] 幻卡列表收錄狀態自我校驗："
            + (album == AlbumState.Verified ? "通過" : "**沒過**")
            + $"（位元遮罩算出 {albumPopCount} 張、遊戲回報 {albumReportedCount} 張、"
            + $"遮罩容量 {bits.BitCount} 位元）。"
            + (album == AlbumState.Verified
                ? string.Empty
                : "沒過時清單一律顯示「?」，不會宣稱任何一張是「多的」；剛登入時短暫沒過是正常的。"));
    }

    private void ScanBags()
    {
        bagCards.Clear();

        var map = itemToCard;
        if (map == null || map.Count == 0)
        {
            saddleBagCards = -1;
            return;
        }

        // InventoryManager.Instance() 同樣是 [StaticAddress] 無 isPointer ⇒ 永不回 null（判空是死碼）。
        var manager = InventoryManager.Instance();

        var verified = album == AlbumState.Verified;
        var bits = CsUIState.Instance()->UnlockedTripleTriadCardsBitArray;

        foreach (var bag in PlayerBags)
        {
            var container = manager->GetInventoryContainer(bag);

            // 🔴 判的是 Items 不是 GetInventorySlot 的回傳值：Items 為 null 而 Size > 0 時，
            //    GetInventorySlot 回的是「null + 偏移」這種非 null 的假指標，item != null 一定通過。
            if (container == null || !container->IsLoaded || container->Items == null) continue;

            var size = container->Size;
            for (var i = 0; i < size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot == null) continue;

                // 幻卡沒有 HQ，但照既有慣例先把 HQ 位移去掉，免得日後資料變了就整批漏。
                var itemId = slot->ItemId % 1_000_000;
                if (itemId == 0) continue;
                if (!map.TryGetValue(itemId, out var cardId)) continue;

                var recorded = verified && bits.TryGet(cardId, out var bit) && bit;
                cardMgp.TryGetValue(cardId, out var mgpEach);

                bagCards.Add(new BagCard(
                    bag, slot->Slot, itemId, cardId, ItemNames.Get(itemId), slot->Quantity, mgpEach, recorded));
            }
        }

        bagCards.Sort(static (a, b) =>
        {
            // 已收錄（＝可回收）的排前面，再來是「整疊值多少」，最後才用名字定序（避免每次順序跳動）。
            var byRecorded = b.Recorded.CompareTo(a.Recorded);
            if (byRecorded != 0) return byRecorded;

            var byValue = (b.MgpEach * b.Quantity).CompareTo(a.MgpEach * a.Quantity);
            if (byValue != 0) return byValue;

            return string.CompareOrdinal(a.Name, b.Name);
        });

        saddleBagCards = CountSaddleBagCards(manager, map);
    }

    /// <summary>
    /// 數鞍袋裡的幻卡。
    /// </summary>
    /// <remarks>
    /// 🔑 回傳 <c>-1</c>＝<b>不知道</b>（鞍袋容器沒載入，也就是這次上線還沒開過鞍袋）。
    /// <b>刻意不回 0</b>：把「沒載入」畫成「0 張」會讓一個把卡全部堆在鞍袋裡的人以為自己沒有多的卡。
    /// </remarks>
    private static int CountSaddleBagCards(InventoryManager* manager, Dictionary<uint, ushort> map)
    {
        var total = 0;
        var anyLoaded = false;

        foreach (var bag in SaddleBags)
        {
            var container = manager->GetInventoryContainer(bag);
            if (container == null || !container->IsLoaded || container->Items == null) continue;

            anyLoaded = true;

            var size = container->Size;
            for (var i = 0; i < size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot == null) continue;

                var itemId = slot->ItemId % 1_000_000;
                if (itemId == 0) continue;
                if (!map.ContainsKey(itemId)) continue;

                total += slot->Quantity;
            }
        }

        return anyLoaded ? total : -1;
    }

    #endregion

    #region 回收流程

    /// <summary>回收視窗現在開著嗎。</summary>
    private static bool RecycleWindowOpen => UiHelper.IsAddonReady(RecycleAddon);

    /// <summary>回收確認框現在開著嗎。<b>只讀，不碰。</b></summary>
    private static bool ConfirmWindowOpen => UiHelper.IsAddonReady(ConfirmAddon);

    /// <summary>
    /// 讀回收視窗上「還剩幾張可回收」。讀不到（含型別不符）回 <see langword="false"/>，UI 畫「?」。
    /// </summary>
    private static bool TryReadRemaining(out int remaining)
    {
        remaining = 0;

        var addon = UiHelper.GetAddon(RecycleAddon);
        if (!UiHelper.IsReady(addon)) return false;

        var values = addon->AtkValues;
        if (values == null) return false;
        if (addon->AtkValuesCount <= RemainingCountValueIndex) return false;

        var value = values[RemainingCountValueIndex];
        if (value.Type != ValueType.Int) return false;

        remaining = value.Int;
        return true;
    }

    /// <summary>
    /// 送出<b>一次</b>回收，然後停下來等你按遊戲自己的確認框。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>刻意沒有「下一張」</b>：整條佇列跑完就結束，要回收下一張得由你再按一次。
    /// 這是「不做自動接手鏈」這條設計約束的實作方式——沒有任何路徑會在確認框關閉後自己再送一次。
    /// </remarks>
    private void StartOneRecycle()
    {
        if (queue.IsBusy) return;

        lastResult = string.Empty;
        lastResultIsProblem = false;

        queue.Enqueue("確認回收視窗還開著", () =>
        {
            if (!RecycleWindowOpen)
            {
                Finish("回收視窗已經關掉了", problem: true);
                return null;
            }

            if (ConfirmWindowOpen)
            {
                Finish("已經有一個回收確認框開著，請先把它處理掉", problem: true);
                return null;
            }

            return true;
        });

        queue.Enqueue("送出回收", () =>
        {
            // 🔴 每一步重新解析 addon：上一步到這一步之間視窗可能已經被關掉。
            var addon = UiHelper.GetAddon(RecycleAddon);
            if (!UiHelper.IsReady(addon))
            {
                Finish("回收視窗已經關掉了", problem: true);
                return null;
            }

            LogRecycleWindowState(addon, "送出回收前");

            // ⚠️ 這組值移植自上游，台服未經離線驗證。送錯的失敗形式是「什麼都沒發生」——
            //    真正的回收只可能發生在 ShopCardDialog 上，而那個對話框本模組完全不碰。
            UiHelper.FireCallback(addon, true, 0, 0, 0);
            return true;
        });

        queue.EnqueueWait(WaitConfirmStep, () => ConfirmWindowOpen, 5_000);

        queue.Enqueue("交還給你", () =>
        {
            Finish(null, problem: false);
            return true;
        });
    }

    private void Finish(string? reason, bool problem)
    {
        lastResultIsProblem = problem;

        if (reason == null)
        {
            lastResult = "已叫出遊戲的回收確認框，請自己確認數量並按下「回收」";
            Svc.Log.Information(
                $"[{InternalName}] 已送出回收並看到「{ConfirmAddon}」。"
                + "接下來由使用者自己決定數量與按下確認——本模組不碰那個對話框。");
            return;
        }

        lastResult = reason;
        Svc.Log.Information($"[{InternalName}] 中止：{reason}。（沒有任何卡被回收。）");
    }

    private void OnStepTimeout(string step)
    {
        lastResultIsProblem = true;

        if (step == WaitConfirmStep)
        {
            lastResult = "遊戲沒有跳出回收確認框——這個操作在台服可能不成立，記錄裡有現場資料";
            Svc.Log.Information(
                $"[{InternalName}] 送出回收後 5 秒內沒有出現「{ConfirmAddon}」。"
                + "這代表送出的呼叫參數在台服對不上（那組參數移植自上游、沒有離線驗證過），"
                + "或是目前根本沒有可回收的卡。**沒有任何卡被回收。**"
                + "下面這行是逾時當下的視窗狀態，回報時請一起附上：");

            LogRecycleWindowState(UiHelper.GetAddon(RecycleAddon), "逾時當下");
            return;
        }

        lastResult = $"逾時中止於「{step}」";
        Svc.Log.Information($"[{InternalName}] 步驟逾時，整輪中止：{step}。（沒有任何卡被回收。）");
    }

    /// <summary>
    /// 把回收視窗的 <c>AtkValues</c> 傾印到記錄。
    /// </summary>
    /// <remarks>
    /// 📌 一律 <c>Information</c> 級：使用者跑 LogLevel 2，<c>Debug</c> 收不到，
    /// 而這是唯一能把「台服的值到底長什麼樣」從實機帶回來的管道
    /// —— 沒有它，「送出去沒反應」永遠只能用猜的。
    /// <para>🔴 <c>AtkValues</c> 指標與 <c>AtkValuesCount</c> 各自判界，字串另外判 null。</para>
    /// </remarks>
    private void LogRecycleWindowState(AtkUnitBase* addon, string when)
    {
        if (addon == null)
        {
            Svc.Log.Information($"[{InternalName}] {when}：取不到「{RecycleAddon}」。");
            return;
        }

        var count = addon->AtkValuesCount;
        var values = addon->AtkValues;
        if (values == null || count == 0)
        {
            Svc.Log.Information($"[{InternalName}] {when}：「{RecycleAddon}」沒有 AtkValues（count={count}）。");
            return;
        }

        var limit = Math.Min((int)count, MaxDumpValues);
        var builder = new StringBuilder();
        var emitted = 0;

        for (var i = 0; i < limit; i++)
        {
            var value = values[i];
            if (value.Type == ValueType.Undefined) continue;

            if (builder.Length > 0) builder.Append(" | ");
            builder.Append(i).Append(':').Append(DescribeValue(value));
            emitted++;

            // 一行塞太多會難讀，切段輸出。
            if (emitted % 16 != 0) continue;

            Svc.Log.Information($"[{InternalName}] {when} AtkValues：{builder}");
            builder.Clear();
        }

        if (builder.Length > 0)
            Svc.Log.Information($"[{InternalName}] {when} AtkValues：{builder}");

        Svc.Log.Information(
            $"[{InternalName}] {when}：「{RecycleAddon}」共 {count} 個 AtkValue（傾印了前 {limit} 個裡有值的部分）。");
    }

    private static string DescribeValue(AtkValue value)
    {
        switch (value.Type)
        {
            case ValueType.Bool:
                return $"bool={value.Byte}";
            case ValueType.Int:
                return $"int={value.Int}";
            case ValueType.Int64:
                return $"i64={value.Int64}";
            case ValueType.UInt:
                return $"uint={value.UInt}";
            case ValueType.UInt64:
                return $"u64={value.UInt64}";
            case ValueType.Float:
                return $"f={value.Float}";
            case ValueType.String:
            case ValueType.String8:
            case ValueType.ManagedString:
            {
                var ptr = value.String.Value;
                if (ptr == null) return "str=(null)";

                var text = MemoryHelper.ReadSeStringNullTerminated((nint)ptr).TextValue;
                return text.Length > 24 ? $"str=\"{text[..24]}…\"" : $"str=\"{text}\"";
            }

            default:
                return value.Type.ToString();
        }
    }

    #endregion

    #region 設定 UI

    public override void DrawConfig()
    {
        var title = recycleTitle.Length > 0 ? recycleTitle : "幻卡回收";

        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
        ImGui.TextDisabled(
            $"列出背包（主四袋）裡「幻卡列表已經收錄、所以是多的」九宮幻卡，以及各自回收可得多少金碟幣。"
            + $"在遊戲的「{title}」視窗開著時，可以按一下幫你送出一張。");
        ImGui.PopTextWrapPos();

        ImGui.TextColored(new Vector4(1f, 0.75f, 0.4f, 1f),
            "遊戲自己的回收確認框永遠由你按——本模組不自動確認、也不會連續回收。");

        ImGui.Spacing();
        ImGui.Separator();

        DrawAlbumState();

        ImGui.Spacing();
        ImGui.Separator();

        DrawCardList();

        ImGui.Spacing();
        ImGui.Separator();

        DrawRecycleControls(title);

        ImGui.Spacing();
        ImGui.Separator();

        DrawOptions();
    }

    private void DrawAlbumState()
    {
        var recordedWord = recordedLabel.Length > 0 ? recordedLabel : "已收錄";

        if (album == AlbumState.Verified)
        {
            ImGui.TextUnformatted($"幻卡列表：{recordedWord} {albumPopCount} 張");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(
                    "這個數字是從遊戲的收錄位元遮罩自己算出來的，而且已經和遊戲回報的收錄總數對上"
                    + $"（{albumPopCount} vs {albumReportedCount}）。\n"
                    + "兩者一致才代表本外掛讀的是正確的欄位，下面那份清單的「已收錄／未收錄」才可信。");
            return;
        }

        ImGui.TextColored(new Vector4(1f, 0.65f, 0.25f, 1f),
            "幻卡列表收錄狀態未知——下面每一張都畫「?」");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                $"自我校驗沒過：位元遮罩算出 {albumPopCount} 張、遊戲回報 {albumReportedCount} 張。\n"
                + "剛登入還沒讀到資料時會短暫是這個狀態，過幾秒再看。\n"
                + "一直是這樣的話，代表台服這一版的結構位置和外掛用的對不上——"
                + "這時候硬猜會給你一份看起來很篤定、實際上是亂的清單，所以刻意標成「不知道」。\n"
                + "遊戲自己的「幻卡回收」視窗上有一欄就是收錄狀態，那份永遠是對的。");
    }

    private void DrawCardList()
    {
        var verified = album == AlbumState.Verified;
        var showAll = Config.ShowUnrecorded;

        var recyclable = 0;
        var recyclableValue = 0;
        var unrecorded = 0;
        foreach (var card in bagCards)
        {
            if (card.Recorded)
            {
                recyclable += card.Quantity;
                recyclableValue += card.Quantity * card.MgpEach;
            }
            else
            {
                unrecorded += card.Quantity;
            }
        }

        if (verified)
        {
            ImGui.TextUnformatted(
                $"背包裡可回收的幻卡：{recyclable} 張，合計約 {recyclableValue:N0} 金碟幣"
                + (unrecorded > 0 ? $"（另有 {unrecorded} 張還沒收錄，不會列在下面除非你打開下面的開關）" : string.Empty));
        }
        else
        {
            ImGui.TextUnformatted($"背包裡的幻卡：{bagCards.Count} 種（可不可以回收：不知道）");
        }

        // 鞍袋那一行：不知道就要寫「不知道」，不能畫成 0。
        if (saddleBagCards < 0)
        {
            ImGui.TextDisabled("鞍袋：未載入，裡面有沒有幻卡不知道（在遊戲裡開一次鞍袋就會顯示）");
        }
        else if (saddleBagCards > 0)
        {
            ImGui.TextDisabled($"鞍袋：另有 {saddleBagCards} 張幻卡（回收視窗看不到鞍袋，要先拿出來）");
        }

        using var child = ImRaii.Child("TriadRecycleList", new Vector2(-1f, 200f), true);
        if (!child) return;

        if (bagCards.Count == 0)
        {
            ImGui.TextDisabled("背包（主四袋）裡沒有九宮幻卡。");
            return;
        }

        var drawn = 0;
        foreach (var card in bagCards)
        {
            if (verified && !showAll && !card.Recorded) continue;

            drawn++;

            // 🔑 狀態欄刻意用文字不用符號：Dalamud 的字型不保證有 ✔ 這類字，
            //    缺字時畫出來是一個看不懂的方框，而「看不懂」與「未收錄」在畫面上分不出來。
            if (!verified)
            {
                ImGui.TextColored(new Vector4(0.68f, 0.68f, 0.68f, 1f), "[ ? ]");
            }
            else if (card.Recorded)
            {
                ImGui.TextUnformatted("[可回收]");
            }
            else
            {
                ImGui.TextColored(new Vector4(1f, 0.65f, 0.25f, 1f), "[未收錄]");
            }

            ImGui.SameLine();
            ImGui.TextUnformatted(
                $"{card.Name} x{card.Quantity}"
                + (card.MgpEach > 0 ? $"　·　每張 {card.MgpEach:N0} 金碟幣" : "　·　不能回收"));

            if (!ImGui.IsItemHovered()) continue;

            var state = !verified
                ? "收錄狀態不知道（自我校驗沒過）"
                : card.Recorded
                    ? "幻卡列表已經收錄過，所以這幾張是多的，可以回收"
                    : "幻卡列表還沒收錄——**先用掉它**，回收掉就沒有了";

            ImGui.SetTooltip(
                $"{state}\n"
                + $"卡片編號 {card.CardId}、道具編號 {card.ItemId}\n"
                + $"位置：{card.Container}#{card.Slot}\n"
                + (card.MgpEach > 0
                    ? $"整疊回收約 {card.Quantity * card.MgpEach:N0} 金碟幣"
                    : "這張卡的回收價是 0，遊戲不收"));
        }

        if (drawn == 0)
            ImGui.TextDisabled("沒有可回收的幻卡（背包裡的都還沒收錄）。");
    }

    private void DrawRecycleControls(string title)
    {
        var windowOpen = RecycleWindowOpen;
        var confirmOpen = ConfirmWindowOpen;
        var busy = queue.IsBusy;

        // 🔑 這顆按鈕刻意**不**受上面那個「收錄狀態自我校驗」的結果影響。
        //    它送出的是「回收視窗上目前選定的那一張」——選哪一張是遊戲決定的，
        //    跟本模組算出來的清單無關；而真正的把關是遊戲自己的確認框（會寫明卡名與金碟幣）。
        //    校驗沒過時把它鎖住，只會讓一顆本來就安全的按鈕永久壞掉，並不會多擋掉任何風險。
        if (!windowOpen)
        {
            ImGui.TextDisabled($"「{title}」視窗沒有開著。請在遊戲裡找幻卡回收的 NPC 對話後再回來。");
        }
        else
        {
            var remainingText = TryReadRemaining(out var remaining) ? remaining.ToString() : "?";
            ImGui.TextUnformatted($"「{title}」視窗開著；遊戲回報還可回收 {remainingText} 張");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(
                    "「?」代表讀不到那個數字（台服的欄位排列沒有離線驗證過），不是 0。\n"
                    + "這個數字只拿來顯示，不會影響按鈕能不能按——真正說了算的是遊戲自己的確認框。");
        }

        using (ImRaii.Disabled(busy || !windowOpen || confirmOpen))
        {
            if (ImGui.Button("送出一張回收"))
                StartOneRecycle();
        }

        // ⚠️ 停用中的項目預設不回報 hover，要 AllowWhenDisabled 才問得到——
        //    而「為什麼這顆按不下去」正是按鈕停用時最需要看到的說明。
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(
                "對回收視窗上目前選定的那一張送出回收，然後停下來。\n"
                + "接著跳出來的是遊戲自己的確認框（可以選數量），**由你自己按下「回收」才會真的回收**。\n"
                + "本模組不會替你按，也不會自己回收下一張——要下一張請再按一次這顆按鈕。\n"
                + "\n選哪一張是遊戲決定的，跟上面那份清單無關；上面的清單是給你「出門前先看看值不值得跑一趟」用的。\n"
                + (confirmOpen ? "\n目前已經有一個確認框開著，先把它處理掉。" : string.Empty));

        ImGui.SameLine();
        using (ImRaii.Disabled(!busy))
        {
            if (ImGui.Button("停止"))
            {
                queue.Abort();
                lastResult = "已手動停止";
                lastResultIsProblem = false;
                Svc.Log.Information($"[{InternalName}] 使用者手動停止。（沒有任何卡被回收。）");
            }
        }

        ImGui.SameLine();
        using (ImRaii.Disabled(!windowOpen))
        {
            if (ImGui.Button("記錄目前視窗狀態"))
            {
                LogRecycleWindowState(UiHelper.GetAddon(RecycleAddon), "手動傾印");
                lastResult = "已把回收視窗的現況寫進記錄";
                lastResultIsProblem = false;
            }
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(
                "把回收視窗現在的內部值寫進 Dalamud 記錄（Information 級）。\n"
                + "「按了沒反應」要回報時附上這段，才有辦法知道台服的欄位排列是什麼樣子。");

        if (busy)
        {
            ImGui.TextDisabled($"執行中：{queue.CurrentStep}");
        }
        else if (lastResult.Length > 0)
        {
            if (lastResultIsProblem)
                ImGui.TextColored(new Vector4(1f, 0.65f, 0.25f, 1f), lastResult);
            else
                ImGui.TextDisabled(lastResult);
        }

        DrawYesAlreadyNotice();
    }

    /// <summary>
    /// YesAlready 提醒。
    /// </summary>
    /// <remarks>
    /// ⚠️ 只提醒，<b>不去暫停它、也不改它的設定</b>——那是使用者自己的選擇。
    /// 但它真的有一個會把這個對話框自動按掉的開關（Bothers 分頁的 <c>ShopCardDialog</c>，預設關），
    /// 而那正好會讓本模組刻意保留的人工確認整個消失，所以不能不講。
    /// <para>📌 IPC 只查得到「YesAlready 這個外掛整體開著沒」，查不到那個個別開關。</para>
    /// </remarks>
    private void DrawYesAlreadyNotice()
    {
        if (Throttle.Pass(YesAlreadyThrottleKey, 3_000))
            yesAlreadyActive = YesAlreadyIpc.QueryActive();

        if (yesAlreadyActive != true) return;

        ImGui.TextDisabled("偵測到 YesAlready 正在運作");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "YesAlready 的 Bothers 分頁裡有一項「ShopCardDialog」（預設是關的），"
                + "勾起來之後它會把回收數量拉到上限並自動按下確認。\n"
                + "本模組不會去動它，但那等於這裡刻意保留的那層人工確認消失了——"
                + "如果你想自己決定每次回收幾張，請確認那一項沒有勾。");
    }

    private void DrawOptions()
    {
        var showAll = Config.ShowUnrecorded;
        if (ImGui.Checkbox("連還沒收錄的幻卡一起列出來", ref showAll))
        {
            Config.ShowUnrecorded = showAll;
            Plugin.Instance.Config.Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("還沒收錄的卡應該拿去用掉而不是回收，所以預設不列。打開只是方便你看看手上有什麼。");

        ImGui.TextDisabled("回收得到的金碟幣超過持有上限的部分不會入帳（遊戲自己的規則）。");
    }

    #endregion
}

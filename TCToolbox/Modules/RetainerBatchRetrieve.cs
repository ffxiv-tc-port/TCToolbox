using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 僱員：批次取回 —— 把目前開著的僱員道具欄整批取回背包，可選一份 AllaganTools 清單當白名單。
/// </summary>
/// <remarks>
/// 🔴 <b>純手動觸發</b>：開著模組但不去按按鈕，遊戲行為完全不變。沒有任何事件驅動的接手鏈，
/// 也不會叫 AutoRetainer 走去傳喚鈴——本模組<b>只在使用者自己已經開著僱員道具欄時</b>才做事。
/// 🔴🔴 <b>送出不等於成功，本模組不拿回傳值當成果。</b>
/// 提供端只送指令、刻意不等結果；而台服對這類指令的拒絕是<b>完全靜默</b>的
/// （不受理時那一格不會有任何變化，也不會有訊息）。所以每送一次就<b>盯著僱員容器裡
/// 那一款的存量有沒有真的下降</b>，只有下降了才計入「已確認」。逾時沒下降的一律
/// 記成「未確認」並跳過那一款，<b>絕不</b>混進成功數字裡。
/// </remarks>
public sealed unsafe class RetainerBatchRetrieve : TcModule
{
    public override string InternalName => "RetainerBatchRetrieve";

    public override string DisplayName => "僱員：批次取回";

    public override string Description =>
        "手動按鈕：把目前開著的僱員道具欄整批取回背包，省下逐格按修飾鍵右鍵。" +
        "可以選一份 AllaganTools 清單只取符合的道具。需要 AutoRetainer 提供取回端點；" +
        "一次一款、每一件都確認真的到手，背包不夠時會停下來說停在哪。不會自動執行。";

    public override ModuleCategory Category => ModuleCategory.Inventory;

    /// <inheritdoc/>
    /// <remarks>開著不按按鈕＝遊戲行為完全不變，所以是手動觸發。</remarks>
    public override bool IsManualTrigger => true;

    public override bool HasConfigUI => true;

    /// <summary>僱員的七個道具頁。順序就是取回的順序。</summary>
    private static readonly InventoryType[] RetainerPages =
    [
        InventoryType.RetainerPage1, InventoryType.RetainerPage2, InventoryType.RetainerPage3,
        InventoryType.RetainerPage4, InventoryType.RetainerPage5, InventoryType.RetainerPage6,
        InventoryType.RetainerPage7,
    ];

    private static readonly InventoryType[] PlayerBags =
    [
        InventoryType.Inventory1, InventoryType.Inventory2,
        InventoryType.Inventory3, InventoryType.Inventory4,
    ];

    /// <summary>僱員道具欄的兩個 addon 名（大視窗／小視窗）。</summary>
    private static readonly string[] RetainerInventoryAddons = ["InventoryRetainer", "InventoryRetainerLarge"];

    /// <summary>送出一次取回之後，等僱員存量下降的時間上限（毫秒）。</summary>
    /// <remarks>
    /// ⚠️ 逾時<b>不是</b>整輪失敗，而是「這一款當成沒取到、跳過」。提供端自己的在途追蹤是 10 秒，
    /// 這裡刻意比它短一點：對方 10 秒後才會重新提供同一格，我們沒必要陪著等滿。
    /// </remarks>
    private const int ConfirmTimeoutMs = 6_000;

    /// <summary>連續幾次「讀不到僱員容器」就放棄整輪。</summary>
    /// <remarks>📌 這是<b>常態</b>而不是暫態（幾乎都是視窗根本沒開），等下去買不到東西。</remarks>
    private const int MaxUnavailableStreak = 12;

    /// <summary>同一款連續拿到「指令還在飛」幾次就跳過它。</summary>
    private const int MaxInFlightStreak = 40;

    /// <summary>單輪最多送幾次指令（失控保險絲，不是業務規則）。</summary>
    /// <remarks>📌 七頁各 25 格＋水晶頁，正常情況遠遠碰不到。</remarks>
    private const int MaxCommandsPerRun = 400;

    /// <summary>單輪時間上限（毫秒）。</summary>
    private const int MaxRunMs = 10 * 60 * 1_000;

    private RetainerBatchRetrieveConfig Config => Plugin.Instance.Config.RetainerBatchRetrieve;

    // ── 本輪狀態 ─────────────────────────────────────────────────────────────
    // 🔴 只存數值，不存 InventoryItem*／InventoryContainer*：每一步之間都會過一個
    //    framework tick，原生指標跨幀持有是艦隊紅線。要用的時候當場重查。

    /// <summary>這一輪要處理的道具編號，照僱員格子的順序。同一款只會出現一次。</summary>
    private readonly List<uint> plan = [];

    private int planIndex;
    private bool running;

    /// <summary>本輪開始時開著的是哪一位僱員。換人＝快照作廢，整輪停止。</summary>
    private ulong runRetainerId;

    private long runStartTick;
    private long lastCommandTick;

    private int commandsFired;
    private int commandedQuantity;

    /// <summary>正在等落地的那一款；0＝沒有在等。</summary>
    private uint pendingItemId;
    private int pendingQtyBefore;
    private long pendingSince;

    /// <summary>已<b>確認</b>離開僱員的件數（僱員存量真的下降了才算）。</summary>
    private int confirmedQuantity;
    private int confirmedCommands;

    /// <summary>送出後在期限內沒看到存量下降的次數。⚠️ 這不是成功。</summary>
    private int unconfirmedCommands;

    private int skippedUnique;
    private int skippedOther;
    private int unavailableStreak;
    private int inFlightStreak;

    private string lastSummary = string.Empty;

    // ── 篩選器快取 ───────────────────────────────────────────────────────────
    // 🔴 AllaganTools.GetFilterItems 在對方那側會跑一次 RefreshList（整份清單重算），
    //    所以<b>絕不</b>每幀（甚至每秒）呼叫：只在「換了清單」「按重新整理」「開始取回」
    //    這三個時刻各叫一次，其餘時間用這裡的快取。

    private readonly Dictionary<string, string> filterChoices = [];
    private bool filterChoicesKnown;
    private string filterChoicesReason = string.Empty;

    private HashSet<uint>? filterItems;
    private string filterItemsFor = string.Empty;
    private string filterItemsReason = string.Empty;
    private long filterItemsFetchedAt;

    /// <summary>面板上的預估款數。⚠️ 算不出來時是 <c>null</c>（畫成「？」），不是 0。</summary>
    private int? previewCount;

    private string previewReason = string.Empty;

    /// <summary>上次算出來的背包空格數，給繪製路徑讀。</summary>
    private int cachedFreeSlots;

    // ── 繪製執行緒 → framework 執行緒的請求 ──────────────────────────────────
    // 🔴 ImGui 的 Draw 是在 Dalamud 的 swapchain Present 掛鉤裡跑的
    //    （InterfaceManager.Display），不保證就是 framework 執行緒。
    //    所以按鈕一律只「登記一個請求」，真正的動作留到下一個 framework tick 做。
    //    ⚠️ 讀原生記憶體（背包／僱員容器）也一起搬過去了，理由相同。

    private bool startRequested;
    private bool stopRequested;

    /// <summary>要重新讀哪一份清單；null＝沒有請求。</summary>
    private string? filterRefreshRequest;

    private bool filterChoicesRequested;

    /// <summary>面板最後一次被畫出來的時間；只有「最近有人在看」才值得重算預估。</summary>
    private long previewWantedAt;

    // ── 生命週期 ─────────────────────────────────────────────────────────────

    protected override void OnEnable()
    {
        ResetRun();
        Svc.Framework.Update += OnFrameworkUpdate;
    }

    protected override void OnDisable()
    {
        Svc.Framework.Update -= OnFrameworkUpdate;
        ResetRun();
        lastSummary = string.Empty;
        previewCount = null;
        previewReason = string.Empty;
        filterChoices.Clear();
        filterChoicesKnown = false;
        filterItems = null;
        filterItemsFor = string.Empty;
    }

    private void ResetRun()
    {
        // 🔴 請求旗標一定要跟著清。使用者按下「開始取回」之後、framework tick 還沒接手之前
        //    就把模組關掉的話，旗標會留到下次啟用——那會變成「沒有人按按鈕卻自己開跑」，
        //    直接違反本模組「純手動觸發」的契約。
        startRequested = false;
        stopRequested = false;
        filterRefreshRequest = null;

        running = false;
        plan.Clear();
        planIndex = 0;
        runRetainerId = 0;
        runStartTick = 0;
        lastCommandTick = 0;
        commandsFired = 0;
        commandedQuantity = 0;
        pendingItemId = 0;
        pendingQtyBefore = 0;
        pendingSince = 0;
        confirmedQuantity = 0;
        confirmedCommands = 0;
        unconfirmedCommands = 0;
        skippedUnique = 0;
        skippedOther = 0;
        unavailableStreak = 0;
        inFlightStreak = 0;
    }

    /// <summary>
    /// 每幀推進一步。
    /// </summary>
    /// <remarks>
    /// 🔴 整段包 <c>try</c> 是因為 <c>RetainerManager.Instance()</c> 這類
    /// <c>[StaticAddress]</c> 產生器方法<b>不會回 null</b>——特徵碼解析失敗時擲的是
    /// <c>InvalidOperationException</c>。要防的是「擲例外」而不是「回 null」。
    /// </remarks>
    private void OnFrameworkUpdate(IFramework framework)
    {
        try
        {
            // 停止優先於一切：使用者按下去就是要它停。
            if (stopRequested)
            {
                stopRequested = false;
                startRequested = false;
                if (running) FinishRun("已由使用者停止");
                return;
            }

            if (filterChoicesRequested)
            {
                filterChoicesRequested = false;
                RefreshFilterChoices();
            }

            if (filterRefreshRequest is { } wanted)
            {
                filterRefreshRequest = null;
                RefreshFilterItems(wanted);
                Throttle.Reset("RetainerBatchRetrieve-Preview");
            }

            if (startRequested)
            {
                startRequested = false;
                if (!running) StartRun();
                return;
            }

            if (running)
            {
                Step();
                return;
            }

            // 只有面板最近真的被畫出來過才重算預估——收起來就不必每秒掃容器。
            if (Environment.TickCount64 - previewWantedAt < 2_000 &&
                Throttle.Pass("RetainerBatchRetrieve-Preview", 1_000))
            {
                RefreshPreview();
                cachedFreeSlots = GetPlayerFreeSlots();
            }
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, $"[{InternalName}] 取回流程擲出例外，已停止本輪。");
            FinishRun("流程發生未預期的錯誤，已停止");
        }
    }

    // ── 取回流程 ─────────────────────────────────────────────────────────────

    private void Step()
    {
        var now = Environment.TickCount64;

        if (commandsFired >= MaxCommandsPerRun)
        {
            FinishRun($"達到單輪指令上限（{MaxCommandsPerRun} 次），已停止");
            return;
        }

        if (runStartTick != 0 && now - runStartTick > MaxRunMs)
        {
            FinishRun("達到單輪時間上限，已停止");
            return;
        }

        if (GetBlockedReason() is { } blocked)
        {
            FinishRun($"已停止：{blocked}");
            return;
        }

        // 🔴 換僱員＝本輪的 plan 與 pending 全部作廢（那是<b>上一位</b>身上的格子）。
        //    提供端自己也會在換人時清掉在途追蹤，但它不知道我們的 plan。
        var retainerId = RetainerManager.Instance()->LastSelectedRetainerId;
        if (retainerId != runRetainerId)
        {
            FinishRun("僱員已切換，已停止（清單是針對上一位僱員算的）");
            return;
        }

        // ① 還在等上一次取回落地？
        if (pendingItemId != 0)
        {
            WaitForPending(now);
            return;
        }

        // ② 節流。提供端把節奏交給呼叫端決定，所以間隔是我們自己的責任。
        if (now - lastCommandTick < Math.Max(50, Config.StepIntervalMs)) return;

        // ③ 清單跑完了。
        if (planIndex >= plan.Count)
        {
            FinishRun("完成");
            return;
        }

        FireNext(now);
    }

    /// <summary>
    /// 盯著僱員身上那一款的存量有沒有真的下降。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>這就是「送出不等於成功」的那道防線。</b>台服的拒絕是完全靜默的：不受理時
    /// 格子不會變、也不會有訊息。所以唯一可信的成功訊號就是「存量真的少了」。
    /// 📌 存量下降之後<b>停在同一款</b>不前進——同一款可能有好幾疊，下一次呼叫會打到下一疊，
    /// 直到提供端回 0（確定沒有了）才換下一款。
    /// </remarks>
    private void WaitForPending(long now)
    {
        var qty = GetRetainerQuantity(pendingItemId, Config.IncludeCrystals);

        if (qty >= 0 && qty < pendingQtyBefore)
        {
            confirmedQuantity += pendingQtyBefore - qty;
            confirmedCommands++;
            pendingItemId = 0;
            return;
        }

        if (now - pendingSince <= ConfirmTimeoutMs) return;

        unconfirmedCommands++;
        Svc.Log.Information(
            $"[{InternalName}] 道具 {pendingItemId} 送出取回後 {ConfirmTimeoutMs}ms 內僱員存量沒有變化" +
            $"（送出前 {pendingQtyBefore}、現在 {(qty < 0 ? "讀不到" : qty.ToString())}），" +
            "當成伺服器沒有受理，跳過這一款。這一件不會計入已取回件數。");

        pendingItemId = 0;
        AdvanceItem();
    }

    private void FireNext(long now)
    {
        var itemId = plan[planIndex];

        // 送出前先讀一次，這是等一下判斷「有沒有真的少」的基準。
        // ⚠️ 讀不到（-1）就不要送：沒有基準就無法確認結果，送出去只會變成一件永遠說不清的事。
        var before = GetRetainerQuantity(itemId, Config.IncludeCrystals);
        if (before < 0)
        {
            unavailableStreak++;
            if (unavailableStreak >= MaxUnavailableStreak)
            {
                FinishRun("讀不到僱員道具欄（視窗沒開好或還在載入），已停止");
            }

            lastCommandTick = now;
            return;
        }

        if (before == 0)
        {
            // 本地讀得到、而且確定沒有了（多半是剛剛那幾疊已經拿完）。換下一款。
            // ⚠️ 這裡容器是<讀得到>的，所以連續計數要歸零——不歸零的話「幾次讀不到」與
            //    「幾款剛好拿完」會累加在同一個計數器上，一輪正常的取回跑到後面就會被
            //    誤判成「讀不到僱員道具欄」而整輪停掉。
            unavailableStreak = 0;
            AdvanceItem();
            return;
        }

        // hqOnly 一律 false：這是「整批取回」，優質與普通品都要。
        var ok = AutoRetainerIpc.TryRetrieveSlotById(itemId, false, Config.IncludeCrystals, out var result);
        lastCommandTick = now;

        if (!ok)
        {
            FinishRun("AutoRetainer 不再回應取回端點，已停止");
            return;
        }

        if (result > 0)
        {
            unavailableStreak = 0;
            inFlightStreak = 0;
            commandsFired++;
            commandedQuantity += result;
            pendingItemId = itemId;
            pendingQtyBefore = before;
            pendingSince = now;
            return;
        }

        switch (result)
        {
            case AutoRetainerIpc.RetrieveResultNotPresent:
                // 確定沒有了（不是「讀不到」）。
                unavailableStreak = 0;
                AdvanceItem();
                break;

            case AutoRetainerIpc.RetrieveResultCommandInFlight:
                // 東西還在，只是上一次的指令還沒被觀察到落地。等，不要重送。
                unavailableStreak = 0;
                inFlightStreak++;
                if (inFlightStreak >= MaxInFlightStreak)
                {
                    Svc.Log.Information(
                        $"[{InternalName}] 道具 {itemId} 連續 {inFlightStreak} 次回報「指令還在飛」，跳過這一款。");
                    skippedOther++;
                    AdvanceItem();
                }

                break;

            case AutoRetainerIpc.RetrieveResultRetainerUnavailable:
                unavailableStreak++;
                if (unavailableStreak >= MaxUnavailableStreak)
                {
                    FinishRun("AutoRetainer 一直回報讀不到僱員道具欄（視窗沒開好或還在載入），已停止");
                }

                break;

            case AutoRetainerIpc.RetrieveResultInventoryFull:
                // 🔴 停在看得見的地方：講清楚停在第幾款，也講清楚這是誰的門檻。
                FinishRun(
                    $"背包空間不足，停在第 {planIndex + 1}/{plan.Count} 款" +
                    $"（AutoRetainer 的背包保留格數擋下的，不一定是背包真的一格不剩；" +
                    "可以到 AutoRetainer 設定裡調那個保留值，或先清一些背包再按一次）");
                break;

            case AutoRetainerIpc.RetrieveResultBlockedUnique:
                // 遊戲對「身上已經有一個的獨占道具」永遠靜默拒絕，重試沒有意義。
                skippedUnique++;
                unavailableStreak = 0;
                AdvanceItem();
                break;

            case AutoRetainerIpc.RetrieveResultInCrystals:
                skippedOther++;
                unavailableStreak = 0;
                AdvanceItem();
                break;

            default:
                Svc.Log.Information(
                    $"[{InternalName}] 道具 {itemId} 拿到無法辨識的回傳值 {result}，跳過這一款。");
                skippedOther++;
                AdvanceItem();
                break;
        }
    }

    private void AdvanceItem()
    {
        planIndex++;
        inFlightStreak = 0;
    }

    // ── 讀容器 ───────────────────────────────────────────────────────────────

    /// <summary>只在真的能走訪時才回傳容器，否則回 <c>null</c>。</summary>
    /// <remarks>
    /// 🔴 判的是 <c>Items</c> 不是 <c>GetInventorySlot()</c> 的回傳值：<c>Items</c> 為 null 而
    /// <c>Size &gt; 0</c> 時，<c>GetInventorySlot(i)</c> 回的是「null＋偏移」這種<b>非 null 的假指標</b>，
    /// 判空一定通過，解參考就是攔不到的 AVE（corrupted-state exception，try/catch 無效）。
    /// ⚠️ <b>刻意不檢查 <c>IsLoaded</c></b>，雖然本外掛別的地方有檢查：提供端
    /// （AutoRetainer <c>RetainerRetrieve.TryGetReadableContainer</c>）只判 <c>Items == null</c>，
    /// 這裡多加一層就會出現「它讀得到、我讀不到」的容器，那會讓下面的存量基準與它不一致，
    /// 表現成「明明取回了卻判成未確認」。<b>要跟提供端看到同一份資料</b>。
    /// </remarks>
    private static InventoryContainer* TryGetReadableContainer(InventoryManager* manager, InventoryType type)
    {
        if (manager == null) return null;

        var container = manager->GetInventoryContainer(type);
        if (container == null || container->Items == null) return null;
        return container;
    }

    /// <summary>
    /// 目前開著的僱員身上有幾個 <paramref name="itemId"/>。
    /// </summary>
    /// <returns>
    /// 總數；<b>任何一個</b>容器走不了就回 <c>-1</c>。
    /// ⚠️ -1 是「不知道」不是「沒有」——刻意不回傳部分加總，因為一個偏低的數字會讓呼叫端
    /// 提早收工並把東西留在僱員身上。
    /// </returns>
    private static int GetRetainerQuantity(uint itemId, bool includeCrystals)
    {
        if (itemId == 0) return 0;

        var manager = InventoryManager.Instance();
        if (manager == null) return -1;

        var total = 0;

        foreach (var type in RetainerPages)
        {
            var container = TryGetReadableContainer(manager, type);
            if (container == null) return -1;

            for (var i = 0; i < container->Size; i++)
            {
                var item = container->GetInventorySlot(i);
                if (item == null) return -1;
                if (item->ItemId == itemId) total += item->Quantity;
            }
        }

        if (includeCrystals)
        {
            var crystals = TryGetReadableContainer(manager, InventoryType.RetainerCrystals);
            if (crystals == null) return -1;

            for (var i = 0; i < crystals->Size; i++)
            {
                var item = crystals->GetInventorySlot(i);
                if (item == null) return -1;
                if (item->ItemId == itemId) total += item->Quantity;
            }
        }

        return total;
    }

    /// <summary>
    /// 僱員身上目前有哪些款，照格子順序、去重。
    /// </summary>
    /// <param name="allow">白名單；<c>null</c>＝不篩選。</param>
    /// <param name="slots">符合條件的<b>格子</b>數（不是款數），純粹拿來寫報告。</param>
    /// <returns>讀不到任何一個容器時回 <c>null</c>——這與「僱員是空的」是不同的答案。</returns>
    private static List<uint>? BuildPlan(HashSet<uint>? allow, bool includeCrystals, out int slots)
    {
        slots = 0;

        var manager = InventoryManager.Instance();
        if (manager == null) return null;

        var types = new List<InventoryType>(RetainerPages);
        if (includeCrystals) types.Add(InventoryType.RetainerCrystals);

        var seen = new HashSet<uint>();
        var result = new List<uint>();

        foreach (var type in types)
        {
            var container = TryGetReadableContainer(manager, type);
            if (container == null) return null;

            for (var i = 0; i < container->Size; i++)
            {
                var item = container->GetInventorySlot(i);
                if (item == null) return null;

                // 🔴 先確認這一格真的有東西，再拿 ItemId 去用。
                if (item->ItemId == 0 || item->Quantity <= 0) continue;

                var id = item->ItemId;
                if (allow != null && !allow.Contains(id)) continue;

                slots++;
                if (seen.Add(id)) result.Add(id);
            }
        }

        return result;
    }

    /// <summary>玩家背包還有幾個空格。讀不到的容器一律跳過（只會少算，不會多算）。</summary>
    private static int GetPlayerFreeSlots()
    {
        var manager = InventoryManager.Instance();
        if (manager == null) return 0;

        var free = 0;
        foreach (var type in PlayerBags)
        {
            var container = TryGetReadableContainer(manager, type);
            if (container == null) continue;

            for (var i = 0; i < container->Size; i++)
            {
                var item = container->GetInventorySlot(i);
                if (item == null) continue;
                if (item->ItemId == 0) free++;
            }
        }

        return free;
    }

    /// <summary>現在不能取回的原因；<c>null</c>＝可以。</summary>
    private static string? GetBlockedReason()
    {
        if (Svc.Objects.LocalPlayer == null) return "目前不在遊戲中。";

        if (Svc.Condition[ConditionFlag.BetweenAreas] || Svc.Condition[ConditionFlag.BetweenAreas51])
            return "正在讀取地圖。";

        if (Svc.Condition[ConditionFlag.WatchingCutscene] ||
            Svc.Condition[ConditionFlag.OccupiedInCutSceneEvent])
            return "正在播放過場動畫。";

        // 🔴 這是提供端唯一的前置條件：那兩個 addon 有沒有開好。誰開的都算數。
        if (!UiHelper.IsAddonReady(RetainerInventoryAddons[0]) &&
            !UiHelper.IsAddonReady(RetainerInventoryAddons[1]))
        {
            return "請先開啟僱員的道具欄視窗（傳喚鈴→選僱員→道具管理）。";
        }

        return null;
    }

    // ── 篩選器 ───────────────────────────────────────────────────────────────

    /// <summary>目前實際生效的清單鍵／名稱；空字串＝不篩選（整批取回）。</summary>
    private string EffectiveFilter() =>
        Config.UseCustomFilterName ? Config.CustomFilterName.Trim() : Config.FilterKey;

    /// <summary>重新問 AllaganTools 有哪些搜尋清單。</summary>
    /// <remarks>
    /// ⚠️ 「問得到但一個都沒有」與「問不到」是兩件事，畫面上要分開講：前者是使用者還沒建
    /// 搜尋清單（很常見），後者是 AllaganTools 沒裝。兩種都畫成空下拉會讓人一直找不到原因。
    /// </remarks>
    private void RefreshFilterChoices()
    {
        filterChoices.Clear();
        filterChoicesKnown = AllaganToolsIpc.TryGetSearchFilters(out var found, out filterChoicesReason);

        if (!filterChoicesKnown) return;

        foreach (var pair in found)
        {
            filterChoices[pair.Key] = pair.Value;
        }
    }

    /// <summary>重新問某份清單現在包含哪些道具。</summary>
    /// <remarks>🔴 對方那側會整份重算，只在使用者換清單／按重新整理／開始取回時叫。</remarks>
    private bool RefreshFilterItems(string keyOrName)
    {
        var ok = AllaganToolsIpc.TryGetFilterItems(keyOrName, out var items, out filterItemsReason);

        filterItems = ok ? items : null;
        filterItemsFor = ok ? keyOrName : string.Empty;
        filterItemsFetchedAt = ok ? Environment.TickCount64 : 0;
        return ok;
    }

    // ── 開始／結束 ───────────────────────────────────────────────────────────

    /// <remarks>
    /// 🔴 這是從 ImGui 的繪製路徑上被叫的，<b>不得擲例外</b>——整段包 try。
    /// </remarks>
    private void StartRun()
    {
        try
        {
            ResetRun();

            if (GetBlockedReason() is { } blocked)
            {
                lastSummary = blocked;
                return;
            }

            if (!AutoRetainerIpc.SupportsItemRetrieve(out _, out var unsupported))
            {
                lastSummary = unsupported;
                return;
            }

            // 🔴 AutoRetainer 正在跑它自己的任務鏈時不要插隊：它可能正要關視窗或換僱員，
            //    而那會讓我們算好的 plan 指到別人身上。
            //    ⚠️ 它逾時會回報「忙」（fail-safe 的那一邊），所以這裡偶爾會多擋一次——
            //    代價只是使用者再按一下，方向是對的。
            if (AutoRetainerIpc.TryGetIsBusy(out var busy) && busy)
            {
                lastSummary = "AutoRetainer 正在跑自己的作業，等它結束再按一次。";
                return;
            }

            HashSet<uint>? allow = null;
            var filter = EffectiveFilter();
            if (filter.Length > 0)
            {
                if (!RefreshFilterItems(filter))
                {
                    lastSummary = filterItemsReason;
                    return;
                }

                allow = filterItems;
                if (allow is { Count: 0 })
                {
                    lastSummary = "這份清單目前一件道具都不包含，沒有東西可以取回。";
                    return;
                }
            }

            var built = BuildPlan(allow, Config.IncludeCrystals, out var slots);
            if (built == null)
            {
                lastSummary = "讀不到僱員道具欄，請確認視窗已經開好再按一次。";
                return;
            }

            if (built.Count == 0)
            {
                lastSummary = filter.Length > 0
                    ? "僱員身上沒有符合這份清單的道具。"
                    : "僱員道具欄是空的。";
                return;
            }

            plan.AddRange(built);
            runRetainerId = RetainerManager.Instance()->LastSelectedRetainerId;
            runStartTick = Environment.TickCount64;
            lastSummary = string.Empty;
            running = true;

            // 讓提供端忘掉上一輪還記著的在途格子，被伺服器拒絕的那些才會立刻重新被提供，
            // 而不是等它自己的 10 秒逾時。
            AutoRetainerIpc.ResetRetrieveTracking();

            Svc.Log.Information(
                $"[{InternalName}] 開始批次取回：{built.Count} 款、{slots} 格，" +
                $"範圍＝{(filter.Length > 0 ? DescribeFilter(filter) : "全部")}，" +
                $"水晶頁{(Config.IncludeCrystals ? "納入" : "不納入")}，" +
                $"間隔 {Config.StepIntervalMs}ms，背包空格 {GetPlayerFreeSlots()}。");
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, $"[{InternalName}] 準備批次取回時擲出例外。");
            ResetRun();
            lastSummary = "準備取回時發生錯誤，詳見記錄。";
        }
    }

    private void FinishRun(string reason)
    {
        var elapsed = runStartTick == 0 ? 0 : Environment.TickCount64 - runStartTick;
        var stoppedAt = plan.Count == 0 ? string.Empty : $"（進度 {Math.Min(planIndex, plan.Count)}/{plan.Count} 款）";

        // 🔴 報告裡「已確認取回」與「送出的指令」是<b>兩個</b>數字，刻意不合併：
        //    送出不等於成功，把它們併成一個就等於在說謊。
        var summary =
            $"{reason}{stoppedAt}：已確認取回 {confirmedQuantity} 件／{confirmedCommands} 次，" +
            $"送出 {commandsFired} 次（涵蓋 {commandedQuantity} 件）" +
            (unconfirmedCommands > 0 ? $"，其中 {unconfirmedCommands} 次沒能確認落地" : string.Empty) +
            (skippedUnique > 0 ? $"，跳過 {skippedUnique} 款獨占道具（身上已有一個，遊戲永遠拒絕）" : string.Empty) +
            (skippedOther > 0 ? $"，另跳過 {skippedOther} 款" : string.Empty) +
            $"，耗時 {elapsed / 1000.0:0.0} 秒。";

        lastSummary = summary;
        Svc.Log.Information($"[{InternalName}] {summary}");

        if (Config.NotifyOnFinish)
            Svc.Chat.Print($"[TC Toolbox] 僱員批次取回：{summary}");

        ResetRun();
        Throttle.Reset("RetainerBatchRetrieve-Preview");
    }

    private string DescribeFilter(string keyOrName) =>
        filterChoices.TryGetValue(keyOrName, out var name) ? $"清單「{name}」" : $"清單「{keyOrName}」";

    // ── 預估 ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// 重算面板上的「這樣按下去會取回幾款」。
    /// </summary>
    /// <remarks>
    /// 📌 這裡<b>只走本地容器</b>，不打任何 IPC——用的是上次快取下來的清單白名單。
    /// 所以每秒重算是便宜的，而真正貴的那一步（問 AllaganTools 要清單內容）留給
    /// 「換清單／按重新整理／開始取回」。
    /// </remarks>
    private void RefreshPreview()
    {
        if (GetBlockedReason() is { } blocked)
        {
            previewCount = null;
            previewReason = blocked;
            return;
        }

        HashSet<uint>? allow = null;
        var filter = EffectiveFilter();
        if (filter.Length > 0)
        {
            if (filterItems == null || filterItemsFor != filter)
            {
                previewCount = null;
                previewReason = filterItemsReason.Length > 0
                    ? filterItemsReason
                    : "還沒讀取這份清單的內容，按一下「重新整理清單」。";
                return;
            }

            allow = filterItems;
        }

        var built = BuildPlan(allow, Config.IncludeCrystals, out _);
        if (built == null)
        {
            previewCount = null;
            previewReason = "讀不到僱員道具欄";
            return;
        }

        previewReason = string.Empty;
        previewCount = built.Count;
    }

    // ── UI ───────────────────────────────────────────────────────────────────

    public override void DrawConfig()
    {
        // 🔴 這裡只登記「有人在看」，真正的掃描在 framework tick 上做（見 OnFrameworkUpdate）。
        previewWantedAt = Environment.TickCount64;

        DrawScopePicker();
        ImGui.Separator();
        DrawRunControls();
        ImGui.Separator();
        DrawOptions();
    }

    private void DrawScopePicker()
    {
        if (!filterChoicesKnown && Throttle.Pass("RetainerBatchRetrieve-Filters", 10_000))
            filterChoicesRequested = true;

        var useCustom = Config.UseCustomFilterName;

        var label = useCustom
            ? (Config.CustomFilterName.Trim().Length > 0 ? $"自訂：{Config.CustomFilterName.Trim()}" : "自訂：（還沒填名稱）")
            : Config.FilterKey.Length == 0
                ? "全部（不篩選）"
                : filterChoices.TryGetValue(Config.FilterKey, out var known)
                    ? known
                    : $"{Config.FilterKey}（這份清單目前不在 AllaganTools 的搜尋清單裡）";

        ImGui.SetNextItemWidth(320f);
        if (ImGui.BeginCombo("取回範圍##retainerBatchRetrieveScope", label))
        {
            if (ImGui.Selectable("全部（不篩選）", !useCustom && Config.FilterKey.Length == 0))
            {
                Config.FilterKey = string.Empty;
                Config.UseCustomFilterName = false;
                Plugin.Instance.Config.Save();
                Throttle.Reset("RetainerBatchRetrieve-Preview");
            }

            foreach (var pair in filterChoices)
            {
                if (!ImGui.Selectable(pair.Value, !useCustom && Config.FilterKey == pair.Key)) continue;

                Config.FilterKey = pair.Key;
                Config.UseCustomFilterName = false;
                Plugin.Instance.Config.Save();
                filterRefreshRequest = pair.Key;
            }

            if (ImGui.Selectable("自訂：直接輸入清單名稱…", useCustom))
            {
                Config.UseCustomFilterName = true;
                Plugin.Instance.Config.Save();
                Throttle.Reset("RetainerBatchRetrieve-Preview");
            }

            ImGui.EndCombo();
        }

        // ⚠️ 「問得到但一個都沒有」與「問不到」要分開講，否則使用者看到空下拉會不知道要修什麼。
        if (!filterChoicesKnown)
        {
            ImGui.TextDisabled($"清單下拉是空的：{filterChoicesReason}沒有 AllaganTools 也能用，只是不能篩選。");
        }
        else if (filterChoices.Count == 0)
        {
            ImGui.TextDisabled(
                "AllaganTools 有裝，但你目前沒有任何「搜尋清單」。" +
                "排序清單與遊戲道具清單不會出現在這個下拉裡（對方的端點只列搜尋清單），" +
                "那兩種請改用下面的「自訂」直接打名稱。");
        }

        if (Config.UseCustomFilterName)
        {
            var custom = Config.CustomFilterName;
            ImGui.SetNextItemWidth(320f);
            if (ImGui.InputTextWithHint("##retainerBatchRetrieveCustom", "AllaganTools 清單名稱或 key…", ref custom, 128))
            {
                Config.CustomFilterName = custom;
                Plugin.Instance.Config.Save();
            }

            ImGui.SameLine();
            if (ImGui.Button("讀取這份清單##retainerBatchRetrieveLoadCustom"))
                filterRefreshRequest = Config.CustomFilterName.Trim();

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    "AllaganTools 那支端點收「清單 key」也收「清單名稱」，\n" +
                    "所以排序清單／遊戲道具清單也可以用——只是它們不會出現在上面的下拉裡。");
            }
        }

        if (EffectiveFilter().Length > 0)
        {
            if (ImGui.Button("重新整理清單##retainerBatchRetrieveRefresh"))
                filterRefreshRequest = EffectiveFilter();

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    "重新問 AllaganTools 這份清單現在包含哪些道具。\n" +
                    "刻意不自動重問：對方每問一次都要把整份清單重算一遍。");
            }

            ImGui.SameLine();
            if (filterItems != null && filterItemsFor == EffectiveFilter())
            {
                var age = (Environment.TickCount64 - filterItemsFetchedAt) / 1000;
                ImGui.TextDisabled($"清單含 {filterItems.Count} 款道具（{age} 秒前讀取）");
            }
            else
            {
                ImGui.TextDisabled($"？（{(filterItemsReason.Length > 0 ? filterItemsReason : "還沒讀取")}）");
            }
        }
    }

    private void DrawRunControls()
    {
        // 「不知道」要在列上看得見：算不出來時畫「？」與原因，不畫 0。
        ImGui.AlignTextToFramePadding();
        if (previewCount is { } count)
            ImGui.TextUnformatted($"可取回：{count} 款");
        else
            ImGui.TextDisabled($"可取回：？（{previewReason}）");

        ImGui.SameLine();
        var freeSlots = cachedFreeSlots;
        if (freeSlots == 0)
            ImGui.TextColored(new Vector4(1f, 0.5f, 0.4f, 1f), "　背包空格：0");
        else
            ImGui.TextDisabled($"　背包空格：{freeSlots}");

        ImGui.Spacing();

        if (running)
        {
            if (ImGui.Button("停止取回##retainerBatchRetrieveStop"))
                stopRequested = true;

            ImGui.SameLine();
            ImGui.TextColored(
                new Vector4(1f, 0.85f, 0.35f, 1f),
                $"進行中 {Math.Min(planIndex + 1, plan.Count)}/{plan.Count} 款" +
                $"（已確認取回 {confirmedQuantity} 件）");
            return;
        }

        if (startRequested)
        {
            ImGui.TextColored(new Vector4(1f, 0.85f, 0.35f, 1f), "準備中…");
            return;
        }

        var blocked = previewCount is null or 0;
        if (blocked) ImGui.BeginDisabled();

        var clicked = ImGui.Button("開始取回##retainerBatchRetrieveStart");

        if (blocked) ImGui.EndDisabled();

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(previewCount is null
                ? previewReason
                : previewCount == 0
                    ? "目前沒有符合條件的道具。"
                    : "把目前開著的僱員道具欄裡符合範圍的道具整批取回背包。\n" +
                      "一次一款、每一件都會確認真的離開僱員才算數，過程中可以隨時停止。\n" +
                      "需要 AutoRetainer 提供取回端點；背包不夠時會停下來並說停在第幾款。");
        }

        if (clicked && !blocked) startRequested = true;

        if (lastSummary.Length > 0)
            ImGui.TextDisabled($"上次結果：{lastSummary}");
    }

    private void DrawOptions()
    {
        var interval = Config.StepIntervalMs;
        ImGui.SetNextItemWidth(240f);
        if (ImGui.SliderInt("每次取回之間的間隔（毫秒）##retainerBatchRetrieveInterval", ref interval, 100, 2_000))
        {
            Config.StepIntervalMs = interval;
            Plugin.Instance.Config.Save();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "AutoRetainer 那支端點刻意不等結果，節奏是呼叫端的責任。\n" +
                "伺服器大約每 0.13 秒消化一格，所以調得比那還快也不會更快，只會多送幾次白工。\n" +
                "覺得有東西沒被取到就調長一點。");
        }

        var includeCrystals = Config.IncludeCrystals;
        if (ImGui.Checkbox("連水晶頁一起取回##retainerBatchRetrieveCrystals", ref includeCrystals))
        {
            Config.IncludeCrystals = includeCrystals;
            Plugin.Instance.Config.Save();
            Throttle.Reset("RetainerBatchRetrieve-Preview");
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "預設關閉。水晶是遊戲已知在「寄放」時一定會跳數量對話框的類別，\n" +
                "「取回」會不會也跳，在這個客戶端上還沒有驗證過——\n" +
                "真的跳了而沒有人回答，這一輪會停在那裡等到逾時。");
        }

        var notify = Config.NotifyOnFinish;
        if (ImGui.Checkbox("結束時在聊天欄報告##retainerBatchRetrieveNotify", ref notify))
        {
            Config.NotifyOnFinish = notify;
            Plugin.Instance.Config.Save();
        }

        ImGui.TextColored(
            new Vector4(1f, 0.8f, 0.35f, 1f),
            "⚠ 需要先自己開好僱員的道具欄視窗，而且要裝 AutoRetainer（本模組不會叫它走去傳喚鈴）。");
    }
}

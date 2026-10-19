using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 貨幣上限警示：神典石／票券類貨幣快滿時在伺服器資訊列亮警告，避免溢出浪費。
/// </summary>
/// <remarks>
/// 上限優先走 <c>CurrencyManager.GetItemMaxCount</c>，<b>不</b>拿 <c>Item.StackSize</c> 當上限
/// ——兩者對神典石剛好都是 2000，但那是巧合：StackSize 是「一格能疊多少」，
/// 而貨幣的持有上限是另一套規則，會隨版本調整。用錯的那個平常看不出來，改版時才靜默失準。
/// 例外：<c>CurrencyManager</c> 不認得的神典石與 <see cref="ExtraWatchedItemIds"/>（回上限 0）
/// 改數貨幣欄＋背包，上限取 <c>StackSize</c>（見 <see cref="TryCountHeld"/>）。
/// ⚠️ <b>「不知道」要在列上看得見。</b>讀不到 <c>CurrencyManager</c> 時顯示的是 <c>?</c> 而不是 0——
/// 把未知畫成 0 等於告訴使用者「沒有任何貨幣快滿」，那是會害人溢出的誤導。
/// <para>📌 純顯示模組：不花貨幣、不買東西、不碰任何遊戲狀態。</para>
/// </remarks>
public sealed unsafe class CurrencyCapAlert : TcModule
{
    public override string InternalName => "CurrencyCapAlert";

    public override string DisplayName => "貨幣上限警示";

    public override string Description =>
        "神典石、票據等貨幣接近持有上限時，在伺服器資訊列顯示警告，滑鼠移上顯示是哪幾種、還差多少。" +
        "另可監看數理神典石的每週取得上限。純顯示，不會自動花費任何貨幣。";

    public override ModuleCategory Category => ModuleCategory.Inventory;

    public override bool HasConfigUI => true;

    /// <summary>資訊列圖示：警告三角。</summary>
    private const BitmapFontIcon DtrIcon = BitmapFontIcon.Warning;

    /// <summary>tooltip 裡最多列出幾種（超過的以「…等 N 種」帶過）。</summary>
    private const int TooltipPreviewCount = 8;

    private CurrencyCapAlertConfig Config => Plugin.Instance.Config.CurrencyCapAlert;

    private IDtrBarEntry? dtrEntry;

    /// <summary>一種被監看的貨幣在這一刻的樣子。</summary>
    /// <remarks>🔴 只有數值與字串，不保存任何原生指標。</remarks>
    private readonly record struct CurrencySnapshot(
        uint ItemId, string Name, uint Count, uint MaxCount, bool IsWeekly)
    {
        /// <summary>已持有的比例（0～1）。<c>MaxCount</c> 為 0 時回 0，呼叫端不會走到這裡。</summary>
        public float Ratio => MaxCount == 0 ? 0f : (float)Count / MaxCount;

        public uint Remaining => MaxCount > Count ? MaxCount - Count : 0;
    }

    /// <summary>目前超過門檻的貨幣。</summary>
    private readonly List<CurrencySnapshot> nearCap = [];

    /// <summary>最近一次掃描抓到的<b>全部</b>貨幣（含沒超過門檻的），只給設定畫面看。</summary>
    private readonly List<CurrencySnapshot> allCurrencies = [];

    /// <summary>
    /// 已經為了「跨過門檻」印過聊天訊息的貨幣。
    /// </summary>
    /// <remarks>
    /// 邊緣觸發的記憶體：掉回門檻以下就從這裡移除，於是下次再超過時會再提醒一次。
    /// 沒有這個集合的話，每次輪詢都會印一則——那正是我們刻意避開的洗版。
    /// <para>週上限用 <see cref="WeeklyPseudoItemId"/> 這個假鍵記在同一個集合裡。</para>
    /// </remarks>
    private readonly HashSet<uint> announced = [];

    /// <summary>
    /// 週上限在 <see cref="announced"/> 裡用的假鍵。
    /// </summary>
    /// <remarks>
    /// 📌 用 0 是安全的：0 不是任何有效的貨幣道具編號（Item 表 row 0 是有效列但無名稱，
    /// 而且不會出現在任何一個貨幣 bucket 裡），所以不可能跟真的貨幣撞鍵。
    /// </remarks>
    private const uint WeeklyPseudoItemId = 0;

    /// <summary>資料讀不讀得到；false＝顯示「?」而不是 0。</summary>
    private bool dataReadable;

    /// <summary>
    /// 特殊貨幣 bucket 的鍵到底是「特殊編號」還是「道具編號」——只在第一次成功解讀時寫一行記錄。
    /// </summary>
    /// <remarks>
    /// ⚠️ <c>SpecialItemBucket</c> 宣告是 <c>StdMap&lt;uint, SpecialCurrencyItem&gt;</c>，
    /// 而值本身又帶一個 <c>SpecialId</c> 欄位，所以「鍵是什麼」從型別上看不出來，
    /// CS 也沒有寫。與其猜一個然後靜默拿到空清單，這裡<b>兩條路都試</b>（見 <see cref="TryResolveSpecialCurrency"/>）。
    /// </remarks>
    private bool loggedSpecialBucketShape;

    /// <summary>另一個角色在 AutoRetainer 紀錄裡的軍票。只有數值與字串。</summary>
    private readonly record struct OtherSeals(
        ulong Cid, string Name, string World, AutoRetainerIpc.SealsState State, uint Seals, int Max, DateTime? UpdatedAt)
    {
        public bool HasName => Name.Length > 0 && World.Length > 0;

        public string FullName => HasName ? $"{Name}@{World}" : $"（讀不到名稱，CID {Cid:X}）";

        public float Ratio => Max > 0 ? (float)Seals / Max : 0f;
    }

    private const string OtherSealsThrottleKey = "CurrencyCapAlert-OtherSeals";

    /// <summary>其他角色的數字只在登入那個角色時才會變，一分鐘問一次就夠。</summary>
    private const int OtherSealsPollMs = 60_000;

    /// <summary>資訊列說明裡最多列出幾位其他角色。</summary>
    private const int OtherSealsPreviewCount = 12;

    private static readonly System.Numerics.Vector4 OtherSealsNearCapColor = new(1f, 0.5f, 0.4f, 1f);
    private static readonly System.Numerics.Vector4 OtherSealsNormalColor = new(0.8f, 0.8f, 0.8f, 1f);

    /// <summary>其他角色的軍票快照。Update 整份換掉，繪製只讀參考。</summary>
    private IReadOnlyList<OtherSeals> otherSeals = [];

    /// <summary>AutoRetainer 打不打得通；null＝還沒問過。</summary>
    private bool? otherSealsReachable;

    /// <summary>快照是以哪個角色的身分問的：換了角色就立刻重問，把自己排除掉。</summary>
    private ulong otherSealsAskedAs;

    /// <summary>設定畫面按下「切換」後，留給下一次 Update 執行的目標（IPC 不在繪製時打）。</summary>
    private string? pendingSwitch;

    /// <summary>道具編號 → Item 表名稱文字（查不到＝空字串）。表在遊戲執行中不會變，每次輪詢重新解析只是白配置。</summary>
    private readonly Dictionary<uint, string> itemTexts = [];

    /// <summary>每次輪詢重用的集合（只在 framework 執行緒用）。</summary>
    private readonly HashSet<uint> seenScratch = [];
    private readonly HashSet<uint> nearCapIdsScratch = [];

    private enum DtrContent { Stale, Unreadable, List }

    /// <summary>資訊列上的文字是照哪一份輸入組的；<see cref="DtrContent.Stale"/>＝下次一定重組。</summary>
    private DtrContent dtrContent;
    private readonly List<CurrencySnapshot> dtrNearCap = [];
    private int dtrOthersNear;

    protected override void OnEnable()
    {
        nearCap.Clear();
        allCurrencies.Clear();
        announced.Clear();
        dataReadable = false;
        loggedSpecialBucketShape = false;
        loggedHeldPath.Clear();
        ResetOtherSeals();
        pendingSwitch = null;
        itemTexts.Clear();
        InvalidateDtr();

        dtrEntry = Svc.DtrBar.Get("TC Toolbox 貨幣上限");
        dtrEntry.Shown = false;
        dtrEntry.Text = new SeString(new IconPayload(DtrIcon), new TextPayload("?"));
        dtrEntry.Tooltip = "TC Toolbox — 貨幣上限警示";
        dtrEntry.OnClick = _ => Plugin.Instance.ToggleMainWindow();

        Svc.Framework.Update += OnUpdate;
    }

    protected override void OnDisable()
    {
        Svc.Framework.Update -= OnUpdate;

        dtrEntry?.Remove();
        dtrEntry = null;

        nearCap.Clear();
        allCurrencies.Clear();
        announced.Clear();
        dataReadable = false;
        ResetOtherSeals();
        pendingSwitch = null;
        itemTexts.Clear();
        InvalidateDtr();
    }

    private void OnUpdate(IFramework framework)
    {
        if (dtrEntry == null) return;

        if (pendingSwitch is { } target)
        {
            pendingSwitch = null;
            CharacterSwitch.Request(target, InternalName);
        }

        // 貨幣數量變化很慢（打完一場副本才動一次），兩秒一次綽綽有餘。
        if (!Throttle.Pass("CurrencyCapAlert-Poll", 2_000)) return;

        // 沒登入時整個藏起來：那不是「不知道」，是「這時候本來就沒有貨幣可談」。
        if (Svc.Objects.LocalPlayer == null)
        {
            dtrEntry.Shown = false;
            nearCap.Clear();
            allCurrencies.Clear();
            announced.Clear();
            dataReadable = false;
            InvalidateDtr();
            return;
        }

        Rescan();
        if (Config.ShowOtherCharacterSeals) PollOtherSeals();
        UpdateDtr();
        AnnounceCrossings();
    }

    private void ResetOtherSeals()
    {
        otherSeals = [];
        otherSealsReachable = null;
        otherSealsAskedAs = 0;
        Throttle.Reset(OtherSealsThrottleKey);
    }

    /// <summary>向 AutoRetainer 問其他角色的軍票，整份換掉 <see cref="otherSeals"/>。</summary>
    /// <remarks>每一位都是整份角色資料的 JSON 來回，所以只在這裡節流著做，繪製時只讀快照。</remarks>
    private void PollOtherSeals()
    {
        var localCid = Svc.PlayerState.ContentId;
        if (localCid != otherSealsAskedAs) Throttle.Reset(OtherSealsThrottleKey);
        if (!Throttle.Pass(OtherSealsThrottleKey, OtherSealsPollMs)) return;

        otherSealsAskedAs = localCid;

        if (!AutoRetainerIpc.IsAvailable())
        {
            otherSealsReachable = false;
            otherSeals = [];
            return;
        }

        var list = new List<OtherSeals>();
        foreach (var cid in AutoRetainerIpc.GetRegisteredCharacters())
        {
            if (cid == localCid) continue;

            if (!AutoRetainerIpc.TryGetCharacterInfo(cid, out var info))
            {
                // 一位打不通就是整條端點不能用（沒了或改了形狀），不顯示半份。
                otherSealsReachable = false;
                otherSeals = [];
                return;
            }

            list.Add(info == null
                ? new OtherSeals(cid, string.Empty, string.Empty, AutoRetainerIpc.SealsState.Unknown, 0, -1, null)
                : new OtherSeals(cid, info.Name ?? string.Empty, info.World ?? string.Empty,
                    info.Seals, info.GCSeals, info.GCSealsMax, info.GCSealsUpdatedAt));
        }

        otherSealsReachable = true;
        otherSeals = list;
    }

    private float ThresholdRatio() => Math.Clamp(Config.ThresholdPercent, 1, 100) / 100f;

    private int CountOtherSealsNearCap()
    {
        if (!Config.ShowOtherCharacterSeals) return 0;

        var threshold = ThresholdRatio();
        var count = 0;
        foreach (var s in otherSeals)
        {
            if (s.State == AutoRetainerIpc.SealsState.Known && s.Ratio >= threshold)
                count++;
        }

        return count;
    }

    /// <summary>資訊列說明裡「其他角色的軍票」那一段；選項關著或還沒問過時是空字串。</summary>
    private string BuildOtherSealsTooltip()
    {
        if (!Config.ShowOtherCharacterSeals || otherSealsReachable == null) return string.Empty;

        var sb = new StringBuilder("\n\n其他角色的軍票（AutoRetainer 的紀錄）：");
        if (otherSealsReachable == false)
            return sb.Append("\n讀不到 AutoRetainer（未安裝、未載入，或版本不相容）。").ToString();

        var snapshot = otherSeals;
        if (snapshot.Count == 0)
            return sb.Append("\nAutoRetainer 沒有登記其他角色。").ToString();

        // 快滿的排最前面，「不知道」排在已知之後但仍然列出來。
        var threshold = ThresholdRatio();
        var ordered = snapshot
            .OrderBy(s => s.State != AutoRetainerIpc.SealsState.Known ? (s.State == AutoRetainerIpc.SealsState.Unknown ? 2 : 3)
                : s.Ratio >= threshold ? 0 : 1)
            .ThenByDescending(s => s.Ratio)
            .ToList();

        var shown = Math.Min(ordered.Count, OtherSealsPreviewCount);
        for (var i = 0; i < shown; i++)
            sb.Append('\n').Append(DescribeOtherSeals(ordered[i], threshold));

        if (ordered.Count > shown)
            sb.Append($"\n…等共 {ordered.Count} 位");

        return sb.ToString();
    }

    private static string DescribeOtherSeals(in OtherSeals s, float threshold) => s.State switch
    {
        AutoRetainerIpc.SealsState.Known =>
            $"{s.FullName}：{s.Seals} / {s.Max}（{FormatAge(s.UpdatedAt)}）" + (s.Ratio >= threshold ? "　※快滿" : string.Empty),
        AutoRetainerIpc.SealsState.NotInGrandCompany => $"{s.FullName}：沒有加入大國防聯軍",
        _ => $"{s.FullName}：?（AutoRetainer 還沒記錄到）",
    };

    /// <summary>「剛剛／N 分鐘前／N 小時前／N 天前」。</summary>
    private static string FormatAge(DateTime? at)
    {
        if (at == null) return "?";

        var age = DateTime.UtcNow - at.Value.ToUniversalTime();
        if (age.TotalMinutes < 1) return "剛剛";
        if (age.TotalHours < 1) return $"{(int)age.TotalMinutes} 分鐘前";
        if (age.TotalDays < 1) return $"{(int)age.TotalHours} 小時前";
        return $"{(int)age.TotalDays} 天前";
    }

    /// <summary>重新掃描所有貨幣，填好 <see cref="allCurrencies"/> 與 <see cref="nearCap"/>。</summary>
    private void Rescan()
    {
        allCurrencies.Clear();
        nearCap.Clear();

        var currencyManager = CurrencyManager.Instance();
        if (currencyManager == null)
        {
            // 🔴 讀不到就是讀不到，不要退回 0。dataReadable=false 會讓資訊列顯示「?」。
            dataReadable = false;
            return;
        }

        dataReadable = true;

        // 用 HashSet 去重：同一種貨幣可能同時出現在神典石表與某個 bucket 裡。
        var seen = seenScratch;
        seen.Clear();

        CollectTomestones(currencyManager, seen);
        CollectSpecialCurrencies(currencyManager, seen);
        CollectPlainCurrencies(currencyManager, seen);

        foreach (var itemId in ExtraWatchedItemIds)
        {
            if (seen.Add(itemId)) AddHeldCurrency(currencyManager, itemId);
        }

        var threshold = Math.Clamp(Config.ThresholdPercent, 1, 100) / 100f;

        foreach (var currency in allCurrencies)
        {
            if (Config.IgnoredItemIds.Contains(currency.ItemId)) continue;
            if (currency.Ratio >= threshold)
                nearCap.Add(currency);
        }

        if (Config.WatchWeeklyTomestone && TryGetWeeklyTomestone(out var weekly))
        {
            allCurrencies.Add(weekly);
            if (!Config.IgnoredItemIds.Contains(WeeklyPseudoItemId) && weekly.Ratio >= threshold)
                nearCap.Add(weekly);
        }

        // 差最少的排前面——那是最急的。
        nearCap.Sort((a, b) => a.Remaining.CompareTo(b.Remaining));
    }

    /// <summary>
    /// 這一版還在用的神典石。
    /// </summary>
    /// <remarks>
    /// 判準是 <c>Tomestones</c> 欄非 0：歷代舊神典石那一欄都是 0。
    /// 用這個判準而不是寫死道具編號，改版換神典石時會自己跟上。
    /// </remarks>
    private void CollectTomestones(CurrencyManager* currencyManager, HashSet<uint> seen)
    {
        var sheet = Svc.Data.GetExcelSheet<TomestonesItem>();
        if (sheet == null) return;

        foreach (var row in sheet)
        {
            if (row.Tomestones.RowId == 0) continue;

            var itemId = row.Item.RowId;
            if (itemId == 0 || !seen.Add(itemId)) continue;

            AddHeldCurrency(currencyManager, itemId);
        }
    }

    /// <summary>十二城邦銀幣、十二城邦金幣：使用者點名要監看，資料表裡沒有能列舉它們的欄位。</summary>
    private static readonly uint[] ExtraWatchedItemIds = [45043, 45044];

    /// <summary>背包與貨幣欄：<see cref="TryCountHeld"/> 要數的容器。</summary>
    private static readonly InventoryType[] HeldContainers =
    [
        InventoryType.Currency,
        InventoryType.Inventory1, InventoryType.Inventory2,
        InventoryType.Inventory3, InventoryType.Inventory4,
    ];

    /// <summary><c>CurrencyManager</c> 認得就照它；回上限 0 時改數身上的道具。</summary>
    private void AddHeldCurrency(CurrencyManager* currencyManager, uint itemId)
    {
        if (currencyManager->GetItemMaxCount(itemId) != 0)
        {
            if (loggedHeldPath.Add(itemId))
                Svc.Log.Information($"[{InternalName}] 道具 {itemId} 由 CurrencyManager 讀取");
            AddCurrency(currencyManager, itemId);
            return;
        }

        if (!TryCountHeld(itemId, out var count, out var maxCount)) return;

        if (loggedHeldPath.Add(itemId))
            Svc.Log.Information($"[{InternalName}] 道具 {itemId} CurrencyManager 回上限 0，改數貨幣欄＋背包：{count}/{maxCount}");
        allCurrencies.Add(new CurrencySnapshot(itemId, ResolveItemName(itemId), count, maxCount, IsWeekly: false));
    }

    /// <summary>每種神典石／點名道具走哪條讀法，每次啟用只記一次（給使用者回報用）。</summary>
    private readonly HashSet<uint> loggedHeldPath = [];

    /// <summary>
    /// 數貨幣欄＋背包裡這件道具的總數，上限取 <c>Item.StackSize</c>（這類道具一種只佔一格）。
    /// </summary>
    /// <remarks>任何一個容器還沒載入就回 false＝不知道，不當成 0。</remarks>
    private static bool TryCountHeld(uint itemId, out uint count, out uint maxCount)
    {
        count = 0;
        maxCount = Svc.Data.GetExcelSheet<Item>().GetRowOrDefault(itemId)?.StackSize ?? 0;
        if (maxCount <= 1) return false;

        var manager = InventoryManager.Instance();
        if (manager == null) return false;

        foreach (var type in HeldContainers)
        {
            var container = manager->GetInventoryContainer(type);
            // 判 Items 不判 GetInventorySlot 的回傳：Items 為 null 時它回的是非 null 的假指標。
            if (container == null || !container->IsLoaded || container->Items == null) return false;

            for (var i = 0; i < container->Size; i++)
            {
                var item = container->GetInventorySlot(i);
                if (item != null && item->ItemId == itemId && item->Quantity > 0) count += (uint)item->Quantity;
            }
        }

        return true;
    }

    /// <summary>特殊貨幣（票據等）。</summary>
    private void CollectSpecialCurrencies(CurrencyManager* currencyManager, HashSet<uint> seen)
    {
        foreach (var pair in currencyManager->SpecialItemBucket)
        {
            // ⚠️ StdPair 的成員是 Item1／Item2（不是 Key／Value）——它是 std::pair 的移植，不是 KeyValuePair。
            if (!TryResolveSpecialCurrency(currencyManager, pair.Item1, pair.Item2.SpecialId, out var itemId))
                continue;

            if (!seen.Add(itemId)) continue;
            AddCurrency(currencyManager, itemId);
        }
    }

    /// <summary>
    /// 把 <c>SpecialItemBucket</c> 的一筆解析成道具編號。
    /// </summary>
    /// <remarks>
    /// 🔑 「鍵是特殊編號還是道具編號」CS 沒有寫，所以<b>兩條路都試，並且都要通過驗證</b>：
    /// 只有在 <c>Item</c> 表查得到、而且名稱非空的時候才算數
    /// （Item 表的 row 0 是有效列但名稱為空，所以光判 null 擋不住）。
    /// </remarks>
    private bool TryResolveSpecialCurrency(
        CurrencyManager* currencyManager, uint key, byte specialId, out uint itemId)
    {
        // (A) 值裡的 SpecialId → GetItemIdBySpecialId
        var viaSpecialId = currencyManager->GetItemIdBySpecialId(specialId);
        if (IsRealItem(viaSpecialId))
        {
            itemId = viaSpecialId;
            LogSpecialBucketShape("值的 SpecialId → GetItemIdBySpecialId");
            return true;
        }

        // (B) 鍵本身就是道具編號
        if (IsRealItem(key))
        {
            itemId = key;
            LogSpecialBucketShape("鍵本身就是道具編號");
            return true;
        }

        itemId = 0;
        return false;
    }

    private void LogSpecialBucketShape(string shape)
    {
        if (loggedSpecialBucketShape) return;
        loggedSpecialBucketShape = true;

        // 要使用者回報的診斷：這行是實機唯一能回答
        // 「SpecialItemBucket 的鍵是什麼」的證據。
        Svc.Log.Information($"[{InternalName}] SpecialItemBucket 解讀方式＝{shape}");
    }

    /// <summary>其他貨幣 bucket（部族貨幣／雙色寶石／戰利水晶…）。鍵就是道具編號。</summary>
    private void CollectPlainCurrencies(CurrencyManager* currencyManager, HashSet<uint> seen)
    {
        foreach (var pair in currencyManager->ItemBucket)
        {
            var itemId = pair.Item1;
            if (!IsRealItem(itemId) || !seen.Add(itemId)) continue;
            AddCurrency(currencyManager, itemId);
        }
    }

    /// <summary>把一種貨幣的現況記進 <see cref="allCurrencies"/>（上限為 0＝無上限，直接略過）。</summary>
    private void AddCurrency(CurrencyManager* currencyManager, uint itemId)
    {
        var maxCount = currencyManager->GetItemMaxCount(itemId);

        // 上限 0＝這種貨幣沒有持有上限（或遊戲還沒填好），沒有「快滿」可言。
        if (maxCount == 0) return;

        var count = currencyManager->GetItemCount(itemId);
        allCurrencies.Add(new CurrencySnapshot(itemId, ResolveItemName(itemId), count, maxCount, IsWeekly: false));
    }

    /// <summary>
    /// 數理神典石這一類「每週可取得量」的上限。
    /// </summary>
    /// <remarks>
    /// 📌 這與持有上限是<b>兩回事</b>：持有上限是背包裡最多放幾顆，週上限是這一週還能再賺幾顆。
    /// 兩個都會造成浪費，但原因不同，所以分開顯示。
    /// ⚠️ 週上限回 0 代表這一版沒有設週上限（或讀不到），這時候整個項目不顯示——
    /// 不要把它當成「已達上限」。
    /// </remarks>
    private static bool TryGetWeeklyTomestone(out CurrencySnapshot snapshot)
    {
        snapshot = default;

        var limit = InventoryManager.GetLimitedTomestoneWeeklyLimit();
        if (limit <= 0) return false;

        var manager = InventoryManager.Instance();
        if (manager == null) return false;

        var acquired = manager->GetWeeklyAcquiredTomestoneCount();
        if (acquired < 0) return false;

        snapshot = new CurrencySnapshot(
            WeeklyPseudoItemId, "本週神典石取得量", (uint)acquired, (uint)limit, IsWeekly: true);
        return true;
    }

    /// <summary>這個編號在 Item 表裡是不是一件真的、有名字的道具。</summary>
    /// <remarks>⚠️ row 0 是有效列但名稱為空，所以不能只判斷「查得到」。</remarks>
    private bool IsRealItem(uint itemId) => itemId != 0 && ItemText(itemId).Length > 0;

    private string ResolveItemName(uint itemId)
    {
        var name = ItemText(itemId);
        return name.Length == 0 ? $"#{itemId}" : name;
    }

    private string ItemText(uint itemId)
    {
        if (itemTexts.TryGetValue(itemId, out var text)) return text;

        var row = Svc.Data.GetExcelSheet<Item>().GetRowOrDefault(itemId);
        text = row?.Name.ExtractText() ?? string.Empty;
        itemTexts[itemId] = text;
        return text;
    }

    private void InvalidateDtr()
    {
        dtrContent = DtrContent.Stale;
        dtrNearCap.Clear();
    }

    /// <summary>資訊列現有的文字是不是就是照這份輸入組的（是＝不必重組）。</summary>
    /// <remarks>其他角色軍票那段含「N 分鐘前」，會隨時間變，選項開著時一律重組。</remarks>
    private bool DtrIsCurrent(DtrContent content, int othersNear)
    {
        if (Config.ShowOtherCharacterSeals || dtrContent != content || dtrOthersNear != othersNear) return false;
        if (dtrNearCap.Count != nearCap.Count) return false;
        for (var i = 0; i < nearCap.Count; i++)
        {
            if (dtrNearCap[i] != nearCap[i]) return false;
        }

        return true;
    }

    private void RememberDtr(DtrContent content, int othersNear)
    {
        dtrContent = content;
        dtrOthersNear = othersNear;
        dtrNearCap.Clear();
        dtrNearCap.AddRange(nearCap);
    }

    private void UpdateDtr()
    {
        if (dtrEntry == null) return;

        var othersNear = CountOtherSealsNearCap();

        // ⚠️ 讀不到資料時<b>要顯示</b>，而且要顯示「?」。
        // 藏起來會讓使用者以為「沒有貨幣快滿」，那正好是相反的意思。
        if (!dataReadable)
        {
            dtrEntry.Shown = true;
            if (DtrIsCurrent(DtrContent.Unreadable, othersNear)) return;
            dtrEntry.Text = new SeString(new IconPayload(DtrIcon), new TextPayload("?"));
            dtrEntry.Tooltip =
                "TC Toolbox — 貨幣上限警示\n讀不到貨幣資料，目前無法判斷有沒有快滿的貨幣。\n" +
                "（剛登入時會有短暫的這個狀態，屬正常。）" + BuildOtherSealsTooltip() +
                "\n\n左鍵／右鍵：開啟／關閉 TC Toolbox 設定";
            RememberDtr(DtrContent.Unreadable, othersNear);
            return;
        }

        if (nearCap.Count == 0 && othersNear == 0)
        {
            // 這是「已知沒事」，不是「不知道」——可以安心藏起來，不占資訊列版面。
            dtrEntry.Shown = false;
            InvalidateDtr();
            return;
        }

        dtrEntry.Shown = true;
        if (DtrIsCurrent(DtrContent.List, othersNear)) return;
        var label = othersNear == 0 ? nearCap.Count.ToString() : $"{nearCap.Count}+{othersNear}";
        dtrEntry.Text = new SeString(new IconPayload(DtrIcon), new TextPayload(label));

        var sb = new StringBuilder();
        sb.Append("TC Toolbox — 貨幣快接近上限\n");

        if (nearCap.Count == 0)
            sb.Append("\n目前角色沒有快滿的貨幣。");

        var shown = Math.Min(nearCap.Count, TooltipPreviewCount);
        for (var i = 0; i < shown; i++)
        {
            var c = nearCap[i];
            sb.Append($"\n{c.Name}：{c.Count} / {c.MaxCount}（還可再收 {c.Remaining}）");
            if (c.IsWeekly) sb.Append("　※每週取得量");
        }

        if (nearCap.Count > shown)
            sb.Append($"\n…等共 {nearCap.Count} 種");

        sb.Append(BuildOtherSealsTooltip());
        sb.Append("\n\n左鍵／右鍵：開啟／關閉 TC Toolbox 設定");
        dtrEntry.Tooltip = sb.ToString();
        RememberDtr(DtrContent.List, othersNear);
    }

    /// <summary>
    /// 邊緣觸發的聊天提醒：只在「這一次剛跨過門檻」時印一則。
    /// </summary>
    /// <remarks>
    /// 掉回門檻以下時會把記憶清掉，所以花掉再存滿還會再提醒一次；
    /// 但持續超標的期間<b>一則都不會多印</b>。
    /// </remarks>
    private void AnnounceCrossings()
    {
        if (!Config.NotifyInChat) return;

        var current = nearCapIdsScratch;
        current.Clear();
        foreach (var c in nearCap) current.Add(c.ItemId);

        foreach (var c in nearCap)
        {
            if (!announced.Add(c.ItemId)) continue;

            var what = c.IsWeekly ? "本週取得量" : "持有量";
            Svc.Chat.Print(
                $"[TC Toolbox] 「{c.Name}」{what}已達 {c.Count} / {c.MaxCount}，還可再收 {c.Remaining}。");
            Svc.Log.Information(
                $"[{InternalName}] 跨過門檻：{c.Name}（{c.ItemId}） {c.Count}/{c.MaxCount}");
        }

        // 掉回門檻以下的要忘掉，否則花掉之後再存滿就不會再提醒。
        announced.IntersectWith(current);
    }

    public override void DrawConfig()
    {
        var threshold = Config.ThresholdPercent;
        if (ImGui.SliderInt("警示門檻（%）##currencyCapThreshold", ref threshold, 50, 100))
        {
            Config.ThresholdPercent = threshold;
            Plugin.Instance.Config.Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("持有量達到上限的這個百分比時，就在伺服器資訊列亮警告。");

        var notify = Config.NotifyInChat;
        if (ImGui.Checkbox("跨過門檻時在聊天欄提醒一次##currencyCapNotify", ref notify))
        {
            Config.NotifyInChat = notify;
            Plugin.Instance.Config.Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("只在「剛跨過門檻」的那一次印一則，不會持續洗版。\n掉回門檻以下再超過時會再提醒一次。");

        var weekly = Config.WatchWeeklyTomestone;
        if (ImGui.Checkbox("同時監看神典石的每週取得上限##currencyCapWeekly", ref weekly))
        {
            Config.WatchWeeklyTomestone = weekly;
            Plugin.Instance.Config.Save();
        }

        var others = Config.ShowOtherCharacterSeals;
        if (ImGui.Checkbox("同時列出其他角色的軍票（讀 AutoRetainer 的紀錄）##currencyCapOtherSeals", ref others))
        {
            Config.ShowOtherCharacterSeals = others;
            Plugin.Instance.Config.Save();
            ResetOtherSeals();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "AutoRetainer 會在角色登入期間（例如登出時）記下軍票數量；這裡列出其他角色最後一次記下的值與時間。\n" +
                "從沒在 AutoRetainer 的記錄下登入過的角色顯示「?」，不會顯示成 0。\n" +
                "快滿的判準與上面的門檻相同；有的話資訊列會亮，數字寫成「目前角色+其他角色」。\n" +
                "需要安裝 AutoRetainer。只顯示，不會自己切換角色。");
        }

        ImGui.Separator();

        DrawCurrencyTable();

        if (Config.ShowOtherCharacterSeals)
            DrawOtherSeals();
    }

    private void DrawCurrencyTable()
    {
        if (!dataReadable)
        {
            ImGui.TextColored(new System.Numerics.Vector4(0.7f, 0.7f, 0.7f, 1f),
                "目前讀不到貨幣資料（未登入或遊戲尚未就緒）。");
            return;
        }

        if (allCurrencies.Count == 0)
        {
            ImGui.TextDisabled("尚未掃描到任何有持有上限的貨幣。");
            return;
        }

        ImGui.TextDisabled($"偵測到 {allCurrencies.Count} 種有上限的貨幣；取消勾選＝不要監看那一種。");

        if (!ImGui.BeginTable("##currencyCapList", 3,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.SizingStretchProp))
            return;

        ImGui.TableSetupColumn("監看", ImGuiTableColumnFlags.WidthFixed, 40f);
        ImGui.TableSetupColumn("貨幣", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("持有 / 上限", ImGuiTableColumnFlags.WidthFixed, 150f);
        ImGui.TableHeadersRow();

        foreach (var c in allCurrencies)
        {
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            var watched = !Config.IgnoredItemIds.Contains(c.ItemId);
            if (ImGui.Checkbox($"##watch{c.ItemId}", ref watched))
            {
                if (watched) Config.IgnoredItemIds.Remove(c.ItemId);
                else Config.IgnoredItemIds.Add(c.ItemId);
                Plugin.Instance.Config.Save();
            }

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(c.IsWeekly ? $"{c.Name}（每週）" : c.Name);

            ImGui.TableNextColumn();
            var ratio = c.Ratio;
            var color = ratio >= Math.Clamp(Config.ThresholdPercent, 1, 100) / 100f
                ? new System.Numerics.Vector4(1f, 0.5f, 0.4f, 1f)
                : new System.Numerics.Vector4(0.8f, 0.8f, 0.8f, 1f);
            ImGui.TextColored(color, $"{c.Count} / {c.MaxCount}");
        }

        ImGui.EndTable();
    }

    /// <summary>設定畫面上「其他角色的軍票」。只讀快照，不打 IPC（切換留給下一次 Update）。</summary>
    private void DrawOtherSeals()
    {
        ImGui.Separator();
        ImGui.TextUnformatted("其他角色的軍票（AutoRetainer 的紀錄）");

        var reachable = otherSealsReachable;
        if (reachable == null)
        {
            ImGui.TextDisabled("還沒向 AutoRetainer 詢問過（登入後幾秒內會更新）。");
            return;
        }

        if (reachable == false)
        {
            ImGui.TextDisabled("讀不到 AutoRetainer（未安裝、未載入，或版本不相容）。");
            return;
        }

        var snapshot = otherSeals;
        if (snapshot.Count == 0)
        {
            ImGui.TextDisabled("AutoRetainer 沒有登記其他角色。");
            return;
        }

        if (!ImGui.BeginTable("##currencyCapOtherSeals", 4,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.SizingStretchProp))
            return;

        ImGui.TableSetupColumn("角色", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("軍票 / 上限", ImGuiTableColumnFlags.WidthFixed, 130f);
        ImGui.TableSetupColumn("記錄時間", ImGuiTableColumnFlags.WidthFixed, 90f);
        ImGui.TableSetupColumn("##switch", ImGuiTableColumnFlags.WidthFixed, 50f);
        ImGui.TableHeadersRow();

        var threshold = ThresholdRatio();
        foreach (var s in snapshot)
        {
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(s.FullName);

            ImGui.TableNextColumn();
            switch (s.State)
            {
                case AutoRetainerIpc.SealsState.Known:
                    ImGui.TextColored(s.Ratio >= threshold ? OtherSealsNearCapColor : OtherSealsNormalColor,
                        $"{s.Seals} / {s.Max}");
                    break;

                case AutoRetainerIpc.SealsState.NotInGrandCompany:
                    ImGui.TextDisabled("—");
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("這個角色沒有加入大國防聯軍。");
                    break;

                default:
                    ImGui.TextDisabled("?");
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("AutoRetainer 還沒記錄到這個角色的軍票：\n用 AutoRetainer 登入一次該角色之後才會有。");
                    break;
            }

            ImGui.TableNextColumn();
            if (s.State != AutoRetainerIpc.SealsState.Unknown && s.UpdatedAt is { } at)
            {
                ImGui.TextDisabled(FormatAge(at));
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(at.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
            }
            else
            {
                ImGui.TextDisabled("?");
            }

            ImGui.TableNextColumn();
            if (s.HasName)
            {
                if (ImGui.SmallButton($"切換##currencyCapSwitch{s.Cid}"))
                    pendingSwitch = $"{s.Name}@{s.World}";

                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip(
                        $"請 AutoRetainer 切換到「{s.Name}@{s.World}」（會登出目前角色）。\n" +
                        "戰鬥中、副本中、事件中、不在原伺服器，或 AutoRetainer 正在忙時不會切換。");
                }
            }
        }

        ImGui.EndTable();
    }
}

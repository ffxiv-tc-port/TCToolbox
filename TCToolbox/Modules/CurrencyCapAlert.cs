using System;
using System.Collections.Generic;
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
/// <para>
/// 🔑 <b>零寫死道具編號。</b>要看的貨幣與它們各自的上限全部在執行期問出來：
/// <list type="bullet">
/// <item>神典石 → <c>TomestonesItem</c> 表（<c>Tomestones</c> 欄非 0＝這一版還在用的）。
/// 台服 7.20 實查為 4 種：詩學(1)／天道(2)／數理(3)／美學(4)，
/// 其餘 19 列是歷代舊神典石，<c>Tomestones</c> 欄為 0。</item>
/// <item>特殊貨幣（票據／centurio 印章等）→ <c>CurrencyManager.SpecialItemBucket</c>，
/// 逐筆用 <c>GetItemIdBySpecialId</c> 還原道具編號。</item>
/// <item>其他貨幣（部族貨幣／雙色寶石／戰利水晶…）→ <c>CurrencyManager.ItemBucket</c>。</item>
/// </list>
/// 上限一律走 <c>CurrencyManager.GetItemMaxCount</c>，<b>不</b>拿 <c>Item.StackSize</c> 當上限
/// ——兩者對神典石剛好都是 2000，但那是巧合：StackSize 是「一格能疊多少」，
/// 而貨幣的持有上限是另一套規則，會隨版本調整。用錯的那個平常看不出來，改版時才靜默失準。
/// </para>
/// <para>
/// 🔴 <b>為什麼走 DTR 而不是聊天訊息</b>：「快滿了」是一個<b>持續存在的狀態</b>，
/// 不是一次性事件——它從超過門檻起一直為真，直到玩家把貨幣花掉為止。
/// 聊天訊息是事件語意：印一次就捲走，沒看到就沒看到；要它可靠就得反覆印，那就變成洗版
/// （上游 CBT 掛在換區事件上，正是這個毛病）。
/// 依艦隊的 UI 判準，「<b>隨時掃視</b>」的資訊放列上、「起疑才查」的放 tooltip，
/// 所以：資訊列只放「幾種快滿了」，是哪幾種、差多少放 tooltip。
/// 聊天訊息保留為選項，但改成<b>邊緣觸發</b>（跨過門檻的那一次才印一則），不會重複洗。
/// </para>
/// <para>
/// ⚠️ <b>「不知道」要在列上看得見。</b>讀不到 <c>CurrencyManager</c> 時顯示的是 <c>?</c> 而不是 0——
/// 把未知畫成 0 等於告訴使用者「沒有任何貨幣快滿」，那是會害人溢出的誤導。
/// </para>
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
    /// CS 也沒有寫。與其猜一個然後靜默拿到空清單，這裡<b>兩條路都試</b>（見
    /// <see cref="TryResolveSpecialCurrency"/>），並把實際走通的那條寫進記錄，
    /// 讓實機 log 直接回答這個問題。
    /// </remarks>
    private bool loggedSpecialBucketShape;

    protected override void OnEnable()
    {
        nearCap.Clear();
        allCurrencies.Clear();
        announced.Clear();
        dataReadable = false;
        loggedSpecialBucketShape = false;

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
    }

    private void OnUpdate(IFramework framework)
    {
        if (dtrEntry == null) return;

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
            return;
        }

        Rescan();
        UpdateDtr();
        AnnounceCrossings();
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
        var seen = new HashSet<uint>();

        CollectTomestones(currencyManager, seen);
        CollectSpecialCurrencies(currencyManager, seen);
        CollectPlainCurrencies(currencyManager, seen);

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
    /// 判準是 <c>Tomestones</c> 欄非 0。台服 7.20 實查：全表 23 列，只有 4 列非 0
    /// （詩學／天道／數理／美學），其餘全是歷代舊神典石。
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

            AddCurrency(currencyManager, itemId);
        }
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
    /// 兩條都不通就跳過這一筆——寧可少顯示一種貨幣，也不要拿一個亂數當道具編號去查表。
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

        // Information 級：使用者跑 LogLevel 1，這行是實機唯一能回答
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
    /// 台服 7.20 的週上限是 450（<c>Tomestones</c> 表 row 3 ＝數理神典石）。
    /// <para>
    /// ⚠️ 週上限回 0 代表這一版沒有設週上限（或讀不到），這時候整個項目不顯示——
    /// 不要把它當成「已達上限」。
    /// </para>
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
    private static bool IsRealItem(uint itemId)
    {
        if (itemId == 0) return false;
        var row = Svc.Data.GetExcelSheet<Item>().GetRowOrDefault(itemId);
        return row != null && !string.IsNullOrEmpty(row.Value.Name.ExtractText());
    }

    private static string ResolveItemName(uint itemId)
    {
        var row = Svc.Data.GetExcelSheet<Item>().GetRowOrDefault(itemId);
        var name = row?.Name.ExtractText() ?? string.Empty;
        return string.IsNullOrEmpty(name) ? $"#{itemId}" : name;
    }

    private void UpdateDtr()
    {
        if (dtrEntry == null) return;

        // ⚠️ 讀不到資料時<b>要顯示</b>，而且要顯示「?」。
        // 藏起來會讓使用者以為「沒有貨幣快滿」，那正好是相反的意思。
        if (!dataReadable)
        {
            dtrEntry.Shown = true;
            dtrEntry.Text = new SeString(new IconPayload(DtrIcon), new TextPayload("?"));
            dtrEntry.Tooltip =
                "TC Toolbox — 貨幣上限警示\n讀不到貨幣資料，目前無法判斷有沒有快滿的貨幣。\n" +
                "（剛登入時會有短暫的這個狀態，屬正常。）";
            return;
        }

        if (nearCap.Count == 0)
        {
            // 這是「已知沒事」，不是「不知道」——可以安心藏起來，不占資訊列版面。
            dtrEntry.Shown = false;
            return;
        }

        dtrEntry.Shown = true;
        dtrEntry.Text = new SeString(new IconPayload(DtrIcon), new TextPayload(nearCap.Count.ToString()));

        var sb = new StringBuilder();
        sb.Append("TC Toolbox — 貨幣快接近上限\n");

        var shown = Math.Min(nearCap.Count, TooltipPreviewCount);
        for (var i = 0; i < shown; i++)
        {
            var c = nearCap[i];
            sb.Append($"\n{c.Name}：{c.Count} / {c.MaxCount}（還可再收 {c.Remaining}）");
            if (c.IsWeekly) sb.Append("　※每週取得量");
        }

        if (nearCap.Count > shown)
            sb.Append($"\n…等共 {nearCap.Count} 種");

        sb.Append("\n\n點擊開啟 TC Toolbox 設定");
        dtrEntry.Tooltip = sb.ToString();
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

        var current = new HashSet<uint>();
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
        announced.RemoveWhere(id => !current.Contains(id));
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

        ImGui.Separator();

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
}

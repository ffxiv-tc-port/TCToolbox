using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 「本 DC 各世界最低價」摘要。
/// </summary>
/// <remarks>
/// 🔴🔴 <b>紅線：純顯示。</b>這一段不會自動換世界、不掛售、不採購，也不會自己去查——
/// 每一次查詢都是使用者按下「查價」那一刻送出去的。上面的換 World 按鈕是另一回事，
/// 走它們自己的二次確認。
/// 🔴 本檔的欄位全部只在<b>遊戲主執行緒</b>讀寫（<c>Framework.Update</c> 與 ImGui 的 Draw 都是）。
/// 背景抓取的結果由 <see cref="UniversalisPrices.Consume"/> 在主執行緒交接。
/// </remarks>
public sealed partial class WorldTravelPanel
{
    /// <summary>同一件道具最短查詢間隔（毫秒）。</summary>
    private const long PriceQueryCooldownMs = 60_000;

    /// <summary>「不知道」用的灰。<b>不是</b>警告色——沒有資料不等於有問題。</summary>
    private static readonly Vector4 PriceUnknownColor = new(0.68f, 0.68f, 0.68f, 1f);

    private UniversalisPrices? prices;

    /// <summary>每件道具的下次可查時間（<see cref="Environment.TickCount64"/>）。</summary>
    private readonly Dictionary<uint, long> nextPriceQueryAt = [];

    /// <summary>本 DC 的世界（列號＋顯示名），照列號排序。</summary>
    private readonly List<(uint Id, string Name)> dcWorlds = [];

    /// <summary>上面那份清單是照哪一個所在 World 建的（0＝還沒建）。</summary>
    private uint dcWorldsBuiltFor;

    private string dcName = string.Empty;

    private int priceItemIdInput;

    private string priceHint = string.Empty;

    /// <summary>上一次輪詢讀到的市場佈告板道具（0＝出售清單視窗沒開）。只拿來判斷「換了一件」。</summary>
    private uint marketItemSeen;

    private void ResetPriceState()
    {
        nextPriceQueryAt.Clear();
        dcWorlds.Clear();
        dcWorldsBuiltFor = 0;
        dcName = string.Empty;
        priceItemIdInput = 0;
        priceHint = string.Empty;
        marketItemSeen = 0;
    }

    /// <summary>
    /// 重建「本 DC 有哪些世界」。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>不寫死任何中文世界名或資料中心名。</b>顯示名與查詢用的 DC 名一律取自
    /// <c>World</c>／<c>WorldDCGroupType</c> 表，寫死的話遊戲改字就整段失效。
    /// 📌 這裡<b>不問 Lifestream 能不能去</b>：價格摘要是純顯示，連去不了的世界也該看得到價格。
    /// </remarks>
    private void EnsureDcWorlds()
    {
        if (dcWorldsBuiltFor == currentWorldId && dcWorlds.Count > 0) return;

        dcWorlds.Clear();
        dcName = string.Empty;
        dcWorldsBuiltFor = currentWorldId;
        if (currentWorldId == 0) return;

        var me = Svc.Data.GetExcelSheet<World>().GetRowOrDefault(currentWorldId);
        if (me == null) return;

        var myDc = me.Value.DataCenter;
        if (myDc.RowId == 0) return;

        dcName = myDc.ValueNullable?.Name.ExtractText() ?? string.Empty;

        foreach (var world in Svc.Data.GetExcelSheet<World>())
        {
            if (world.RowId == 0 || world.DataCenter.RowId != myDc.RowId) continue;

            var name = world.Name.ExtractText();
            if (string.IsNullOrWhiteSpace(name)) continue;

            dcWorlds.Add((world.RowId, name));
        }

        dcWorlds.Sort(static (a, b) => a.Id.CompareTo(b.Id));
    }

    private void DrawPriceSummary()
    {
        ImGui.TextUnformatted("本 DC 各世界最低價");
        ImGui.SameLine();
        ImGui.TextDisabled("(Universalis · 純顯示)");
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "資料來自 Universalis 的公開市場資料，不是遊戲裡的即時查詢。\n"
                + "台服的上傳者很少，所以「沒有資料」是常態，不代表那個世界真的沒人賣。\n"
                + "這一段只顯示，不會自動換世界、不會掛售、不會採購。");
        }

        EnsureDcWorlds();

        if (currentWorldId == 0 || dcWorlds.Count == 0 || string.IsNullOrEmpty(dcName))
        {
            ImGui.TextDisabled("還沒登入，或查不到你的資料中心。");
            return;
        }

        DrawPriceControls();

        var result = prices?.Latest;
        if (result == null)
        {
            if (!string.IsNullOrEmpty(priceHint))
            {
                ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
                ImGui.TextColored(PriceUnknownColor, priceHint);
                ImGui.PopTextWrapPos();
            }

            return;
        }

        if (result.Error != null)
        {
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
            ImGui.TextColored(WarnColor, $"查價失敗：{result.Error}");
            ImGui.PopTextWrapPos();
            return;
        }

        DrawPriceTable(result);
    }

    private void DrawPriceControls()
    {
        var itemId = priceItemIdInput;
        ImGui.SetNextItemWidth(140f);
        if (ImGui.InputInt("道具編號##WorldTravelPriceItem", ref itemId))
            priceItemIdInput = Math.Clamp(itemId, 0, int.MaxValue);

        ImGui.SameLine();
        if (ImGui.Button("用滑鼠指著的道具##WorldTravelPriceHover"))
            TakeHoveredItem();

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "在市場佈告板點開一件道具、看它的出售清單時，那件道具會自動填進左邊的欄位，不必按這裡。\n"
                + "其他地方（背包、製作清單…）的道具：把滑鼠移到道具上，再按這裡。\n"
                + "兩種都只會填編號，不會送出查詢。");
        }

        var target = (uint)priceItemIdInput;
        var now = Environment.TickCount64;

        // 🔴 分兩行寫：塞進 && 鏈裡的話 until 在鏈之後不算「確定已指派」，下面的 tooltip 用不到它。
        nextPriceQueryAt.TryGetValue(target, out var until);
        var cooling = target != 0 && now < until;
        var busy = prices?.IsFetching ?? false;

        ImGui.SameLine();
        using (ImRaii.Disabled(target == 0 || cooling || busy))
        {
            if (ImGui.Button("查價##WorldTravelPriceGo"))
                StartPriceQuery(target);
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            var why = target == 0
                ? "先填一個道具編號。"
                : busy
                    ? "上一次查詢還沒回來。"
                    : cooling
                        ? $"同一件道具 {PriceQueryCooldownMs / 1000} 秒只查一次，還要等 {Math.Max(1, (until - now + 999) / 1000)} 秒。"
                        : $"向 Universalis 問一次「{dcName}」各世界的掛單。只讀資料，不會做任何事。";
            ImGui.SetTooltip(why);
        }

        if (target != 0)
        {
            ImGui.SameLine();
            var sheet = Svc.Data.GetExcelSheet<Item>().GetRowOrDefault(target);
            var name = ItemNames.Get(target);

            // 市場買賣不了的道具在 Universalis 上永遠是空的——先講，免得被讀成「查詢壞了」。
            if (sheet == null || sheet.Value.ItemSearchCategory.RowId == 0)
            {
                ImGui.TextColored(PriceUnknownColor, $"{name}（這件道具不能在市場交易）");
            }
            else
            {
                ImGui.TextUnformatted(name);
            }
        }
    }

    /// <summary>
    /// 把滑鼠指著的道具編號填進輸入框。
    /// </summary>
    /// <remarks>
    /// 📌 來源是 Dalamud 的 <c>IGameGui.HoveredItem</c>，不是原生視窗的內部欄位——
    /// 沒有寫死的結構偏移，台服與國際服共用同一條路徑。
    /// 🔴 HQ 是 <c>+1000000</c>、收藏品是 <c>+500000</c>，一律正規化回基礎列號再查價
    /// （Universalis 的 NQ／HQ 是同一件道具的兩欄，不是兩個編號）。
    /// </remarks>
    private void TakeHoveredItem()
    {
        var raw = Svc.GameGui.HoveredItem;
        if (raw == 0)
        {
            priceHint = "現在沒有指著任何道具。把滑鼠移到道具上再按一次。";
            return;
        }

        if (!ItemContextResolver.TryGetItemName(raw, out var itemId, out var itemName))
        {
            priceHint = "認不出滑鼠指著的那個道具。";
            return;
        }

        priceItemIdInput = (int)itemId;
        priceHint = $"已填入「{itemName}」（#{itemId}）。按「查價」才會送出查詢。";
    }

    /// <summary>
    /// 市場佈告板的出售清單換了一件道具時，把它填進輸入框（只填、不查）。
    /// </summary>
    /// <remarks>
    /// 🔴 只在 framework 執行緒呼叫（<c>Refresh</c>）。只在「換了一件」那一刻填，
    /// 所以使用者之後自己改的編號不會被下一次輪詢蓋掉。
    /// </remarks>
    private void PollMarketBoardItem()
    {
        var raw = ReadMarketBoardItemId();
        if (raw == marketItemSeen) return;

        marketItemSeen = raw;
        if (raw == 0) return;

        var row = Svc.Data.GetExcelSheet<Item>().GetRowOrDefault(raw);
        var name = row?.Name.ExtractText();

        // 不在 Item 表、或是不能在市場交易的值＝讀錯了：不填，同一個值只記一次。
        if (row == null || string.IsNullOrWhiteSpace(name) || row.Value.ItemSearchCategory.RowId == 0)
        {
            Svc.Log.Information(
                $"[WorldTravelPanel] 市場佈告板的出售清單開著，但讀到的道具編號 {raw} 不是可交易道具，沒有帶入。");
            return;
        }

        priceItemIdInput = (int)raw;
        priceHint = $"已從市場佈告板帶入「{name}」（#{raw}）。按「查價」才會送出查詢。";
        Svc.Log.Information($"[WorldTravelPanel] 已從市場佈告板帶入道具 {raw}（{name}）。");
    }

    /// <summary>市場佈告板出售清單正在看的道具；視窗沒開或 agent 拿不到時回 0。</summary>
    /// <remarks>
    /// 先認視窗再讀欄位：視窗關掉之後 agent 裡可能還留著上一件。指標當場取、當場丟。
    /// </remarks>
    private static unsafe uint ReadMarketBoardItemId()
    {
        if (!UiHelper.IsAddonReady("ItemSearchResult")) return 0;

        var agent = AgentItemSearch.Instance();
        return agent == null ? 0 : agent->ResultItemId;
    }

    private void StartPriceQuery(uint itemId)
    {
        if (itemId == 0 || prices == null || string.IsNullOrEmpty(dcName)) return;

        if (!prices.TryStart(itemId, dcName))
        {
            priceHint = "上一次查詢還沒回來。";
            return;
        }

        nextPriceQueryAt[itemId] = Environment.TickCount64 + PriceQueryCooldownMs;
        priceHint = $"正在查「{ItemNames.Get(itemId)}」在 {dcName} 各世界的掛單…";

        // 使用者回報用的定錨點：出事時這一行是唯一能證明查詢是這個面板送出去的證據。
        Svc.Log.Information(
            $"[WorldTravelPanel] 使用者按下查價：道具 {itemId}（{ItemNames.Get(itemId)}），資料中心 {dcName}。");
    }

    /// <summary>
    /// 畫結果表。
    /// </summary>
    /// <remarks>
    /// 🔑 <b>「不知道」要在列上看得見。</b>沒有價格的格子分三種而且畫法不同：
    /// 從來沒人上傳過＝「沒有資料」、確認整個 DC 都沒人賣＝「無掛單」、
    /// 掛單筆數頂到上限而這個世界沒擠進來＝「? 超出前 N 筆」。
    /// 全部畫成 0 會讓人以為那裡免費，全部畫成「無」會讓「不知道」冒充成結論。
    /// </remarks>
    private void DrawPriceTable(PriceFetchResult result)
    {
        var name = ItemNames.Get(result.ItemId);
        ImGui.TextDisabled($"「{name}」· {result.DcName} · 看了 {result.ListingsSeen} 筆掛單"
                           + (result.Capped ? $"（已達上限 {UniversalisPrices.ListingCap} 筆）" : string.Empty));

        uint bestNq = 0;
        uint bestNqWorld = 0;
        uint bestHq = 0;
        uint bestHqWorld = 0;
        foreach (var (id, _) in dcWorlds)
        {
            if (!result.Worlds.TryGetValue(id, out var row)) continue;
            if (row.MinNq != 0 && (bestNq == 0 || row.MinNq < bestNq)) (bestNq, bestNqWorld) = (row.MinNq, id);
            if (row.MinHq != 0 && (bestHq == 0 || row.MinHq < bestHq)) (bestHq, bestHqWorld) = (row.MinHq, id);
        }

        using (var table = ImRaii.Table("##WorldTravelPrices", 4,
                   ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp))
        {
            if (table)
            {
                ImGui.TableSetupColumn("世界", ImGuiTableColumnFlags.WidthFixed, 110f);
                ImGui.TableSetupColumn("普通品最低", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("高品質最低", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("資料時間", ImGuiTableColumnFlags.WidthFixed, 110f);
                ImGui.TableHeadersRow();

                foreach (var (id, worldName) in dcWorlds)
                {
                    ImGui.TableNextRow();

                    ImGui.TableNextColumn();
                    var label = id == currentWorldId ? $"▶ {worldName}" : worldName;
                    if (id == bestNqWorld) ImGui.TextColored(OkColor, $"★ {label}");
                    else ImGui.TextUnformatted(label);

                    result.Worlds.TryGetValue(id, out var row);
                    var uploaded = row?.UploadedAtUnixMs ?? 0;

                    ImGui.TableNextColumn();
                    DrawPriceCell(row?.MinNq ?? 0, bestNq, uploaded, result.Capped, id == bestNqWorld);

                    ImGui.TableNextColumn();
                    DrawPriceCell(row?.MinHq ?? 0, bestHq, uploaded, result.Capped, id == bestHqWorld);

                    ImGui.TableNextColumn();
                    if (uploaded <= 0) ImGui.TextColored(PriceUnknownColor, "—");
                    else ImGui.TextDisabled(DescribeAge(uploaded));
                }
            }
        }

        DrawPriceVerdict(result, bestNq, bestNqWorld);
    }

    private void DrawPriceCell(uint price, uint best, long uploaded, bool capped, bool isBest)
    {
        if (price != 0)
        {
            var text = $"{price:N0}";
            if (!isBest && best != 0) text += $"　(+{price - best:N0})";

            if (isBest) ImGui.TextColored(OkColor, text);
            else ImGui.TextUnformatted(text);
            return;
        }

        // 🔑 三種「沒有價格」是三件不同的事，不可以合成同一句話。
        if (uploaded <= 0)
        {
            ImGui.TextColored(PriceUnknownColor, "? 沒有資料");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Universalis 從來沒有收到這個世界的這件道具資料，不代表沒人賣。");
        }
        else if (capped)
        {
            ImGui.TextColored(PriceUnknownColor, $"? 超出前 {UniversalisPrices.ListingCap} 筆");
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    $"這次只看了整個資料中心最便宜的 {UniversalisPrices.ListingCap} 筆掛單，這個世界沒擠進來。\n"
                    + "也就是說它的最低價比那 100 筆都貴——找最便宜的世界時可以直接略過。");
            }
        }
        else
        {
            ImGui.TextColored(PriceUnknownColor, "無掛單");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("這次看完了整個資料中心的掛單，這個世界確實沒有人在賣。");
        }
    }

    /// <summary>最後那一句結論：值不值得跑一趟。</summary>
    private void DrawPriceVerdict(PriceFetchResult result, uint bestNq, uint bestNqWorld)
    {
        if (bestNq == 0 || bestNqWorld == 0)
        {
            ImGui.TextColored(PriceUnknownColor, "這次沒有看到任何普通品掛單。");
            return;
        }

        var bestName = FindDcWorldName(bestNqWorld);

        if (bestNqWorld == currentWorldId)
        {
            ImGui.TextColored(OkColor, $"你現在就在最便宜的世界（{bestName}，{bestNq:N0}）。");
            return;
        }

        var mine = result.Worlds.TryGetValue(currentWorldId, out var here) ? here.MinNq : 0;
        if (mine == 0)
        {
            ImGui.TextColored(OkColor,
                $"最便宜：{bestName} {bestNq:N0}。（你現在的 {currentWorldName} 這次沒看到掛單，比不出差額。）");
            return;
        }

        var diff = mine - bestNq;
        var percent = mine == 0 ? 0 : diff * 100.0 / mine;
        ImGui.TextColored(OkColor,
            $"最便宜：{bestName} {bestNq:N0}，比你現在的 {currentWorldName}（{mine:N0}）便宜 {diff:N0}（{percent:F0}%）。");

        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
        ImGui.TextDisabled("要過去的話用上面的換 World 按鈕；這一段不會自己送你過去。");
        ImGui.PopTextWrapPos();
    }

    private string FindDcWorldName(uint id)
    {
        foreach (var (worldId, worldName) in dcWorlds)
        {
            if (worldId == id) return worldName;
        }

        return $"World #{id}";
    }

    /// <summary>把 Unix 毫秒畫成「幾分鐘前」。</summary>
    /// <remarks>📌 絕對時間放這裡沒有用：使用者要判斷的是「這筆資料夠不夠新」。</remarks>
    private static string DescribeAge(long unixMs)
    {
        var age = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(unixMs);
        if (age < TimeSpan.Zero) return "剛剛";
        if (age.TotalMinutes < 1) return "剛剛";
        if (age.TotalHours < 1) return $"{(int)age.TotalMinutes} 分鐘前";
        if (age.TotalDays < 1) return $"{(int)age.TotalHours} 小時前";
        return $"{(int)age.TotalDays} 天前";
    }
}

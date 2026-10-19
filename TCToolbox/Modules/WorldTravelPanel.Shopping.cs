using System;
using System.Collections.Generic;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 「在這個 World 要買的」：Artisan 製作清單還缺的材料裡，Artisan 已知最佳世界就是你現在這裡的那些。
/// </summary>
/// <remarks>
/// 🔴🔴 紅線：純顯示。不查價（價格只用 Artisan 已經有的）、不採購、不換世界。
/// 「在市場佈告板搜尋」只把道具名複製到剪貼簿、搜尋視窗開著時填進搜尋欄，不按搜尋。
/// 🔴 本檔欄位只在遊戲主執行緒讀寫；IPC 只在 <c>Framework.Update</c> 呼叫（換世界、按重新整理時各一次），Draw 只讀結果。
/// </remarks>
public sealed partial class WorldTravelPanel
{
    /// <summary>一列要畫的東西；字串在建表時組好，Draw 不再組字。</summary>
    private sealed record ShoppingRow(
        string ListName, uint ItemId, string ItemName, string RemainingText, string CountTooltip,
        string PriceText, string PriceTooltip, bool PriceDim, string ButtonLabel);

    private List<ArtisanShoppingPlan> shoppingPlans = [];

    /// <summary>上一次讀取是照哪一個所在 World（0＝還沒讀過）；換世界就重讀。</summary>
    private uint shoppingReadForWorld;

    private bool shoppingRefreshRequested;
    private bool shoppingOk;
    private string shoppingError = string.Empty;
    private long shoppingReadAt;

    // 以下是從 shoppingPlans 依所在 World 算出來的；只在讀取或換世界時重算。
    private readonly List<ShoppingRow> shoppingHere = [];
    private uint shoppingRowsBuiltFor;
    private string shoppingNothingHereText = string.Empty;
    private string shoppingElsewhereText = string.Empty;
    private string shoppingUnknownText = string.Empty;
    private string shoppingUnknownTooltip = string.Empty;
    private string shoppingSkippedText = string.Empty;

    private uint pendingMarketSearchItem;
    private string shoppingHint = string.Empty;

    private void ResetShoppingState()
    {
        shoppingPlans = [];
        shoppingReadForWorld = 0;
        shoppingRefreshRequested = false;
        shoppingOk = false;
        shoppingError = string.Empty;
        shoppingReadAt = 0;
        shoppingHere.Clear();
        shoppingRowsBuiltFor = 0;
        shoppingNothingHereText = string.Empty;
        shoppingElsewhereText = string.Empty;
        shoppingUnknownText = string.Empty;
        shoppingUnknownTooltip = string.Empty;
        shoppingSkippedText = string.Empty;
        pendingMarketSearchItem = 0;
        shoppingHint = string.Empty;
    }

    /// <summary>由 <c>Refresh</c> 呼叫（已登入、面板看得見、開關開著）。</summary>
    private void PollShopping()
    {
        if (currentWorldId == 0) return;

        if (shoppingRefreshRequested || shoppingReadForWorld != currentWorldId)
        {
            shoppingRefreshRequested = false;
            shoppingReadForWorld = currentWorldId;
            shoppingReadAt = Environment.TickCount64;
            shoppingOk = ArtisanShoppingIpc.TryGetAllPlans(out var plans, out shoppingError);
            shoppingPlans = plans;
            shoppingRowsBuiltFor = 0;
        }

        if (shoppingRowsBuiltFor != currentWorldId) BuildShoppingRows();
    }

    private bool IsHere(ArtisanShoppingItem item) =>
        item.OptimalWorldId != 0
            ? item.OptimalWorldId == currentWorldId
            : item.OptimalWorld.Length > 0 && string.Equals(item.OptimalWorld, currentWorldName, StringComparison.Ordinal);

    private void BuildShoppingRows()
    {
        shoppingRowsBuiltFor = currentWorldId;
        shoppingHere.Clear();

        var unknownPrice = 0;
        var npcCheaper = 0;
        var craftedOnList = 0;
        var universalisOff = false;
        var elsewhere = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var plan in shoppingPlans)
        {
            if (!plan.UniversalisEnabled) universalisOff = true;

            foreach (var item in plan.Items)
            {
                if (item == null || item.Remaining <= 0) continue;

                // 清單自己會做的半成品不買：它的材料已經另外列在同一份清單裡。
                if (item.CraftedOnList)
                {
                    craftedOnList++;
                    continue;
                }

                if (item.NpcCheaper)
                {
                    npcCheaper++;
                    continue;
                }

                if (!item.HasListings || string.IsNullOrEmpty(item.OptimalWorld))
                {
                    unknownPrice++;
                    continue;
                }

                if (!IsHere(item))
                {
                    elsewhere[item.OptimalWorld] = elsewhere.GetValueOrDefault(item.OptimalWorld) + 1;
                    continue;
                }

                shoppingHere.Add(MakeRow(plan.ListName, item, shoppingHere.Count));
            }
        }

        shoppingNothingHereText = $"{currentWorldName}沒有要買的（以 Artisan 已有的價格判斷）。";

        var others = new List<(string World, int Count)>();
        foreach (var (world, count) in elsewhere) others.Add((world, count));
        others.Sort(static (a, b) => b.Count != a.Count ? b.Count.CompareTo(a.Count) : string.CompareOrdinal(a.World, b.World));
        var parts = new List<string>(others.Count);
        foreach (var (world, count) in others) parts.Add($"{world} {count} 項");
        shoppingElsewhereText = parts.Count > 0 ? "其他 World：" + string.Join("、", parts) : string.Empty;

        shoppingUnknownText = unknownPrice > 0 ? $"? {unknownPrice} 項還缺的材料沒有價格資料" : string.Empty;
        shoppingUnknownTooltip =
            (universalisOff ? "Artisan 設定裡的 Universalis 查價是關的。\n" : string.Empty)
            + "在 Artisan 打開那份清單的編輯器、材料表取得價格之後，再按這裡的「重新整理」。\n"
            + "不知道在哪裡買最便宜，不代表這個 World 沒有人賣。";

        var skipped = new List<string>(2);
        if (npcCheaper > 0) skipped.Add($"{npcCheaper} 項 NPC 商店比較便宜");
        if (craftedOnList > 0) skipped.Add($"{craftedOnList} 項由清單自己製作");
        shoppingSkippedText = skipped.Count > 0 ? string.Join("；", skipped) + "，沒有列出" : string.Empty;
    }

    private static ShoppingRow MakeRow(string listName, ArtisanShoppingItem item, int index)
    {
        var shortBy = item.OptimalQuantity < item.Remaining;
        var price = item.Approximate ? $"約 {item.OptimalCost:N0}" : $"{item.OptimalCost:N0}";
        if (shortBy) price += $"（只有 {item.OptimalQuantity:N0} 件）";

        var source = item.PriceSource == 1 ? "Artisan 清單編輯器的材料表" : "Artisan 最近查過的 Universalis 快取";
        var tip = $"買 {item.OptimalQuantity:N0} 件的總價（{source}）。";
        if (item.Approximate)
            tip += "\nUniversalis 當時只回了最低單價，數量與總價是約略值。";
        if (shortBy)
            tip += $"\n這個 World 掛單只夠 {item.OptimalQuantity:N0} 件，不夠的 {item.Remaining - item.OptimalQuantity:N0} 件要去別處買。";

        return new ShoppingRow(
            listName, item.ItemId, ItemNames.Get(item.ItemId), item.Remaining.ToString(),
            $"清單需要 {item.Required}，背包＋僱員已有 {item.Owned}，還缺 {item.Remaining}。",
            price, tip, item.Approximate || shortBy, $"在市場佈告板搜尋##WorldTravelShopSearch{index}");
    }

    /// <summary>使用者按下「在市場佈告板搜尋」之後的那一次 <c>Framework.Update</c>。</summary>
    private void FlushMarketSearch()
    {
        if (pendingMarketSearchItem == 0) return;

        var itemId = pendingMarketSearchItem;
        pendingMarketSearchItem = 0;
        var name = ItemNames.Get(itemId);

        if (TryFillMarketSearch(name))
        {
            shoppingHint = $"已把「{name}」填進市場佈告板的搜尋欄（也複製到剪貼簿）。按「搜尋」才會找。";
            Svc.Log.Information($"[WorldTravelPanel] 已把道具 {itemId}（{name}）填進市場佈告板搜尋欄。");
        }
        else
        {
            shoppingHint = $"已複製「{name}」到剪貼簿。到市場佈告板的搜尋欄按 Ctrl+V 貼上再搜尋（搜尋視窗開著時按這裡會直接填進去）。";
        }
    }

    /// <summary>市場佈告板的搜尋視窗開著就把字填進搜尋欄；不按搜尋。</summary>
    /// <remarks>指標當場取、當場用、不留存；函式位址解不出來（改版）就只走剪貼簿。</remarks>
    private static unsafe bool TryFillMarketSearch(string name)
    {
        if (string.IsNullOrEmpty(name) || AtkComponentTextInput.MemberFunctionPointers.SetText == null) return false;

        var unit = UiHelper.GetAddon("ItemSearch");
        if (!UiHelper.IsReady(unit)) return false;

        var input = ((AddonItemSearch*)unit)->SearchTextInput;
        if (input == null) return false;

        input->SetText(name);
        return true;
    }

    private void DrawShoppingSection()
    {
        ImGui.TextUnformatted("在這個 World 要買的");
        ImGui.SameLine();
        ImGui.TextDisabled("(Artisan 製作清單 · 純顯示)");
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "列出 Artisan 製作清單還缺、而且 Artisan 算出「最佳購買世界」就是你現在這個 World 的材料。\n"
                + "價格只用 Artisan 已經有的：開著的清單編輯器材料表，或它最近查過的 Universalis 快取。\n"
                + "這一段不會查價、不會採購、不會換世界；換 World 或按「重新整理」時重讀一次。");
        }

        ImGui.SameLine(0f, 16f);
        if (ImGui.SmallButton("重新整理##WorldTravelShopRefresh")) shoppingRefreshRequested = true;

        if (shoppingReadAt != 0 && ImGui.IsItemHovered())
        {
            var age = (Environment.TickCount64 - shoppingReadAt) / 1000;
            ImGui.SetTooltip(age < 60 ? $"{age} 秒前讀的。" : $"{age / 60} 分鐘前讀的。");
        }

        if (currentWorldId == 0)
        {
            ImGui.TextDisabled("還沒登入。");
            return;
        }

        if (shoppingReadForWorld == 0)
        {
            ImGui.TextDisabled("讀取中…");
            return;
        }

        if (!shoppingOk)
        {
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
            ImGui.TextColored(WarnColor, shoppingError);
            ImGui.PopTextWrapPos();
            return;
        }

        if (shoppingPlans.Count == 0)
        {
            ImGui.TextDisabled("Artisan 沒有任何製作清單。");
            return;
        }

        if (shoppingHere.Count == 0) ImGui.TextDisabled(shoppingNothingHereText);
        else DrawShoppingTable();

        if (shoppingElsewhereText.Length > 0)
        {
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
            ImGui.TextDisabled(shoppingElsewhereText);
            ImGui.PopTextWrapPos();
        }

        if (shoppingUnknownText.Length > 0)
        {
            ImGui.TextColored(PriceUnknownColor, shoppingUnknownText);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(shoppingUnknownTooltip);
        }

        if (shoppingSkippedText.Length > 0) ImGui.TextDisabled(shoppingSkippedText);

        if (shoppingHint.Length > 0)
        {
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
            ImGui.TextColored(OkColor, shoppingHint);
            ImGui.PopTextWrapPos();
        }
    }

    private void DrawShoppingTable()
    {
        using var table = ImRaii.Table("##WorldTravelShopping", 5,
            ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp);
        if (!table) return;

        ImGui.TableSetupColumn("清單", ImGuiTableColumnFlags.WidthStretch, 0.8f);
        ImGui.TableSetupColumn("材料", ImGuiTableColumnFlags.WidthStretch, 1.2f);
        ImGui.TableSetupColumn("數量", ImGuiTableColumnFlags.WidthFixed, 50f);
        ImGui.TableSetupColumn("價格", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn(string.Empty, ImGuiTableColumnFlags.WidthFixed, 130f);
        ImGui.TableHeadersRow();

        foreach (var row in shoppingHere)
        {
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.ListName);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.ItemName);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.RemainingText);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(row.CountTooltip);

            ImGui.TableNextColumn();
            if (row.PriceDim) ImGui.TextColored(PriceUnknownColor, row.PriceText);
            else ImGui.TextUnformatted(row.PriceText);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(row.PriceTooltip);

            ImGui.TableNextColumn();
            if (ImGui.SmallButton(row.ButtonLabel))
            {
                ImGui.SetClipboardText(row.ItemName);
                pendingMarketSearchItem = row.ItemId;
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    "把道具名複製到剪貼簿；市場佈告板的搜尋視窗開著時也會直接填進搜尋欄。\n"
                    + "只填字、不按搜尋、不查價、不買。");
            }
        }
    }
}

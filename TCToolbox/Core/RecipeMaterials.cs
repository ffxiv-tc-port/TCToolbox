using System;
using System.Collections.Generic;
using Lumina.Excel.Sheets;

namespace TCToolbox.Core;

/// <summary>把一個成品展開成材料需求的結果。</summary>
public enum RecipeExpandResult
{
    /// <summary>
    /// 這個道具<b>沒有配方</b>——它自己就是要去買／去採的東西。
    /// </summary>
    /// <remarks>
    /// 🔴 零值刻意給這個：展開失敗時把成品自己當成材料是安全的方向
    /// （使用者會看到「這件要自己弄到」），反過來把它當成「已展開」就會讓它<b>整列消失</b>。
    /// </remarks>
    NoRecipe = 0,

    /// <summary>展開完成。</summary>
    Ok = 1,

    /// <summary>
    /// 展開到了深度上限（或撞到循環）才停，<b>底下還有沒展開的中間材料</b>。
    /// </summary>
    /// <remarks>這個狀態必須傳到畫面上：使用者看到的材料清單在這種情況下是不完整的。</remarks>
    Truncated = 2,
}

/// <summary>
/// 用 Lumina <c>Recipe</c> 表把「要做幾個成品」展開成「要幾個材料」。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 <b>不呼叫任何外掛、不碰遊戲記憶體、零封包。</b>整支只讀 <c>Recipe</c> 與 <c>Item</c>
/// 兩張資料表（台服自帶繁中）。
/// </para>
/// <para>
/// 📌 <b>為什麼要自己展開</b>：AllaganTools 的 <c>AllaganTools.GetCraftItems</c> 回的是
/// <b>成品</b>（它的實作只收 <c>craftItem.IsOutputItem</c> 的那幾筆），不是材料；
/// 而 Artisan 沒有「清單裡有什麼」的端點。所以「缺什麼」這件事沒有現成的 IPC 可問，
/// 只能自己從配方表算。
/// </para>
/// <para>
/// ⚠️ <b>一個成品可能有好幾個配方</b>（不同製作職業各一筆）。這裡取<b>列號最小</b>的那一筆，
/// 並且把「有幾筆」一起回報出去（<see cref="RecipeCount"/>），畫面上才說得出
/// 「這一列是照哪個配方算的」。⚠️ 同一件成品的不同職業配方<b>材料通常相同但不保證</b>，
/// 所以這是一個近似值，不是保證。
/// </para>
/// <para>
/// ⚠️ <b>水晶／碎晶／晶簇也是材料，會一起展開出來。</b>它們在配方表裡與其他材料同格
/// （2026-09-12 以台服 dump 實證：配方列 1「青銅錠」的 <c>Ingredient[2]</c> 就是
/// <c>Item</c> #2 火之碎晶），沒有獨立的欄位可以跳過。要不要顯示由呼叫端決定。
/// </para>
/// </remarks>
internal static class RecipeMaterials
{
    /// <summary>
    /// 遞迴展開的深度上限。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>這不只是效能上限，是終止保證的一半。</b>另一半是路徑上的循環偵測
    /// （見 <see cref="Walk"/>）。遊戲資料裡目前沒有已知的循環配方，但「目前沒有」
    /// 不是一個可以拿來當終止條件的性質——資料表是會改的，而無限遞迴的失敗形式是
    /// <b>遊戲整個當掉</b>，不是一行錯誤訊息。
    /// </remarks>
    public const int MaxDepth = 6;

    /// <summary>道具編號 → 產出它的配方列號（升冪）。<c>null</c>＝還沒建。</summary>
    /// <remarks>
    /// ⚠️ 只在遊戲主執行緒建立與讀取（呼叫端全部是 <c>Framework.Update</c>）。
    /// 建一次約一萬四千列，之後都是字典查詢。
    /// </remarks>
    private static Dictionary<uint, List<uint>>? resultIndex;

    /// <summary>
    /// 這個道具有沒有配方。
    /// </summary>
    public static bool HasRecipe(uint itemId) => itemId != 0 && Index().ContainsKey(itemId);

    /// <summary>這個道具有幾個配方（0＝沒有）。</summary>
    public static int RecipeCount(uint itemId) =>
        itemId != 0 && Index().TryGetValue(itemId, out var list) ? list.Count : 0;

    /// <summary>
    /// 把「要做 <paramref name="wantedQuantity"/> 個 <paramref name="outputItemId"/>」
    /// 展開成材料需求，每一筆交給 <paramref name="visit"/>。
    /// </summary>
    /// <param name="outputItemId">成品的道具編號。</param>
    /// <param name="wantedQuantity">要做幾個（&lt;=0 時什麼都不做並回 <see cref="RecipeExpandResult.Ok"/>）。</param>
    /// <param name="recursive">
    /// 中間材料本身也有配方時，要不要繼續往下展開。
    /// <see langword="false"/>（預設）＝只展開一層，中間材料原樣列出來。
    /// </param>
    /// <param name="visit">
    /// 每一筆材料需求的回呼：<c>(材料編號, 需要數量, 這一筆是在第幾層算出來的)</c>。
    /// <b>同一個材料可能被呼叫多次</b>（不同成品、不同分支），呼叫端要自己累加。
    /// </param>
    /// <remarks>
    /// 🔴 <b>產量（<c>AmountResult</c>）一定要算進去</b>，而且是<b>向上取整</b>：
    /// 一次產 3 個的配方要 4 個成品，得做 2 輪＝材料要兩倍。
    /// 忘記取整的失敗形式是「材料算少了」，而使用者是在商人面前才發現的。
    /// </remarks>
    public static RecipeExpandResult Expand(
        uint outputItemId, long wantedQuantity, bool recursive, Action<uint, long, int> visit)
    {
        if (wantedQuantity <= 0) return RecipeExpandResult.Ok;
        if (!TryGetPrimaryRecipe(outputItemId, out var recipe)) return RecipeExpandResult.NoRecipe;

        // 路徑上已經展開過的成品：撞到自己就停，不再往下。
        var path = new HashSet<uint> { outputItemId };
        var truncated = false;

        Walk(recipe, wantedQuantity, 0, recursive, visit, path, ref truncated);

        return truncated ? RecipeExpandResult.Truncated : RecipeExpandResult.Ok;
    }

    /// <summary>取這個成品「列號最小」的那個配方。</summary>
    /// <remarks>
    /// 📌 挑列號最小不是因為它比較正確，而是因為它<b>穩定</b>——同一份資料表每次都給同一筆，
    /// 使用者不會看到數字在兩次重新整理之間跳。
    /// </remarks>
    public static bool TryGetPrimaryRecipe(uint itemId, out Recipe recipe)
    {
        recipe = default;

        if (itemId == 0) return false;
        if (!Index().TryGetValue(itemId, out var rows) || rows.Count == 0) return false;

        var row = Svc.Data.GetExcelSheet<Recipe>().GetRowOrDefault(rows[0]);
        if (row == null) return false;

        recipe = row.Value;
        return true;
    }

    private static void Walk(
        Recipe recipe, long wanted, int depth, bool recursive,
        Action<uint, long, int> visit, HashSet<uint> path, ref bool truncated)
    {
        // 🔴 產量 0 的列在資料表裡是有的（佔位／未開放）。當成 1 而不是除以零。
        var yield = recipe.AmountResult == 0 ? 1L : recipe.AmountResult;

        // 向上取整的整數寫法（不走 double，避免大數量時的精度問題）。
        var sets = (wanted + yield - 1) / yield;

        // ⚠️ 兩個集合理論上同長（各 8 格），但取較小的那個才不會在資料表改動時越界。
        var slots = Math.Min(recipe.Ingredient.Count, recipe.AmountIngredient.Count);

        for (var i = 0; i < slots; i++)
        {
            var amount = recipe.AmountIngredient[i];
            if (amount == 0) continue;

            var ingredient = recipe.Ingredient[i];
            var itemId = ingredient.RowId;
            if (itemId == 0) continue;

            // 🔴 空格在台服 dump 裡是 -1 不是 0（實證：配方列 1 的 Ingredient[3] 就是 -1，
            //    配對的數量是 0）。轉成 RowId 之後會變成一個查不到的巨大數字，
            //    所以除了數量為 0 之外還要確認這一列真的解得開——不然畫面上會出現
            //    一列名字是「#4294967295」的材料。
            if (ingredient.ValueNullable == null) continue;

            var need = sets * amount;

            if (recursive && HasRecipe(itemId))
            {
                if (depth + 1 >= MaxDepth || path.Contains(itemId))
                {
                    // 展不下去了：這一筆照樣列出來（它是使用者拿得到的東西），
                    // 但要讓畫面知道清單不完整。
                    truncated = true;
                }
                else if (TryGetPrimaryRecipe(itemId, out var sub))
                {
                    path.Add(itemId);
                    Walk(sub, need, depth + 1, true, visit, path, ref truncated);
                    path.Remove(itemId);
                    continue;
                }
            }

            visit(itemId, need, depth);
        }
    }

    private static Dictionary<uint, List<uint>> Index()
    {
        if (resultIndex != null) return resultIndex;

        var map = new Dictionary<uint, List<uint>>(8192);

        foreach (var recipe in Svc.Data.GetExcelSheet<Recipe>())
        {
            var result = recipe.ItemResult.RowId;
            if (result == 0) continue;

            if (!map.TryGetValue(result, out var rows))
            {
                rows = [];
                map[result] = rows;
            }

            rows.Add(recipe.RowId);
        }

        // 列號升冪：ExcelSheet 的列舉本來就是升冪的，這裡只是把前提寫成碼
        // （將來若列舉順序改了，TryGetPrimaryRecipe 的「穩定」承諾不會跟著壞掉）。
        foreach (var rows in map.Values)
            rows.Sort();

        resultIndex = map;
        Svc.Log.Information($"[RecipeMaterials] 已建立配方索引：{map.Count} 種成品。");

        return map;
    }
}

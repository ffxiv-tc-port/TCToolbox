using System;
using System.Collections.Generic;
using Lumina.Excel.Sheets;

namespace TCToolbox.Core;

/// <summary>一格園圃的狀態。</summary>
/// <remarks>
/// 📌 零值是 <see cref="Unknown"/>：讀不出來的時候必須看得出「不知道」，
/// 而不是被當成某個具體狀態去動它。
/// </remarks>
public enum PatchState
{
    /// <summary>讀不出來。<b>一律當成「不要動」</b>。</summary>
    Unknown = 0,

    /// <summary>沒有種東西（Talk：「地壟裡沒有種任何東西。」）。</summary>
    Empty = 1,

    /// <summary>正茁壯成長（Talk 行 8）。</summary>
    Growing = 2,

    /// <summary>狀態不太好，需要護理（Talk 行 9）。</summary>
    NeedsCare = 3,

    /// <summary>已經成熟，可以收穫（Talk 行 10）。</summary>
    Ripe = 4,

    /// <summary>已經枯萎（Talk 行 7）。收不到東西，只能處理掉。</summary>
    Withered = 5,
}

/// <summary>成熟作物的處置策略。</summary>
public enum MatureCropPolicy
{
    /// <summary>不動它。<b>零值＝最保守</b>。</summary>
    Skip = 0,

    /// <summary>收穫，收完就讓它空著。</summary>
    HarvestOnly = 1,

    /// <summary>收穫，之後在同一輪把目標作物種回去。</summary>
    HarvestAndReplant = 2,
}

/// <summary>施肥策略。</summary>
public enum FertilizePolicy
{
    /// <summary>不施肥。<b>零值＝最保守</b>（肥料是消耗品）。</summary>
    None = 0,

    /// <summary>只對目標作物施肥。</summary>
    TargetOnly = 1,

    /// <summary>對所有生長中的作物施肥。</summary>
    All = 2,
}

/// <summary>枯萎作物的處置策略。</summary>
public enum WitheredPolicy
{
    /// <summary>不動它。<b>零值＝最保守</b>。</summary>
    Leave = 0,

    /// <summary>處理掉（清空地壟）。</summary>
    Dispose = 1,

    /// <summary>處理掉，之後在同一輪把目標作物種回去。</summary>
    DisposeAndReplant = 2,
}

/// <summary>這一格的決定被「缺材料」影響到的種類。</summary>
/// <remarks>
/// 🔴 <b>存在的理由是「不要靠比對理由字串來判斷缺料」。</b>
/// <see cref="GardenDecision.Reason"/> 是給人看的句子，改一個字就會讓任何字串比對靜默失效，
/// 而失效的形式正好就是這個旗標要修掉的那一種：畫面上什麼都不說。
/// 📌 零值是 <see cref="None"/>：「沒有被材料擋到」是預設。
/// </remarks>
public enum GardenMaterial
{
    /// <summary>沒有被材料擋到。</summary>
    None = 0,

    /// <summary>想播種（含收穫／處理之後重種）但沒有可用的種子或土壤。</summary>
    SeedOrSoil = 1,

    /// <summary>想施肥但沒有可用的肥料。</summary>
    Fertilizer = 2,
}

/// <summary>對一格園圃的決定。</summary>
/// <param name="Action">要執行的動作；<c>null</c>＝這一格什麼都不做。</param>
/// <param name="Replant">這一格處理完之後，要不要在同一輪的最後把目標作物種回去。</param>
/// <param name="Reason">寫進記錄與 UI 的理由（一句話）。</param>
/// <param name="Missing">
/// 這一格有沒有因為缺材料而做不成本來想做的事。
/// ⚠️ <b>不等於「什麼都沒做」</b>：「收穫（想重種但沒有可用的種子或土壤）」照樣會收穫，
/// 但那仍然是一個使用者該知道的缺料狀況。
/// </param>
public readonly record struct GardenDecision(
    AutoGardenAction? Action, bool Replant, string Reason, GardenMaterial Missing = GardenMaterial.None);

/// <summary>決策層會產出的動作。與模組內部的 <c>GardenAction</c> 一一對應，刻意分開宣告。</summary>
/// <remarks>
/// 📌 分開的理由：這個列舉是<b>純決策</b>的輸出，不含 <c>Scan</c>／<c>Auto</c> 這種「流程用」的成員。
/// 混在一起的話，決策函式的回傳型別就能表達「決定去做一次掃描」這種沒有意義的東西。
/// </remarks>
public enum AutoGardenAction
{
    Harvest = 0,
    Tend = 1,
    Fertilize = 2,
    Plant = 3,
    Dispose = 4,
}

/// <summary>
/// 園圃決策層：由「這一格的狀態 ＋ 種的是什麼 ＋ 使用者的策略」算出要做什麼。
/// </summary>
/// <remarks>
/// 🔑 <b>純函式，不碰遊戲、不碰 UI、不碰設定檔。</b>唯一的輸入是參數，唯一的輸出是回傳值。
/// 這樣「決策」與「互動」才分得開——互動那一層已經很難改了，不該再把判斷混進去。
/// </remarks>
public static class GardenDecisionMaker
{
    /// <summary>
    /// 決定這一格要做什麼。
    /// </summary>
    /// <param name="state">Talk 讀到的狀態。</param>
    /// <param name="cropItemId">這一格種的作物 item id；0＝不明。</param>
    /// <param name="targetCropItemId">目標作物的 item id（由設定的種子推得）；0＝沒設定目標。</param>
    /// <param name="targetMature">目標作物成熟時的策略。</param>
    /// <param name="otherMature">非目標作物成熟時的策略。</param>
    /// <param name="fertilize">施肥策略。</param>
    /// <param name="withered">枯萎作物的策略。</param>
    /// <param name="tendWhenNeeded">狀態不好時要不要護理。</param>
    /// <param name="plantWhenEmpty">空地壟要不要播種。</param>
    /// <param name="canPlant">現在播得了種嗎（種子與土壤都選了、背包也有貨）。</param>
    /// <param name="canFertilize">現在施得了肥嗎（肥料選了、背包也有貨）。</param>
    public static GardenDecision Decide(
        PatchState state,
        uint cropItemId,
        uint targetCropItemId,
        MatureCropPolicy targetMature,
        MatureCropPolicy otherMature,
        FertilizePolicy fertilize,
        WitheredPolicy withered,
        bool tendWhenNeeded,
        bool plantWhenEmpty,
        bool canPlant,
        bool canFertilize)
    {
        switch (state)
        {
            // 🔴 讀不出狀態就什麼都不做。這裡是唯一一個「不知道」會走到的分支，
            //    而它的每一個替代選項都是拿使用者的作物去賭。
            case PatchState.Unknown:
                return new GardenDecision(null, false, "讀不到這一格的狀態，本次不動它");

            case PatchState.Empty:
                if (!plantWhenEmpty) return new GardenDecision(null, false, "空地壟（設定為不播種）");
                if (!canPlant)
                    return new GardenDecision(
                        null, false, "空地壟，但沒有可用的種子或土壤", GardenMaterial.SeedOrSoil);
                return new GardenDecision(AutoGardenAction.Plant, false, "空地壟，播種目標作物");

            case PatchState.Withered:
                return withered switch
                {
                    WitheredPolicy.Dispose =>
                        new GardenDecision(AutoGardenAction.Dispose, false, "已枯萎，處理掉"),
                    WitheredPolicy.DisposeAndReplant when canPlant =>
                        new GardenDecision(AutoGardenAction.Dispose, true, "已枯萎，處理掉並重種"),
                    WitheredPolicy.DisposeAndReplant =>
                        new GardenDecision(AutoGardenAction.Dispose, false,
                            "已枯萎，處理掉（想重種但沒有可用的種子或土壤）", GardenMaterial.SeedOrSoil),
                    _ => new GardenDecision(null, false, "已枯萎（設定為不動）"),
                };

            case PatchState.Ripe:
            {
                // 🔴 目標比對一律走 item id。作物 id 讀不出來（cropItemId==0）時**當成非目標**：
                //    「不知道是什麼」跟「確定是目標」之間，只有前者是安全的預設。
                var isTarget = targetCropItemId != 0 && cropItemId == targetCropItemId;
                var policy = isTarget ? targetMature : otherMature;
                var who = isTarget ? "目標作物" : cropItemId == 0 ? "作物不明" : "非目標作物";

                return policy switch
                {
                    MatureCropPolicy.HarvestOnly =>
                        new GardenDecision(AutoGardenAction.Harvest, false, $"{who}已成熟，收穫"),
                    MatureCropPolicy.HarvestAndReplant when canPlant =>
                        new GardenDecision(AutoGardenAction.Harvest, true, $"{who}已成熟，收穫並重種"),
                    MatureCropPolicy.HarvestAndReplant =>
                        new GardenDecision(AutoGardenAction.Harvest, false,
                            $"{who}已成熟，收穫（想重種但沒有可用的種子或土壤）", GardenMaterial.SeedOrSoil),
                    _ => new GardenDecision(null, false, $"{who}已成熟（設定為跳過）"),
                };
            }

            case PatchState.NeedsCare:
                // 護理是免費的、不消耗任何東西，而且是這個狀態唯一能改善它的動作 ⇒ 優先於施肥。
                if (tendWhenNeeded)
                    return new GardenDecision(AutoGardenAction.Tend, false, "狀態不好，護理");
                goto case PatchState.Growing;

            case PatchState.Growing:
            {
                var isTarget = targetCropItemId != 0 && cropItemId == targetCropItemId;

                // 🔴 <b>先問策略要不要施肥，再問肥料夠不夠。</b>順序顛倒的話，選了「不施肥」
                //    卻剛好沒有肥料的人會被告知「沒有可用的肥料」——那是一句正確但完全誤導的話，
                //    而缺料提示要是照著它去算，畫面上就會對一個根本不需要肥料的人喊缺肥料。
                //    （行為兩邊相同：這兩條路都是不動手，改的只有理由字串與缺料歸因。）
                var wantsFertilizer = fertilize switch
                {
                    FertilizePolicy.All => true,
                    FertilizePolicy.TargetOnly => isTarget,
                    _ => false,
                };

                if (!wantsFertilizer)
                {
                    return fertilize == FertilizePolicy.TargetOnly
                        ? new GardenDecision(null, false, "生長中，但不是目標作物（設定為只對目標施肥）")
                        : new GardenDecision(null, false, "生長中（設定為不施肥）");
                }

                if (!canFertilize)
                    return new GardenDecision(
                        null, false, "生長中，想施肥但沒有可用的肥料", GardenMaterial.Fertilizer);

                return fertilize == FertilizePolicy.TargetOnly
                    ? new GardenDecision(AutoGardenAction.Fertilize, false, "生長中的目標作物，施肥")
                    : new GardenDecision(AutoGardenAction.Fertilize, false, "生長中，施肥");
            }

            default:
                return new GardenDecision(null, false, "未知狀態，本次不動它");
        }
    }
}

/// <summary>
/// 園藝的資料表推導：種子 → 收穫作物、作物名 → 作物 item id。
/// </summary>
/// <remarks>
/// 🔑 <b>種子與收穫物的對應是可以從遊戲資料推出來的，不必寫死一張表。</b>
/// 路徑是 <c>Item.AdditionalData</c>（種子）→ <c>GardeningSeed</c> 列 → 該列的 <c>Item</c> 欄
/// ＝<b>收穫得到的作物</b>（不是種子自己）。
/// </remarks>
public static class GardenCropData
{
    /// <summary>園藝用品的 <c>ItemUICategory</c>。</summary>
    private const uint GardeningUiCategory = 82;

    /// <summary>園藝用品裡「種子」那一組的 <c>FilterGroup</c>（20＝種子、21＝土壤、22＝肥料）。</summary>
    private const byte SeedFilterGroup = 20;

    private static Dictionary<uint, uint>? seedToCrop;

    /// <summary>作物顯示名 → 作物 item id。只在 Talk 沒有帶 item 連結時當退路。</summary>
    private static Dictionary<string, uint>? cropNameToId;

    /// <summary>
    /// 這個種子種下去會收穫到哪個道具；推不出來回 0。
    /// </summary>
    public static uint CropOfSeed(uint seedItemId)
    {
        if (seedItemId == 0) return 0;
        EnsureBuilt();
        return seedToCrop!.TryGetValue(seedItemId, out var crop) ? crop : 0;
    }

    /// <summary>
    /// 用作物的<b>顯示名</b>查它的 item id；查不到回 0。
    /// </summary>
    /// <remarks>
    /// 🔴 這是<b>退路</b>，不是主要手段。主要手段是從 Talk 那句話裡的 item 連結直接拿 id
    /// （完全不碰文字）。只有當那句話沒有帶連結時才會走到這裡。
    /// </remarks>
    public static uint CropIdByName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return 0;
        EnsureBuilt();
        return cropNameToId!.TryGetValue(name.Trim(), out var id) ? id : 0;
    }

    /// <summary>對照表有幾筆（UI 顯示用；0＝沒建起來）。</summary>
    public static int KnownCropCount
    {
        get
        {
            EnsureBuilt();
            return seedToCrop!.Count;
        }
    }

    private static void EnsureBuilt()
    {
        if (seedToCrop != null && cropNameToId != null) return;

        var seeds = new Dictionary<uint, uint>();
        var names = new Dictionary<string, uint>(StringComparer.Ordinal);

        try
        {
            var gardening = Svc.Data.GetExcelSheet<GardeningSeed>();

            foreach (var item in Svc.Data.GetExcelSheet<Item>())
            {
                if (item.ItemUICategory.RowId != GardeningUiCategory) continue;
                if (item.FilterGroup != SeedFilterGroup) continue;

                // 種子的 AdditionalData 指向 GardeningSeed 的列；那一列的 Item 欄才是收穫物。
                var row = gardening.GetRowOrDefault(item.AdditionalData.RowId);
                if (row == null) continue;

                var cropId = row.Value.Item.RowId;
                if (cropId == 0) continue;

                seeds[item.RowId] = cropId;

                var cropName = row.Value.Item.ValueNullable?.Name.ExtractText();
                if (!string.IsNullOrWhiteSpace(cropName))
                    names[cropName] = cropId;
            }
        }
        catch (Exception ex)
        {
            // 建不起來就留空字典：呼叫端一律拿到 0＝「不明」，決策層對「不明」的處置是不動它。
            Svc.Log.Information($"[GardenCropData] 建立種子／作物對照表失敗：{ex.Message}");
        }

        seedToCrop = seeds;
        cropNameToId = names;

        Svc.Log.Information(
            $"[GardenCropData] 種子→作物 {seeds.Count} 筆、作物名→id {names.Count} 筆（由 GardeningSeed 表推導）。");
    }
}

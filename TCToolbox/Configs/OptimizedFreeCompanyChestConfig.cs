using FFXIVClientStructs.FFXIV.Client.Game;

namespace TCToolbox;

public sealed class OptimizedFreeCompanyChestConfig
{
    /// <summary>
    /// 開啟公會儲物櫃時要切到的頁面。
    /// ⚠️ 預設 <see cref="InventoryType.Invalid"/>＝不切換，也就是<b>維持遊戲原本的行為</b>。
    /// 舊設定檔沒有這個欄位，反序列化不會覆寫欄位初始值，所以升級不會讓人突然被換頁。
    /// ⚠️ 水晶頁是 <c>FreeCompanyCrystals</c>(22001)，不是「20000＋5」——不要用頁序去算。
    /// </summary>
    public InventoryType DefaultPage = InventoryType.Invalid;
}

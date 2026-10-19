namespace TCToolbox;

/// <summary>商店介面預設值。</summary>
public sealed class ShopDefaultsConfig
{
    /// <summary>
    /// 軍票商店開啟時要切到的分頁序（0 起算，對應 <c>GCShopItemCategory</c> 裡有名字的列的順序）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 預設 <c>-1</c>＝不切換，也就是<b>維持遊戲原本的行為</b>。
    /// 舊設定檔沒有這個欄位，反序列化不會覆寫欄位初始值，所以升級不會讓人突然被換分頁。
    /// </remarks>
    public int GrandCompanyDefaultTab = -1;
}

namespace TCToolbox;

/// <summary>把在場的寶箱與風脈泉顯示到小地圖（透過 Mini-Mappingway）。</summary>
/// <remarks>
/// 📌 這個模組<b>預設關閉</b>（所有模組都是——啟用與否記在
/// <see cref="Configuration.EnabledModules"/>），所以既有使用者更新後不會多出任何東西。
/// 下面的預設值是「啟用之後」的行為：兩種來源都開，這樣打開模組就真的看得到東西。
/// 🔴 <b>這裡刻意沒有顏色設定。</b>顏色只有在「來源第一次出現在 Mini-Mappingway」時才有作用
/// （之後對方一律讀它自己存檔的那一份），所以做成設定會變成一個看起來會動、其實不會動的選項。
/// 顏色請在 Mini-Mappingway 自己的設定裡調。
/// </remarks>
public sealed class NearbyOnMinimapConfig
{
    public bool ShowChests { get; set; } = true;

    public bool ShowAetherCurrents { get; set; } = true;

    /// <summary>風脈泉只顯示還沒共鳴的那些。</summary>
    /// <remarks>📌 預設開啟：已經共鳴過的風脈泉不必再走過去，畫出來只是噪音。</remarks>
    public bool OnlyUnattunedAetherCurrents { get; set; } = true;

    /// <summary>搜尋半徑（公尺）。⚠️ 讀取時夾在 5～200，舊設定檔裡的 0 不會讓它什麼都找不到。</summary>
    public float MaxDistance { get; set; } = 80f;

    /// <summary>名單重新整理間隔（毫秒）。⚠️ 讀取時夾在 100～5000。</summary>
    /// <remarks>
    /// 📌 這只影響「新目標多久會冒出來」。圓點的<b>位置</b>是 Mini-Mappingway 自己每一幀重算的，
    /// 與這個值無關。
    /// </remarks>
    public int RefreshMilliseconds { get; set; } = 500;
}

namespace TCToolbox;

/// <summary>「換 World 與副本區」面板的設定。</summary>
/// <remarks>
/// 📌 這個模組<b>預設關閉</b>（所有模組都是——啟用與否記在
/// <see cref="Configuration.EnabledModules"/>），所以既有使用者更新後不會多出任何東西。
/// </remarks>
public sealed class WorldTravelPanelConfig
{
    /// <summary>會讓角色移動的按鈕要按兩次才真的送出。</summary>
    /// <remarks>
    /// 🔴 <b>預設開啟</b>。跨界傳送要花好幾分鐘、中途不能操作角色，而按鈕之間只隔幾個像素——
    /// 誤觸的代價遠高於多按一下的成本。
    /// <para>
    /// 📌 只作用在會讓角色移動的按鈕。「中止」與「重新整理清單」永遠是按一次就生效：
    /// 前者只往安全方向走，後者根本不碰角色。
    /// </para>
    /// </remarks>
    public bool ConfirmTravel { get; set; } = true;

    /// <summary>面板下方顯示「本 DC 各世界最低價」摘要。</summary>
    /// <remarks>
    /// 📌 <b>預設開啟</b>：模組本身已經是預設關閉的 opt-in，這一層再關一次等於功能永遠不會被看到。
    /// 既有使用者的設定檔裡沒有這個鍵，Newtonsoft 會保留這裡的初始值，所以升級的人也吃得到。
    /// 🔴 開著也不會有任何自動行為：查詢只在使用者按下「查價」時送出，同一件道具 60 秒一次，
    /// 而且從頭到尾只顯示——不換世界、不掛售、不採購。
    /// </remarks>
    public bool ShowMarketPrices { get; set; } = true;

    /// <summary>面板下方顯示「在這個 World 要買的」：讀 Artisan 製作清單還缺的材料與它已有的最佳世界。</summary>
    /// <remarks>預設關。純顯示：不查價、不採購、不換世界；「在市場佈告板搜尋」只填字、不按搜尋。</remarks>
    public bool ShowArtisanShopping { get; set; }
}

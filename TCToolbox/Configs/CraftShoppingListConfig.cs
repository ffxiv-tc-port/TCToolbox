namespace TCToolbox;

/// <summary>「製作清單缺料與採買」的設定。</summary>
/// <remarks>
/// 📌 這個模組<b>預設關閉</b>（所有模組都是——啟用與否記在
/// <see cref="Configuration.EnabledModules"/>），所以既有使用者更新後不會多出任何行為。
/// <para>
/// 🔴 每一個預設值都選「不會讓使用者意外多花錢／多跑一趟」的那一邊：
/// 不遞迴展開（清單短、假設少）、只顯示缺料、「前往」要按兩次。
/// </para>
/// </remarks>
public sealed class CraftShoppingListConfig
{
    /// <summary>選中的 AllaganTools 製作清單 key；空字串＝還沒選。</summary>
    public string CraftListKey { get; set; } = string.Empty;

    /// <summary>
    /// 改用手打的清單名稱／key。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>這不是多餘的選項。</b>對方的 <c>AllaganTools.GetCraftLists</c> 實作刻意排除了
    /// 「預設製作清單」（<c>!c.CraftListDefault</c>），所以只用那一張的人在下拉選單裡
    /// 一張都看不到。而 <c>GetCraftItems</c> 走的是 <c>GetListByKeyOrName</c>，<b>名稱也收</b>。
    /// </remarks>
    public bool UseCustomListName { get; set; }

    /// <summary>手打的清單名稱或 key。</summary>
    public string CustomListName { get; set; } = string.Empty;

    /// <summary>
    /// 中間材料本身也有配方時，要不要繼續往下展開。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>預設關閉（只展開一層）</b>，理由有兩個：
    /// ①展開到底的清單種類會暴增到看不完；
    /// ②遞迴展開<b>內含一個假設</b>——「所有中間材料都自己做」。使用者打算買現成中間材料時，
    /// 那個假設會把需求算成一堆他根本不需要的原料，而畫面上看不出那是假設。
    /// </remarks>
    public bool ExpandIntermediates { get; set; }

    /// <summary>只顯示「還缺」與「不知道有幾個」的材料。</summary>
    /// <remarks>
    /// 🔑 「不知道」<b>永遠不被這個開關篩掉</b>——它是最需要使用者看一眼的一類，
    /// 被篩掉的話畫面會誠懇地宣稱「什麼都不缺」。
    /// </remarks>
    public bool OnlyShowMissing { get; set; } = true;

    /// <summary>把水晶／碎晶／晶簇從表上藏起來。</summary>
    /// <remarks>
    /// 📌 <b>預設不藏。</b>它們確實是配方材料（在配方表裡與其他材料同格），
    /// 藏起來是「我知道我不缺它」的個人偏好，不該是出廠預設。
    /// </remarks>
    public bool HideCrystals { get; set; }

    /// <summary>「前往」時允許 Lifestream 用飛行坐騎跑最後一段。</summary>
    /// <remarks>📌 不能飛或起飛失敗時 Lifestream 自己退回地面路線，所以開著是安全的。</remarks>
    public bool AllowFlying { get; set; } = true;

    /// <summary>「前往」要按兩次才真的出發。</summary>
    /// <remarks>
    /// 🔴 <b>預設開啟。</b>表上每一列都有一顆「前往」，彼此只隔幾個像素，
    /// 而按錯的代價是角色被傳到另一座城市去（還得自己走回來）。
    /// <para>📌 「中止」永遠是按一次就生效——它只往安全方向走。</para>
    /// </remarks>
    public bool ConfirmTravel { get; set; } = true;
}

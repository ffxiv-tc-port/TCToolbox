namespace TCToolbox;

/// <summary>老主顧交易總覽。</summary>
/// <remarks>
/// 📌 這裡沒有「總開關」欄位——<b>模組本身的啟用狀態就是總開關</b>
/// （與 TC Toolbox 其他模組一致：預設關閉，由使用者自己勾）。
/// </remarks>
public sealed class CustomDeliveriesOverviewConfig
{
    /// <summary>把老主顧的位置推給 Mappy 畫在地圖上（需要另外安裝 Mappy）。</summary>
    /// <remarks>
    /// 📌 預設 <see langword="true"/>：這是模組內的選項，模組本身仍然預設關閉。
    /// 沒裝 Mappy 就靜靜地什麼都不做。
    /// </remarks>
    public bool ShowOnMappy { get; set; } = true;

    /// <summary>只畫「本週還交得了」的老主顧。</summary>
    /// <remarks>
    /// 📌 預設 <see langword="true"/>：本週已經交滿的、或全體共用額度已經用完的，畫在地圖上
    /// 只會讓人白跑一趟。想看全部（含已交滿的）就把這格取消。
    /// ⚠️ 未解鎖的老主顧<b>兩種設定下都不畫</b>——那不是「今天還沒去」，是根本還去不了。
    /// </remarks>
    public bool OnlyWithRemainingDeliveries { get; set; } = true;

    /// <summary>Mappy 上的老主顧圖示 id。0＝用內建預設值。</summary>
    /// <remarks>
    /// 📌 預設值不是猜的：<c>MapSymbol</c> 表第 69 列的 <c>Icon</c> 欄就是它，
    /// 對應的 <c>PlaceName</c> 台服繁中逐字是「老主顧交易」。
    /// 🔴 <b>初始器一定要是 0，不能寫具體常數。</b>寫具體常數的話任何一次 Save 就把當下的編號烙死，
    /// 之後修正 <c>Default…IconId</c> 對<b>所有既有使用者靜默無效</b>，哨兵機制形同虛設。
    /// </remarks>
    public uint MappyIconId { get; set; }
}

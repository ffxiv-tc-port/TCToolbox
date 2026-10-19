namespace TCToolbox;

/// <summary><see cref="Modules.AetherCurrentTracker"/> 的設定。</summary>
public sealed class AetherCurrentTrackerConfig
{
    /// <summary>在畫面上把風脈泉的位置畫出來。</summary>
    public bool ShowWorldOverlay { get; set; } = true;

    /// <summary>畫面外的目標在螢幕邊緣畫箭頭指出方向。</summary>
    public bool ShowOffScreenArrows { get; set; } = true;

    /// <summary>已共鳴的也畫（細灰圈）。</summary>
    /// <remarks>
    /// 📌 預設 <see langword="false"/>：已經共鳴過的對「還差哪幾個」沒有幫助，
    /// 全畫出來只會讓畫面變亂。想確認位置的人再打開。
    /// </remarks>
    public bool ShowResonatedInOverlay { get; set; }

    /// <summary>把未共鳴的風脈泉推給 Mappy 畫在地圖上（需要另外安裝 Mappy）。</summary>
    /// <remarks>
    /// 📌 預設 <see langword="true"/>：這是<b>模組內</b>的選項，而模組本身照 TC Toolbox 慣例
    /// 預設關閉——也就是說使用者主動打開這個模組時，地圖標記是跟著一起來的。
    /// 沒裝 Mappy 就靜靜地什麼都不做。
    /// </remarks>
    public bool ShowOnMappy { get; set; } = true;

    /// <summary>Mappy 上的風脈泉圖示 id。0＝用內建預設值。</summary>
    /// <remarks>
    /// ⚠️ 做成可設定的理由：圖示的<b>存在</b>可以離線驗證，<b>長什麼樣子</b>不行。
    /// 🔴 <b>初始器一定要是 0，不能寫具體常數。</b>寫具體常數的話任何一次 Save 就把當下的編號烙死，
    /// 之後修正 <c>Default…IconId</c> 對<b>所有既有使用者靜默無效</b>，哨兵機制形同虛設。
    /// </remarks>
    public uint MappyIconId { get; set; }
}

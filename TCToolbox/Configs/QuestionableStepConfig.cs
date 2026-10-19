namespace TCToolbox;

/// <summary>Questionable 目前步驟顯示到 Mappy 與資訊列。</summary>
/// <remarks>
/// 📌 這裡沒有「啟用」欄位是刻意的：模組本身預設就是關的（所有模組都是），
/// 不需要第二段開關。下面兩格是「開了之後要顯示哪些東西」。
/// </remarks>
public sealed class QuestionableStepConfig
{
    /// <summary>在 Mappy 地圖上標出目前這一步的位置。</summary>
    /// <remarks>⚠️ 有些步驟本來就沒有座標（切換職業、等你手動繼續…），那時候地圖上不會有標記。</remarks>
    public bool ShowMarker { get; set; } = true;

    /// <summary>在伺服器資訊列顯示目前的互動類型。</summary>
    /// <remarks>📌 沒在跑任務時整格藏起來，不會一直佔著資訊列的位置。</remarks>
    public bool ShowDtr { get; set; } = true;

    /// <summary>目前步驟的圖示 id。0＝用內建預設值。</summary>
    /// <remarks>
    /// 🔴 <b>初始器一定要是 0，不能寫具體常數。</b>寫具體常數的話任何一次 Save 就把當下的編號烙死，
    /// 之後修正 <c>DefaultStepIconId</c> 對<b>所有既有使用者靜默無效</b>。
    /// </remarks>
    public uint StepIconId { get; set; }
}

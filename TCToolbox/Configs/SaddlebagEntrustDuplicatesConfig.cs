using System;

namespace TCToolbox;

/// <summary>
/// 陸行鳥鞍囊：寄放重複道具。
/// ⚠️ 沒有「啟用」欄位是刻意的：模組本身預設就是關的，而且開著也要按按鈕才會動。
/// </summary>
public sealed class SaddlebagEntrustDuplicatesConfig
{
    /// <summary>
    /// 每寄放一件之間的最短間隔（毫秒）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 每一件都要「開右鍵選單 → 點項目 → 等伺服器生效」三步，本來就不快。
    /// 這個間隔是在那三步<b>之外</b>再多留的餘裕，預設 500ms。
    /// </remarks>
    public int StepIntervalMs = 500;

    /// <summary>結束時在聊天欄報告結果（記錄一律會寫，不受這格影響）。</summary>
    public bool NotifyOnFinish = true;

    /// <summary>
    /// 把「優質」與「普通品」當成兩款不同的道具。
    /// </summary>
    /// <remarks>
    /// 📌 預設 <c>true</c>，與上游 PandorasBox <c>EntrustChocoboDuplicates</c> 不同
    /// （它只比對 <c>ItemId</c>，而 HQ 資訊在 <c>Flags</c> 裡，所以優質品會被當成普通品的重複）。
    /// 保守的方向是「少搬幾件」：搬錯的話要自己一件一件從鞍囊拿回來。
    /// </remarks>
    public bool MatchQuality = true;
}

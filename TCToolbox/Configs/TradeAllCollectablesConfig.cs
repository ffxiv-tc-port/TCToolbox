using TCToolbox.Core;

namespace TCToolbox;

/// <summary>
/// 收藏品一鍵全交。
/// ⚠️ 這裡沒有「啟用」欄位是刻意的：模組本身預設就是關的，而且開著也要按按鈕才會動
/// （<see cref="TCToolbox.Core.TcModule.IsManualTrigger"/>），不需要第二段開關。
/// </summary>
public sealed class TradeAllCollectablesConfig
{
    /// <summary>
    /// 每交一件之間的最短間隔（毫秒）。
    /// </summary>
    /// <remarks>
    /// 📌 預設 500ms。交易是真的送到伺服器的請求，而且伺服器要一段時間才會把背包更新回來——
    /// 間隔太短會讓「這一下到底有沒有生效」的判斷失準，也讓使用者來不及按停止。
    /// </remarks>
    public int StepIntervalMs = 500;

    /// <summary>結束時在聊天欄報告結果（記錄一律會寫，不受這格影響）。</summary>
    public bool NotifyOnFinish = true;
}

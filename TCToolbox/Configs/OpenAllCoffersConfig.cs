namespace TCToolbox;

/// <summary>箱類「全部開啟」。</summary>
public sealed class OpenAllCoffersConfig
{
    /// <summary>
    /// 每開一件之間的最短間隔（毫秒）。
    /// </summary>
    /// <remarks>
    /// 📌 預設 500ms。開箱是真的送到伺服器的道具使用，而且每一件都可能跳出獲得道具的訊息；
    /// 太快既沒有好處，也讓使用者來不及按停止。
    /// </remarks>
    public int StepIntervalMs = 500;

    /// <summary>結束時在聊天欄報告結果（記錄一律會寫，不受這格影響）。</summary>
    public bool NotifyOnFinish = true;
}

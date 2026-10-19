using TCToolbox.Core;

namespace TCToolbox;

/// <summary>背包堆疊合併。</summary>
/// <remarks>
/// 📌 這裡沒有「啟用」欄位是刻意的：模組本身預設就是關的，而且開著也要按按鈕才會動
/// （<see cref="TCToolbox.Core.TcModule.IsManualTrigger"/>），不需要第二段開關。
/// </remarks>
public sealed class AutoMergeConfig
{
    /// <summary>
    /// 每一次搬移之間的最短間隔（毫秒）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 這是<b>會真的送到伺服器</b>的搬移，不是本機動畫。預設 300ms 是保守值：
    /// 合併 20 格要 6 秒，慢得看得出來，但也因此使用者來得及按「停止合併」。
    /// </remarks>
    public int StepIntervalMs = 300;

    /// <summary>結束時在聊天欄報告結果（記錄一律會寫，不受這格影響）。</summary>
    public bool NotifyOnFinish = true;
}

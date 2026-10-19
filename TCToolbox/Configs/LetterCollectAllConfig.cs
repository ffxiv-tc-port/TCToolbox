using TCToolbox.Core;

namespace TCToolbox;

/// <summary>信箱一鍵收取。</summary>
/// <remarks>
/// 📌 這裡沒有「啟用」欄位是刻意的：模組本身預設就是關的，而且開著也要按按鈕才會動
/// （<see cref="TCToolbox.Core.TcModule.IsManualTrigger"/>），不需要第二段開關。
/// </remarks>
public sealed class LetterCollectAllConfig
{
    /// <summary>
    /// 每一步之間的最短間隔（毫秒）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 每一步都是真的送到伺服器的操作。預設 500ms 是保守值：慢得看得出來，
    /// 但也因此使用者來得及按「停止」。
    /// </remarks>
    public int StepIntervalMs = 500;

    /// <summary>
    /// 自動按掉收取途中出現的確認框。
    /// </summary>
    /// <remarks>
    /// 📌 預設開啟，但作用範圍被壓到很窄：<b>只在本模組的佇列正在跑，而且信箱與信件視窗都開著時</b>
    /// 才會按。也就是說時間窗只有使用者自己按下按鈕之後的那幾秒。
    /// ⚠️ 關掉的話，真的跳出確認框時那一輪會停在原地直到逾時。
    /// 無論按不按，確認框的文字一律寫進記錄——台服到底會不會跳、跳哪一句，離線查不出來。
    /// </remarks>
    public bool AutoConfirm = true;

    /// <summary>結束時在聊天欄報告結果（記錄一律會寫，不受這格影響）。</summary>
    public bool NotifyOnFinish = true;
}

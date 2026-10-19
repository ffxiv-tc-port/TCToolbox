namespace TCToolbox;

/// <summary><see cref="Modules.AutoCrafterGathererManual"/> 的設定。</summary>
public sealed class AutoCrafterGathererManualConfig
{
    /// <summary>
    /// 兩次檢查之間隔幾秒。
    /// </summary>
    /// <remarks>
    /// 📌 指南的加成狀態長達 30 分鐘，所以這個值再大都不會漏掉太久；
    /// 預設 10 秒是為了「剛換職／剛用完」時反應得夠快。
    /// 一次檢查的成本是幾個欄位讀取，只有真的該用時才會去查背包數量。
    /// </remarks>
    public int PollSeconds { get; set; } = 10;

    /// <summary>用掉一本指南時在聊天視窗說一聲。</summary>
    public bool NotifyOnUse { get; set; } = true;
}

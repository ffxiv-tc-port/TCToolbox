namespace TCToolbox;

/// <summary><see cref="Modules.PluginUpdateNotice"/> 的設定。</summary>
public sealed class PluginUpdateNoticeConfig
{
    /// <summary>檢查間隔（分鐘）。值域 30～180，模組讀取時會 Clamp。</summary>
    public int IntervalMinutes { get; set; } = 60;
}

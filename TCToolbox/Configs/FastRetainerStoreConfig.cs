namespace TCToolbox;

/// <summary>僱員存取加速（同款全部）。</summary>
public sealed class FastRetainerStoreConfig
{
    /// <summary>每搬一格之間的最短間隔（毫秒）。每一格都是真的送到伺服器的命令。</summary>
    public int StepIntervalMs = 200;

    /// <summary>結束時在聊天欄報告（記錄一律會寫，不受這格影響）。</summary>
    public bool NotifyOnFinish = true;
}

namespace TCToolbox;

public sealed class AutoRefreshPartyFinderConfig
{
    /// <summary>自動刷新間隔（秒）。</summary>
    public int IntervalSeconds = 30;

    /// <summary>清單自己更新過就重新計時。</summary>
    public bool OnlyWhenIdle = true;

    /// <summary>在招募板上方顯示倒數與立即刷新鈕。</summary>
    public bool ShowCountdown = true;
}

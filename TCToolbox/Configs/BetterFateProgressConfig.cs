namespace TCToolbox;

/// <summary>共鬥 F.A.T.E. 進度總覽。</summary>
public sealed class BetterFateProgressConfig
{
    /// <summary>開窗時自動重新整理一次。</summary>
    public bool AutoRefreshOnOpen { get; set; } = true;

    /// <summary>每筆成就進度查詢之間的最短間隔（毫秒）。台服速率限制未知，預設保守。</summary>
    public int RequestIntervalMs { get; set; } = 1000;
}

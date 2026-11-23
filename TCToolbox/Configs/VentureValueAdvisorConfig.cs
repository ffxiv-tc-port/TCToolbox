namespace TCToolbox;

/// <summary><see cref="Modules.VentureValueAdvisor"/> 的設定。</summary>
public sealed class VentureValueAdvisorConfig
{
    /// <summary>展開一位僱員時列出前幾名探險。</summary>
    public int TopCount { get; set; } = 5;

    /// <summary>行情資料超過幾小時沒人上傳就畫成淡色（冷門物品常是幾十天前的資料）。</summary>
    public int StaleHours { get; set; } = 72;
}

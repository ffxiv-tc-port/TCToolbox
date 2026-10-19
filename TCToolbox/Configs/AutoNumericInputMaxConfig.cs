namespace TCToolbox;

/// <summary><see cref="Modules.AutoNumericInputMax"/> 的設定。</summary>
public sealed class AutoNumericInputMaxConfig
{
    /// <summary>把上限＝99 的數字輸入框放寬到這個上限（夾在 100–9999）。</summary>
    public int MaxValue = 999;

    /// <summary>
    /// 順便把值預先填到上限。
    /// </summary>
    /// <remarks>
    /// 🔴 預設 <c>false</c>：自動把數量填到最大是誤買／誤丟的地雷。只放大上限（讓你能自己打更大的數字）
    /// 沒有這個風險，要「開框就是最大」的人再自己打開。
    /// </remarks>
    public bool AutoFillToMax;
}

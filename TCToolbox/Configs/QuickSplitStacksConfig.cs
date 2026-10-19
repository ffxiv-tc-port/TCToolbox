namespace TCToolbox;

/// <summary><see cref="Modules.QuickSplitStacks"/> 的設定。</summary>
public sealed class QuickSplitStacksConfig
{
    /// <summary>拆完在聊天視窗說一聲。</summary>
    public bool NotifyOnSplit { get; set; } = true;

    /// <summary>下次開啟輸入框時沿用上次填的數量。</summary>
    public bool RememberAmount { get; set; } = true;

    /// <summary>上次填的數量。</summary>
    /// <remarks>
    /// 📌 開輸入框時仍會夾在 <c>1 .. 這一疊數量-1</c> 之間，
    /// 所以這裡就算存著一個對當前道具不合理的值也不會送出不合法的數量。
    /// </remarks>
    public int LastAmount { get; set; } = 1;
}

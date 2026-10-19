namespace TCToolbox;

/// <summary>
/// 一組可綁定的熱鍵（主鍵＋修飾鍵）。
/// </summary>
/// <remarks>
/// 🔴 <b>預設是「未綁定」（<see cref="KeyCode"/> 為 0）。</b>新增的熱鍵一律不預先佔用按鍵——
/// 使用者的鍵盤上每一顆鍵都可能已經綁了遊戲技能，替他決定綁哪一顆就是替他改遊戲行為。
/// </remarks>
public sealed class HotkeyConfig
{
    /// <summary>主鍵的 <c>VirtualKey</c> 值。0＝未綁定。</summary>
    public int KeyCode;

    /// <summary>需要按著 CTRL。</summary>
    public bool Ctrl;

    /// <summary>需要按著 SHIFT。</summary>
    public bool Shift;

    /// <summary>需要按著 ALT。</summary>
    public bool Alt;
}

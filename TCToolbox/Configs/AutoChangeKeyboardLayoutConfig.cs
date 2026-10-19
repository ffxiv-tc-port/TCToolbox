namespace TCToolbox;

/// <summary><see cref="Modules.AutoChangeKeyboardLayout"/> 的設定。</summary>
/// <remarks>
/// ⚠️ 兩個欄位都是 <c>ushort</c> 的鍵盤配置語言 id（HKL 低字組）。
/// 🔴 預設 0＝「不切換」，而且是<b>持久的哨兵值</b>：模組看到 0 就不動作。
/// <b>兩欄相同（含都是 0）也一律視為不切換</b>：舊版會把兩欄烙成同一個值，那些人因此自動回到 no-op；
/// 設定畫面另有「（不切換）」選項可寫回 0。
/// </remarks>
public sealed class AutoChangeKeyboardLayoutConfig
{
    /// <summary>文字輸入框取得焦點時要切到的配置語言 id。</summary>
    public ushort FocusLayoutLangID;

    /// <summary>離開文字輸入框時要切回的配置語言 id。</summary>
    public ushort UnfocusLayoutLangID;
}

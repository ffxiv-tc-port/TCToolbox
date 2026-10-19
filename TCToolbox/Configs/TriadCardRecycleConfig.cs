namespace TCToolbox;

/// <summary>幻卡回收清單。只有顯示偏好，沒有任何會改變遊戲行為的設定。</summary>
public sealed class TriadCardRecycleConfig
{
    /// <summary>
    /// 連「幻卡列表還沒收錄」的卡也列在清單上。
    /// </summary>
    /// <remarks>
    /// 📌 預設 <c>false</c>：還沒收錄的卡應該拿去用掉而不是回收，列出來只會增加誤點的機會。
    /// 打開純粹是方便看看手上有什麼——<b>不會讓那些卡變成可以按的目標</b>，
    /// 回收按鈕送出的一律是遊戲自己在回收視窗上選定的那一張。
    /// </remarks>
    public bool ShowUnrecorded { get; set; }
}

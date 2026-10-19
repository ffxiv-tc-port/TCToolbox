namespace TCToolbox;

/// <summary>招募詳細視窗自動加入。</summary>
public sealed class AutoJoinPartyFinderConfig
{
    /// <summary>
    /// 詳細視窗開啟後等這麼久（毫秒）才按下「加入」。
    /// </summary>
    /// <remarks>
    /// 📌 預設 300ms：一方面讓視窗把內容填好（招募資訊是非同步回來的），
    /// 另一方面這也是使用者「按下取消鍵反悔」的時間窗。
    /// </remarks>
    public int DelayMs = 300;

    /// <summary>
    /// 取消鍵的 <c>VirtualKey</c> 值（0＝不使用）。按著它點開招募時，這一則不會自動加入。
    /// </summary>
    /// <remarks>
    /// 📌 預設 0（不使用）：開了這個模組的人要的就是「點開就加入」。
    /// 想保留「純粹看內容」的操作方式再自己設一顆。
    /// </remarks>
    public int CancelKeyCode;

    /// <summary>
    /// 跳過密碼招募。
    /// </summary>
    /// <remarks>
    /// 📌 預設 <c>true</c>：密碼招募按下去只會跳出輸入密碼的視窗，我們不會也不該去填它。
    /// ⚠️ 判斷依據是 <c>AgentLookingForGroup.LastViewedListing.JoinConditionFlags</c> 的 bit1，
    /// 這個讀法<b>無法離線證明</b>；兩個方向的失敗都設計成無害
    /// （多擋了＝這一則要自己按、少擋了＝跳出密碼視窗自己關）。
    /// </remarks>
    public bool SkipPrivate = true;

    /// <summary>
    /// 順便按掉「確定要加入…」的確認框。
    /// </summary>
    /// <remarks>
    /// 🔴 只在「我們剛按過加入的 5 秒內、而且按下去那一刻畫面上沒有其他確認框、
    /// 而且招募詳細視窗還開著」三個條件同時成立時才代按。
    /// 少了這層因果連結就變成「看到 Yes/No 就按是」，那是完全不同的風險等級。
    /// </remarks>
    public bool ConfirmYesNo = true;

    /// <summary>按下加入時在聊天欄顯示一行（記錄一律會寫，不受這格影響）。</summary>
    /// <remarks>
    /// 📌 預設 <c>true</c>：這個功能會在使用者沒按任何按鈕的情況下把他丟進一支隊伍，
    /// 一行訊息是他唯一看得到的「是這個外掛做的」證據。
    /// </remarks>
    public bool NotifyInChat = true;
}

namespace TCToolbox;

/// <summary>點擊移動。</summary>
public sealed class ClickToMoveConfig
{
    /// <summary>
    /// 觸發用修飾鍵的 <c>VirtualKey</c> 值（0＝不需要修飾鍵，裸左鍵就觸發）。
    /// </summary>
    /// <remarks>
    /// 🔴 預設 <c>SHIFT</c>（16）而不是 0，這是刻意的：FFXIV 的左鍵拖曳是旋轉鏡頭、
    /// 左鍵點擊是選取目標。裸左鍵的話每一次轉鏡頭放開都會發一次尋路。
    /// 上游 CBT 就是裸左鍵，所以在 FFXIV 裡實際上很難用。
    /// <para>📌 想要真正的「點哪走哪」可以自己改成「無」——拖曳排除的防線仍然生效。</para>
    /// </remarks>
    public int ModifierKeyCode = 16;

    /// <summary>
    /// 允許 vnavmesh 使用飛行路徑。
    /// </summary>
    /// <remarks>
    /// 📌 預設 <c>false</c>，與 F.A.T.E. 總覽的導航一致：在未解鎖飛行的區域開飛行會讓
    /// vnavmesh 算不出路徑，而失敗的樣子是「點了沒反應」，比走路慢難察覺得多。
    /// </remarks>
    public bool AllowFly;

    /// <summary>每次開始移動時在聊天欄顯示一行（記錄一律會寫，不受這格影響）。</summary>
    /// <remarks>
    /// 📌 預設 <c>true</c>：這個功能會讓角色在沒有任何視窗開著的情況下跑起來，
    /// 一行「前往…（/tcstop 可停下）」是使用者唯一看得到的「是我自己觸發的」證據，
    /// 也順便把停止指令講出來。
    /// </remarks>
    public bool NotifyOnMove = true;
}

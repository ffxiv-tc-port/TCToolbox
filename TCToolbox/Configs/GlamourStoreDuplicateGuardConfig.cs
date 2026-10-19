namespace TCToolbox;

/// <summary>投影台：攔截重複收納。</summary>
public sealed class GlamourStoreDuplicateGuardConfig
{
    /// <summary>
    /// 偵測到重複時直接幫忙按下確認框的「否」。
    /// </summary>
    /// <remarks>
    /// 📌 預設 <c>true</c>：這正是模組存在的理由，而模組本身預設是關的，所以不會有人被動生效。
    /// 🔴 <b>不論這格開或關，攔截／提醒一定會在聊天欄出現。</b>
    /// 靜默地把使用者的操作取消掉是最糟的失敗形式——他只會覺得「按了沒反應」。
    /// ⚠️ 關掉時只提醒、不動遊戲，確認框留給使用者自己決定。
    /// </remarks>
    public bool BlockConfirmation = true;

    /// <summary>
    /// 把「染色不同」的同款裝備視為不同幻影，不當成重複。
    /// </summary>
    /// <remarks>
    /// 📌 預設 <c>true</c>，與 <see cref="GlamourDuplicateCleanupConfig.DistinguishByDye"/> 一致：
    /// 同一件裝備染成兩個顏色在投影台裡是兩種可用的外觀，一律當重複會擋掉正當的收納。
    /// <para>⚠️ 優質／普通品則<b>刻意不分</b>——它們在投影台裡長得一模一樣，兩件都留就是浪費一格。</para>
    /// </remarks>
    public bool DistinguishByDye = true;
}

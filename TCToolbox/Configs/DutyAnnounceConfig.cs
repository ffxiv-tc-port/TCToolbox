using TCToolbox.Core;

namespace TCToolbox;

/// <summary>副本開始／結束播報。</summary>
public sealed class DutyAnnounceConfig
{
    /// <summary>副本開始時送出的訊息（空字串＝不送）。</summary>
    /// <remarks>📌 預設空字串：這個模組唯一的行為就是送使用者寫的字，沒寫就什麼都不該送。</remarks>
    public string StartMessage = string.Empty;

    /// <summary>
    /// 開始訊息要送到的頻道（<c>TextCommand</c> 表的列號）。
    /// </summary>
    /// <remarks>
    /// 🔴 預設是<b>默語</b>（116）而不是上游的小隊頻道：預設值送出的東西必須是
    /// 「就算設錯也不會打擾任何人」的。要送給隊友的人自己改一格就好，
    /// 反方向（預設就往公開頻道送）出錯的代價是每一場副本對整隊洗一次版。
    /// </remarks>
    public uint StartChannelRow = TCToolbox.Core.TextCommands.ChatChannelRows.Echo;

    /// <summary>副本通關時送出的訊息（空字串＝不送）。</summary>
    public string EndMessage = string.Empty;

    /// <summary>結束訊息要送到的頻道（<c>TextCommand</c> 表的列號）。預設默語，理由同上。</summary>
    public uint EndChannelRow = TCToolbox.Core.TextCommands.ChatChannelRows.Echo;

    /// <summary>
    /// 事件觸發之後先等這麼久（毫秒）才送出。
    /// </summary>
    /// <remarks>
    /// 📌 預設 1000ms：通關那一瞬間畫面還在演出、系統訊息也正在刷，太早送出容易被淹掉。
    /// ⚠️ 等待期間離開副本區域的話這次播報會被取消——否則那句話會送到副本外面去。
    /// </remarks>
    public int DelayMs = 1_000;
}

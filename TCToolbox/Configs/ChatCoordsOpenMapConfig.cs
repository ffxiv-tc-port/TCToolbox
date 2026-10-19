using System.Collections.Generic;

namespace TCToolbox;

/// <summary>聊天座標自動開地圖。</summary>
public sealed class ChatCoordsOpenMapConfig
{
    /// <summary>
    /// 不處理的頻道（<see cref="Dalamud.Game.Text.XivChatType"/> 的數值）。
    /// </summary>
    /// <remarks>
    /// 🔴 刻意存<b>黑名單</b>而不是白名單：空集合＝全部頻道都處理，
    /// 所以之後在模組裡多列一個頻道時，既有使用者會自動吃到它。
    /// 反過來存白名單的話，新頻道對所有既有使用者都是靜默關閉的。
    /// </remarks>
    public HashSet<ushort> IgnoredChannels { get; set; } = [];

    /// <summary>同一個座標在幾秒內不重複開啟。</summary>
    public int DedupeSeconds { get; set; } = 5;

    /// <summary>副本中不自動開地圖。</summary>
    public bool SkipWhileBoundByDuty { get; set; } = true;

    /// <summary>開啟後在聊天視窗留一行（記錄一律會寫，不受這格影響）。</summary>
    public bool AnnounceInChat { get; set; }
}

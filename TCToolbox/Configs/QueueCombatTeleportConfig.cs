namespace TCToolbox;

/// <summary>戰鬥中排隊傳送。</summary>
public sealed class QueueCombatTeleportConfig
{
    /// <summary>脫離戰鬥後延遲多久再傳送（毫秒）。</summary>
    /// <remarks>📌 預設 500ms：戰鬥剛結束常常還在撿取／對話，留一點延遲比較不會打斷。</remarks>
    public int DelayMs { get; set; } = 500;

    /// <summary>在聊天欄提示排隊與傳送（記錄一律會寫，不受這格影響）。</summary>
    public bool AnnounceInChat { get; set; } = true;
}

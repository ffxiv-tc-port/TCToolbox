namespace TCToolbox;

public sealed class WeeklyBingoClickToOpenConfig
{
    /// <summary>成功開啟副本時在聊天欄顯示一行。</summary>
    public bool NotifyOnOpen = true;

    /// <summary>格子對不到副本時在聊天欄說明原因（關掉的話點下去就完全沒反應，只有 log 有記錄）。</summary>
    public bool NotifyWhenUnresolved = true;
}

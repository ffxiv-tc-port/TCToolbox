namespace TCToolbox;

public sealed class AutoGysahlGreensConfig
{
    /// <summary>剩餘時間低於此分鐘數時自動餵食。</summary>
    public int ThresholdMinutes = 5;

    /// <summary>餵食後在聊天欄顯示訊息。</summary>
    public bool NotifyOnFeed = true;
}

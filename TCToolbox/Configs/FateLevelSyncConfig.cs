namespace TCToolbox;

/// <summary>F.A.T.E. 自動等級同步。</summary>
public sealed class FateLevelSyncConfig
{
    /// <summary>戰鬥中不動作。</summary>
    /// <remarks>📌 預設 <c>false</c>：F.A.T.E. 本來就是一進去就在打，等到脫離戰鬥往往已經打完了。</remarks>
    public bool SkipInCombat { get; set; }

    /// <summary>
    /// 送出「開啟」之後仍未同步時，改用無參數的切換再試一次。
    /// </summary>
    /// <remarks>
    /// 🔴 這件事之所以安全，唯一的理由是<b>送出前剛確認過 <c>SyncedFateId != 目前的 F.A.T.E.</c></b>
    /// （也就是現在確實是關的），所以切換的方向必然是「開」。
    /// 拿掉那道確認的話，這個選項就會變成「有機會把已經同步的狀態解除掉」。
    /// </remarks>
    public bool RetryWithToggle { get; set; } = true;

    /// <summary>送出指令後等多久再確認結果（毫秒）。</summary>
    public int VerifyDelayMs { get; set; } = 2500;

    /// <summary>同步成功時在聊天視窗留一行（記錄一律會寫，不受這格影響）。</summary>
    public bool AnnounceInChat { get; set; } = true;
}

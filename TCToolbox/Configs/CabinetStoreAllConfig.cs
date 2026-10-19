namespace TCToolbox;

/// <summary><see cref="Modules.CabinetStoreAll"/> 的設定。</summary>
public sealed class CabinetStoreAllConfig
{
    /// <summary>兩件之間額外等多久（毫秒）。</summary>
    /// <remarks>
    /// 📌 主要的節奏來自「等伺服器確認」那一步，這裡只是額外餘裕。
    /// ⚠️ 台服的封包速率限制沒有公開數字，DailyRoutines 用的 100ms 是國服經驗值。
    /// </remarks>
    public int IntervalMs { get; set; } = 300;

    /// <summary>等伺服器把「已在收藏櫃」旗標翻過來的上限（毫秒），逾時整條中止。</summary>
    public int ConfirmTimeoutMs { get; set; } = 5_000;
}

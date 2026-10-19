namespace TCToolbox;

/// <summary>
/// 自動繳交道具時，同一款道具有 NQ／HQ 兩種可選時要先挑哪一種。
/// </summary>
/// <remarks>
/// 🔴 這是「偏好」不是「限定」：偏好的那一種在候選清單裡找不到時會退回另一種，
/// 不會因為挑不到就卡住不交。
/// </remarks>
public enum HandInQualityPreference
{
    /// <summary>不挑，照遊戲給的候選順序取第一個（＝本設定加入之前的行為）。</summary>
    None = 0,

    /// <summary>優先挑優質（HQ）；沒有 HQ 候選時退回普通品。</summary>
    PreferHighQuality = 1,

    /// <summary>優先挑普通品（NQ）；沒有 NQ 候選時退回 HQ。</summary>
    PreferNormalQuality = 2,
}

public sealed class AutoRequestItemSubmitConfig
{
    /// <summary>每一步（填一格／按一次鈕）之間的最短間隔（毫秒）。</summary>
    public int DelayMs = 500;

    /// <summary>自動確認「確定要交易優質道具嗎？」這扇確認框。</summary>
    public bool ConfirmHighQuality = true;

    /// <summary>
    /// NQ／HQ 都符合要求時先挑哪一種。預設 <see cref="HandInQualityPreference.None"/>，
    /// 也就是維持「取遊戲給的第一個候選」這個既有行為。
    /// </summary>
    public HandInQualityPreference QualityPreference = HandInQualityPreference.None;
}

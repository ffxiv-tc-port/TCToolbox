namespace TCToolbox;

/// <summary><see cref="Modules.LargerIMECandidates"/> 的設定。</summary>
public sealed class LargerIMECandidatesConfig
{
    /// <summary>候選字清單容器的縮放倍率。</summary>
    /// <remarks>
    /// 🔴 <b>預設 1.0 ＝行為完全不變。</b>新增設定一律沿用現行行為當預設，
    /// 要放大請自己把滑桿拉大。
    /// <para>
    /// 📌 實際生效值會在使用點被夾在 1.0~3.0：滑桿可以 Ctrl+點擊鍵入範圍外的值，
    /// 手改設定檔也會持久生效，光靠滑桿範圍擋不住。
    /// </para>
    /// </remarks>
    public float Scale { get; set; } = 1.0f;
}

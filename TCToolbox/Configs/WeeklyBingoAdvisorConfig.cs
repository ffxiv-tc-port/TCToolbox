namespace TCToolbox;

/// <summary><see cref="Modules.WeeklyBingoAdvisor"/> 的設定。</summary>
public sealed class WeeklyBingoAdvisorConfig
{
    /// <summary>打開天書時自動開啟建議面板，關掉天書時一起收起來。</summary>
    public bool FollowAddon = true;

    /// <summary>標出「副本已完成、印花還沒貼」的格子。</summary>
    public bool HighlightFreeStickers = true;

    /// <summary>每張表最多列幾個建議；<c>0</c>＝全部列出。</summary>
    public int TopCount = 5;

    /// <summary>建議的排序依據：<c>0</c>／<c>1</c>／<c>2</c>＝依貼出 1／2／3 條故事線的機率變化排序。</summary>
    public int RankMetric;

    /// <summary>「下一個印花貼哪一格」表多一欄「跑一輪」：按一下就請 AutoDuty 用劇情輔助器跑那個副本一輪。</summary>
    /// <remarks>預設關：本模組在預設啟用名單裡，那份名單只准放純顯示的模組。</remarks>
    public bool RunWithAutoDuty;
}

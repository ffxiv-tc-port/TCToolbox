namespace TCToolbox;

public sealed class MarkerInPartyListConfig
{
    /// <summary>疊圖標記邊長（未乘介面縮放）。</summary>
    public float IconSize = 24f;

    /// <summary>相對隊員欄位左上角的偏移。</summary>
    public float OffsetX = 40f;

    public float OffsetY = 4f;

    /// <summary>有標記時隱藏小隊列表原生的隊員序號。</summary>
    public bool HideMemberNumbers = true;
}

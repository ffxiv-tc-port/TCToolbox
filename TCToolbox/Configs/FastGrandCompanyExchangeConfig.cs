namespace TCToolbox;

/// <summary>軍票商店快速交換。記住上次輸入的道具名與數量。</summary>
public sealed class FastGrandCompanyExchangeConfig
{
    /// <summary>上次輸入的道具名（可為片段）。</summary>
    public string ItemName = string.Empty;

    /// <summary>要交換的數量；-1＝可負擔上限。預設 1（不預設為花光全部軍票）。</summary>
    public int Count = 1;
}

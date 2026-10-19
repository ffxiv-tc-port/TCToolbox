namespace TCToolbox;

public sealed class GlamourSetRetrieveConfig
{
    /// <summary>只取投影台裡湊得齊整組的裝備組合（關閉後會拆散不完整的組合）。</summary>
    public bool OnlyCompleteSets = true;

    /// <summary>跳過已染色的幻影（染過色的通常是特地調過的外觀）。</summary>
    public bool SkipDyedItems = true;
}

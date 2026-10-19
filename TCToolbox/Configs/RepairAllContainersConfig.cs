namespace TCToolbox;

/// <summary>一鍵全修（跨容器）。</summary>
public sealed class RepairAllContainersConfig
{
    /// <summary>
    /// 每個容器之間的最短間隔（毫秒）。
    /// </summary>
    /// <remarks>
    /// 📌 預設 400ms。每一步都是真的送到伺服器的修理要求（遊戲自己是一次一個容器），
    /// 一口氣連送七次既沒有好處，也讓人來不及按停止。
    /// </remarks>
    public int StepIntervalMs = 400;

    /// <summary>
    /// 使用暗物質自行修理時也接手。
    /// </summary>
    /// <remarks>
    /// 📌 預設 <c>true</c>：呼叫的是遊戲自己在「全部修理」按鈕背後跑的同一支函式，
    /// 自行修理與修理工只差在 <c>isNpc</c> 這個旗標，行為上沒有額外風險。
    /// 每一次自行修理都會佔用角色（<c>Occupied39</c>），流程會等上一次結束才送下一次。
    /// </remarks>
    public bool IncludeSelfRepair = true;

    /// <summary>結束時在聊天視窗回報（記錄一律會寫，不受這格影響）。</summary>
    public bool AnnounceInChat = true;
}

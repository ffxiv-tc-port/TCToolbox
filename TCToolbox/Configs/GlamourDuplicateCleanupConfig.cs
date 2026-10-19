namespace TCToolbox;

public sealed class GlamourDuplicateCleanupConfig
{
    /// <summary>
    /// 把「染色不同」的同款裝備視為不同幻影，不當成重複。
    /// ⚠️ 預設 <c>true</c>，與 DailyRoutines 的行為不同（它只看道具編號）——
    /// 同一件裝備染兩個顏色在投影台裡是兩種可用外觀，只看編號會把其中一種取走。
    /// 要完全比照 DR 就關掉它。
    /// </summary>
    public bool DistinguishByDye = true;
}

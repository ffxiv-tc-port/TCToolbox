namespace TCToolbox;

/// <summary><see cref="Modules.AutoFCWSDeliver"/> 的設定。</summary>
public sealed class AutoFCWSDeliverConfig
{
    /// <summary>
    /// 連續推進：一個階段的素材交完之後，自動選「推進工程進展」再開始交下一個階段，直到所有階段交完。
    /// </summary>
    /// <remarks>📌 預設關閉。最後的「完成」與回收成品另由 <see cref="CompleteAndCollect"/> 控制。</remarks>
    public bool ContinuousPhases { get; set; }

    /// <summary>
    /// 最後完成並回收成品：所有階段交完後，自動選「完成…」，過場後選「領取道具」並在回收確認框按「是」。
    /// </summary>
    /// <remarks>📌 預設關閉；只在 <see cref="ContinuousPhases"/> 也勾著時生效。</remarks>
    public bool CompleteAndCollect { get; set; }
}

namespace TCToolbox;

/// <summary>
/// 解除互動限制的逐項開關。影響範圍窄的預設開啟，會波及互動以外系統的預設關閉
/// （理由與呼叫端數量寫在 <see cref="Modules.OptimizedInteraction"/> 的型別註解）。
/// </summary>
public sealed class OptimizedInteractionConfig
{
    /// <summary>無視「目標處於視野之外」。</summary>
    public bool IgnoreViewRange = true;

    /// <summary>無視「目標被物件遮擋」。</summary>
    public bool IgnoreCameraBlocked = true;

    /// <summary>無視「目標位置過高過低」。</summary>
    public bool IgnoreTargetPosition = true;

    /// <summary>無視「距離太遠」。</summary>
    public bool IgnoreDistance = true;

    /// <summary>無視「跳躍中無法操作」。⚠️ 兩支狀態判定各有 49／46 個呼叫端，預設關閉。</summary>
    public bool IgnoreJumping;

    /// <summary>無視「騎乘／低空飛行中」。⚠️ 這支在事件腳本條件判定器裡，不在互動閘門上，預設關閉。</summary>
    public bool IgnoreMountFlight;
}

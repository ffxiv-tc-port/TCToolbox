namespace TCToolbox;

/// <summary>旗標指令（/tpflag、/gotoflag）。</summary>
public sealed class FlagCommandsConfig
{
    /// <summary>
    /// <c>/gotoflag</c> 允許 vnavmesh 使用飛行路徑。
    /// </summary>
    /// <remarks>
    /// 📌 預設 <c>false</c>，與其他導航呼叫端一致：在未解鎖飛行的區域開飛行會讓
    /// vnavmesh 算不出路徑，而失敗的樣子是「指令下了沒反應」。
    /// </remarks>
    public bool AllowFly;
}

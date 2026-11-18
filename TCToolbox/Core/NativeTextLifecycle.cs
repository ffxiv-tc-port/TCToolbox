namespace TCToolbox.Core;

/// <summary>收到 addon 的 PostUpdate 時要做的事。</summary>
internal enum NativeUpdateAction
{
    /// <summary>什麼都不做（已停用，或 addon 還沒載入完）。</summary>
    Skip = 0,

    /// <summary>還沒掛節點、addon 就緒：掛上去。</summary>
    Attach = 1,

    /// <summary>節點就掛在這個 addon 上：更新內容。</summary>
    Apply = 2,

    /// <summary>節點掛在別的 addon 上＝那個 addon 被釋放時沒收到 PreFinalize；節點只能放著不碰。</summary>
    Mismatch = 3,
}

/// <summary>要收掉節點時（PreFinalize 或停用）要做的事。</summary>
internal enum NativeDetachAction
{
    None = 0,

    /// <summary>節點掛在這個仍存活的 addon 上：拆下並釋放。</summary>
    Detach = 1,

    /// <summary>事件來自同名的另一個 addon：不關這個節點的事。</summary>
    Ignore = 2,

    /// <summary>節點掛的 addon 已不在、又沒收到 PreFinalize：父節點已被釋放，拆＝寫進已釋放的記憶體。</summary>
    Abandon = 3,
}

/// <summary>
/// 掛在 addon 上的原生文字節點的生命週期判斷（純函式，離線可驗）。
/// </summary>
/// <remarks>
/// 不變式：<c>attached</c>（掛上去時的 addon 位址，只做身分比對、不解參）非 0 ⇔ 節點存在且已掛上。
/// 只在 PostUpdate 掛（有 PostUpdate＝Dalamud 一定裝了 Finalize 掛勾），只在 PreFinalize／停用拆，
/// 絕不在下一次 PostSetup 才清上一個 addon 的節點。
/// </remarks>
internal static class NativeTextLifecycle
{
    public static NativeUpdateAction OnUpdate(bool faulted, nint attached, nint eventAddon, bool addonReady)
    {
        if (faulted) return NativeUpdateAction.Skip;
        if (attached == 0) return addonReady ? NativeUpdateAction.Attach : NativeUpdateAction.Skip;
        return attached == eventAddon ? NativeUpdateAction.Apply : NativeUpdateAction.Mismatch;
    }

    /// <summary>PreFinalize：只拆掛在正要被釋放的那個 addon 上的節點。</summary>
    public static NativeDetachAction OnFinalize(nint attached, nint eventAddon)
    {
        if (attached == 0) return NativeDetachAction.None;
        return attached == eventAddon ? NativeDetachAction.Detach : NativeDetachAction.Ignore;
    }

    /// <summary>停用：<paramref name="currentAddon"/> 是此刻同名 addon 的位址（不存在＝0）。</summary>
    public static NativeDetachAction OnStop(nint attached, nint currentAddon)
    {
        if (attached == 0) return NativeDetachAction.None;
        return attached == currentAddon ? NativeDetachAction.Detach : NativeDetachAction.Abandon;
    }
}

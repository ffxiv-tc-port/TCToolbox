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

    /// <summary>節點掛在別的 addon 上、或同位址的 addon 樹上已找不到它＝漏了 PreFinalize；節點只能放著不碰。</summary>
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

    /// <summary>節點掛的 addon 已不在或樹上找不到它、又沒收到 PreFinalize：拆＝寫進已釋放的記憶體。</summary>
    Abandon = 3,
}

/// <summary>
/// 掛在 addon 上的原生文字節點的生命週期判斷（純函式，離線可驗）。
/// </summary>
/// <remarks>
/// 不變式：<c>attached</c>（掛上去時的 addon 位址，只做身分比對、不解參）非 0 ⇔ 節點存在且已掛上。
/// 只在 PostUpdate 掛（有 PostUpdate＝Dalamud 一定裝了 Finalize 掛勾），只在 PreFinalize／停用拆，
/// 絕不在下一次 PostSetup 才清上一個 addon 的節點。
/// <c>linked</c>＝從此刻活著的那個 addon 的 RootNode 走直屬子節點找得到我們的節點；位址相同但找不到＝
/// addon 被同位址重建而我們漏了 PreFinalize，節點可能已被遊戲釋放，一律放棄不碰。
/// </remarks>
internal static class NativeTextLifecycle
{
    public static NativeUpdateAction OnUpdate(bool faulted, nint attached, nint eventAddon, bool addonReady, bool linked)
    {
        if (faulted) return NativeUpdateAction.Skip;
        if (attached == 0) return addonReady ? NativeUpdateAction.Attach : NativeUpdateAction.Skip;
        return attached == eventAddon && linked ? NativeUpdateAction.Apply : NativeUpdateAction.Mismatch;
    }

    /// <summary>PreFinalize：只拆掛在正要被釋放的那個 addon 上、而且還在它樹上的節點。</summary>
    public static NativeDetachAction OnFinalize(nint attached, nint eventAddon, bool linked)
    {
        if (attached == 0) return NativeDetachAction.None;
        if (attached != eventAddon) return NativeDetachAction.Ignore;
        return linked ? NativeDetachAction.Detach : NativeDetachAction.Abandon;
    }

    /// <summary>停用：<paramref name="currentAddon"/> 是此刻同名 addon 的位址（不存在＝0）。</summary>
    public static NativeDetachAction OnStop(nint attached, nint currentAddon, bool linked)
    {
        if (attached == 0) return NativeDetachAction.None;
        return attached == currentAddon && linked ? NativeDetachAction.Detach : NativeDetachAction.Abandon;
    }
}

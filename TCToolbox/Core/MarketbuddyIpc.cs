using System;
using System.Reflection;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;

namespace TCToolbox.Core;

/// <summary>對 Marketbuddy 的「停止鎖」包裝：請它在我們動僱員的期間不要碰市場。</summary>
/// <remarks>
/// 🔴 <b>本檔零市場動作</b>：只掛鎖與解鎖，不掛售、不改價、不下架。
/// 🔴 <b>鎖是全域且持久的</b>——取得鎖的路徑都必須有對應的解鎖（<c>finally</c> 語意）；
/// 真的漏掉時使用者可以在 Marketbuddy 設定裡按「解除鎖定」。
/// ⚠️ 只在框架執行緒上呼叫：IPC 的實作跑在<b>呼叫端</b>的執行緒上。
/// </remarks>
internal static class MarketbuddyIpc
{
    /// <summary>本外掛掛鎖時報的名字。⚠️ 改了它，舊版留下的殘鎖就認不出來是自己的了。</summary>
    internal const string LockName = "TCToolbox.RetainerBatchRetrieve";

    private static readonly Lazy<ICallGateSubscriber<string, bool>> LockGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<string, bool>("Marketbuddy.Lock"));

    private static readonly Lazy<ICallGateSubscriber<string, bool>> UnlockGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<string, bool>("Marketbuddy.Unlock"));

    /// <summary>參數 <c>null</c> 問的是「有沒有任何鎖」，否則問的是「這個名字有沒有掛鎖」。</summary>
    private static readonly Lazy<ICallGateSubscriber<string, bool>> IsLockedGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<string, bool>("Marketbuddy.IsLocked"));

    /// <summary>這一類例外代表「這條 IPC 現在不能用」，不是我們自己算錯。</summary>
    /// <remarks>🔴 不裸 <c>catch (Exception)</c>：那會把本外掛自己的程式錯誤一起吞掉。</remarks>
    private static bool IsIpcFailure(Exception ex) =>
        ex is IpcError or TargetInvocationException or InvalidCastException;

    /// <summary>Marketbuddy 有沒有裝並載入（用唯讀的 IsLocked 探測，無副作用）。</summary>
    public static bool IsAvailable()
    {
        try
        {
            IsLockedGate.Value.InvokeFunc(LockName);
            return true;
        }
        catch (Exception ex) when (IsIpcFailure(ex))
        {
            return false;
        }
    }

    /// <summary>
    /// 問 Marketbuddy 現在有沒有鎖。
    /// </summary>
    /// <param name="owner">要問的鎖名；<see langword="null"/>＝問「有沒有任何鎖」。</param>
    /// <param name="locked">結果；問不到時為 <see langword="false"/>。</param>
    /// <returns>IPC 打得通才回 <see langword="true"/>（<see langword="false"/>＝Marketbuddy 不在）。</returns>
    public static bool TryIsLocked(string? owner, out bool locked)
    {
        try
        {
            locked = IsLockedGate.Value.InvokeFunc(owner!);
            return true;
        }
        catch (Exception ex) when (IsIpcFailure(ex))
        {
            locked = false;
            return false;
        }
    }

    /// <summary>
    /// 掛上本外掛的鎖。
    /// </summary>
    /// <returns>
    /// 鎖<b>真的在我們手上</b>才回 <see langword="true"/>——呼叫端要拿這個值決定收尾時要不要解鎖。
    /// ⚠️ Marketbuddy 沒裝時回 <see langword="false"/>，那是正常狀態，不是錯誤。
    /// </returns>
    public static bool TryLock()
    {
        try
        {
            // 對方的回傳值是 HashSet.Add 的語意：同名鎖已經在了會回 false，
            // 但鎖一樣在我們名下 ⇒ 兩種情況都算「我們持有」。
            var added = LockGate.Value.InvokeFunc(LockName);
            Svc.Log.Information($"[MarketbuddyIpc] 已掛上市場停止鎖「{LockName}」（新掛：{added}）。");
            return true;
        }
        catch (Exception ex) when (IsIpcFailure(ex))
        {
            Svc.Log.Information($"[MarketbuddyIpc] 沒有掛上市場停止鎖（Marketbuddy 未安裝或未載入）：{ex.GetType().Name}");
            return false;
        }
    }

    /// <summary>解掉本外掛的鎖。</summary>
    /// <remarks>
    /// 🔴 失敗要寫 <c>Warning</c>：鎖沒解開的話 Marketbuddy 會一直不動作，
    /// 而使用者看到的現象是「重新上架突然沒反應了」，和本外掛毫無關聯。
    /// </remarks>
    /// <returns>IPC 打得通才回 <see langword="true"/>。</returns>
    public static bool TryUnlock()
    {
        try
        {
            var removed = UnlockGate.Value.InvokeFunc(LockName);
            Svc.Log.Information($"[MarketbuddyIpc] 已解除市場停止鎖「{LockName}」（原本掛著：{removed}）。");
            return true;
        }
        catch (Exception ex) when (IsIpcFailure(ex))
        {
            Svc.Log.Warning(
                $"[MarketbuddyIpc] 解除市場停止鎖「{LockName}」失敗（{ex.GetType().Name}）。"
                + "如果 Marketbuddy 之後不動作，請到它的設定裡按「解除鎖定」。");
            return false;
        }
    }
}

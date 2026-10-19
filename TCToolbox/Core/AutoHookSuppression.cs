using System;
using System.Reflection;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;
using Dalamud.Plugin.Services;

namespace TCToolbox.Core;

/// <summary>
/// 向 AutoHook 承租一把<b>具名暫停租約</b>，並在急停期間持續續約。
/// </summary>
/// <remarks>
/// 🔑 用租約而不是單向寫入 <c>AutoHook.SetPluginState(false)</c>：租約有硬性到期時間，
/// 持有者當掉時對方會自動還原成使用者自己的值並寫一行記錄指名是誰壓著。
/// <para>
/// 🔴 <b>拿到租約本身就開始壓制</b>（refcount 語意），不要再呼叫
/// <c>SetLeasedPluginState</c>——那是「不交回租約但暫時不壓制」用的選配端點。
/// </para>
/// <para>
/// 🔴 急停的契約是<b>只停不啟</b>，而租約會到期 ⇒ 押住之後必須持續心跳，
/// 否則急停會在五分鐘後自己失效。
/// </para>
/// <para>⚠️ 只在遊戲主執行緒使用（急停按鈕、指令與 framework 更新都在主執行緒）。</para>
/// </remarks>
internal static class AutoHookSuppression
{
    /// <summary>租用者名稱，會出現在 AutoHook 的記錄與它設定頁上的租約標示。</summary>
    private const string Owner = "TCToolbox";

    /// <summary>單把租約的時長（毫秒）。對方的硬性上限就是這個數，要求更長會被夾。</summary>
    private const int LeaseMs = 300_000;

    /// <summary>心跳間隔。必須明顯短於 <see cref="LeaseMs"/>，否則第一次續約必定失敗。</summary>
    private const int RenewIntervalMs = 30_000;

    private const string RenewThrottleKey = "AutoHookSuppression-Renew";

    // AutoHook/IPC/AutoHookIPC.cs [EzIPC] public Guid AcquireSuppressionFor(string, int)
    private static readonly Lazy<ICallGateSubscriber<string, int, Guid>> AcquireGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<string, int, Guid>("AutoHook.AcquireSuppressionFor"));

    // AutoHook/IPC/AutoHookIPC.cs [EzIPC] public bool RenewSuppressionFor(Guid, int)
    private static readonly Lazy<ICallGateSubscriber<Guid, int, bool>> RenewGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<Guid, int, bool>("AutoHook.RenewSuppressionFor"));

    // AutoHook/IPC/AutoHookIPC.cs [EzIPC] public bool ReleaseSuppression(Guid)
    private static readonly Lazy<ICallGateSubscriber<Guid, bool>> ReleaseGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<Guid, bool>("AutoHook.ReleaseSuppression"));

    /// <summary>沒有租約端點的舊版 AutoHook 走的退路：單向寫入，只改執行期的值。</summary>
    private static readonly Lazy<ICallGateSubscriber<bool, object>> SetPluginStateGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<bool, object>("AutoHook.SetPluginState"));

    private static Guid lease;

    private static bool heartbeatSubscribed;

    public static bool Holding => lease != Guid.Empty;

    /// <summary>
    /// 叫 AutoHook 停手，回傳要放進急停結果 tooltip 的細節。
    /// </summary>
    /// <exception cref="IpcNotReadyError">AutoHook 沒安裝，或它這一版連舊端點都沒有。</exception>
    public static string Suppress()
    {
        if (Holding && TryRenew())
            return $"AutoHook 暫停租約續約成功（{LeaseMs / 1000} 秒）。";

        Guid acquired;
        try
        {
            acquired = AcquireGate.Value.InvokeFunc(Owner, LeaseMs);
        }
        catch (IpcNotReadyError)
        {
            // 租約端點不在有兩種可能：沒裝 AutoHook，或裝的是還沒有這組端點的舊版。
            // 舊端點也不在就是沒裝，例外原樣往上丟給 FleetStop 歸成「未安裝」。
            SetPluginStateGate.Value.InvokeAction(false);
            return "這一版 AutoHook 沒有暫停租約端點，改用 SetPluginState(false)（只改執行期）。";
        }

        if (acquired == Guid.Empty)
        {
            SetPluginStateGate.Value.InvokeAction(false);
            Svc.Log.Information("[AutoHookSuppression] AutoHook 沒有發出租約（多半是租約把數已達上限），改用舊端點。");
            return "AutoHook 沒有發出租約（租約把數已滿），改用 SetPluginState(false)。";
        }

        lease = acquired;
        Subscribe();
        return $"AutoHook.AcquireSuppressionFor：{LeaseMs / 1000} 秒租約，每 {RenewIntervalMs / 1000} 秒續約，卸載時交回。";
    }

    /// <summary>
    /// 交回租約並拆掉心跳。<b>只給本外掛整個卸載的路徑用。</b>
    /// </summary>
    /// <remarks>
    /// 🔴 組件要被卸載了，絕對不能留一個指向本組件的 <c>Framework.Update</c> 訂閱。
    /// ⚠️ 交回之後 AutoHook 會恢復成使用者自己的設定——那是租約唯一的安全失效方向。
    /// </remarks>
    public static void ForceTeardown()
    {
        var held = lease;
        lease = Guid.Empty;
        Unsubscribe();

        if (held == Guid.Empty) return;

        try
        {
            ReleaseGate.Value.InvokeFunc(held);
        }
        catch (Exception ex) when (IsIpcFailure(ex))
        {
            Svc.Log.Information(
                $"[AutoHookSuppression] 交回租約失敗（{ex.GetType().Name}），對方會在租期到期時自動還原。");
        }
    }

    /// <summary>這一類例外全部代表「這條 IPC 現在不能用」，而不是我們自己算錯。</summary>
    private static bool IsIpcFailure(Exception ex) =>
        ex is IpcError or TargetInvocationException or InvalidCastException;

    /// <summary>續約。回 <see langword="false"/>＝手上那把已經不在了，必須重新承租。</summary>
    private static bool TryRenew()
    {
        try
        {
            if (RenewGate.Value.InvokeFunc(lease, LeaseMs)) return true;
        }
        catch (Exception ex) when (IsIpcFailure(ex))
        {
            Svc.Log.Information(
                $"[AutoHookSuppression] 續約呼叫失敗（{ex.GetType().Name}）：{ex.Message}");
        }

        lease = Guid.Empty;
        return false;
    }

    private static void Subscribe()
    {
        if (heartbeatSubscribed) return;

        Svc.Framework.Update += OnFrameworkUpdate;
        heartbeatSubscribed = true;
        Throttle.Reset(RenewThrottleKey);
    }

    private static void Unsubscribe()
    {
        if (!heartbeatSubscribed) return;

        Svc.Framework.Update -= OnFrameworkUpdate;
        heartbeatSubscribed = false;
    }

    /// <summary>沒握著租約時，這裡就只是一行比較後直接返回。</summary>
    private static void OnFrameworkUpdate(IFramework framework)
    {
        if (!Holding)
        {
            Unsubscribe();
            return;
        }

        if (!Throttle.Pass(RenewThrottleKey, RenewIntervalMs)) return;

        if (TryRenew()) return;

        // 續約失敗＝AutoHook 已經恢復使用者自己的設定（對方重載，或租約被掃掉）。
        // 這裡不自動重新承租：急停是一次性的動作，要再壓一次請再按一次急停。
        Svc.Log.Information("[AutoHookSuppression] 暫停租約已經不在，AutoHook 恢復使用者自己的設定。");
        Unsubscribe();
    }
}

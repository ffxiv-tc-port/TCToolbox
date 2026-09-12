using System;
using System.Reflection;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;

namespace TCToolbox.Core;

/// <summary>
/// Lifestream 的「換 World／換副本區／回家」IPC 包裝。
/// </summary>
/// <remarks>
/// 🔴🔴 <b>紅線：絕不透過聊天指令呼叫 <c>/li</c>。</b>空參數的 <c>/li</c> 是跨界傳送
/// （會把角色送到別的 World 去）。這一整個檔的存在理由就是「有具名的端點可以用，
/// 就不要去碰那個會依參數改變語意的指令」。
/// </remarks>
internal static class LifestreamTravel
{
    // ── 換 World ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 📌 <c>[EzIPC] public bool ChangeWorldById(uint worldId)</c>。
    /// 內部查 <c>World</c> 表拿名字之後轉呼叫 <c>ChangeWorld(string)</c>，
    /// 那一支再依 <c>CanVisitCrossDC</c>／<c>CanVisitSameDC</c> 決定走超域傳送還是跨界傳送。
    /// 回 <see langword="false"/>＝查無此列、Lifestream 正在忙、或那個 World 兩張清單都不在。
    /// </summary>
    private static readonly Lazy<ICallGateSubscriber<uint, bool>> ChangeWorldByIdGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<uint, bool>("Lifestream.ChangeWorldById"));

    /// <summary>
    /// 📌 <c>[EzIPC] public bool CanVisitSameDC(string world)</c>——
    /// 實作是 <c>S.Data.DataStore.Worlds.Contains(world)</c>，也就是「同 DC 的可前往清單」。
    /// </summary>
    /// <remarks>
    /// 🔑 <b>這是「台服有哪些 World 可以去」唯一該信的來源。</b>
    /// </remarks>
    private static readonly Lazy<ICallGateSubscriber<string, bool>> CanVisitSameDcGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<string, bool>("Lifestream.CanVisitSameDC"));

    /// <summary>
    /// 📌 <c>[EzIPC] public bool CanVisitCrossDC(string world)</c>——
    /// <c>S.Data.DataStore.DCWorlds.Contains(world)</c>，跨 DC（超域傳送）的可前往清單。
    /// ⚠️ 台服走的是 Lifestream 裡的台服分支，那條分支<b>把 DCWorlds 設成空陣列</b>，
    /// 所以在台服這一支恆為 false——那是正確的（台服只有一個 DC）。
    /// </summary>
    private static readonly Lazy<ICallGateSubscriber<string, bool>> CanVisitCrossDcGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<string, bool>("Lifestream.CanVisitCrossDC"));

    // ── 副本區（分線）─────────────────────────────────────────────────────────

    /// <summary>📌 <c>[EzIPC] public bool CanChangeInstance()</c>。</summary>
    private static readonly Lazy<ICallGateSubscriber<bool>> CanChangeInstanceGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<bool>("Lifestream.CanChangeInstance"));

    /// <summary>
    /// 📌 <c>[EzIPC] public int GetNumberOfInstances()</c>。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>回 0 的意思是「Lifestream 還不知道」，不是「這一區沒有副本區」。</b>
    /// ⇒ 呼叫端<b>不可以</b>把 0 畫成「共 0 線」，那會讓使用者以為功能壞了。
    /// </remarks>
    private static readonly Lazy<ICallGateSubscriber<int>> GetNumberOfInstancesGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<int>("Lifestream.GetNumberOfInstances"));

    /// <summary>
    /// 📌 <c>[EzIPC] public int GetCurrentInstance()</c>——
    /// <c>UIState.Instance()-&gt;PublicInstance.InstanceId</c>。
    /// 0＝不在有副本區的區域裡（這個 0 是真的「沒有」，與上面那個 0 語意不同）。
    /// </summary>
    private static readonly Lazy<ICallGateSubscriber<int>> GetCurrentInstanceGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<int>("Lifestream.GetCurrentInstance"));

    /// <summary>
    /// 📌 <c>[EzIPC] public void ChangeInstance(int number)</c>——無回傳。
    /// 排進 Lifestream 的任務佇列（先解除待機、再走去乙太之光操作選單）。
    /// ⚠️ 因為沒有回傳值，「送出去了」不等於「會成功」；追蹤要靠 <c>Lifestream.IsBusy</c>。
    /// </summary>
    private static readonly Lazy<ICallGateSubscriber<int, object>> ChangeInstanceGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<int, object>("Lifestream.ChangeInstance"));

    // ── 回家捷徑 ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 📌 <c>[EzIPC] public void EnqueueLocalInnShortcut(int? innIndex)</c>——無回傳。
    /// </summary>
    /// <remarks>
    /// 🔑 <b>刻意用 Local 這一支而不是 <c>EnqueueInnShortcut</c>。</b>兩者差在
    /// <c>TaskPropertyShortcut.Enqueue(..., useSameWorld:)</c>：Local 版傳 <c>true</c>＝
    /// <b>就待在目前這個 World</b>；非 Local 版是 false，而 <c>Enqueue</c> 在 <c>useSameWorld</c>
    /// 為 false 且角色不在原始 World 時<b>會先自己跨界傳送回原始 World</b>。
    /// 一顆寫著「回旅店」的按鈕不該把人送去別的 World。
    /// </remarks>
    private static readonly Lazy<ICallGateSubscriber<int?, object>> EnqueueLocalInnShortcutGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<int?, object>("Lifestream.EnqueueLocalInnShortcut"));

    /// <summary>
    /// 📌 <c>[EzIPC] public bool TeleportToHome()</c>——自宅（個人）。
    /// 回 <see langword="false"/>＝Lifestream 正在忙（<c>P.TaskManager.IsBusy</c>）。
    /// ⚠️ 這一支走的是 <c>useSameWorld: false</c>：<b>人在別的 World 時它會先把你傳回原始 World。</b>
    /// </summary>
    private static readonly Lazy<ICallGateSubscriber<bool>> TeleportToHomeGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<bool>("Lifestream.TeleportToHome"));

    /// <summary>📌 <c>[EzIPC] public bool TeleportToFC()</c>——自宅（公會）。跨 World 行為同上。</summary>
    private static readonly Lazy<ICallGateSubscriber<bool>> TeleportToFcGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<bool>("Lifestream.TeleportToFC"));

    /// <summary>📌 <c>[EzIPC] public bool TeleportToApartment()</c>——自宅（公寓）。跨 World 行為同上。</summary>
    private static readonly Lazy<ICallGateSubscriber<bool>> TeleportToApartmentGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<bool>("Lifestream.TeleportToApartment"));

    /// <summary>
    /// 📌 <c>[EzIPC] public bool? HasPrivateHouse()</c>。
    /// 🔴 回傳<b>一定要宣告成 <c>bool?</c></b>：提供端的回傳型別就是 <c>bool?</c>，
    /// 宣告成 <c>bool</c> 的話 <c>CallGateChannel</c> 會在型別不同時走 <c>ConvertObject</c>，
    /// 而它對 null 立刻回 null ⇒ 最後那個 <c>(TRet)result</c> 對值型別擲的是
    /// <see cref="NullReferenceException"/>——有值時一切正常，只有回 null 的那一次炸。
    /// </summary>
    private static readonly Lazy<ICallGateSubscriber<bool?>> HasPrivateHouseGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<bool?>("Lifestream.HasPrivateHouse"));

    /// <summary>📌 <c>[EzIPC] public bool? HasFreeCompanyHouse()</c>。可空理由同上。</summary>
    private static readonly Lazy<ICallGateSubscriber<bool?>> HasFreeCompanyHouseGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<bool?>("Lifestream.HasFreeCompanyHouse"));

    /// <summary>
    /// 📌 <c>[EzIPC] public bool? HasApartment()</c>。可空理由同上。
    /// ⚠️ 它在「目前不在原始 World」時<b>刻意回 null</b>（查不到，不是沒有）。
    /// </summary>
    private static readonly Lazy<ICallGateSubscriber<bool?>> HasApartmentGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<bool?>("Lifestream.HasApartment"));

    /// <summary>
    /// 📌 <c>[EzIPC] public void Abort()</c>——中止 Lifestream 的任務佇列與跟隨路徑。
    /// </summary>
    /// <remarks>
    /// 📌 這是本檔唯一「往安全方向走」的端點：它只會讓事情停下來，
    /// 所以呼叫端不必先問狀態，也不需要二次確認。
    /// </remarks>
    private static readonly Lazy<ICallGateSubscriber<object>> AbortGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<object>("Lifestream.Abort"));

    // ── 查詢 ──────────────────────────────────────────────────────────────────

    /// <summary>這個 World 名字在 Lifestream 的「同 DC 可前往」清單裡嗎（跨界傳送）。</summary>
    /// <remarks>⚠️ 打不通時回 <see langword="false"/>：沒有 Lifestream 就哪裡都去不了，語意上是對的。</remarks>
    public static bool CanVisitSameDc(string worldName)
        => QueryWithString(CanVisitSameDcGate, worldName);

    /// <summary>這個 World 名字在 Lifestream 的「跨 DC 可前往」清單裡嗎（超域傳送）。</summary>
    public static bool CanVisitCrossDc(string worldName)
        => QueryWithString(CanVisitCrossDcGate, worldName);

    /// <summary>現在按下換副本區會不會被 Lifestream 接受。</summary>
    public static bool CanChangeInstance() => Query(CanChangeInstanceGate, false);

    /// <summary>
    /// 目前所在的副本區編號。<b>0＝這一區沒有副本區</b>（或還沒登入／Lifestream 不在）。
    /// </summary>
    public static int GetCurrentInstance() => Query(GetCurrentInstanceGate, 0);

    /// <summary>
    /// 這一區共有幾個副本區。<b>0＝Lifestream 還不知道</b>，不是「沒有」——見
    /// <see cref="GetNumberOfInstancesGate"/> 的備註。呼叫端要把它畫成「?」而不是 0。
    /// </summary>
    public static int GetNumberOfInstances() => Query(GetNumberOfInstancesGate, 0);

    /// <summary>角色有沒有自宅（個人）。<see langword="null"/>＝查不到（含 Lifestream 不在）。</summary>
    public static bool? HasPrivateHouse() => QueryNullable(HasPrivateHouseGate);

    /// <summary>角色有沒有自宅（公會）。<see langword="null"/>＝查不到。</summary>
    public static bool? HasFreeCompanyHouse() => QueryNullable(HasFreeCompanyHouseGate);

    /// <summary>角色有沒有自宅（公寓）。<see langword="null"/>＝查不到（含「目前不在原始 World」）。</summary>
    public static bool? HasApartment() => QueryNullable(HasApartmentGate);

    // ── 動作 ──────────────────────────────────────────────────────────────────

    /// <summary>請 Lifestream 把角色換到指定的 World。</summary>
    /// <param name="worldId"><c>World</c> 表的列號。</param>
    /// <param name="accepted">
    /// Lifestream 收下了這次請求（<b>不代表已經抵達</b>，那要花好幾分鐘；追蹤用 <c>Lifestream.IsBusy</c>）。
    /// <see langword="false"/>＝它正在忙、或那個 World 不在可前往清單上。
    /// </param>
    /// <returns>IPC 呼叫本身是否送達（<see langword="false"/>＝Lifestream 未安裝／未載入／端點擲例外）。</returns>
    public static bool TryChangeWorld(uint worldId, out bool accepted)
        => InvokeFunc(ChangeWorldByIdGate, worldId, "Lifestream.ChangeWorldById", out accepted);

    /// <summary>請 Lifestream 換到指定編號的副本區。</summary>
    /// <returns>
    /// IPC 呼叫本身是否送達。⚠️ <b>這個端點沒有回傳值</b>，所以 <see langword="true"/> 只代表
    /// 「指令送出去了」，不代表 Lifestream 真的排得進去（它自己會在忙碌時寫一行聊天訊息拒絕）。
    /// </returns>
    public static bool TryChangeInstance(int number)
        => InvokeAction(ChangeInstanceGate, number, "Lifestream.ChangeInstance");

    /// <summary>請 Lifestream 帶角色回<b>目前這個 World</b>的旅店房間。</summary>
    /// <returns>IPC 呼叫本身是否送達（沒有回傳值可以確認結果）。</returns>
    public static bool TryGoToLocalInn()
        => InvokeAction(EnqueueLocalInnShortcutGate, (int?)null, "Lifestream.EnqueueLocalInnShortcut");

    /// <summary>請 Lifestream 帶角色回自宅（個人）。⚠️ 人在別的 World 時會先傳回原始 World。</summary>
    public static bool TryTeleportToPrivateHouse(out bool accepted)
        => InvokeFunc(TeleportToHomeGate, "Lifestream.TeleportToHome", out accepted);

    /// <summary>請 Lifestream 帶角色回自宅（公會）。⚠️ 人在別的 World 時會先傳回原始 World。</summary>
    public static bool TryTeleportToFreeCompanyHouse(out bool accepted)
        => InvokeFunc(TeleportToFcGate, "Lifestream.TeleportToFC", out accepted);

    /// <summary>請 Lifestream 帶角色回自宅（公寓）。⚠️ 人在別的 World 時會先傳回原始 World。</summary>
    public static bool TryTeleportToApartment(out bool accepted)
        => InvokeFunc(TeleportToApartmentGate, "Lifestream.TeleportToApartment", out accepted);

    /// <summary>叫 Lifestream 把手上的行程整個中止。</summary>
    /// <returns>IPC 呼叫本身是否送達。</returns>
    public static bool TryAbort() => InvokeAction(AbortGate, "Lifestream.Abort");

    // ── 共用的呼叫外殼 ────────────────────────────────────────────────────────

    /// <summary>
    /// 把 Lifestream 端擲出來的例外記一行，當成「這次沒成功」。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>只攔 <see cref="TargetInvocationException"/>，不是裸 <c>catch (Exception)</c>。</b>
    /// <c>CallGateChannel</c> 是用 <c>Delegate.DynamicInvoke</c> 呼叫提供端的，
    /// 所以<b>提供端擲出來的東西一律被包成這一個型別</b>；我們自己這一側的程式錯誤
    /// 不會長成這個形狀，裸攔會把它們一起吞掉。
    /// </remarks>
    private static void LogFault(Exception ex, string endpoint)
    {
        if (!Throttle.Pass($"LifestreamTravel-Fault-{endpoint}", 10_000)) return;

        var inner = (ex as TargetInvocationException)?.InnerException ?? ex;
        Svc.Log.Information(
            $"[LifestreamTravel] 呼叫 {endpoint} 沒有成功：Lifestream 端擲出 "
            + $"{inner.GetType().Name}：{inner.Message}");
    }

    private static bool Query(Lazy<ICallGateSubscriber<bool>> gate, bool unavailable)
    {
        try
        {
            return gate.Value.InvokeFunc();
        }
        catch (IpcError)
        {
            return unavailable;
        }
        catch (TargetInvocationException ex)
        {
            LogFault(ex, "Lifestream 查詢端點");
            return unavailable;
        }
    }

    private static int Query(Lazy<ICallGateSubscriber<int>> gate, int unavailable)
    {
        try
        {
            return gate.Value.InvokeFunc();
        }
        catch (IpcError)
        {
            return unavailable;
        }
        catch (TargetInvocationException ex)
        {
            LogFault(ex, "Lifestream 查詢端點");
            return unavailable;
        }
    }

    private static bool? QueryNullable(Lazy<ICallGateSubscriber<bool?>> gate)
    {
        try
        {
            return gate.Value.InvokeFunc();
        }
        catch (IpcError)
        {
            return null;
        }
        catch (TargetInvocationException ex)
        {
            LogFault(ex, "Lifestream 查詢端點");
            return null;
        }
    }

    private static bool QueryWithString(Lazy<ICallGateSubscriber<string, bool>> gate, string argument)
    {
        // 🔴 空字串會在 Lifestream 那側命中 World 表裡「名稱為空」的佔位列，
        //    而我們這裡的用途是篩清單——空名字本來就不該被列進去。
        if (string.IsNullOrWhiteSpace(argument)) return false;

        try
        {
            return gate.Value.InvokeFunc(argument);
        }
        catch (IpcError)
        {
            return false;
        }
        catch (TargetInvocationException ex)
        {
            LogFault(ex, "Lifestream 世界清單查詢");
            return false;
        }
    }

    private static bool InvokeFunc(Lazy<ICallGateSubscriber<bool>> gate, string endpoint, out bool accepted)
    {
        try
        {
            accepted = gate.Value.InvokeFunc();
            return true;
        }
        catch (IpcError ex)
        {
            Svc.Log.Warning(ex, $"[LifestreamTravel] 呼叫 {endpoint} 失敗");
            accepted = false;
            return false;
        }
        catch (TargetInvocationException ex)
        {
            LogFault(ex, endpoint);
            accepted = false;
            return false;
        }
    }

    private static bool InvokeFunc(
        Lazy<ICallGateSubscriber<uint, bool>> gate, uint argument, string endpoint, out bool accepted)
    {
        try
        {
            accepted = gate.Value.InvokeFunc(argument);
            return true;
        }
        catch (IpcError ex)
        {
            Svc.Log.Warning(ex, $"[LifestreamTravel] 呼叫 {endpoint} 失敗");
            accepted = false;
            return false;
        }
        catch (TargetInvocationException ex)
        {
            LogFault(ex, endpoint);
            accepted = false;
            return false;
        }
    }

    private static bool InvokeAction(Lazy<ICallGateSubscriber<object>> gate, string endpoint)
    {
        try
        {
            gate.Value.InvokeAction();
            return true;
        }
        catch (IpcError ex)
        {
            Svc.Log.Warning(ex, $"[LifestreamTravel] 呼叫 {endpoint} 失敗");
            return false;
        }
        catch (TargetInvocationException ex)
        {
            LogFault(ex, endpoint);
            return false;
        }
    }

    private static bool InvokeAction(Lazy<ICallGateSubscriber<int, object>> gate, int argument, string endpoint)
    {
        try
        {
            gate.Value.InvokeAction(argument);
            return true;
        }
        catch (IpcError ex)
        {
            Svc.Log.Warning(ex, $"[LifestreamTravel] 呼叫 {endpoint} 失敗");
            return false;
        }
        catch (TargetInvocationException ex)
        {
            LogFault(ex, endpoint);
            return false;
        }
    }

    private static bool InvokeAction(Lazy<ICallGateSubscriber<int?, object>> gate, int? argument, string endpoint)
    {
        try
        {
            gate.Value.InvokeAction(argument);
            return true;
        }
        catch (IpcError ex)
        {
            Svc.Log.Warning(ex, $"[LifestreamTravel] 呼叫 {endpoint} 失敗");
            return false;
        }
        catch (TargetInvocationException ex)
        {
            LogFault(ex, endpoint);
            return false;
        }
    }
}

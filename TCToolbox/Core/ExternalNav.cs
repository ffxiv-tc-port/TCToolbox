using System;
using System.Numerics;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;

namespace TCToolbox.Core;

/// <summary>
/// 對外呼叫 Lifestream／vnavmesh 的唯讀 IPC 包裝。兩套外掛都可能沒安裝、未啟用、
/// 或在執行期間被停用，所以每次呼叫都即時探測、失敗一律回傳 false，不擲例外、
/// 不快取「可用」狀態（避免使用者中途切換外掛後我們還沿用舊判定）。
/// ⚠️ 紅線：只走這裡的 IPC，絕不透過聊天指令呼叫 <c>/li</c>——空參數的 <c>/li</c>
/// 是跨世界傳送，會把角色傳到別的伺服器去（實測踩過）。
/// </summary>
internal static class ExternalNav
{
    // ICallGateSubscriber 本身建立時不會探測對方是否存在（純本地物件、零成本），
    // 真正的探測發生在 InvokeFunc()：對方沒註冊同名端點就丟 IpcNotReadyError。
    private static readonly Lazy<ICallGateSubscriber<bool>> LifestreamIsBusy =
        new(() => Svc.PluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy"));

    private static readonly Lazy<ICallGateSubscriber<uint, byte, bool>> LifestreamTeleportGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<uint, byte, bool>("Lifestream.Teleport"));

    private static readonly Lazy<ICallGateSubscriber<bool>> VnavNavIsReady =
        new(() => Svc.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady"));

    private static readonly Lazy<ICallGateSubscriber<Vector3, bool, bool>> VnavMoveToGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<Vector3, bool, bool>("vnavmesh.SimpleMove.PathfindAndMoveTo"));

    // 📌 vnavmesh 端這兩個是 RegisterAction／RegisterFunc（見 vnavmesh/IPCProvider.cs:35-36）：
    //    Path.Stop 無參數無回傳 → 訂閱型別是 ICallGateSubscriber<object> 且用 InvokeAction()，
    //    寫成 InvokeFunc() 會在執行期炸（型別對不上），編譯期看不出來。
    private static readonly Lazy<ICallGateSubscriber<bool>> VnavPathIsRunning =
        new(() => Svc.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning"));

    private static readonly Lazy<ICallGateSubscriber<object>> VnavPathStop =
        new(() => Svc.PluginInterface.GetIpcSubscriber<object>("vnavmesh.Path.Stop"));

    private static readonly Lazy<ICallGateSubscriber<bool>> VnavPathfindInProgress =
        new(() => Svc.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.SimpleMove.PathfindInProgress"));

    // 只拿來分辨「沒安裝」與「安裝了但網格沒好」。挑 Nav.BuildProgress 是因為它唯讀、
    // 零副作用，而且與網格狀態無關——它一定註冊得起來，所以擲例外＝真的沒這個外掛。
    private static readonly Lazy<ICallGateSubscriber<float>> VnavBuildProgress =
        new(() => Svc.PluginInterface.GetIpcSubscriber<float>("vnavmesh.Nav.BuildProgress"));

    /// <summary>Lifestream 是否已安裝並載入（用唯讀的 IsBusy 端點探測，無副作用）。</summary>
    public static bool IsLifestreamAvailable()
    {
        try
        {
            LifestreamIsBusy.Value.InvokeFunc();
            return true;
        }
        catch (IpcError)
        {
            return false;
        }
    }

    /// <summary>
    /// vnavmesh 是否已安裝且導航網格就緒。刻意把「沒安裝」與「網格還沒載完」合併成同一個
    /// false——兩者對呼叫端而言結果一樣：這一刻呼叫 PathfindAndMoveTo 大概率不會成功，
    /// 該走地圖標旗的退化路徑。
    /// </summary>
    public static bool IsVnavmeshReady()
    {
        try
        {
            return VnavNavIsReady.Value.InvokeFunc();
        }
        catch (IpcError)
        {
            return false;
        }
    }

    /// <summary>
    /// 呼叫 Lifestream 的長距離乙太之光傳送（<c>Telepo.Teleport</c>，需已解鎖該乙太之光；
    /// 非本地小型網路傳送，走 IPC 不走聊天指令）。
    /// </summary>
    /// <param name="aetheryteId">目的地乙太之光的 Aetheryte 表 RowId。</param>
    /// <param name="subIndex">通常為 0；僅私人／部隊房屋等共用同一 RowId 的情況才非零。</param>
    /// <param name="accepted">Lifestream 是否接受了這次傳送請求（不代表已抵達）。</param>
    /// <returns>IPC 呼叫本身是否成功（false＝Lifestream 未安裝/未載入）。</returns>
    public static bool TryTeleport(uint aetheryteId, byte subIndex, out bool accepted)
    {
        try
        {
            accepted = LifestreamTeleportGate.Value.InvokeFunc(aetheryteId, subIndex);
            return true;
        }
        catch (IpcError ex)
        {
            Svc.Log.Warning(ex, "[ExternalNav] 呼叫 Lifestream.Teleport 失敗");
            accepted = false;
            return false;
        }
    }

    /// <summary>
    /// vnavmesh 這個外掛在不在（<b>與導航網格就緒與否無關</b>）。
    /// </summary>
    /// <remarks>
    /// 📌 存在的意義只有一個：把 <see cref="IsVnavmeshReady"/> 的 false 拆成兩種原因，
    /// 好讓使用者看到的是「未偵測到 vnavmesh」還是「網格還沒載完」。
    /// 兩者的處置完全不同（前者要去裝外掛，後者只要等），合併成一句話等於沒說。
    /// </remarks>
    public static bool IsVnavmeshInstalled()
    {
        try
        {
            VnavBuildProgress.Value.InvokeFunc();
            return true;
        }
        catch (IpcError)
        {
            return false;
        }
    }

    /// <summary>
    /// vnavmesh 是不是正在<b>計算</b>路徑（尚未開始走）。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>這段期間按停止是攔不住的。</b>vnavmesh 的 <c>SimpleMove</c> 把路徑計算丟到背景工作，
    /// 算完後在自己的 Update 裡直接交給 FollowPath 開走；而 <c>Path.Stop</c> 清的是 FollowPath
    /// 的路徑點，<b>碰不到那個還沒算完的工作</b>——所以「按了停止、幾秒後角色自己走起來」
    /// 是真的會發生的（vnavmesh/AsyncMoveRequest.cs:60-78 直證）。
    /// <para>
    /// ⚠️ 而 <c>Nav.PathfindCancelAll</c> <b>不是</b>用來解這個的：那個端點的實作是
    /// <c>navmeshManager.Reload(true)</c>，也就是把整份導航網格重載一次，名字與行為不符。
    /// </para>
    /// <para>
    /// ⇒ 呼叫端要嘛在這段期間擋住新的導航請求，要嘛在使用者按停止後持續補送
    /// <see cref="TryStopMovement"/> 直到這裡回 false。
    /// </para>
    /// </remarks>
    public static bool IsVnavmeshPathfindInProgress()
    {
        try
        {
            return VnavPathfindInProgress.Value.InvokeFunc();
        }
        catch (IpcError)
        {
            return false;
        }
    }

    /// <summary>
    /// vnavmesh 目前是不是正在沿路徑移動（<c>Path.IsRunning</c>＝還有未走完的路徑點）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 這只涵蓋「已經在走」，<b>不涵蓋「還在算路徑」</b>
    /// （那是 <see cref="IsVnavmeshPathfindInProgress"/>）。呼叫端若要顯示「移動中」給使用者看，
    /// 光看這個會在剛按下按鈕、路徑還沒算完的那幾百毫秒內顯示成「沒在動」。
    /// <para>
    /// 📌 未安裝 vnavmesh 時回傳 false——沒有那個外掛就不可能有我們發起的移動在跑，
    /// 語意上是對的。
    /// </para>
    /// </remarks>
    public static bool IsVnavmeshPathRunning()
    {
        try
        {
            return VnavPathIsRunning.Value.InvokeFunc();
        }
        catch (IpcError)
        {
            return false;
        }
    }

    /// <summary>
    /// 叫 vnavmesh 立刻停止移動（清空路徑點）。
    /// </summary>
    /// <returns>
    /// IPC 呼叫本身是否送達（false＝vnavmesh 未安裝／未載入）。
    /// ⚠️ 回傳 true 只代表「指令送出去了」，vnavmesh 端沒有回傳值可以確認真的停了。
    /// </returns>
    /// <remarks>
    /// 📌 這個端點對「本來就沒在移動」的情況是安全的無操作，呼叫端不必先查 IsRunning。
    /// </remarks>
    public static bool TryStopMovement()
    {
        try
        {
            VnavPathStop.Value.InvokeAction();
            return true;
        }
        catch (IpcError ex)
        {
            Svc.Log.Warning(ex, "[ExternalNav] 呼叫 vnavmesh.Path.Stop 失敗");
            return false;
        }
    }

    /// <summary>呼叫 vnavmesh 就地導航到世界座標；只下指令，不等待走到、不接後續互動。</summary>
    public static bool TryMoveTo(Vector3 destination, bool fly, out bool started)
    {
        try
        {
            started = VnavMoveToGate.Value.InvokeFunc(destination, fly);
            return true;
        }
        catch (IpcError ex)
        {
            Svc.Log.Warning(ex, "[ExternalNav] 呼叫 vnavmesh.SimpleMove.PathfindAndMoveTo 失敗");
            started = false;
            return false;
        }
    }
}

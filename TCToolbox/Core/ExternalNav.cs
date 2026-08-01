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

using System;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
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

    // 📌 vnavmesh 端註冊成 RegisterFunc("Query.Mesh.PointOnFloor",
    //    (Vector3 p, bool allowUnlandable, float halfExtentXZ) => ...FindPointOnFloor(p, halfExtentXZ))
    //    （vnavmesh/IPCProvider.cs:32）。回傳是 Vector3?——查不到落點時是 null，不是 Vector3.Zero，
    //    ⚠️ 拿 Zero 當「查不到」會把地圖原點附近的合法落點誤判成失敗。
    private static readonly Lazy<ICallGateSubscriber<Vector3, bool, float, Vector3?>> VnavPointOnFloor =
        new(() => Svc.PluginInterface.GetIpcSubscriber<Vector3, bool, float, Vector3?>("vnavmesh.Query.Mesh.PointOnFloor"));

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
    /// 📌 <b>2026-09-03 更正</b>：上面那段成立，但原本接著寫的「<c>Nav.PathfindCancelAll</c>
    /// 不是用來解這個的，它的實作是 <c>navmeshManager.Reload(true)</c>」<b>已經過期，而且結論是反的</b>。
    /// vnavmesh <c>02dcefe</c>（已隨 v7.20.0.32 出貨）把該端點改成真正的
    /// <c>navmeshManager.CancelAllPathfinds()</c>：拆出獨立的 <c>_pathfindCTS</c> 只取消尋路批次，
    /// <b>不動導航網格</b>。而 <c>SimpleMove</c> 的在途工作走的正是 <c>QueryPath</c>
    /// （vnavmesh/AsyncMoveRequest.cs 的 <c>MoveTo</c>），會被那個 CTS 取消。
    /// ⇒ <c>Nav.PathfindCancelAll</c> 現在就是解這個問題的正確工具。
    /// </para>
    /// <para>
    /// ⚠️ <b>但本檔目前還沒有改成去呼叫它</b>（那是使用者可見的行為變更，等裁決）。
    /// 在改之前，呼叫端仍然要嘛在這段期間擋住新的導航請求，要嘛在使用者按停止後持續補送
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

    /// <summary>
    /// 問 vnavmesh：從這個位置<b>垂直往下</b>找，地板在哪裡。
    /// </summary>
    /// <remarks>
    /// 📌 用途是把「只有 X／Z 的座標」補成完整的三維座標。地圖旗標就是這種情況——
    /// <c>FlagMapMarker</c> 只存 X 與 Z（世界座標），沒有高度。
    /// vnavmesh 自己的 <c>MapUtils.FlagToPoint</c> 就是這樣做的：拿 Y=1024 當起點往下打。
    /// <para>
    /// ⚠️ <paramref name="probe"/> 的 Y 要給一個<b>高於地形</b>的值，否則會從地板底下往下找而落空。
    /// </para>
    /// </remarks>
    /// <param name="probe">探測起點（Y 要夠高）。</param>
    /// <param name="allowUnlandable">是否接受「站不住」的落點（例如水面）。</param>
    /// <param name="halfExtentXZ">水平方向的搜尋半徑。</param>
    /// <param name="point">找到的落點。</param>
    /// <returns>是否找到落點（false＝vnavmesh 未安裝、網格沒好，或這個位置下面沒有地板）。</returns>
    public static bool TryFindPointOnFloor(
        Vector3 probe, bool allowUnlandable, float halfExtentXZ, out Vector3 point)
    {
        try
        {
            var result = VnavPointOnFloor.Value.InvokeFunc(probe, allowUnlandable, halfExtentXZ);
            if (result == null)
            {
                point = default;
                return false;
            }

            point = result.Value;
            return true;
        }
        catch (IpcError ex)
        {
            Svc.Log.Warning(ex, "[ExternalNav] 呼叫 vnavmesh.Query.Mesh.PointOnFloor 失敗");
            point = default;
            return false;
        }
    }

    /// <summary>
    /// 現在這一刻「飛得起來」嗎（已乘坐騎／已在飛行中／正在潛水）。
    /// </summary>
    /// <remarks>
    /// 🔴 這道判斷存在的唯一理由是 <b>vnavmesh 對這件事的失敗是完全靜默的</b>：
    /// <c>PathfindAndMoveTo(pos, fly: true)</c> 在玩家沒乘坐騎時照樣回傳 true、路徑也真的算得出來，
    /// 但 <c>FollowPath.Update</c> 走到第一個「比目前高」的路徑點時判定需要起飛，
    /// 而沒乘坐騎就直接 <c>_movement.Enabled = false; return</c>
    /// （vnavmesh/Movement/FollowPath.cs:183-192 直證）——角色站在原地不動，<b>沒有任何訊息</b>。
    /// <para>
    /// 📌 三個條件與 vnavmesh 那段<b>逐項對齊</b>：已在飛行中（<c>InFlight</c>）或潛水中
    /// （<c>Diving</c>）根本不需要起飛動作，這時候把 fly 降級反而是幫倒忙。
    /// </para>
    /// <para>
    /// ⚠️ <b>刻意不看 <c>ConditionFlag.Mounted2</c></b>（本 pin 已改名 <c>RidingPillion</c>，
    /// 語意是「坐在別人的坐騎後座」）。那個狀態下 <c>Mounted</c> 是 false，
    /// vnavmesh 照樣會卡在起飛判斷上——把它算成「飛得起來」等於重新製造這個 bug。
    /// </para>
    /// <para>
    /// ⚠️ 這<b>不</b>檢查該區域有沒有解鎖飛行——那是另一回事，而且沒有便宜可靠的判法。
    /// 解鎖與否的失敗形式是 vnavmesh 自己算不出飛行路徑，那條路徑上它會回報失敗，不是靜默的。
    /// </para>
    /// </remarks>
    private static bool CanFly()
        => Svc.Condition[ConditionFlag.Mounted]
           || Svc.Condition[ConditionFlag.InFlight]
           || Svc.Condition[ConditionFlag.Diving];

    /// <summary>呼叫 vnavmesh 就地導航到世界座標；只下指令，不等待走到、不接後續互動。</summary>
    /// <param name="destination">目的地世界座標。</param>
    /// <param name="fly">是否允許飛行路線。⚠️ 沒乘坐騎時會在這裡被就地降級成地面路線（見備註）。</param>
    /// <param name="started">vnavmesh 是否收下了這次導航（不代表走得到）。</param>
    /// <param name="source">呼叫端模組名，只用在降級訊息裡指名；null＝不指名。</param>
    /// <remarks>
    /// 🔴 <b>刻意不替使用者自動乘坐騎</b>——本外掛不新增自動化。降級成地面路線是保守處置：
    /// 走得到就走過去，走不到 vnavmesh 自己會拒絕或半路停下，兩種都比「站著不動又零訊息」好。
    /// </remarks>
    public static bool TryMoveTo(Vector3 destination, bool fly, out bool started, string? source = null)
    {
        if (fly && !CanFly())
        {
            fly = false;

            // 節流的理由：目前三個會傳 fly:true 的呼叫端都是離散的使用者動作
            // （點擊放開、按鈕、聊天指令），這道閘門幾乎一定放行，節流形同不存在。
            // 它在的目的是保險——將來若接上每幀重試的呼叫端，沒有它就會把聊天視窗與 log 洗爆。
            if (Throttle.Pass("ExternalNav-FlyNeedsMount", 2_000))
            {
                var tag = string.IsNullOrEmpty(source) ? string.Empty : $"{source}：";
                Svc.Chat.Print(
                    $"[TC Toolbox] {tag}目的地需飛行但未乘坐騎，改走地面路線；請先乘上坐騎再下指令。");

                // 使用者回報用的定錨點：出事時這一行是唯一能證明「飛行被降級了、是誰要求的」的證據。
                Svc.Log.Information(
                    $"[ExternalNav] 飛行降級為地面路線（未乘坐騎）：呼叫端={source ?? "未指名"}"
                    + $" 目的地={destination:F1}");
            }
        }

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

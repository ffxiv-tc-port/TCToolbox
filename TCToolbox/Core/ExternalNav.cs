using System;
using System.Numerics;
using System.Reflection;
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

    // 📌 Lifestream/IPC/IPCProvider.cs 的 [EzIPC] public bool GoToMapPoint(uint, float, float, bool)。
    //    對方的註解逐字寫著「參數順序與型別是對外契約，已有消費端（Mappy 地圖右鍵的
    //    『移動到這裡』）照此接線，不要改」。座標是<世界座標>的 X 與 Z，不需要 Y——
    //    抵達之後由 Lifestream 自己向 vnavmesh 問那個 XZ 底下的地板高度。
    private static readonly Lazy<ICallGateSubscriber<uint, float, float, bool, bool>> LifestreamGoToMapPoint =
        new(() => Svc.PluginInterface.GetIpcSubscriber<uint, float, float, bool, bool>("Lifestream.GoToMapPoint"));

    private static readonly Lazy<ICallGateSubscriber<bool>> VnavNavIsReady =
        new(() => Svc.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady"));

    private static readonly Lazy<ICallGateSubscriber<Vector3, bool, bool>> VnavMoveToGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<Vector3, bool, bool>("vnavmesh.SimpleMove.PathfindAndMoveTo"));

    // 📌 vnavmesh/IPCProvider.cs 的
    //    RegisterFunc("SimpleMove.PathfindAndMoveCloseTo", (Vector3 dest, bool fly, float range) => move.MoveTo(dest, fly, range))
    //    比 PathfindAndMoveTo 多一個「到這麼近就算到了」的容許值，最後會變成 FollowPath 的
    //    DestinationTolerance：那一段的實作是「距離最後一個路徑點小於容許值就把路徑點清空」
    //    （vnavmesh/Movement/FollowPath.cs 的 Update）。
    // 🔑 對「走到某個物件旁邊互動」這種需求，這支才是對的工具：目的地是那個物件本身，
    //    而物件本身通常是站不上去的（地壟、花盆、NPC），用 PathfindAndMoveTo 等於要求
    //    走進障礙物裡面。
    private static readonly Lazy<ICallGateSubscriber<Vector3, bool, float, bool>> VnavMoveCloseToGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<Vector3, bool, float, bool>("vnavmesh.SimpleMove.PathfindAndMoveCloseTo"));

    // 📌 vnavmesh/IPCProvider.cs 的
    //    RegisterFunc("Query.Mesh.NearestPoint", (Vector3 p, float halfExtentXZ, float halfExtentY) => navmeshManager.Query?.FindNearestPointOnMesh(p, halfExtentXZ, halfExtentY))
    // 🔴🔴 回傳型別<b>必須</b>宣告成 <c>Vector3?</c>，不能寫成 <c>Vector3</c>。
    //    CallGateChannel.InvokeFunc 只在型別「不同」時才走 ConvertObject，而 ConvertObject
    //    對 null 輸入立刻回 null ⇒ 最後那個 (TRet)result 對值型別擲的是
    //    NullReferenceException。失敗形式是「有值時一切正常，只有查不到的那一次擲一個
    //    看起來與 IPC 完全無關的 NRE」——正是這種缺陷可以長期潛伏不被發現的原因。
    private static readonly Lazy<ICallGateSubscriber<Vector3, float, float, Vector3?>> VnavNearestPoint =
        new(() => Svc.PluginInterface.GetIpcSubscriber<Vector3, float, float, Vector3?>("vnavmesh.Query.Mesh.NearestPoint"));

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

    // 📌 AutoDuty 與 BossMod 的「正在導航」旗標。兩邊都是實查過的：
    //    AutoDuty/IPC/IPCProvider.cs 的 [EzIPC] public bool IsNavigating()（前綴取自 InternalName，即 AutoDuty），
    //    BossmodReborn/BossMod/Framework/IPCProvider.cs 的 Register("AI.IsNavigating", ...)（那支 Register 實作是 "BossMod." + name）。
    private static readonly Lazy<ICallGateSubscriber<bool>> AutoDutyIsNavigating =
        new(() => Svc.PluginInterface.GetIpcSubscriber<bool>("AutoDuty.IsNavigating"));

    private static readonly Lazy<ICallGateSubscriber<bool>> BossModAiIsNavigating =
        new(() => Svc.PluginInterface.GetIpcSubscriber<bool>("BossMod.AI.IsNavigating"));

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
    /// <param name="started">
    /// 🔴 <b>本 pin 的 vnavmesh 恆為 <see langword="true"/>，不要拿它當「導航成功」的判準。</b>
    /// <c>AsyncMoveRequest.MoveTo</c> 全檔只有兩個 <c>return</c>，兩個都是 <c>return true</c>：
    /// 上一筆還在跑時它<b>接手</b>（把新請求寫進單格佇列）而不是拒絕，其餘情況走 <c>StartMove</c>
    /// 也是無條件回 true。⇒ 這個值只代表「IPC 呼叫回來了」。
    /// </param>
    /// <param name="source">呼叫端模組名，只用在降級訊息裡指名；null＝不指名。</param>
    /// <remarks>
    /// 🔴 <b>刻意不替使用者自動乘坐騎</b>——本外掛不新增自動化。降級成地面路線是保守處置：
    /// 走得到就走過去，走不到 vnavmesh 自己會拒絕或半路停下，兩種都比「站著不動又零訊息」好。
    /// <para>
    /// 🔴🔴 <b>這支沒有可靠的「導航失敗」訊號，呼叫端不要假裝有。</b>
    /// 回 <see langword="false"/> 代表<b>兩件事之一</b>：<c>IpcError</c>（vnavmesh 沒安裝／沒載入），
    /// 或 vnavmesh 端自己擲了例外（見下一段）。<b>兩者都不是「這條路走不到」的意思。</b>
    /// <paramref name="started"/> 見上，恆為 true。
    /// 而「導航網格還沒載入」那一種失敗<b>既不是 false 也不是 IpcError</b>——
    /// vnavmesh 的 <c>NavmeshManager.QueryPath</c> 在 <c>_currentCTS</c> 為 null 時直接擲一個普通的
    /// <c>Exception</c>（訊息 Can't initiate query - navmesh is not loaded），
    /// 經 <c>Delegate.DynamicInvoke</c> 包成 <c>TargetInvocationException</c>，
    /// <b>而 <c>TargetInvocationException</c> 不是 <c>IpcError</c> 的子型別</b>。
    /// 📌 <b>2026-09-08 起這支自己攔掉它了</b>（見下面的 <c>catch</c>）：那種失敗現在會變成
    /// 一行 Information ＋ 回 <see langword="false"/>，走呼叫端本來就有的「沒有開始移動」那條路。
    /// 在那之前它是直接逃進呼叫端的——兩個 <c>IssueWalkOrFallback</c> 因此從來沒有真的
    /// 退化成地圖標旗過（例外被 <c>TaskQueue</c> 接住並中止整條佇列）。
    /// ⇒ 呼叫端能誠實說的只有「已經把這趟交給 vnavmesh 了」；
    /// 真要知道走不走得到，只能像 <c>AutoGardensWork</c> 的走位那樣自己用距離與
    /// <c>Path.IsRunning</c>／<c>PathfindInProgress</c> 監看。
    /// </para>
    /// </remarks>
    public static bool TryMoveTo(Vector3 destination, bool fly, out bool started, string? source = null)
    {
        fly = DegradeFlyIfNotMounted(fly, destination, source);

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
        catch (TargetInvocationException ex)
        {
            LogNavProviderFault(ex, "vnavmesh.SimpleMove.PathfindAndMoveTo");
            started = false;
            return false;
        }
    }

    /// <summary>
    /// 同 <see cref="TryMoveTo"/>，但只要走到距離目的地 <paramref name="range"/> 碼以內就算到了。
    /// </summary>
    /// <param name="destination">目的地世界座標（通常就是要互動的那個物件本身的位置）。</param>
    /// <param name="fly">是否允許飛行路線。⚠️ 沒乘坐騎時會在這裡被就地降級成地面路線。</param>
    /// <param name="range">
    /// 容許值（碼）。<b>0 等於沒有容許值</b>（vnavmesh 端是 <c>DestinationTolerance &gt; 0</c> 才判），
    /// 也就是退化成 <see cref="TryMoveTo"/>。
    /// </param>
    /// <param name="started">恆為 <see langword="true"/>；只代表 IPC 呼叫回來了。見下面的備註。</param>
    /// <param name="source">呼叫端模組名，只用在降級訊息裡指名；null＝不指名。</param>
    /// <remarks>
    /// 🔴 <b><paramref name="started"/> 回 <see langword="true"/> 幾乎沒有資訊量。</b>
    /// vnavmesh 的 <c>AsyncMoveRequest.MoveTo</c> 現在對「上一筆還在跑」是接手而不是拒絕，
    /// 兩條路徑都回 true ⇒ 呼叫端<b>不可以</b>拿它當「走得到」的證據，
    /// 一定要自己用距離判定抵達、自己設上限判定走不到。
    /// <para>
    /// 📌 失敗語意與 <see cref="TryMoveTo"/> 完全相同（含提供端擲例外的處置），細節寫在那邊。
    /// </para>
    /// </remarks>
    public static bool TryMoveCloseTo(
        Vector3 destination, bool fly, float range, out bool started, string? source = null)
    {
        fly = DegradeFlyIfNotMounted(fly, destination, source);

        try
        {
            started = VnavMoveCloseToGate.Value.InvokeFunc(destination, fly, range);
            return true;
        }
        catch (IpcError ex)
        {
            Svc.Log.Warning(ex, "[ExternalNav] 呼叫 vnavmesh.SimpleMove.PathfindAndMoveCloseTo 失敗");
            started = false;
            return false;
        }
        catch (TargetInvocationException ex)
        {
            LogNavProviderFault(ex, "vnavmesh.SimpleMove.PathfindAndMoveCloseTo");
            started = false;
            return false;
        }
    }

    /// <summary>
    /// 問 vnavmesh：這個位置附近的導航網格上，最近的一個點在哪裡。
    /// </summary>
    /// <remarks>
    /// 🔑 <b>這是「這裡到底有沒有導航網格」唯一便宜可靠的判法。</b>
    /// <see cref="IsVnavmeshReady"/> 只說「這張圖建出了一張網格」，不保證<b>你要去的那個角落</b>
    /// 在網格上（室內、庭園、被家具圍住的區塊都可能整塊沒有）。
    /// 而 vnavmesh 對「終點不在網格上」的處置是<b>算不出路徑</b>——呼叫端拿到的仍然是
    /// <c>started == true</c>，然後角色站著不動。
    /// <para>
    /// ⚠️ <paramref name="halfExtentY"/> 的預設在 vnavmesh 端是 5：只有 X／Z 而 Y 隨便給
    /// （例如 0 或 1024）時一定查不到。這裡要求呼叫端明確給值，就是為了不讓那個預設值
    /// 被靜默套用。物件表拿到的座標本來就是完整三維的，直接傳進來即可。
    /// </para>
    /// </remarks>
    /// <returns><see langword="false"/>＝vnavmesh 未安裝、網格沒好，或這個位置附近沒有網格。</returns>
    public static bool TryFindNearestMeshPoint(
        Vector3 probe, float halfExtentXZ, float halfExtentY, out Vector3 point)
    {
        try
        {
            var result = VnavNearestPoint.Value.InvokeFunc(probe, halfExtentXZ, halfExtentY);
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
            Svc.Log.Warning(ex, "[ExternalNav] 呼叫 vnavmesh.Query.Mesh.NearestPoint 失敗");
            point = default;
            return false;
        }
    }

    /// <summary>
    /// vnavmesh 的端點實作自己擲了例外——當成「沒有開始移動」，並寫一行給使用者看的說明。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 🔴 <b>刻意只攔 <see cref="TargetInvocationException"/>，不是裸 <c>catch (Exception)</c>。</b>
    /// <c>CallGateChannel.InvokeFunc</c> 是用 <c>Delegate.DynamicInvoke</c> 呼叫提供端的，
    /// 所以<b>提供端擲出的東西一律被包成這一個型別</b>；本外掛自己這一側的程式錯誤
    /// （空參考、轉型失敗）不會長成這個形狀，裸攔會把它們一起吞掉。
    /// </para>
    /// <para>
    /// 📌 <b>為什麼不需要再攔別的型別</b>（逐行讀過本 pin 的 <c>CallGateChannel</c>）：
    /// <c>IpcNotReadyError</c>／<c>IpcLengthMismatchError</c>／<c>IpcTypeMismatchError</c>／
    /// <c>IpcValueNullError</c> 全部是 <c>IpcError</c> 的子型別，上面那個 catch 已經接住；
    /// 而 <c>ConvertObject</c> 與最後那個 <c>(TRet)result</c> 只有在「宣告型別與提供端回傳型別不同」
    /// 時才會走到，這兩支端點兩邊都是 <c>bool</c>，走不到。
    /// </para>
    /// <para>
    /// 🔑 <b>不指名確切原因，但把證據附上。</b>最常見的成因是導航網格還沒載入好
    /// （<c>NavmeshManager.QueryPath</c> 在 <c>_currentCTS</c> 為 null 時直接擲例外），
    /// 但那是對方的內部訊息、隨時可能改，拿字串去比對只會變成另一種「宣稱查不到的事」。
    /// 所以這裡說「多半是」，並且逐字附上對方擲出來的型別與訊息。
    /// </para>
    /// <para>
    /// 📌 <b>Information 級、只進記錄不進聊天。</b>要不要跟使用者說話是呼叫端的事
    /// （它們各自已經有「沒有開始移動」的訊息與退路），這裡只負責留下可回報的證據。
    /// ⚠️ 本 pin 的聊天佇列不是執行緒安全的，而 <c>Svc.Log</c> 是——這裡走 log 也順便免疫那件事。
    /// </para>
    /// <para>
    /// ⚠️ 節流 10 秒：目前每一個呼叫端不是使用者的離散動作就是幾十秒一輪的迴圈，
    /// 這道閘門幾乎一定放行。它在的目的是保險——將來若接上每幀重試的呼叫端，
    /// 沒有它就會把記錄洗爆。
    /// </para>
    /// </remarks>
    private static void LogNavProviderFault(TargetInvocationException ex, string endpoint)
    {
        if (!Throttle.Pass("ExternalNav-NavProviderFault", 10_000)) return;

        var inner = ex.InnerException ?? ex;
        Svc.Log.Information(
            $"[ExternalNav] vnavmesh 沒有接下這次導航（{endpoint}）：它的導航網格多半還沒載入好。"
            + $" vnavmesh 端擲出 {inner.GetType().Name}：{inner.Message}");
    }

    /// <summary>沒乘坐騎就把飛行請求降級成地面路線，並（節流地）說一聲。</summary>
    /// <returns>實際要傳給 vnavmesh 的 fly 值。</returns>
    private static bool DegradeFlyIfNotMounted(bool fly, Vector3 destination, string? source)
    {
        if (!fly || CanFly()) return fly;

        // 節流的理由：會傳 fly:true 的呼叫端都是離散的使用者動作
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

        return false;
    }

    /// <summary>
    /// 請 Lifestream 帶角色去「地圖上的一個點」：跨區時它自己挑最近的乙太之光傳送，
    /// 最後一段交給 vnavmesh 走（或飛）。
    /// </summary>
    /// <param name="territory">目標區域的 <c>TerritoryType</c> 列號。</param>
    /// <param name="worldX">目標點的<b>世界座標</b> X。</param>
    /// <param name="worldZ">目標點的<b>世界座標</b> Z。</param>
    /// <param name="fly">允許用飛行坐騎跑最後一段；不可飛或起飛失敗時 Lifestream 自己退回地面路線。</param>
    /// <param name="accepted">Lifestream 收下了這次請求（<b>不代表已抵達</b>，用 <c>Lifestream.IsBusy</c> 追蹤）。</param>
    /// <returns>IPC 呼叫本身是否送達（<see langword="false"/>＝Lifestream 未安裝／未載入）。</returns>
    /// <remarks>
    /// <para>
    /// 🔴 <b>座標一定是世界座標，不是地圖座標。</b>兩者差一個縮放與位移，傳錯不會有任何錯誤訊息——
    /// 角色只是被帶到那張圖上不相干的地方。地圖座標要先換回世界座標
    /// （<see cref="MapCoords.TryHuntHelperMapToWorld"/> 之類）再進來。
    /// </para>
    /// <para>
    /// 🔴 <b>絕不用聊天指令 <c>/li</c> 代替這支。</b>空參數的 <c>/li</c> 是跨世界傳送。
    /// </para>
    /// <para>
    /// ⚠️ <c>accepted</c> 為 <see langword="false"/> 是<b>正常結果不是錯誤</b>：區域 id 為 0、
    /// Lifestream 正在忙、角色不可互動、vnavmesh 沒載入、或該區沒有任何已解鎖的乙太之光。
    /// 呼叫端該做的是告訴使用者「沒開始」，不是重試。
    /// </para>
    /// </remarks>
    public static bool TryGoToMapPoint(uint territory, float worldX, float worldZ, bool fly, out bool accepted)
    {
        try
        {
            accepted = LifestreamGoToMapPoint.Value.InvokeFunc(territory, worldX, worldZ, fly);
            return true;
        }
        catch (IpcError ex)
        {
            Svc.Log.Warning(ex, "[ExternalNav] 呼叫 Lifestream.GoToMapPoint 失敗");
            accepted = false;
            return false;
        }
    }
    /// <summary>
    /// 現在有沒有<b>任何一個外掛</b>正在把角色帶往某處。
    /// </summary>
    /// <param name="mover">
    /// 第一個回報「在動」的外掛名稱（給 tooltip 與記錄用）；回 <see langword="false"/> 時是空字串。
    /// </param>
    /// <remarks>
    /// <para>
    /// 🔴 <b>失敗方向是「沒人在動」。</b>四支端點隨便哪一支都可能因為對方沒裝而打不通，
    /// 而打不通的意思就是「那個外掛不可能正在移動角色」——語意上就是 false。
    /// 所以這裡不把「問不到」當成「不知道所以當成在動」；否則沒裝 Lifestream 的人會永遠看到按鈕是灰的。
    /// </para>
    /// <para>
    /// ⚠️ vnavmesh 這邊<b>兩個狀態都要問</b>：<c>Path.IsRunning</c> 只涵蓋「已經在走」，
    /// 剛按下去、路徑還在背景算的那幾百毫秒它是 false（見 <see cref="IsVnavmeshPathRunning"/>）。
    /// 只問它的話，使用者連按兩下就會在那個空窗裡把第二個請求送出去。
    /// </para>
    /// </remarks>
    public static bool TryGetActiveMover(out string mover)
    {
        mover = string.Empty;

        if (Query(LifestreamIsBusy)) { mover = "Lifestream"; return true; }
        if (IsVnavmeshPathRunning() || IsVnavmeshPathfindInProgress()) { mover = "vnavmesh"; return true; }
        if (Query(AutoDutyIsNavigating)) { mover = "AutoDuty"; return true; }
        if (Query(BossModAiIsNavigating)) { mover = "BossMod AI"; return true; }

        return false;
    }

    /// <summary>問一個無參數的 bool 端點；打不通一律回 <see langword="false"/>。</summary>
    /// <remarks>
    /// 🔴 <c>IpcError</c> 不只是「沒註冊」：對方把端點型別改掉時擲出的是 <c>IpcTypeMismatchError</c>，
    /// 而它<b>不是</b> <c>IpcNotReadyError</c> 的子型別——兩種都要接住，否則會變成每幀擲一次例外。
    /// </remarks>
    private static bool Query(Lazy<ICallGateSubscriber<bool>> gate)
    {
        try
        {
            return gate.Value.InvokeFunc();
        }
        catch (IpcError)
        {
            return false;
        }
    }
}

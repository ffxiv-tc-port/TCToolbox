using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 狩獵列車顯示到 Mappy：把 Hunt Helper 的狩獵列車清單同步成 Mappy 地圖上的標記。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 <b>純顯示，零自動化。</b>本模組只做「讀清單 → 放標記」：不追怪、不移動、不傳送、
/// 不碰目標、也<b>不呼叫 Hunt Helper 的 <c>ImportTrainList</c></b>（那會改寫使用者的列車清單）。
/// 唯一的副作用是 Mappy 地圖上多出一組來源為 <see cref="MarkerSource"/> 的標記。
/// </para>
/// <para>
/// 📌 <b>兩端都不必改。</b>Hunt Helper 的 <c>HH.*</c> 與 Mappy 的 <c>Mappy.*</c> 都是既有的
/// 對外 IPC，本模組是純粹的中介：<c>HH.GetTrainList</c> → <c>Mappy.AddMarker</c>。
/// 座標不需要換算——兩邊講的都是<b>地圖座標</b>（介面上顯示的 X/Y），
/// 詳見 <see cref="HuntHelperIpc.TrainMob"/> 的註解。
/// </para>
/// <para>
/// 🔑 <b>同步策略</b>：每隔幾秒（可設定）拉一次清單，算出一個內容簽章；簽章沒變就什麼都不做。
/// 有變動時交給 <see cref="MappyMarkerPublisher"/> 做<b>增量</b>比對（只加新的、只刪不要的、
/// 內容沒變的原地不動）。
/// <para>
/// 🔴 <b>週期性重推刻意不用 <c>ClearSource</c>。</b>那支是把整個來源從 Mappy 的表裡拿掉，
/// 下一次 <c>AddMarker</c> 會被判成「新的標記來源」而寫一行 <c>Information</c> 記錄；
/// 每分鐘保險重推一次、又有好幾個來源的話，那些沒有資訊量的行會把使用者的記錄檔淹掉。
/// <c>ClearSource</c> 只留給模組停用／卸載。
/// </para>
/// </para>
/// <para>
/// 🔴 <b>為什麼還要定期強制重建</b>：Mappy 被重新載入時它的標記表是空的，而我們的簽章還記得
/// 「已經同步過了」——那會變成<b>標記再也不會出現，而且完全沒有徵兆</b>。
/// 所以除了「Mappy 從無到有」的狀態轉換會重設簽章之外，另外每
/// <see cref="ForceResyncIntervalMs"/> 毫秒無條件全量重建一次當保險。
/// </para>
/// </remarks>
public sealed class HuntTrainOnMappy : TcModule
{
    public override string InternalName => "HuntTrainOnMappy";

    public override string DisplayName => "狩獵列車顯示到 Mappy";

    public override string Description =>
        "把 Hunt Helper 的狩獵列車清單同步成 Mappy 地圖上的標記，不必在兩個視窗之間對照座標。"
        + "純顯示：不追怪、不移動、不改動 Hunt Helper 的列車清單。需要同時安裝 Hunt Helper 與 Mappy。";

    public override ModuleCategory Category => ModuleCategory.Combat;

    public override bool HasConfigUI => true;

    /// <summary>
    /// 放到 Mappy 的標記來源名稱。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>不要改。</b>Mappy 端會用這個字串當鍵記住「這個來源要不要顯示」
    /// （<c>SystemConfig.IpcSourceEnabled</c>），改了之後使用者原本關掉的來源會變成一個
    /// 永遠留在 Mappy 設定裡的孤兒，而新來源會以「開」的狀態冒出來。
    /// </remarks>
    public const string MarkerSource = "TCToolbox.HuntTrain";

    /// <summary>無條件全量重建的間隔（毫秒）。理由見類別註解。</summary>
    private const int ForceResyncIntervalMs = 60_000;

    /// <summary>收到 <c>MarkSeen</c> 廣播後的最短重拉間隔（毫秒）。</summary>
    /// <remarks>
    /// 📌 一次「整團標記已看過」會連續打很多筆廣播，沒有這道下限的話每一筆都會觸發一次全量重建。
    /// </remarks>
    private const int MarkSeenMinIntervalMs = 1_000;

    /// <summary>設定畫面上重新探測「兩端在不在」的間隔（毫秒）。</summary>
    private const int UiProbeIntervalMs = 2_000;

    /// <summary>重新探測「有沒有別的外掛正在移動角色」的間隔（毫秒）。</summary>
    /// <remarks>
    /// 📌 這個值決定「按鈕變灰」的反應速度。500ms 已經比人按下去的反應快，
    /// 而它一次要打四支 IPC（Lifestream／vnavmesh 兩個／AutoDuty／BossMod），不宜每幀打。
    /// </remarks>
    private const int NavProbeIntervalMs = 500;

    /// <summary>
    /// 存活目標的圖示。
    /// </summary>
    /// <remarks>
    /// 📌 <b>來源＝Mappy 自己</b>：<c>Mappy/MapRenderer/MapRenderer.GameObject.cs</c> 對
    /// <c>ObjectKind.BattleNpc</c> 且 <c>IsBoss</c>（判準是 <c>BNpcBase.Rank is 2 or 6</c>，
    /// 也就是狩獵標記怪的階級）、且未被接戰時畫的就是 60402。
    /// 換句話說<b>同一隻怪只要進了 ObjectTable，Mappy 本來就會用這顆圖示畫它</b>，
    /// 我們只是把「還沒進視野」的那些也用同一顆畫出來，視覺上是一致的。
    /// <para>
    /// ✅ 2026-08-26 以 <c>tools/sqpack/path_exists.py</c> 離線直讀台服 <c>060000.win32.index</c>
    /// 確認 <c>ui/icon/060000/060402.tex</c> 存在（校準閘門通過）。
    /// </para>
    /// <para>
    /// ⚠️ <b>沒有離線驗證過的部分＝這顆圖示長什麼樣子。</b>圖示的「存在」與「語意」是兩件事，
    /// 而 <c>MapSymbol</c> 表（Mappy 用來反查圖示名稱的那張）只收地標類圖示，查不到 604xx 這段。
    /// ⇒ 所以這兩個 id 都做成<b>可設定</b>的，設定畫面上也直接把圖示畫出來讓使用者自己看。
    /// </para>
    /// </remarks>
    public const uint DefaultAliveIconId = 60402;

    /// <summary>
    /// 已擊殺目標的圖示（預設不顯示，見 <see cref="HuntTrainOnMappyConfig.ShowDead"/>）。
    /// </summary>
    /// <remarks>
    /// 📌 同樣取自 Mappy 的 <c>MapRenderer.GameObject.cs</c>：60424 是它對「一般敵對 NPC 且未接戰」
    /// 用的圖示——刻意選一顆比 60402 低調、又確定同屬一組地圖標記的，
    /// 讓「已經打完的」和「還沒打的」在地圖上分得開。
    /// ✅ 2026-08-26 同批離線確認 <c>ui/icon/060000/060424.tex</c> 存在。
    /// </remarks>
    public const uint DefaultDeadIconId = 60424;

    /// <summary>實際會用的存活圖示編號（設定值為哨兵 0 時＝內建預設值）。</summary>
    private uint EffectiveAliveIcon => Config.AliveIconId is 0 ? DefaultAliveIconId : Config.AliveIconId;

    /// <summary>實際會用的已擊殺圖示編號（設定值為哨兵 0 時＝內建預設值）。</summary>
    private uint EffectiveDeadIcon => Config.DeadIconId is 0 ? DefaultDeadIconId : Config.DeadIconId;

    /// <summary>橋接目前的狀態。<b>「不知道」是零值</b>，模組列上要看得見。</summary>
    private enum BridgeState
    {
        /// <summary>還沒同步過任何一次。</summary>
        Unknown = 0,

        /// <summary>Hunt Helper 沒裝或還沒載入。</summary>
        HuntHelperMissing,

        /// <summary>Mappy 沒裝或還沒載入。</summary>
        MappyMissing,

        /// <summary>對方回報的 IPC 版本比本模組寫的時候還舊。</summary>
        VersionTooOld,

        /// <summary>清單讀不出來（多半是對方的資料形狀變了）。</summary>
        ReadFailed,

        /// <summary>一切正常。</summary>
        Ok,
    }

    private HuntTrainOnMappyConfig Config => Plugin.Instance.Config.HuntTrainOnMappy;

    /// <summary>增量同步到 Mappy 的那一層（追蹤 handle、只動有變的那幾筆）。</summary>
    private readonly MappyMarkerPublisher publisher = new(MarkerSource);

    /// <summary>重複使用的緩衝區，免得每次同步都配一個新的 List。</summary>
    private readonly List<MappyMarkerPublisher.Marker> pending = [];

    /// <summary>上一次成功同步的內容簽章；空字串＝還沒同步過或已被作廢。</summary>
    private string lastSignature = string.Empty;

    /// <summary>
    /// 收到 <c>MarkSeen</c> 廣播後要重拉。
    /// </summary>
    /// <remarks>
    /// ⚠️ <c>volatile</c>：這個旗標是<b>由對方的 <c>SendMessage</c> 呼叫端</b>寫的，
    /// 而那不保證是框架執行緒。讀的一方在 <c>Framework.Update</c> 上。
    /// </remarks>
    private volatile bool markSeenPending;

    private bool mappyWasAvailable;
    private bool subscribedToMarkSeen;

    // ── 給 UI 看的快取狀態（只在框架執行緒上寫，繪製路徑只讀）────────────────
    private BridgeState state = BridgeState.Unknown;
    private int lastPlaced;
    private int lastRejected;
    private int lastSkippedDead;
    private int lastSkippedInvalid;
    private int lastTotal;
    private DateTime lastSyncLocal = DateTime.MinValue;

    /// <summary>訂閱用的委派實例。取消訂閱必須傳同一個實例，所以存起來。</summary>
    private Action<object>? markSeenHandler;

    // ── 「帶我去下一隻」──────────────────────────────────────────────────

    /// <summary>一次「前往」的請求。<b>只有數字與字串，不含任何原生指標。</b></summary>
    /// <param name="TerritoryId">目標區域的 <c>TerritoryType</c> 列號。</param>
    /// <param name="WorldX">目標點的<b>世界座標</b> X。</param>
    /// <param name="WorldZ">目標點的<b>世界座標</b> Z。</param>
    /// <param name="Label">給訊息用的名字。</param>
    private readonly record struct GoToRequest(uint TerritoryId, float WorldX, float WorldZ, string Label);

    /// <summary>
    /// 使用者按下「前往」之後、還沒送出去的請求。
    /// </summary>
    /// <remarks>
    /// 🔴🔴 <b>按鈕不直接打 IPC，一律先擱在這裡，交給 <see cref="OnUpdate"/> 去送。</b>
    /// 這是本模組既有的紀律（<c>DrawStatus</c> 那段註解寫著同一條）：對方的端點內部是
    /// <c>IpcFrameworkGate</c>／<c>RunOnFrameworkThread(…).Result</c>，
    /// 從繪製路徑呼叫等於在主執行緒上等一個要靠主執行緒才跑得到的 tick。
    /// 真的發生時的樣子是「按下去之後整個遊戲卡住好幾秒」，而不是任何一種錯誤訊息。
    /// <para>📌 只留最後一次：連按只會出發一次，不會排成一串。</para>
    /// </remarks>
    private GoToRequest? pendingGoTo;

    /// <summary>給設定畫面列清單用的快照（只在框架執行緒上寫，繪製路徑只讀）。</summary>
    /// <remarks>
    /// 🔴 <b>存在的理由與上面同一條</b>：<c>HH.GetTrainList</c> 不能從繪製路徑呼叫。
    /// 這份快照是 <see cref="Republish"/> 每次同步時順手抄下來的，
    /// 內容與剛剛畫到 Mappy 上的那一份完全一致（含已擊殺的，清單要看得到進度）。
    /// </remarks>
    private readonly List<HuntHelperIpc.TrainMob> uiSnapshot = [];

    /// <summary>有沒有別的外掛正在移動角色（框架執行緒上更新，繪製路徑只讀）。</summary>
    private bool navBusy;

    /// <summary>是誰在移動（<see cref="navBusy"/> 為 <see langword="false"/> 時是空字串）。</summary>
    private string navBusyWho = string.Empty;

    /// <summary>Lifestream 在不在。不在的話「前往」整個做不到，要在列上說清楚。</summary>
    private bool lifestreamAvailable;

    protected override void OnEnable()
    {
        markSeenHandler = OnMarkSeen;
        subscribedToMarkSeen = HuntHelperIpc.TrySubscribeMarkSeen(markSeenHandler);

        Svc.Framework.Update += OnUpdate;

        // 啟用後立刻同步一次，不要讓使用者等一個節流週期。
        Throttle.Reset(SyncThrottleKey);
        Throttle.Reset(ForceResyncThrottleKey);
        lastSignature = string.Empty;

        Svc.Log.Information(
            $"[{InternalName}] 模組啟用：同步間隔 {Config.RefreshSeconds} 秒、"
            + $"顯示已擊殺＝{Config.ShowDead}、圖示 存活 {EffectiveAliveIcon}／已擊殺 {EffectiveDeadIcon}"
            + $"（設定值 {Config.AliveIconId}／{Config.DeadIconId}，0＝跟隨內建預設）、"
            + $"MarkSeen 訂閱＝{subscribedToMarkSeen}");
    }

    protected override void OnDisable()
    {
        Svc.Framework.Update -= OnUpdate;

        if (markSeenHandler is not null)
        {
            HuntHelperIpc.TryUnsubscribeMarkSeen(markSeenHandler);
            markSeenHandler = null;
        }

        subscribedToMarkSeen = false;

        // 🔴 停用（含外掛卸載，Plugin.Dispose 會把每個模組 Disable 一次）時一定要把標記收乾淨，
        //    否則 Mappy 會一直畫著一組沒有人再更新的舊標記。
        publisher.Clear();

        lastSignature = string.Empty;
        mappyWasAvailable = false;
        markSeenPending = false;
        state = BridgeState.Unknown;

        uiSnapshot.Clear();
        pendingGoTo = null;
        navBusy = false;
        navBusyWho = string.Empty;
        lifestreamAvailable = false;
    }

    private string SyncThrottleKey => $"TCToolbox.{InternalName}.Sync";

    private string ForceResyncThrottleKey => $"TCToolbox.{InternalName}.ForceResync";

    private string MarkSeenThrottleKey => $"TCToolbox.{InternalName}.MarkSeen";

    private string NavProbeThrottleKey => $"TCToolbox.{InternalName}.NavProbe";

    /// <summary>
    /// <c>MarkSeen</c> 廣播的處理常式。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>這支絕對不能擲例外。</b>它是被 Hunt Helper 的 <c>SendMessage</c> 直接叫的，
    /// 例外會一路傳回對方的事件裡去——我們的 bug 會表現成<b>別人家的外掛壞掉</b>。
    /// <para>
    /// 🔴 這裡<b>只打旗標，不做任何 IPC 呼叫</b>。除了上面那條之外還有第二個理由：
    /// <c>HH.GetTrainList</c> 內部是 <c>RunOnFrameworkThread(…).Result</c>，
    /// 而這支的執行緒無法保證，在這裡呼叫它有阻塞的風險。真正的重拉留給 <see cref="OnUpdate"/>。
    /// </para>
    /// </remarks>
    private void OnMarkSeen(object _)
    {
        try
        {
            markSeenPending = true;
        }
        catch
        {
            // 理論上寫一個 bool 不會失敗，但這支的呼叫端是別人的外掛，不賭。
        }
    }

    private void OnUpdate(IFramework framework)
    {
        // 未登入時遊戲端沒有任何東西會變，省下輪詢。已放上去的標記留著不動——
        // Mappy 只在對應的地圖上畫它們，不會造成困擾。
        if (!Svc.ClientState.IsLoggedIn) return;

        // 🔴 這兩件事都在框架執行緒上做：繪製路徑只讀快取。
        if (Throttle.Pass(NavProbeThrottleKey, NavProbeIntervalMs))
        {
            navBusy = ExternalNav.TryGetActiveMover(out navBusyWho);
            lifestreamAvailable = ExternalNav.IsLifestreamAvailable();
        }

        // 使用者按下的「前往」在這裡才真的送出去（理由見 pendingGoTo）。
        if (pendingGoTo is { } request)
        {
            pendingGoTo = null;
            ExecuteGoTo(request);
        }

        var forced = false;

        if (markSeenPending)
        {
            // 一次「整團已看過」會連打很多筆廣播，這裡壓成最多每秒一次。
            if (Throttle.Pass(MarkSeenThrottleKey, MarkSeenMinIntervalMs))
            {
                markSeenPending = false;
                forced = true;
            }
        }

        if (!forced && !Throttle.Pass(SyncThrottleKey, Math.Clamp(Config.RefreshSeconds, 1, 300) * 1000)) return;

        Sync(forced);
    }

    private void Sync(bool forced)
    {
        if (!HuntHelperIpc.TryGetVersion(out var huntVersion))
        {
            // Hunt Helper 不在了：把標記收掉，不要留一組沒有人維護的舊資料在地圖上。
            if (state != BridgeState.HuntHelperMissing) publisher.Clear();

            state = BridgeState.HuntHelperMissing;
            lastSignature = string.Empty;

            // 🔴 清單也要清掉：留著一份沒有人再更新的舊清單，使用者會照著它按「前往」。
            uiSnapshot.Clear();
            return;
        }

        if (!MappyMarkerIpc.TryGetVersion(out var mappyVersion))
        {
            state = BridgeState.MappyMissing;
            mappyWasAvailable = false;

            // 🔴 作廢簽章＋忘掉 handle：Mappy 回來的時候它的標記表是空的，必須重放一次。
            //    手上的 handle 全部失效，留著會讓下一次同步以為「已經放上去了」。
            lastSignature = string.Empty;
            publisher.Forget();
            return;
        }

        // Mappy 從「不在」變成「在」＝它的標記表是空的，一定要重放。
        if (!mappyWasAvailable)
        {
            lastSignature = string.Empty;
            publisher.Forget();
            mappyWasAvailable = true;
        }

        if (huntVersion < HuntHelperIpc.SupportedVersion || mappyVersion < MappyMarkerIpc.SupportedVersion)
        {
            state = BridgeState.VersionTooOld;
            return;
        }

        if (!HuntHelperIpc.TryGetTrainList(out var train))
        {
            state = BridgeState.ReadFailed;
            return;
        }

        var signature = BuildSignature(train);

        // 保險：即使簽章沒變，也定期整組重放一次（Mappy 可能在我們沒看見的時候被重載過）。
        var forceFull = forced || Throttle.Pass(ForceResyncThrottleKey, ForceResyncIntervalMs);

        var contentChanged = signature != lastSignature;

        if (!forceFull && !contentChanged && state == BridgeState.Ok) return;

        // 📌 只有「內容真的變了」才寫記錄：定期的保險重放每分鐘一次，
        //    全部都寫的話會把使用者的 log 洗掉，而那些行沒有任何新資訊。
        Republish(train, contentChanged, forceFull);
        lastSignature = signature;
    }

    /// <summary>把清單同步到 Mappy（增量；<paramref name="forceFull"/> 時整組刪掉重加）。</summary>
    /// <param name="train">要放上去的清單。</param>
    /// <param name="log">是否寫一行記錄（只有內容變動時才寫，見呼叫端）。</param>
    /// <param name="forceFull">保險用的全量重推（Mappy 可能在我們沒看見的時候被重載過）。</param>
    private void Republish(List<HuntHelperIpc.TrainMob> train, bool log, bool forceFull)
    {
        var skippedDead = 0;
        var skippedInvalid = 0;

        var aliveIcon = EffectiveAliveIcon;
        var deadIcon = EffectiveDeadIcon;

        pending.Clear();

        // 🔑 清單快照抄的是<b>原始清單</b>，不是下面篩過的那一份：地圖上可以只留還沒打的，
        //    但設定畫面的清單要看得到整趟列車的進度（哪幾隻已經打完了）。
        uiSnapshot.Clear();
        uiSnapshot.AddRange(train);

        foreach (var mob in train)
        {
            if (mob.Dead && !Config.ShowDead)
            {
                skippedDead++;
                continue;
            }

            // Mappy 對這些一律回 0；先擋掉，讓「被拒絕」的計數只剩下真正意外的狀況。
            if (mob.MapID is 0 || !float.IsFinite(mob.Position.X) || !float.IsFinite(mob.Position.Y))
            {
                skippedInvalid++;
                continue;
            }

            // 🔑 鍵要跨次穩定：同一隻怪在同一張圖的同一個分區永遠是同一個鍵，
            //    這樣「只有死活狀態變了」就只會動到那一筆。
            pending.Add(new MappyMarkerPublisher.Marker(
                $"{mob.MapID}:{mob.MobID}:{mob.Instance}",
                mob.MapID,
                mob.Position,
                mob.Dead ? deadIcon : aliveIcon,
                BuildTooltip(mob)));
        }

        publisher.Publish(pending, forceFull);
        pending.Clear();

        lastPlaced = publisher.Placed;
        lastRejected = publisher.LastRejected;
        lastSkippedDead = skippedDead;
        lastSkippedInvalid = skippedInvalid + publisher.LastDuplicateKeys;
        lastTotal = train.Count;
        lastSyncLocal = DateTime.Now;
        state = BridgeState.Ok;

        if (!log) return;

        // 🔴 Information 級：使用者跑 LogLevel 1，盲區只有 Verbose,Debug 收得到但單檔數十萬行會淹沒，
        //    而「到底同步了幾筆、被拒絕幾筆」是事後看實機記錄時唯一問得到的地方。
        Svc.Log.Information(
            $"[{InternalName}] 同步狩獵列車：清單 {lastTotal} 筆 → 地圖上 {lastPlaced} 筆"
            + $"（本次異動 {publisher.LastIpcCalls} 次）"
            + (lastSkippedDead > 0 ? $"、略過已擊殺 {lastSkippedDead} 筆" : string.Empty)
            + (lastSkippedInvalid > 0 ? $"、資料不完整 {lastSkippedInvalid} 筆" : string.Empty)
            + (lastRejected > 0 ? $"、被 Mappy 拒絕 {lastRejected} 筆" : string.Empty));
    }

    /// <summary>
    /// 內容簽章：只要這串沒變，地圖上該畫的東西就沒變。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>設定值也要進簽章</b>——不然使用者改了「顯示已擊殺」或圖示 id 之後，
    /// 因為清單本身沒變而完全不會重畫，表現成「設定沒有作用」。
    /// </remarks>
    private string BuildSignature(List<HuntHelperIpc.TrainMob> train)
    {
        var sb = new StringBuilder();

        sb.Append(Config.ShowDead ? '1' : '0').Append('|')
          .Append(EffectiveAliveIcon).Append('|')
          .Append(EffectiveDeadIcon).Append('|');

        foreach (var mob in train)
        {
            sb.Append(mob.MobID).Append(',')
              .Append(mob.MapID).Append(',')
              .Append(mob.Instance).Append(',')
              .Append(mob.Position.X.ToString("F2", CultureInfo.InvariantCulture)).Append(',')
              .Append(mob.Position.Y.ToString("F2", CultureInfo.InvariantCulture)).Append(',')
              .Append(mob.Dead ? '1' : '0').Append(';');
        }

        return sb.ToString();
    }

    private static string BuildTooltip(HuntHelperIpc.TrainMob mob)
    {
        var name = string.IsNullOrWhiteSpace(mob.Name) ? $"#{mob.MobID}" : mob.Name;

        var sb = new StringBuilder();
        sb.Append("狩獵列車：").Append(name);

        if (mob.Instance > 0) sb.Append("（第 ").Append(mob.Instance).Append(" 分區）");

        if (mob.Dead) sb.Append('\n').Append("狀態：已擊殺");

        sb.Append('\n').Append("最後回報：").Append(FormatLastSeen(mob.LastSeenUTC));

        return sb.ToString();
    }

    /// <summary>
    /// 把 <c>LastSeenUTC</c> 轉成當地時間字串。
    /// </summary>
    /// <remarks>
    /// ⚠️ 這個值走過一次 JSON 來回（見 <see cref="HuntHelperIpc"/>），<c>Kind</c> 有可能掉成
    /// <c>Unspecified</c>——那時候直接 <c>ToLocalTime()</c> 會把它<b>當成當地時間</b>再加一次時差，
    /// 結果是靜默地差好幾個小時。所以 <c>Unspecified</c> 一律當成 UTC。
    /// <para>
    /// 📌 沒有時間資料時顯示「？」而不是某個看起來很具體的時間——「不知道」要看得出來是不知道。
    /// </para>
    /// </remarks>
    private static string FormatLastSeen(DateTime lastSeenUtc)
    {
        if (lastSeenUtc == default) return "？";

        var utc = lastSeenUtc.Kind switch
        {
            DateTimeKind.Unspecified => DateTime.SpecifyKind(lastSeenUtc, DateTimeKind.Utc),
            DateTimeKind.Local => lastSeenUtc.ToUniversalTime(),
            _ => lastSeenUtc,
        };

        return utc.ToLocalTime().ToString("MM/dd HH:mm", CultureInfo.InvariantCulture);
    }

    public override ModuleNotice? RowNotice
    {
        get
        {
            // 模組關著的時候沒有話要說（快取的狀態已經停止更新，講出來只會誤導）。
            if (!IsEnabled) return null;

            return state switch
            {
                BridgeState.HuntHelperMissing => new ModuleNotice(
                    ModuleNoticeLevel.Unknown,
                    "Hunt Helper 未載入",
                    "找不到 Hunt Helper 的狩獵列車 IPC，所以沒有清單可以同步。模組會靜靜地等它出現，不需要重開。"),

                BridgeState.MappyMissing => new ModuleNotice(
                    ModuleNoticeLevel.Unknown,
                    "Mappy 未載入",
                    "找不到 Mappy 的標記 IPC，所以標記沒有地方可以放。模組會靜靜地等它出現，不需要重開。"),

                BridgeState.VersionTooOld => new ModuleNotice(
                    ModuleNoticeLevel.Warning,
                    "對方的 IPC 版本過舊",
                    "Hunt Helper 或 Mappy 回報的 IPC 版本比本模組需要的還舊，為了避免傳錯資料，同步已停下來。"),

                BridgeState.ReadFailed => new ModuleNotice(
                    ModuleNoticeLevel.Warning,
                    "讀不到狩獵列車清單",
                    "Hunt Helper 在，但清單讀不出來——多半是它的資料格式改過了。詳細例外請看記錄（關鍵字 HuntHelperIpc）。"),

                BridgeState.Unknown => new ModuleNotice(
                    ModuleNoticeLevel.Unknown,
                    "尚未同步",
                    "模組剛啟用，還沒完成第一次同步。"),

                BridgeState.Ok when lastRejected > 0 => new ModuleNotice(
                    ModuleNoticeLevel.Warning,
                    $"{lastRejected} 筆被 Mappy 拒絕",
                    "Mappy 端每個來源最多 512 筆標記，超過的會被拒絕；圖示 id 設成 0 也會被拒絕。"),

                _ => null,
            };
        }
    }

    public override void DrawConfig()
    {
        DrawStatus();

        ImGui.Separator();

        var showDead = Config.ShowDead;
        if (ImGui.Checkbox("一併顯示已擊殺的目標", ref showDead))
        {
            Config.ShowDead = showDead;
            Plugin.Instance.Config.Save();
            lastSignature = string.Empty;
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("預設不顯示，地圖上只留還沒打的。打開的話已擊殺的目標會用另一顆圖示畫出來，可以看出列車的進度。");

        ImGui.SetNextItemWidth(160f);
        var seconds = Config.RefreshSeconds;
        if (ImGui.SliderInt("同步間隔（秒）", ref seconds, 1, 60))
        {
            Config.RefreshSeconds = seconds;
            Plugin.Instance.Config.Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("每隔這麼久向 Hunt Helper 拉一次清單。Hunt Helper 每標記一隻怪也會即時通知，所以這個值不必調得很小。");

        ImGui.Separator();
        DrawTrainList();
        ImGui.Separator();

        if (!ImGui.CollapsingHeader("圖示")) return;

        using var indent = ImRaii.PushIndent();

        ImGui.TextDisabled("預設值取自 Mappy 畫同類目標時用的圖示。覺得不好認就改成別的編號。");

        DrawIconSetting("存活目標", Config.AliveIconId, DefaultAliveIconId, value =>
        {
            Config.AliveIconId = value;
            Plugin.Instance.Config.Save();
            lastSignature = string.Empty;
        });

        DrawIconSetting("已擊殺目標", Config.DeadIconId, DefaultDeadIconId, value =>
        {
            Config.DeadIconId = value;
            Plugin.Instance.Config.Save();
            lastSignature = string.Empty;
        });
    }

    /// <summary>
    /// 設定畫面上的狀態列。
    /// </summary>
    /// <remarks>
    /// 🔴 這裡<b>只呼叫 <c>GetVersion</c>，絕不呼叫 <c>GetTrainList</c></b>：後者內部是
    /// <c>RunOnFrameworkThread(…).Result</c>，從繪製路徑呼叫有阻塞主執行緒的風險。
    /// 需要清單內容的資訊一律用 <see cref="OnUpdate"/> 快取下來的數字。
    /// </remarks>
    private void DrawStatus()
    {
        // 模組關著時 OnUpdate 沒在跑，快取是死的——這裡自己（節流地）探一次，
        // 讓使用者在還沒啟用之前就看得出來「兩端在不在」。
        if (!IsEnabled && Throttle.Pass($"TCToolbox.{InternalName}.UiProbe", UiProbeIntervalMs))
        {
            var huntOk = HuntHelperIpc.TryGetVersion(out _);
            var mappyOk = MappyMarkerIpc.TryGetVersion(out _);

            state = !huntOk ? BridgeState.HuntHelperMissing
                : !mappyOk ? BridgeState.MappyMissing
                : BridgeState.Unknown;
        }

        switch (state)
        {
            case BridgeState.HuntHelperMissing:
                ImGui.TextDisabled("Hunt Helper 未載入——沒有狩獵列車可以讀。");
                return;

            case BridgeState.MappyMissing:
                ImGui.TextDisabled("Mappy 未載入——標記沒有地方可以放。");
                return;

            case BridgeState.VersionTooOld:
                ImGui.TextDisabled("對方的 IPC 版本過舊，同步已停下來。");
                return;

            case BridgeState.ReadFailed:
                ImGui.TextDisabled("Hunt Helper 在，但清單讀不出來（詳見記錄，關鍵字 HuntHelperIpc）。");
                return;

            case BridgeState.Unknown:
                ImGui.TextDisabled(IsEnabled ? "尚未完成第一次同步。" : "兩端都在，啟用模組後開始同步。");
                return;

            case BridgeState.Ok:
            default:
                break;
        }

        ImGui.TextUnformatted($"目前放上 {lastPlaced} 筆標記（清單共 {lastTotal} 筆）。");

        if (!ImGui.IsItemHovered()) return;

        var sb = new StringBuilder();
        sb.Append("最後同步：").Append(lastSyncLocal == DateTime.MinValue ? "？" : lastSyncLocal.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
        sb.Append('\n').Append("略過已擊殺：").Append(lastSkippedDead).Append(" 筆");
        sb.Append('\n').Append("資料不完整：").Append(lastSkippedInvalid).Append(" 筆");
        sb.Append('\n').Append("被 Mappy 拒絕：").Append(lastRejected).Append(" 筆");
        sb.Append('\n').Append("MarkSeen 即時通知：").Append(subscribedToMarkSeen ? "已訂閱" : "未訂閱（只靠定時輪詢）");
        sb.Append('\n').Append("標記來源：").Append(MarkerSource).Append("（可在 Mappy 的設定裡單獨關掉）");

        ImGui.SetTooltip(sb.ToString());
    }

    /// <summary>
    /// 狩獵列車清單，每一列一顆「前往」。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 🔴 <b>手動觸發，而且只走到那裡為止。</b>按下去只做一件事：請 Lifestream 把角色帶過去。
    /// 不選取目標、不開打、不自動接續下一隻——那些都是無人值守的自動化，本模組刻意不做。
    /// </para>
    /// <para>
    /// 🔑 <b>「別的外掛正在移動」寫在表格上方一行，不是每一列各寫一次。</b>
    /// 那是一個全域狀態，每一列都一樣；重複 20 次只是噪音，而使用者要看的是按鈕為什麼是灰的。
    /// </para>
    /// </remarks>
    private void DrawTrainList()
    {
        if (!ImGui.CollapsingHeader($"狩獵列車清單（{uiSnapshot.Count}）###huntTrainList")) return;

        if (!IsEnabled)
        {
            ImGui.TextDisabled("? 模組還沒啟用，所以還沒有清單可以列（清單是同步時順手抄下來的）。");
            return;
        }

        if (uiSnapshot.Count == 0)
        {
            ImGui.TextDisabled("Hunt Helper 的狩獵列車目前是空的。");
            return;
        }

        DrawTravelControls();

        if (!ImGui.BeginTable("##huntTrainRows", 4,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.ScrollY,
                new Vector2(0f, 220f)))
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("目標", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("區域", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("座標", ImGuiTableColumnFlags.WidthFixed, 110f);
        ImGui.TableSetupColumn("前往", ImGuiTableColumnFlags.WidthFixed, 64f);
        ImGui.TableHeadersRow();

        for (var i = 0; i < uiSnapshot.Count; i++)
        {
            var mob = uiSnapshot[i];
            ImGui.TableNextRow();

            var name = string.IsNullOrWhiteSpace(mob.Name) ? $"#{mob.MobID}" : mob.Name;

            ImGui.TableNextColumn();
            if (mob.Dead) ImGui.TextDisabled($"{name}（已擊殺）");
            else ImGui.TextUnformatted(name);

            ImGui.TableNextColumn();
            var zone = ZoneName(mob.TerritoryID);
            // 「不知道」要在列上看得見，不要畫一個空白格假裝沒事。
            if (zone.Length == 0) ImGui.TextDisabled($"? 區域 #{mob.TerritoryID}");
            else if (mob.Instance > 0) ImGui.TextUnformatted($"{zone}（第 {mob.Instance} 分區）");
            else ImGui.TextUnformatted(zone);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(
                $"{mob.Position.X.ToString("F1", CultureInfo.InvariantCulture)}, "
                + mob.Position.Y.ToString("F1", CultureInfo.InvariantCulture));

            ImGui.TableNextColumn();
            DrawGoToButton(mob, $"##goto{i}");
        }

        ImGui.EndTable();
    }

    /// <summary>清單上方那一排：飛行開關、「下一隻」，以及為什麼按鈕是灰的。</summary>
    private void DrawTravelControls()
    {
        var next = uiSnapshot.FindIndex(x => !x.Dead);

        using (ImRaii.Disabled(!CanTravel || next < 0))
        {
            if (ImGui.Button("前往下一隻未擊殺的") && next >= 0)
                RequestGoTo(uiSnapshot[next]);
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "清單由上往下第一隻還沒標記為已擊殺的目標。" + Environment.NewLine
                + "「已擊殺」是 Hunt Helper 那邊的標記，本模組只讀不寫。" + Environment.NewLine
                + "到了就停——不選取目標、不開打、不會自己接著跑下一隻。");
        }

        ImGui.SameLine();

        var fly = Config.FlyWhenTravelling;
        if (ImGui.Checkbox("允許飛行", ref fly))
        {
            Config.FlyWhenTravelling = fly;
            Plugin.Instance.Config.Save();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "區域不可飛、或你沒乘坐騎時，Lifestream 會自己退回地面路線，不會因此失敗。"
                + Environment.NewLine + "本外掛不會替你上坐騎。");
        }

        // 🔑 按鈕為什麼是灰的，要在列上看得見，不能只放 tooltip。
        if (navBusy)
        {
            ImGui.TextDisabled($"別的外掛正在移動（{navBusyWho}），先讓它跑完。");
        }
        else if (!lifestreamAvailable)
        {
            ImGui.TextDisabled("未偵測到 Lifestream —— 「前往」需要它（以及 vnavmesh）才能運作。");
        }
        else if (next < 0)
        {
            ImGui.TextDisabled("清單上每一隻都已標記為已擊殺。");
        }
    }

    /// <summary>能不能按「前往」（Lifestream 在，而且沒有別人在移動角色）。</summary>
    private bool CanTravel => lifestreamAvailable && !navBusy;

    private void DrawGoToButton(HuntHelperIpc.TrainMob mob, string id)
    {
        using var disabled = ImRaii.Disabled(!CanTravel);
        if (ImGui.SmallButton($"前往{id}")) RequestGoTo(mob);
    }

    /// <summary>把一筆「前往」擱進佇列，等框架執行緒去送（理由見 <see cref="pendingGoTo"/>）。</summary>
    private void RequestGoTo(HuntHelperIpc.TrainMob mob)
    {
        var name = string.IsNullOrWhiteSpace(mob.Name) ? $"#{mob.MobID}" : mob.Name;

        if (mob.TerritoryID == 0)
        {
            Svc.Chat.PrintError($"[TC Toolbox] 「{name}」沒有區域資料，無法前往。");
            return;
        }

        // 🔴 Hunt Helper 給的是<b>地圖座標</b>，Lifestream 要的是<b>世界座標</b>。
        //    傳錯不會有錯誤訊息，角色只會被帶到那張圖上不相干的地方。
        if (!MapCoords.TryHuntHelperMapToWorld(mob.TerritoryID, mob.Position, out var worldX, out var worldZ))
        {
            Svc.Chat.PrintError($"[TC Toolbox] 「{name}」的座標換算不出來，無法前往。");
            return;
        }

        pendingGoTo = new GoToRequest(mob.TerritoryID, worldX, worldZ, name);
    }

    /// <summary>真的把請求送給 Lifestream。<b>只在框架執行緒上呼叫。</b></summary>
    private void ExecuteGoTo(GoToRequest request)
    {
        // 從按下按鈕到這一幀之間可能有人開始移動了，所以這裡再確認一次。
        // 🔑 這不是重複：按鈕的灰階用的是最多 500ms 前的快取，這裡是當下的真值。
        if (ExternalNav.TryGetActiveMover(out var mover))
        {
            Svc.Chat.PrintError($"[TC Toolbox] {mover} 正在移動，這次的「前往」沒有送出。");
            return;
        }

        var fly = Config.FlyWhenTravelling;

        if (!ExternalNav.TryGoToMapPoint(request.TerritoryId, request.WorldX, request.WorldZ, fly, out var accepted))
        {
            Svc.Chat.PrintError("[TC Toolbox] 找不到 Lifestream，無法前往。");
            return;
        }

        if (!accepted)
        {
            // ⚠️ 這是<b>正常結果不是錯誤</b>：Lifestream 正在忙、角色不可互動、vnavmesh 沒載入，
            //    或那個區域沒有任何已解鎖的乙太之光。重試沒有用，要讓使用者知道「沒開始」。
            Svc.Chat.PrintError(
                $"[TC Toolbox] Lifestream 沒有接下前往「{request.Label}」的請求"
                + "（可能它正在忙、角色不可互動、vnavmesh 未載入，或該區沒有已解鎖的乙太之光）。");
        }
        else
        {
            Svc.Chat.Print($"[TC Toolbox] 出發前往「{request.Label}」。");
        }

        // 🔴 Information 級：座標換算是這個功能最可能靜默出錯的地方，而「角色被帶到哪裡」
        //    事後只能靠這一行對證。
        Svc.Log.Information(
            $"[{InternalName}] 前往「{request.Label}」：territory={request.TerritoryId}"
            + $" 世界座標=({request.WorldX.ToString("F1", CultureInfo.InvariantCulture)},"
            + $" {request.WorldZ.ToString("F1", CultureInfo.InvariantCulture)})"
            + $" 飛行={fly} Lifestream 接受={accepted}");
    }

    /// <summary>區域名稱；查不到回空字串（<b>不回一個看起來正常的假名字</b>）。</summary>
    /// <remarks>📌 每幀每列都會問一次，所以查到的結果快取起來。</remarks>
    private static readonly Dictionary<uint, string> ZoneNameCache = [];

    private static string ZoneName(uint territoryId)
    {
        if (territoryId == 0) return string.Empty;
        if (ZoneNameCache.TryGetValue(territoryId, out var cached)) return cached;

        var name = string.Empty;
        try
        {
            name = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>()
                      .GetRowOrDefault(territoryId)?.PlaceName.ValueNullable?.Name.ExtractText()
                   ?? string.Empty;
        }
        catch (Exception ex)
        {
            // 🔴 這支在 ImGui 的 Draw 路徑上，擲例外的代價是整個模組的設定區被錯誤面板取代。
            Svc.Log.Warning(ex, $"[HuntTrainOnMappy] 查 territory {territoryId} 的名稱失敗");
        }

        ZoneNameCache[territoryId] = name;
        return name;
    }
    private static void DrawIconSetting(string label, uint current, uint defaultValue, Action<uint> apply)
    {
        using var id = ImRaii.PushId(label);

        // 哨兵 0 ＝ 跟隨內建預設值；畫圖與輸入框一律顯示「實際會用的編號」，不要讓使用者看到 0。
        var effective = current is 0 ? defaultValue : current;

        var wrap = GameIcons.TryGet(effective);
        if (wrap != null)
        {
            ImGui.Image(wrap.Handle, new Vector2(24f, 24f));
            ImGui.SameLine();
        }

        ImGui.SetNextItemWidth(120f);
        var value = (int)effective;
        if (ImGui.InputInt(label, ref value))
        {
            // 🔴 清空／輸入 0 一律寫回哨兵 0，不要寫具體常數：寫具體常數＝把編號烙死，
            //    日後修正 Default…IconId 對這個人靜默無效。
            apply(value <= 0 ? 0u : (uint)value);
        }

        ImGui.SameLine();
        if (ImGui.SmallButton($"預設（目前 {defaultValue}）")) apply(0);

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                $"寫回「跟隨內建預設值」（目前是 {defaultValue}）。\n"
                + "跟隨的意思是：日後內建預設值若有修正，你會自動吃到。");
        }
    }
}

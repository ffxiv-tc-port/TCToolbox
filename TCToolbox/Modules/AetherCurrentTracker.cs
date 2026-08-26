using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 風脈泉一覽：列出目前所在區域的每一個風脈泉、是否已共鳴，並提供標旗／場地標記／導航。
/// </summary>
/// <remarks>
/// <para>
/// 🔴🔴 <b>移動一律走「傳送最近的乙太之光 ＋ vnavmesh 走過去」，不做記憶體瞬移。</b>
/// DailyRoutines 原版的 <c>TeleportTo()</c> 是 <c>MovementManager.TPSmart_BetweenZone</c>
/// ——直接改寫角色座標的記憶體瞬移，是本艦隊的紅線，<b>整段丟棄</b>。
/// 這裡的導航形狀與 <see cref="CustomDeliveriesOverview"/> 完全相同
/// （Lifestream IPC 傳送 → 等進圖 → vnavmesh <c>PathfindAndMoveTo</c>），
/// 那是已經出貨、使用者用過的那條路。
/// </para>
/// <para>
/// 📌 <b>資料鏈</b>（全部走 Lumina，零 hook、零特徵碼、不寫記憶體）：
/// <list type="number">
/// <item><c>AetherCurrentCompFlgSet</c>：一列＝一個區域，欄位 <c>AetherCurrents</c> 是該區
/// 所有風脈泉的 <c>AetherCurrent</c> 參照。</item>
/// <item><c>AetherCurrent</c>：台服 7.20 共 <b>448 列</b>，列號是 <c>EventId</c>
/// （<c>0x2B0000</c>..<c>0x2B01BF</c>，已對 <c>exd-tc/7.20/AetherCurrent.csv</c> 查證）。
/// 只有兩個欄位：<c>#</c> 與 <c>Quest</c>。
/// <b><c>Quest</c> 非 0 ＝任務給的</b>（沒有地圖上的實體，走過去也沒用）；
/// <b><c>Quest</c> ＝ 0 ＝地圖上的實體風脈泉</b>，才查得到座標。</item>
/// <item>實體的：拿 <c>AetherCurrent</c> 列號去 <c>EObj.Data</c> 反查 <c>EObj</c> 列號，
/// 再用該列號去 <c>Level</c>（<c>Object</c> 指向 <c>EObj</c> 的那些列）拿世界座標。</item>
/// <item>共鳴狀態：<c>PlayerState.UnlockedAetherCurrentsBitArray</c>，
/// 索引＝<c>列號 - 0x2B0000</c>。</item>
/// </list>
/// </para>
/// <para>
/// 🔴 <b>查不到座標的一律顯示「？」，絕不顯示 (0, 0, 0)。</b>
/// 把未知畫成 0 會讓使用者被導到地圖原點，而那看起來像個正常答案。
/// </para>
/// </remarks>
public sealed unsafe class AetherCurrentTracker : TcModule
{
    public override string InternalName => "AetherCurrentTracker";
    public override string DisplayName => "風脈泉一覽";

    public override string Description =>
        "列出目前所在區域的每一個風脈泉與是否已共鳴，可以標旗、插場地標記、或直接導航過去。" +
        "任務給的風脈泉會另外標明（那種沒有地圖實體，走過去也拿不到）。" +
        "裝了 Mappy 的話還會把全部區域「還沒共鳴」的風脈泉畫到地圖上（可關）。" +
        "移動只走「Lifestream 傳送最近水晶 ＋ vnavmesh 走過去」，不做任何瞬移。唯讀顯示，不會自己動作。";

    public override ModuleCategory Category => ModuleCategory.Misc;

    public override bool HasConfigUI => true;

    /// <summary><c>AetherCurrent</c> 列號的基底（列號是 EventId）。</summary>
    private const uint AetherCurrentIdBase = 0x2B0000;

    /// <summary>場地標記的數量上限。</summary>
    private const int FieldMarkerSlots = 8;

    /// <summary>
    /// 放到 Mappy 的標記來源名稱。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>不要改。</b>Mappy 端拿這個字串當鍵記住「這個來源要不要顯示」，
    /// 改了之後使用者原本關掉的來源會變成孤兒，而新來源會以「開」的狀態冒出來。
    /// </remarks>
    public const string MarkerSource = "TCToolbox.AetherCurrents";

    /// <summary>
    /// 風脈泉在 Mappy 上的圖示。
    /// </summary>
    /// <remarks>
    /// 📌 <b>來源＝Mappy 自己</b>：<c>Mappy/MapRenderer/MapRenderer.GameObject.cs</c> 對
    /// <c>ObjectKind.EventObj</c> 且事件內容是 <c>EventHandlerContent.AetherCurrent</c> 的物件
    /// 畫的就是 60653。換句話說<b>風脈泉只要進了 ObjectTable，Mappy 本來就用這顆圖示畫它</b>——
    /// 本模組做的事情是把「還沒走到附近、所以不在 ObjectTable 裡」的那些也用同一顆畫出來，
    /// 視覺上完全一致。
    /// <para>
    /// ✅ 2026-08-26 以 <c>tools/sqpack/path_exists.py</c> 離線直讀台服 <c>060000.win32.index</c>
    /// 確認 <c>ui/icon/060000/060653.tex</c> 存在（校準閘門通過，另附一個必不存在的負對照）。
    /// </para>
    /// <para>
    /// ⚠️ <b>沒有離線驗證過的部分＝這顆圖示長什麼樣子。</b>圖示的「存在」與「語意」是兩件事，
    /// 而 <c>MapSymbol</c> 表（地圖圖示的官方名稱對照）只收地標類圖示，查不到 606xx 這段。
    /// ⇒ 做成可設定的，設定畫面也直接把圖示畫出來讓使用者自己看。
    /// </para>
    /// </remarks>
    public const uint DefaultMappyIconId = 60653;

    /// <summary>向 Mappy 重新同步的間隔（毫秒）。</summary>
    /// <remarks>
    /// 📌 共鳴狀態只會在玩家親自走過去互動的那一刻改變，所以這個值不需要很小；
    /// 而且比對本身是「算簽章 → 沒變就直接 return」，沒變時的成本只有一次表掃描。
    /// </remarks>
    private const int MappySyncIntervalMs = 5_000;

    /// <summary>無條件全量重推的間隔（毫秒）。理由見 <see cref="MappyMarkerPublisher"/>。</summary>
    private const int MappyForceResyncIntervalMs = 60_000;

    /// <summary>偵測到 Mappy 不在時，下一次重探之前先退避多久（毫秒）。</summary>
    /// <remarks>
    /// ⚠️ Dalamud 的 IPC 在對方沒註冊時是<b>擲例外</b>，不是回 null。沒裝 Mappy 的人本來就佔多數，
    /// 讓他們每 5 秒付一次「擲＋接」的代價沒有意義（而且永遠不會停）。
    /// 🔴 退避要用 <see cref="Throttle.Block"/> 不能用 <see cref="Throttle.Pass"/>——
    /// <c>Pass</c> 在鍵還在冷卻中時根本不寫入，而那正是我們想設退避的那一刻。
    /// </remarks>
    private const int MappyMissingBackoffMs = 60_000;

    /// <summary>一個風脈泉的靜態資料。</summary>
    /// <param name="AetherCurrentId"><c>AetherCurrent</c> 列號（EventId）。</param>
    /// <param name="QuestId">任務型的任務列號；0＝地圖上的實體。</param>
    /// <param name="Territory">所在區域（實體型才有意義）。</param>
    /// <param name="Map">所在地圖。</param>
    /// <param name="Position">世界座標。<b><paramref name="HasPosition"/> 為 false 時這個值沒有意義。</b></param>
    /// <param name="HasPosition">查不查得到座標。</param>
    private sealed record Point(
        uint AetherCurrentId, uint QuestId, ushort Territory, uint Map,
        Vector3 Position, bool HasPosition)
    {
        public bool IsQuest => QuestId != 0;

        /// <summary>畫面上的序號來源：同區內的順序（1 起算），由建表時決定。</summary>
        public int IndexInZone { get; init; }

        public string QuestName { get; init; } = string.Empty;

        /// <summary>所在區域的繁中名稱（建表時查一次；查不到就是空字串）。</summary>
        public string ZoneName { get; init; } = string.Empty;
    }

    /// <summary><c>Level</c> 表單一列的落點。</summary>
    private readonly record struct LevelPlacement(Vector3 Position, ushort Territory, uint Map);

    /// <summary>區域 → 該區的風脈泉。啟用時建一次，之後只讀。</summary>
    private readonly Dictionary<ushort, List<Point>> byTerritory = [];

    /// <summary>
    /// 區域 → 該區可長途傳送的主乙太之光（列號＋名稱）。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>刻意不做「自動挑最近的那一顆」。</b>台服 7.20 的資料裡<b>查不到主水晶的世界座標</b>：
    /// <c>Level</c> 表中 <c>Object</c> 指向乙太之光的只有 <b>5 列</b>（全 239 顆水晶），
    /// 而 <c>Aetheryte.Level[0]</c> 指到的列號在整張 <c>Level</c> 表裡<b>根本不存在</b>
    /// （108 顆主水晶全部解不到，2026-08-25 實測）。剩下的來源是 <c>MapMarker</c>，
    /// 但那是地圖座標，換算回世界座標需要一組<b>沒有辦法離線校準</b>的公式
    /// （我沒有任何一顆水晶的世界座標真值可以對）。
    /// <para>
    /// ⚠️ 而 31 個有風脈泉的區域裡有 <b>28 個</b>不只一顆主水晶（最多 4 顆），
    /// 所以「挑最近」不是可有可無的細節——猜錯的失敗形式是「傳到比較遠的那顆」，靜默。
    /// ⇒ 改成<b>把選擇權交給使用者</b>：選單直接列出該區每一顆已解鎖的水晶名字，
    /// 使用者看得到自己會落在哪裡。這比一個可能算錯的「最近」誠實。
    /// </para>
    /// <para>
    /// 📌 同樣的資料限制也套在 <see cref="CustomDeliveriesOverview"/> 上
    /// （它用 <c>Level.Type == 12</c> 找水晶座標，在台服只會命中 5 列，
    /// 於是「挑最近」實際上退化成「挑第一顆」）。那邊沒有改，因為退化的後果只是路程比較遠。
    /// </para>
    /// </remarks>
    private readonly Dictionary<ushort, List<(uint Id, string Name)>> aetherytesByTerritory = [];

    private readonly TaskQueue navQueue = new();

    private AetherCurrentTrackerConfig Config => Plugin.Instance.Config.AetherCurrentTracker;

    private bool windowOpen;

    /// <summary><c>MarkingController::PlacePreset</c> 的特徵碼有沒有解出來。</summary>
    private bool placePresetAvailable;

    private string placePresetFailure = string.Empty;

    /// <summary>建表時的統計，供啟用記錄與「資料是不是空的」判斷。</summary>
    private int totalPoints;
    private int totalPhysical;
    private int totalWithPosition;

    // ── Mappy 標記（增量同步）─────────────────────────────────────────────
    private readonly MappyMarkerPublisher mappy = new(MarkerSource);

    /// <summary>重複使用的緩衝區，免得每次同步都配一個新的 List。</summary>
    private readonly List<MappyMarkerPublisher.Marker> mappyPending = [];

    /// <summary>上一次成功推給 Mappy 的內容簽章；空字串＝還沒推過或已被作廢。</summary>
    private string lastMappySignature = string.Empty;

    /// <summary>Mappy 上一次探測時在不在（用來偵測「從不在變成在」）。</summary>
    private bool mappyWasAvailable;

    /// <summary>目前地圖上有沒有我們放的東西（設定被關掉時要收乾淨）。</summary>
    private bool mappyHasMarkers;

    /// <summary>Mappy 端的狀態，給設定畫面顯示用。<b>零值＝「不知道」</b>。</summary>
    private MappyState mappyState = MappyState.Unknown;

    /// <summary>最後一次同步的筆數（設定畫面用）。</summary>
    private int lastMappyPlaced;
    private int lastMappyRejected;
    private int lastMappySkippedNoCoords;

    /// <summary>Mappy 橋接的狀態。<b>「不知道」是零值</b>。</summary>
    private enum MappyState
    {
        /// <summary>還沒探測過。</summary>
        Unknown = 0,

        /// <summary>使用者把「也顯示到 Mappy」關掉了。</summary>
        Disabled,

        /// <summary>Mappy 沒裝或還沒載入。</summary>
        Missing,

        /// <summary>Mappy 回報的 IPC 版本比本模組寫的時候還舊。</summary>
        VersionTooOld,

        /// <summary>一切正常。</summary>
        Ok,
    }

    protected override void OnEnable()
    {
        BuildData();
        ProbePlacePreset();

        navQueue.OnTimeout = step => Svc.Chat.Print($"[TC Toolbox] 導航逾時，已取消：{step}");

        Svc.PluginInterface.UiBuilder.Draw += DrawAll;
        Svc.Framework.Update += OnUpdate;

        // 啟用後立刻同步一次，不要讓使用者等一個節流週期。
        Throttle.Reset(MappySyncThrottleKey);
        Throttle.Reset(MappyForceResyncThrottleKey);
        lastMappySignature = string.Empty;
        mappyWasAvailable = false;
        mappy.Forget();

        // 🔑 「回 0」比「報錯」常見。四個數字一起印才分得出「表讀不到」與「鏈斷在中間」，
        //    所以期望值也一起印——這樣使用者回報的 log 不必再回頭查資料就看得出對不對。
        //    📌 台服 7.20 離線實測（2026-08-25，兩路獨立驗證）：
        //       31 個區域／303 個風脈泉（任務型 151、地圖實體 152）／實體型座標覆蓋率 152/152。
        //    ⚠️ 舊資料片每張圖只有 4 個實體風脈泉（黃金之遺產才是 10 個）——
        //       那是遊戲現況不是資料壞掉（已與全球服的 Questionable 資料集逐一比對過）。
        Svc.Log.Information(
            $"[{InternalName}] 模組啟用：{byTerritory.Count} 個區域、風脈泉 {totalPoints} 個" +
            $"（地圖實體 {totalPhysical} 個，其中查得到座標 {totalWithPosition} 個）" +
            $"；乙太之光 {aetherytesByTerritory.Count} 個區域有主水晶。" +
            $"　台服 7.20 期望：31／303／152／152。" +
            $"　場地標記：{(placePresetAvailable ? "可用" : placePresetFailure)}");
    }

    protected override void OnDisable()
    {
        Svc.Framework.Update -= OnUpdate;
        Svc.PluginInterface.UiBuilder.Draw -= DrawAll;

        navQueue.Abort();

        // 🔴 停用（含外掛卸載，Plugin.Dispose 會把每個模組 Disable 一次）時一定要把標記收乾淨，
        //    否則 Mappy 會一直畫著一組沒有人再更新的舊標記。
        mappy.Clear();
        mappyHasMarkers = false;
        mappyWasAvailable = false;
        mappyState = MappyState.Unknown;
        lastMappySignature = string.Empty;
        lastMappyPlaced = 0;
        lastMappyRejected = 0;
        lastMappySkippedNoCoords = 0;

        byTerritory.Clear();
        windowOpen = false;
        totalPoints = 0;
        totalPhysical = 0;
        totalWithPosition = 0;
    }

    private string MappySyncThrottleKey => $"TCToolbox.{InternalName}.MappySync";

    private string MappyForceResyncThrottleKey => $"TCToolbox.{InternalName}.MappyForce";

    private void OnUpdate(IFramework framework)
    {
        navQueue.Tick();
        SyncMappy();
    }

    // ───────────────────────── Mappy 標記 ─────────────────────────

    /// <summary>
    /// 把「還沒共鳴、而且查得到座標」的風脈泉同步成 Mappy 地圖上的標記。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 🔑 <b>推的是全部區域，不是只有目前所在區域。</b>共鳴狀態是玩家層級的資料，離開該區也讀得到，
    /// 所以在任何一張地圖上都答得出「這張圖還差哪幾個」——那正是規劃路線時最想知道的事。
    /// 數量上也放得下：台服 7.20 全部地圖實體風脈泉共 152 個，Mappy 每個來源可放 512 筆。
    /// </para>
    /// <para>
    /// 📌 <b>只推未共鳴的。</b>已共鳴的畫出來對「還差哪幾個」沒有幫助，而且要區分兩種狀態就得再挑
    /// 一顆「已完成」的圖示——那顆<b>沒有任何資料來源可以佐證</b>（<c>MapSymbol</c> 查不到這段），
    /// 猜一顆出來是靜默的錯。想確認已共鳴的位置請用世界疊加層的「已共鳴的也畫出來」。
    /// </para>
    /// <para>
    /// 🔴 <b>任務型與查不到座標的一律不推。</b>任務型沒有地圖實體（標上去等於叫人白跑一趟）；
    /// 查不到座標的更不能拿 <c>(0,0,0)</c> 去換算——那會在地圖角落長出一個看起來很正常的標記。
    /// </para>
    /// </remarks>
    private void SyncMappy()
    {
        if (!Throttle.Pass(MappySyncThrottleKey, MappySyncIntervalMs)) return;

        if (!Config.ShowOnMappy)
        {
            // 使用者剛把它關掉：收乾淨。之後就什麼都不做。
            if (mappyHasMarkers)
            {
                mappy.Clear();
                mappyHasMarkers = false;
                lastMappySignature = string.Empty;
                lastMappyPlaced = 0;
            }

            mappyState = MappyState.Disabled;
            return;
        }

        // 未登入時共鳴狀態沒有意義（而且不會變），跳過。已放上去的標記留著不動。
        if (!Svc.ClientState.IsLoggedIn) return;

        if (!MappyMarkerIpc.TryGetVersion(out var version))
        {
            if (mappyWasAvailable || mappyState != MappyState.Missing)
            {
                // 🔴 手上的 handle 全部失效了。留著會讓下一次同步以為「已經放上去了」，
                //    表現成標記再也不出現而且毫無徵兆。
                mappy.Forget();
                lastMappySignature = string.Empty;
            }

            mappyWasAvailable = false;
            mappyHasMarkers = false;
            mappyState = MappyState.Missing;

            // 沒裝 Mappy 是常態，退避到一分鐘一次（理由見 MappyMissingBackoffMs）。
            Throttle.Block(MappySyncThrottleKey, MappyMissingBackoffMs);
            return;
        }

        if (version < MappyMarkerIpc.SupportedVersion)
        {
            mappyState = MappyState.VersionTooOld;
            return;
        }

        // Mappy 從「不在」變成「在」＝它的標記表是空的，一定要重放。
        if (!mappyWasAvailable)
        {
            mappy.Forget();
            lastMappySignature = string.Empty;
            mappyWasAvailable = true;
        }

        var iconId = Config.MappyIconId is 0 ? DefaultMappyIconId : Config.MappyIconId;

        mappyPending.Clear();
        var skippedNoCoords = 0;

        var signature = new StringBuilder();
        signature.Append(iconId).Append('|');

        foreach (var list in byTerritory.Values)
        {
            foreach (var point in list)
            {
                if (point.IsQuest || !point.HasPosition) continue;
                if (IsResonated(point.AetherCurrentId)) continue;

                if (!MapCoords.TryWorldToMap(point.Map, point.Position, out var coords))
                {
                    skippedNoCoords++;
                    continue;
                }

                mappyPending.Add(new MappyMarkerPublisher.Marker(
                    point.AetherCurrentId.ToString(CultureInfo.InvariantCulture),
                    point.Map,
                    coords,
                    iconId,
                    BuildMappyTooltip(point)));

                signature.Append(point.AetherCurrentId).Append(',');
            }
        }

        var newSignature = signature.ToString();

        // 保險：即使簽章沒變也定期整組重推一次（Mappy 可能在我們沒看見的時候被重載過）。
        var forceFull = Throttle.Pass(MappyForceResyncThrottleKey, MappyForceResyncIntervalMs);
        var contentChanged = newSignature != lastMappySignature;

        if (!forceFull && !contentChanged && mappyState == MappyState.Ok)
        {
            mappyPending.Clear();
            return;
        }

        mappy.Publish(mappyPending, forceFull);
        mappyPending.Clear();

        lastMappyPlaced = mappy.Placed;
        lastMappyRejected = mappy.LastRejected;
        lastMappySkippedNoCoords = skippedNoCoords;
        lastMappySignature = newSignature;
        mappyHasMarkers = mappy.Placed > 0;
        mappyState = MappyState.Ok;

        // 📌 只有「內容真的變了」才寫記錄：每分鐘一次的保險重推全部都寫的話會洗掉使用者的記錄，
        //    而那些行沒有任何新資訊。
        if (!contentChanged) return;

        // 🔴 Information 級：使用者跑 LogLevel 2，Debug／Verbose 收不到。
        Svc.Log.Information(
            $"[{InternalName}] 同步風脈泉到 Mappy：未共鳴 {lastMappyPlaced} 個"
            + $"（本次異動 {mappy.LastIpcCalls} 次、圖示 {iconId}）"
            + (skippedNoCoords > 0 ? $"、座標換算不出來 {skippedNoCoords} 個" : string.Empty)
            + (lastMappyRejected > 0 ? $"、被 Mappy 拒絕 {lastMappyRejected} 個" : string.Empty));
    }

    private static string BuildMappyTooltip(Point point)
    {
        var sb = new StringBuilder();
        sb.Append("風脈泉 #").Append(point.IndexInZone);

        if (point.ZoneName.Length > 0) sb.Append('\n').Append("地點：").Append(point.ZoneName);

        sb.Append('\n').Append("狀態：未共鳴");
        return sb.ToString();
    }

    /// <summary>
    /// 探一次 <c>MarkingController::PlacePreset</c> 的位址。
    /// </summary>
    /// <remarks>
    /// 🔴 在<b>啟用時</b>探而不是用到才探：那是 <c>[MemberFunction]</c>，特徵碼失配時是在
    /// 第一次呼叫的當下擲受管理例外，而按鈕在 Draw 路徑上——例外逸出到 Dalamud 的 Draw，
    /// 整個介面到重開遊戲前都不會回來。先探一次，之後只看旗標。
    /// <para>
    /// 📌 2026-08-25 離線對台服 7.20 驗過：該特徵碼在 <c>.text</c> <b>唯一命中</b>
    /// （<c>0x140DC9541</c>，跟隨 <c>E8</c> 後落在 <c>0x140A0E560</c>）。
    /// 這裡仍然把它做成執行期閘門，不相信離線結論會永遠成立。
    /// </para>
    /// </remarks>
    private void ProbePlacePreset()
    {
        placePresetAvailable = false;
        placePresetFailure = string.Empty;

        try
        {
            var address = MarkingController.Addresses.PlacePreset.Value;
            if (address == 0)
            {
                placePresetFailure = "找不到場地標記函式的特徵碼";
                return;
            }

            placePresetAvailable = true;
        }
        catch (Exception ex)
        {
            placePresetFailure = "場地標記函式特徵碼解析失敗";
            Svc.Log.Warning(ex, $"[{InternalName}] {placePresetFailure}，插場地標記的按鈕會停用。");
        }
    }

    /// <summary>掃一次 Lumina 表把所有風脈泉建成查找表。只在模組啟用時跑一次。</summary>
    private void BuildData()
    {
        byTerritory.Clear();
        totalPoints = 0;
        totalPhysical = 0;
        totalWithPosition = 0;

        // ① AetherCurrent 列號 → EObj 列號。EObj.Data 存的就是 AetherCurrent 的列號。
        //    同一個 Data 理論上只對應一個 EObj，重複時取先出現的。
        var eobjByData = new Dictionary<uint, uint>();
        foreach (var eobj in Svc.Data.GetExcelSheet<EObj>())
        {
            if (eobj.Data != 0) eobjByData.TryAdd(eobj.Data, eobj.RowId);
        }

        // ② EObj 列號 → 世界座標。只收 Object 指向 EObj 的那些 Level 列。
        //    ⚠️ Level.Type 的數值語意各版本都可能變，所以用 RowRef 的 Is<EObj>() 判型別，
        //       不用寫死的 Type 常數。
        var levelByEObj = new Dictionary<uint, LevelPlacement>();
        foreach (var level in Svc.Data.GetExcelSheet<Level>())
        {
            if (!level.Object.Is<EObj>()) continue;

            levelByEObj.TryAdd(level.Object.RowId, new LevelPlacement(
                new Vector3(level.X, level.Y, level.Z),
                (ushort)level.Territory.RowId,
                level.Map.RowId));
        }

        BuildAetherytes();

        // ③ 逐區域展開。
        foreach (var set in Svc.Data.GetExcelSheet<AetherCurrentCompFlgSet>())
        {
            var territory = (ushort)set.Territory.RowId;
            if (territory == 0) continue;

            var mapId = set.Territory.ValueNullable?.Map.RowId ?? 0;

            // 區域名稱建表時查一次就好（Mappy 標記的提示文字要用）。
            var zoneName = set.Territory.ValueNullable?.PlaceName.ValueNullable?.Name.ExtractText()
                           ?? string.Empty;

            var list = new List<Point>();

            foreach (var reference in set.AetherCurrents)
            {
                var currentId = reference.RowId;
                if (currentId == 0) continue;

                var row = reference.ValueNullable;
                if (row == null) continue;

                var questId = row.Value.Quest.RowId;
                totalPoints++;

                if (questId != 0)
                {
                    // 任務型：沒有地圖實體。刻意<b>不</b>沿用 DailyRoutines 那套「拿任務發布者的
                    // 座標當風脈泉座標」——那個座標是去接任務的地方，不是風脈泉，
                    // 標旗過去只會讓人白跑一趟。這裡老實標成「沒有座標」並附上任務名。
                    var questName = row.Value.Quest.ValueNullable?.Name.ExtractText() ?? string.Empty;
                    list.Add(new Point(currentId, questId, territory, mapId, Vector3.Zero, false)
                    {
                        IndexInZone = list.Count + 1,
                        QuestName = questName,
                        ZoneName = zoneName,
                    });
                    continue;
                }

                totalPhysical++;

                var hasPosition = eobjByData.TryGetValue(currentId, out var eobjId)
                                  && levelByEObj.TryGetValue(eobjId, out var placement);

                if (!hasPosition)
                {
                    list.Add(new Point(currentId, 0, territory, mapId, Vector3.Zero, false)
                    {
                        IndexInZone = list.Count + 1,
                        ZoneName = zoneName,
                    });
                    continue;
                }

                levelByEObj.TryGetValue(eobjId, out var found);
                totalWithPosition++;

                // 🔴 座標所屬的區域以 Level 列自己說的為準，不是 CompFlgSet 那一欄——
                //    兩者不一致時（子區域／副本佈景）相信實際擺放的那一份。
                //    📌 台服 7.20 實測 152/152 兩者相同，這一段是防未來改版，不是在修現況。
                var realTerritory = found.Territory != 0 ? found.Territory : territory;
                var realMap = found.Map != 0 ? found.Map : mapId;

                list.Add(new Point(currentId, 0, realTerritory, realMap, found.Position, true)
                {
                    IndexInZone = list.Count + 1,
                    ZoneName = zoneName,
                });
            }

            if (list.Count > 0) byTerritory[territory] = list;
        }
    }

    /// <summary>
    /// 只收 <c>IsAetheryte==true</c> 的主水晶（能用長途傳送動作抵達的那種），依區域分組並帶名稱。
    /// </summary>
    /// <remarks>
    /// 📌 名稱取 <c>Aetheryte.PlaceName</c>（台服自帶繁中）。台服對未開放內容會保留列但名稱是空字串，
    /// 那種一律跳過——選單上出現一個沒有名字的項目比少一個項目更糟。
    /// </remarks>
    private void BuildAetherytes()
    {
        aetherytesByTerritory.Clear();

        foreach (var ae in Svc.Data.GetExcelSheet<Aetheryte>())
        {
            if (!ae.IsAetheryte) continue;
            if (!ae.Territory.IsValid) continue;

            var name = ae.PlaceName.ValueNullable?.Name.ExtractText() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name)) continue;

            var territory = (ushort)ae.Territory.RowId;
            if (!aetherytesByTerritory.TryGetValue(territory, out var list))
                aetherytesByTerritory[territory] = list = [];

            list.Add((ae.RowId, name));
        }
    }

    /// <summary>
    /// 這個風脈泉共鳴了沒有。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>刻意不呼叫 <c>PlayerState.IsAetherCurrentUnlocked</c>，改自己走 <c>TryGet</c>。</b>
    /// 那支是 <c>UnlockedAetherCurrentsBitArray.Get(id - 0x2B0000)</c>，而 <c>Get</c>
    /// 對越界是<b>擲 <c>ArgumentOutOfRangeException</c></b>——這條路徑會被 Draw 走到，
    /// 例外逸出到 Dalamud 的 Draw 就是整個介面到重開遊戲前都不回來。
    /// <para>
    /// ⚠️ 而且 <c>BitArray.Get</c> 的上界檢查寫的是
    /// <c>ThrowIfGreaterThan(index, bitCount)</c> 而不是 <c>…OrEqual</c>——
    /// <b><c>index == bitCount</c> 會通過檢查並讀到陣列後面那一個 byte</b>
    /// （風脈泉這個位元陣列剛好 448 bits／56 bytes，後面緊接著的是
    /// <c>_unlockedAetherCurrentCompFlgSets</c>）。目前 <c>AetherCurrent</c> 恰好 448 列
    /// 所以踩不到，但改版加列就會靜默讀到隔壁的旗標。<c>TryGet</c> 用的是
    /// <c>(uint)index >= (uint)bitCount</c>，沒有這個問題。
    /// </para>
    /// </remarks>
    private static bool IsResonated(uint aetherCurrentId)
    {
        var bits = PlayerState.Instance()->UnlockedAetherCurrentsBitArray;
        var index = (long)aetherCurrentId - AetherCurrentIdBase;
        if (index < 0 || index >= bits.BitCount) return false;

        return bits.TryGet((int)index, out var value) && value;
    }

    /// <summary>目前所在區域的風脈泉（沒有就回空清單）。</summary>
    private List<Point> CurrentZonePoints()
    {
        var territory = Svc.ClientState.TerritoryType;
        return byTerritory.TryGetValue(territory, out var list) ? list : [];
    }

    private void DrawAll()
    {
        DrawWorldOverlay();
        DrawWindow();
    }

    // ───────────────────────── 世界疊加層 ─────────────────────────

    /// <summary>
    /// 世界疊加層。風格以 NecroLens 為基準：<b>有方向、有外框、不疊顏色</b>。
    /// </summary>
    /// <remarks>
    /// 「可能有」與「已確認」的兩態參考 PalacePal：<b>已共鳴＝細的灰圈</b>（存在但不必再去），
    /// <b>未共鳴＝粗的亮圈＋序號</b>。兩者都只畫外框不填色，疊在一起也還讀得出來。
    /// <para>
    /// 📌 在畫面外的目標畫成螢幕邊緣的箭頭（那就是「有方向」）。
    /// </para>
    /// </remarks>
    private void DrawWorldOverlay()
    {
        if (!Config.ShowWorldOverlay) return;
        if (Svc.Objects.LocalPlayer == null) return;
        if (Svc.Condition[ConditionFlag.BetweenAreas] || Svc.Condition[ConditionFlag.BetweenAreas51]) return;

        var points = CurrentZonePoints();
        if (points.Count == 0) return;

        var drawList = ImGui.GetBackgroundDrawList();
        var viewport = ImGui.GetMainViewport();
        var center = viewport.Pos + (viewport.Size * 0.5f);

        foreach (var point in points)
        {
            if (!point.HasPosition) continue;

            var resonated = IsResonated(point.AetherCurrentId);
            if (resonated && !Config.ShowResonatedInOverlay) continue;

            // 📌 回傳值＝「在鏡頭前方」（W > 0），inView＝「而且落在視窗矩形內」。
            var inFront = Svc.GameGui.WorldToScreen(point.Position, out var screen, out var inView);

            if (inView)
            {
                DrawMarker(drawList, screen, point, resonated);
                continue;
            }

            if (!Config.ShowOffScreenArrows || resonated) continue;

            DrawEdgeArrow(drawList, viewport.Size, center, screen, inFront);
        }
    }

    private static void DrawMarker(ImDrawListPtr drawList, Vector2 screen, Point point, bool resonated)
    {
        // 不疊顏色：外框一律「深色描邊 ＋ 亮色本體」，讓它在任何背景上都讀得出來。
        var radius = resonated ? 8f : 13f;
        var thickness = resonated ? 1.5f : 2.5f;
        var body = resonated
            ? new Vector4(0.55f, 0.60f, 0.62f, 0.75f)  // 已共鳴：灰
            : new Vector4(0.35f, 0.85f, 1.00f, 0.95f); // 未共鳴：亮青

        var outline = ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.85f));
        drawList.AddCircle(screen, radius + 1f, outline, 0, thickness + 2f);
        drawList.AddCircle(screen, radius, ImGui.GetColorU32(body), 0, thickness);

        if (resonated) return;

        var label = point.IndexInZone.ToString();
        var size = ImGui.CalcTextSize(label);
        var textPos = screen - (size * 0.5f);
        drawList.AddText(textPos + new Vector2(1f, 1f), outline, label);
        drawList.AddText(textPos, ImGui.GetColorU32(body), label);
    }

    /// <summary>畫面外的目標：在螢幕邊緣畫一個指向它的三角形。</summary>
    /// <param name="screen">
    /// <c>WorldToScreen</c> 算出來的投影點（<b>即使在畫面外／鏡頭背後也有值</b>）。
    /// </param>
    /// <param name="inFront">目標在不在鏡頭前方（＝<c>WorldToScreen</c> 的回傳值）。</param>
    /// <remarks>
    /// 🔑 <b>方向完全由 <c>WorldToScreen</c> 的輸出推出來，不自己算鏡頭朝向。</b>
    /// 那支用的就是遊戲當下真正在用的 <c>ViewProjectionMatrix</c>，所以推出來的方向
    /// 與畫面上看到的一定一致；自己去解 view matrix 的 yaw 則是「錯了也不會有人發現」的那種錯。
    /// <para>
    /// 🔴 <b>鏡頭背後的投影是鏡像的，方向要反過來。</b>Dalamud 的實作在除以 W 時用的是
    /// <c>MathF.Abs(1.0f / pCoords.W)</c>（<c>GameGui.cs:165</c>）——取絕對值代表
    /// <c>W &lt; 0</c>（目標在身後）時，投影點會落在畫面中心的<b>相反</b>側。
    /// 少了這個反轉，站在風脈泉前面轉過身時箭頭會指向完全相反的方向，
    /// 而那看起來只是「箭頭怪怪的」，不像壞掉。
    /// </para>
    /// </remarks>
    private static void DrawEdgeArrow(
        ImDrawListPtr drawList, Vector2 viewportSize, Vector2 center, Vector2 screen, bool inFront)
    {
        var offset = screen - center;
        if (!inFront) offset = -offset;

        if (offset.LengthSquared() < 0.0001f) return;

        var direction = Vector2.Normalize(offset);
        if (!float.IsFinite(direction.X) || !float.IsFinite(direction.Y)) return;

        // 邊緣內縮，免得箭頭被切掉一半。
        const float margin = 46f;
        var half = (viewportSize * 0.5f) - new Vector2(margin, margin);
        if (half.X <= 0 || half.Y <= 0) return;

        var scale = MathF.Min(
            half.X / MathF.Max(MathF.Abs(direction.X), 0.0001f),
            half.Y / MathF.Max(MathF.Abs(direction.Y), 0.0001f));

        var tip = center + (direction * scale);
        var perpendicular = new Vector2(-direction.Y, direction.X);

        var a = tip;
        var b = tip - (direction * 16f) + (perpendicular * 8f);
        var c = tip - (direction * 16f) - (perpendicular * 8f);

        var outline = ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.85f));
        var body = ImGui.GetColorU32(new Vector4(0.35f, 0.85f, 1.00f, 0.95f));

        // 有外框、不疊顏色：黑底三角形 ＋ 亮色描邊。
        drawList.AddTriangleFilled(a, b, c, outline);
        drawList.AddTriangle(a, b, c, body, 2f);
    }

    // ───────────────────────── 清單視窗 ─────────────────────────

    private void DrawWindow()
    {
        if (!windowOpen) return;

        ImGui.SetNextWindowSize(new Vector2(520f, 380f), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("風脈泉一覽###TCToolboxAetherCurrents", ref windowOpen))
        {
            ImGui.End();
            return;
        }

        var points = CurrentZonePoints();
        var zoneName = Svc.Data.GetExcelSheet<TerritoryType>()
                          .GetRowOrDefault(Svc.ClientState.TerritoryType)?
                          .PlaceName.ValueNullable?.Name.ExtractText() ?? string.Empty;

        if (points.Count == 0)
        {
            ImGui.TextDisabled(zoneName.Length > 0
                ? $"「{zoneName}」沒有風脈泉。"
                : "這個區域沒有風脈泉。");
            ImGui.End();
            return;
        }

        var physical = 0;
        var physicalDone = 0;
        var questTotal = 0;
        var questDone = 0;
        foreach (var p in points)
        {
            var done = IsResonated(p.AetherCurrentId);
            if (p.IsQuest)
            {
                questTotal++;
                if (done) questDone++;
            }
            else
            {
                physical++;
                if (done) physicalDone++;
            }
        }

        ImGui.TextUnformatted(
            $"{zoneName}　地圖實體 {physicalDone}/{physical}　任務 {questDone}/{questTotal}");

        // 🔴 少數區域（摩杜納、魔大陸阿濟茲拉）一個地圖實體都沒有，全部靠任務給。
        //    不明講的話「0/0」看起來像模組壞了。
        if (physical == 0)
        {
            ImGui.TextColored(new Vector4(1f, 0.8f, 0.35f, 1f),
                              "這個區域的風脈泉全部由任務給予，地圖上沒有可以走過去的實體。");
        }

        DrawToolbar(points);
        ImGui.Separator();

        DrawTable(points);

        ImGui.End();
    }

    private void DrawToolbar(List<Point> points)
    {
        var markable = new List<Point>();
        foreach (var p in points)
        {
            if (p.HasPosition && !IsResonated(p.AetherCurrentId)) markable.Add(p);
        }

        using (ImRaii.Disabled(!placePresetAvailable || markable.Count == 0))
        {
            if (ImGui.Button($"插場地標記（{Math.Min(markable.Count, FieldMarkerSlots)} 個）"))
                PlaceFieldMarkers(markable);
        }

        if (ImGui.IsItemHovered())
        {
            var tip = placePresetAvailable
                ? $"把還沒共鳴、而且查得到座標的風脈泉插上場地標記，最多 {FieldMarkerSlots} 個" +
                  $"（目前有 {markable.Count} 個）。\n" +
                  "⚠️ 場地標記能不能在這個區域使用是遊戲決定的（戰鬥中、或該區不允許時會失敗）。\n" +
                  "插完會回讀遊戲的標記狀態確認，沒插上會直接告訴你。"
                : $"停用中：{placePresetFailure}。";
            ImGui.SetTooltip(tip);
        }

        ImGui.SameLine();
        using (ImRaii.Disabled(!placePresetAvailable))
        {
            if (ImGui.Button("清除場地標記")) ClearFieldMarkers();
        }

        ImGui.SameLine();
        if (ImGui.Button("停止導航"))
        {
            navQueue.Abort();
            ExternalNav.TryStopMovement();
        }

        if (navQueue.IsBusy)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(navQueue.CurrentStep ?? string.Empty);
        }
    }

    private void DrawTable(List<Point> points)
    {
        using var table = ImRaii.Table("aethercurrents", 4,
                                       ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH |
                                       ImGuiTableFlags.ScrollY);
        if (!table) return;

        ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed, 28f);
        ImGui.TableSetupColumn("狀態", ImGuiTableColumnFlags.WidthFixed, 72f);
        ImGui.TableSetupColumn("來源／座標", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("距離", ImGuiTableColumnFlags.WidthFixed, 68f);
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();

        var player = Svc.Objects.LocalPlayer;

        foreach (var point in points)
        {
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(point.IndexInZone.ToString());

            ImGui.TableNextColumn();
            var resonated = IsResonated(point.AetherCurrentId);
            if (resonated)
                ImGui.TextColored(new Vector4(0.45f, 0.85f, 0.5f, 1f), "已共鳴");
            else
                ImGui.TextColored(new Vector4(1f, 0.65f, 0.4f, 1f), "未共鳴");

            ImGui.TableNextColumn();
            DrawSourceCell(point);

            ImGui.TableNextColumn();
            if (point.HasPosition && player != null && Svc.ClientState.TerritoryType == point.Territory)
            {
                var d = Vector3.Distance(player.Position, point.Position);
                ImGui.TextUnformatted($"{d:0} y");
            }
            else
            {
                // 🔑 「不知道」要在列上看得見，而且不能畫成 0。
                ImGui.TextDisabled("？");
            }
        }
    }

    private void DrawSourceCell(Point point)
    {
        if (point.IsQuest)
        {
            var name = point.QuestName.Length > 0 ? point.QuestName : $"任務 #{point.QuestId}";
            ImGui.TextDisabled($"任務：{name}");
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    "這個風脈泉是完成任務時直接給的，地圖上沒有實體。\n" +
                    "所以沒有座標可以標旗，也沒有地方可以走過去。");
            }

            return;
        }

        if (!point.HasPosition)
        {
            ImGui.TextDisabled("座標未知");
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    "這是地圖上的實體風脈泉，但從遊戲資料表裡查不到它的擺放位置\n" +
                    "（AetherCurrent → EObj → Level 這條鏈在這一筆斷了）。\n" +
                    "刻意不顯示 (0, 0, 0)——那會把你導到地圖原點。");
            }

            return;
        }

        ImGui.TextUnformatted($"({point.Position.X:0}, {point.Position.Z:0})");
        DrawRowContextMenu(point);
        if (ImGui.IsItemHovered() && !ImGui.IsPopupOpen($"##acctx{point.AetherCurrentId}"))
            ImGui.SetTooltip("右鍵：標旗／導航過去");
    }

    private void DrawRowContextMenu(Point point)
    {
        if (!ImGui.BeginPopupContextItem($"##acctx{point.AetherCurrentId}")) return;

        var sameZone = Svc.ClientState.TerritoryType == point.Territory;
        var vnavReady = ExternalNav.IsVnavmeshReady();

        if (sameZone)
        {
            // 已經在同一張圖：不必傳送，直接走過去（沒有 vnavmesh 就退化成標旗）。
            var label = vnavReady ? "走過去" : "走過去（未偵測到 vnavmesh，改為地圖標旗）";
            if (ImGui.MenuItem(label)) StartNavigation(point, null, 0);
        }
        else
        {
            DrawTeleportItems(point, vnavReady);
        }

        if (ImGui.MenuItem("僅地圖標旗（不傳送、不自動走路）")) SetMapFlagAndOpenMap(point);

        ImGui.EndPopup();
    }

    /// <summary>
    /// 跨區時：<b>逐一列出該區已解鎖的主乙太之光，讓使用者自己挑要落在哪一顆。</b>
    /// </summary>
    /// <remarks>
    /// 🔑 刻意不做「自動挑最近」——理由見 <see cref="aetherytesByTerritory"/>：
    /// 台服資料查不到主水晶的世界座標，任何「最近」都是猜的，而猜錯是靜默的。
    /// 把名字列出來，使用者一眼就知道自己會落在哪裡。
    /// </remarks>
    private void DrawTeleportItems(Point point, bool vnavReady)
    {
        if (!ExternalNav.IsLifestreamAvailable())
        {
            using (ImRaii.Disabled())
            {
                ImGui.MenuItem("傳送過去（未偵測到 Lifestream）");
            }

            return;
        }

        if (!aetherytesByTerritory.TryGetValue(point.Territory, out var candidates) || candidates.Count == 0)
        {
            using (ImRaii.Disabled())
            {
                ImGui.MenuItem("傳送過去（該區沒有可長途傳送的乙太之光）");
            }

            return;
        }

        var any = false;
        foreach (var (id, name) in candidates)
        {
            if (!IsAetheryteUnlocked(id, out var subIndex)) continue;

            any = true;
            var label = vnavReady
                ? $"傳送到「{name}」再走過去"
                : $"傳送到「{name}」並標旗（未偵測到 vnavmesh）";

            if (ImGui.MenuItem(label)) StartNavigation(point, id, subIndex);
        }

        if (any) return;

        using (ImRaii.Disabled())
        {
            ImGui.MenuItem("傳送過去（該區的乙太之光都還沒解鎖）");
        }
    }

    // ───────────────────────── 動作 ─────────────────────────

    /// <summary>
    /// 傳送最近的乙太之光再用 vnavmesh 走過去。形狀與
    /// <see cref="CustomDeliveriesOverview"/> 相同（那條路已經出貨、使用者用過）。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>絕不做記憶體瞬移</b>，也<b>絕不用聊天指令呼叫 <c>/li</c></b>
    /// （空參數的 <c>/li</c> 是跨世界傳送）。傳送一律走 <see cref="ExternalNav.TryTeleport"/> 的 IPC。
    /// </remarks>
    /// <param name="aetheryteId">要傳去哪一顆水晶；<c>null</c>＝已經在同一張圖，不必傳送。</param>
    private void StartNavigation(Point point, uint? aetheryteId, byte subIndex)
    {
        navQueue.Abort();

        if (aetheryteId is not { } targetAetheryte)
        {
            navQueue.Enqueue("下達導航指令", () => IssueWalkOrFallback(point));
            return;
        }

        var targetTerritory = point.Territory;
        var teleportIssued = false;

        navQueue.Enqueue("等待傳送完成", () =>
        {
            if (!teleportIssued)
            {
                teleportIssued = true;
                if (!ExternalNav.TryTeleport(targetAetheryte, subIndex, out var accepted) || !accepted)
                {
                    Svc.Chat.Print("[TC Toolbox] Lifestream 傳送請求失敗（可能忙碌中或傳送動作被鎖定），已取消導航。");
                    return (bool?)null;
                }
            }

            return Svc.Condition[ConditionFlag.BetweenAreas]
                   || Svc.Condition[ConditionFlag.BetweenAreas51]
                   || Svc.ClientState.TerritoryType == targetTerritory;
        }, timeoutMs: 15_000);

        navQueue.Enqueue("等待進入區域", () =>
            Svc.ClientState.TerritoryType == targetTerritory
            && !Svc.Condition[ConditionFlag.BetweenAreas]
            && !Svc.Condition[ConditionFlag.BetweenAreas51],
            timeoutMs: 20_000);

        navQueue.Enqueue("下達導航指令", () => IssueWalkOrFallback(point));
    }

    /// <summary>下達 vnavmesh 導航指令；失敗就退化成標旗＋開地圖。</summary>
    private static bool IssueWalkOrFallback(Point point)
    {
        if (ExternalNav.TryMoveTo(point.Position, false, out var started) && started) return true;

        Svc.Chat.Print("[TC Toolbox] 無法透過 vnavmesh 自動走過去，已改為地圖標旗，請自行前往。");
        SetMapFlagAndOpenMap(point);
        return true;
    }

    /// <summary>原生 SetFlagMapMarker／OpenMap；不依賴任何外掛，永遠可用。</summary>
    private static void SetMapFlagAndOpenMap(Point point)
    {
        if (!point.HasPosition) return;

        var agent = AgentMap.Instance();
        if (agent == null) return;

        agent->SetFlagMapMarker(point.Territory, point.Map, point.Position);
        agent->OpenMap(point.Map, point.Territory, $"風脈泉 #{point.IndexInZone}");
    }

    private static bool IsAetheryteUnlocked(uint aetheryteId, out byte subIndex)
    {
        foreach (var entry in Svc.AetheryteList)
        {
            if (entry.AetheryteId == aetheryteId)
            {
                subIndex = entry.SubIndex;
                return true;
            }
        }

        subIndex = 0;
        return false;
    }

    /// <summary>
    /// 把未共鳴的風脈泉插上場地標記（最多 8 個），插完<b>回讀確認</b>。
    /// </summary>
    /// <remarks>
    /// 🔴 <c>PlacePreset</c> 沒有回傳值，所以「遊戲收不收」只能自己回讀
    /// <c>MarkingController.FieldMarkers</c> 來判斷。
    /// 場地標記在戰鬥中、或該區域不允許時會被遊戲拒絕，而那是<b>靜默</b>的——
    /// 沒有這道回讀，使用者會看到「已插上 8 個」然後畫面上一個都沒有。
    /// <para>
    /// 📌 座標是整數，單位是<b>世界座標 × 1000</b>（<c>MarkerPresetPlacement</c> 的
    /// <c>_x</c>／<c>_y</c>／<c>_z</c> 都是 <c>int</c>，而 <c>FieldMarker</c> 同時存
    /// <c>Vector3 Position</c> 與同名的整數三元組）。
    /// </para>
    /// </remarks>
    private void PlaceFieldMarkers(List<Point> markable)
    {
        if (!placePresetAvailable)
        {
            Svc.Chat.PrintError($"[TC Toolbox] 無法插場地標記：{placePresetFailure}。");
            return;
        }

        var controller = MarkingController.Instance();
        if (controller == null) return;

        var placement = new MarkerPresetPlacement();
        var wanted = 0;

        for (var i = 0; i < FieldMarkerSlots && i < markable.Count; i++)
        {
            var p = markable[i];
            placement.Active[i] = true;
            placement.X[i] = (int)MathF.Round(p.Position.X * 1000f);
            placement.Y[i] = (int)MathF.Round(p.Position.Y * 1000f);
            placement.Z[i] = (int)MathF.Round(p.Position.Z * 1000f);
            wanted++;
        }

        if (wanted == 0) return;

        controller->PlacePreset(&placement);

        // 回讀確認。遊戲是同步套用的（不是封包往返），所以下一行就讀得到結果。
        var placed = 0;
        var markers = controller->FieldMarkers;
        for (var i = 0; i < wanted && i < markers.Length; i++)
        {
            if (markers[i].Active) placed++;
        }

        if (placed >= wanted)
        {
            Svc.Chat.Print($"[TC Toolbox] 已插上 {placed} 個場地標記。");
        }
        else
        {
            Svc.Chat.PrintError(
                $"[TC Toolbox] 場地標記只插上 {placed}/{wanted} 個——" +
                "遊戲拒絕了（戰鬥中，或這個區域不允許場地標記）。");
        }

        Svc.Log.Information($"[{InternalName}] 場地標記：要求 {wanted} 個、實際 {placed} 個。");
    }

    private void ClearFieldMarkers()
    {
        var controller = MarkingController.Instance();
        if (controller == null) return;

        // 回傳碼語意見 CS 註解：0 成功／2 操作過於頻繁／4 戰鬥中／5 該區域不允許。
        var result = controller->ClearFieldMarkers();
        if (result == 0) return;

        var why = result switch
        {
            2 => "操作太頻繁（遊戲有 500 毫秒的鎖），請稍候再試",
            3 => "還有標記正在處理中",
            4 => "戰鬥中無法清除",
            5 => "這個區域不允許場地標記",
            _ => $"遊戲回傳 {result}",
        };

        Svc.Chat.PrintError($"[TC Toolbox] 清除場地標記失敗：{why}。");
    }

    // ───────────────────────── 設定 ─────────────────────────

    public override void DrawConfig()
    {
        if (ImGui.Button(windowOpen ? "關閉一覽視窗" : "開啟一覽視窗")) windowOpen = !windowOpen;

        ImGui.SameLine();
        ImGui.TextDisabled($"目前資料：{byTerritory.Count} 個區域、{totalPoints} 個風脈泉");
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                $"其中地圖上的實體 {totalPhysical} 個，查得到座標的 {totalWithPosition} 個。\n" +
                "查不到座標的在清單上會顯示「座標未知」而不是 (0, 0, 0)。\n" +
                "任務給的風脈泉本來就沒有座標，不算在裡面。");
        }

        ImGui.Spacing();

        var overlay = Config.ShowWorldOverlay;
        if (ImGui.Checkbox("在畫面上標出風脈泉的位置", ref overlay))
        {
            Config.ShowWorldOverlay = overlay;
            Plugin.Instance.Config.Save();
        }

        using (ImRaii.Disabled(!Config.ShowWorldOverlay))
        using (ImRaii.PushIndent())
        {
            var arrows = Config.ShowOffScreenArrows;
            if (ImGui.Checkbox("畫面外的用箭頭指出方向", ref arrows))
            {
                Config.ShowOffScreenArrows = arrows;
                Plugin.Instance.Config.Save();
            }

            var showDone = Config.ShowResonatedInOverlay;
            if (ImGui.Checkbox("已共鳴的也畫出來（細灰圈）", ref showDone))
            {
                Config.ShowResonatedInOverlay = showDone;
                Plugin.Instance.Config.Save();
            }
        }

        ImGui.Spacing();

        DrawMappyConfig();

        ImGui.Spacing();
        ImGui.TextDisabled("移動只走「傳送最近水晶 ＋ vnavmesh 走過去」。");
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "本模組不做任何形式的瞬移，也不會用聊天指令呼叫 /li。\n" +
                "沒裝 Lifestream 就不能跨區傳送、沒裝 vnavmesh 就退化成地圖標旗，\n" +
                "選單上的文字會直接告訴你目前是哪一種狀況。");
        }
    }

    /// <summary>設定畫面上的 Mappy 區塊。</summary>
    /// <remarks>
    /// 📌 模組沒啟用時 <see cref="SyncMappy"/> 沒在跑，<see cref="mappyState"/> 是死的——
    /// 這裡自己（節流地）探一次，讓使用者在還沒啟用之前就看得出來「Mappy 在不在」。
    /// </remarks>
    private void DrawMappyConfig()
    {
        var onMappy = Config.ShowOnMappy;
        if (ImGui.Checkbox("也顯示到 Mappy 地圖", ref onMappy))
        {
            Config.ShowOnMappy = onMappy;
            Plugin.Instance.Config.Save();
            lastMappySignature = string.Empty;
            Throttle.Reset(MappySyncThrottleKey);
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "把「還沒共鳴」的風脈泉畫到 Mappy 的地圖上，不必人在附近也看得到。\n" +
                "推的是全部區域，所以在任何一張地圖上都看得出那張圖還差哪幾個。\n" +
                "已共鳴的不畫（那對「還差哪幾個」沒有幫助）；任務給的沒有地圖實體，也不畫。\n" +
                "沒裝 Mappy 就靜靜地什麼都不做，不會有錯誤訊息。");
        }

        if (!Config.ShowOnMappy) return;

        using var indent = ImRaii.PushIndent();

        if (!IsEnabled && Throttle.Pass($"TCToolbox.{InternalName}.MappyUiProbe", 2_000))
            mappyState = MappyMarkerIpc.TryGetVersion(out _) ? MappyState.Unknown : MappyState.Missing;

        switch (mappyState)
        {
            case MappyState.Missing:
                ImGui.TextDisabled("Mappy 未載入——標記沒有地方可以放。");
                break;

            case MappyState.VersionTooOld:
                ImGui.TextDisabled("Mappy 回報的 IPC 版本過舊，同步已停下來。");
                break;

            case MappyState.Ok:
                ImGui.TextDisabled($"目前放上 {lastMappyPlaced} 個未共鳴的風脈泉。");
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip(
                        $"座標換算不出來：{lastMappySkippedNoCoords} 個\n" +
                        $"被 Mappy 拒絕：{lastMappyRejected} 個\n" +
                        $"標記來源：{MarkerSource}（可在 Mappy 的設定裡單獨關掉）");
                }

                break;

            default:
                ImGui.TextDisabled(IsEnabled ? "尚未完成第一次同步。" : "Mappy 在，啟用模組後開始同步。");
                break;
        }

        DrawMappyIconSetting();
    }

    /// <summary>
    /// 圖示編號設定。
    /// </summary>
    /// <remarks>
    /// ⚠️ 做成可設定的理由與 <see cref="HuntTrainOnMappy"/> 相同：圖示的<b>存在</b>可以離線驗證，
    /// <b>長什麼樣子</b>不行。所以把它畫出來讓使用者自己看，不對就自己換。
    /// </remarks>
    private void DrawMappyIconSetting()
    {
        var current = Config.MappyIconId is 0 ? DefaultMappyIconId : Config.MappyIconId;

        var wrap = GameIcons.TryGet(current);
        if (wrap != null)
        {
            ImGui.Image(wrap.Handle, new Vector2(24f, 24f));
            ImGui.SameLine();
        }

        ImGui.SetNextItemWidth(120f);

        // 輸入框顯示的是「實際會用的編號」（哨兵 0 時顯示內建預設值），不要讓使用者看到一個 0。
        var value = (int)current;
        if (ImGui.InputInt("地圖圖示編號", ref value))
        {
            // 🔴 清空／輸入 0 一律寫回哨兵 0，不要寫具體常數：寫具體常數＝把編號烙死，
            //    日後修正 DefaultMappyIconId 對這個人靜默無效。
            Config.MappyIconId = value <= 0 ? 0u : (uint)value;
            Plugin.Instance.Config.Save();
            lastMappySignature = string.Empty;
        }

        ImGui.SameLine();
        if (ImGui.SmallButton($"預設（目前 {DefaultMappyIconId}）"))
        {
            Config.MappyIconId = 0;
            Plugin.Instance.Config.Save();
            lastMappySignature = string.Empty;
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                $"寫回「跟隨內建預設值」（目前是 {DefaultMappyIconId}"
                + "＝Mappy 畫「已經在視野內的風脈泉」時用的同一顆圖示）。\n"
                + "跟隨的意思是：日後內建預設值若有修正，你會自動吃到。");
        }
    }
}

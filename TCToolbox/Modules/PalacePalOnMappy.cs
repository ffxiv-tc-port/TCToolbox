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
/// Palace Pal 的陷阱／埋藏寶藏顯示到 Mappy 地圖上。
/// </summary>
/// <remarks>
/// 🔴 <b>純顯示，零自動化。</b>只呼叫 Palace Pal 的唯讀端點；不移動、不開箱、不碰目標，
/// 也不寫回它的任何資料。唯一的副作用是 Mappy 地圖上多出一組來源為 <see cref="MarkerSource"/>
/// 的標記。
/// 🔑 <b>「已確認」與「可能有」畫成兩顆不同的圖示</b>；對方版本較舊、沒有兩態端點時，整組落回
/// 舊的合併端點畫成單一態，並在模組列上說出「分不出來」——不知道本身要看得見。
/// </remarks>
public sealed class PalacePalOnMappy : TcModule
{
    public override string InternalName => "PalacePalOnMappy";

    public override string DisplayName => "深層迷宮陷阱與寶藏顯示到 Mappy";

    public override string Description =>
        "把 Palace Pal 已經標示出來的陷阱與埋藏寶藏位置，同步成 Mappy 地圖上的標記，"
        + "「已確認」與「可能有」用兩顆不同的圖示分開畫，可以先看整層的分布再決定路線。"
        + "另可加畫「現在真的看得到」的銀／金寶箱（預設關閉）。"
        + "純顯示：不移動、不開箱、不改 Palace Pal 的資料。"
        + "需要同時安裝 Palace Pal 與 Mappy。預設關閉——世界上已經有東西在畫了，"
        + "地圖要不要再疊一份由你決定。";

    public override ModuleCategory Category => ModuleCategory.Combat;

    public override bool HasConfigUI => true;

    /// <summary>
    /// 放到 Mappy 的標記來源名稱。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>不要改。</b>Mappy 端拿這個字串當鍵記住「這個來源要不要顯示」，
    /// 改了之後使用者原本關掉的來源會變成一個永遠留在 Mappy 設定裡的孤兒。
    /// </remarks>
    public const string MarkerSource = "TCToolbox.PalacePal";

    /// <summary>無條件全量重推的間隔（毫秒）——Mappy 可能在我們沒看見的時候被重載過。</summary>
    private const int ForceResyncIntervalMs = 60_000;

    /// <summary>設定畫面在模組關著時重新探測「對方在不在」的間隔（毫秒）。</summary>
    private const int UiProbeIntervalMs = 2_000;

    /// <summary>
    /// 陷阱的預設圖示。
    /// </summary>
    /// <remarks>
    /// 📌 <b>語意有遊戲資料背書</b>：27902 是 <c>DeepDungeonItem</c> 第 2 列
    /// 「魔陶器：全景」的圖示，而那件道具的說明逐字是「點亮本層所有地圖，可看到本層所有陷阱」
    /// ——遊戲自己用來代表「看見陷阱」的那顆圖。
    /// ⚠️ 圖示的「存在」與「長什麼樣子」是兩件事，所以仍然做成可設定的。
    /// </remarks>
    public const uint DefaultTrapIconId = 27902;

    /// <summary>
    /// 埋藏寶藏的預設圖示。
    /// </summary>
    /// <remarks>
    /// 📌 同一份資料表：27916 是第 14 列「魔陶器：感知寶藏」的圖示，說明逐字是
    /// 「顯示埋藏的寶藏的所在位置」。✅ 同批離線確認 <c>ui/icon/027000/027916.tex</c> 存在。
    /// </remarks>
    public const uint DefaultHoardIconId = 27916;

    /// <summary>
    /// 「可能有」陷阱的預設圖示。
    /// </summary>
    /// <remarks>
    /// 📌 60071 是 Mappy 自己的 <c>DrawHelpers.QuestionMarkIcon</c>——問號，語意就是「不確定」。
    /// 🔑 刻意讓兩態的差別大到一眼可辨，而不是在同一組魔陶器裡換一顆長得很像的。
    /// ⚠️ 兩種「可能有」預設是同一顆問號（種類寫在提示文字裡），想分開就各自改圖示。
    /// </remarks>
    public const uint DefaultTrapUnconfirmedIconId = 60071;

    /// <summary>「可能有」埋藏寶藏的預設圖示（理由見 <see cref="DefaultTrapUnconfirmedIconId"/>）。</summary>
    public const uint DefaultHoardUnconfirmedIconId = 60071;

    /// <summary>銀寶箱的預設圖示。</summary>
    /// <remarks>📌 60003 是 Mappy 自己對 <c>ObjectKind.Treasure</c> 用的那顆圖示。</remarks>
    public const uint DefaultSilverCofferIconId = 60003;

    /// <summary>金寶箱的預設圖示。</summary>
    /// <remarks>
    /// 📌 60354 取自 <c>TreasureHuntRank</c> 表的 <c>Icon</c> 欄（高階藏寶圖）——另一顆確定存在、
    /// 又確定與 60003 不同的寶箱圖，讓金銀在地圖上分得開。
    /// </remarks>
    public const uint DefaultGoldCofferIconId = 60354;

    /// <summary>可見快照超過這麼久就當「不知道」（毫秒）。</summary>
    /// <remarks>
    /// 📌 對方建議 1000。那份快照是每幀重拍的，過期代表它已經不再描述「現在」。
    /// </remarks>
    private const int VisibleSnapshotMaxAgeMs = 1_000;

    private uint EffectiveTrapIcon => Config.TrapIconId is 0 ? DefaultTrapIconId : Config.TrapIconId;

    private uint EffectiveHoardIcon => Config.HoardIconId is 0 ? DefaultHoardIconId : Config.HoardIconId;

    private uint EffectiveTrapUnconfirmedIcon =>
        Config.TrapUnconfirmedIconId is 0 ? DefaultTrapUnconfirmedIconId : Config.TrapUnconfirmedIconId;

    private uint EffectiveHoardUnconfirmedIcon =>
        Config.HoardUnconfirmedIconId is 0 ? DefaultHoardUnconfirmedIconId : Config.HoardUnconfirmedIconId;

    private uint EffectiveSilverCofferIcon =>
        Config.SilverCofferIconId is 0 ? DefaultSilverCofferIconId : Config.SilverCofferIconId;

    private uint EffectiveGoldCofferIcon =>
        Config.GoldCofferIconId is 0 ? DefaultGoldCofferIconId : Config.GoldCofferIconId;

    /// <summary>橋接目前的狀態。<b>「不知道」是零值</b>。</summary>
    private enum BridgeState
    {
        /// <summary>還沒同步過任何一次。</summary>
        Unknown = 0,

        /// <summary>Palace Pal 沒裝或還沒註冊 IPC。</summary>
        PalacePalMissing,

        /// <summary>Mappy 沒裝或還沒註冊 IPC。</summary>
        MappyMissing,

        /// <summary>對方回報的 IPC 版本比本模組寫的時候還舊。</summary>
        VersionTooOld,

        /// <summary>不在深層迷宮裡（Palace Pal 對這種區域一律回空清單）。</summary>
        NotInDeepDungeon,

        /// <summary>一切正常。</summary>
        Ok,
    }

    private PalacePalOnMappyConfig Config => Plugin.Instance.Config.PalacePalOnMappy;

    private readonly MappyMarkerPublisher publisher = new(MarkerSource);

    private readonly List<MappyMarkerPublisher.Marker> pending = [];

    private string lastSignature = string.Empty;

    private bool mappyWasAvailable;

    // ── 這一輪從 Palace Pal 拿到的座標（Sync 內填、Republish 讀）─────────────
    private List<Vector3> trapsConfirmed = [];
    private List<Vector3> trapsUnconfirmed = [];
    private List<Vector3> hoardsConfirmed = [];
    private List<Vector3> hoardsUnconfirmed = [];
    private List<Vector3> trapsMerged = [];
    private List<Vector3> hoardsMerged = [];
    private List<Vector3> silverCoffers = [];
    private List<Vector3> goldCoffers = [];

    // ── 給 UI 看的快取（只在框架執行緒上寫，繪製路徑只讀）────────────────────
    private BridgeState state = BridgeState.Unknown;

    /// <summary>對方有沒有兩態能力。<see langword="false"/>＝落回舊的合併端點。</summary>
    private bool twoState;

    private int lastTrapCount;
    private int lastHoardCount;
    private int lastTrapConfirmed;
    private int lastTrapUnconfirmed;
    private int lastHoardConfirmed;
    private int lastHoardUnconfirmed;

    /// <summary>可見快照的計數與年齡／樓層。<b>-1＝不知道</b>，介面上不可以畫成 0。</summary>
    private int lastVisibleTrapCount = -1;
    private int lastVisibleHoardCount = -1;
    private int lastSilverCount = -1;
    private int lastGoldCount = -1;
    private int lastVisibleAgeMs = -1;
    private int lastVisibleFloor = -1;
    private int lastPlaced;
    private int lastRejected;
    private int lastSkippedInvalid;
    private uint lastTerritory;
    private DateTime lastSyncLocal = DateTime.MinValue;

    protected override void OnEnable()
    {
        Throttle.Reset(SyncThrottleKey);
        Throttle.Reset(ForceResyncThrottleKey);
        lastSignature = string.Empty;
        state = BridgeState.Unknown;

        Svc.Framework.Update += OnUpdate;

        Svc.Log.Information(
            $"[{InternalName}] 模組啟用：陷阱＝{Config.ShowTraps}、寶藏＝{Config.ShowHoards}、"
            + $"看得到的寶箱＝{Config.ShowVisibleCoffers}、重新整理間隔 {EffectiveRefreshSeconds} 秒、"
            + $"圖示 已確認 陷阱 {EffectiveTrapIcon}／寶藏 {EffectiveHoardIcon}、"
            + $"可能有 陷阱 {EffectiveTrapUnconfirmedIcon}／寶藏 {EffectiveHoardUnconfirmedIcon}、"
            + $"寶箱 銀 {EffectiveSilverCofferIcon}／金 {EffectiveGoldCofferIcon}（設定值 0＝跟隨內建預設）");
    }

    protected override void OnDisable()
    {
        Svc.Framework.Update -= OnUpdate;

        // 🔴 停用（含外掛卸載）一定要把標記收乾淨。
        publisher.Clear();

        lastSignature = string.Empty;
        mappyWasAvailable = false;
        state = BridgeState.Unknown;
        lastTrapCount = 0;
        lastHoardCount = 0;
        lastTrapConfirmed = 0;
        lastTrapUnconfirmed = 0;
        lastHoardConfirmed = 0;
        lastHoardUnconfirmed = 0;
        ForgetVisibleSnapshot();
        lastPlaced = 0;
    }

    /// <summary>把可見快照的計數全部退回「不知道」。</summary>
    private void ForgetVisibleSnapshot()
    {
        silverCoffers = [];
        goldCoffers = [];
        lastVisibleTrapCount = -1;
        lastVisibleHoardCount = -1;
        lastSilverCount = -1;
        lastGoldCount = -1;
        lastVisibleAgeMs = -1;
        lastVisibleFloor = -1;
    }

    private string SyncThrottleKey => $"TCToolbox.{InternalName}.Sync";

    private string ForceResyncThrottleKey => $"TCToolbox.{InternalName}.ForceResync";

    /// <summary>夾緊之後的重新整理間隔（秒）。</summary>
    /// <remarks>⚠️ 夾緊發生在<b>讀取</b>那一刻：舊設定檔裡的 0 或負數不會讓它每幀跑。</remarks>
    private int EffectiveRefreshSeconds => Math.Clamp(Config.RefreshSeconds, 1, 60);

    private void OnUpdate(IFramework framework)
    {
        if (!Svc.ClientState.IsLoggedIn) return;
        if (!Throttle.Pass(SyncThrottleKey, EffectiveRefreshSeconds * 1000)) return;

        Sync();
    }

    private void Sync()
    {
        if (!PalacePalIpc.TryGetVersion(out var palVersion))
        {
            if (state != BridgeState.PalacePalMissing)
            {
                publisher.Clear();
                lastSignature = string.Empty;
            }

            state = BridgeState.PalacePalMissing;
            return;
        }

        if (!MappyMarkerIpc.TryGetVersion(out var mappyVersion))
        {
            state = BridgeState.MappyMissing;
            mappyWasAvailable = false;

            // 🔴 作廢簽章＋忘掉 handle：Mappy 回來時它的標記表是空的，留著會讓下一次同步
            //    以為「已經放上去了」——表現成標記再也不出現，且毫無徵兆。
            lastSignature = string.Empty;
            publisher.Forget();
            return;
        }

        if (!mappyWasAvailable)
        {
            lastSignature = string.Empty;
            publisher.Forget();
            mappyWasAvailable = true;
        }

        if (palVersion < PalacePalIpc.SupportedVersion || mappyVersion < MappyMarkerIpc.SupportedVersion)
        {
            state = BridgeState.VersionTooOld;
            return;
        }

        // ⚠️ 對方的簽章是 ushort。TerritoryType 目前遠小於 65536，但這裡仍然明確截斷而不是
        //    賭 CallGate 的數值轉換——照抄對方的型別是這組 IPC 唯一不會出事的接法。
        var territory = Svc.ClientState.TerritoryType;
        lastTerritory = territory;

        LoadLocations(territory);
        LoadVisibleSnapshot(territory);

        // 📌 全都空＝不是深層迷宮（或那一層還沒有任何資料）。這是正常狀態不是錯誤：
        //    Palace Pal 的 GetTerritoryIfReady 對非深層迷宮的區域一律回 null ⇒ 空清單。
        if (lastTrapCount == 0 && lastHoardCount == 0
            && silverCoffers.Count == 0 && goldCoffers.Count == 0)
        {
            if (lastSignature.Length > 0)
            {
                publisher.Publish([]);
                lastSignature = string.Empty;
                lastPlaced = 0;
            }

            state = BridgeState.NotInDeepDungeon;
            return;
        }

        Republish(territory);
    }

    /// <summary>
    /// 抓這個區域的陷阱／寶藏座標，優先用兩態端點。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>「對方有兩態版本」與「四支端點真的都答得出來」是兩件事</b>：只要有一支打不通就整組
    /// 落回合併端點。半套的兩態會讓地圖上少掉一整類的點，而且完全沒有徵兆。
    /// </remarks>
    private void LoadLocations(ushort territory)
    {
        // 六份清單一律先清空：版本探測若短路掉 LoadTwoState，留著的會是上一輪的座標。
        trapsConfirmed = [];
        trapsUnconfirmed = [];
        hoardsConfirmed = [];
        hoardsUnconfirmed = [];
        trapsMerged = [];
        hoardsMerged = [];

        twoState = PalacePalIpc.TryGetLocationStateVersion(out var stateVersion)
                   && stateVersion >= PalacePalIpc.LocationStateSupportedVersion
                   && LoadTwoState(territory);

        if (!twoState) LoadMerged(territory);

        lastTrapConfirmed = trapsConfirmed.Count;
        lastTrapUnconfirmed = trapsUnconfirmed.Count;
        lastHoardConfirmed = hoardsConfirmed.Count;
        lastHoardUnconfirmed = hoardsUnconfirmed.Count;
        lastTrapCount = twoState ? lastTrapConfirmed + lastTrapUnconfirmed : trapsMerged.Count;
        lastHoardCount = twoState ? lastHoardConfirmed + lastHoardUnconfirmed : hoardsMerged.Count;
    }

    /// <summary>兩態端點；任何一支打不通就回 <see langword="false"/>，由呼叫端整組落回。</summary>
    private bool LoadTwoState(ushort territory)
    {
        if (Config.ShowTraps
            && !(PalacePalIpc.TryGetConfirmedTraps(territory, out trapsConfirmed)
                 && PalacePalIpc.TryGetUnconfirmedTraps(territory, out trapsUnconfirmed)))
            return false;

        if (Config.ShowHoards
            && !(PalacePalIpc.TryGetConfirmedHoards(territory, out hoardsConfirmed)
                 && PalacePalIpc.TryGetUnconfirmedHoards(territory, out hoardsUnconfirmed)))
            return false;

        return true;
    }

    /// <summary>舊的合併端點。</summary>
    /// <remarks>🔴 先清掉兩態那四份：失敗的兩態查詢可能已經在裡面留了半套資料。</remarks>
    private void LoadMerged(ushort territory)
    {
        trapsConfirmed = [];
        trapsUnconfirmed = [];
        hoardsConfirmed = [];
        hoardsUnconfirmed = [];

        if (Config.ShowTraps && PalacePalIpc.TryGetTraps(territory, out var t)) trapsMerged = t;
        if (Config.ShowHoards && PalacePalIpc.TryGetHoards(territory, out var h)) hoardsMerged = h;
    }

    /// <summary>
    /// 抓「這一趟這一層現在真的看得到」的快照：寶箱畫到地圖上，陷阱／寶藏只拿來報數。
    /// </summary>
    /// <remarks>
    /// 🔴 先問快照有多舊：<c>-1</c> 或太舊一律當<b>不知道</b>——這時不畫任何寶箱，計數也留在 -1
    /// 讓介面畫成「？」。把不知道畫成 0 會讓人以為這一層是乾淨的。
    /// 🔑 陷阱與埋藏寶藏刻意不畫成標記：現形之後遊戲自己的地圖本來就會畫。
    /// </remarks>
    private void LoadVisibleSnapshot(ushort territory)
    {
        ForgetVisibleSnapshot();

        if (!Config.ShowVisibleCoffers) return;

        if (!PalacePalIpc.TryGetVisibleLocationVersion(out var visibleVersion)
            || visibleVersion < PalacePalIpc.VisibleLocationSupportedVersion)
            return;

        if (!PalacePalIpc.TryGetVisibleAgeMillis(territory, out var age)) return;

        lastVisibleAgeMs = age;
        if (age < 0 || age > VisibleSnapshotMaxAgeMs) return;

        PalacePalIpc.TryGetVisibleFloor(territory, out lastVisibleFloor);

        if (PalacePalIpc.TryGetVisibleTraps(territory, out var vt)) lastVisibleTrapCount = vt.Count;
        if (PalacePalIpc.TryGetVisibleHoards(territory, out var vh)) lastVisibleHoardCount = vh.Count;

        if (PalacePalIpc.TryGetVisibleSilverCoffers(territory, out silverCoffers))
            lastSilverCount = silverCoffers.Count;

        if (PalacePalIpc.TryGetVisibleGoldCoffers(territory, out goldCoffers))
            lastGoldCount = goldCoffers.Count;
    }

    private void Republish(ushort territory)
    {
        var mapId = MapIdOf(territory);

        var skippedInvalid = 0;

        pending.Clear();

        // 🔴 Palace Pal 給的是<b>世界座標</b>，Mappy 要的是<b>地圖座標</b>。
        //    不換算的失敗形式是標記靜靜地落在地圖上不相干的位置，沒有任何錯誤訊息。
        if (twoState)
        {
            AddAll(trapsConfirmed, "trap+", EffectiveTrapIcon,
                   "陷阱（已確認）", ConfirmedNote, mapId, ref skippedInvalid);
            AddAll(trapsUnconfirmed, "trap?", EffectiveTrapUnconfirmedIcon,
                   "陷阱（可能有）", UnconfirmedNote, mapId, ref skippedInvalid);
            AddAll(hoardsConfirmed, "hoard+", EffectiveHoardIcon,
                   "埋藏的寶藏（已確認）", ConfirmedNote, mapId, ref skippedInvalid);
            AddAll(hoardsUnconfirmed, "hoard?", EffectiveHoardUnconfirmedIcon,
                   "埋藏的寶藏（可能有）", UnconfirmedNote, mapId, ref skippedInvalid);
        }
        else
        {
            AddAll(trapsMerged, "trap", EffectiveTrapIcon, "陷阱", MergedNote, mapId, ref skippedInvalid);
            AddAll(hoardsMerged, "hoard", EffectiveHoardIcon,
                   "埋藏的寶藏", MergedNote, mapId, ref skippedInvalid);
        }

        AddAll(silverCoffers, "silver", EffectiveSilverCofferIcon,
               "銀寶箱", VisibleNote, mapId, ref skippedInvalid);
        AddAll(goldCoffers, "gold", EffectiveGoldCofferIcon,
               "金寶箱", VisibleNote, mapId, ref skippedInvalid);

        var signature = BuildSignature(mapId);

        var forceFull = Throttle.Pass(ForceResyncThrottleKey, ForceResyncIntervalMs);
        var contentChanged = signature != lastSignature;

        if (!forceFull && !contentChanged && state == BridgeState.Ok)
        {
            pending.Clear();
            return;
        }

        publisher.Publish(pending, forceFull);
        pending.Clear();

        lastPlaced = publisher.Placed;
        lastRejected = publisher.LastRejected;
        lastSkippedInvalid = skippedInvalid + publisher.LastDuplicateKeys;
        lastSyncLocal = DateTime.Now;
        state = BridgeState.Ok;
        lastSignature = signature;

        if (!contentChanged) return;

        // 🔴 Information 級：使用者跑 LogLevel 1。座標換算是這裡最可能靜默出錯的地方。
        Svc.Log.Information(
            $"[{InternalName}] 區域 {territory}（地圖 {mapId}）：陷阱 {lastTrapCount}、寶藏 {lastHoardCount}"
            + (twoState
                   ? $"（已確認 {lastTrapConfirmed}／{lastHoardConfirmed}、"
                     + $"可能有 {lastTrapUnconfirmed}／{lastHoardUnconfirmed}）"
                   : "（對方較舊，分不出兩態）")
            + $"、現在看得到 陷阱 {CountText(lastVisibleTrapCount)}、寶藏 {CountText(lastVisibleHoardCount)}、"
            + $"銀寶箱 {CountText(lastSilverCount)}、金寶箱 {CountText(lastGoldCount)}"
            + $" → 地圖上 {lastPlaced} 筆（本次異動 {publisher.LastIpcCalls} 次）"
            + (lastSkippedInvalid > 0 ? $"、座標換不出來 {lastSkippedInvalid} 筆" : string.Empty)
            + (lastRejected > 0 ? $"、被 Mappy 拒絕 {lastRejected} 筆" : string.Empty));
    }

    /// <summary>把一組世界座標換算後排進待推清單。</summary>
    /// <remarks>
    /// 🔑 <b>鍵用「種類＋原始世界座標」組出來，不用清單索引。</b>Palace Pal 那份清單來自
    /// <c>ConcurrentBag</c> 的列舉，<b>順序不保證跨次穩定</b>——用索引當鍵的話，
    /// 順序一變就變成「整組刪掉重加」，每一次同步都在洗 Mappy 的標記表。
    /// </remarks>
    private void AddAll(
        List<Vector3> positions,
        string kind,
        uint iconId,
        string label,
        string note,
        uint mapId,
        ref int skippedInvalid)
    {
        foreach (var world in positions)
        {
            if (!MapCoords.TryWorldToMap(mapId, world, out var map))
            {
                skippedInvalid++;
                continue;
            }

            var key = kind
                      + ":"
                      + world.X.ToString("F1", CultureInfo.InvariantCulture)
                      + ","
                      + world.Y.ToString("F1", CultureInfo.InvariantCulture)
                      + ","
                      + world.Z.ToString("F1", CultureInfo.InvariantCulture);

            var tooltip = label
                          + "\n"
                          + map.X.ToString("F1", CultureInfo.InvariantCulture)
                          + ", "
                          + map.Y.ToString("F1", CultureInfo.InvariantCulture)
                          + "\n"
                          + note
                          + "\n資料來源：Palace Pal";

            pending.Add(new MappyMarkerPublisher.Marker(key, mapId, map, iconId, tooltip));
        }
    }

    /// <summary>提示文字第三行：這個點位的可信度是什麼意思。</summary>
    /// <remarks>
    /// ⚠️ 「已確認／可能有」講的是<b>資料可信度</b>，不是「這一層現在有」——一個區域涵蓋十層，
    /// 那兩份清單都是十層累積下來的候選生成點。
    /// </remarks>
    private const string ConfirmedNote = "你自己在這個座標遇過；這是整個區域累積的候選點，不代表這一層現在有";

    private const string UnconfirmedNote = "只來自共享資料，本機還沒親眼確認過；不代表這一層現在有";

    private const string MergedNote = "Palace Pal 版本較舊，分不出已確認／可能有";

    private const string VisibleNote = "此刻真的擺在那裡（走遠到遊戲收掉物件就會消失）";

    /// <summary>計數轉字串。<b>-1＝不知道，畫成「？」不要畫成 0。</b></summary>
    private static string CountText(int count) =>
        count < 0 ? "？" : count.ToString(CultureInfo.InvariantCulture);

    /// <summary>可見快照的樓層說明。<c>-1</c>＝沒快照；<c>0</c>＝有快照但樓層還沒讀到。</summary>
    private string VisibleFloorText => lastVisibleFloor switch
    {
        < 0 => "（還沒拍到這一層的快照）",
        0 => "（樓層還沒讀到）",
        _ => $"（第 {lastVisibleFloor} 層）",
    };

    /// <summary>
    /// 內容簽章：這串沒變＝地圖上該畫的東西沒變。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>設定值也要進簽章</b>——不然使用者改了圖示 id 或關掉其中一種之後，
    /// 因為清單本身沒變而完全不會重畫，表現成「設定沒有作用」。
    /// </remarks>
    private string BuildSignature(uint mapId)
    {
        var sb = new StringBuilder();
        sb.Append(mapId).Append('|')
          .Append(EffectiveTrapIcon).Append('|').Append(EffectiveTrapUnconfirmedIcon).Append('|')
          .Append(EffectiveHoardIcon).Append('|').Append(EffectiveHoardUnconfirmedIcon).Append('|')
          .Append(EffectiveSilverCofferIcon).Append('|').Append(EffectiveGoldCofferIcon).Append('|');

        foreach (var marker in pending)
            sb.Append(marker.Key).Append(';');

        return sb.ToString();
    }

    private static readonly Dictionary<uint, uint> MapIdCache = [];

    /// <summary>區域 → 地圖列號；查不到回 0（Mappy 會拒絕 0，那是對的）。</summary>
    private static uint MapIdOf(uint territoryId)
    {
        if (territoryId == 0) return 0;
        if (MapIdCache.TryGetValue(territoryId, out var cached)) return cached;

        uint mapId = 0;
        try
        {
            mapId = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>()
                       .GetRowOrDefault(territoryId)?.Map.RowId ?? 0;
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, $"[{nameof(PalacePalOnMappy)}] 查 territory {territoryId} 的地圖失敗");
        }

        MapIdCache[territoryId] = mapId;
        return mapId;
    }

    // ── 模組列上的提示 ──────────────────────────────────────────────────────

    public override ModuleNotice? RowNotice
    {
        get
        {
            if (!IsEnabled) return null;

            return state switch
            {
                BridgeState.PalacePalMissing => new ModuleNotice(
                    ModuleNoticeLevel.Unknown,
                    "Palace Pal 未載入",
                    "找不到 Palace Pal 的 IPC，所以沒有陷阱／寶藏資料可以讀。模組會靜靜地等它出現，不需要重開。"),

                BridgeState.MappyMissing => new ModuleNotice(
                    ModuleNoticeLevel.Unknown,
                    "Mappy 未載入",
                    "找不到 Mappy 的標記 IPC，所以標記沒有地方可以放。模組會靜靜地等它出現，不需要重開。"),

                BridgeState.VersionTooOld => new ModuleNotice(
                    ModuleNoticeLevel.Warning,
                    "對方的 IPC 版本過舊",
                    "Palace Pal 或 Mappy 回報的 IPC 版本比本模組需要的還舊，為了避免傳錯資料，同步已停下來。"),

                BridgeState.Unknown => new ModuleNotice(
                    ModuleNoticeLevel.Unknown,
                    "尚未同步",
                    "模組剛啟用，還沒完成第一次同步。"),

                BridgeState.Ok when lastRejected > 0 => new ModuleNotice(
                    ModuleNoticeLevel.Warning,
                    $"{lastRejected} 筆被 Mappy 拒絕",
                    "Mappy 端每個來源最多 512 筆標記，超過的會被拒絕；圖示 id 設成 0 也會被拒絕。"),

                // 「不知道」本身要在列上看得見，不能只表現成地圖上少了一種圖示。
                BridgeState.Ok when !twoState => new ModuleNotice(
                    ModuleNoticeLevel.Unknown,
                    "分不出「已確認／可能有」",
                    "這個版本的 Palace Pal 只提供合併後的清單，所以地圖上每個點都畫成同一種。"
                    + "更新 Palace Pal 之後會自動分成兩顆圖示，不需要動這裡的設定。"),

                _ => null,
            };
        }
    }

    // ── 設定 UI ─────────────────────────────────────────────────────────────

    public override void DrawConfig()
    {
        DrawStatus();

        ImGui.Separator();

        var traps = Config.ShowTraps;
        if (ImGui.Checkbox("顯示陷阱", ref traps))
        {
            Config.ShowTraps = traps;
            Plugin.Instance.Config.Save();
            lastSignature = string.Empty;
        }

        ImGui.SameLine();

        var hoards = Config.ShowHoards;
        if (ImGui.Checkbox("顯示埋藏的寶藏", ref hoards))
        {
            Config.ShowHoards = hoards;
            Plugin.Instance.Config.Save();
            lastSignature = string.Empty;
        }

        var coffers = Config.ShowVisibleCoffers;
        if (ImGui.Checkbox("顯示現在看得到的銀／金寶箱", ref coffers))
        {
            Config.ShowVisibleCoffers = coffers;
            Plugin.Instance.Config.Save();
            lastSignature = string.Empty;
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "只畫「這一趟這一層此刻真的擺在那裡」的寶箱。" + Environment.NewLine
                + "範圍由遊戲自己的物件串流距離決定——走遠到遊戲把物件收掉，標記下次同步就消失。"
                + Environment.NewLine
                + "陷阱與埋藏寶藏刻意不走這條：現形之後遊戲自己的地圖本來就會畫。"
                + Environment.NewLine
                + "需要 Palace Pal 有這組端點；沒有的話這個選項不會有任何作用。");
        }

        ImGui.SetNextItemWidth(160f);
        var seconds = Config.RefreshSeconds;
        if (ImGui.SliderInt("重新整理間隔（秒）", ref seconds, 1, 30))
        {
            Config.RefreshSeconds = seconds;
            Plugin.Instance.Config.Save();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "每隔這麼久向 Palace Pal 拿一次目前這一層的資料。" + Environment.NewLine
                + "內容沒變的話只是比對一下字串，不會動到 Mappy 上的任何標記。");
        }

        if (!ImGui.CollapsingHeader("圖示###palacePalIcons")) return;

        using var indent = ImRaii.PushIndent();

        ImGui.TextDisabled(
            "「已確認」預設取自遊戲裡「魔陶器：全景」與「魔陶器：感知寶藏」的道具圖示；"
            + Environment.NewLine + "「可能有」兩種預設都是問號（種類寫在提示文字裡，想分開就各自改）。");

        GameIcons.DrawIconIdSetting("陷阱（已確認）", Config.TrapIconId, DefaultTrapIconId, value =>
        {
            Config.TrapIconId = value;
            Plugin.Instance.Config.Save();
            lastSignature = string.Empty;
        });

        GameIcons.DrawIconIdSetting(
            "陷阱（可能有）", Config.TrapUnconfirmedIconId, DefaultTrapUnconfirmedIconId, value =>
            {
                Config.TrapUnconfirmedIconId = value;
                Plugin.Instance.Config.Save();
                lastSignature = string.Empty;
            });

        GameIcons.DrawIconIdSetting("埋藏的寶藏（已確認）", Config.HoardIconId, DefaultHoardIconId, value =>
        {
            Config.HoardIconId = value;
            Plugin.Instance.Config.Save();
            lastSignature = string.Empty;
        });

        GameIcons.DrawIconIdSetting(
            "埋藏的寶藏（可能有）", Config.HoardUnconfirmedIconId, DefaultHoardUnconfirmedIconId, value =>
            {
                Config.HoardUnconfirmedIconId = value;
                Plugin.Instance.Config.Save();
                lastSignature = string.Empty;
            });

        GameIcons.DrawIconIdSetting("銀寶箱", Config.SilverCofferIconId, DefaultSilverCofferIconId, value =>
        {
            Config.SilverCofferIconId = value;
            Plugin.Instance.Config.Save();
            lastSignature = string.Empty;
        });

        GameIcons.DrawIconIdSetting("金寶箱", Config.GoldCofferIconId, DefaultGoldCofferIconId, value =>
        {
            Config.GoldCofferIconId = value;
            Plugin.Instance.Config.Save();
            lastSignature = string.Empty;
        });
    }

    /// <summary>
    /// 設定畫面上的狀態列。
    /// </summary>
    /// <remarks>
    /// 📌 這裡只呼叫兩支<b>無參數、零副作用</b>的版本查詢；清單內容一律用
    /// <see cref="OnUpdate"/> 快取下來的數字。
    /// </remarks>
    /// <summary>對方沒有兩態端點時就說出來。</summary>
    /// <remarks>🔑 模組還關著時也要畫得到——使用者正是在這一刻決定要不要開。</remarks>
    private void DrawTwoStateHint()
    {
        if (!twoState)
            ImGui.TextDisabled("Palace Pal 版本較舊：分不出「已確認／可能有」，全部畫成同一種。");
    }

    private void DrawStatus()
    {
        if (!IsEnabled && Throttle.Pass($"TCToolbox.{InternalName}.UiProbe", UiProbeIntervalMs))
        {
            var palOk = PalacePalIpc.TryGetVersion(out _);
            var mappyOk = MappyMarkerIpc.TryGetVersion(out _);

            twoState = PalacePalIpc.TryGetLocationStateVersion(out var stateVersion)
                       && stateVersion >= PalacePalIpc.LocationStateSupportedVersion;

            state = !palOk ? BridgeState.PalacePalMissing
                : !mappyOk ? BridgeState.MappyMissing
                : BridgeState.Unknown;
        }

        switch (state)
        {
            case BridgeState.PalacePalMissing:
                ImGui.TextDisabled("Palace Pal 未載入——沒有陷阱／寶藏資料可以讀。");
                return;

            case BridgeState.MappyMissing:
                ImGui.TextDisabled("Mappy 未載入——標記沒有地方可以放。");
                return;

            case BridgeState.VersionTooOld:
                ImGui.TextDisabled("對方的 IPC 版本過舊，同步已停下來。");
                return;

            case BridgeState.NotInDeepDungeon:
                ImGui.TextDisabled("目前這一層沒有資料（不在深層迷宮裡，或這一層還沒探索過）。");
                return;

            case BridgeState.Unknown:
                ImGui.TextDisabled(IsEnabled ? "尚未完成第一次同步。" : "兩端都在，啟用模組後開始同步。");
                DrawTwoStateHint();
                return;

            case BridgeState.Ok:
            default:
                break;
        }

        ImGui.TextUnformatted(
            twoState
                ? $"目前放上 {lastPlaced} 筆標記（陷阱 已確認 {lastTrapConfirmed}／可能有 {lastTrapUnconfirmed}、"
                  + $"寶藏 已確認 {lastHoardConfirmed}／可能有 {lastHoardUnconfirmed}）。"
                : $"目前放上 {lastPlaced} 筆標記（陷阱 {lastTrapCount}、寶藏 {lastHoardCount}）。");

        if (ImGui.IsItemHovered())
        {
            var sb = new StringBuilder();
            sb.Append("最後同步：")
              .Append(lastSyncLocal == DateTime.MinValue
                          ? "？"
                          : lastSyncLocal.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
            sb.Append('\n').Append("區域：").Append(lastTerritory)
              .Append("（地圖 ").Append(MapIdOf(lastTerritory)).Append('）');
            sb.Append('\n').Append("座標換不出來：").Append(lastSkippedInvalid).Append(" 筆");
            sb.Append('\n').Append("被 Mappy 拒絕：").Append(lastRejected).Append(" 筆");
            sb.Append('\n').Append("標記來源：").Append(MarkerSource).Append("（可在 Mappy 的設定裡單獨關掉）");

            ImGui.SetTooltip(sb.ToString());
        }

        DrawTwoStateHint();

        if (!Config.ShowVisibleCoffers) return;

        // 🔴 數不出來時畫「？」不是 0：把不知道畫成 0 會讓人以為這一層什麼都沒有。
        ImGui.TextUnformatted(
            $"現在看得到：陷阱 {CountText(lastVisibleTrapCount)}、寶藏 {CountText(lastVisibleHoardCount)}、"
            + $"銀寶箱 {CountText(lastSilverCount)}、金寶箱 {CountText(lastGoldCount)}{VisibleFloorText}");

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                lastVisibleAgeMs < 0
                    ? "Palace Pal 還沒拍到這個區域的快照（不在深層迷宮，或那一層還沒載入完）。"
                    : $"快照拍下來 {lastVisibleAgeMs} 毫秒（超過 {VisibleSnapshotMaxAgeMs} 就當作不知道）。"
                      + Environment.NewLine
                      + "只算遊戲自己還留在物件表裡的實體；陷阱要現形、埋藏寶藏要感知過才有實體。");
        }
    }
}

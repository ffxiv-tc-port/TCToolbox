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
/// <para>
/// 🔴 <b>純顯示，零自動化。</b>只呼叫 Palace Pal 的三支唯讀端點；不移動、不開箱、不碰目標，
/// 也不寫回它的任何資料。唯一的副作用是 Mappy 地圖上多出一組來源為 <see cref="MarkerSource"/>
/// 的標記。
/// </para>
/// <para>
/// 📌 <b>為什麼還需要它</b>：Palace Pal 與 NecroLens 都是把陷阱畫在<b>世界上</b>
/// （視野內、有距離限制）。畫到地圖上是另一件事——可以先看整層的分布再決定往哪邊走。
/// </para>
/// <para>
/// 🔴 <b>預設關。</b>世界上已經有東西在畫了，地圖要不要跟著疊一份應該由使用者自己選；
/// 深層迷宮一層的陷阱數量不少，硬塞給每個人是幫倒忙。
/// </para>
/// <para>
/// 🔑 <b>「可能有」與「已確認」不分兩態是刻意的</b>：Palace Pal 的 IPC 端點回的是它
/// <b>已經在畫的那一份</b>（伺服器下載 ＋ 本機看過，早就合併過了），
/// 分不出來源。與其自己發明一個猜出來的兩態，不如誠實地畫成同一種——
/// 要看「可能有 vs 已確認」的差別，Palace Pal 自己的世界疊加層本來就分得出來。
/// </para>
/// </remarks>
public sealed class PalacePalOnMappy : TcModule
{
    public override string InternalName => "PalacePalOnMappy";

    public override string DisplayName => "深層迷宮陷阱與寶藏顯示到 Mappy";

    public override string Description =>
        "把 Palace Pal 已經標示出來的陷阱與埋藏寶藏位置，同步成 Mappy 地圖上的標記，"
        + "可以先看整層的分布再決定路線。純顯示：不移動、不開箱、不改 Palace Pal 的資料。"
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
    /// ✅ 2026-09-08 以 <c>tools/sqpack/path_exists.py</c> 離線直讀台服 index，
    /// 確認 <c>ui/icon/027000/027902.tex</c> 存在（校準閘門通過）。
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

    private uint EffectiveTrapIcon => Config.TrapIconId is 0 ? DefaultTrapIconId : Config.TrapIconId;

    private uint EffectiveHoardIcon => Config.HoardIconId is 0 ? DefaultHoardIconId : Config.HoardIconId;

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

    // ── 給 UI 看的快取（只在框架執行緒上寫，繪製路徑只讀）────────────────────
    private BridgeState state = BridgeState.Unknown;
    private int lastTrapCount;
    private int lastHoardCount;
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
            + $"重新整理間隔 {EffectiveRefreshSeconds} 秒、"
            + $"圖示 陷阱 {EffectiveTrapIcon}／寶藏 {EffectiveHoardIcon}"
            + $"（設定值 {Config.TrapIconId}／{Config.HoardIconId}，0＝跟隨內建預設）");
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
        lastPlaced = 0;
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

        // 🔴 兩個查詢分開寫、不要塞進一個短路的 &&：`out var` 在 `&&` 右側對編譯器而言是
        //    「可能沒被指派」，而且那個寫法也讓「關掉這一種」與「IPC 打不通」混成同一件事。
        //    這裡明確地把兩者都收斂成空清單——顯示層不需要分辨，但讀碼的人需要看得出來。
        List<Vector3> traps = [];
        if (Config.ShowTraps && PalacePalIpc.TryGetTraps(territory, out var t)) traps = t;

        List<Vector3> hoards = [];
        if (Config.ShowHoards && PalacePalIpc.TryGetHoards(territory, out var h)) hoards = h;

        lastTrapCount = traps.Count;
        lastHoardCount = hoards.Count;

        // 📌 兩邊都空＝不是深層迷宮（或那一層還沒有任何資料）。這是正常狀態不是錯誤：
        //    Palace Pal 的 GetTerritoryIfReady 對非深層迷宮的區域一律回 null ⇒ 空清單。
        if (traps.Count == 0 && hoards.Count == 0)
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

        Republish(territory, traps, hoards);
    }

    private void Republish(ushort territory, List<Vector3> traps, List<Vector3> hoards)
    {
        var mapId = MapIdOf(territory);
        var trapIcon = EffectiveTrapIcon;
        var hoardIcon = EffectiveHoardIcon;

        var skippedInvalid = 0;

        pending.Clear();

        // 🔴 Palace Pal 給的是<b>世界座標</b>，Mappy 要的是<b>地圖座標</b>。
        //    不換算的失敗形式是標記靜靜地落在地圖上不相干的位置，沒有任何錯誤訊息。
        AddAll(traps, "trap", trapIcon, "陷阱", mapId, ref skippedInvalid);
        AddAll(hoards, "hoard", hoardIcon, "埋藏的寶藏", mapId, ref skippedInvalid);

        var signature = BuildSignature(mapId, trapIcon, hoardIcon);

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
            $"[{InternalName}] 區域 {territory}（地圖 {mapId}）：陷阱 {traps.Count}、寶藏 {hoards.Count}"
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
        List<Vector3> positions, string kind, uint iconId, string label, uint mapId, ref int skippedInvalid)
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
                          + "\n資料來源：Palace Pal";

            pending.Add(new MappyMarkerPublisher.Marker(key, mapId, map, iconId, tooltip));
        }
    }

    /// <summary>
    /// 內容簽章：這串沒變＝地圖上該畫的東西沒變。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>設定值也要進簽章</b>——不然使用者改了圖示 id 或關掉其中一種之後，
    /// 因為清單本身沒變而完全不會重畫，表現成「設定沒有作用」。
    /// </remarks>
    private string BuildSignature(uint mapId, uint trapIcon, uint hoardIcon)
    {
        var sb = new StringBuilder();
        sb.Append(mapId).Append('|').Append(trapIcon).Append('|').Append(hoardIcon).Append('|');

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

        ImGui.TextDisabled("預設值取自遊戲裡「魔陶器：全景」與「魔陶器：感知寶藏」的道具圖示。");

        GameIcons.DrawIconIdSetting("陷阱", Config.TrapIconId, DefaultTrapIconId, value =>
        {
            Config.TrapIconId = value;
            Plugin.Instance.Config.Save();
            lastSignature = string.Empty;
        });

        GameIcons.DrawIconIdSetting("埋藏的寶藏", Config.HoardIconId, DefaultHoardIconId, value =>
        {
            Config.HoardIconId = value;
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
    private void DrawStatus()
    {
        if (!IsEnabled && Throttle.Pass($"TCToolbox.{InternalName}.UiProbe", UiProbeIntervalMs))
        {
            var palOk = PalacePalIpc.TryGetVersion(out _);
            var mappyOk = MappyMarkerIpc.TryGetVersion(out _);

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
                return;

            case BridgeState.Ok:
            default:
                break;
        }

        ImGui.TextUnformatted($"目前放上 {lastPlaced} 筆標記（陷阱 {lastTrapCount}、寶藏 {lastHoardCount}）。");

        if (!ImGui.IsItemHovered()) return;

        var sb = new StringBuilder();
        sb.Append("最後同步：")
          .Append(lastSyncLocal == DateTime.MinValue
                      ? "？"
                      : lastSyncLocal.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
        sb.Append('\n').Append("區域：").Append(lastTerritory).Append("（地圖 ").Append(MapIdOf(lastTerritory)).Append('）');
        sb.Append('\n').Append("座標換不出來：").Append(lastSkippedInvalid).Append(" 筆");
        sb.Append('\n').Append("被 Mappy 拒絕：").Append(lastRejected).Append(" 筆");
        sb.Append('\n').Append("標記來源：").Append(MarkerSource).Append("（可在 Mappy 的設定裡單獨關掉）");

        ImGui.SetTooltip(sb.ToString());
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using TCToolbox.Core;
using CSGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace TCToolbox.Modules;

/// <summary>
/// 把「在場的寶箱」與「還沒共鳴的風脈泉」畫到<b>小地圖</b>上（透過 Mini-Mappingway）。
/// </summary>
/// <remarks>
/// <para>
/// 📌 <b>為什麼是小地圖</b>：艦隊裡已經有好幾個東西會畫大地圖（Mappy 標記）與世界疊加層
/// （NecroLens／Palace Pal），但<b>小地圖</b>只有 Mini-Mappingway 畫得到，
/// 而小地圖是唯一「不必按任何鍵就一直在畫面上」的方向指示。
/// 目標就是<b>不用開大地圖也知道往哪走</b>。
/// </para>
/// <para>
/// 🔴 <b>純顯示，零自動化。</b>只讀物件表、只呼叫 Mini-Mappingway 的加／刪端點；
/// 不移動、不開箱、不改目標、不與任何 NPC 互動。
/// </para>
/// <para>
/// 🔴 <b>預設關</b>（所有模組都是）。啟用之後兩種來源預設都開，這樣「打開模組」就真的看得到東西；
/// 兩種都可以單獨關掉，而且在 Mini-Mappingway 自己的設定裡還可以再關一層（含顏色與大小）。
/// </para>
/// <para>
/// 🔴 <b>絕不跨幀保存原生指標。</b>每一輪都重新走訪物件表；記下來交給 Mini-Mappingway 的是
/// <c>GameObjectId</c>（受管理的整數），而對方也是拿 id 每幀重查位置。
/// 判「是不是風脈泉」時要碰一次原生的 <c>EventHandler</c>，那個解參考與取得物件位址在<b>同一幀、
/// 同一個方法內</b>完成，不留到下一幀。
/// </para>
/// <para>
/// ⚠️ <b>已知限制（不是缺陷，是對方的設計）</b>：Mini-Mappingway 在<b>戰鬥中</b>會整組隱藏標記
/// （<c>NaviMapWindow.DrawConditions</c>），在 PvP 區域也一樣。戰鬥結束就會自己回來。
/// </para>
/// <para>
/// 📌 需要 Mini-Mappingway 的 IPC <b>1.2 以上</b>：1.1 的 <c>AddPerson</c> 對非玩家物件是
/// 完全靜默地不生效的（它找物件時只掃物件表的玩家欄位）。版本不夠時模組列上會直接說。
/// </para>
/// </remarks>
public sealed unsafe class NearbyOnMinimap : TcModule
{
    public override string InternalName => "NearbyOnMinimap";

    public override string DisplayName => "附近的寶箱與風脈泉顯示到小地圖";

    public override string Description =>
        "把在場的寶箱（含深宮的銅／銀／金寶箱）與還沒共鳴的風脈泉，畫成小地圖上的圓點，"
        + "不用開大地圖就知道往哪個方向走。純顯示：不移動、不開箱、不改目標。"
        + "需要安裝 Mini-Mappingway（1.2 以上）。顏色、大小、要不要顯示可以在 Mini-Mappingway 的設定裡再調。"
        + "預設關閉。";

    public override ModuleCategory Category => ModuleCategory.Misc;

    public override bool HasConfigUI => true;

    /// <summary>
    /// 寶箱的標記來源名稱。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>不要改。</b>Mini-Mappingway 拿這個字串當鍵把顏色／大小／要不要顯示存進它自己的設定檔，
    /// 改了之後使用者調過的那一份會變成永遠留在對方設定裡的孤兒，而新來源會以預設值冒出來。
    /// 📌 它同時是顯示在對方設定畫面上的標籤，所以要人看得懂。
    /// </remarks>
    public const string ChestSource = "TC Toolbox 寶箱";

    /// <summary>風脈泉的標記來源名稱。<b>不要改</b>，理由同 <see cref="ChestSource"/>。</summary>
    public const string AetherCurrentSource = "TC Toolbox 風脈泉";

    /// <summary>
    /// 風脈泉的 <c>AetherCurrent</c> 列號基底（列號＝EventId，高 16 位元就是 ContentId 0x2B）。
    /// </summary>
    private const uint AetherCurrentIdBase = 0x2B0000;

    /// <summary>無條件全量重推的間隔（毫秒）——對方可能在我們沒看見的時候被重載過。</summary>
    private const int ForceResyncIntervalMs = 60_000;

    /// <summary>設定畫面在模組關著時重新探測「對方在不在」的間隔（毫秒）。</summary>
    private const int UiProbeIntervalMs = 2_000;

    /// <summary>
    /// 寶箱標記的<b>初始</b>顏色（金黃）。
    /// </summary>
    /// <remarks>
    /// 📌 只有「這個來源第一次出現在 Mini-Mappingway」時才會用到：對方會把來源設定存進自己的
    /// 設定檔，之後一律讀存檔那一份。⇒ <b>使用者改過的顏色不會被我們蓋掉</b>，
    /// 也代表這裡不需要做成可設定的（做了反而是一個看起來會動、其實不會動的設定）。
    /// </remarks>
    private static readonly Vector4 ChestColour = new(1f, 0.80f, 0.25f, 1f);

    /// <summary>風脈泉標記的<b>初始</b>顏色（水藍）。理由同 <see cref="ChestColour"/>。</summary>
    private static readonly Vector4 AetherCurrentColour = new(0.30f, 0.85f, 1f, 1f);

    /// <summary>橋接目前的狀態。<b>「不知道」是零值</b>。</summary>
    private enum BridgeState
    {
        /// <summary>還沒同步過任何一次。</summary>
        Unknown = 0,

        /// <summary>Mini-Mappingway 沒裝或還沒註冊 IPC。</summary>
        MiniMappingwayMissing,

        /// <summary>對方回報的 IPC 版本不合用。</summary>
        VersionMismatch,

        /// <summary>一切正常。</summary>
        Ok,
    }

    private static NearbyOnMinimapConfig Config => Plugin.Instance.Config.NearbyOnMinimap;

    private readonly MiniMappingwayPublisher chestPublisher = new(ChestSource);

    private readonly MiniMappingwayPublisher currentPublisher = new(AetherCurrentSource);

    private readonly List<MiniMappingwayPublisher.Person> pendingChests = [];

    private readonly List<MiniMappingwayPublisher.Person> pendingCurrents = [];

    /// <summary>對方上一次探測時在不在（用來偵測「從不在變成在」）。</summary>
    private bool wasAvailable;

    // ── 給 UI 看的快取（只在框架執行緒上寫，繪製路徑只讀）────────────────────
    private BridgeState state = BridgeState.Unknown;
    private int lastMajor;
    private int lastMinor;
    private int lastChestCount;
    private int lastCurrentCount;
    private int lastSkippedWideId;
    private DateTime lastSyncLocal = DateTime.MinValue;

    protected override void OnEnable()
    {
        ChestIdentity.EnsureBuilt();

        Throttle.Reset(SyncThrottleKey);
        Throttle.Reset(ForceResyncThrottleKey);

        state = BridgeState.Unknown;
        wasAvailable = false;
        lastSkippedWideId = 0;

        Svc.Framework.Update += OnUpdate;

        Svc.Log.Information(
            $"[{InternalName}] 模組啟用：寶箱＝{Config.ShowChests}、風脈泉＝{Config.ShowAetherCurrents}"
            + $"（未共鳴才顯示＝{Config.OnlyUnattunedAetherCurrents}）、"
            + $"搜尋距離 {EffectiveMaxDistance:0} 公尺、重新整理間隔 {EffectiveRefreshMs} 毫秒。"
            + $"寶箱 EventObj 對照表 {ChestIdentity.EventObjIds.Count} 筆"
            + (string.IsNullOrEmpty(ChestIdentity.DegradedReason) ? "。" : "（已降級）。"));
    }

    protected override void OnDisable()
    {
        Svc.Framework.Update -= OnUpdate;

        // 🔴 停用（含外掛卸載）一定要把標記收乾淨，否則小地圖上會留下再也不會更新的圓點。
        chestPublisher.Clear();
        currentPublisher.Clear();

        pendingChests.Clear();
        pendingCurrents.Clear();

        state = BridgeState.Unknown;
        wasAvailable = false;
        lastChestCount = 0;
        lastCurrentCount = 0;
    }

    private string SyncThrottleKey => $"TCToolbox.{InternalName}.Sync";

    private string ForceResyncThrottleKey => $"TCToolbox.{InternalName}.ForceResync";

    /// <summary>夾緊之後的重新整理間隔（毫秒）。</summary>
    /// <remarks>⚠️ 夾緊發生在<b>讀取</b>那一刻：舊設定檔裡的 0 或負數不會讓它每幀跑。</remarks>
    private static int EffectiveRefreshMs => Math.Clamp(Config.RefreshMilliseconds, 100, 5000);

    /// <summary>夾緊之後的搜尋距離（公尺）。</summary>
    private static float EffectiveMaxDistance => Math.Clamp(Config.MaxDistance, 5f, 200f);

    private void OnUpdate(IFramework framework)
    {
        if (!Svc.ClientState.IsLoggedIn) return;

        // 📌 節流用 Throttle（本外掛自己的、只在主執行緒用的字典），不是 ECommons 的 EzThrottler。
        //    這條路徑只跑在 framework 執行緒上，沒有 IPC 端點會進來。
        if (!Throttle.Pass(SyncThrottleKey, EffectiveRefreshMs)) return;

        Sync();
    }

    private void Sync()
    {
        if (!MiniMappingwayIpc.TryGetVersion(out var major, out var minor))
        {
            state = BridgeState.MiniMappingwayMissing;

            // 🔴 忘掉追蹤表：對方回來時它的清單是空的，留著會讓下一輪同步以為「已經加上去了」
            //    ——表現成標記再也不出現，且毫無徵兆。
            ForgetEverything();
            return;
        }

        lastMajor = major;
        lastMinor = minor;

        // 🔴 主版號用相等比對（主版號改變＝破壞相容性），次版號用 >=（對方合法遞增不該讓我們失效）。
        if (major != MiniMappingwayIpc.RequiredMajor || minor < MiniMappingwayIpc.RequiredMinor)
        {
            state = BridgeState.VersionMismatch;
            ForgetEverything();
            return;
        }

        if (!wasAvailable)
        {
            // 🔴 註冊來源會把該來源的名單整個換成一個新的空字典（對方的 AddOrUpdateSource
            //    對 PersonDict 是無條件覆蓋）⇒ 只在這個轉換點呼叫，而且呼叫完一定要 Forget。
            //    每輪都註冊的話，每一輪都會把上一輪加進去的人洗掉。
            MiniMappingwayIpc.RegisterSource(ChestSource, ChestColour);
            MiniMappingwayIpc.RegisterSource(AetherCurrentSource, AetherCurrentColour);

            chestPublisher.Forget();
            currentPublisher.Forget();

            wasAvailable = true;
            Throttle.Reset(ForceResyncThrottleKey);

            Svc.Log.Information(
                $"[{InternalName}] 已向 Mini-Mappingway（IPC {major}.{minor}）註冊兩個標記來源："
                + $"「{ChestSource}」與「{AetherCurrentSource}」。");
        }

        CollectTargets();

        var forceFull = Throttle.Pass(ForceResyncThrottleKey, ForceResyncIntervalMs);

        chestPublisher.Publish(pendingChests, forceFull);
        currentPublisher.Publish(pendingCurrents, forceFull);

        lastChestCount = chestPublisher.Placed;
        lastCurrentCount = currentPublisher.Placed;
        lastSyncLocal = DateTime.Now;
        state = BridgeState.Ok;
    }

    private void ForgetEverything()
    {
        wasAvailable = false;
        chestPublisher.Forget();
        currentPublisher.Forget();
        lastChestCount = 0;
        lastCurrentCount = 0;
    }

    /// <summary>
    /// 走訪物件表，把這一刻想顯示的東西收進 <see cref="pendingChests"/>／<see cref="pendingCurrents"/>。
    /// </summary>
    /// <remarks>
    /// 🔴 取得與使用都在這個方法內、同一幀完成。存進待推清單的只有 <c>GameObjectId</c> 與名字，
    /// 沒有任何原生位址。
    /// </remarks>
    private void CollectTargets()
    {
        pendingChests.Clear();
        pendingCurrents.Clear();
        lastSkippedWideId = 0;

        var player = Svc.Objects.LocalPlayer;
        if (player == null) return;

        var wantChests = Config.ShowChests;
        var wantCurrents = Config.ShowAetherCurrents;
        if (!wantChests && !wantCurrents) return;

        var maxDistanceSquared = EffectiveMaxDistance * EffectiveMaxDistance;
        var playerPosition = player.Position;

        foreach (var obj in Svc.Objects)
        {
            // 兩種目標都只可能是這兩類，先擋掉絕大多數的物件（玩家、NPC、坐騎……）。
            if (obj.ObjectKind is not (ObjectKind.Treasure or ObjectKind.EventObj)) continue;

            // 已經開過的寶箱會變成不可選取，那正是「不必再走過去」的訊號。
            if (!obj.IsTargetable) continue;

            if (Vector3.DistanceSquared(playerPosition, obj.Position) > maxDistanceSquared) continue;

            var isChest = wantChests && ChestIdentity.IsChest(obj);
            var isCurrent = !isChest && wantCurrents && IsWantedAetherCurrent(obj);

            if (!isChest && !isCurrent) continue;

            // 🔴 對方的端點是 uint，而 GameObjectId 是 ulong。塞不下的話那一筆<b>無法</b>經由 IPC
            //    定址（對方會拿截斷後的值去比對，永遠對不上）⇒ 明確跳過並計數，
            //    讓「沒顯示」這件事在設定畫面上看得見，而不是靜靜地少一個點。
            var id = obj.GameObjectId;
            if (id > uint.MaxValue)
            {
                lastSkippedWideId++;
                continue;
            }

            var person = new MiniMappingwayPublisher.Person((uint)id, obj.Name.ToString());

            if (isChest)
                pendingChests.Add(person);
            else
                pendingCurrents.Add(person);
        }

        if (lastSkippedWideId > 0 && Throttle.Pass($"TCToolbox.{InternalName}.WideId", 60_000))
        {
            // 🔴 Information 級：這是「離線證不了、只有實機才知道會不會發生」的那一條，
            //    所以一定要讓使用者回報得出來。
            Svc.Log.Information(
                $"[{InternalName}] 有 {lastSkippedWideId} 個目標的 GameObjectId 超出 32 位元，"
                + "Mini-Mappingway 的 IPC 端點定址不到它們，這一輪略過（60 秒內只報一次）。");
        }
    }

    /// <summary>
    /// 這個物件是不是「我們想顯示的風脈泉」。
    /// </summary>
    /// <remarks>
    /// 📌 判準取自 Mappy 的 <c>MapRenderer.IsAetherCurrent</c>：<c>EventObj</c> ＋
    /// <c>EventHandler-&gt;Info.EventId.ContentId == AetherCurrent</c>。
    /// <b>不是</b>比對名字，也不是寫死一張 <c>EObj</c> 清單。
    /// <para>
    /// 🔴 原生解參考只在這個方法內發生，用的是<b>本幀</b>從物件表拿到的位址；每一跳都先判空。
    /// 不做 <c>try</c>/<c>catch</c>——AccessViolationException 在 .NET Core 是 corrupted-state
    /// exception，catch 不到，加了只是假的安全感。
    /// </para>
    /// </remarks>
    private static bool IsWantedAetherCurrent(IGameObject obj)
    {
        if (obj.ObjectKind != ObjectKind.EventObj) return false;

        var address = obj.Address;
        if (address == nint.Zero) return false;

        var handler = ((CSGameObject*)address)->EventHandler;
        if (handler == null) return false;

        var eventId = handler->Info.EventId;
        if (eventId.ContentId != EventHandlerContent.AetherCurrent) return false;

        if (!Config.OnlyUnattunedAetherCurrents) return true;

        return !IsResonated(eventId.Id);
    }

    /// <summary>
    /// 這個風脈泉共鳴了沒有。取不到資料時一律回 <see langword="false"/>（＝照樣顯示）。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>刻意不呼叫 <c>PlayerState.IsAetherCurrentUnlocked</c>，改自己走 <c>TryGet</c>。</b>
    /// 那支是 <c>UnlockedAetherCurrentsBitArray.Get(id - 0x2B0000)</c>，而 <c>Get</c> 對越界是
    /// <b>擲 <c>ArgumentOutOfRangeException</c></b>；而且它的上界檢查寫的是
    /// <c>ThrowIfGreaterThan(index, bitCount)</c> 不是 <c>…OrEqual</c>，
    /// <c>index == bitCount</c> 會通過檢查並讀到陣列後面那一個 byte。
    /// <c>TryGet</c> 用的是 <c>(uint)index &gt;= (uint)bitCount</c>，沒有這個問題。
    /// （這段判準與 <see cref="AetherCurrentTracker"/> 的 <c>IsResonated</c> 同源。）
    /// <para>
    /// 🔴 <c>PlayerState.Instance()</c> 由 CS 產生：解不出位址時擲 <c>InvalidOperationException</c>，
    /// 而登入前／切場景時也可能回 null —— 兩種失效模式並存，只擋一種等於假防護。
    /// </para>
    /// </remarks>
    private static bool IsResonated(uint aetherCurrentId)
    {
        PlayerState* playerState;
        try
        {
            playerState = PlayerState.Instance();
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        if (playerState == null) return false;

        var bits = playerState->UnlockedAetherCurrentsBitArray;
        var index = (long)aetherCurrentId - AetherCurrentIdBase;
        if (index < 0 || index >= bits.BitCount) return false;

        return bits.TryGet((int)index, out var value) && value;
    }

    // ── 模組列上的提示 ──────────────────────────────────────────────────────

    public override ModuleNotice? RowNotice
    {
        get
        {
            if (!IsEnabled) return null;

            return state switch
            {
                BridgeState.MiniMappingwayMissing => new ModuleNotice(
                    ModuleNoticeLevel.Unknown,
                    "Mini-Mappingway 未載入",
                    "找不到 Mini-Mappingway 的 IPC，所以標記沒有地方可以放。"
                    + "模組會靜靜地等它出現，不需要重開。"),

                BridgeState.VersionMismatch => new ModuleNotice(
                    ModuleNoticeLevel.Warning,
                    $"需要 Mini-Mappingway IPC {MiniMappingwayIpc.RequiredMajor}.{MiniMappingwayIpc.RequiredMinor} 以上",
                    $"對方回報 {lastMajor}.{lastMinor}。"
                    + "1.2 以前的版本對非玩家物件（寶箱、風脈泉）是完全靜默地不生效的："
                    + "它找物件時只掃物件表的玩家欄位，寶箱與風脈泉不在那一段。\n"
                    + "為了避免「開著卻什麼都沒有、也不知道為什麼」，同步已停下來。"),

                BridgeState.Unknown => new ModuleNotice(
                    ModuleNoticeLevel.Unknown,
                    "尚未同步",
                    "模組剛啟用，還沒完成第一次同步。"),

                BridgeState.Ok when lastSkippedWideId > 0 => new ModuleNotice(
                    ModuleNoticeLevel.Warning,
                    $"{lastSkippedWideId} 個目標定址不到",
                    "它們的 GameObjectId 超出 32 位元，而 Mini-Mappingway 的 IPC 端點是 uint。"),

                _ => null,
            };
        }
    }

    // ── 設定 UI ─────────────────────────────────────────────────────────────

    public override void DrawConfig()
    {
        DrawStatus();

        ImGui.Separator();

        var chests = Config.ShowChests;
        if (ImGui.Checkbox("顯示寶箱", ref chests))
        {
            Config.ShowChests = chests;
            Plugin.Instance.Config.Save();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "深宮的銅寶箱與一般副本的寶箱是 ObjectKind.Treasure；" + Environment.NewLine
                + "銀／金寶箱與擬態怪的箱子是 EventObj，靠遊戲自己的名稱表比對（程式碼裡沒有寫死的中文名）。"
                + Environment.NewLine + "已經開過的箱子會變成不可選取，那一刻就會從小地圖上消失。");
        }

        ImGui.SameLine();

        var currents = Config.ShowAetherCurrents;
        if (ImGui.Checkbox("顯示風脈泉", ref currents))
        {
            Config.ShowAetherCurrents = currents;
            Plugin.Instance.Config.Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("只認遊戲自己標成「風脈泉」的事件物件，不比對名字。");

        if (Config.ShowAetherCurrents)
        {
            var onlyUnattuned = Config.OnlyUnattunedAetherCurrents;
            if (ImGui.Checkbox("只顯示還沒共鳴的風脈泉", ref onlyUnattuned))
            {
                Config.OnlyUnattunedAetherCurrents = onlyUnattuned;
                Plugin.Instance.Config.Save();
            }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("預設開啟。關掉的話已經共鳴過的也會畫出來。");
        }

        ImGui.SetNextItemWidth(180f);
        var distance = Config.MaxDistance;
        if (ImGui.SliderFloat("搜尋距離（公尺）", ref distance, 5f, 200f, "%.0f"))
        {
            Config.MaxDistance = distance;
            Plugin.Instance.Config.Save();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "超出小地圖範圍的圓點會被 Mini-Mappingway 貼在小地圖邊緣，" + Environment.NewLine
                + "所以距離放大等於「邊緣會多一圈指向用的點」。嫌吵就調小。");
        }

        ImGui.SetNextItemWidth(180f);
        var refresh = Config.RefreshMilliseconds;
        if (ImGui.SliderInt("重新整理間隔（毫秒）", ref refresh, 100, 3000))
        {
            Config.RefreshMilliseconds = refresh;
            Plugin.Instance.Config.Save();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "這個間隔只影響「名單」多久更新一次（新箱子多久會冒出來）。" + Environment.NewLine
                + "圓點的位置是 Mini-Mappingway 自己每一幀重算的，跟這個值無關。");
        }

        ImGui.Spacing();
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);

        ImGui.TextDisabled(
            "顏色、圓點大小、外框與優先度請在 Mini-Mappingway 自己的設定裡調"
            + $"（來源名稱：「{ChestSource}」與「{AetherCurrentSource}」）。"
            + "本模組只在來源第一次出現時給一個初始顏色，之後不會覆蓋你調過的值。");

        ImGui.TextDisabled(
            "⚠️ Mini-Mappingway 在戰鬥中與 PvP 區域會整組隱藏標記，那是它本來的行為；戰鬥結束就會自己回來。");

        if (!string.IsNullOrEmpty(ChestIdentity.DegradedReason))
            ImGui.TextDisabled(ChestIdentity.DegradedReason);

        ImGui.PopTextWrapPos();
    }

    /// <summary>
    /// 設定畫面上的狀態列。
    /// </summary>
    /// <remarks>
    /// 📌 這裡只呼叫一支<b>無參數、零副作用</b>的版本查詢；數量一律用 <see cref="Sync"/> 快取下來的。
    /// </remarks>
    private void DrawStatus()
    {
        if (!IsEnabled && Throttle.Pass($"TCToolbox.{InternalName}.UiProbe", UiProbeIntervalMs))
        {
            if (!MiniMappingwayIpc.TryGetVersion(out var major, out var minor))
            {
                state = BridgeState.MiniMappingwayMissing;
            }
            else
            {
                lastMajor = major;
                lastMinor = minor;
                state = major != MiniMappingwayIpc.RequiredMajor || minor < MiniMappingwayIpc.RequiredMinor
                    ? BridgeState.VersionMismatch
                    : BridgeState.Unknown;
            }
        }

        switch (state)
        {
            case BridgeState.MiniMappingwayMissing:
                ImGui.TextDisabled("Mini-Mappingway 未載入——標記沒有地方可以放。");
                return;

            case BridgeState.VersionMismatch:
                ImGui.TextDisabled(
                    $"Mini-Mappingway 的 IPC 版本是 {lastMajor}.{lastMinor}，"
                    + $"本模組需要 {MiniMappingwayIpc.RequiredMajor}.{MiniMappingwayIpc.RequiredMinor} 以上。");
                return;

            case BridgeState.Unknown:
                ImGui.TextDisabled(IsEnabled ? "尚未完成第一次同步。" : "Mini-Mappingway 在，啟用模組後開始同步。");
                return;

            case BridgeState.Ok:
            default:
                break;
        }

        ImGui.TextUnformatted($"小地圖上：寶箱 {lastChestCount} 個、風脈泉 {lastCurrentCount} 個。");

        if (!ImGui.IsItemHovered()) return;

        var sb = new StringBuilder();
        sb.Append("最後同步：")
          .Append(lastSyncLocal == DateTime.MinValue
                      ? "？"
                      : lastSyncLocal.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
        sb.Append('\n').Append("Mini-Mappingway IPC：").Append(lastMajor).Append('.').Append(lastMinor);
        sb.Append('\n').Append("寶箱 EventObj 對照表：").Append(ChestIdentity.EventObjIds.Count).Append(" 筆");
        sb.Append('\n').Append("id 超出 32 位元而略過：").Append(lastSkippedWideId).Append(" 個");
        sb.Append('\n').Append("標記來源：").Append(ChestSource).Append('／').Append(AetherCurrentSource);

        ImGui.SetTooltip(sb.ToString());
    }
}

using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 旗標指令：<c>/tpflag</c> 傳送到離地圖旗標最近的乙太之光、<c>/gotoflag</c> 走到旗標位置。
/// </summary>
/// <remarks>
/// <para>
/// 🔴🔴 <b>絕不呼叫空參數的 <c>/li</c>。</b>那是 Lifestream 的跨世界傳送指令，
/// 空參數等於把角色送到別的伺服器去。傳送一律走
/// <see cref="ExternalNav.TryTeleport"/>（Lifestream 的 <c>Teleport</c> IPC，指定乙太之光編號），
/// 移動一律走 <see cref="ExternalNav.TryMoveTo"/>（vnavmesh 的 <c>PathfindAndMoveTo</c>）。
/// </para>
/// <para>
/// 📌 <b>旗標座標的真相</b>：<c>FlagMapMarker.XFloat</c>／<c>YFloat</c> 存的是<b>世界座標的 X 與 Z</b>，
/// 不是地圖介面上顯示的那組座標。依據有兩個：CS 的
/// <c>AgentMap.SetFlagMapMarker(territoryId, mapId, Vector3 worldPosition)</c> 直接把
/// <c>worldPosition.X</c>／<c>.Z</c> 傳進去；以及艦隊裡已出貨的 vnavmesh 自己
/// （<c>vnavmesh/MapUtils.cs</c>）就是這樣讀的。
/// <b>高度沒有存</b>，所以要走過去必須先問 vnavmesh 地板在哪
/// （從 Y=1024 往下打，做法同樣照抄 vnavmesh 自己）。
/// </para>
/// <para>
/// ⚠️ 兩個指令都是<b>一次性</b>的：下完就結束，不會排隊、不會等抵達、不會接後續動作。
/// 移動中隨時可以用 <c>/tcstop</c> 停下來。
/// </para>
/// </remarks>
public sealed unsafe class FlagCommands : TcModule
{
    public override string InternalName => "FlagCommands";

    public override string DisplayName => "旗標指令";

    public override string Description =>
        "/tpflag 傳送到離地圖旗標最近的乙太之光（需要 Lifestream）；/gotoflag 走到旗標位置（需要 vnavmesh）。" +
        "都是輸入才會動的一次性指令，移動中可用 /tcstop 停下。";

    public override ModuleCategory Category => ModuleCategory.Misc;

    /// <inheritdoc/>
    /// <remarks>開著但不輸入指令＝遊戲行為完全不變。</remarks>
    public override bool IsManualTrigger => true;

    public override bool HasConfigUI => true;

    private const string TeleportCommand = "/tpflag";
    private const string TeleportAlias = "/tpf";
    private const string GotoCommand = "/gotoflag";
    private const string GotoAlias = "/gtf";

    /// <summary>
    /// 問地板時的探測高度。
    /// </summary>
    /// <remarks>📌 1024 照抄 vnavmesh 自己的 <c>MapUtils.FlagToPoint</c>，要高過所有地形。</remarks>
    private const float FloorProbeHeight = 1024f;

    /// <summary>問地板時的水平搜尋半徑（公尺）。</summary>
    /// <remarks>旗標可能落在牆上或斜坡外緣，給一點餘裕才找得到旁邊的地板。</remarks>
    private const float FloorProbeHalfExtent = 5f;

    private FlagCommandsConfig Config => Plugin.Instance.Config.FlagCommands;

    /// <summary>
    /// 乙太之光世界座標的快取。
    /// </summary>
    /// <remarks>
    /// 📌 這是靜態遊戲資料，查一次就不會變。<b>連 <c>null</c>（查不到）也要快取</b>，
    /// 否則每次下指令都會對同一個查不到的水晶重掃一次 MapMarker 全表。
    /// </remarks>
    private readonly Dictionary<uint, Vector3?> positionCache = [];

    /// <summary>本次啟用期間有沒有真的用 <c>/tcgoto</c> 發起過移動。</summary>
    /// <remarks>
    /// 🔴 停用模組時要據此把自己發起的移動收掉：<c>OnDisable</c> 同時把
    /// <c>/tcstop</c> 登出（<see cref="NavStop.Release"/>），若不主動停下，
    /// 使用者會落到「角色繼續自動走、停止指令同時消失」的狀態。
    /// 📌 沒發起過就不呼叫：不要在拆卸路徑上對別的外掛做沒必要的 IPC
    /// （做法同 ClickToMove／FateTracker）。
    /// </remarks>
    private bool hasStartedNav;

    protected override void OnEnable()
    {
        positionCache.Clear();
        hasStartedNav = false;
        NavStop.Acquire();

        Svc.Commands.AddHandler(TeleportCommand, new CommandInfo(OnTeleportFlag)
        {
            HelpMessage = "傳送到離地圖旗標最近的乙太之光",
        });

        Svc.Commands.AddHandler(TeleportAlias, new CommandInfo(OnTeleportFlag)
        {
            HelpMessage = $"{TeleportCommand} 的簡寫",
            ShowInHelp = false,
        });

        Svc.Commands.AddHandler(GotoCommand, new CommandInfo(OnGotoFlag)
        {
            HelpMessage = "走到地圖旗標的位置（需要 vnavmesh）",
        });

        Svc.Commands.AddHandler(GotoAlias, new CommandInfo(OnGotoFlag)
        {
            HelpMessage = $"{GotoCommand} 的簡寫",
            ShowInHelp = false,
        });
    }

    protected override void OnDisable()
    {
        Svc.Commands.RemoveHandler(TeleportCommand);
        Svc.Commands.RemoveHandler(TeleportAlias);
        Svc.Commands.RemoveHandler(GotoCommand);
        Svc.Commands.RemoveHandler(GotoAlias);

        // 使用者在我們發起的移動還在跑的時候關掉模組——是我們讓他跑起來的，就由我們收掉。
        // 🔴 順序不能顛倒：Release 會把 /tcstop 登出，先停再放才不會留下
        //    「還在走、但停不了」的狀態。（補送窗口本身會活過 Release，見 NavStop.Release。）
        if (hasStartedNav)
            NavStop.RequestStop();

        NavStop.Release();
        hasStartedNav = false;
        positionCache.Clear();
    }

    /// <summary>目前的地圖旗標。</summary>
    /// <remarks>🔴 只回傳數值，不把 <c>AgentMap</c> 的指標留到下一幀。</remarks>
    private readonly record struct FlagInfo(uint TerritoryId, uint MapId, float WorldX, float WorldZ)
    {
        /// <summary>旗標的水平位置（高度未知，要另外問地板）。</summary>
        public Vector2 Horizontal => new(WorldX, WorldZ);
    }

    private static bool TryGetFlag(out FlagInfo flag, out string error)
    {
        flag = default;

        var agentMap = AgentMap.Instance();
        if (agentMap == null)
        {
            error = "讀不到地圖資料。";
            return false;
        }

        // 🔴 沒有這一行的話，沒設過旗標時讀到的是上一次的殘留（或全 0），
        //    表現是「莫名其妙傳送到某個地方」。
        if (agentMap->FlagMarkerCount == 0)
        {
            error = "你還沒有在地圖上設定旗標。";
            return false;
        }

        var marker = agentMap->FlagMapMarkers[0];
        flag = new FlagInfo(marker.TerritoryId, marker.MapId, marker.XFloat, marker.YFloat);
        error = string.Empty;
        return true;
    }

    // ── /tpflag ──────────────────────────────────────────────────────────────

    private void OnTeleportFlag(string command, string arguments)
    {
        if (!TryGetFlag(out var flag, out var flagError))
        {
            Svc.Chat.PrintError($"[TC Toolbox] {flagError}");
            return;
        }

        if (!ExternalNav.IsLifestreamAvailable())
        {
            Svc.Chat.PrintError("[TC Toolbox] 未偵測到 Lifestream 外掛，無法傳送。");
            return;
        }

        if (!TryFindNearestAetheryte(flag, out var aetheryteId, out var subIndex, out var name, out var distance))
        {
            Svc.Chat.PrintError(
                $"[TC Toolbox] 旗標所在的區域沒有你已解鎖的乙太之光（區域編號 {flag.TerritoryId}）。");
            return;
        }

        if (!ExternalNav.TryTeleport(aetheryteId, subIndex, out var accepted))
        {
            Svc.Chat.PrintError("[TC Toolbox] 呼叫 Lifestream 傳送失敗。");
            return;
        }

        if (!accepted)
        {
            Svc.Chat.PrintError($"[TC Toolbox] Lifestream 沒有接受這次傳送（可能正在忙，或該乙太之光無法使用）。");
            return;
        }

        // 使用者回報用的定錨點：證明這次傳送是他自己下的指令、目的地是哪一個。
        Svc.Log.Information(
            $"[{InternalName}] 使用者以 {command} 傳送 → 乙太之光 {aetheryteId}（{name}）" +
            $" subIndex={subIndex} 距旗標 {distance:F0}m");

        Svc.Chat.Print($"[TC Toolbox] 傳送到「{name}」（距旗標約 {distance:F0} 公尺）。");
    }

    /// <summary>
    /// 找旗標所在區域裡、離旗標最近的<b>已解鎖</b>乙太之光。
    /// </summary>
    /// <remarks>
    /// 📌 從 <see cref="Svc.AetheryteList"/> 出發而不是掃 <c>Aetheryte</c> 全表，
    /// 因為那份清單就是「這個角色真的能傳送到的地方」——對沒解鎖的水晶下傳送指令一定失敗。
    /// <para>
    /// 🔴 <b>解析不出座標的水晶要跳過，不能當成原點。</b>常見的寫法是查不到就回
    /// <c>Vector3.Zero</c> 再一起比距離——那會讓一個「查不到」的水晶在旗標靠近地圖原點時
    /// 贏過真正最近的那個，而且完全沒有徵兆。
    /// </para>
    /// <para>
    /// ⚠️ 距離只比水平面（X／Z）。旗標本來就沒有高度，硬湊一個 Y 只會讓比較結果更不準。
    /// </para>
    /// </remarks>
    private bool TryFindNearestAetheryte(
        in FlagInfo flag, out uint aetheryteId, out byte subIndex, out string name, out float distance)
    {
        aetheryteId = 0;
        subIndex = 0;
        name = string.Empty;
        distance = 0f;

        var best = float.MaxValue;
        var found = false;
        var target = flag.Horizontal;

        foreach (var entry in Svc.AetheryteList)
        {
            if (entry.TerritoryId != flag.TerritoryId) continue;

            var position = GetAetherytePosition(entry.AetheryteId);
            if (position == null) continue;

            var horizontal = new Vector2(position.Value.X, position.Value.Z);
            var d = Vector2.Distance(horizontal, target);
            if (d >= best) continue;

            best = d;
            aetheryteId = entry.AetheryteId;
            subIndex = entry.SubIndex;
            found = true;
        }

        if (!found) return false;

        distance = best;
        name = ResolveAetheryteName(aetheryteId);
        return true;
    }

    /// <summary>
    /// 乙太之光的世界座標。
    /// </summary>
    /// <remarks>
    /// 兩條路，依序試：
    /// <list type="number">
    /// <item><c>Aetheryte.Level[0]</c> 直接就是世界座標。台服 7.20 離線比對：108 個可傳送水晶
    /// <b>全部</b>有非零的 <c>Level[0]</c>。⚠️ 但 <c>exd-tc</c> 的 <c>Level</c> 匯出檔是殘缺的
    /// （這 108 個列號一個都不在裡面），所以「這條路真的解得開」<b>只能在實機驗證</b>。</item>
    /// <item><c>MapMarker</c>（<c>DataType==3</c>）的地圖像素座標換算回世界座標。
    /// 台服 7.20 離線比對：108 個裡有 107 個有標記，唯一沒有的是 row 1
    /// （<c>PlaceName</c> 為空、<c>Territory</c>＝1 的佔位列，不是真的目的地）。</item>
    /// </list>
    /// 兩條都不通就回 <c>null</c>，呼叫端會<b>跳過</b>這個水晶——絕不退化成 <c>Vector3.Zero</c>。
    /// </remarks>
    private Vector3? GetAetherytePosition(uint aetheryteId)
    {
        if (positionCache.TryGetValue(aetheryteId, out var cached)) return cached;

        Vector3? result = null;

        var aetheryte = Svc.Data.GetExcelSheet<Aetheryte>().GetRowOrDefault(aetheryteId);
        if (aetheryte != null)
        {
            // ① Level[0]：本身就是世界座標。
            var level = aetheryte.Value.Level[0].ValueNullable;
            if (level != null)
                result = new Vector3(level.Value.X, level.Value.Y, level.Value.Z);

            // ② 退路：地圖標記的像素座標換算。
            if (result == null)
                result = TryResolveViaMapMarker(aetheryte.Value);
        }

        if (result == null)
        {
            // Information 級：使用者跑 LogLevel 2。哪一個水晶解不出座標，只有這行說得出來。
            Svc.Log.Information(
                $"[{InternalName}] 解析不出乙太之光 {aetheryteId} 的座標，找最近的時候會跳過它。");
        }

        positionCache[aetheryteId] = result;
        return result;
    }

    /// <summary>用 <c>MapMarker</c> 的像素座標換算世界座標。</summary>
    /// <remarks>
    /// ⚠️ <c>MapMarker</c> 是<b>子列</b>表（<c>#</c> 欄長 <c>0.0</c> 這樣），
    /// 要用 <c>GetSubrowExcelSheet</c>，用一般的 <c>GetExcelSheet</c> 會拿不到東西。
    /// </remarks>
    private static Vector3? TryResolveViaMapMarker(in Aetheryte aetheryte)
    {
        var sheet = Svc.Data.GetSubrowExcelSheet<MapMarker>();
        if (sheet == null) return null;

        var map = aetheryte.Territory.ValueNullable?.Map.ValueNullable;
        if (map == null) return null;

        var aetheryteId = aetheryte.RowId;

        foreach (var subrows in sheet)
        {
            foreach (var marker in subrows)
            {
                if (marker.DataType != 3 || marker.DataKey.RowId != aetheryteId) continue;

                var x = PixelCoordToWorldCoord(marker.X, map.Value.SizeFactor, map.Value.OffsetX);
                var z = PixelCoordToWorldCoord(marker.Y, map.Value.SizeFactor, map.Value.OffsetY);

                // 🔴 地圖標記沒有高度資訊，只能給 0。呼叫端只比水平距離，所以這個 0 不會影響結果；
                //    但也因此這個回傳值**不可以**拿去當導航目的地。
                return new Vector3(x, 0f, z);
            }
        }

        return null;
    }

    /// <summary>
    /// 地圖像素座標 → 世界座標。
    /// </summary>
    /// <remarks>
    /// 公式與常數照抄 xivapi 的 MapCoordinates 文件與 Dalamud 的 <c>MapLinkPayload</c>
    /// （艦隊裡 ECommons 的 <c>Map.PixelCoordToWorldCoord</c> 也是同一份）。
    /// </remarks>
    private static float PixelCoordToWorldCoord(float coord, float scale, short offset)
    {
        const float factor = 2048.0f / (50 * 41);
        return (coord * factor - 1024f) / scale - offset * 0.001f;
    }

    private static string ResolveAetheryteName(uint aetheryteId)
    {
        var row = Svc.Data.GetExcelSheet<Aetheryte>().GetRowOrDefault(aetheryteId);
        var name = row?.PlaceName.ValueNullable?.Name.ExtractText() ?? string.Empty;
        return string.IsNullOrEmpty(name) ? $"#{aetheryteId}" : name;
    }

    // ── /gotoflag ────────────────────────────────────────────────────────────

    private void OnGotoFlag(string command, string arguments)
    {
        if (!TryGetFlag(out var flag, out var flagError))
        {
            Svc.Chat.PrintError($"[TC Toolbox] {flagError}");
            return;
        }

        // 🔴 旗標可以設在別張地圖上。那種情況下走是走不過去的——
        //    不擋的話會變成「朝著本區某個對應座標亂跑」。
        if (flag.TerritoryId != Svc.ClientState.TerritoryType)
        {
            Svc.Chat.PrintError(
                $"[TC Toolbox] 旗標不在目前的區域，無法走過去。先用 {TeleportCommand} 傳送過去。");
            return;
        }

        if (!ExternalNav.IsVnavmeshReady())
        {
            Svc.Chat.PrintError(
                ExternalNav.IsVnavmeshInstalled()
                    ? "[TC Toolbox] vnavmesh 的導航網格尚未就緒，請稍候再試。"
                    : "[TC Toolbox] 未偵測到 vnavmesh 外掛，無法自動移動。");
            return;
        }

        // 旗標只有 X／Z，高度要問 vnavmesh：從很高的地方往下找地板（做法同 vnavmesh 自己）。
        var probe = new Vector3(flag.WorldX, FloorProbeHeight, flag.WorldZ);
        if (!ExternalNav.TryFindPointOnFloor(probe, allowUnlandable: false, FloorProbeHalfExtent, out var destination))
        {
            Svc.Chat.PrintError("[TC Toolbox] 旗標的位置下面找不到可以站的地板，無法導航。");
            return;
        }

        // 前一趟就此作廢。vnavmesh 對「還在算路徑時又收到新請求」是直接拒絕的。
        if (ExternalNav.IsVnavmeshPathRunning() || ExternalNav.IsVnavmeshPathfindInProgress())
            NavStop.RequestStop();

        if (!ExternalNav.TryMoveTo(destination, Config.AllowFly, out var started, DisplayName))
        {
            Svc.Chat.PrintError("[TC Toolbox] 無法呼叫 vnavmesh，沒有開始移動。");
            return;
        }

        if (!started)
        {
            Svc.Chat.PrintError("[TC Toolbox] vnavmesh 拒絕了這次導航（多半是上一個路徑還在計算中），請稍候再試。");
            return;
        }

        // 走到這裡＝vnavmesh 真的收下了這趟移動，停用模組時要負責收掉。
        hasStartedNav = true;

        Svc.Log.Information(
            $"[{InternalName}] 使用者以 {command} 導航 → 旗標 {destination:F1}" +
            $"（區域 {flag.TerritoryId}）飛行={Config.AllowFly}");

        Svc.Chat.Print($"[TC Toolbox] 前往地圖旗標（{NavStop.Command} 可停下）。");
    }

    public override void DrawConfig()
    {
        ImGui.TextUnformatted($"{TeleportCommand}（或 {TeleportAlias}）：傳送到離旗標最近的乙太之光。");
        ImGui.TextDisabled("需要 Lifestream；只考慮你已經解鎖的乙太之光。");

        ImGui.TextUnformatted($"{GotoCommand}（或 {GotoAlias}）：走到旗標位置。");
        ImGui.TextDisabled("需要 vnavmesh；旗標必須在目前的區域內。");

        ImGui.Separator();

        var allowFly = Config.AllowFly;
        if (ImGui.Checkbox($"{GotoCommand} 允許飛行路徑##flagCommandsFly", ref allowFly))
        {
            Config.AllowFly = allowFly;
            Plugin.Instance.Config.Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("在還沒解鎖飛行的區域開這個，vnavmesh 會算不出路徑，\n表現是「指令下了沒反應」。");

        ImGui.Separator();

        // 外掛在不在，直接顯示在列上——「不知道」不必藏在 tooltip 裡。
        var lifestream = ExternalNav.IsLifestreamAvailable();
        var vnavInstalled = ExternalNav.IsVnavmeshInstalled();

        ImGui.TextColored(
            lifestream ? new Vector4(0.6f, 0.9f, 0.6f, 1f) : new Vector4(1f, 0.5f, 0.4f, 1f),
            lifestream ? "Lifestream：已偵測到" : "Lifestream：未偵測到（/tpflag 不能用）");

        ImGui.TextColored(
            vnavInstalled ? new Vector4(0.6f, 0.9f, 0.6f, 1f) : new Vector4(1f, 0.5f, 0.4f, 1f),
            vnavInstalled ? "vnavmesh：已偵測到" : "vnavmesh：未偵測到（/gotoflag 不能用）");

        ImGui.TextDisabled($"移動中可用 {NavStop.Command} 停下。");
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.Config;
using TCToolbox.Core;
using CSCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;

namespace TCToolbox.Modules;

// 本檔：面板裡的俯視小圖（鳥瞰圖）。
// - 玩家位置每 250 ms 在框架執行緒（Draw）重掃一次物件表、只抄值（座標、Battalion、敵對旗標）；繪製用這份快照。
// - 隊伍＝CharacterData.Battalion 相對自己那組的起始值（FrontlineTeamRules）；判不出就畫灰色並在列上標「?」。
// - 範圍、隊伍、領先、倍率全在 Core/FrontlineBirdsEye.cs（純類別，離線探針）。
public sealed partial class FrontlineObjectivePanel
{
    private const long PlayerSnapshotIntervalMs = 250;

    /// <summary>進場多久後才寫每場那行診斷：備戰階段大家都還在自己的基地，人數與分數都還沒意義。</summary>
    private const double SummaryDelaySeconds = 90;

    private readonly record struct PlayerDot(Vector2 Pos, byte Battalion, bool Hostile);

    private readonly List<PlayerDot> players = [];
    private readonly FrontlineViewArea viewArea = new();
    private readonly int[] scoreCopy = [-1, -1, -1];
    private long playersTakenAt = long.MinValue;
    private byte? selfBattalion;
    private FrontlineMap viewMap = FrontlineMap.None;
    private double matchSeenAt = -1;
    private bool loggedSummary;
    private bool loggedGrowth;

    private void ResetMatchView()
    {
        players.Clear();
        playersTakenAt = long.MinValue;
        selfBattalion = null;
        viewMap = FrontlineMap.None;
        matchSeenAt = -1;
        loggedSummary = false;
        loggedGrowth = false;
    }

    /// <summary>只在框架執行緒呼叫；物件表的包裝當場讀完就丟，不留到下一幀。</summary>
    private unsafe void RefreshPlayers()
    {
        var now = Environment.TickCount64;
        if (now - playersTakenAt < PlayerSnapshotIntervalMs)
            return;
        playersTakenAt = now;
        players.Clear();
        selfBattalion = null;

        var local = Svc.Objects.LocalPlayer;
        if (local == null || local.Address == nint.Zero)
            return;
        var localId = local.EntityId;
        selfBattalion = ((CSCharacter*)local.Address)->CharacterData.Battalion;

        foreach (var o in Svc.Objects.PlayerObjects)
        {
            var addr = o.Address;
            if (addr == nint.Zero || o.EntityId == localId || o.ObjectKind != ObjectKind.Player)
                continue;
            var ch = (CSCharacter*)addr;
            if (ch->CharacterData.Health == 0)
                continue;
            var pos = o.Position;
            players.Add(new PlayerDot(new Vector2(pos.X, pos.Z), ch->CharacterData.Battalion, ch->IsHostile));
        }
    }

    private void EnsureViewArea(FrontlineMap map, (Vector2 Min, Vector2 Max)? objectiveBounds)
    {
        if (viewMap == map)
            return;
        viewMap = map;
        viewArea.Reset(map, objectiveBounds);
    }

    private void DrawMiniMap(List<FrontlineObjectiveView> rows, Vector2? playerPos, float playerRot, uint territory, FrontlineMap map, int winScore)
    {
        if (!viewArea.Valid)
            return;

        if (playerPos is { } selfPos && viewArea.Include(selfPos) && !loggedGrowth)
        {
            loggedGrowth = true;
            Svc.Log.Information($"[{ModuleInternalName}] 鳥瞰圖：本場（{territory}）自己的位置 ({selfPos.X:0},{selfPos.Y:0}) 超出預設範圍，已擴大到 X {viewArea.Min.X:0}～{viewArea.Max.X:0}、Z {viewArea.Min.Y:0}～{viewArea.Max.Y:0}。本場不再重複記。");
        }

        var showPlayers = Config.ShowPlayers;
        if (showPlayers)
            RefreshPlayers();

        var battalionBase = selfBattalion is { } sb ? FrontlineTeamRules.BattalionBase(sb) : null;
        var selfTeam = selfBattalion is { } sb2 ? FrontlineTeamRules.TeamOf(sb2, battalionBase) : -1;
        var leaderMask = FrontlineTeamRules.LeaderMask(scoreCopy);

        if (showPlayers)
            DrawLegend(selfTeam, leaderMask, battalionBase);

        var size = UiScale.Px(220);
        var origin = ImGui.GetCursorScreenPos();
        ImGui.Dummy(new Vector2(size, size));
        var hover = ImGui.IsItemHovered();
        var dl = ImGui.GetWindowDrawList();
        var boxMax = origin + new Vector2(size, size);
        dl.AddRectFilled(origin, boxMax, ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.35f)));
        dl.AddRect(origin, boxMax, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.25f)));

        var (center, extent) = viewArea.View();
        var scale = size / extent;
        Vector2 ToScreen(Vector2 w) => origin + new Vector2(size * 0.5f) + ((w - center) * scale);

        var mouse = ImGui.GetMousePos();
        FrontlineObjectiveView? hovered = null;
        var radius = UiScale.Px(6);
        var outline = ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.85f));

        dl.PushClipRect(origin, boxMax, true);
        try
        {
            if (showPlayers)
            {
                var dot = UiScale.Px(3f);
                foreach (var p in players)
                {
                    var team = FrontlineTeamRules.TeamOf(p.Battalion, battalionBase);
                    var r = dot * FrontlineTeamRules.PlayerScale(team, selfTeam, leaderMask, Config.LeaderEnemyScale);
                    var (s, _) = FrontlineTeamRules.ClampToSquare(ToScreen(p.Pos), origin, boxMax, r + 1f);
                    dl.AddCircleFilled(s, r + 1f, outline);
                    dl.AddCircleFilled(s, r, ImGui.GetColorU32(team >= 0 ? TeamColors[team] : UnknownColor));
                }
            }

            foreach (var v in rows)
            {
                if (v.Position is not { } p)
                    continue;
                var s = ToScreen(p);
                if (v.Phase is FrontlineObjectivePhase.Idle)
                {
                    dl.AddCircleFilled(s, radius * 0.35f, ImGui.GetColorU32(WithAlpha(EndedColor, 0.6f)));
                    continue;
                }

                var r = radius * FrontlineTeamRules.ObjectiveScale(v.Phase, v.Owner, Config.NeutralObjectiveScale);
                DrawObjectiveMarker(dl, s, r, v, outline);

                if (hover && Vector2.DistanceSquared(mouse, s) <= (r + 3) * (r + 3))
                    hovered = v;
            }

            if (playerPos is { } me)
            {
                var r = UiScale.Px(7);
                var (s, _) = FrontlineTeamRules.ClampToSquare(ToScreen(me), origin, boxMax, r);
                var d = new Vector2(MathF.Sin(playerRot), MathF.Cos(playerRot));
                var n = new Vector2(-d.Y, d.X);
                var tip = s + (d * r);
                var a = s - (d * r * 0.6f) + (n * r * 0.55f);
                var b = s - (d * r * 0.6f) - (n * r * 0.55f);
                dl.AddTriangleFilled(tip, a, b, ImGui.GetColorU32(Vector4.One));
                dl.AddTriangle(tip, a, b, outline, 1.5f);
            }
        }
        finally
        {
            dl.PopClipRect();
        }

        if (hovered != null)
            ImGui.SetTooltip($"{hovered.Name}：{hovered.Status}\n{hovered.Detail}");

        if (showPlayers)
            MaybeLogSummary(territory, map, battalionBase, selfTeam, leaderMask, winScore);
    }

    /// <summary>圖上方一列：三隊顏色、我方、領先；判不出隊伍時列上直接是「?」。</summary>
    private void DrawLegend(int selfTeam, int leaderMask, int? battalionBase)
    {
        if (battalionBase == null)
        {
            ImGui.TextColored(UnknownColor, "隊伍 ?");
            UiHelperTooltip(selfBattalion == null
                ? "讀不到自己的資料，其他玩家先畫成灰色。"
                : $"自己的隊伍編號（{selfBattalion}）不在已知的三組裡，分不出誰是哪隊，其他玩家先畫成灰色。");
            return;
        }

        for (var t = 0; t < 3; t++)
        {
            if (t > 0)
                ImGui.SameLine();
            var mark = (t == selfTeam ? "（我方）" : string.Empty) + ((leaderMask & (1 << t)) != 0 ? "▲" : string.Empty);
            ImGui.TextColored(TeamColors[t], FrontlineObjectiveTracker.TeamName(t) + mark);
        }

        UiHelperTooltip("▲＝分數領先；領先的敵隊玩家點畫得稍大。白色箭頭是自己。\n只畫得到遊戲有載入的玩家（太遠的不會出現）；倒下的不畫。");
    }

    private void MaybeLogSummary(uint territory, FrontlineMap map, int? battalionBase, int selfTeam, int leaderMask, int winScore)
    {
        if (loggedSummary)
            return;
        var now = Now;
        if (matchSeenAt < 0)
            matchSeenAt = now;
        if (now - matchSeenAt < SummaryDelaySeconds || players.Count == 0)
            return;
        loggedSummary = true;

        var counts = new int[4];
        var mismatch = 0;
        var byBattalion = new SortedDictionary<byte, int>();
        foreach (var p in players)
        {
            var team = FrontlineTeamRules.TeamOf(p.Battalion, battalionBase);
            counts[team >= 0 ? team : 3]++;
            byBattalion[p.Battalion] = byBattalion.GetValueOrDefault(p.Battalion) + 1;
            if (selfBattalion is { } sb && (p.Battalion != sb) != p.Hostile)
                mismatch++;
        }

        var leaders = Enumerable.Range(0, 3).Where(t => (leaderMask & (1 << t)) != 0).Select(FrontlineObjectiveTracker.TeamName).ToList();
        var gc = Svc.PlayerState.GrandCompany.RowId;
        var gcFree = Svc.GameConfig.TryGet(UiConfigOption.PvPFrontlinesGCFree, out uint free) ? free.ToString() : "?";
        Svc.Log.Information(
            $"[{ModuleInternalName}] 鳥瞰圖：本場（{territory}，{map}）自己 Battalion {selfBattalion?.ToString() ?? "?"}、起始值 {battalionBase?.ToString() ?? "?"}（我方 {FrontlineObjectiveTracker.TeamName(selfTeam)}）；"
            + $"其他玩家 {players.Count} 人，Battalion 分布 {string.Join(" ", byBattalion.Select(kv => $"{kv.Key}:{kv.Value}"))}，與敵對旗標對不上 {mismatch} 人；"
            + $"各隊 黑渦團 {counts[0]}・雙蛇黨 {counts[1]}・不滅隊 {counts[2]}・不明 {counts[3]}；分數 {string.Join("/", scoreCopy)}（勝利 {winScore}），領先 {(leaders.Count > 0 ? string.Join("、", leaders) : "無")}；"
            + $"範圍 X {viewArea.Min.X:0}～{viewArea.Max.X:0}、Z {viewArea.Min.Y:0}～{viewArea.Max.Y:0}（擴大過：{(viewArea.Grown ? "是" : "否")}）；倍率 領先敵隊 {Config.LeaderEnemyScale:0.##}、中立 {Config.NeutralObjectiveScale:0.##}；"
            + $"大國防聯軍 {gc}、前線不限國防聯軍設定 {gcFree}。");
    }
}

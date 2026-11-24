using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Lumina.Excel.Sheets;
using TCToolbox.Core;

namespace TCToolbox.Modules;

// 本檔：把前線目標疊在遊戲小地圖（_NaviMap）上。
// - 每幀經 NaviMapReader 重讀 _NaviMap、只拿值；讀不到、不可見、數值不合理＝這幀不畫。
// - 換算全在 MinimapProjection（純函式，與 MiniMappingway 同源）；本檔只負責畫與自我對準紀錄。
// - 圓外的目標夾在圓邊畫成朝外的箭頭：前線目標多半在小地圖範圍外，「往哪走」正是要看的資訊。
public sealed partial class FrontlineObjectivePanel
{
    /// <summary>換算中心與地圖元件節點中心相差超過這麼多像素，就記一行「可能對不準」。</summary>
    private const float AnchorTolerancePx = 6f;

    /// <summary>讀不到小地圖要連續這麼久才記 log：剛進場、換區那幾幀讀不到是常態。</summary>
    private const double FailureLogDelaySeconds = 3;

    private uint minimapTerritory = uint.MaxValue;
    private bool zoneScaleResolved;
    private float? zoneScale;
    private bool minimapLoggedFirst;
    private bool minimapLoggedDrift;
    private bool minimapLoggedFailure;
    private double minimapFailingSince = -1;

    private static readonly string?[] SecondsLabels = new string?[181];
    private static readonly string?[] ApproxSecondsLabels = new string?[181];
    private static readonly string?[] PercentLabels = new string?[101];

    private void ResetMinimapMatchState(uint territory)
    {
        minimapTerritory = territory;
        zoneScaleResolved = false;
        zoneScale = null;
        minimapLoggedFirst = false;
        minimapLoggedDrift = false;
        minimapLoggedFailure = false;
        minimapFailingSince = -1;
    }

    private float? ZoneScale(uint territory)
    {
        if (!zoneScaleResolved || minimapTerritory != territory)
        {
            if (minimapTerritory != territory)
                ResetMinimapMatchState(territory);
            zoneScaleResolved = true;
            var sizeFactor = Svc.Data.GetExcelSheet<TerritoryType>().GetRowOrDefault(territory)?.Map.ValueNullable?.SizeFactor ?? 0;
            zoneScale = sizeFactor > 0 ? sizeFactor / 100f : null;
        }

        return zoneScale;
    }

    private void DrawMinimapOverlay(uint territory, List<FrontlineObjectiveView> rows, Vector2? playerPos, int events)
    {
        // 換區中、過場隱藏介面時本來就看不到小地圖，不算失敗。
        if (Svc.GameGui.GameUiHidden || Svc.Condition[ConditionFlag.BetweenAreas] || Svc.Condition[ConditionFlag.BetweenAreas51])
        {
            minimapFailingSince = -1;
            return;
        }

        if (ZoneScale(territory) is not { } zs)
        {
            NoteMinimapFailure(territory, "查不到這張圖的 Map.SizeFactor");
            return;
        }

        if (playerPos is not { } me)
        {
            NoteMinimapFailure(territory, "讀不到自己的位置");
            return;
        }

        if (!NaviMapReader.TryRead(out var reading, out var failure))
        {
            NoteMinimapFailure(territory, null, failure);
            return;
        }

        var state = new NaviMapState(reading.AddonPos, reading.AddonScale, reading.Zoom, reading.Rotation, reading.Locked, zs, ImGui.GetMainViewport().Pos);
        if (!MinimapProjection.IsSane(state))
        {
            NoteMinimapFailure(territory, "小地圖的縮放、位置或旋轉不在合理範圍", NaviMapReader.Failure.None, state);
            return;
        }

        minimapFailingSince = -1;
        var center = MinimapProjection.Center(state);
        var radius = MinimapProjection.Radius(state);
        var anchorDelta = (reading.MapNodeCenter + state.ViewportPos) - center;

        var drawn = 0;
        var clamped = 0;
        // 刻意不畫：連一筆事件都沒有時全是「?」，和面板一樣先不列。
        if (events > 0)
            DrawOverlayMarkers(rows, state, me, center, radius, ref drawn, ref clamped);

        if (!minimapLoggedFirst)
        {
            minimapLoggedFirst = true;
            var selfOffset = Vector2.Distance(MinimapProjection.Project(state, me, me), center);
            Svc.Log.Information(
                $"[{ModuleInternalName}] 小地圖疊加：本場（{territory}）第一次畫。_NaviMap 位置 ({reading.AddonPos.X:0},{reading.AddonPos.Y:0})、根節點 {reading.AddonSize.X:0}x{reading.AddonSize.Y:0}、縮放 {reading.AddonScale:0.###}；"
                + $"地圖縮放 {reading.Zoom:0.###}、旋轉 {reading.Rotation:0.###} 弧度、{(reading.Locked ? "鎖定北朝上" : "跟著鏡頭轉")}、區域比例 {zs:0.##}、視埠 ({state.ViewportPos.X:0},{state.ViewportPos.Y:0})。"
                + $"換算中心 ({center.X:0.0},{center.Y:0.0})、半徑 {radius:0.0}；地圖元件節點中心 ({reading.MapNodeCenter.X + state.ViewportPos.X:0.0},{reading.MapNodeCenter.Y + state.ViewportPos.Y:0.0})，"
                + $"差 ({anchorDelta.X:0.0},{anchorDelta.Y:0.0})＝{anchorDelta.Length():0.0} 像素（版面推算預期約 (0,{5 - (3 * reading.AddonScale):0.0})）；"
                + $"玩家換算後離中心 {selfOffset:0.0} 像素（換算以玩家為原點，恆為 0）；本幀事件 {events} 筆、畫 {drawn} 個目標（{clamped} 個在圓外夾邊）。");
        }

        if (!minimapLoggedDrift && anchorDelta.Length() > AnchorTolerancePx)
        {
            minimapLoggedDrift = true;
            Svc.Log.Information(
                $"[{ModuleInternalName}] 小地圖疊加：換算中心與地圖元件節點中心差 ({anchorDelta.X:0.0},{anchorDelta.Y:0.0})＝{anchorDelta.Length():0.0} 像素，超過 {AnchorTolerancePx:0} 像素，疊加可能對不準。"
                + $"縮放 {reading.AddonScale:0.###}、地圖縮放 {reading.Zoom:0.###}、{(reading.Locked ? "鎖定" : "未鎖定")}、_NaviMap 位置 ({reading.AddonPos.X:0},{reading.AddonPos.Y:0})。");
        }
    }

    private void NoteMinimapFailure(uint territory, string? reason, NaviMapReader.Failure failure = NaviMapReader.Failure.None, NaviMapState? state = null)
    {
        var now = Now;
        if (minimapFailingSince < 0)
        {
            minimapFailingSince = now;
            return;
        }

        if (minimapLoggedFailure || now - minimapFailingSince < FailureLogDelaySeconds)
            return;

        minimapLoggedFailure = true;
        var why = reason ?? NaviMapReader.Describe(failure);
        if (state is { } s)
            why += $"（縮放 {s.AddonScale}、地圖縮放 {s.Zoom}、旋轉 {s.Rotation}、位置 ({s.AddonPos.X},{s.AddonPos.Y})、區域比例 {s.ZoneScale}）";
        Svc.Log.Information($"[{ModuleInternalName}] 小地圖疊加：本場（{territory}）已連續 {now - minimapFailingSince:0} 秒沒畫，原因：{why}。本場不再重複記。");
    }

    private static void DrawOverlayMarkers(List<FrontlineObjectiveView> rows, in NaviMapState state, Vector2 me, Vector2 center, float radius, ref int drawn, ref int clamped)
    {
        var dl = ImGui.GetBackgroundDrawList();
        var markerRadius = 6.5f * state.AddonScale;
        var innerRadius = radius - markerRadius - 1f;
        if (innerRadius <= markerRadius)
            return;

        var outline = ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.85f));
        var fontPx = ImGui.GetFontSize() * 0.85f;
        var fontRatio = fontPx / ImGui.GetFontSize();

        // 外框只是保險：真正讓圖形不出圓的是上面的夾邊與下面的文字框檢查。
        dl.PushClipRect(center - new Vector2(radius), center + new Vector2(radius), true);
        try
        {
            foreach (var v in rows)
            {
                if (v.Position is not { } pos || v.Phase == FrontlineObjectivePhase.Idle)
                    continue;

                var (p, wasClamped) = MinimapProjection.ClampToCircle(center, innerRadius, MinimapProjection.Project(state, me, pos));
                var alpha = v.Confirmed ? 1f : PossibleAlpha;
                if (wasClamped)
                {
                    // 刻意不畫：已結束的不必再走過去；不知道狀態的箭頭沒有可行動的資訊，只會把圓邊塞滿。
                    if (v.Phase is FrontlineObjectivePhase.Ended or FrontlineObjectivePhase.Unknown)
                        continue;
                    DrawEdgeArrow(dl, center, p, markerRadius, ImGui.GetColorU32(WithAlpha(PhaseColor(v), alpha)), outline);
                    clamped++;
                }
                else
                {
                    DrawObjectiveMarker(dl, p, markerRadius, v, outline);
                }

                drawn++;
                if (ShortLabel(v) is { } label)
                    DrawLabel(dl, center, radius, p, markerRadius, wasClamped, label, fontPx, fontRatio, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, alpha)));
            }
        }
        finally
        {
            dl.PopClipRect();
        }
    }

    private static void DrawEdgeArrow(ImDrawListPtr dl, Vector2 center, Vector2 p, float size, uint color, uint outline)
    {
        var d = Vector2.Normalize(p - center);
        var n = new Vector2(-d.Y, d.X);
        var tip = p + (d * size);
        var a = p - (d * size * 0.6f) + (n * size * 0.7f);
        var b = p - (d * size * 0.6f) - (n * size * 0.7f);
        dl.AddTriangleFilled(tip, a, b, color);
        dl.AddTriangle(tip, a, b, outline, 1.5f);
    }

    /// <summary>文字放在圖示下方，放不進圓就換上方；夾邊的放在箭頭往圓心那側。都放不進就不畫。</summary>
    private static void DrawLabel(ImDrawListPtr dl, Vector2 center, float radius, Vector2 p, float markerRadius, bool clamped, string label, float fontPx, float fontRatio, uint color)
    {
        var size = ImGui.CalcTextSize(label) * fontRatio;
        Span<Vector2> candidates = stackalloc Vector2[2];
        if (clamped)
        {
            var inward = Vector2.Normalize(center - p);
            var reach = markerRadius + (MathF.Max(size.X, size.Y) * 0.5f) + 1f;
            candidates[0] = p + (inward * reach) - (size * 0.5f);
            candidates[1] = candidates[0];
        }
        else
        {
            candidates[0] = p + new Vector2(-size.X * 0.5f, markerRadius + 1f);
            candidates[1] = p + new Vector2(-size.X * 0.5f, -markerRadius - 1f - size.Y);
        }

        foreach (var origin in candidates)
        {
            if (!MinimapProjection.RectInsideCircle(center, radius, origin, origin + size))
                continue;
            OverlayFont.DrawOutlined(dl, fontPx, origin, color, label);
            return;
        }
    }

    /// <summary>圖示旁的短字：倒數（推估的加「約」）、沒有倒數時的剩餘百分比；不知道＝「?」。</summary>
    private static string? ShortLabel(FrontlineObjectiveView v)
    {
        if (v.Phase is FrontlineObjectivePhase.Idle or FrontlineObjectivePhase.Ended)
            return null;
        if (v.Phase == FrontlineObjectivePhase.Unknown)
            return "?";
        if (v.SecondsLeft is { } s)
        {
            var n = (int)Math.Ceiling(Math.Max(0, s));
            return v.Estimated
                ? Cached(ApproxSecondsLabels, n, static k => $"約{k}秒")
                : Cached(SecondsLabels, n, static k => $"{k}秒");
        }

        if (v.Fraction is { } f && v.Phase is FrontlineObjectivePhase.Active or FrontlineObjectivePhase.Available)
            return Cached(PercentLabels, (int)MathF.Round(Math.Clamp(f, 0f, 1f) * 100), static k => $"{k}%");
        return null;
    }

    private static string Cached(string?[] cache, int n, Func<int, string> make)
    {
        if ((uint)n >= (uint)cache.Length)
            return make(n);
        return cache[n] ??= make(n);
    }
}

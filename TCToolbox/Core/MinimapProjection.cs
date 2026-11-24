using System;
using System.Numerics;

namespace TCToolbox.Core;

// 本檔假設與契約：
// - 純受管理、不碰原生記憶體也不碰 ImGui：呼叫端每幀把讀到的 _NaviMap 數值抄成 NaviMapState 傳進來；離線探針直接連結本檔。
// - 換算與 MiniMappingway（MIT，Copyright (c) 2022 jaycewhite）同源，跨 repo 同步：Utility/MarkerUtility.cs 的 RunChecks（218、0.315）、
//   PrepareDrawOnMinimap（中心、-5）、CalculateCirclePosition（三個比例相乘、未鎖定才旋轉、夾在圓邊）、RotateForMiniMap。
// - 世界座標一律 (X, Z)：X 往東＝螢幕右、Z 往南＝螢幕下（北朝上時）。

/// <param name="AddonPos">_NaviMap 左上角（遊戲畫面像素，未加視埠位移）。</param>
/// <param name="AddonScale">_NaviMap 的 Scale（介面大小設定）。</param>
/// <param name="Zoom">地圖元件（節點 18）裡圖片節點 6 的 ScaleX＝小地圖縮放。</param>
/// <param name="Rotation">節點 8 的 Rotation（弧度）；只在未鎖定時套用。</param>
/// <param name="Locked">節點 4 的勾選框：鎖定＝北朝上。</param>
/// <param name="ZoneScale">Map.SizeFactor / 100。</param>
/// <param name="ViewportPos">ImGui 主視埠左上角。</param>
public readonly record struct NaviMapState(
    Vector2 AddonPos,
    float AddonScale,
    float Zoom,
    float Rotation,
    bool Locked,
    float ZoneScale,
    Vector2 ViewportPos);

public static class MinimapProjection
{
    /// <summary>_NaviMap 根節點的邊長（ULD 節點 1：218×218）。</summary>
    public const float NaviMapSize = 218f;

    /// <summary>可畫圓半徑＝邊長 × 此值（MiniMappingway 的經驗值，比地圖圖片的 88 小，留出外框）。</summary>
    public const float RadiusFactor = 0.315f;

    /// <summary>中心往上挪的像素（MiniMappingway 寫死、不乘縮放）。</summary>
    public const float PivotYOffset = 5f;

    // 合理範圍以外一律當成讀錯（不畫）：介面大小設定最多到 200%、小地圖縮放實際落在 0.x～數倍。
    public const float MinAddonScale = 0.25f;
    public const float MaxAddonScale = 6f;
    public const float MinZoom = 0.05f;
    public const float MaxZoom = 20f;

    public static bool IsSane(in NaviMapState s) =>
        float.IsFinite(s.AddonPos.X) && float.IsFinite(s.AddonPos.Y)
        && float.IsFinite(s.ViewportPos.X) && float.IsFinite(s.ViewportPos.Y)
        && s.AddonScale is >= MinAddonScale and <= MaxAddonScale
        && s.Zoom is >= MinZoom and <= MaxZoom
        && s.ZoneScale is > 0f and <= 10f
        && float.IsFinite(s.Rotation) && MathF.Abs(s.Rotation) <= 8 * MathF.PI;

    public static Vector2 Center(in NaviMapState s)
    {
        var half = NaviMapSize * s.AddonScale / 2;
        return s.ViewportPos + s.AddonPos + new Vector2(half, half - PivotYOffset);
    }

    public static float Radius(in NaviMapState s) => NaviMapSize * s.AddonScale * RadiusFactor;

    /// <summary>世界座標換成螢幕座標（不夾圓）；玩家自己永遠落在 <see cref="Center"/>。</summary>
    public static Vector2 Project(in NaviMapState s, Vector2 playerXZ, Vector2 targetXZ)
    {
        var center = Center(s);
        var p = center + ((targetXZ - playerXZ) * (s.ZoneScale * s.AddonScale * s.Zoom));
        return s.Locked ? p : Rotate(center, p, s.Rotation);
    }

    public static Vector2 Rotate(Vector2 center, Vector2 p, float angle)
    {
        var (sin, cos) = MathF.SinCos(angle);
        var d = p - center;
        return center + new Vector2((cos * d.X) - (sin * d.Y), (sin * d.X) + (cos * d.Y));
    }

    /// <summary>夾進以 <paramref name="center"/> 為心、<paramref name="radius"/> 為半徑的圓；回傳是否被夾。</summary>
    public static (Vector2 Pos, bool Clamped) ClampToCircle(Vector2 center, float radius, Vector2 p)
    {
        var d = p - center;
        var len = d.Length();
        if (len <= radius || len <= 0f)
            return (p, false);
        return (center + (d * (radius / len)), true);
    }

    /// <summary>矩形四角都在圓內（文字框放得下才畫，避免蓋到小地圖外的介面）。</summary>
    public static bool RectInsideCircle(Vector2 center, float radius, Vector2 min, Vector2 max)
    {
        var r2 = radius * radius;
        return Vector2.DistanceSquared(center, min) <= r2
            && Vector2.DistanceSquared(center, max) <= r2
            && Vector2.DistanceSquared(center, new Vector2(min.X, max.Y)) <= r2
            && Vector2.DistanceSquared(center, new Vector2(max.X, min.Y)) <= r2;
    }
}

using System.Numerics;
using Dalamud.Utility;
using Lumina.Excel.Sheets;

namespace TCToolbox.Core;

/// <summary>
/// 世界座標 → 地圖座標（遊戲介面上顯示的那個 X/Y）的換算。
/// </summary>
/// <remarks>
/// 🔴 <b>刻意不自己寫公式，一律轉呼叫 Dalamud 的 <see cref="MapUtil"/>。</b>
/// 🔴 <b>offset 的正負號有兩套慣例，用錯的失敗形式是「標記靜靜地落在偏掉的位置」。</b>
/// Lumina <c>Map.OffsetX</c> 與 <c>AgentMap.SelectedOffsetX</c> <b>符號相反</b>
/// 所以直接把 <c>Map.OffsetX</c>／<c>OffsetY</c> 原值傳進去，不加負號。
/// </remarks>
internal static class MapCoords
{
    /// <summary>
    /// 把世界座標換成某張地圖上的地圖座標。
    /// </summary>
    /// <param name="mapId"><c>Map</c> 表的列號。</param>
    /// <param name="worldPosition">世界座標（<b>用 X 與 Z，Y 是高度不參與換算</b>）。</param>
    /// <param name="mapCoordinates">換算結果。</param>
    /// <returns>
    /// 換不出來時回 <see langword="false"/>，<b>而且不會給一個看起來很正常的 0</b>——
    /// 呼叫端應該把這一筆整個略過，不是拿 <c>(0, 0)</c> 去畫。
    /// </returns>
    public static bool TryWorldToMap(uint mapId, Vector3 worldPosition, out Vector2 mapCoordinates)
    {
        mapCoordinates = default;

        if (mapId == 0) return false;
        if (!float.IsFinite(worldPosition.X) || !float.IsFinite(worldPosition.Z)) return false;

        var map = Svc.Data.GetExcelSheet<Map>().GetRowOrDefault(mapId);
        if (map == null) return false;

        // scale 進到公式裡是分母（2048 / scale），0 會算出無限大。
        var scale = map.Value.SizeFactor;
        if (scale == 0) return false;

        var x = MapUtil.ConvertWorldCoordXZToMapCoord(worldPosition.X, scale, map.Value.OffsetX);
        var y = MapUtil.ConvertWorldCoordXZToMapCoord(worldPosition.Z, scale, map.Value.OffsetY);

        if (!float.IsFinite(x) || !float.IsFinite(y)) return false;

        mapCoordinates = new Vector2(x, y);
        return true;
    }

    /// <summary>
    /// 把 <b>Hunt Helper 算出來的地圖座標</b>換回世界座標的 X／Z。
    /// </summary>
    /// <param name="territoryId">那隻怪所在的 <c>TerritoryType</c> 列號。</param>
    /// <param name="mapCoordinates">Hunt Helper <c>MobRecord.Position</c> 的值。</param>
    /// <param name="worldX">換回來的世界座標 X。</param>
    /// <param name="worldZ">換回來的世界座標 Z。</param>
    /// <remarks>
    /// 🔴🔴 <b>這支刻意<i>不</i>用 <see cref="TryWorldToMap"/> 的反函數，而是逐字反轉
    /// Hunt Helper 自己那條公式。</b>它的
    /// <c>MapHelpers.ConvertToMapCoordinate</c> 是 <c>2048/scale + pos/50 + 1</c>——
    /// <b>沒有 offset 那一項</b>，與 Dalamud <c>MapUtil</c> 那條差一個位移。
    /// </remarks>
    /// <returns>換不出來時回 <see langword="false"/>（<b>不會給一個看起來很正常的 0</b>）。</returns>
    public static bool TryHuntHelperMapToWorld(
        uint territoryId, Vector2 mapCoordinates, out float worldX, out float worldZ)
    {
        worldX = 0f;
        worldZ = 0f;

        if (!float.IsFinite(mapCoordinates.X) || !float.IsFinite(mapCoordinates.Y)) return false;

        var scale = HuntHelperZoneScale(territoryId);
        var offset = 2048f / scale;

        worldX = (mapCoordinates.X - 1f - offset) * 50f;
        worldZ = (mapCoordinates.Y - 1f - offset) * 50f;

        return float.IsFinite(worldX) && float.IsFinite(worldZ);
    }

    /// <summary>Hunt Helper 的 <c>GetMapZoneScale</c>，逐字照抄（理由見上）。</summary>
    /// <remarks>
    /// ⚠️ 對方的參數名寫的是 <c>mapID</c>，但唯一的呼叫點傳的是 <b>territory id</b>
    /// （<c>MapUI.cs</c> 的 <c>_mapZoneScale = _huntManager.GetMapZoneScale(_territoryId)</c>）。
    /// 這裡照它<b>實際被呼叫的樣子</b>接參數，不照它的參數名——名字錯了不影響來回的正確性，
    /// 但如果「好心」改成傳 map id，反而會讓那六張圖的還原偏掉約 54 碼。
    /// </remarks>
    private static float HuntHelperZoneScale(uint territoryId) =>
        territoryId is >= 397 and <= 402 ? 95f : 100f;
}

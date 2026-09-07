using System.Numerics;
using Dalamud.Utility;
using Lumina.Excel.Sheets;

namespace TCToolbox.Core;

/// <summary>
/// 世界座標 → 地圖座標（遊戲介面上顯示的那個 X/Y）的換算。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 <b>刻意不自己寫公式，一律轉呼叫 Dalamud 的 <see cref="MapUtil"/>。</b>
/// 那支的常數是從遊戲本體反組譯出來的（<c>MapUtil</c> 檔頭寫著來源特徵碼），
/// 而艦隊裡至少有三份長得不一樣、彼此不相容的「地圖座標公式」：
/// </para>
/// <list type="bullet">
/// <item>Dalamud <c>MapUtil.ConvertWorldCoordXZToMapCoord</c>：
/// <c>0.02*offset + 2048/scale + 0.02*value + 1</c>（本檔採用）。</item>
/// <item>HuntHelper <c>MapHelpers.ConvertToMapCoordinate</c>：<c>2048/scale + pos/50 + 1</c>
/// ——與上面同一條，只是<b>沒有 offset 那一項</b>。</item>
/// <item>ECommons <c>Map.PixelCoordToWorldCoord</c>（<see cref="Modules.FlagCommands"/> 抄了一份）：
/// 那支的輸入是 <c>MapMarker</c> 的<b>貼圖像素座標</b>，不是世界座標，
/// 而且它的 <c>offset * 0.001f</c> 與 Lumina <c>Map.OffsetX</c> 的單位對不起來。
/// <b>不要拿它來做世界座標的換算。</b></item>
/// </list>
/// <para>
/// 🔴 <b>offset 的正負號有兩套慣例，用錯的失敗形式是「標記靜靜地落在偏掉的位置」。</b>
/// Lumina <c>Map.OffsetX</c> 與 <c>AgentMap.SelectedOffsetX</c> <b>符號相反</b>
/// （Dalamud <c>MapUtil.GetMapCoordinates</c> 逐字寫著這件事，連上游 issue 編號都附了，
/// 呼叫時傳的是 <c>-agentMap-&gt;CurrentOffsetX</c>）。本檔吃的是 <b>Lumina 那一套</b>，
/// 所以直接把 <c>Map.OffsetX</c>／<c>OffsetY</c> 原值傳進去，不加負號。
/// </para>
/// <para>
/// ✅ 與 Mappy 端對得起來：Mappy 畫 IPC 標記時做的是
/// <c>(mapCoord - 1) * SelectedMapSizeFactorFloat * 2048 / 41</c>
/// （<c>MapRenderer.Ipc.cs</c>），那正是本公式的反函數；而它畫旗標／遊戲物件時
/// 用的是 <c>(world - SelectedOffset) * c + 1024</c>，代入上面那條符號關係之後
/// 與本公式同一條。⚠️ 兩邊的常數有微小差異（Dalamud 用 0.02 與 2048，
/// 幾何上的精確值是 41/2048 與 2050）——換算下來不到 0.02 個地圖格，
/// 一顆圖示的大小遠大於它，不影響顯示。
/// </para>
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
    /// <para>
    /// 🔴🔴 <b>這支刻意<i>不</i>用 <see cref="TryWorldToMap"/> 的反函數，而是逐字反轉
    /// Hunt Helper 自己那條公式。</b>它的
    /// <c>MapHelpers.ConvertToMapCoordinate</c> 是 <c>2048/scale + pos/50 + 1</c>——
    /// <b>沒有 offset 那一項</b>，與 Dalamud <c>MapUtil</c> 那條差一個位移。
    /// </para>
    /// <para>
    /// 🔑 <b>為什麼「用它自己的公式」才是對的</b>：我們要的不是「這個地圖座標在遊戲裡對應哪個世界座標」，
    /// 而是「Hunt Helper 當初是從哪個世界座標算出這個值的」。那是一次<b>來回</b>：
    /// scale 那一項只是個加法常數，用<b>同一個常數</b>減回去就<b>逐位元</b>還原出原始的
    /// <c>mob.Position.X／Z</c>（<c>TrainManager.AddMob</c> 直接餵 <c>IBattleNpc.Position</c>）。
    /// 換成別條公式反而會引入一個 offset 的誤差，而失敗形式是<b>角色被帶到那張圖上不相干的地方</b>，
    /// 沒有任何錯誤訊息。
    /// </para>
    /// <para>
    /// 📌 <b>scale 也照抄它的規則</b>：<c>HuntManager.GetMapZoneScale</c> 是
    /// 「territory 落在 397~402（蒼天伊修加爾德六張野外圖）回 95，其餘回 100」。
    /// ✅ 2026-09-08 以台服 EXD dump 交叉驗證：TerritoryType 397~402 → Map 211~216，
    /// <c>SizeFactor</c> 全部是 95，而全表 SizeFactor 為 95 的<b>恰好只有這 6 張</b>
    /// ⇒ 對狩獵區而言這條規則與 <c>Map.SizeFactor</c> 完全一致。
    /// ⚠️ 但真正的正確性論證是上面那條「來回」，不是這個一致性——就算哪天不一致了，
    /// 照抄它的規則仍然還原得出原始座標。
    /// </para>
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

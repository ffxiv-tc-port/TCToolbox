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
}

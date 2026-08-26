using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace TCToolbox.Core;

/// <summary>
/// 把一組「想要出現在 Mappy 上的標記」增量同步到 Mappy：只加新的、只刪不要的，
/// 內容沒變的原地不動。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 <b>存在的理由是「清空重放」會洗記錄。</b>Mappy 的 <c>ClearSource</c> 把整個來源從它的表裡
/// 拿掉，於是下一次 <c>AddMarker</c> 會被判定成「新的標記來源」而寫一行 <c>Information</c>
/// （<c>MarkerIpcController.AddMarker</c> 逐字如此）。每分鐘保險重推一次、又有好幾個來源，
/// 使用者的記錄檔就會被這些沒有資訊量的行淹掉。
/// ⇒ 這裡改成用 <c>RemoveMarker</c> 逐筆增量，<c>ClearSource</c> 只留給<b>模組停用／卸載</b>。
/// </para>
/// <para>
/// 🔑 <b>比對的單位是「鍵 ＋ 內容簽章」。</b>Mappy 的標記建立之後不可變，所以「改一筆」
/// 只能是<b>先刪再加</b>；靠鍵才知道要刪哪一個 handle。鍵由呼叫端給
/// （風脈泉用 <c>AetherCurrent</c> 列號、狩獵列車用 怪 id＋分區），要求是<b>同一個東西跨次同鍵</b>。
/// </para>
/// <para>
/// 🔴 <b><c>AddMarker</c> 回 0 是「被拒絕」不是例外</b>（來源／地圖／圖示為 0、座標不是有限數、
/// 或超出 32 來源 × 512 筆的上限）。被拒絕的那一筆<b>不會進追蹤表</b>，
/// 下一次同步會再試一次；<see cref="LastRejected"/> 讓呼叫端把這個數字顯示出來。
/// </para>
/// <para>
/// ⚠️ <b>Mappy 可能在我們沒看見的時候被重新載入</b>——那時它的表是空的，而我們的追蹤表還記得
/// 一堆 handle，於是「標記再也不出現，而且完全沒有徵兆」。呼叫端偵測到 Mappy 從「不在」變成
/// 「在」時要呼叫 <see cref="Forget"/>，另外定期用 <c>refreshAll</c> 全量重推一次當保險。
/// </para>
/// <para>
/// 📌 全部在框架執行緒上呼叫，沒有鎖。
/// </para>
/// </remarks>
internal sealed class MappyMarkerPublisher
{
    /// <summary>一筆想要出現在地圖上的標記。</summary>
    /// <param name="Key">
    /// 跨次穩定的識別字串（同一個東西每次都要給同一個鍵）。空字串會被略過。
    /// </param>
    /// <param name="MapId"><c>Map</c> 表列號。0 會被 Mappy 拒絕。</param>
    /// <param name="MapCoordinates"><b>地圖座標</b>（介面上顯示的 X/Y），不是世界座標。</param>
    /// <param name="IconId">遊戲圖示 id。0 會被 Mappy 拒絕。</param>
    /// <param name="Tooltip">提示文字；超過 256 字由 Mappy 端自己截掉。</param>
    public readonly record struct Marker(
        string Key, uint MapId, Vector2 MapCoordinates, uint IconId, string Tooltip);

    private readonly record struct Entry(uint Handle, string Signature);

    private readonly Dictionary<string, Entry> live = [];

    private readonly HashSet<string> seen = [];

    public MappyMarkerPublisher(string source) => Source = source;

    /// <summary>
    /// 標記來源字串。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>不要改既有模組的來源字串。</b>Mappy 端拿它當鍵記住「這個來源要不要顯示」
    /// （<c>SystemConfig.IpcSourceEnabled</c>），改了之後使用者原本關掉的來源會變成一個
    /// 永遠留在 Mappy 設定裡的孤兒，而新來源會以「開」的狀態冒出來。
    /// </remarks>
    public string Source { get; }

    /// <summary>目前確實在 Mappy 上的標記筆數。</summary>
    public int Placed => live.Count;

    /// <summary>最後一次同步時被 Mappy 拒絕（<c>AddMarker</c> 回 0）的筆數。</summary>
    public int LastRejected { get; private set; }

    /// <summary>最後一次同步時因為鍵重複或空白而被略過的筆數（正常應為 0）。</summary>
    public int LastDuplicateKeys { get; private set; }

    /// <summary>最後一次同步實際打了幾次 <c>AddMarker</c>／<c>RemoveMarker</c>。</summary>
    public int LastIpcCalls { get; private set; }

    /// <summary>把目前的狀態同步成 <paramref name="desired"/>。</summary>
    /// <param name="desired">這一刻想要出現在地圖上的全部標記。</param>
    /// <param name="refreshAll">
    /// <see langword="true"/>＝即使內容沒變也整組刪掉重加（Mappy 可能被重載過的保險）。
    /// </param>
    public void Publish(IReadOnlyList<Marker> desired, bool refreshAll = false)
    {
        LastRejected = 0;
        LastDuplicateKeys = 0;
        LastIpcCalls = 0;

        seen.Clear();

        foreach (var marker in desired)
        {
            // 鍵重複的話後面那一筆會把前面那一筆的 handle 蓋掉 ⇒ 蓋掉的那個永遠刪不掉。
            // 寧可略過並計數，也不要製造刪不掉的幽靈標記。
            if (string.IsNullOrEmpty(marker.Key) || !seen.Add(marker.Key))
            {
                LastDuplicateKeys++;
                continue;
            }

            var signature = BuildSignature(marker);

            if (!refreshAll && live.TryGetValue(marker.Key, out var existing) && existing.Signature == signature)
                continue;

            // 內容變了（或強制重推）：Mappy 的標記不可變，只能先刪再加。
            if (live.Remove(marker.Key, out var stale))
            {
                MappyMarkerIpc.RemoveMarker(Source, stale.Handle);
                LastIpcCalls++;
            }

            var handle = MappyMarkerIpc.AddMarker(
                Source, marker.MapId, marker.MapCoordinates, marker.IconId, marker.Tooltip);
            LastIpcCalls++;

            // 🔴 回 0＝被拒絕。不記進追蹤表（記了就會有一個刪不掉的假 handle），
            //    下一次同步會自己再試一次。
            if (handle == 0)
            {
                LastRejected++;
                continue;
            }

            live[marker.Key] = new Entry(handle, signature);
        }

        RemoveUnwanted();
    }

    /// <summary>
    /// 清掉這個來源在 Mappy 上的全部標記並忘掉追蹤表。<b>只在模組停用／卸載時用</b>
    /// （理由見 <see cref="MappyMarkerIpc.RemoveMarker"/>）。
    /// </summary>
    public void Clear()
    {
        MappyMarkerIpc.ClearSource(Source);
        live.Clear();
        LastRejected = 0;
        LastDuplicateKeys = 0;
        LastIpcCalls = 0;
    }

    /// <summary>
    /// 只忘掉追蹤表，<b>不打任何 IPC</b>。
    /// </summary>
    /// <remarks>
    /// 用在「Mappy 不見了」或「Mappy 從不在變成在」這兩個狀態轉換上：那時候手上的 handle
    /// 全部已經失效，拿去 <c>RemoveMarker</c> 只會得到 <see langword="false"/>，
    /// 而留著它們會讓下一次同步以為「已經放上去了」——表現成標記再也不出現，且毫無徵兆。
    /// </remarks>
    public void Forget()
    {
        live.Clear();
        LastRejected = 0;
        LastDuplicateKeys = 0;
        LastIpcCalls = 0;
    }

    private void RemoveUnwanted()
    {
        // 🔴 這裡<b>不能</b>用「筆數相同就跳過」當捷徑。`seen` 含被 Mappy 拒絕（沒進 live）的鍵，
        //    所以「舊的 A 該刪、新的 B 被拒絕」會讓兩邊筆數剛好相等，而 A 就永遠刪不掉了。
        //    唯一安全的捷徑是「本來就沒有東西」。
        if (live.Count == 0) return;

        List<string>? drop = null;
        foreach (var key in live.Keys)
        {
            if (!seen.Contains(key)) (drop ??= []).Add(key);
        }

        if (drop == null) return;

        foreach (var key in drop)
        {
            if (!live.Remove(key, out var entry)) continue;

            MappyMarkerIpc.RemoveMarker(Source, entry.Handle);
            LastIpcCalls++;
        }
    }

    /// <summary>
    /// 內容簽章：這一串沒變＝這筆標記畫出來的東西沒變，可以整筆不動。
    /// </summary>
    /// <remarks>
    /// ⚠️ 座標用 <c>InvariantCulture</c> 格式化——區域設定用逗號當小數點的機器上，
    /// 預設格式會讓分隔字元與數字撞在一起，兩個不同的座標可能產生同一個簽章。
    /// </remarks>
    private static string BuildSignature(Marker marker)
    {
        var sb = new StringBuilder();
        sb.Append(marker.MapId).Append('|')
          .Append(marker.MapCoordinates.X.ToString("F2", CultureInfo.InvariantCulture)).Append('|')
          .Append(marker.MapCoordinates.Y.ToString("F2", CultureInfo.InvariantCulture)).Append('|')
          .Append(marker.IconId).Append('|')
          .Append(marker.Tooltip);
        return sb.ToString();
    }
}

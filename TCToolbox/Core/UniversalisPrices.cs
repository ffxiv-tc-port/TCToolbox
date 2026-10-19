using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TCToolbox.Core;

/// <summary>某個世界在這次查價裡的結果。</summary>
/// <param name="WorldId"><c>World</c> 表的列號。顯示名一律另外查表，這裡不存名字。</param>
/// <param name="MinNq">普通品最低單價；<b>0＝這次沒看到</b>（不是免費，也不一定是沒人賣）。</param>
/// <param name="MinHq">高品質最低單價；<b>0＝這次沒看到</b>。</param>
/// <param name="UploadedAtUnixMs">
/// Universalis 上一次收到這個世界的這件道具資料的時間；<b>0＝從來沒有人上傳過</b>。
/// 這一格才分得出「確認沒人賣」與「根本沒資料」。
/// </param>
internal sealed record WorldPriceRow(uint WorldId, uint MinNq, uint MinHq, long UploadedAtUnixMs);

/// <summary>一次跨世界查價的完整結果。</summary>
/// <param name="Capped">
/// 掛單筆數頂到上限了。<b>頂到上限時，沒出現在結果裡的世界是「不知道」而不是「沒人賣」</b>——
/// 那個世界的最低價只是高於整個 DC 的第 N 便宜而已。
/// </param>
/// <param name="Error"><c>null</c>＝這次成功。非 null 時其餘欄位沒有意義。</param>
internal sealed record PriceFetchResult(
    uint ItemId,
    string DcName,
    long FetchedAtUnixMs,
    IReadOnlyDictionary<uint, WorldPriceRow> Worlds,
    int ListingsSeen,
    bool Capped,
    string? Error);

/// <summary>
/// 向 Universalis 查「本資料中心各世界的最低掛單價」。
/// </summary>
/// <remarks>
/// 🔴🔴 <b>紅線：純顯示。</b>這支只送 HTTP GET 取公開的市場資料，不掛售、不採購、
/// 不自動換世界，也不會自己排程——每一次查詢都必須是使用者當下按下去的。
/// 🔴 抓取跑在背景執行緒，<b>不碰任何 Dalamud 服務</b>（<c>Svc.Log</c> 是 Serilog，執行緒安全）。
/// 結果用 <see cref="Interlocked.Exchange{T}"/> 交給主執行緒，主執行緒在 <see cref="Consume"/> 取走。
/// 📌 一次 DC 查詢就夠：Universalis 的資料中心端點把掛單<b>照單價升冪</b>排好並附上每一筆的
/// <c>worldID</c>，所以逐世界取最小值就是各世界最低價，不必對八個世界各送一次。
/// </remarks>
internal sealed class UniversalisPrices : IDisposable
{
    /// <summary>一次要幾筆掛單。頂到這個數就代表「更貴的那些沒看到」。</summary>
    /// <remarks>實測單筆回應 100 筆約 20 KB；再往上拉對「找最便宜的世界」沒有幫助。</remarks>
    public const int ListingCap = 100;

    private const int HttpTimeoutSeconds = 15;

    /// <summary>回應大小上界，只是不讓意外的回應無上限地配置記憶體。</summary>
    private const int ResponseSizeLimitBytes = 8 * 1024 * 1024;

    /// <summary>同一種失敗最多多久寫一次記錄（毫秒）。</summary>
    private const long FailLogIntervalMs = 600_000;

    private readonly HttpClient http;
    private CancellationTokenSource? cts;

    /// <summary>0＝閒置，1＝有一次查詢還沒回來。</summary>
    private int inFlight;

    /// <summary>背景丟過來的結果，主執行緒用 <see cref="Interlocked.Exchange{T}"/> 取走。</summary>
    private PriceFetchResult? incoming;

    /// <summary>上一次寫失敗記錄的時間（<see cref="Environment.TickCount64"/>）。</summary>
    /// <remarks>
    /// 🔴 這裡<b>不能用共用的 <see cref="Throttle"/></b>：那支是主執行緒專用的裸 Dictionary，
    /// 從背景執行緒插入會把字典本身弄壞，連帶弄壞整個外掛的節流。
    /// 同時只有一個抓取工作（<see cref="inFlight"/> 保證），所以一個 volatile 長整數就夠。
    /// </remarks>
    private long lastFailLogAt = -FailLogIntervalMs;

    public UniversalisPrices()
    {
        cts = new CancellationTokenSource();
        http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(HttpTimeoutSeconds),
            MaxResponseContentBufferSize = ResponseSizeLimitBytes,
        };

        // Universalis 對沒有 User-Agent 的請求會回 403。
        http.DefaultRequestHeaders.UserAgent.ParseAdd("TCToolbox/1.0 (+https://github.com/ffxiv-tc-port/TCToolbox)");
    }

    /// <summary>最近一次查價的結果（<c>null</c>＝還沒查過）。<b>只在主執行緒讀寫。</b></summary>
    public PriceFetchResult? Latest { get; private set; }

    /// <summary>有一次查詢還在路上。</summary>
    public bool IsFetching => Volatile.Read(ref inFlight) != 0;

    /// <summary>
    /// 排一次查詢。已經有一次在路上時回 <see langword="false"/>。
    /// </summary>
    /// <remarks>🔴 呼叫端負責節流（同一件道具 60 秒一次），這裡只擋「同時兩次」。</remarks>
    public bool TryStart(uint itemId, string dcName)
    {
        if (itemId == 0 || string.IsNullOrWhiteSpace(dcName)) return false;

        var tokenSource = cts;
        if (tokenSource == null) return false;

        if (Interlocked.CompareExchange(ref inFlight, 1, 0) != 0) return false;

        var token = tokenSource.Token;
        _ = Task.Run(() => RunFetch(itemId, dcName, token), token);
        return true;
    }

    /// <summary>把背景抓回來的結果搬到 <see cref="Latest"/>。<b>只在主執行緒呼叫。</b></summary>
    /// <returns>這一次真的有新結果。</returns>
    public bool Consume()
    {
        var result = Interlocked.Exchange(ref incoming, null);
        if (result == null) return false;

        Latest = result;
        return true;
    }

    private async Task RunFetch(uint itemId, string dcName, CancellationToken token)
    {
        try
        {
            var url = $"https://universalis.app/api/v2/{Uri.EscapeDataString(dcName)}/{itemId}"
                      + $"?listings={ListingCap}&entries=0";

            using var response = await http.GetAsync(url, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var payload = await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
            Interlocked.Exchange(ref incoming, Parse(itemId, dcName, payload));
        }
        // 🔴 判準是「我們的 token 被取消了」，不是例外型別：HttpClient 的 Timeout 也擲
        //    OperationCanceledException，只看型別會把逾時當成停用而不回報結果。
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException
                                   && token.IsCancellationRequested)
        {
            // 停用／卸載途中，不是錯誤，也不要回報結果。
        }
        catch (Exception ex)
        {
            // 失敗只寫一次：使用者連按時不要洗版。UI 上照樣每次都看得到失敗原因。
            var now = Environment.TickCount64;
            if (now - Volatile.Read(ref lastFailLogAt) >= FailLogIntervalMs)
            {
                Volatile.Write(ref lastFailLogAt, now);
                Svc.Log.Information($"[UniversalisPrices] 查價失敗（道具 {itemId}／{dcName}）：{ex.GetType().Name} {ex.Message}");
            }

            Interlocked.Exchange(ref incoming, Failure(itemId, dcName, $"{ex.GetType().Name}：{ex.Message}"));
        }
        finally
        {
            Interlocked.Exchange(ref inFlight, 0);
        }
    }

    private static PriceFetchResult Failure(uint itemId, string dcName, string error) =>
        new(itemId, dcName, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            new Dictionary<uint, WorldPriceRow>(), 0, false, error);

    /// <summary>
    /// 把回應解成「每個世界一列」。
    /// </summary>
    /// <remarks>
    /// 📌 世界集合取自 <c>worldUploadTimes</c>（Universalis 認得的整個 DC），再把掛單裡出現的
    /// 世界補進去。只靠掛單會讓「沒人賣的世界」整列消失，而那一列正是要給使用者看的。
    /// 🔴 單筆格式不對只跳過那一筆，不讓整次查詢失敗。
    /// </remarks>
    private static PriceFetchResult Parse(uint itemId, string dcName, byte[] payload)
    {
        var json = new ReadOnlyMemory<byte>(payload);
        if (json.Length >= 3 && json.Span[0] == 0xEF && json.Span[1] == 0xBB && json.Span[2] == 0xBF)
            json = json[3..];

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            return Failure(itemId, dcName, "回應最上層不是物件");

        var rows = new Dictionary<uint, (uint Nq, uint Hq, long Uploaded)>();

        if (root.TryGetProperty("worldUploadTimes", out var uploads) && uploads.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in uploads.EnumerateObject())
            {
                if (!uint.TryParse(entry.Name, out var worldId)) continue;
                if (entry.Value.ValueKind != JsonValueKind.Number) continue;
                if (!entry.Value.TryGetInt64(out var at)) continue;
                rows[worldId] = (0, 0, at);
            }
        }

        var seen = 0;
        if (root.TryGetProperty("listings", out var listings) && listings.ValueKind == JsonValueKind.Array)
        {
            foreach (var listing in listings.EnumerateArray())
            {
                if (listing.ValueKind != JsonValueKind.Object) continue;
                if (!listing.TryGetProperty("worldID", out var worldElement)) continue;
                if (!worldElement.TryGetUInt32(out var worldId)) continue;
                if (!listing.TryGetProperty("pricePerUnit", out var priceElement)) continue;
                if (!priceElement.TryGetUInt32(out var price) || price == 0) continue;

                seen++;
                var hq = listing.TryGetProperty("hq", out var hqElement) && hqElement.ValueKind == JsonValueKind.True;

                rows.TryGetValue(worldId, out var row);
                if (hq)
                {
                    if (row.Hq == 0 || price < row.Hq) row.Hq = price;
                }
                else
                {
                    if (row.Nq == 0 || price < row.Nq) row.Nq = price;
                }

                rows[worldId] = row;
            }
        }

        var worlds = new Dictionary<uint, WorldPriceRow>();
        foreach (var (worldId, row) in rows)
            worlds[worldId] = new WorldPriceRow(worldId, row.Nq, row.Hq, row.Uploaded);

        return new PriceFetchResult(
            itemId, dcName, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            worlds, seen, seen >= ListingCap, null);
    }

    public void Dispose()
    {
        // 先取消再收 HttpClient：進行中的請求會擲 OperationCanceled／ObjectDisposed，
        // 兩種都由 RunFetch 自己吞掉。
        Interlocked.Exchange(ref cts, null)?.Cancel();
        http.Dispose();
        Interlocked.Exchange(ref incoming, null);
        Latest = null;
    }
}

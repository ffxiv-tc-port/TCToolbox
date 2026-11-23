using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace TCToolbox.Core;

/// <summary>一次聚合查詢（一個世界、最多 <see cref="UniversalisAggregated.BatchSize"/> 件物品）的結果。</summary>
/// <param name="Error"><c>null</c>＝成功；非 null 時 <paramref name="Items"/> 是空的，<paramref name="Requested"/> 全部算失敗。</param>
internal sealed record AggregatedBatch(
    uint WorldId,
    uint[] Requested,
    IReadOnlyDictionary<uint, WorldItemMarket> Items,
    IReadOnlyList<uint> Failed,
    long FetchedAtUnixMs,
    string? Error);

/// <summary>
/// 向 Universalis 的聚合端點批次查「某世界、多件物品」的最低掛單、成交均價、日銷量。
/// </summary>
/// <remarks>
/// 🔴 紅線：純顯示。只送 HTTP GET，網址只有世界編號與物品編號（不送角色名等任何個資）；每一次都必須是使用者當下按的，這裡不排程。
/// 🔴 抓取跑在背景執行緒、不碰 Dalamud 服務（<c>Svc.Log</c> 是 Serilog，執行緒安全）；結果經佇列交給主執行緒，主執行緒用 <see cref="TryTake"/> 取走。
/// 遇 429／5xx／連線失敗退避重試（有 Retry-After 就照它），其他狀態碼不重試；請求之間固定間隔，不平行。
/// </remarks>
internal sealed class UniversalisAggregated : IDisposable
{
    /// <summary>聚合端點一次最多 100 件。</summary>
    public const int BatchSize = 100;

    private const int MaxRetries = 3;
    private const int PaceMs = 300;
    private const int MaxBackoffMs = 60_000;
    private const int HttpTimeoutSeconds = 20;
    private const int ResponseSizeLimitBytes = 8 * 1024 * 1024;

    private readonly HttpClient http;
    private readonly ConcurrentQueue<AggregatedBatch> results = new();
    private CancellationTokenSource? cts = new();
    private int inFlight;
    private int done;
    private int total;

    public UniversalisAggregated()
    {
        http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(HttpTimeoutSeconds),
            MaxResponseContentBufferSize = ResponseSizeLimitBytes,
        };

        // 與 UniversalisPrices 同一個 User-Agent：沒有 User-Agent 的請求 Universalis 回 403。
        http.DefaultRequestHeaders.UserAgent.ParseAdd("TCToolbox/1.0 (+https://github.com/ffxiv-tc-port/TCToolbox)");
    }

    public bool IsFetching => Volatile.Read(ref inFlight) != 0;

    /// <summary>這一輪已完成／總共的請求數。</summary>
    public (int Done, int Total) Progress => (Volatile.Read(ref done), Volatile.Read(ref total));

    /// <summary>排一輪查詢；已有一輪在路上時回 false。</summary>
    /// <param name="work">每個世界要查的物品（呼叫端已去重）。</param>
    public bool TryStart(IReadOnlyList<(uint WorldId, uint[] ItemIds)> work)
    {
        var tokenSource = cts;
        if (tokenSource == null || work.Count == 0) return false;
        if (Interlocked.CompareExchange(ref inFlight, 1, 0) != 0) return false;

        var batches = new List<(uint WorldId, uint[] Items)>();
        foreach (var (worldId, items) in work)
        {
            for (var i = 0; i < items.Length; i += BatchSize)
                batches.Add((worldId, items.Skip(i).Take(BatchSize).ToArray()));
        }

        Volatile.Write(ref done, 0);
        Volatile.Write(ref total, batches.Count);
        var token = tokenSource.Token;
        _ = Task.Run(() => RunAll(batches, token), token);
        return true;
    }

    /// <summary>取走一批已完成的結果。<b>只在主執行緒呼叫。</b></summary>
    public bool TryTake(out AggregatedBatch batch) => results.TryDequeue(out batch!);

    private async Task RunAll(List<(uint WorldId, uint[] Items)> batches, CancellationToken token)
    {
        try
        {
            for (var i = 0; i < batches.Count; i++)
            {
                if (i > 0) await Task.Delay(PaceMs, token).ConfigureAwait(false);
                var (worldId, items) = batches[i];
                results.Enqueue(await FetchWithRetry(worldId, items, token).ConfigureAwait(false));
                Interlocked.Increment(ref done);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException && token.IsCancellationRequested)
        {
            // 停用／卸載途中，不是錯誤。
        }
        catch (Exception ex)
        {
            Svc.Log.Information($"[VentureValue] 查價中斷：{ex.GetType().Name} {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref inFlight, 0);
        }
    }

    private async Task<AggregatedBatch> FetchWithRetry(uint worldId, uint[] items, CancellationToken token)
    {
        var url = $"https://universalis.app/api/v2/aggregated/{worldId}/{string.Join(',', items)}";
        string error = "未知錯誤";
        for (var attempt = 0; attempt <= MaxRetries; attempt++)
        {
            int? retryAfterMs = null;
            try
            {
                using var response = await http.GetAsync(url, token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    var payload = await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
                    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    return VentureValueMath.TryParseAggregated(payload, worldId, out var parsed, out var failed)
                        ? new AggregatedBatch(worldId, items, parsed, failed, now, null)
                        : Failure(worldId, items, "回應形狀不對");
                }

                var code = (int)response.StatusCode;
                error = $"HTTP {code}";
                if (code != (int)HttpStatusCode.TooManyRequests && code < 500) return Failure(worldId, items, error);
                if (response.Headers.RetryAfter?.Delta is { } delta) retryAfterMs = (int)Math.Min(delta.TotalMilliseconds, MaxBackoffMs);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !token.IsCancellationRequested)
            {
                error = $"{ex.GetType().Name}：{ex.Message}";
            }

            if (attempt == MaxRetries) break;
            var waitMs = retryAfterMs ?? Math.Min(2000 << attempt, MaxBackoffMs);
            Svc.Log.Information($"[VentureValue] Universalis {error}，{waitMs / 1000.0:0.#} 秒後重試（世界 {worldId}，{items.Length} 件，第 {attempt + 1} 次）");
            await Task.Delay(waitMs, token).ConfigureAwait(false);
        }

        return Failure(worldId, items, error);
    }

    private static AggregatedBatch Failure(uint worldId, uint[] items, string error) =>
        new(worldId, items, new Dictionary<uint, WorldItemMarket>(), [], DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), error);

    public void Dispose()
    {
        Interlocked.Exchange(ref cts, null)?.Cancel();
        http.Dispose();
        while (results.TryDequeue(out _)) { }
    }
}

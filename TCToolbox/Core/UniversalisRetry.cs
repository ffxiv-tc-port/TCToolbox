using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace TCToolbox.Core;

/// <summary>Universalis GET 的共用退避重試：429／5xx／連線失敗／逾時才重試，有 Retry-After 就照它，其他狀態碼直接失敗。</summary>
/// <remarks>
/// 跑在背景執行緒，不碰 Dalamud 服務（<c>Svc.Log</c> 是 Serilog，執行緒安全）；不用共用的 <see cref="Throttle"/>。
/// 我們的 token 被取消時照樣擲 <see cref="OperationCanceledException"/>，由呼叫端當成停用處理。
/// </remarks>
internal static class UniversalisRetry
{
    public const int MaxRetries = 3;
    public const int MaxBackoffMs = 60_000;

    /// <param name="logPrefix">重試記錄行開頭，例如 <c>[VentureValue]</c>。</param>
    /// <param name="logContext">重試記錄行括號裡的查詢描述。</param>
    /// <param name="onAttempt">每送出一次 GET 前呼叫（參數 0＝第一次，之後是重試）；在背景執行緒上。</param>
    /// <returns>成功＝回應本文、<c>Error</c> 為 null；失敗＝<c>Payload</c> 為 null、<c>Error</c> 是可直接顯示的原因。</returns>
    public static async Task<(byte[]? Payload, string? Error)> GetAsync(
        HttpClient http, string url, string logPrefix, string logContext, CancellationToken token,
        Action<int>? onAttempt = null)
    {
        var error = "未知錯誤";
        for (var attempt = 0; attempt <= MaxRetries; attempt++)
        {
            int? retryAfterMs = null;
            try
            {
                onAttempt?.Invoke(attempt);
                using var response = await http.GetAsync(url, token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                    return (await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false), null);

                var code = (int)response.StatusCode;
                error = $"HTTP {code}";
                if (code != (int)HttpStatusCode.TooManyRequests && code < 500) return (null, error);
                if (response.Headers.RetryAfter?.Delta is { } delta) retryAfterMs = (int)Math.Min(delta.TotalMilliseconds, MaxBackoffMs);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !token.IsCancellationRequested)
            {
                error = $"{ex.GetType().Name}：{ex.Message}";
            }

            if (attempt == MaxRetries) break;
            var waitMs = retryAfterMs ?? Math.Min(2000 << attempt, MaxBackoffMs);
            Svc.Log.Information($"{logPrefix} Universalis {error}，{waitMs / 1000.0:0.#} 秒後重試（{logContext}，第 {attempt + 1} 次）");
            await Task.Delay(waitMs, token).ConfigureAwait(false);
        }

        return (null, error);
    }
}

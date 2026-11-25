using System;
using System.Collections.Generic;
using System.Threading;

namespace TCToolbox.Core;

/// <summary>對 Universalis 的請求量計數，每小時吐一行彙總（整小時沒有任何動靜就不吐）。</summary>
/// <remarks>
/// 契約：<see cref="Query"/>、<see cref="TryFlush"/> 只在主執行緒呼叫；
/// <see cref="Attempt"/>、<see cref="Succeeded"/>、<see cref="Failed"/> 可從背景執行緒呼叫。
/// </remarks>
internal sealed class UniversalisRequestStats
{
    public const long IntervalMs = 3_600_000;

    private readonly string logPrefix;
    private readonly HashSet<uint> items = [];
    private long windowStartMs;
    private int queries, requests, retries, successes, failures;

    public UniversalisRequestStats(string logPrefix, long nowMs)
    {
        this.logPrefix = logPrefix;
        windowStartMs = nowMs;
    }

    public void Query(uint itemId)
    {
        queries++;
        items.Add(itemId);
    }

    /// <param name="attempt">0＝第一次送，之後是重試。</param>
    public void Attempt(int attempt)
    {
        Interlocked.Increment(ref requests);
        if (attempt > 0) Interlocked.Increment(ref retries);
    }

    public void Succeeded() => Interlocked.Increment(ref successes);

    public void Failed() => Interlocked.Increment(ref failures);

    /// <summary>滿一小時（或 <paramref name="force"/>）就回傳彙總行並歸零；否則回 null。</summary>
    public string? TryFlush(long nowMs, bool force = false)
    {
        var elapsed = nowMs - windowStartMs;
        if (!force && elapsed < IntervalMs) return null;

        windowStartMs = nowMs;
        var q = queries;
        var itemCount = items.Count;
        queries = 0;
        items.Clear();
        var req = Interlocked.Exchange(ref requests, 0);
        var retry = Interlocked.Exchange(ref retries, 0);
        var ok = Interlocked.Exchange(ref successes, 0);
        var fail = Interlocked.Exchange(ref failures, 0);
        if (q + req + ok + fail == 0) return null;

        var span = elapsed >= IntervalMs ? "本小時" : $"近 {Math.Max(1, elapsed / 60_000)} 分";
        return $"{logPrefix} Universalis {span}：查價 {q} 次（道具 {itemCount} 件）、HTTP 請求 {req} 次（其中重試 {retry} 次）、成功 {ok}、失敗 {fail}";
    }
}

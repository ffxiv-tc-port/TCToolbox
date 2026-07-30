using System;
using System.Collections.Generic;

namespace TCToolbox.Core;

/// <summary>字串鍵節流器。只在主執行緒使用。</summary>
public static class Throttle
{
    private static readonly Dictionary<string, DateTime> NextAllowed = [];

    /// <summary>若鍵已冷卻完畢則通過並重新計時，否則回傳 false。</summary>
    public static bool Pass(string key, int milliseconds)
    {
        var now = DateTime.UtcNow;
        if (NextAllowed.TryGetValue(key, out var next) && now < next)
            return false;

        NextAllowed[key] = now.AddMilliseconds(milliseconds);
        return true;
    }

    public static void Reset(string key) => NextAllowed.Remove(key);
}

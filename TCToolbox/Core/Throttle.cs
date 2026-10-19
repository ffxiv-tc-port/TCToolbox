using System;
using System.Collections.Generic;

namespace TCToolbox.Core;

/// <summary>字串鍵節流器。整個插件共用一份表，但每個鍵各自獨立：<see cref="Reset"/> 只動那一個鍵。</summary>
/// <remarks>
/// 任何執行緒都可以呼叫：模組在插件建構時（非框架執行緒）啟用，那時先啟用的模組已經在框架執行緒上每幀讀寫同一份表。
/// </remarks>
public static class Throttle
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, DateTime> NextAllowed = [];

    /// <summary>若鍵已冷卻完畢則通過並重新計時，否則回傳 false。</summary>
    public static bool Pass(string key, int milliseconds)
    {
        var now = DateTime.UtcNow;
        lock (Gate)
        {
            if (NextAllowed.TryGetValue(key, out var next) && now < next)
                return false;

            NextAllowed[key] = now.AddMilliseconds(milliseconds);
            return true;
        }
    }

    /// <summary>
    /// 無條件把這個鍵的下次可用時間往後推（用來實作「連續失敗就退避一段時間」）。
    /// </summary>
    /// <remarks>
    /// 🔴 <b><see cref="Pass"/> 拿來當「設定退避」用會靜默失效</b>：鍵<b>還在冷卻中</b>時它走的是 <c>return false</c> 那條路，
    /// <b>根本不會去寫 <see cref="NextAllowed"/></b>——而「剛做完一次動作、正在冷卻」恰好就是想要設退避的那一刻。
    /// </remarks>
    public static void Block(string key, int milliseconds)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        lock (Gate)
        {
            if (NextAllowed.TryGetValue(key, out var existing) && existing >= until) return;

            NextAllowed[key] = until;
        }
    }

    public static void Reset(string key)
    {
        lock (Gate) NextAllowed.Remove(key);
    }
}

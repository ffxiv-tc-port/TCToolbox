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

    /// <summary>
    /// 無條件把這個鍵的下次可用時間往後推（用來實作「連續失敗就退避一段時間」）。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>這支存在的理由是 <see cref="Pass"/> 拿來當「設定退避」用會靜默失效。</b>
    /// <c>Pass</c> 在鍵<b>還在冷卻中</b>時走的是 <c>return false</c> 那條路，
    /// <b>根本不會去寫 <see cref="NextAllowed"/></b>——而「剛做完一次動作、正在冷卻」
    /// 恰好就是想要設退避的那一刻。也就是說 <c>Pass(key, 十分鐘)</c> 在真正需要它的時候
    /// 一律是無操作，而且不報錯：表現成「退避沒生效，繼續每 30 秒重試」。
    /// <para>
    /// 📌 只會往後推、不會往前縮：已經被擋到更久之後的鍵不受影響，
    /// 免得比較短的退避把比較長的那個蓋掉。
    /// </para>
    /// </remarks>
    public static void Block(string key, int milliseconds)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        if (NextAllowed.TryGetValue(key, out var existing) && existing >= until) return;

        NextAllowed[key] = until;
    }

    public static void Reset(string key) => NextAllowed.Remove(key);
}

using System;
using System.Collections.Generic;
using System.Text;

namespace TCToolbox.Modules;

// 本檔只有純函式：不讀遊戲記憶體、不碰 Dalamud，離線探針直接編這一個檔。

/// <summary>
/// 畫面上顯示的道具名 ↔ 清單裡道具表名稱的比對。
/// 台服道具表有些列 <c>Singular</c> 與 <c>Name</c> 寫法不同，畫面顯示其中一種，兩種都當候選。
/// </summary>
internal static class ShopItemNameMatch
{
    /// <summary>沒有任何候選命中。</summary>
    public const int NoMatch = -1;

    /// <summary>命中的候選分屬不同的鍵，無法判定是哪一個。</summary>
    public const int Ambiguous = -2;

    /// <summary>
    /// 去掉控制字元與私用區圖示字元、全形空白與不換行空白換成半形、去掉前後空白；其餘逐字保留。
    /// </summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c < ' ' || c is >= '\uE000' and <= '\uF8FF')
                continue;
            sb.Append(c is '\u3000' or '\u00A0' ? ' ' : c);
        }

        return sb.ToString().Trim();
    }

    /// <summary>正規化後不重複、非空的候選名；兩欄都空時回空陣列。</summary>
    public static string[] Candidates(string? singular, string? name)
    {
        var s = Normalize(singular);
        var n = Normalize(name);
        if (s.Length == 0)
            return n.Length == 0 ? [] : [n];
        if (n.Length == 0 || string.Equals(s, n, StringComparison.OrdinalIgnoreCase))
            return [s];
        return [s, n];
    }

    /// <summary>
    /// 先找候選與 <paramref name="shown"/> 整句相等的項目；都不相等才退回「候選包含 shown」（畫面可能截斷）。
    /// 命中的項目全部同一個 <c>Key</c> 才回第一個的索引；分屬不同鍵回 <see cref="Ambiguous"/>，沒有命中回 <see cref="NoMatch"/>。
    /// <paramref name="shown"/> 必須已經 <see cref="Normalize"/> 過。不配置記憶體。
    /// </summary>
    public static int Find(IReadOnlyList<(uint Key, string[] Names)> entries, string shown)
    {
        if (shown.Length == 0)
            return NoMatch;

        var hit = FindUnique(entries, shown, exact: true);
        return hit == NoMatch ? FindUnique(entries, shown, exact: false) : hit;
    }

    private static int FindUnique(IReadOnlyList<(uint Key, string[] Names)> entries, string shown, bool exact)
    {
        var found = NoMatch;
        for (var i = 0; i < entries.Count; i++)
        {
            var (key, names) = entries[i];
            if (!AnyMatches(names, shown, exact))
                continue;
            if (found == NoMatch)
                found = i;
            else if (entries[found].Key != key)
                return Ambiguous;
        }

        return found;
    }

    private static bool AnyMatches(string[] names, string shown, bool exact)
    {
        foreach (var name in names)
        {
            if (exact
                    ? string.Equals(name, shown, StringComparison.OrdinalIgnoreCase)
                    : name.Contains(shown, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}

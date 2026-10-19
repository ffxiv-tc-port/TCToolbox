using Dalamud.Game.Text;
using Lumina.Excel.Sheets;

namespace TCToolbox.Core;

/// <summary>道具名稱解析。一律走 Lumina <c>Item</c> 表（台服自帶繁中），不讀 addon 上的文字。</summary>
public static class ItemNames
{
    /// <summary>
    /// 取道具名稱。查不到時回 <c>#id</c> 而不是空字串。
    /// ⚠️ <c>Item</c> 表的第 0 列是**有效列但名稱為空**，所以不能只判斷 null ——
    /// 少了空字串判斷，訊息會變成「已取出『』」這種看起來像 bug 的輸出。
    /// </summary>
    public static string Get(uint baseItemId, bool highQuality = false)
    {
        var name = Svc.Data.GetExcelSheet<Item>().GetRowOrDefault(baseItemId)?.Name.ExtractText() ?? string.Empty;
        if (string.IsNullOrEmpty(name)) return $"#{baseItemId}";

        return highQuality ? $"{name} {(char)SeIconChar.HighQuality}" : name;
    }
}

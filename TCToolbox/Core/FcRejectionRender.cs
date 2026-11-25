using System;
using System.Text;
using Lumina.Text.Payloads;
using Lumina.Text.ReadOnly;

namespace TCToolbox.Core;

/// <summary>把公會特效的失敗訊息（LogMessage）展開成遊戲印出來的整句。只依賴 Lumina，離線可測。</summary>
internal static class FcRejectionRender
{
    public const string AuthoritySheet = "FCAuthority";

    /// <summary>
    /// 只認純文字，和嵌 <c>&lt;sheet(FCAuthority,列,0)&gt;</c> 的句型（LogMessage 1919）；
    /// 其他巨集一律回空字串（fail-closed：寧可少比對一種，也不要拿殘句去比）。
    /// </summary>
    /// <param name="authorityName">FCAuthority 列號→名稱；讀不到回空字串。</param>
    public static string Render(ReadOnlySeString text, Func<uint, string> authorityName, out string problem)
    {
        problem = string.Empty;
        var sb = new StringBuilder();
        foreach (var payload in text)
        {
            if (payload.Type == ReadOnlySePayloadType.Text)
            {
                sb.Append(Encoding.UTF8.GetString(payload.Body.Span));
                continue;
            }

            if (payload.MacroCode != MacroCode.Sheet
                || !payload.TryGetExpression(out var sheetExpr, out var rowExpr, out _)
                || !sheetExpr.TryGetString(out var sheetName) || sheetName.ExtractText() != AuthoritySheet
                || !rowExpr.TryGetUInt(out var authorityRow))
            {
                problem = $"含不認得的巨集 {payload.MacroCode}";
                return string.Empty;
            }

            var name = authorityName(authorityRow);
            if (name.Length == 0)
            {
                problem = $"{AuthoritySheet} {authorityRow} 讀不到";
                return string.Empty;
            }

            sb.Append(name);
        }

        var result = sb.ToString().Trim();
        if (result.Length == 0) problem = "是空的";
        return result;
    }
}

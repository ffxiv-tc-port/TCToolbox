using System;
using System.Collections.Generic;
using System.Text;
using Dalamud.Memory;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;

namespace TCToolbox.Core;

/// <summary>
/// 「這個確認框是不是我要的那一個」的資料驅動判準：拿 <c>Addon</c> 表的某一列當樣板，
/// 只留下句子裡<b>不含 placeholder 的固定片段</b>，再要求實際對話框的文字
/// <b>依序含有全部片段</b>才算命中。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 <b>為什麼不能整句完全比對</b>：確認框的句子普遍帶 placeholder（隊長名、副本名、道具名、
/// 數量…），實際顯示的文字每次都不一樣，逐字比對永遠不會命中。
/// </para>
/// <para>
/// 🔴 <b>為什麼不能「任何一段命中就算」</b>：ECommons <c>GenericHelpers.ContainsPartOf</c> 是
/// any-match，把 needle 拆成片段後<b>任何一段</b>被含到就回 true。Addon 表的句子拆完就是好幾段
/// 短文字，於是「拿某一列當樣板比對當下對話框」會在<b>別的 Addon 列</b>上誤中，而失敗形式是
/// 靜默的（照樣自動按「是」，只是按在別的對話框上）。
/// 實測（台服 7.20，全表 14850 列）：小隊邀請那句 <c>Addon#120</c> 用 any-match 會誤中
/// 「新人頻道」與兩句「同好會邀請」；改成本類別的 all-match 依序之後只剩本句。
/// </para>
/// <para>
/// 🔑 <b>比對基準</b>：兩邊都先<b>去掉所有空白字元</b>再比。原因是換行在 SeString 裡是 macro
/// 而不是文字，遊戲把它塞進節點時渲染成什麼（真的換行／整個消失）離線證明不了 ——
/// 去掉空白就讓這個不確定性完全不影響判定。中日文句子去空白不會損失鑑別力：
/// 離線實測本檔目前用到的六個樣板，每一個在全表 14850 列裡都只命中它自己那一列。
/// </para>
/// <para>
/// ⚠️ <b>一律 fail-closed</b>：樣板解不出來（台服沒有這列／欄位是空的）就回
/// <see langword="false"/>，也就是「不動作」。寧可讓自動確認失效，也不要按在別人的對話框上。
/// </para>
/// </remarks>
internal static unsafe class AddonPrompt
{
    /// <summary>
    /// 把 <c>Addon</c> 表某一列拆成「不含 placeholder 的固定片段」（已去空白，空片段丟掉）。
    /// </summary>
    /// <remarks>
    /// 走的是 payload 列舉而不是 <c>ExtractText()</c>：<c>ExtractText()</c> 會把 macro 直接抹掉並
    /// 把前後文字接起來，片段邊界就消失了（「確定要領取」＋「×」＋「嗎？」會變成一整串），
    /// 那樣就沒辦法只比對固定的部分。
    /// </remarks>
    public static List<string> GetFragments(uint addonRowId)
    {
        var result = new List<string>();
        if (addonRowId == 0) return result;

        var row = Svc.Data.GetExcelSheet<Addon>().GetRowOrDefault(addonRowId);
        if (row == null) return result;

        foreach (var payload in row.Value.Text)
        {
            if (payload.Type != ReadOnlySePayloadType.Text) continue;

            var text = Normalize(Encoding.UTF8.GetString(payload.Body.Span));
            if (text.Length == 0) continue;

            result.Add(text);
        }

        return result;
    }

    /// <summary>一次解析多列，回傳「每列一組片段」（解不出來的列直接不收）。</summary>
    public static List<List<string>> GetTemplates(IReadOnlyList<uint> addonRowIds)
    {
        var result = new List<List<string>>();
        foreach (var rowId in addonRowIds)
        {
            var fragments = GetFragments(rowId);
            if (fragments.Count > 0) result.Add(fragments);
        }

        return result;
    }

    /// <summary>提示文字是否依序含有樣板的全部片段。</summary>
    public static bool Matches(string prompt, IReadOnlyList<string> fragments)
    {
        // 樣板解不出來就不要放行——沒有判準時「不動作」才是安全的方向。
        if (fragments == null || fragments.Count == 0) return false;
        if (string.IsNullOrEmpty(prompt)) return false;

        var haystack = Normalize(prompt);
        if (haystack.Length == 0) return false;

        var at = 0;
        foreach (var fragment in fragments)
        {
            var hit = haystack.IndexOf(fragment, at, StringComparison.Ordinal);
            if (hit < 0) return false;
            at = hit + fragment.Length;
        }

        return true;
    }

    /// <summary>提示文字是否命中任何一組樣板。</summary>
    public static bool MatchesAny(string prompt, IReadOnlyList<List<string>> templates)
    {
        if (templates == null) return false;

        foreach (var fragments in templates)
        {
            if (Matches(prompt, fragments)) return true;
        }

        return false;
    }

    /// <summary>把一組樣板攤平成一行方便寫進 log（診斷用，出事時這行是唯一的線索）。</summary>
    public static string Describe(IReadOnlyList<List<string>> templates)
    {
        if (templates == null || templates.Count == 0) return "（無）";

        var sb = new StringBuilder();
        foreach (var fragments in templates)
        {
            if (sb.Length > 0) sb.Append(" ｜ ");
            sb.Append(string.Join("…", fragments));
        }

        return sb.ToString();
    }

    /// <summary>
    /// 讀 <c>SelectYesno</c> 的提示文字；讀不到一律回空字串。
    /// </summary>
    /// <remarks>
    /// 🔴 用 <c>MemoryHelper.ReadSeString(...).TextValue</c> 而不是 <c>Utf8String.ToString()</c>：
    /// 前者是「拿掉 payload 之後的純文字」，與 <see cref="GetFragments"/> 那一側同一個基準
    /// （做法同 AutoRequestItemSubmit）；後者會把 macro 的控制位元組一起解出來，
    /// 那些位元組會把片段從中間切斷，比對就靜默失效。
    /// </remarks>
    public static string ReadSelectYesnoText(AtkUnitBase* addon)
    {
        if (addon == null) return string.Empty;

        var yesno = (AddonSelectYesno*)addon;
        if (yesno->PromptText == null) return string.Empty;

        return MemoryHelper.ReadSeString(&yesno->PromptText->NodeText).TextValue;
    }

    /// <summary>
    /// 這串提示文字看起來是不是「讀到一半」的（含 U+FFFD 替換字元）。
    /// </summary>
    /// <remarks>
    /// 🔴 2026-08-31 崩潰（<c>crash-20260831205734</c>）前的實機 log 裡，最後幾行讀到的
    /// 確認框文字帶著替換字元——那是<b>視窗的記憶體正在變動</b>（多半是正在關閉）時，
    /// UTF-8 解碼撞到半個字元的徵兆。這一幀讀到這種東西就<b>什麼都不要碰</b>，下一幀重讀即可。
    /// <para>
    /// ⚠️ 這是<b>額外</b>的一道，不是主防線——主防線是
    /// <see cref="AddonPressGuard.TryBeginPress"/>（按過就不再按）。
    /// 只有「窗的記憶體剛好正在被改」的那幾幀讀得到替換字元，讀不到不代表安全。
    /// </para>
    /// </remarks>
    public static bool LooksMidUpdate(string prompt) =>
        !string.IsNullOrEmpty(prompt) && prompt.Contains('�');

    /// <summary>
    /// 去掉所有空白字元。
    /// </summary>
    /// <remarks>
    /// <c>char.IsWhiteSpace</c> 涵蓋半形空白、tab、CR/LF，以及中日文常見的全形空白 U+3000
    /// 與 NBSP U+00A0 —— 這幾種正是「同一句話在表裡與在畫面上長得不一樣」的來源。
    /// </remarks>
    private static string Normalize(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (!char.IsWhiteSpace(ch)) sb.Append(ch);
        }

        return sb.ToString();
    }
}

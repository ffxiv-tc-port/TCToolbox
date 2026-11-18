using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace TCToolbox.Core;

// 本檔假設與契約：純邏輯，不碰 Dalamud 與原生指標（離線探針直接編譯本檔）。
// 呼叫端每幀把「現在開著哪些窗」抄成 SquadronFrame 交給 Decide，自己負責同幀取窗與送出。
// 失敗方向一律是 Stop：比不中、看不懂、多出一扇沒預期的窗，都交還給使用者。

/// <summary>選單那一項按了沒。零值＝還沒按。</summary>
internal enum SquadronMenuState
{
    NotPressed = 0,

    /// <summary>已送出，還沒看到那扇選單收掉。</summary>
    PressedWaitingClose = 1,

    /// <summary>已送出，也看到它收掉了；之後再出現的選單一律不按。</summary>
    PressedClosed = 2,
}

/// <summary><see cref="SquadronFlow.Decide"/> 的結論。零值＝等下一幀。</summary>
internal enum SquadronActionKind
{
    Wait = 0,

    /// <summary>目標清單已打開，收工。</summary>
    Done = 1,

    /// <summary>停手，交還使用者；<see cref="SquadronDecision.Reason"/> 是一句給人看的說明。</summary>
    Stop = 2,

    /// <summary>按選單第 <see cref="SquadronDecision.MenuIndex"/> 項。</summary>
    PressMenu = 3,

    /// <summary>推進一頁對話。</summary>
    AdvanceTalk = 4,
}

internal readonly record struct SquadronDecision(SquadronActionKind Kind, int MenuIndex, string Reason)
{
    public static SquadronDecision Wait() => new(SquadronActionKind.Wait, -1, string.Empty);

    public static SquadronDecision Stop(string reason) => new(SquadronActionKind.Stop, -1, reason);
}

/// <summary>一幀觀察到的視窗狀態（全部是值，不含指標）。</summary>
/// <param name="GoalReady">目標清單（GcArmyExpedition／GcArmyTraining）已就緒。</param>
/// <param name="OtherWindow">其他分隊相關視窗（結算、隊員名單…）的名字；空字串＝沒有。</param>
/// <param name="MenuEntries">SelectString 就緒時的選項（已剝 payload）；<c>null</c>＝選單沒開。</param>
/// <param name="TalkSpeaker">Talk 就緒時的說話者（已剝 payload）；<c>null</c>＝對話框沒開。</param>
/// <param name="MsSinceLastAction">距離上一次互動／按選單／推進對話的毫秒數。</param>
internal readonly record struct SquadronFrame(
    bool GoalReady,
    string? YesnoText,
    string OtherWindow,
    IReadOnlyList<string>? MenuEntries,
    string? TalkSpeaker,
    SquadronMenuState Menu,
    long MsSinceLastAction);

/// <summary>派遣確認框解析出的結果。</summary>
/// <param name="ReturnLocal">預定歸隊時間（本機時間，已補上年份）。</param>
/// <param name="Hours">確認框上的「N 小時後」；「立刻」為 0。</param>
internal readonly record struct SquadronReturn(string Mission, DateTime ReturnLocal, int Hours);

internal static class SquadronFlow
{
    /// <summary>推算出來的歸隊時間與「現在＋N 小時」最多可以差多少（分鐘）；超過就當成看不懂。</summary>
    public const int ReturnToleranceMinutes = 75;

    /// <summary>
    /// 這一幀該做什麼。順序就是優先序：確認框、其他分隊視窗優先於目標清單，
    /// 未按過的選單只按整句相等的那一項，對話框只推進說話者相符的。
    /// </summary>
    /// <param name="menuTarget">要按的選項全文（來自遊戲資料）。</param>
    /// <param name="expectedSpeaker">允許推進的說話者全名（來自遊戲資料）。</param>
    /// <param name="allowBlankSpeaker">說話者欄位空白時也推進（物件互動的對話框可能沒有名字）。</param>
    /// <param name="noWindowTimeoutMs">上一個動作之後多久什麼都沒出現就停手。</param>
    public static SquadronDecision Decide(
        in SquadronFrame f, string menuTarget, string expectedSpeaker, bool allowBlankSpeaker, long noWindowTimeoutMs)
    {
        if (f.YesnoText != null)
            return SquadronDecision.Stop($"出現了確認框「{OneLine(f.YesnoText)}」");

        if (f.OtherWindow.Length > 0)
            return SquadronDecision.Stop($"出現了 {f.OtherWindow} 視窗");

        if (f.GoalReady)
            return new SquadronDecision(SquadronActionKind.Done, -1, string.Empty);

        if (f.MenuEntries != null)
        {
            if (f.Menu == SquadronMenuState.PressedWaitingClose) return SquadronDecision.Wait();
            if (f.Menu == SquadronMenuState.PressedClosed)
                return SquadronDecision.Stop($"選單又出現了（{string.Join("、", f.MenuEntries)}）");

            if (LooksMidUpdate(f.MenuEntries)) return SquadronDecision.Wait();

            var index = FindExactEntry(f.MenuEntries, menuTarget);
            return index >= 0
                ? new SquadronDecision(SquadronActionKind.PressMenu, index, string.Empty)
                : SquadronDecision.Stop($"選單裡沒有「{menuTarget}」（{string.Join("、", f.MenuEntries)}）");
        }

        if (f.TalkSpeaker != null)
        {
            var speaker = NormalizeEntry(f.TalkSpeaker);
            if (speaker == NormalizeEntry(expectedSpeaker) || (allowBlankSpeaker && speaker.Length == 0))
                return new SquadronDecision(SquadronActionKind.AdvanceTalk, -1, string.Empty);

            return SquadronDecision.Stop($"對話框的說話者是「{speaker}」，不是「{expectedSpeaker}」");
        }

        return f.MsSinceLastAction > noWindowTimeoutMs
            ? SquadronDecision.Stop($"{noWindowTimeoutMs / 1000} 秒內沒有出現選單或清單")
            : SquadronDecision.Wait();
    }

    /// <summary>整句相等（剝換行、去頭尾空白）且只出現一次的那一項；找不到或不唯一回 -1。</summary>
    /// <remarks>刻意不用 Contains／StartsWith：「確認分隊任務」與「確認分隊攻略任務」、中止項都長得很像。</remarks>
    public static int FindExactEntry(IReadOnlyList<string> entries, string target)
    {
        var wanted = NormalizeEntry(target);
        if (wanted.Length == 0) return -1;

        var found = -1;
        for (var i = 0; i < entries.Count; i++)
        {
            if (NormalizeEntry(entries[i]) != wanted) continue;
            if (found >= 0) return -1;
            found = i;
        }

        return found;
    }

    public static string NormalizeEntry(string? value) => (value ?? string.Empty).ReplaceLineEndings(string.Empty).Trim();

    /// <summary>任何一項含 U+FFFD＝窗的記憶體正在變動，這一幀不判。</summary>
    public static bool LooksMidUpdate(IReadOnlyList<string> entries)
    {
        foreach (var entry in entries)
        {
            if (entry.Contains('�')) return true;
        }

        return false;
    }

    /// <summary>去掉所有空白字元（含全形空白與換行）。</summary>
    public static string StripWhitespace(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (!char.IsWhiteSpace(ch)) sb.Append(ch);
        }

        return sb.ToString();
    }

    private static string OneLine(string text) => text.ReplaceLineEndings(" ").Trim();

    private static readonly Regex ReturnTail = new(
        @"^\s*([0-9]{1,2})\s*/\s*([0-9]{1,2})\s+([0-9]{1,2})\s*:\s*([0-9]{2})\s*\D?\s*([0-9]{1,3})?",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// 解析派遣確認框（Addon 10522）。<paramref name="fragments"/> 是該列去空白後的固定片段，
    /// 前三段依序是「確定要開始」「嗎？」「預定歸隊時間：」。字串沒有年份：取讓結果最接近
    /// 「現在＋N 小時」的那一年，差超過 <see cref="ReturnToleranceMinutes"/> 就當成看不懂。
    /// </summary>
    public static bool TryParseReturn(
        string? text, IReadOnlyList<string> fragments, DateTime nowLocal, out SquadronReturn result)
    {
        result = default;
        if (string.IsNullOrEmpty(text) || fragments.Count < 3) return false;
        if (LooksMidUpdate([text])) return false;

        var head = text.IndexOf(fragments[0], StringComparison.Ordinal);
        if (head < 0) return false;

        var nameStart = head + fragments[0].Length;
        var nameEnd = text.IndexOf(fragments[1], nameStart, StringComparison.Ordinal);
        if (nameEnd <= nameStart) return false;

        var mission = text[nameStart..nameEnd].ReplaceLineEndings(string.Empty).Trim();
        if (mission.Length == 0) return false;

        var label = text.IndexOf(fragments[2], nameEnd + fragments[1].Length, StringComparison.Ordinal);
        if (label < 0) return false;

        var m = ReturnTail.Match(text[(label + fragments[2].Length)..]);
        if (!m.Success) return false;

        var month = int.Parse(m.Groups[1].Value);
        var day = int.Parse(m.Groups[2].Value);
        var hour = int.Parse(m.Groups[3].Value);
        var minute = int.Parse(m.Groups[4].Value);
        var hours = m.Groups[5].Success ? int.Parse(m.Groups[5].Value) : 0;
        if (month is < 1 or > 12 || day < 1 || hour > 23 || minute > 59) return false;

        var expected = nowLocal.AddHours(hours);
        DateTime? best = null;
        for (var year = nowLocal.Year - 1; year <= nowLocal.Year + 1; year++)
        {
            if (day > DateTime.DaysInMonth(year, month)) continue;

            var candidate = new DateTime(year, month, day, hour, minute, 0, nowLocal.Kind);
            if (best == null || Math.Abs((candidate - expected).Ticks) < Math.Abs((best.Value - expected).Ticks))
                best = candidate;
        }

        if (best == null || Math.Abs((best.Value - expected).TotalMinutes) > ReturnToleranceMinutes) return false;

        result = new SquadronReturn(mission, best.Value, hours);
        return true;
    }
}

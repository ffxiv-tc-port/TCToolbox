using System;
using System.Collections.Generic;
using Lumina.Excel.Sheets;

namespace TCToolbox.Core;

/// <summary>
/// 遊戲原生斜線指令查表（<c>TextCommand</c> 表）。
/// </summary>
/// <remarks>
/// 🔴 <b>指令字面值一律從遊戲自己的表讀出來，不寫死。</b>
/// 台服每一列同時有中文別名與英文別名，而且分佈在四個不同欄位
/// （<c>Alias</c>＝「/小隊頻道」、<c>ShortAlias</c>＝「/隊」、
/// <c>Command</c>＝「/party」、<c>ShortCommand</c>＝「/p」），
/// <b>哪一欄有值是逐列不同的</b>——例如列 3「/戰隊命令」的 <c>ShortAlias</c> 是空的。
/// 寫死任何一種就等於賭那一欄在這一列有值，而賭輸的表現是「指令送出去但遊戲不認得」，
/// 完全沒有錯誤訊息。
/// <para>
/// ⚠️ 這個型別在 ImGui 的 Draw 路徑上會被呼叫，<b>所有讀表都包在 try 裡</b>：
/// 讀不到就回「不知道」，絕不擲例外。
/// </para>
/// </remarks>
public static class TextCommands
{
    /// <summary>
    /// 聊天頻道在 <c>TextCommand</c> 表裡的列號（台服 7.20 離線比對 <c>exd-tc/7.20/TextCommand.csv</c>）。
    /// </summary>
    /// <remarks>
    /// 📌 這裡存的是<b>列號</b>而不是指令字串——列號是資料的身分，字串才是會隨語言／版本變的表現。
    /// 真正要送出去的字面值一律靠 <see cref="Resolve"/> 當場查表。
    /// <para>
    /// ⚠️ 對不到列（台服未開放、或表結構改了）時 <see cref="Resolve"/> 回 <c>null</c>，
    /// 呼叫端就當這個頻道不存在——<b>fail closed，不會退回猜出來的字面值</b>。
    /// </para>
    /// </remarks>
    public static class ChatChannelRows
    {
        /// <summary>默語（/echo）：只有自己看得到，不會送給任何人。</summary>
        public const uint Echo = 116;

        /// <summary>小隊頻道（/party）。</summary>
        public const uint Party = 105;

        /// <summary>團隊頻道（/alliance）。</summary>
        public const uint Alliance = 119;

        /// <summary>說話頻道（/say）。</summary>
        public const uint Say = 102;

        /// <summary>公會頻道（/freecompany）。</summary>
        public const uint FreeCompany = 115;

        /// <summary>喊話頻道（/shout）。</summary>
        public const uint Shout = 103;

        /// <summary>呼喊頻道（/yell）。</summary>
        public const uint Yell = 117;

        /// <summary>新人頻道（/beginner）。</summary>
        public const uint Beginner = 101;

        /// <summary>
        /// 播報功能可選的頻道列號，依「影響範圍由小到大」排。
        /// </summary>
        /// <remarks>
        /// 🔴 <b>第一個必須是 <see cref="Echo"/></b>：它是唯一不會送給任何其他玩家的頻道，
        /// 所以是所有播報功能的預設值。清單順序就是設定 UI 上的順序，
        /// 讓「最安全的選項」永遠在第一個。
        /// <para>
        /// 📌 刻意不收 <c>/tell</c>（需要對象參數）與通訊貝／跨界貝（要先選好第幾個貝，
        /// 而且送錯地方的代價是打擾一整群不相干的人）。
        /// </para>
        /// </remarks>
        public static readonly uint[] AnnounceChoices =
        [
            Echo, Party, Alliance, Say, FreeCompany, Shout, Yell, Beginner,
        ];
    }

    /// <summary>遊戲認得的所有斜線指令字面值（含中英長短四種寫法），第一次用到才建。</summary>
    private static HashSet<string>? knownCommands;

    /// <summary>
    /// 查出某一列 <c>TextCommand</c> 實際可以送出去的指令字面值。
    /// </summary>
    /// <returns>找不到（列不存在、四個欄位全空、或讀表失敗）時回 <c>null</c>。</returns>
    /// <remarks>
    /// 📌 優先序是 <c>ShortCommand</c> → <c>Command</c> → <c>ShortAlias</c> → <c>Alias</c>：
    /// 英文短別名（/p）最不容易在指令列上與其他東西混淆，中文別名放最後當保險。
    /// </remarks>
    public static string? Resolve(uint rowId)
    {
        try
        {
            var row = Svc.Data.GetExcelSheet<TextCommand>().GetRowOrDefault(rowId);
            if (row == null) return null;

            return FirstUsable(
                row.Value.ShortCommand.ExtractText(),
                row.Value.Command.ExtractText(),
                row.Value.ShortAlias.ExtractText(),
                row.Value.Alias.ExtractText());
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, $"[TextCommands] 讀取 TextCommand 第 {rowId} 列失敗");
            return null;
        }
    }

    /// <summary>
    /// 某一列 <c>TextCommand</c> 拿來顯示給人看的名字（中文長別名優先）。
    /// </summary>
    /// <returns>查不到時回 <c>null</c>。</returns>
    public static string? DisplayName(uint rowId)
    {
        try
        {
            var row = Svc.Data.GetExcelSheet<TextCommand>().GetRowOrDefault(rowId);
            if (row == null) return null;

            var name = FirstUsable(
                row.Value.Alias.ExtractText(),
                row.Value.Command.ExtractText(),
                row.Value.ShortAlias.ExtractText(),
                row.Value.ShortCommand.ExtractText());

            return name?.TrimStart('/');
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, $"[TextCommands] 讀取 TextCommand 第 {rowId} 列的顯示名失敗");
            return null;
        }
    }

    /// <summary>
    /// 這個字面值是不是遊戲自己認得的原生指令。
    /// </summary>
    /// <remarks>
    /// ⚠️ 回 <c>false</c> <b>不代表指令是錯的</b>——外掛註冊的指令（/snd、/vnav…）不在這張表裡。
    /// 呼叫端要把「遊戲不認得」與「外掛註冊的」分開處理，見
    /// <see cref="IsRegisteredPluginCommand"/>。
    /// </remarks>
    public static bool IsKnownGameCommand(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;

        var set = knownCommands;
        if (set == null)
        {
            set = [];
            try
            {
                foreach (var row in Svc.Data.GetExcelSheet<TextCommand>())
                {
                    AddIfCommand(set, row.Alias.ExtractText());
                    AddIfCommand(set, row.ShortAlias.ExtractText());
                    AddIfCommand(set, row.Command.ExtractText());
                    AddIfCommand(set, row.ShortCommand.ExtractText());
                }
            }
            catch (Exception ex)
            {
                // 讀表失敗就留空集合：所有指令都會被標成「不認得」（只是提示，不擋執行）。
                Svc.Log.Warning(ex, "[TextCommands] 建立原生指令清單失敗，指令提示將全部顯示為未知");
            }

            knownCommands = set;
            Svc.Log.Information($"[TextCommands] 原生斜線指令字面值共 {set.Count} 種（含中英長短別名）。");
        }

        return set.Contains(token.Trim());
    }

    /// <summary>這個字面值是不是某個已載入外掛（含本外掛）向 Dalamud 註冊的指令。</summary>
    public static bool IsRegisteredPluginCommand(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;

        try
        {
            return Svc.Commands.Commands.ContainsKey(token.Trim());
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, "[TextCommands] 讀取已註冊外掛指令清單失敗");
            return false;
        }
    }

    /// <summary>取一行指令的第一個詞（也就是指令本身，不含參數）。</summary>
    public static string FirstToken(string line)
    {
        line = line.Trim();
        if (line.Length == 0) return string.Empty;

        var space = line.IndexOf(' ');
        return space < 0 ? line : line[..space];
    }

    private static void AddIfCommand(HashSet<string> set, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;

        var trimmed = value.Trim();
        if (trimmed.StartsWith('/')) set.Add(trimmed);
    }

    /// <summary>回傳第一個「非空且以 / 開頭」的候選；全都不合格時回 <c>null</c>。</summary>
    private static string? FirstUsable(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;

            var trimmed = candidate.Trim();
            if (trimmed.StartsWith('/')) return trimmed;
        }

        return null;
    }
}

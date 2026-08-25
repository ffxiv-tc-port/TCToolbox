using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 用指令把任務搜尋器開到指定的副本／隨機任務並登記（選取）那一項。
/// </summary>
/// <remarks>
/// 參考 DailyRoutines <c>ContentFinderCommand</c> 重寫，但<b>刻意縮小到「開啟＋選取」</b>：
/// <para>
/// 🔴 <b>指令名不用 <c>/pdrduty</c>。</b>那是 DailyRoutines 的指令，使用者機器上還裝著 DR，
/// 兩邊都註冊同一個名字時第二個會靜默失敗（誰先載入誰贏，之後那條指令的行為變成賭運氣）。
/// 一律用 TC Toolbox 自己的前綴 <c>/tcduty</c>。
/// </para>
/// <para>
/// 🔴 <b>只呼叫 <c>AgentContentsFinder::OpenRegularDuty</c> / <c>OpenRouletteDuty</c>
/// ——「開啟搜尋器並把這一項選起來」，不送報名封包、不自動排隊。</b>
/// DR 原版走的是 <c>ContentsFinderHelper.RequestDuty*</c>（等於幫你按下「參加」把整隊送進佇列），
/// 那條路要靠一支 <c>ExecuteCommand</c> 呼叫點特徵碼，台服有 9 個近乎相同的函式、抓錯一個是靜默災難。
/// 這裡整條避開：使用者自己還要在搜尋器上按「參加」，跟手動選好副本一模一樣，只是省掉翻頁找。
/// </para>
/// <para>
/// 🔴 <b>對不到就不開，也不猜。</b>名稱比對到多個副本時列出前幾個讓使用者自己挑，
/// 絕不拿「第一個 Contains 命中」去賭——那正是 DR 原版的 <c>FirstOrDefault</c> 會做的事。
/// </para>
/// </remarks>
public sealed unsafe class ContentFinderCommand : TcModule
{
    public override string InternalName => "ContentFinderCommand";
    public override string DisplayName => "指令開啟任務搜尋器";

    public override string Description =>
        "用 /tcduty 指令把任務搜尋器開到指定副本並選取，省掉自己翻頁找。" +
        "可用副本名稱或編號；加上 r 開頭則是隨機任務（輪盤）。" +
        "只會開啟並選取——不會替你按「參加」、不會自動排隊。";

    public override ModuleCategory Category => ModuleCategory.Combat;

    /// <inheritdoc/>
    /// <remarks>開著但不下指令＝遊戲行為完全不變。</remarks>
    public override bool IsManualTrigger => true;

    public override bool HasConfigUI => true;

    private const string Command = "/tcduty";
    private const string CommandAlias = "/tcdf";

    private ContentFinderCommandConfig Config => Plugin.Instance.Config.ContentFinderCommand;

    /// <summary>指令要開的是一般副本還是隨機任務。</summary>
    private enum DutyKind
    {
        Normal,
        Roulette,
    }

    protected override void OnEnable()
    {
        Svc.Commands.AddHandler(Command, new CommandInfo(OnCommand)
        {
            HelpMessage = "開啟任務搜尋器到指定副本並選取（用法：/tcduty <副本名或編號>；隨機任務用 /tcduty r <名稱>）",
        });

        Svc.Commands.AddHandler(CommandAlias, new CommandInfo(OnCommand)
        {
            HelpMessage = $"{Command} 的簡寫",
            ShowInHelp = false,
        });
    }

    protected override void OnDisable()
    {
        Svc.Commands.RemoveHandler(Command);
        Svc.Commands.RemoveHandler(CommandAlias);
    }

    private void OnCommand(string command, string arguments)
    {
        try
        {
            HandleCommand(arguments);
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, $"[{InternalName}] 處理 {command} 指令時發生例外");
            Svc.Chat.PrintError("[TC Toolbox] 處理指令時發生錯誤，詳見記錄。");
        }
    }

    private void HandleCommand(string arguments)
    {
        var tokens = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            PrintUsage();
            return;
        }

        var kind = DutyKind.Normal;
        var queryStart = 0;

        var head = tokens[0].ToLowerInvariant();
        if (head is "n" or "normal")
        {
            kind = DutyKind.Normal;
            queryStart = 1;
        }
        else if (head is "r" or "roulette")
        {
            kind = DutyKind.Roulette;
            queryStart = 1;
        }

        var query = string.Join(' ', tokens.Skip(queryStart)).Trim();
        if (query.Length == 0)
        {
            PrintUsage();
            return;
        }

        if (!TryResolve(kind, query, out var id, out var name, out var error))
        {
            Svc.Chat.PrintError($"[TC Toolbox] {error}");
            return;
        }

        var agent = AgentContentsFinder.Instance();
        if (agent == null)
        {
            Svc.Chat.PrintError("[TC Toolbox] 現在還不能開啟任務搜尋器（介面尚未就緒）。");
            return;
        }

        switch (kind)
        {
            case DutyKind.Roulette:
                agent->OpenRouletteDuty((byte)id);
                break;
            default:
                agent->OpenRegularDuty(id);
                break;
        }

        Svc.Log.Information(
            $"[{InternalName}] 使用者以指令開啟{(kind == DutyKind.Roulette ? "隨機任務" : "副本")} {id}「{name}」（查詢字串：{query}）");

        if (Config.NotifyOnOpen)
            Svc.Chat.Print($"[TC Toolbox] 已開啟任務搜尋器到「{name}」，請自行按「參加」。");
    }

    /// <summary>把使用者輸入的字串解析成一個確定的副本／輪盤列號。</summary>
    /// <remarks>
    /// 🔑 先試純數字＝直接當列號（先驗證那一列真的存在，不然開了也是空的）；
    /// 再試名稱：先要求「去空白後完全相等」，唯一命中才用；退而求其次才用 Contains，
    /// 而且 Contains 命中<b>多於一個時一律不猜</b>，列出來讓使用者自己指定。
    /// </remarks>
    private static bool TryResolve(DutyKind kind, string query, out uint id, out string name, out string error)
    {
        id = 0;
        name = string.Empty;
        error = string.Empty;

        // ① 純數字＝列號。
        if (uint.TryParse(query, out var directId))
        {
            if (TryGetNameById(kind, directId, out name))
            {
                id = directId;
                return true;
            }

            error = kind == DutyKind.Roulette
                        ? $"找不到編號 {directId} 的隨機任務。"
                        : $"找不到編號 {directId} 的副本。";
            return false;
        }

        // ② 名稱比對。
        var needle = query.Replace(" ", string.Empty);
        var exact = new List<(uint Id, string Name)>();
        var partial = new List<(uint Id, string Name)>();

        foreach (var (rowId, rowName) in EnumerateNames(kind))
        {
            if (rowName.Length == 0) continue;

            var compact = rowName.Replace(" ", string.Empty);
            if (string.Equals(compact, needle, StringComparison.OrdinalIgnoreCase))
                exact.Add((rowId, rowName));
            else if (compact.Contains(needle, StringComparison.OrdinalIgnoreCase))
                partial.Add((rowId, rowName));
        }

        // 完全相符優先；唯一才採用。
        if (exact.Count == 1)
        {
            (id, name) = exact[0];
            return true;
        }

        if (exact.Count > 1)
        {
            error = $"「{query}」對到多個同名項目（編號 {string.Join("、", exact.Take(5).Select(e => e.Id))}），請直接用編號指定。";
            return false;
        }

        if (partial.Count == 1)
        {
            (id, name) = partial[0];
            return true;
        }

        if (partial.Count > 1)
        {
            var preview = string.Join("、", partial.Take(5).Select(e => $"{e.Name}({e.Id})"));
            error = partial.Count > 5
                        ? $"「{query}」對到 {partial.Count} 個副本：{preview}…；請補完整名稱或用編號。"
                        : $"「{query}」對到多個副本：{preview}；請補完整名稱或用編號。";
            return false;
        }

        error = kind == DutyKind.Roulette
                    ? $"找不到名稱含「{query}」的隨機任務。"
                    : $"找不到名稱含「{query}」的副本。";
        return false;
    }

    private static IEnumerable<(uint Id, string Name)> EnumerateNames(DutyKind kind)
    {
        if (kind == DutyKind.Roulette)
        {
            foreach (var row in Svc.Data.GetExcelSheet<ContentRoulette>())
                yield return (row.RowId, row.Name.ExtractText());
        }
        else
        {
            foreach (var row in Svc.Data.GetExcelSheet<ContentFinderCondition>())
                yield return (row.RowId, row.Name.ExtractText());
        }
    }

    private static bool TryGetNameById(DutyKind kind, uint id, out string name)
    {
        name = string.Empty;

        if (kind == DutyKind.Roulette)
        {
            var row = Svc.Data.GetExcelSheet<ContentRoulette>().GetRowOrDefault(id);
            if (row == null) return false;
            name = row.Value.Name.ExtractText();
            return true;
        }

        var cfc = Svc.Data.GetExcelSheet<ContentFinderCondition>().GetRowOrDefault(id);
        if (cfc == null) return false;

        var text = cfc.Value.Name.ExtractText();
        // ContentFinderCondition 有一堆名稱為空的佔位列（未開放／非任務），這些拿去開也是空的。
        if (string.IsNullOrWhiteSpace(text)) return false;

        name = text;
        return true;
    }

    private void PrintUsage()
    {
        Svc.Chat.Print("[TC Toolbox] 用法：");
        Svc.Chat.Print($"　{Command} <副本名稱或編號>　— 開一般副本，例：{Command} 水晶塔");
        Svc.Chat.Print($"　{Command} r <名稱或編號>　— 開隨機任務（輪盤），例：{Command} r 冒險者");
        if (Config.NotifyOnOpen)
            Svc.Chat.Print("　開啟後仍需自行按「參加」，不會自動排隊。");
    }

    public override void DrawConfig()
    {
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
        ImGui.TextDisabled(
            $"{Command}（或 {CommandAlias}）把任務搜尋器開到指定副本並選取。\n" +
            "· 一般副本：/tcduty <名稱或編號>\n" +
            "· 隨機任務（輪盤）：/tcduty r <名稱或編號>\n" +
            "名稱是模糊比對；對到多個時不會亂猜，會請你補完整名稱或改用編號。\n" +
            "只會開啟並選取，不會替你按「參加」，也不會自動排隊。");
        ImGui.PopTextWrapPos();

        ImGui.Spacing();

        var notify = Config.NotifyOnOpen;
        if (ImGui.Checkbox("開啟後在聊天欄提示", ref notify))
        {
            Config.NotifyOnOpen = notify;
            Plugin.Instance.Config.Save();
        }
    }
}

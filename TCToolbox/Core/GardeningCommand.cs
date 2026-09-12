using System;
using System.Linq;
using Dalamud.Game.Command;
using TCToolbox.Modules;

namespace TCToolbox.Core;

/// <summary>
/// 自動園圃作業的聊天指令（<c>/tcgarden</c>，別名 <c>/tcgardens</c>）。
/// </summary>
/// <remarks>
/// 🔴 <b>刻意註冊在外掛層而不是模組的 <c>OnEnable</c> 裡。</b>
/// 註冊在模組裡的話，模組一關指令就跟著消失，快捷列上那顆巨集按下去得到的是遊戲自己的
/// 「無此指令」——那句話不會告訴任何人「是模組被關掉了」。
/// </remarks>
public sealed class GardeningCommand : IDisposable
{
    public const string Command = "/tcgarden";

    /// <summary>別名。單複數兩種都收，猜錯一個字母不該讓快捷列失效。</summary>
    public const string CommandAlias = "/tcgardens";

    public GardeningCommand()
    {
        var info = new CommandInfo(OnCommand)
        {
            HelpMessage =
                $"園圃自動整理：{Command}＝跑一輪；"
                + $"{Command} ui／stop／status／harvest／tend／fertilize／plant",
        };

        Svc.Commands.AddHandler(Command, info);

        // 📌 別名另外 new 一個 CommandInfo 是因為 ShowInHelp 是實例上的屬性：
        //    共用同一個實例的話，兩個名字要嘛都出現在 /xlhelp、要嘛都不出現。
        Svc.Commands.AddHandler(CommandAlias, new CommandInfo(OnCommand)
        {
            HelpMessage = info.HelpMessage,
            ShowInHelp = false,
        });
    }

    private static AutoGardensWork? Module =>
        Plugin.Instance?.Modules.OfType<AutoGardensWork>().FirstOrDefault();

    /// <summary>
    /// 指令處理常式。
    /// </summary>
    /// <remarks>
    /// ⚠️ 指令處理常式跑在遊戲的主執行緒上（Dalamud 的聊天指令派送），
    /// 所以下面同步讀取原生記憶體（背包、物件表）是安全的。
    /// </remarks>
    private static void OnCommand(string command, string arguments)
    {
        var module = Module;
        if (module == null)
        {
            Svc.Chat.PrintError("[TC Toolbox] 找不到自動園圃作業模組（這是內部錯誤，請回報）。");
            return;
        }

        var arg = arguments.Trim().ToLowerInvariant();

        switch (arg)
        {
            case "":
            case "auto":
                Report(module, module.RunBatchFromCommand(AutoGardensWork.GardenAction.Auto));
                return;

            case "ui":
            case "config":
                Plugin.Instance.OpenMainWindow();
                if (!module.IsEnabled)
                    Svc.Chat.Print("[TC Toolbox] 自動園圃作業目前是關閉的，請在「部隊 · 生活」分頁把它打開。");
                return;

            case "stop":
                if (!module.IsEnabled)
                {
                    PrintDisabled();
                    return;
                }

                if (!module.IsBusy)
                {
                    Svc.Chat.Print("[TC Toolbox] 園圃批次目前沒有在跑。");
                    return;
                }

                module.StopBatch();
                Svc.Chat.Print(
                    $"[TC Toolbox] 已停止園圃批次（完成 {module.DoneCount} 格、跳過 {module.SkippedCount} 格）。");
                return;

            case "status":
                PrintStatus(module);
                return;

            case "harvest":
                Report(module, module.RunBatchFromCommand(AutoGardensWork.GardenAction.Harvest));
                return;

            case "tend":
                Report(module, module.RunBatchFromCommand(AutoGardensWork.GardenAction.Tend));
                return;

            case "fertilize":
                Report(module, module.RunBatchFromCommand(AutoGardensWork.GardenAction.Fertilize));
                return;

            case "plant":
                Report(module, module.RunBatchFromCommand(AutoGardensWork.GardenAction.Plant));
                return;

            default:
                Svc.Chat.PrintError($"[TC Toolbox] 認不得的參數「{arguments.Trim()}」。");
                PrintUsage();
                return;
        }
    }

    /// <summary>把「沒有開始」的理由印出來；空字串＝真的開始了，什麼都不印。</summary>
    /// <remarks>
    /// 🔴 「是不是因為模組沒啟用」一律<b>直接問 <see cref="TcModule.IsEnabled"/></b>，
    /// 不去比對回傳理由的字串——那句話是給人看的，改一個字這裡就會靜默走錯分支。
    /// </remarks>
    private static void Report(AutoGardensWork module, string reason)
    {
        if (reason.Length == 0) return;

        if (!module.IsEnabled)
        {
            PrintDisabled();
            return;
        }

        Svc.Chat.PrintError($"[TC Toolbox] {reason}");
    }

    private static void PrintDisabled() =>
        Svc.Chat.PrintError(
            "[TC Toolbox] 自動園圃作業模組沒有啟用，這個指令不會做任何事。"
            + $"請用 {Command} ui 打開設定視窗，在「部隊 · 生活」分頁把它開起來。");

    private static void PrintUsage()
    {
        Svc.Chat.Print($"[TC Toolbox] {Command}（不帶參數）＝跑一輪「自動整理」。");
        Svc.Chat.Print($"[TC Toolbox] {Command} ui＝開設定視窗；status＝看目前狀態；stop＝停止正在跑的批次。");
        Svc.Chat.Print($"[TC Toolbox] {Command} harvest／tend／fertilize／plant＝只跑那一種單一動作批次。");
    }

    private static void PrintStatus(AutoGardensWork module)
    {
        if (!module.IsEnabled)
        {
            PrintDisabled();
            return;
        }

        var config = Plugin.Instance.Config.GardensWork;

        Svc.Chat.Print(
            module.IsBusy
                ? $"[TC Toolbox] 園圃：正在跑「{module.CurrentStepName}」"
                  + $"（完成 {module.DoneCount} 格、跳過 {module.SkippedCount} 格）。"
                : "[TC Toolbox] 園圃：目前沒有批次在跑。");

        Svc.Chat.Print(
            config.AutoLoopEnabled
                ? $"[TC Toolbox] 園圃：自動重跑開著，間隔約 {config.AutoLoopIntervalSeconds} 秒。"
                : "[TC Toolbox] 園圃：自動重跑關著，只有按按鈕或下指令才會動。");

        // 🔴 缺料要講在前面：它是「開著卻什麼都沒發生」最常見的原因。
        var shortage = module.MaterialShortage;
        if (shortage.Length > 0)
            Svc.Chat.PrintError($"[TC Toolbox] 園圃缺料：{shortage}。補齊之前那幾格不會被處理。");

        // 「不知道」也要看得見：還沒跑過就明講還沒跑過，不要印一行空的彙總。
        var summary = module.LastSummary;
        Svc.Chat.Print(summary.Length > 0
            ? $"[TC Toolbox] 園圃上次結果：{summary}"
            : "[TC Toolbox] 園圃：這次載入之後還沒跑過任何一輪。");

        var unavailable = module.GetUnavailableReason();
        if (unavailable.Length > 0)
            Svc.Chat.Print($"[TC Toolbox] 園圃：現在這裡不能作業（{unavailable}）。");
    }

    public void Dispose()
    {
        Svc.Commands.RemoveHandler(Command);
        Svc.Commands.RemoveHandler(CommandAlias);
    }
}

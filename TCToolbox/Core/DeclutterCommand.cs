using System;
using System.Linq;
using Dalamud.Game.Command;
using TCToolbox.Modules;

namespace TCToolbox.Core;

/// <summary>
/// 背包一鍵清倉的聊天指令（<c>/tcdeclutter</c>）。
/// </summary>
/// <remarks>
/// 🔴 <b>刻意註冊在外掛層而不是模組的 <c>OnEnable</c> 裡</b>（同 <see cref="GardeningCommand"/>）：
/// 註冊在模組裡的話，模組一關指令就跟著消失，快捷列上那顆巨集按下去得到的是遊戲自己的
/// 「無此指令」——那句話不會告訴任何人「是模組被關掉了」。
/// 📌 只做「開始／停止」。批次的外部入口<b>刻意不開</b>（同 <see cref="GardeningIpc"/>）：
/// 搬背包的東西一律要有人按下去才會動。
/// </remarks>
public sealed class DeclutterCommand : IDisposable
{
    public const string Command = "/tcdeclutter";

    public DeclutterCommand()
    {
        Svc.Commands.AddHandler(Command, new CommandInfo(OnCommand)
        {
            HelpMessage =
                $"背包一鍵清倉：{Command}＝開始一輪，正在跑時再打一次＝停止；"
                + $"{Command} ui／stop／status",
        });
    }

    private static InventoryDeclutter? Module =>
        Plugin.Instance?.Modules.OfType<InventoryDeclutter>().FirstOrDefault();

    /// <remarks>⚠️ 跑在遊戲主執行緒上（Dalamud 的聊天指令派送），所以直接設旗標是安全的。</remarks>
    private static void OnCommand(string command, string arguments)
    {
        var module = Module;
        if (module == null)
        {
            Svc.Chat.PrintError("[TC Toolbox] 找不到背包一鍵清倉模組（這是內部錯誤，請回報）。");
            return;
        }

        var arg = arguments.Trim().ToLowerInvariant();

        switch (arg)
        {
            case "":
            case "toggle":
                if (!module.IsEnabled)
                {
                    PrintDisabled();
                    return;
                }

                module.ToggleRoundExternally($"指令 {Command}");
                return;

            case "ui":
            case "config":
                Plugin.Instance.OpenMainWindow();
                if (!module.IsEnabled)
                    Svc.Chat.Print("[TC Toolbox] 背包一鍵清倉目前是關閉的，請在「背包 · 裝備」分頁把它打開。");
                return;

            case "stop":
                if (!module.IsEnabled)
                {
                    PrintDisabled();
                    return;
                }

                if (!module.StopRoundExternally($"指令 {Command} stop"))
                    Svc.Chat.Print("[TC Toolbox] 一鍵清倉目前沒有在跑。");
                return;

            case "status":
                PrintStatus(module);
                return;

            default:
                Svc.Chat.PrintError($"[TC Toolbox] 認不得的參數「{arguments.Trim()}」。");
                PrintUsage();
                return;
        }
    }

    /// <remarks>
    /// 🔴 「是不是因為模組沒啟用」一律直接問 <see cref="TcModule.IsEnabled"/>：
    /// 模組關著時 <c>Framework.Update</c> 沒有訂閱，旗標設了也沒有人會去看。
    /// </remarks>
    private static void PrintDisabled() =>
        Svc.Chat.PrintError(
            "[TC Toolbox] 背包一鍵清倉模組沒有啟用，這個指令不會做任何事。"
            + $"請用 {Command} ui 打開設定視窗，在「背包 · 裝備」分頁把它開起來。");

    private static void PrintUsage()
    {
        Svc.Chat.Print($"[TC Toolbox] {Command}（不帶參數）＝開始一輪；正在跑的時候再打一次就是停止。");
        Svc.Chat.Print($"[TC Toolbox] {Command} ui＝開設定視窗；status＝看目前狀態；stop＝停止。");
    }

    private static void PrintStatus(InventoryDeclutter module)
    {
        if (!module.IsEnabled)
        {
            PrintDisabled();
            return;
        }

        Svc.Chat.Print(
            module.IsRoundRunning
                ? $"[TC Toolbox] 一鍵清倉：正在跑（{module.RoundProgressText}）。"
                : "[TC Toolbox] 一鍵清倉：目前沒有在跑。");

        var config = Plugin.Instance.Config.Declutter;
        Svc.Chat.Print(
            $"[TC Toolbox] 一鍵清倉段落：鞍囊重複道具{(config.RunSaddlebag ? "開" : "關")}、"
            + $"收藏櫃可收納裝備{(config.RunCabinet ? "開" : "關")}；"
            + $"熱鍵 {HotkeyUi.Describe(config.Hotkey)}。");

        // 「不知道」也要看得見：還沒跑過就明講還沒跑過，不要印一行空的彙總。
        var summary = module.LastRoundSummary;
        Svc.Chat.Print(summary.Length > 0
            ? $"[TC Toolbox] 一鍵清倉上次結果：{summary}"
            : "[TC Toolbox] 一鍵清倉：這次載入之後還沒跑過任何一輪。");
    }

    public void Dispose() => Svc.Commands.RemoveHandler(Command);
}

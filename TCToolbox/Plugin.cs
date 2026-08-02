using System.Collections.Generic;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using TCToolbox.Core;
using TCToolbox.Modules;
using TCToolbox.Windows;

namespace TCToolbox;

public sealed class Plugin : IDalamudPlugin
{
    public const string Command = "/tctoolbox";

    public static Plugin Instance { get; private set; } = null!;

    public Configuration Config { get; }

    public List<TcModule> Modules { get; } = [];

    public WindowSystem WindowSystem { get; } = new("TCToolbox");

    private readonly MainWindow mainWindow;

    /// <summary>自動園圃作業的 IPC 端點（供本機腳本細項操作用；與模組開關無關，恆常註冊）。</summary>
    private readonly GardeningIpc gardeningIpc;

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        Instance = this;
        pluginInterface.Create<Svc>();

        Config = Svc.PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        Modules.Add(new AutoFCWSDeliver());
        Modules.Add(new AutoGysahlGreens());
        Modules.Add(new AutoQTE());
        Modules.Add(new AutoCountPlayers());
        Modules.Add(new AutoGardensWork());
        Modules.Add(new AutoAntiAfk());
        Modules.Add(new AutoConstantlyClick());
        Modules.Add(new AutoPlayerCommend());
        Modules.Add(new OptimizedDutyFinderSetting());
        Modules.Add(new AutoHideBanners());
        Modules.Add(new AutoMaterialize());
        Modules.Add(new AutoRetarget());
        Modules.Add(new MarkerInPartyList());
        Modules.Add(new AutoBlockTitleMovie());
        Modules.Add(new OptimizedEnemyList());
        Modules.Add(new AutoInventoryTransfer());
        Modules.Add(new OptimizedTargetInfo());
        Modules.Add(new CustomDeliveriesOverview());
        Modules.Add(new AutoHideNeedlessPopups());
        Modules.Add(new OptimizedFreeShop());
        Modules.Add(new AutoRefreshPartyFinder());

        foreach (var module in Modules)
        {
            if (Config.EnabledModules.Contains(module.InternalName))
                module.Enable();
        }

        gardeningIpc = new GardeningIpc();

        mainWindow = new MainWindow(this);
        WindowSystem.AddWindow(mainWindow);

        Svc.PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        Svc.PluginInterface.UiBuilder.OpenConfigUi += mainWindow.Toggle;
        Svc.PluginInterface.UiBuilder.OpenMainUi += mainWindow.Toggle;

        Svc.Commands.AddHandler(Command, new CommandInfo(OnCommand)
        {
            HelpMessage = "開啟 TC Toolbox 模組設定視窗",
        });
    }

    public void SetModuleEnabled(TcModule module, bool enabled)
    {
        if (enabled)
        {
            module.Enable();
            if (module.IsEnabled)
                Config.EnabledModules.Add(module.InternalName);
        }
        else
        {
            module.Disable();
            Config.EnabledModules.Remove(module.InternalName);
        }

        Config.Save();
    }

    /// <summary>開關主視窗。給模組用（例如 DTR 的右鍵動作）。</summary>
    public void ToggleMainWindow() => mainWindow.Toggle();

    private void OnCommand(string command, string args) => mainWindow.Toggle();

    public void Dispose()
    {
        Svc.Commands.RemoveHandler(Command);

        gardeningIpc.Dispose();

        Svc.PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        Svc.PluginInterface.UiBuilder.OpenConfigUi -= mainWindow.Toggle;
        Svc.PluginInterface.UiBuilder.OpenMainUi -= mainWindow.Toggle;
        WindowSystem.RemoveAllWindows();

        foreach (var module in Modules)
            module.Disable();
    }
}

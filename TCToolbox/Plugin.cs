using System;
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

        // 🔴 這裡的順序就是主視窗上的顯示順序：分類分頁與「全部」分頁都直接照 Modules 的
        //    順序畫，沒有第二份排序表。要調整某個模組在頁面上的位置就搬這裡的行，
        //    但「分組」必須和該模組的 Category 一致，否則分類分頁裡會出現插隊的模組。
        // 📌 註冊順序不影響行為：模組彼此獨立，唯一的跨模組互動
        //    （AutoRequestItemSubmit 讓路給 AutoFCWSDeliver）是執行期查 IsEnabled，與註冊順序無關。

        // ① 背包 · 裝備
        Modules.Add(new AutoInventoryTransfer());
        Modules.Add(new AutoMerge());
        Modules.Add(new CurrencyCapAlert());
        Modules.Add(new OpenAllCoffers());
        Modules.Add(new AutoMateriaRetrieveAll());
        Modules.Add(new AutoMaterialize());
        Modules.Add(new MoveGearsNotInSet());
        Modules.Add(new OptimizedFreeCompanyChest());
        Modules.Add(new ShopDefaults());
        Modules.Add(new OptimizedFreeShop());
        Modules.Add(new CopyItemNameContextMenu());
        Modules.Add(new HuijiWikiContextMenu());
        Modules.Add(new GlamourSetRetrieve());
        Modules.Add(new GlamourDuplicateCleanup());
        Modules.Add(new GlamourArmoireCleanup());
        Modules.Add(new TradeAllCollectables());

        // ② 戰鬥 · 小隊
        Modules.Add(new OptimizedEnemyList());
        Modules.Add(new OptimizedTargetInfo());
        Modules.Add(new AutoRetarget());
        Modules.Add(new AutoRefocus());
        Modules.Add(new MarkerInPartyList());
        Modules.Add(new AutoPlayerCommend());
        Modules.Add(new AutoClaimPVPRewards());
        Modules.Add(new OptimizedDutyFinderSetting());
        Modules.Add(new WeeklyBingoClickToOpen());
        Modules.Add(new AutoRefreshPartyFinder());
        Modules.Add(new PFPageSizeCustomize());
        Modules.Add(new FateTracker());

        // ③ 部隊 · 生活
        Modules.Add(new AutoFCWSDeliver());
        Modules.Add(new AutoGardensWork());
        Modules.Add(new AutoGysahlGreens());
        Modules.Add(new CustomDeliveriesOverview());
        Modules.Add(new AutoCustomDeliveryResult());
        Modules.Add(new AutoRequestItemSubmit());
        Modules.Add(new ARSwitcher());

        // ④ 介面 · 雜項
        Modules.Add(new AutoHideBanners());
        Modules.Add(new AutoHideNeedlessPopups());
        Modules.Add(new AutoBlockTitleMovie());
        Modules.Add(new OptimizedInteraction());
        Modules.Add(new AutoQuestAccept());
        Modules.Add(new AutoQTE());
        Modules.Add(new AutoConstantlyClick());
        Modules.Add(new AutoAntiAfk());
        Modules.Add(new AutoIgnoreLoginLock());
        Modules.Add(new AutoCountPlayers());
        Modules.Add(new ClickToMove());
        Modules.Add(new FlagCommands());

        foreach (var module in Modules)
        {
            if (Config.EnabledModules.Contains(module.InternalName))
                module.Enable();
        }

        LogModuleState();

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

    /// <summary>
    /// 啟動時把模組狀態寫進記錄。
    /// 🔴 **一律 <c>Information</c> 級**：使用者跑 LogLevel 2，Debug／Verbose 完全收不到，
    /// 而「哪些模組是開的」是事後看實機記錄時唯一無法從別處推得的資訊
    /// （模組啟用時本來一行都不寫，2026-08-06 的調查就是卡在這裡）。
    /// <para>
    /// 每一行都帶「已啟用模組」這個關鍵字，所以 <c>grep "已啟用模組"</c> 一次就能把整份清單撈出來，
    /// 不必先找到開頭那行再往下數。
    /// </para>
    /// <para>
    /// ⚠️ 順便報告兩種靜默狀況，它們都不會有別的徵兆：
    /// <list type="bullet">
    /// <item>設定裡開著、但 <see cref="TcModule.Enable"/> 失敗的（例外本身是 Error 級，
    /// 但「所以最後到底幾個是開的」只有這裡看得到）。</item>
    /// <item>設定裡有、這一版卻已經不存在的模組名（改名／移除的殘留）。
    /// **只報告不清除** —— 使用者在版本之間來回時清掉就回不來了。</item>
    /// </list>
    /// </para>
    /// </summary>
    private void LogModuleState()
    {
        var known = new HashSet<string>();
        var enabled = new List<string>();
        var failed = new List<string>();

        foreach (var module in Modules)
        {
            known.Add(module.InternalName);
            if (!Config.EnabledModules.Contains(module.InternalName)) continue;

            // Enable() 內部把例外吃掉並改寫 IsEnabled，所以「設定裡開著」≠「真的開起來了」。
            (module.IsEnabled ? enabled : failed).Add(module.InternalName);
        }

        var unknown = new List<string>();
        foreach (var name in Config.EnabledModules)
        {
            if (!known.Contains(name)) unknown.Add(name);
        }

        Svc.Log.Information(
            $"[TCToolbox] 已啟用模組 {enabled.Count}/{Modules.Count}"
            + (failed.Count > 0 ? $"，啟用失敗 {failed.Count}" : string.Empty)
            + (unknown.Count > 0 ? $"，設定檔中有 {unknown.Count} 個不存在的模組名" : string.Empty));

        if (enabled.Count == 0)
        {
            Svc.Log.Information("[TCToolbox] 已啟用模組 （無）—— 目前所有模組都是關閉狀態。");
        }
        else
        {
            // 二十幾個名字擠成一行會長到難讀，切成每行 6 個；行首的序號範圍讓人一眼看出有沒有漏行。
            const int perLine = 6;
            for (var i = 0; i < enabled.Count; i += perLine)
            {
                var slice = enabled.GetRange(i, Math.Min(perLine, enabled.Count - i));
                Svc.Log.Information(
                    $"[TCToolbox] 已啟用模組 [{i + 1}-{i + slice.Count}/{enabled.Count}] {string.Join("、", slice)}");
            }
        }

        if (failed.Count > 0)
            Svc.Log.Information($"[TCToolbox] 啟用失敗模組 {failed.Count}：{string.Join("、", failed)}（例外內容見上方 Error 記錄）");

        if (unknown.Count > 0)
            Svc.Log.Information($"[TCToolbox] 設定檔中不存在的模組名 {unknown.Count}：{string.Join("、", unknown)}（保留不動）");

        // 📌 只在有釘選時才寫，沒用這個功能的人不會多出一行雜訊。
        // 「常用分頁是空的」有兩種成因（沒釘過／釘的模組名這一版不存在），這一行讓它們分得開。
        if (Config.FavoriteModules.Count > 0)
        {
            var liveFavorites = 0;
            foreach (var name in Config.FavoriteModules)
            {
                if (known.Contains(name)) liveFavorites++;
            }

            Svc.Log.Information(
                $"[TCToolbox] 常用模組 {liveFavorites}/{Config.FavoriteModules.Count} 個在這一版存在："
                + string.Join("、", Config.FavoriteModules));
        }
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

    /// <summary>這個模組有沒有被釘選成「常用」。</summary>
    public bool IsFavorite(TcModule module) => Config.FavoriteModules.Contains(module.InternalName);

    /// <summary>
    /// 釘選／取消釘選一個模組。
    /// </summary>
    /// <remarks>
    /// 📌 純顯示用途，<b>與模組的啟用狀態完全無關</b>——釘選不會啟用它，取消釘選也不會停用它。
    /// 沒有實際變更就不寫檔（<c>HashSet.Add</c>／<c>Remove</c> 的回傳值就是判準），
    /// 避免每幀被誤呼叫時整份設定一直重寫。
    /// </remarks>
    public void SetModuleFavorite(TcModule module, bool favorite)
    {
        var changed = favorite
            ? Config.FavoriteModules.Add(module.InternalName)
            : Config.FavoriteModules.Remove(module.InternalName);

        if (changed) Config.Save();
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

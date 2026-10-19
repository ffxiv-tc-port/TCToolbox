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

    /// <summary>
    /// 自動園圃作業的聊天指令。與 <see cref="gardeningIpc"/> 同樣<b>恆常註冊</b>：
    /// 模組關著時要回一句看得懂的話，而不是讓遊戲回「無此指令」。
    /// </summary>
    private readonly GardeningCommand gardeningCommand;

    /// <summary>背包一鍵清倉的聊天指令。同樣<b>恆常註冊</b>，理由見 <see cref="gardeningCommand"/>。</summary>
    private readonly DeclutterCommand declutterCommand;

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        Instance = this;
        pluginInterface.Create<Svc>();

        Config = Svc.PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        // 🔴 一定要在建立模組之前：模組的建構子／OnEnable 會讀設定，遷移晚一步就讀到舊值。
        Config.Migrate();

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
        Modules.Add(new LetterCollectAll());
        Modules.Add(new AutoMateriaRetrieveAll());
        Modules.Add(new AutoMaterialize());
        Modules.Add(new RepairAllContainers());
        Modules.Add(new MoveGearsNotInSet());
        Modules.Add(new OptimizedFreeCompanyChest());
        Modules.Add(new ShopDefaults());
        Modules.Add(new OptimizedFreeShop());
        Modules.Add(new ShopDisplayRealItemIcon());
        Modules.Add(new AutoShopPurchase());
        Modules.Add(new QuickSplitStacks());
        Modules.Add(new CopyItemNameContextMenu());
        Modules.Add(new HuijiWikiContextMenu());
        Modules.Add(new GlamourSetRetrieve());
        Modules.Add(new GlamourDuplicateCleanup());
        Modules.Add(new GlamourStoreDuplicateGuard());
        Modules.Add(new GlamourArmoireCleanup());
        Modules.Add(new CabinetStoreAll());
        Modules.Add(new TradeAllCollectables());
        Modules.Add(new SaddlebagEntrustDuplicates());
        Modules.Add(new InventoryDeclutter());
        Modules.Add(new FastRetainerStore());
        Modules.Add(new RetainerBatchRename());
        Modules.Add(new RetainerBatchRetrieve());
        Modules.Add(new CraftShoppingList());
        Modules.Add(new TriadCardRecycle());

        // ② 戰鬥 · 小隊
        Modules.Add(new OptimizedEnemyList());
        Modules.Add(new OptimizedTargetInfo());
        Modules.Add(new AutoRetarget());
        Modules.Add(new DeepDungeonChestTarget());
        Modules.Add(new AppraisalDiagnostics());
        Modules.Add(new AutoCheckFoodUsage());
        Modules.Add(new AutoRefocus());
        Modules.Add(new MarkerInPartyList());
        Modules.Add(new AutoPlayerCommend());
        Modules.Add(new DutyAnnounce());
        Modules.Add(new AutoClaimPVPRewards());
        Modules.Add(new OptimizedDutyFinderSetting());
        Modules.Add(new WeeklyBingoClickToOpen());
        Modules.Add(new WeeklyBingoAdvisor());
        Modules.Add(new ContentFinderCommand());
        Modules.Add(new FastContentsFinderRegister());
        Modules.Add(new AutoRefreshPartyFinder());
        Modules.Add(new AutoJoinPartyFinder());
        Modules.Add(new PFPageSizeCustomize());
        Modules.Add(new PartyFinderFilter());
        Modules.Add(new BetterPartyFinderSort());
        Modules.Add(new NoAutoClosePartyFinder());
        Modules.Add(new SelectableRecruitmentText());
        Modules.Add(new FateTracker());
        Modules.Add(new FateLevelSync());
        Modules.Add(new QueueCombatTeleport());
        Modules.Add(new BetterFateProgressUI());
        Modules.Add(new HuntTrainOnMappy());
        Modules.Add(new PalacePalOnMappy());
        Modules.Add(new AutoVeryEasyFirstBattle());

        // ③ 公會 · 生活
        Modules.Add(new AutoFCWSDeliver());
        Modules.Add(new FcActionAutoUse());
        Modules.Add(new FastGrandCompanyExchange());
        Modules.Add(new AutoGardensWork());
        Modules.Add(new AutoGysahlGreens());
        Modules.Add(new CustomDeliveriesOverview());
        Modules.Add(new AutoCustomDeliveryResult());
        Modules.Add(new AutoRequestItemSubmit());
        Modules.Add(new AutoCrafterGathererManual());
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
        Modules.Add(new AchievementProgressTracker());
        Modules.Add(new ChatCoordsOpenMap());
        Modules.Add(new LoginCommands());
        Modules.Add(new AetherCurrentTracker());
        Modules.Add(new DiscardList());
        Modules.Add(new AutoDisplayMSQProgress());
        Modules.Add(new AutoChangeKeyboardLayout());
        Modules.Add(new LargerIMECandidates());
        Modules.Add(new AutoNumericInputMax());
        Modules.Add(new IconReference());
        Modules.Add(new MovementSpeedMultiplier());
        Modules.Add(new QuestionableStepOnMappy());
        Modules.Add(new NearbyOnMinimap());
        Modules.Add(new FleetEmergencyStop());
        Modules.Add(new FleetControlWatch());
        Modules.Add(new WorldTravelPanel());
        Modules.Add(new PluginUpdateNotice());
        Modules.Add(new ExpertDeliveryUnlock());
        Modules.Add(new DailyDutyTodo());

        // 🔴 一定要在模組 Enable() 之前：同一個 AddonLifecycle 事件的監聽器是依註冊順序在同一次派送裡逐一呼叫的，
        //    守衛排在模組之後的話，模組在自己的 PostSetup 處理常式裡按下、登記完位址，緊接著守衛就把那筆紀錄清掉。
        //    ⚠️ 這只是「盡量排前面」（RegisterListener 走 Framework.RunOnTick，順序不保證）；
        //    真正的防線在 AddonPressGuard.OnAddonLifecycle 的「這一幀才登記的不清」。
        AddonPressGuard.EnsureWatching();

        foreach (var module in Modules)
        {
            if (Config.EnabledModules.Contains(module.InternalName))
                module.Enable();
        }

        LogModuleState();

        // 跨外掛熱鍵撞鍵偵測：唯讀掃描別的外掛的設定檔，結果畫在主視窗頂端。
        HotkeyConflictProbe.EnsureWatching();

        gardeningIpc = new GardeningIpc();
        gardeningCommand = new GardeningCommand();
        declutterCommand = new DeclutterCommand();

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
    /// 啟動時把模組狀態寫進記錄（一律 <c>Information</c> 級：要使用者回報的診斷）。
    /// 設定裡有、這一版卻已經不存在的模組名（改名／移除的殘留）**只報告不清除** ——
    /// 使用者在版本之間來回時清掉就回不來了。
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

    /// <summary>開啟主視窗；已經開著就維持開著。</summary>
    /// <remarks>
    /// 🔴 指令要的是「開起來」不是「切換」。用 <see cref="ToggleMainWindow"/> 的話，
    /// 視窗已經開著時打指令會把它<b>關掉</b>——那正好是最容易被讀成「指令壞了」的行為。
    /// </remarks>
    public void OpenMainWindow() => mainWindow.IsOpen = true;

    private void OnCommand(string command, string args) => mainWindow.Toggle();

    public void Dispose()
    {
        Svc.Commands.RemoveHandler(Command);

        gardeningIpc.Dispose();
        gardeningCommand.Dispose();
        declutterCommand.Dispose();

        Svc.PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        Svc.PluginInterface.UiBuilder.OpenConfigUi -= mainWindow.Toggle;
        Svc.PluginInterface.UiBuilder.OpenMainUi -= mainWindow.Toggle;
        WindowSystem.RemoveAllWindows();

        foreach (var module in Modules)
            module.Disable();

        // 🔴 硬拆共用的停止移動看門狗：模組停用路徑的 NavStop.Release 會「延後拆卸」
        //    讓補送窗口活到最後（見 NavStop.Release 的說明），但外掛整個卸載時
        //    絕對不能留一個指向本組件的 Framework.Update 訂閱。
        NavStop.ForceTeardown();

        // 🔴 同理：確認框防重按閘門的 AddonLifecycle 監聽器是共用的（不屬於任何一個模組），
        //    外掛整個卸載時一定要拆乾淨。
        AddonPressGuard.ForceTeardown();

        // 模組停用時各自 Release；這裡兜底，卸載後不能留名字讓 TextAdvance 一直暫停。
        TextAdvanceLock.ForceTeardown();

        // 🔴 急停時向 AutoHook 承租的暫停租約帶著一個 Framework.Update 心跳，
        //    本組件要被卸載了就一定要拆掉，順手把租約交回。
        AutoHookSuppression.ForceTeardown();

        // 🔴 撞鍵偵測訂閱了 ClientState.Login，拆不掉就是一個指向本組件的活訂閱。
        HotkeyConflictProbe.ForceTeardown();

        // 模組啟用失敗時排進來、等登入才印的聊天提示也掛在 Framework.Update 上。
        ModuleFailureNotice.ForceTeardown();
    }
}

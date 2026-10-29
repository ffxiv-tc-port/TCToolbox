using System;
using System.Collections.Generic;
using Dalamud.Configuration;
using TCToolbox.Core;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace TCToolbox;

public sealed class Configuration : IPluginConfiguration
{
    /// <summary>目前的設定結構版本。</summary>
    /// <remarks>
    /// v1 → v2：OptimizedEnemyList 的詠唱顯示由 <c>ShowCast</c>(bool) 換成 <c>CastDisplay</c>(enum)，<c>OffsetX</c>／<c>OffsetY</c> 的<b>語意</b>也變了
    /// （舊＝從列左上角的絕對偏移×縮放；新＝距列邊緣距離＋垂直置中），預設值 4/20 → 6/0，見 <see cref="MigrateEnemyListToV2"/>。
    /// <para>
    /// v2 → v3：沒有資料變更（宣告的預設啟用名單當時沒有寫進播種碼）。
    /// </para>
    /// <para>
    /// v3 → v4：真的把那份名單寫進去（見 <see cref="SeedDefaultOnModules"/>）。模組開關全部存在
    /// <see cref="EnabledModules"/>，所以「預設開」只能靠一次性寫進那份清單。
    /// </para>
    /// <para>
    /// v4 → v5：移動速度倍率的白名單從副本（ContentFinderCondition）改成地圖（TerritoryType），
    /// 舊清單換成地圖併進新欄位、舊欄位原樣保留（見 <see cref="MigrateSpeedWhitelistToTerritories"/>）。
    /// </para>
    /// <para>
    /// v5 → v6：預設開啟寶藏鑑定紀錄（只讀、只寫 log；見 <see cref="DefaultOnModulesV6"/>）。
    /// </para>
    /// </remarks>
    public const int CurrentVersion = 6;

    public int Version { get; set; } = 1;

    /// <summary>已啟用模組的 InternalName 清單。</summary>
    public HashSet<string> EnabledModules { get; set; } = [];

    /// <summary>
    /// 釘選為「常用」的模組 InternalName 清單。
    /// </summary>
    /// <remarks>
    /// ⚠️ 預設是<b>空集合</b>：升級上來的人在勾任何星號之前完全感覺不到差別。
    /// 🔴 這裡存的是 <see cref="TCToolbox.Core.TcModule.InternalName"/>，跟
    /// <see cref="EnabledModules"/> 同一組識別字。<b>模組改名時這兩份會一起失效</b>，
    /// 所以顯示名可以改、InternalName 不能改。
    /// 📌 清單裡出現這一版沒有的模組名時<b>只忽略、不清除</b>（與 <see cref="EnabledModules"/> 同樣的處理）：
    /// 使用者在版本之間來回時清掉就回不來了。
    /// </remarks>
    public HashSet<string> FavoriteModules { get; set; } = [];

    public AutoGysahlGreensConfig GysahlGreens { get; set; } = new();
    public AutoCountPlayersConfig CountPlayers { get; set; } = new();
    public AutoGardensWorkConfig GardensWork { get; set; } = new();
    public AutoAntiAfkConfig AntiAfk { get; set; } = new();
    public AutoConstantlyClickConfig ConstantlyClick { get; set; } = new();
    public AutoPlayerCommendConfig PlayerCommend { get; set; } = new();
    public OptimizedDutyFinderSettingConfig DutyFinderSetting { get; set; } = new();
    public AutoHideBannersConfig HideBanners { get; set; } = new();
    public AutoRetargetConfig Retarget { get; set; } = new();
    public MarkerInPartyListConfig MarkerInPartyList { get; set; } = new();
    public OptimizedEnemyListConfig EnemyList { get; set; } = new();
    public AutoInventoryTransferConfig InventoryTransfer { get; set; } = new();
    public OptimizedTargetInfoConfig TargetInfo { get; set; } = new();
    public AutoHideNeedlessPopupsConfig HideNeedlessPopups { get; set; } = new();
    public OptimizedFreeShopConfig FreeShop { get; set; } = new();
    public AutoRefreshPartyFinderConfig RefreshPartyFinder { get; set; } = new();
    public AutoClaimPVPRewardsConfig ClaimPvpRewards { get; set; } = new();
    public PFPageSizeCustomizeConfig PfPageSize { get; set; } = new();
    public OptimizedInteractionConfig OptimizedInteraction { get; set; } = new();
    public AutoRefocusConfig Refocus { get; set; } = new();
    public AutoQuestAcceptConfig QuestAccept { get; set; } = new();
    public AutoCustomDeliveryResultConfig CustomDeliveryResult { get; set; } = new();
    public CopyItemNameContextMenuConfig CopyItemName { get; set; } = new();
    public HuijiWikiContextMenuConfig HuijiWiki { get; set; } = new();
    public AutoRequestItemSubmitConfig RequestItemSubmit { get; set; } = new();
    public OptimizedFreeCompanyChestConfig FreeCompanyChest { get; set; } = new();
    public WeeklyBingoClickToOpenConfig WeeklyBingoClickToOpen { get; set; } = new();
    public WeeklyBingoAdvisorConfig WeeklyBingoAdvisor { get; set; } = new();
    public GlamourSetRetrieveConfig GlamourSetRetrieve { get; set; } = new();
    public GlamourDuplicateCleanupConfig GlamourDuplicateCleanup { get; set; } = new();
    public MoveGearsNotInSetConfig MoveGearsNotInSet { get; set; } = new();
    public AutoMateriaRetrieveAllConfig MateriaRetrieveAll { get; set; } = new();
    public ShopDefaultsConfig ShopDefaults { get; set; } = new();
    public FateTrackerConfig FateTracker { get; set; } = new();
    public AutoMergeConfig Merge { get; set; } = new();
    public CurrencyCapAlertConfig CurrencyCapAlert { get; set; } = new();
    public ClickToMoveConfig ClickToMove { get; set; } = new();
    public FlagCommandsConfig FlagCommands { get; set; } = new();
    public OpenAllCoffersConfig OpenAllCoffers { get; set; } = new();
    public ARSwitcherConfig ArSwitcher { get; set; } = new();
    public TradeAllCollectablesConfig TradeAllCollectables { get; set; } = new();
    public SaddlebagEntrustDuplicatesConfig SaddlebagEntrust { get; set; } = new();
    public GlamourStoreDuplicateGuardConfig GlamourStoreGuard { get; set; } = new();
    public AutoJoinPartyFinderConfig AutoJoinPartyFinder { get; set; } = new();
    public PartyFinderFilterConfig PartyFinderFilter { get; set; } = new();
    public RepairAllContainersConfig RepairAll { get; set; } = new();
    public AchievementProgressTrackerConfig AchievementTracker { get; set; } = new();
    public ChatCoordsOpenMapConfig ChatCoordsOpenMap { get; set; } = new();
    public FateLevelSyncConfig FateLevelSync { get; set; } = new();
    public LoginCommandsConfig LoginCommands { get; set; } = new();
    public DutyAnnounceConfig DutyAnnounce { get; set; } = new();
    public LetterCollectAllConfig LetterCollectAll { get; set; } = new();
    public RetainerBatchRenameConfig RetainerBatchRename { get; set; } = new();
    public AutoCrafterGathererManualConfig CrafterGathererManual { get; set; } = new();
    public QuickSplitStacksConfig QuickSplitStacks { get; set; } = new();
    public QuickTradeConfig QuickTrade { get; set; } = new();
    public CabinetStoreAllConfig CabinetStoreAll { get; set; } = new();
    public InventoryDeclutterConfig Declutter { get; set; } = new();
    public AetherCurrentTrackerConfig AetherCurrentTracker { get; set; } = new();
    public ContentFinderCommandConfig ContentFinderCommand { get; set; } = new();
    public FastContentsFinderRegisterConfig FastContentsFinderRegister { get; set; } = new();
    public FastRetainerStoreConfig FastRetainerStore { get; set; } = new();
    public RetainerBatchRetrieveConfig RetainerBatchRetrieve { get; set; } = new();
    public FleetControlWatchConfig FleetControlWatch { get; set; } = new();
    public FastGrandCompanyExchangeConfig FastGrandCompanyExchange { get; set; } = new();
    public AutoShopPurchaseConfig AutoShopPurchase { get; set; } = new();
    public DiscardListConfig DiscardList { get; set; } = new();
    public AutoChangeKeyboardLayoutConfig KeyboardLayout { get; set; } = new();
    public LargerIMECandidatesConfig LargerIMECandidates { get; set; } = new();
    public AutoNumericInputMaxConfig NumericInputMax { get; set; } = new();
    public AutoCheckFoodUsageConfig CheckFoodUsage { get; set; } = new();
    public QueueCombatTeleportConfig QueueCombatTeleport { get; set; } = new();
    public BetterFateProgressConfig BetterFateProgress { get; set; } = new();
    public HuntTrainOnMappyConfig HuntTrainOnMappy { get; set; } = new();
    public QuestionableStepConfig QuestionableStep { get; set; } = new();
    public PalacePalOnMappyConfig PalacePalOnMappy { get; set; } = new();
    public CustomDeliveriesOverviewConfig CustomDeliveriesOverview { get; set; } = new();
    public TriadCardRecycleConfig TriadCardRecycle { get; set; } = new();
    public MovementSpeedMultiplierConfig MovementSpeedMultiplier { get; set; } = new();
    public FleetEmergencyStopConfig FleetEmergencyStop { get; set; } = new();
    public DeepDungeonChestTargetConfig DeepDungeonChestTarget { get; set; } = new();
    public NearbyOnMinimapConfig NearbyOnMinimap { get; set; } = new();
    public WorldTravelPanelConfig WorldTravel { get; set; } = new();
    public CraftShoppingListConfig CraftShoppingList { get; set; } = new();
    public PluginUpdateNoticeConfig PluginUpdateNotice { get; set; } = new();
    public DailyDutyTodoConfig DailyDutyTodo { get; set; } = new();
    public AutoFCWSDeliverConfig FcwsDeliver { get; set; } = new();
    public FcActionAutoUseConfig FcActionAutoUse { get; set; } = new();
    public NearbyInteractablesConfig NearbyInteractables { get; set; } = new();
    public NearbyEnemiesConfig NearbyEnemies { get; set; } = new();

    public void Save() => Svc.PluginInterface.SavePluginConfig(this);

    /// <summary>
    /// 一次性的設定結構遷移。<b>載入之後、模組建立之前</b>呼叫。
    /// </summary>
    /// <remarks>
    /// 🔴 改欄位名時舊鍵會變成孤兒被靜默丟掉、新鍵吃預設值；<b>改欄位語意卻沿用舊欄位名</b>更糟——舊值會被原封不動當成新語意用。
    /// 🔑 欄位語意改變時要<b>同時改欄位名</b>（例如 <c>EdgeOffsetX</c>／<c>CenterOffsetY</c>），讓舊鍵自然孤兒化去吃新預設。
    /// </remarks>
    public void Migrate()
    {
        if (Version >= CurrentVersion) return;

        var from = Version;
        var migrated = MigrateEnemyListToV2();

        // 播種只屬於 v3 → v4：之後的升級再播一次，會把使用者自己關掉的模組重新打開。
        var seeded = from < 4 ? SeedDefaultOnModules(DefaultOnModules) : [];
        if (from < 6) seeded.AddRange(SeedDefaultOnModules(DefaultOnModulesV6));
        var speed = from < 5
            ? MigrateSpeedWhitelistToTerritories(DutySupportData.GetTerritoryType)
            : [];

        Version = CurrentVersion;
        Save();

        // 使用者回報用：設定被動過就一定要留下痕跡。
        Svc.Log.Information(
            $"[TCToolbox] 設定結構升級 v{from} → v{CurrentVersion}"
            + (migrated ? "；已把 OptimizedEnemyList 的舊 ShowCast／偏移值遷移到新 schema" : string.Empty)
            + (seeded.Count > 0 ? $"；預設啟用了 {string.Join("、", seeded)}" : string.Empty)
            + (speed.Count > 0 ? $"；移動速度倍率的副本白名單 {speed.Count} 筆換成地圖" : string.Empty)
            + (!migrated && seeded.Count == 0 && speed.Count == 0 ? "（沒有需要遷移的舊值）" : string.Empty));

        foreach (var (duty, territory) in speed)
        {
            var dutyText = $"{DutySupportData.GetDutyName(duty)}（{duty}）";
            Svc.Log.Information(territory == 0
                ? $"[TCToolbox] 移動速度倍率：舊白名單的副本 {dutyText} 找不到對應的地圖，沒有換過來（舊清單仍保留）。"
                : $"[TCToolbox] 移動速度倍率：舊白名單的副本 {dutyText} → 地圖 {PlaceNameOf(territory)}（{territory}）。");
        }
    }

    private static string PlaceNameOf(uint territory) =>
        Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>().GetRowOrDefault(territory)?
            .PlaceName.ValueNullable?.Name.ExtractText() ?? string.Empty;

    /// <summary>
    /// 移動速度倍率 v4 → v5：舊的副本白名單換成地圖 id 併進 <see cref="MovementSpeedMultiplierConfig.TerritoryWhitelist"/>。
    /// </summary>
    /// <remarks>
    /// 契約：只加不刪，<see cref="MovementSpeedMultiplierConfig.DutyWhitelist"/> 原樣保留備查。
    /// 查表函式由呼叫端給（遊戲裡是 Lumina 的 ContentFinderCondition.TerritoryType），離線可以換成假資料測。
    /// </remarks>
    internal List<(uint Duty, uint Territory)> MigrateSpeedWhitelistToTerritories(Func<uint, uint> dutyToTerritory) =>
        Modules.SpeedWhitelist.ConvertLegacyDuties(
            MovementSpeedMultiplier.DutyWhitelist, dutyToTerritory, MovementSpeedMultiplier.TerritoryWhitelist);

    /// <summary>v4 起「預設開著」的模組。🔴 只准放<b>純顯示</b>、不碰遊戲狀態的模組。</summary>
    private static readonly string[] DefaultOnModules =
    [
        Modules.PluginUpdateNotice.ModuleInternalName,
        Modules.WeeklyBingoAdvisor.ModuleInternalName,
    ];

    /// <summary>v6 起預設開著的模組（同一條規則：只准純顯示／只寫 log）。</summary>
    private static readonly string[] DefaultOnModulesV6 =
    [
        Modules.AppraisalDiagnostics.ModuleInternalName,
    ];

    /// <summary>把 <paramref name="names"/> 寫進 <see cref="EnabledModules"/>，回傳真的加進去的名字。</summary>
    /// <remarks>
    /// 契約：<b>只加不刪</b>，只在該鍵不在清單裡時加，清單裡其他東西一律不碰。
    /// ⚠️ 停用＝直接從清單移除（沒有 DisabledModules 也沒有旗標），所以「從沒開過」與
    /// 「開過又自己關掉」分不開；使用者已裁決按「缺鍵就加」處理。
    /// </remarks>
    private List<string> SeedDefaultOnModules(string[] names)
    {
        List<string> added = [];
        foreach (var name in names)
        {
            if (EnabledModules.Add(name)) added.Add(name);
        }

        return added;
    }

    /// <summary>
    /// OptimizedEnemyList 的 v1 → v2 遷移。有動到東西回 <see langword="true"/>。
    /// </summary>
    /// <remarks>
    /// <b>判準是孤兒鍵 <c>ShowCast</c> 存在</b>——那是「這份設定檔是由舊 schema 寫出來的」
    /// 唯一的正面證據（Dalamud 存設定時會把所有公開成員都寫出去，所以跑過舊版並存過設定的人
    /// 一定有這個鍵）。沒有它就什麼都不做，全新安裝與早已是新 schema 的人都不受影響。
    /// </remarks>
    private bool MigrateEnemyListToV2()
    {
        if (EnemyList.ShowCast is not { } legacyShowCast) return false;

        // ① 舊 ShowCast(bool) → 新 CastDisplay(enum)。
        //    關掉過的人升級後詠唱顯示會被靜默重新打開（舊鍵孤兒、新鍵吃預設 Auto），這條就是修那個。
        EnemyList.CastDisplay = legacyShowCast ? CastDisplayMode.Auto : CastDisplayMode.Off;

        // ② 偏移值：語意變了，自訂過的值換算不回來，所以<b>只</b>處理「剛好等於舊預設」的組合
        //    ——那一族（絕大多數人）直接給新預設；自訂過的視為刻意版面，一律不動。
        //    舊預設在新語意下會把疊圖畫在垂直置中再往下 20px（貼列底／出列），與全新安裝差 20px。
        if (EnemyList.OffsetX == LegacyEnemyListOffsetX && EnemyList.OffsetY == LegacyEnemyListOffsetY)
        {
            EnemyList.OffsetX = OptimizedEnemyListConfig.DefaultOffsetX;
            EnemyList.OffsetY = OptimizedEnemyListConfig.DefaultOffsetY;
        }

        // 孤兒鍵用完就丟，不要再寫回設定檔。
        EnemyList.ShowCast = null;
        return true;
    }

    /// <summary>v1 時代 OptimizedEnemyList 的 <c>OffsetX</c> 預設值。</summary>
    private const float LegacyEnemyListOffsetX = 4f;

    /// <summary>v1 時代 OptimizedEnemyList 的 <c>OffsetY</c> 預設值。</summary>
    private const float LegacyEnemyListOffsetY = 20f;
}

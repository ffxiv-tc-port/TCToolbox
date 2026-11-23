using System;
using System.Collections.Generic;
using Dalamud.Configuration;
using TCToolbox.Core;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace TCToolbox;

public sealed partial class Configuration : IPluginConfiguration
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
    public SquadronRoutineConfig SquadronRoutine { get; set; } = new();
    public AutoFCWSDeliverConfig FcwsDeliver { get; set; } = new();
    public FcActionAutoUseConfig FcActionAutoUse { get; set; } = new();
    public NearbyInteractablesConfig NearbyInteractables { get; set; } = new();
    public NearbyEnemiesConfig NearbyEnemies { get; set; } = new();
    public VentureValueAdvisorConfig VentureValueAdvisor { get; set; } = new();

    public void Save() => Svc.PluginInterface.SavePluginConfig(this);
}

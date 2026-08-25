using System.Collections.Generic;
using Dalamud.Configuration;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace TCToolbox;

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    /// <summary>已啟用模組的 InternalName 清單。</summary>
    public HashSet<string> EnabledModules { get; set; } = [];

    /// <summary>
    /// 釘選為「常用」的模組 InternalName 清單。
    /// </summary>
    /// <remarks>
    /// ⚠️ 預設是<b>空集合</b>——「常用」分頁是空的，其他分頁一模一樣，
    /// 也就是升級上來的人在勾任何星號之前完全感覺不到差別。
    /// <para>
    /// 🔴 這裡存的是 <see cref="TCToolbox.Core.TcModule.InternalName"/>，跟
    /// <see cref="EnabledModules"/> 同一組識別字。<b>模組改名時這兩份會一起失效</b>，
    /// 所以顯示名可以改、InternalName 不能改。
    /// </para>
    /// <para>
    /// 📌 清單裡出現這一版沒有的模組名時<b>只忽略、不清除</b>（與 <see cref="EnabledModules"/> 同樣的處理）：
    /// 使用者在版本之間來回時清掉就回不來了。
    /// </para>
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
    public CabinetStoreAllConfig CabinetStoreAll { get; set; } = new();
    public AetherCurrentTrackerConfig AetherCurrentTracker { get; set; } = new();
    public ContentFinderCommandConfig ContentFinderCommand { get; set; } = new();
    public FastContentsFinderRegisterConfig FastContentsFinderRegister { get; set; } = new();
    public FastRetainerStoreConfig FastRetainerStore { get; set; } = new();
    public FastGrandCompanyExchangeConfig FastGrandCompanyExchange { get; set; } = new();
    public AutoShopPurchaseConfig AutoShopPurchase { get; set; } = new();
    public DiscardListConfig DiscardList { get; set; } = new();
    public AutoChangeKeyboardLayoutConfig KeyboardLayout { get; set; } = new();
    public AutoNumericInputMaxConfig NumericInputMax { get; set; } = new();
    public AutoCheckFoodUsageConfig CheckFoodUsage { get; set; } = new();

    public void Save() => Svc.PluginInterface.SavePluginConfig(this);
}

/// <summary><see cref="Modules.AutoChangeKeyboardLayout"/> 的設定。</summary>
/// <remarks>
/// ⚠️ 兩個欄位都是 <c>ushort</c> 的鍵盤配置語言 id（HKL 低字組）。
/// 🔴 預設 0＝「尚未設定」，模組啟用時會把 0 補成目前系統配置＝兩邊相同＝不切換，沿用現行行為。
/// 舊設定檔沒有這兩個鍵，反序列化不會覆寫初始值，所以升級不會讓人突然被切輸入法。
/// </remarks>
public sealed class AutoChangeKeyboardLayoutConfig
{
    /// <summary>文字輸入框取得焦點時要切到的配置語言 id。</summary>
    public ushort FocusLayoutLangID;

    /// <summary>離開文字輸入框時要切回的配置語言 id。</summary>
    public ushort UnfocusLayoutLangID;
}

/// <summary><see cref="Modules.AutoNumericInputMax"/> 的設定。</summary>
public sealed class AutoNumericInputMaxConfig
{
    /// <summary>把上限＝99 的數字輸入框放寬到這個上限（夾在 100–9999）。</summary>
    public int MaxValue = 999;

    /// <summary>
    /// 順便把值預先填到上限。
    /// </summary>
    /// <remarks>
    /// 🔴 預設 <c>false</c>：自動把數量填到最大是誤買／誤丟的地雷。只放大上限（讓你能自己打更大的數字）
    /// 沒有這個風險，要「開框就是最大」的人再自己打開。
    /// </remarks>
    public bool AutoFillToMax;
}

/// <summary>一份「什麼時候吃什麼食物」的設定。</summary>
public sealed class FoodPreset
{
    /// <summary>食物道具編號（NQ 的 Item RowId）。</summary>
    public uint ItemId { get; set; }

    /// <summary>用優質（HQ）版本。</summary>
    public bool IsHq { get; set; } = true;

    /// <summary>啟用這一條。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>限定職業（<c>ClassJob</c> RowId）；空＝所有職業。</summary>
    public HashSet<uint> Jobs { get; set; } = [];

    /// <summary>限定區域（<c>TerritoryType</c> RowId）；空＝所有區域。</summary>
    public HashSet<uint> Zones { get; set; } = [];
}

/// <summary><see cref="Modules.AutoCheckFoodUsage"/> 的設定。</summary>
/// <remarks>
/// 🔴🔴 三個觸發時機都<b>預設 false</b>（使用者裁決）：這是唯一會在沒按按鈕的情況下替使用者用道具的模組，
/// 開了模組但一個時機都沒勾＝完全不動作。舊設定檔沒有這些鍵，反序列化不覆寫初始值。
/// </remarks>
public sealed class AutoCheckFoodUsageConfig
{
    /// <summary>進副本／切換區域時檢查食物。</summary>
    public bool OnZoneChange;

    /// <summary>倒數計時開始時檢查食物。</summary>
    public bool OnCountdown;

    /// <summary>指定的戰鬥條件變更時檢查食物。</summary>
    public bool OnConditionChange;

    /// <summary>「條件變更時」中，哪些條件<b>開始</b>時觸發（<see cref="Dalamud.Game.ClientState.Conditions.ConditionFlag"/> 值）。</summary>
    public HashSet<uint> ConditionStart { get; set; } = [];

    /// <summary>「條件變更時」中，哪些條件<b>結束</b>時觸發。</summary>
    public HashSet<uint> ConditionEnd { get; set; } = [];

    /// <summary>元氣 buff 剩餘不足這麼多秒就補（預設 600＝10 分）。</summary>
    public int RefreshThresholdSeconds { get; set; } = 600;

    /// <summary>用食物時在聊天欄說一聲（記錄一律會寫，不受這格影響）。</summary>
    public bool NotifyInChat { get; set; } = true;

    /// <summary>食物清單。</summary>
    public List<FoodPreset> Presets { get; set; } = [];
}

/// <summary><see cref="Modules.AetherCurrentTracker"/> 的設定。</summary>
public sealed class AetherCurrentTrackerConfig
{
    /// <summary>在畫面上把風脈泉的位置畫出來。</summary>
    public bool ShowWorldOverlay { get; set; } = true;

    /// <summary>畫面外的目標在螢幕邊緣畫箭頭指出方向。</summary>
    public bool ShowOffScreenArrows { get; set; } = true;

    /// <summary>已共鳴的也畫（細灰圈）。</summary>
    /// <remarks>
    /// 📌 預設 <see langword="false"/>：已經共鳴過的對「還差哪幾個」沒有幫助，
    /// 全畫出來只會讓畫面變亂。想確認位置的人再打開。
    /// </remarks>
    public bool ShowResonatedInOverlay { get; set; }
}

/// <summary><see cref="Modules.CabinetStoreAll"/> 的設定。</summary>
public sealed class CabinetStoreAllConfig
{
    /// <summary>兩件之間額外等多久（毫秒）。</summary>
    /// <remarks>
    /// 📌 主要的節奏來自「等伺服器確認」那一步，這裡只是額外餘裕。
    /// ⚠️ 台服的封包速率限制沒有公開數字，DailyRoutines 用的 100ms 是國服經驗值。
    /// </remarks>
    public int IntervalMs { get; set; } = 300;

    /// <summary>等伺服器把「已在收藏櫃」旗標翻過來的上限（毫秒），逾時整條中止。</summary>
    public int ConfirmTimeoutMs { get; set; } = 5_000;
}

/// <summary><see cref="Modules.QuickSplitStacks"/> 的設定。</summary>
public sealed class QuickSplitStacksConfig
{
    /// <summary>拆完在聊天視窗說一聲。</summary>
    public bool NotifyOnSplit { get; set; } = true;

    /// <summary>下次開啟輸入框時沿用上次填的數量。</summary>
    public bool RememberAmount { get; set; } = true;

    /// <summary>上次填的數量。</summary>
    /// <remarks>
    /// 📌 開輸入框時仍會夾在 <c>1 .. 這一疊數量-1</c> 之間，
    /// 所以這裡就算存著一個對當前道具不合理的值也不會送出不合法的數量。
    /// </remarks>
    public int LastAmount { get; set; } = 1;
}

/// <summary><see cref="Modules.AutoCrafterGathererManual"/> 的設定。</summary>
public sealed class AutoCrafterGathererManualConfig
{
    /// <summary>
    /// 兩次檢查之間隔幾秒。
    /// </summary>
    /// <remarks>
    /// 📌 指南的加成狀態長達 30 分鐘，所以這個值再大都不會漏掉太久；
    /// 預設 10 秒是為了「剛換職／剛用完」時反應得夠快。
    /// 一次檢查的成本是幾個欄位讀取，只有真的該用時才會去查背包數量。
    /// </remarks>
    public int PollSeconds { get; set; } = 10;

    /// <summary>用掉一本指南時在聊天視窗說一聲。</summary>
    public bool NotifyOnUse { get; set; } = true;
}

/// <summary>候選名字的狀態。</summary>
/// <remarks>
/// 🔴 <b>零值必須是有效狀態。</b>反序列化時 JSON 缺少這個鍵就會落在零值上，
/// 而「未用」正是那種情況的正確答案——把零值放在「已被占用」之類的狀態上，
/// 會讓舊設定檔升上來之後整份名單靜默變成不可用。
/// </remarks>
public enum RetainerNameCandidateState
{
    /// <summary>還沒用過，可以指派。<b>這是預設狀態，所以必須是零值。</b></summary>
    Available = 0,

    /// <summary>已經用在某位僱員身上。</summary>
    Used = 1,

    /// <summary>伺服器（或使用者）回報這個名字已經被占用。</summary>
    Taken = 2,

    /// <summary>預檢或伺服器判定這個名字不能用。</summary>
    Rejected = 3,
}

/// <summary>一個候選名字的持久化狀態。</summary>
/// <remarks>
/// 🔴 <b>這份狀態是全域的，不分角色。</b>僱員名字是<b>全世界唯一</b>的，所以
/// 「這個名字已經被占用」對每個角色都成立；同一份名單要能跨角色輪流消耗
/// （使用者的僱員分散在多個角色上，每個角色上限 10 位）。
/// 換角色<b>絕對不能</b>重置這些狀態——那會讓流程一再去試已知不能用的名字。
/// </remarks>
public sealed class RetainerNameCandidateStatus
{
    public RetainerNameCandidateState State { get; set; } = RetainerNameCandidateState.Available;

    /// <summary>用在哪一位僱員身上（<c>RetainerId</c> 全域唯一）。</summary>
    public ulong UsedByRetainerId { get; set; }

    /// <summary>當時那位僱員屬於哪個角色（僅供視窗顯示用）。</summary>
    public ulong UsedByContentId { get; set; }

    /// <summary>當時的角色名（僅供視窗顯示用；角色改名的話這裡會是舊的，無妨）。</summary>
    public string UsedByCharacterName { get; set; } = string.Empty;

    /// <summary>當時那位僱員叫什麼（僅供視窗顯示用）。</summary>
    public string UsedByRetainerName { get; set; } = string.Empty;

    /// <summary>人看的說明（為什麼被剔除／被誰用掉）。</summary>
    public string Note { get; set; } = string.Empty;
}

/// <summary>批次僱員改名。</summary>
public sealed class RetainerBatchRenameConfig
{
    /// <summary>
    /// 自動操作 CharaMake 改名畫面（保留容貌、只改名字），而不是停在那裡等你手動改。
    /// </summary>
    /// <remarks>
    /// 📌 預設 <c>true</c>（呼叫者裁決）：走管理人選單「有什麼事？」→ 改變樣貌性格名字 → 選這位僱員 →
    /// 一路按確認、<b>在「要使用已儲存的角色形象嗎？」按「是」保留原容貌</b> → 性格統一選第一項（開朗）→
    /// 填入候選名字 → 送出。名字被占用會自動換下一個候選。
    /// <para>
    /// 🔴 這條路徑的每一步都<b>比對話文字</b>才動作，對不上就<b>不動、逾時跳過</b>（fail-closed）——
    /// 尤其「要使用已儲存的角色形象嗎？」那道閘門若沒先通過，絕不確認任何「設定成目前的樣子」，
    /// 以免把外觀改成空白（不可逆）。
    /// </para>
    /// <para>
    /// ⚠️ 關掉＝退回舊行為：只自動卸裝／穿裝，改名畫面停在那裡等你手動操作（期間照舊錄製）。
    /// </para>
    /// </remarks>
    public bool AutoCharaMakeRename = true;

    /// <summary>
    /// 使用者手動改名的期間，把畫面資訊寫進記錄。
    /// </summary>
    /// <remarks>
    /// 📌 預設 <c>true</c>。這一版<b>不自動操作改名畫面</b>（CharaMake 的僱員模式在台服沒有
    /// 任何可離線查證的資料，猜 callback 序號去點它是不可逆的破壞），改成在使用者手動操作時
    /// 把 addon 開關、選單文字、每一次 UI callback、以及背包／僱員裝備欄的每一格變化
    /// 寫進記錄（<c>Information</c> 等級）。
    /// 那些資料是之後判斷「能不能自動化」的唯一依據，所以預設開著。
    /// <para>
    /// ⚠️ 錄製只在流程停在「等你改名」的那一段才生效，
    /// 其餘時間 hook 是停用的（<c>AtkUnitBase::FireCallback</c> 是全遊戲的熱路徑）。
    /// 想錄整條流程請用視窗上的「開始錄製（我自己操作）」。
    /// </para>
    /// </remarks>
    public bool RecordDuringManualRename = true;

    /// <summary>
    /// 偵測到僱員名稱已經改變就自動往下走，不必按「改名完成」。
    /// </summary>
    /// <remarks>
    /// 📌 預設 <c>true</c>：名字變了是「使用者已經改完」最直接的證據。
    /// 關掉的話一律等按鈕——按鈕本身<b>永遠都在</b>，這格只影響要不要另外自動偵測。
    /// </remarks>
    public bool AutoDetectRenameDone = true;

    /// <summary>
    /// 卸裝之後等這麼久（毫秒）才驗證裝備真的離開僱員身上。
    /// </summary>
    /// <remarks>
    /// 🔴 預設 10000ms（2026-08-23 主 session 抽驗時從 2500 調高：要蓋過實機量到的退回延遲上界）。<c>MoveItemSlot</c> 會<b>同步</b>改好本機容器，所以呼叫完立刻回讀一定是成功的；
    /// 伺服器若拒絕，要幾秒之後才把道具退回來（本 repo 2026-07-31 實機量到的退回延遲是 3.9～10.6 秒，
    /// 但那批是在沒帶 <c>a6</c> 的情況下量的，帶了 <c>a6: true</c> 之後的延遲分佈未知）。
    /// ⚠️ 設太短的後果是「卸裝其實沒生效卻繼續往下跑」，那會讓改名整個失敗；
    /// 設太長只是慢一點。不確定就往大的調。
    /// </remarks>
    public int UndressVerifyDelayMs = 10_000;

    /// <summary>
    /// 同一位僱員最多連續試幾個候選名字。
    /// </summary>
    /// <remarks>
    /// 📌 預設 5。名字被占用時會自動換下一個候選；這是<b>失控保險絲</b>——
    /// 若偵測「被占用」的訊號誤判成常態，沒有這道上限會一路把整份名單燒光。
    /// </remarks>
    public int MaxCandidateAttemptsPerRetainer = 5;

    /// <summary>
    /// 改名之前，先把該僱員<b>已經完成</b>的探險成果收回來（<b>不重新派遣</b>）。
    /// </summary>
    /// <remarks>
    /// 📌 預設 <c>true</c>。台服 <c>LogMessage</c> 3904「僱員在探險的過程中無法更換裝備。」
    /// ⇒ 探險中的僱員卸不了裝、也就改不了名。先前的行為是把這種僱員<b>整個擋在前置檢查</b>，
    /// 使用者得自己一隻一隻去收。
    /// <para>
    /// ⚠️ 這一格只影響「探險<b>已經完成</b>」的僱員。<b>探險還在跑</b>的僱員照舊擋下來
    /// （沒有任何辦法讓探險提早結束），前置檢查會顯示剩餘時間。
    /// </para>
    /// <para>
    /// 🔴 打開時流程會在卸裝之前多一道硬閘門：探險沒有真的收回就<b>當場停下</b>，
    /// 不會讓錯誤在後面以「卸裝沒生效」的樣貌出現。
    /// </para>
    /// <para>
    /// 🔴 <b>與 AutoRetainer 互斥</b>：AutoRetainer 若正在跑自己的收派循環，
    /// 會把剛收回的僱員立刻重新派遣。所以「這一輪真的會去收探險」時，
    /// 前置檢查會另外要求 AutoRetainer 不在忙碌狀態。
    /// </para>
    /// </remarks>
    public bool CollectCompletedVentureBeforeRename = true;

    /// <summary>
    /// 候選名單原文（一行一個名字）。
    /// </summary>
    /// <remarks>
    /// 📌 預設空字串＝<b>沒有名單，流程不會開始</b>（前置閘門會擋）。
    /// 使用者可以直接貼、從設定資料夾的 <c>retainer_names.txt</c> 載入、
    /// 或按「載入內建名單」把組件內附的 100 個候選加進來。
    /// </remarks>
    public string NamePoolText = string.Empty;

    /// <summary>
    /// 每個候選名字的狀態。<b>鍵是名字本身</b>，不是行號。
    /// </summary>
    /// <remarks>
    /// 🔑 用名字當鍵，使用者重新排序或增刪名單時，已知被占用的名字不會因為位置變了就被重試。
    /// 🔴 這份是<b>全域</b>的（見 <see cref="RetainerNameCandidateStatus"/>）：僱員名全世界唯一，
    /// 所以跨角色共用同一份消耗紀錄。
    /// </remarks>
    public Dictionary<string, RetainerNameCandidateStatus> CandidateStatus { get; set; } = [];
}

/// <summary>招募詳細視窗自動加入。</summary>
public sealed class AutoJoinPartyFinderConfig
{
    /// <summary>
    /// 詳細視窗開啟後等這麼久（毫秒）才按下「加入」。
    /// </summary>
    /// <remarks>
    /// 📌 預設 300ms：一方面讓視窗把內容填好（招募資訊是非同步回來的），
    /// 另一方面這也是使用者「按下取消鍵反悔」的時間窗。
    /// </remarks>
    public int DelayMs = 300;

    /// <summary>
    /// 取消鍵的 <c>VirtualKey</c> 值（0＝不使用）。按著它點開招募時，這一則不會自動加入。
    /// </summary>
    /// <remarks>
    /// 📌 預設 0（不使用）：開了這個模組的人要的就是「點開就加入」。
    /// 想保留「純粹看內容」的操作方式再自己設一顆。
    /// </remarks>
    public int CancelKeyCode;

    /// <summary>
    /// 跳過密碼招募。
    /// </summary>
    /// <remarks>
    /// 📌 預設 <c>true</c>：密碼招募按下去只會跳出輸入密碼的視窗，我們不會也不該去填它。
    /// <para>
    /// ⚠️ 判斷依據是 <c>AgentLookingForGroup.LastViewedListing.JoinConditionFlags</c> 的 bit1，
    /// 這個讀法<b>無法離線證明</b>；兩個方向的失敗都設計成無害
    /// （多擋了＝這一則要自己按、少擋了＝跳出密碼視窗自己關）。
    /// </para>
    /// </remarks>
    public bool SkipPrivate = true;

    /// <summary>
    /// 順便按掉「確定要加入…」的確認框。
    /// </summary>
    /// <remarks>
    /// 🔴 只在「我們剛按過加入的 5 秒內、而且按下去那一刻畫面上沒有其他確認框、
    /// 而且招募詳細視窗還開著」三個條件同時成立時才代按。
    /// 少了這層因果連結就變成「看到 Yes/No 就按是」，那是完全不同的風險等級。
    /// </remarks>
    public bool ConfirmYesNo = true;

    /// <summary>按下加入時在聊天欄顯示一行（記錄一律會寫，不受這格影響）。</summary>
    /// <remarks>
    /// 📌 預設 <c>true</c>：這個功能會在使用者沒按任何按鈕的情況下把他丟進一支隊伍，
    /// 一行訊息是他唯一看得到的「是這個外掛做的」證據。
    /// </remarks>
    public bool NotifyInChat = true;
}

/// <summary>招募清單過濾器。</summary>
public sealed class PartyFinderFilterConfig
{
    /// <summary>隱藏重複招募（同副本＋同說明文字）。</summary>
    public bool FilterSameDescription = true;

    /// <summary>關鍵字比對為白名單（true＝只留命中的）；false＝黑名單（命中就藏）。</summary>
    public bool RegexIsWhitelist;

    /// <summary>關鍵字規則（正規表示式，比對招募人名稱＋說明）。</summary>
    public List<PartyFinderRegexRule> RegexRules { get; set; } = [];

    /// <summary>高難度副本：隊裡已有同職業就藏。</summary>
    public bool HighEndFilterSameJob = true;

    /// <summary>高難度副本：我這職能已滿／沒有空位收我就藏。</summary>
    /// <remarks>📌 預設關：這道過濾依賴載入時的職能欄位自我校準，且會靜默隱藏隊伍，讓使用者自己決定要不要開。</remarks>
    public bool HighEndFilterRoleCount;

    /// <summary>各職能上限（-1＝忽略），順序＝坦／純治／盾治／近戰／遠物／遠魔。</summary>
    /// <remarks>🔴 長度必須是 6；模組啟用時會正規化（長度不符就重設成預設）。</remarks>
    public int[] RoleCaps { get; set; } = [2, 1, 1, 2, 1, 2];
}

/// <summary>招募清單過濾器的一條關鍵字規則。</summary>
public sealed class PartyFinderRegexRule
{
    public bool Enabled = true;
    public string Pattern = string.Empty;
}

/// <summary>投影台：攔截重複收納。</summary>
public sealed class GlamourStoreDuplicateGuardConfig
{
    /// <summary>
    /// 偵測到重複時直接幫忙按下確認框的「否」。
    /// </summary>
    /// <remarks>
    /// 📌 預設 <c>true</c>：這正是模組存在的理由，而模組本身預設是關的，所以不會有人被動生效。
    /// <para>
    /// 🔴 <b>不論這格開或關，攔截／提醒一定會在聊天欄出現。</b>
    /// 靜默地把使用者的操作取消掉是最糟的失敗形式——他只會覺得「按了沒反應」。
    /// </para>
    /// <para>
    /// ⚠️ 關掉時只提醒、不動遊戲，確認框留給使用者自己決定。
    /// </para>
    /// </remarks>
    public bool BlockConfirmation = true;

    /// <summary>
    /// 把「染色不同」的同款裝備視為不同幻影，不當成重複。
    /// </summary>
    /// <remarks>
    /// 📌 預設 <c>true</c>，與 <see cref="GlamourDuplicateCleanupConfig.DistinguishByDye"/> 一致：
    /// 同一件裝備染成兩個顏色在投影台裡是兩種可用的外觀，一律當重複會擋掉正當的收納。
    /// <para>⚠️ 優質／普通品則<b>刻意不分</b>——它們在投影台裡長得一模一樣，兩件都留就是浪費一格。</para>
    /// </remarks>
    public bool DistinguishByDye = true;
}

/// <summary>
/// 陸行鳥鞍囊：寄放重複道具。
/// ⚠️ 沒有「啟用」欄位是刻意的：模組本身預設就是關的，而且開著也要按按鈕才會動。
/// </summary>
public sealed class SaddlebagEntrustDuplicatesConfig
{
    /// <summary>
    /// 每寄放一件之間的最短間隔（毫秒）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 每一件都要「開右鍵選單 → 點項目 → 等伺服器生效」三步，本來就不快。
    /// 這個間隔是在那三步<b>之外</b>再多留的餘裕，預設 500ms。
    /// </remarks>
    public int StepIntervalMs = 500;

    /// <summary>結束時在聊天欄報告結果（記錄一律會寫，不受這格影響）。</summary>
    public bool NotifyOnFinish = true;

    /// <summary>
    /// 把「優質」與「普通品」當成兩款不同的道具。
    /// </summary>
    /// <remarks>
    /// 📌 預設 <c>true</c>，與上游 PandorasBox <c>EntrustChocoboDuplicates</c> 不同
    /// （它只比對 <c>ItemId</c>，而 HQ 資訊在 <c>Flags</c> 裡，所以優質品會被當成普通品的重複）。
    /// 保守的方向是「少搬幾件」：搬錯的話要自己一件一件從鞍囊拿回來。
    /// </remarks>
    public bool MatchQuality = true;
}

/// <summary>
/// 收藏品一鍵全交。
/// ⚠️ 這裡沒有「啟用」欄位是刻意的：模組本身預設就是關的，而且開著也要按按鈕才會動
/// （<see cref="TCToolbox.Core.TcModule.IsManualTrigger"/>），不需要第二段開關。
/// </summary>
public sealed class TradeAllCollectablesConfig
{
    /// <summary>
    /// 每交一件之間的最短間隔（毫秒）。
    /// </summary>
    /// <remarks>
    /// 📌 預設 500ms。交易是真的送到伺服器的請求，而且伺服器要一段時間才會把背包更新回來——
    /// 間隔太短會讓「這一下到底有沒有生效」的判斷失準，也讓使用者來不及按停止。
    /// </remarks>
    public int StepIntervalMs = 500;

    /// <summary>結束時在聊天欄報告結果（記錄一律會寫，不受這格影響）。</summary>
    public bool NotifyOnFinish = true;
}

/// <summary>AutoRetainer 角色切換。</summary>
public sealed class ARSwitcherConfig
{
    /// <summary>
    /// 點擊伺服器資訊列的項目就切換角色（左鍵＝下一個、右鍵＝上一個）。
    /// </summary>
    /// <remarks>
    /// 🔴 預設 <c>false</c>，與上游 CBT 的行為<b>刻意不同</b>：
    /// 切換角色等於<b>登出再登入</b>，而那顆圖示就在時鐘旁邊——手滑點到的代價是整個角色被登出。
    /// 預設關閉時點擊只會開啟 TC Toolbox 設定視窗，切換一律走指令。
    /// </remarks>
    public bool SwitchOnDtrClick;
}

/// <summary>一鍵全修（跨容器）。</summary>
public sealed class RepairAllContainersConfig
{
    /// <summary>
    /// 每個容器之間的最短間隔（毫秒）。
    /// </summary>
    /// <remarks>
    /// 📌 預設 400ms。每一步都是真的送到伺服器的修理要求（遊戲自己是一次一個容器），
    /// 一口氣連送七次既沒有好處，也讓人來不及按停止。
    /// </remarks>
    public int StepIntervalMs = 400;

    /// <summary>
    /// 使用暗物質自行修理時也接手。
    /// </summary>
    /// <remarks>
    /// 📌 預設 <c>true</c>：呼叫的是遊戲自己在「全部修理」按鈕背後跑的同一支函式，
    /// 自行修理與修理工只差在 <c>isNpc</c> 這個旗標，行為上沒有額外風險。
    /// 每一次自行修理都會佔用角色（<c>Occupied39</c>），流程會等上一次結束才送下一次。
    /// </remarks>
    public bool IncludeSelfRepair = true;

    /// <summary>結束時在聊天視窗回報（記錄一律會寫，不受這格影響）。</summary>
    public bool AnnounceInChat = true;
}

/// <summary>成就進度追蹤：單筆追蹤項。</summary>
public sealed class TrackedAchievementEntry
{
    /// <summary>成就編號（Lumina <c>Achievement</c> 表的列號）。</summary>
    public uint Id { get; set; }

    /// <summary>上次查回來的目前進度。</summary>
    public uint Current { get; set; }

    /// <summary>上次查回來的目標值。</summary>
    public uint Max { get; set; }

    /// <summary>
    /// 上次成功查詢的 UTC 時間（<c>DateTime.Ticks</c>）。
    /// </summary>
    /// <remarks>
    /// 🔴 <c>0</c>＝<b>從來沒有查過</b>，UI 必須據此畫成灰色的 <c>?</c>。
    /// 沒有這個欄位的話，「還沒查」與「查過而進度真的是 0」在畫面上長得一模一樣。
    /// </remarks>
    public long UpdatedAtUtcTicks { get; set; }
}

/// <summary>成就進度追蹤。</summary>
public sealed class AchievementProgressTrackerConfig
{
    /// <summary>追蹤清單。</summary>
    public List<TrackedAchievementEntry> Tracked { get; set; } = [];

    /// <summary>「全部重新整理」時略過已達成的項目。</summary>
    public bool SkipCompletedOnRefresh { get; set; } = true;

    /// <summary>
    /// 每筆查詢之間的最短間隔（毫秒）。
    /// </summary>
    /// <remarks>
    /// 📌 預設 1000ms。伺服器對成就進度查詢的速率限制<b>未知</b>，
    /// 而遊戲本身只有一組進度欄位（一次只能有一筆在途），所以間隔留得保守。
    /// </remarks>
    public int RequestIntervalMs { get; set; } = 1000;

    /// <summary>等待伺服器回應的逾時（毫秒）。逾時只是放棄這一筆，不會改動已記錄的進度。</summary>
    public int ResponseTimeoutMs { get; set; } = 8000;
}

/// <summary>聊天座標自動開地圖。</summary>
public sealed class ChatCoordsOpenMapConfig
{
    /// <summary>
    /// 不處理的頻道（<see cref="Dalamud.Game.Text.XivChatType"/> 的數值）。
    /// </summary>
    /// <remarks>
    /// 🔴 刻意存<b>黑名單</b>而不是白名單：空集合＝全部頻道都處理，
    /// 所以之後在模組裡多列一個頻道時，既有使用者會自動吃到它。
    /// 反過來存白名單的話，新頻道對所有既有使用者都是靜默關閉的。
    /// </remarks>
    public HashSet<ushort> IgnoredChannels { get; set; } = [];

    /// <summary>同一個座標在幾秒內不重複開啟。</summary>
    public int DedupeSeconds { get; set; } = 5;

    /// <summary>副本中不自動開地圖。</summary>
    public bool SkipWhileBoundByDuty { get; set; } = true;

    /// <summary>開啟後在聊天視窗留一行（記錄一律會寫，不受這格影響）。</summary>
    public bool AnnounceInChat { get; set; }
}

/// <summary>F.A.T.E. 自動等級同步。</summary>
public sealed class FateLevelSyncConfig
{
    /// <summary>戰鬥中不動作。</summary>
    /// <remarks>📌 預設 <c>false</c>：F.A.T.E. 本來就是一進去就在打，等到脫離戰鬥往往已經打完了。</remarks>
    public bool SkipInCombat { get; set; }

    /// <summary>
    /// 送出「開啟」之後仍未同步時，改用無參數的切換再試一次。
    /// </summary>
    /// <remarks>
    /// 🔴 這件事之所以安全，唯一的理由是<b>送出前剛確認過 <c>SyncedFateId != 目前的 F.A.T.E.</c></b>
    /// （也就是現在確實是關的），所以切換的方向必然是「開」。
    /// 拿掉那道確認的話，這個選項就會變成「有機會把已經同步的狀態解除掉」。
    /// </remarks>
    public bool RetryWithToggle { get; set; } = true;

    /// <summary>送出指令後等多久再確認結果（毫秒）。</summary>
    public int VerifyDelayMs { get; set; } = 2500;

    /// <summary>同步成功時在聊天視窗留一行（記錄一律會寫，不受這格影響）。</summary>
    public bool AnnounceInChat { get; set; } = true;
}

/// <summary>箱類「全部開啟」。</summary>
public sealed class OpenAllCoffersConfig
{
    /// <summary>
    /// 每開一件之間的最短間隔（毫秒）。
    /// </summary>
    /// <remarks>
    /// 📌 預設 500ms。開箱是真的送到伺服器的道具使用，而且每一件都可能跳出獲得道具的訊息；
    /// 太快既沒有好處，也讓使用者來不及按停止。
    /// </remarks>
    public int StepIntervalMs = 500;

    /// <summary>結束時在聊天欄報告結果（記錄一律會寫，不受這格影響）。</summary>
    public bool NotifyOnFinish = true;
}

/// <summary>旗標指令（/tpflag、/gotoflag）。</summary>
public sealed class FlagCommandsConfig
{
    /// <summary>
    /// <c>/gotoflag</c> 允許 vnavmesh 使用飛行路徑。
    /// </summary>
    /// <remarks>
    /// 📌 預設 <c>false</c>，與其他導航呼叫端一致：在未解鎖飛行的區域開飛行會讓
    /// vnavmesh 算不出路徑，而失敗的樣子是「指令下了沒反應」。
    /// </remarks>
    public bool AllowFly;
}

/// <summary>點擊移動。</summary>
public sealed class ClickToMoveConfig
{
    /// <summary>
    /// 觸發用修飾鍵的 <c>VirtualKey</c> 值（0＝不需要修飾鍵，裸左鍵就觸發）。
    /// </summary>
    /// <remarks>
    /// 🔴 預設 <c>SHIFT</c>（16）而不是 0，這是刻意的：FFXIV 的左鍵拖曳是旋轉鏡頭、
    /// 左鍵點擊是選取目標。裸左鍵的話每一次轉鏡頭放開都會發一次尋路。
    /// 上游 CBT 就是裸左鍵，所以在 FFXIV 裡實際上很難用。
    /// <para>📌 想要真正的「點哪走哪」可以自己改成「無」——拖曳排除的防線仍然生效。</para>
    /// </remarks>
    public int ModifierKeyCode = 16;

    /// <summary>
    /// 允許 vnavmesh 使用飛行路徑。
    /// </summary>
    /// <remarks>
    /// 📌 預設 <c>false</c>，與 F.A.T.E. 總覽的導航一致：在未解鎖飛行的區域開飛行會讓
    /// vnavmesh 算不出路徑，而失敗的樣子是「點了沒反應」，比走路慢難察覺得多。
    /// </remarks>
    public bool AllowFly;

    /// <summary>每次開始移動時在聊天欄顯示一行（記錄一律會寫，不受這格影響）。</summary>
    /// <remarks>
    /// 📌 預設 <c>true</c>：這個功能會讓角色在沒有任何視窗開著的情況下跑起來，
    /// 一行「前往…（/tcstop 可停下）」是使用者唯一看得到的「是我自己觸發的」證據，
    /// 也順便把停止指令講出來。
    /// </remarks>
    public bool NotifyOnMove = true;
}

/// <summary>貨幣上限警示。</summary>
public sealed class CurrencyCapAlertConfig
{
    /// <summary>持有量達到上限的百分之多少就警示。</summary>
    /// <remarks>
    /// 📌 預設 90：剩最後一成通常還來得及安排一次兌換，而更早提醒會在整個版本週期裡一直亮著。
    /// </remarks>
    public int ThresholdPercent = 90;

    /// <summary>
    /// 跨過門檻時在聊天欄提醒一次。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>邊緣觸發</b>：只在「剛跨過」的那一次印，持續超標期間不會再印
    /// （掉回門檻以下後再超過才會再提醒）。所以預設開啟不會造成洗版。
    /// </remarks>
    public bool NotifyInChat = true;

    /// <summary>同時監看神典石的每週取得上限（與持有上限是兩回事）。</summary>
    public bool WatchWeeklyTomestone = true;

    /// <summary>
    /// <b>不</b>要監看的貨幣道具編號。
    /// </summary>
    /// <remarks>
    /// 🔴 刻意做成「排除清單」而不是「監看清單」：貨幣種類會隨版本增加，
    /// 用監看清單的話新貨幣預設不會被看到，而且沒有任何徵兆
    /// （使用者要先知道有新貨幣、才會想到來這裡勾它）。排除清單則是新貨幣自動納入。
    /// <para>📌 預設空集合＝全部監看。</para>
    /// </remarks>
    public HashSet<uint> IgnoredItemIds { get; set; } = [];
}

/// <summary>背包堆疊合併。</summary>
/// <remarks>
/// 📌 這裡沒有「啟用」欄位是刻意的：模組本身預設就是關的，而且開著也要按按鈕才會動
/// （<see cref="TCToolbox.Core.TcModule.IsManualTrigger"/>），不需要第二段開關。
/// </remarks>
public sealed class AutoMergeConfig
{
    /// <summary>
    /// 每一次搬移之間的最短間隔（毫秒）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 這是<b>會真的送到伺服器</b>的搬移，不是本機動畫。預設 300ms 是保守值：
    /// 合併 20 格要 6 秒，慢得看得出來，但也因此使用者來得及按「停止合併」。
    /// </remarks>
    public int StepIntervalMs = 300;

    /// <summary>結束時在聊天欄報告結果（記錄一律會寫，不受這格影響）。</summary>
    public bool NotifyOnFinish = true;
}

/// <summary>FATE 總覽與導航。</summary>
public sealed class FateTrackerConfig
{
    /// <summary>
    /// 導航時允許 vnavmesh 用飛行路徑。
    /// </summary>
    /// <remarks>
    /// 📌 預設 <c>false</c>（沿用既有 <see cref="TCToolbox.Core.ExternalNav.TryMoveTo"/> 呼叫端的保守值）。
    /// 開飛行在未解鎖飛行的區域會讓 vnavmesh 算不出路徑而整個導航失敗，
    /// 而失敗的樣子是「按了沒反應」，比走路慢一點難察覺得多。
    /// </remarks>
    public bool AllowFly;

    /// <summary>
    /// 清單依距離由近到遠排序。
    /// </summary>
    /// <remarks>
    /// 🔴 預設 <c>false</c>（依 FateId 的穩定順序）<b>是刻意的，不是偷懶</b>：
    /// 依距離排序時，玩家一移動列就會互換位置，而這個清單上每一列都有一顆會真的
    /// 讓角色跑起來的「導航」按鈕——列在游標下方跳動＝按錯目標。
    /// 要距離資訊的人看得到那一欄，不必靠排序。
    /// </remarks>
    public bool SortByDistance;

    /// <summary>連已結束／已失敗的 FATE 也顯示。</summary>
    /// <remarks>預設 <c>false</c>：結束的 FATE 幾秒後就會從表裡消失，留著只是讓清單抖動。</remarks>
    public bool ShowEnded;
}

/// <summary>商店介面預設值。</summary>
public sealed class ShopDefaultsConfig
{
    /// <summary>
    /// 軍票商店開啟時要切到的分頁序（0 起算，對應 <c>GCShopItemCategory</c> 裡有名字的列的順序）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 預設 <c>-1</c>＝不切換，也就是<b>維持遊戲原本的行為</b>。
    /// 舊設定檔沒有這個欄位，反序列化不會覆寫欄位初始值，所以升級不會讓人突然被換分頁。
    /// </remarks>
    public int GrandCompanyDefaultTab = -1;
}

/// <summary>
/// 自動取出全部魔晶石。
/// ⚠️ 模組本身預設是關的（<c>EnabledModules</c> 不含它），所以這裡的
/// <see cref="Enabled"/> 預設 <c>true</c> 不會讓任何人被動生效 ——
/// 它是「模組開著時要不要接手」的第二段開關，讓人可以暫時停掉接手而不必整個模組關掉。
/// </summary>
public sealed class AutoMateriaRetrieveAllConfig
{
    /// <summary>接手開關。模組開著但這格關掉＝完全維持遊戲原本的行為。</summary>
    public bool Enabled = true;

    /// <summary>每顆之間的最短間隔（毫秒）。</summary>
    public int DelayMs = 500;

    /// <summary>把結果印到聊天視窗（記錄一律會寫，不受這格影響）。</summary>
    public bool AnnounceInChat = true;
}

public sealed class OptimizedFreeCompanyChestConfig
{
    /// <summary>
    /// 開啟部隊置物櫃時要切到的頁面。
    /// ⚠️ 預設 <see cref="InventoryType.Invalid"/>＝不切換，也就是<b>維持遊戲原本的行為</b>。
    /// 舊設定檔沒有這個欄位，反序列化不會覆寫欄位初始值，所以升級不會讓人突然被換頁。
    /// ⚠️ 水晶頁是 <c>FreeCompanyCrystals</c>(22001)，不是「20000＋5」——不要用頁序去算。
    /// </summary>
    public InventoryType DefaultPage = InventoryType.Invalid;
}

/// <summary>
/// 自動繳交道具時，同一款道具有 NQ／HQ 兩種可選時要先挑哪一種。
/// </summary>
/// <remarks>
/// 🔴 這是「偏好」不是「限定」：偏好的那一種在候選清單裡找不到時會退回另一種，
/// 不會因為挑不到就卡住不交。
/// </remarks>
public enum HandInQualityPreference
{
    /// <summary>不挑，照遊戲給的候選順序取第一個（＝本設定加入之前的行為）。</summary>
    None = 0,

    /// <summary>優先挑優質（HQ）；沒有 HQ 候選時退回普通品。</summary>
    PreferHighQuality = 1,

    /// <summary>優先挑普通品（NQ）；沒有 NQ 候選時退回 HQ。</summary>
    PreferNormalQuality = 2,
}

public sealed class AutoRequestItemSubmitConfig
{
    /// <summary>每一步（填一格／按一次鈕）之間的最短間隔（毫秒）。</summary>
    public int DelayMs = 500;

    /// <summary>自動確認「確定要交易優質道具嗎？」這扇確認框。</summary>
    public bool ConfirmHighQuality = true;

    /// <summary>
    /// NQ／HQ 都符合要求時先挑哪一種。預設 <see cref="HandInQualityPreference.None"/>，
    /// 也就是維持「取遊戲給的第一個候選」這個既有行為。
    /// </summary>
    public HandInQualityPreference QualityPreference = HandInQualityPreference.None;
}

public sealed class HuijiWikiContextMenuConfig
{
    /// <summary>開啟瀏覽器後在聊天欄顯示訊息。</summary>
    public bool NotifyOnOpen;
}

public sealed class WeeklyBingoClickToOpenConfig
{
    /// <summary>成功開啟副本時在聊天欄顯示一行。</summary>
    public bool NotifyOnOpen = true;

    /// <summary>格子對不到副本時在聊天欄說明原因（關掉的話點下去就完全沒反應，只有 log 有記錄）。</summary>
    public bool NotifyWhenUnresolved = true;
}

public sealed class GlamourSetRetrieveConfig
{
    /// <summary>只取投影台裡湊得齊整組的裝備組合（關閉後會拆散不完整的組合）。</summary>
    public bool OnlyCompleteSets = true;

    /// <summary>跳過已染色的幻影（染過色的通常是特地調過的外觀）。</summary>
    public bool SkipDyedItems = true;
}

public sealed class GlamourDuplicateCleanupConfig
{
    /// <summary>
    /// 把「染色不同」的同款裝備視為不同幻影，不當成重複。
    /// ⚠️ 預設 <c>true</c>，與 DailyRoutines 的行為不同（它只看道具編號）——
    /// 同一件裝備染兩個顏色在投影台裡是兩種可用外觀，只看編號會把其中一種取走。
    /// 要完全比照 DR 就關掉它。
    /// </summary>
    public bool DistinguishByDye = true;
}

public sealed class MoveGearsNotInSetConfig
{
    /// <summary>
    /// 連套裝指定的「投影來源」裝備一起保護。預設開啟：搞錯的方向是「少搬幾件」，
    /// 而反方向（搬走還在用的投影來源）要重新設定一輪外觀。
    /// </summary>
    public bool ProtectGlamourSources = true;
}

public sealed class CopyItemNameContextMenuConfig
{
    /// <summary>複製後在聊天欄顯示訊息。</summary>
    public bool NotifyOnCopy = true;
}

public sealed class AutoCustomDeliveryResultConfig
{
    /// <summary>送出確認的最短間隔（毫秒），同時也是視窗沒關掉時的重送間隔。</summary>
    public int DelayMs = 500;
}

public sealed class AutoQuestAcceptConfig
{
    /// <summary>任務受理視窗出現後，等這麼久（毫秒）才按下「接受」。</summary>
    public int DelayMs = 500;
}

public sealed class AutoRefocusConfig
{
    /// <summary>只在副本內生效。</summary>
    public bool OnlyInDuty = true;

    /// <summary>恢復焦點時寫一筆 Information 記錄（已節流）。</summary>
    public bool NotifyOnRestore;
}

/// <summary>
/// 解除互動限制的逐項開關。影響範圍窄的預設開啟，會波及互動以外系統的預設關閉
/// （理由與呼叫端數量寫在 <see cref="Modules.OptimizedInteraction"/> 的型別註解）。
/// </summary>
public sealed class OptimizedInteractionConfig
{
    /// <summary>無視「目標處於視野之外」。</summary>
    public bool IgnoreViewRange = true;

    /// <summary>無視「目標被物件遮擋」。</summary>
    public bool IgnoreCameraBlocked = true;

    /// <summary>無視「目標位置過高過低」。</summary>
    public bool IgnoreTargetPosition = true;

    /// <summary>無視「距離太遠」。</summary>
    public bool IgnoreDistance = true;

    /// <summary>無視「跳躍中無法操作」。⚠️ 兩支狀態判定各有 49／46 個呼叫端，預設關閉。</summary>
    public bool IgnoreJumping;

    /// <summary>無視「騎乘／低空飛行中」。⚠️ 這支在事件腳本條件判定器裡，不在互動閘門上，預設關閉。</summary>
    public bool IgnoreMountFlight;
}

public sealed class PFPageSizeCustomizeConfig
{
    /// <summary>招募板單頁筆數（1–100）；預設維持遊戲自己的 50。</summary>
    public int PageSize = 50;
}

public sealed class AutoClaimPVPRewardsConfig
{
    /// <summary>戰利水晶持有量到達此數就停止領取（上限 20000，超出的部分會消失）。</summary>
    public int StopAtTrophyCrystals = 19000;
}

public sealed class AutoRefreshPartyFinderConfig
{
    /// <summary>自動刷新間隔（秒）。</summary>
    public int IntervalSeconds = 30;

    /// <summary>清單自己更新過就重新計時。</summary>
    public bool OnlyWhenIdle = true;

    /// <summary>在招募板上方顯示倒數與立即刷新鈕。</summary>
    public bool ShowCountdown = true;
}

public sealed class OptimizedFreeShopConfig
{
    /// <summary>領取時自動按掉確認對話框（只在「報酬」視窗開著時生效）。</summary>
    public bool SkipConfirmation = true;
}

public sealed class AutoHideNeedlessPopupsConfig
{
    /// <summary>是否已套用過首次預設值（避免使用者清空後又被塞回來）。</summary>
    public bool Initialized;

    /// <summary>要自動關閉的彈窗 addon 名稱。</summary>
    public HashSet<string> HiddenPopups { get; set; } = [];
}

public sealed class OptimizedTargetInfoConfig
{
    public bool ShowHp = true;
    public bool CompactNumbers = true;
    public bool ShowHpPercent;
    public bool ShowCastRemaining = true;
    public bool ShowClearFocusButton;
    public float TextScale = 0.9f;
    public float TargetOffsetX = 8f;
    public float TargetOffsetY = -18f;
    public float FocusOffsetX = 8f;
    public float FocusOffsetY = -18f;
    public float ClearFocusButtonOffsetX = -22f;
    public float ClearFocusButtonOffsetY = 0f;
}

public sealed class AutoInventoryTransferConfig
{
    /// <summary>觸發用修飾鍵的 VirtualKey 值（0＝停用）。</summary>
    public int ModifierKeyCode;

    /// <summary>轉移後在聊天欄顯示訊息。</summary>
    public bool NotifyOnTransfer = true;
}

/// <summary>敵對列表疊圖的詠唱顯示方式。</summary>
public enum CastDisplayMode
{
    /// <summary>完全不顯示詠唱資訊。</summary>
    Off = 0,

    /// <summary>只印剩餘秒數，技名交給原生詠唱列。</summary>
    SecondsOnly = 1,

    /// <summary>技名與秒數都印。</summary>
    NameAndSeconds = 2,

    /// <summary>依遊戲的「敵對列表詠唱列」設定自動決定（原生有顯示就只印秒數）。</summary>
    Auto = 3,
}

public sealed class OptimizedEnemyListConfig
{
    public bool ShowHp = true;
    public bool CompactNumbers = true;
    public bool ShowHpPercent;

    /// <summary>詠唱顯示方式；預設自動——原生詠唱列開著時只印秒數，避免技名重複疊字。</summary>
    public CastDisplayMode CastDisplay = CastDisplayMode.Auto;

    public bool HighlightTargetingYou = true;
    public float TextScale = 0.9f;

    /// <summary>疊圖畫在整列的右側外緣（false＝左側外緣）。</summary>
    public bool AnchorRight = true;

    /// <summary>離開列邊緣的距離（兩側都是正值往外）。</summary>
    public float OffsetX = 6f;

    /// <summary>相對垂直置中的微調。</summary>
    public float OffsetY;
}

public sealed class MarkerInPartyListConfig
{
    /// <summary>疊圖標記邊長（未乘介面縮放）。</summary>
    public float IconSize = 24f;

    /// <summary>相對隊員欄位左上角的偏移。</summary>
    public float OffsetX = 40f;

    public float OffsetY = 4f;

    /// <summary>有標記時隱藏小隊列表原生的隊員序號。</summary>
    public bool HideMemberNumbers = true;
}

public sealed class AutoRetargetConfig
{
    /// <summary>搜尋敵人的最大距離（公尺）。</summary>
    public float MaxDistance = 30f;

    /// <summary>輪詢間隔（毫秒）。</summary>
    public int PollIntervalMs = 300;

    /// <summary>迷失者／迷失少女優先（會搶走既有目標）。</summary>
    public bool PrioritizeForlorn = true;

    /// <summary>只在戰鬥中生效。</summary>
    public bool OnlyInCombat;
}

public sealed class AutoHideBannersConfig
{
    /// <summary>是否已套用過首次預設值（避免使用者清空後又被塞回來）。</summary>
    public bool Initialized;

    /// <summary>要屏蔽的橫幅圖示 ID。</summary>
    public HashSet<uint> HiddenBanners { get; set; } = [];

    /// <summary>設定畫面是否顯示橫幅預覽圖。</summary>
    public bool ShowPreview = true;
}

public sealed class OptimizedDutyFinderSettingConfig
{
    /// <summary>疊圖按鈕邊長（像素）。</summary>
    public float ButtonSize = 30f;

    /// <summary>是否一併顯示語言按鈕。</summary>
    public bool ShowLanguageButtons;
}

public sealed class AutoPlayerCommendConfig
{
    /// <summary>跳過遊戲黑名單內的玩家。</summary>
    public bool IgnoreBlacklistedPlayers = true;

    /// <summary>推薦後在聊天欄顯示訊息。</summary>
    public bool NotifyOnCommend = true;
}

public sealed class AutoConstantlyClickConfig
{
    /// <summary>按住期間的重複觸發間隔（毫秒）。鍵盤／滑鼠與手把共用。</summary>
    public int RepeatIntervalMs = 200;

    /// <summary>
    /// 手把的十字熱鍵也套用連發。
    /// </summary>
    /// <remarks>
    /// 🔴 預設 <c>false</c>＝<b>與這個選項加入之前的行為完全一樣</b>。
    /// 舊設定檔沒有這個鍵，反序列化不會覆寫欄位初始值，所以升級上來的人不會突然多出一種行為。
    /// <para>
    /// 📌 範圍只含十字熱鍵的動作格（<c>HOT_PAD_LL</c>–<c>HOT_PAD_RD_R</c>，194–218）。
    /// L2／R2 扳機（191／192）與切換組（193）刻意排除：那三個連發會讓十字熱鍵
    /// 在按住期間不停開關或跳組。
    /// </para>
    /// </remarks>
    public bool IncludeGamepadHotbar;
}

public sealed class AutoAntiAfkConfig
{
    /// <summary>是否連一般閒置計時器（待機動作／鏡頭回正）也一併重置。</summary>
    public bool ResetIdleAnimationTimer;
}

public sealed class AutoGysahlGreensConfig
{
    /// <summary>剩餘時間低於此分鐘數時自動餵食。</summary>
    public int ThresholdMinutes = 5;

    /// <summary>餵食後在聊天欄顯示訊息。</summary>
    public bool NotifyOnFeed = true;
}

public sealed class AutoCountPlayersConfig
{
    /// <summary>戰鬥中隱藏伺服器資訊列項目（PvP 區域除外的副本戰鬥干擾）。</summary>
    public bool HideInCombat = true;

    /// <summary>玩家偵測規則（命中時通知／執行指令）。</summary>
    public List<PlayerWatchRule> WatchRules { get; set; } = [];
}

/// <summary>
/// 偵測規則裡多個條件之間的組合方式。
/// </summary>
/// <remarks>
/// ⚠️ 一定要有零值：舊設定檔沒有這個鍵時反序列化會落在欄位初始值，
/// 而新規則的 <c>default</c> 也是零值——沒有零值的列舉會讓兩者落在無效狀態上。
/// </remarks>
public enum WatchRuleMatchMode
{
    /// <summary>
    /// 任一啟用中的條件命中就觸發。
    /// <para>預設值，理由是「警報」的語意是寧可多響不可漏響。</para>
    /// </summary>
    Any = 0,

    /// <summary>所有啟用中的條件都命中才觸發（用來收斂範圍，例如「特定伺服器的遊戲管理員」）。</summary>
    All = 1,
}

/// <summary>
/// 周邊玩家偵測規則：玩家出現且<b>條件命中</b>時通知並執行指令。
/// </summary>
/// <remarks>
/// <para>
/// 判定條件可以多選、可組合，名稱只是其中一種。目前支援四種條件，每種都有自己的啟用開關：
/// 名稱（<see cref="MatchName"/>）、線上狀態（<see cref="MatchOnlineStatus"/>）、
/// 部隊標籤（<see cref="MatchCompanyTag"/>）、距離（<see cref="MatchMaxDistance"/>）。
/// </para>
/// <para>
/// 🔴 <b>所有新增欄位的預設值都等於「這個條件不啟用」</b>，唯一的例外是
/// <see cref="MatchName"/>＝<c>true</c>——那正是多條件化之前的既有行為。
/// 舊設定檔沒有這些鍵，反序列化不會覆寫欄位初始值，因此升級後行為逐字不變。
/// </para>
/// </remarks>
public sealed class PlayerWatchRule
{
    public bool Enabled = true;

    /// <summary>比對樣式；<see cref="UseRegex"/> 時為 .NET 正規表達式，否則須與名稱完全相符。</summary>
    public string Pattern = string.Empty;

    /// <summary>以正規表達式比對（不分大小寫、部分符合即命中）。</summary>
    public bool UseRegex = true;

    /// <summary>比對對象改為「名稱@伺服器」而非只有名稱。</summary>
    public bool MatchWithWorld;

    /// <summary>命中時逐行執行的斜線指令；支援 {name}／{world}／{job} 佔位符。</summary>
    public string Command = string.Empty;

    /// <summary>命中時在聊天欄顯示通知。</summary>
    public bool NotifyChat = true;

    /// <summary>同一位玩家再次觸發的冷卻（秒）。</summary>
    public int CooldownSeconds = 300;

    // ── 以下為多條件判定（2026-08-07 新增；預設值一律等於既有行為）──────────────

    /// <summary>條件之間的組合方式。預設 <see cref="WatchRuleMatchMode.Any"/>。</summary>
    /// <remarks>只有一個條件啟用時，Any 與 All 完全等價，所以舊規則不受影響。</remarks>
    public WatchRuleMatchMode MatchMode = WatchRuleMatchMode.Any;

    /// <summary>
    /// 啟用「名稱」條件（<see cref="Pattern"/>／<see cref="UseRegex"/>／<see cref="MatchWithWorld"/>）。
    /// </summary>
    /// <remarks>🔴 預設 <c>true</c>：這是多條件化之前唯一存在的條件，改成 false 會讓既有規則全部失效。</remarks>
    public bool MatchName = true;

    /// <summary>啟用「線上狀態」條件。</summary>
    public bool MatchOnlineStatus;

    /// <summary>
    /// 要命中的 <c>OnlineStatus</c> 列號集合（空集合＝條件視為未啟用）。
    /// </summary>
    /// <remarks>
    /// 台服 7.20 的 <c>OnlineStatus</c> 全表 48 列（0–47），其中
    /// <b>列 2 與列 3 的名稱都是「遊戲管理員」</b>（離線比對 <c>exd-tc/7.20/OnlineStatus.csv</c>），
    /// 所以偵測 GM 要兩個都收。
    /// </remarks>
    public HashSet<uint> OnlineStatuses { get; set; } = [];

    /// <summary>啟用「部隊標籤」條件。</summary>
    public bool MatchCompanyTag;

    /// <summary>部隊標籤比對樣式（空字串＝條件視為未啟用）。</summary>
    public string CompanyTagPattern = string.Empty;

    /// <summary>部隊標籤以正規表達式比對。</summary>
    public bool CompanyTagUseRegex = true;

    /// <summary>啟用「距離」條件。</summary>
    public bool MatchMaxDistance;

    /// <summary>距離條件的上限（公尺）：玩家與自己的距離小於等於此值即命中。</summary>
    public float MaxDistance = 30f;
}

public sealed class AutoGardensWorkConfig
{
    /// <summary>播種用種子 ItemId。</summary>
    public uint SeedItemId;

    /// <summary>播種用土壤 ItemId。</summary>
    public uint SoilItemId;

    /// <summary>施肥用肥料 ItemId（預設 7767 魚粉）。</summary>
    public uint FertilizerItemId = 7767;
}

/// <summary>信箱一鍵收取。</summary>
/// <remarks>
/// 📌 這裡沒有「啟用」欄位是刻意的：模組本身預設就是關的，而且開著也要按按鈕才會動
/// （<see cref="TCToolbox.Core.TcModule.IsManualTrigger"/>），不需要第二段開關。
/// </remarks>
public sealed class LetterCollectAllConfig
{
    /// <summary>
    /// 每一步之間的最短間隔（毫秒）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 每一步都是真的送到伺服器的操作。預設 500ms 是保守值：慢得看得出來，
    /// 但也因此使用者來得及按「停止」。
    /// </remarks>
    public int StepIntervalMs = 500;

    /// <summary>
    /// 自動按掉收取途中出現的確認框。
    /// </summary>
    /// <remarks>
    /// 📌 預設開啟，但作用範圍被壓到很窄：<b>只在本模組的佇列正在跑，而且信箱與信件視窗都開著時</b>
    /// 才會按。也就是說時間窗只有使用者自己按下按鈕之後的那幾秒。
    /// <para>
    /// ⚠️ 關掉的話，真的跳出確認框時那一輪會停在原地直到逾時。
    /// 無論按不按，確認框的文字一律寫進記錄——台服到底會不會跳、跳哪一句，離線查不出來。
    /// </para>
    /// </remarks>
    public bool AutoConfirm = true;

    /// <summary>結束時在聊天欄報告結果（記錄一律會寫，不受這格影響）。</summary>
    public bool NotifyOnFinish = true;
}

/// <summary>副本開始／結束播報。</summary>
public sealed class DutyAnnounceConfig
{
    /// <summary>副本開始時送出的訊息（空字串＝不送）。</summary>
    /// <remarks>📌 預設空字串：這個模組唯一的行為就是送使用者寫的字，沒寫就什麼都不該送。</remarks>
    public string StartMessage = string.Empty;

    /// <summary>
    /// 開始訊息要送到的頻道（<c>TextCommand</c> 表的列號）。
    /// </summary>
    /// <remarks>
    /// 🔴 預設是<b>默語</b>（116）而不是上游的小隊頻道：預設值送出的東西必須是
    /// 「就算設錯也不會打擾任何人」的。要送給隊友的人自己改一格就好，
    /// 反方向（預設就往公開頻道送）出錯的代價是每一場副本對整隊洗一次版。
    /// </remarks>
    public uint StartChannelRow = TCToolbox.Core.TextCommands.ChatChannelRows.Echo;

    /// <summary>副本通關時送出的訊息（空字串＝不送）。</summary>
    public string EndMessage = string.Empty;

    /// <summary>結束訊息要送到的頻道（<c>TextCommand</c> 表的列號）。預設默語，理由同上。</summary>
    public uint EndChannelRow = TCToolbox.Core.TextCommands.ChatChannelRows.Echo;

    /// <summary>
    /// 事件觸發之後先等這麼久（毫秒）才送出。
    /// </summary>
    /// <remarks>
    /// 📌 預設 1000ms：通關那一瞬間畫面還在演出、系統訊息也正在刷，太早送出容易被淹掉。
    /// ⚠️ 等待期間離開副本區域的話這次播報會被取消——否則那句話會送到副本外面去。
    /// </remarks>
    public int DelayMs = 1_000;
}

/// <summary>指令開啟任務搜尋器。</summary>
public sealed class ContentFinderCommandConfig
{
    /// <summary>開啟搜尋器後在聊天欄提示（記錄一律會寫，不受這格影響）。</summary>
    public bool NotifyOnOpen = true;
}

/// <summary>任務搜尋器快速登記面板的一筆收藏。</summary>
public sealed class FavoriteDuty
{
    /// <summary>ContentsId 類型：1＝隨機任務（ContentRoulette）、2＝一般副本（ContentFinderCondition）。</summary>
    public byte ContentType { get; set; }

    /// <summary>列號（依 <see cref="ContentType"/> 指向 ContentRoulette 或 ContentFinderCondition）。</summary>
    public uint Id { get; set; }

    /// <summary>收藏當下的名稱快取。查表查得到時以查表為準，這只是查不到時的備援顯示。</summary>
    public string Name { get; set; } = string.Empty;
}

/// <summary>任務搜尋器快速登記。</summary>
public sealed class FastContentsFinderRegisterConfig
{
    /// <summary>是否在任務搜尋器旁顯示快速登記面板。</summary>
    public bool ShowOverlay = true;

    /// <summary>開啟／選取副本後在聊天欄提示。</summary>
    public bool NotifyOnOpen = true;

    /// <summary>收藏的常用副本清單。</summary>
    public System.Collections.Generic.List<FavoriteDuty> Favorites { get; set; } = new();
}

/// <summary>登入後執行自訂指令。</summary>
public sealed class LoginCommandsConfig
{
    /// <summary>
    /// 要執行的指令，一行一條。
    /// </summary>
    /// <remarks>
    /// 📌 預設空字串＝<b>開了模組也什麼都不會做</b>。這是刻意的：這個模組唯一的行為就是
    /// 執行使用者寫的東西，沒寫就沒有預設動作可言，任何「範例指令」放進預設值都會變成
    /// 一條真的被送出去的指令。
    /// </remarks>
    public string Commands = string.Empty;

    /// <summary>
    /// 角色資料就緒之後再等這麼久（毫秒）才跑第一條。
    /// </summary>
    /// <remarks>
    /// 📌 預設 5000ms。太短的話很多外掛的指令還沒註冊，送出去只會得到「查無此指令」，
    /// 而那個失敗<b>不會重試</b>——寧可慢，不要靜默漏跑。
    /// </remarks>
    public int InitialDelayMs = 5_000;

    /// <summary>每條指令之間的間隔（毫秒）。</summary>
    public int IntervalMs = 1_000;

    /// <summary>
    /// AutoRetainer 正在作業時整輪略過。
    /// </summary>
    /// <remarks>
    /// 📌 預設開啟：AutoRetainer 的多角色模式自己會反覆登入登出，那種登入不是「使用者要開始玩了」，
    /// 此時插一輪指令進去多半只會打斷它。AutoRetainer 沒安裝時這格沒有作用。
    /// </remarks>
    public bool SkipWhenAutoRetainerBusy = true;

    /// <summary>執行時在聊天欄顯示一行（記錄一律會寫，不受這格影響）。</summary>
    /// <remarks>
    /// 📌 預設 <c>true</c>：登入之後突然有一串指令自己跑起來，這一行是使用者唯一看得到的
    /// 「這是我自己設定的」證據。
    /// </remarks>
    public bool NotifyInChat = true;
}

/// <summary>雇員存取加速（同款全部）。</summary>
public sealed class FastRetainerStoreConfig
{
    /// <summary>每搬一格之間的最短間隔（毫秒）。每一格都是真的送到伺服器的命令。</summary>
    public int StepIntervalMs = 200;

    /// <summary>結束時在聊天欄報告（記錄一律會寫，不受這格影響）。</summary>
    public bool NotifyOnFinish = true;
}

/// <summary>軍票商店快速交換。記住上次輸入的道具名與數量。</summary>
public sealed class FastGrandCompanyExchangeConfig
{
    /// <summary>上次輸入的道具名（可為片段）。</summary>
    public string ItemName = string.Empty;

    /// <summary>要交換的數量；-1＝可負擔上限。預設 1（不預設為花光全部軍票）。</summary>
    public int Count = 1;
}

/// <summary>商店快速購買（預設）。</summary>
public sealed class AutoShopPurchaseConfig
{
    /// <summary>已擷取的購買預設清單。</summary>
    public List<AutoShopPurchasePreset> Presets { get; set; } = [];
}

/// <summary>一個商店購買預設：在哪個視窗、點清單的哪一項。node id 與索引都是現地擷取的。</summary>
public sealed class AutoShopPurchasePreset
{
    public string Name { get; set; } = string.Empty;
    public string AddonName { get; set; } = string.Empty;
    public uint ListNodeId { get; set; }
    public int ClickIndex { get; set; }

    /// <summary>綁定的對象 NPC 名稱；非空時執行前會比對目前目標，不符即拒絕。</summary>
    public string TargetName { get; set; } = string.Empty;
}

/// <summary>道具丟棄清單。使用者自己維護要丟的道具，走遊戲原生「捨棄」，確認框一律由人按。</summary>
public sealed class DiscardListConfig
{
    /// <summary>
    /// 要納入丟棄清單的道具 base id（不含 HQ 位移）。
    /// </summary>
    /// <remarks>
    /// 📌 預設空清單＝開了模組也不會列出任何東西、更不會丟任何東西。這是刻意的：
    /// 這個模組唯一會動到的道具就是使用者自己放進這份清單的，任何預設項目都可能變成一件真的被丟掉的道具。
    /// </remarks>
    public List<uint> Items { get; set; } = [];

    /// <summary>整批發起時每一步之間的最短間隔（毫秒）。</summary>
    public int StepIntervalMs = 250;

    /// <summary>結束時在聊天欄報告（記錄一律會寫，不受這格影響）。</summary>
    public bool NotifyInChat = true;
}

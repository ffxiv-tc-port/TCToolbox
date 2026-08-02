using System.Collections.Generic;
using Dalamud.Configuration;

namespace TCToolbox;

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    /// <summary>已啟用模組的 InternalName 清單。</summary>
    public HashSet<string> EnabledModules { get; set; } = [];

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

    public void Save() => Svc.PluginInterface.SavePluginConfig(this);
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
    /// <summary>按住期間的重複觸發間隔（毫秒）。</summary>
    public int RepeatIntervalMs = 200;
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

/// <summary>周邊玩家偵測規則：玩家出現且名稱命中時通知並執行指令。</summary>
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

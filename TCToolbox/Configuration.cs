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

    public void Save() => Svc.PluginInterface.SavePluginConfig(this);
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

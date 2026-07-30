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

    public void Save() => Svc.PluginInterface.SavePluginConfig(this);
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

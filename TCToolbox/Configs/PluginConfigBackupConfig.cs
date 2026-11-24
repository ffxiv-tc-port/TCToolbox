using System.Collections.Generic;
using TCToolbox.Core;

namespace TCToolbox;

/// <summary><see cref="Modules.PluginConfigBackup"/> 的設定。</summary>
public sealed class PluginConfigBackupConfig
{
    /// <summary>至少保留最新的幾份（值域 3～30，讀取時 Clamp）。</summary>
    public int KeepRecent { get; set; } = 5;

    /// <summary>最近幾天每天保留一份（值域 1～60，讀取時 Clamp）。</summary>
    public int KeepDays { get; set; } = 7;

    /// <summary>使用者說過「這是我自己改的」的檔：相對路徑 → 當時的大小與修改時間。檔案再變動就失效。</summary>
    public Dictionary<string, ConfigBackupAck> Acknowledged { get; set; } = [];
}

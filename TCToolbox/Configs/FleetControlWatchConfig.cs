using System.Collections.Generic;
using TCToolbox.Core;

namespace TCToolbox;

/// <summary>「誰在控制：跑完了叫我」的設定。</summary>
/// <remarks>
/// 📌 模組本身<b>預設關閉</b>（所有模組都是），所以升級上來的人不會多出任何行為。
/// </remarks>
public sealed class FleetControlWatchConfig
{
    /// <summary>多久問一次（秒）。實際使用時會夾回模組定義的上下限。</summary>
    public int PollSeconds { get; set; } = 10;

    /// <summary>同一個來源最多多久通知一次（分鐘）。</summary>
    public int MinIntervalMinutes { get; set; } = 10;

    /// <summary>全艦隊急停之後安靜幾秒（0＝不做這道過濾）。</summary>
    public int EmergencyStopQuietSeconds { get; set; } = 120;

    /// <summary>不要通知的來源。</summary>
    /// <remarks>
    /// 🔴 <b>初始式刻意留空。</b>Dalamud 的 <c>GetPluginConfig()</c> 對集合欄位是<b>併入</b>：
    /// 初始式裡放東西的話，使用者永遠刪不掉它，而且完全沒有徵兆。
    /// 📌 鍵是 <c>FleetControlStatus.Subject*</c> 常數；認不得的鍵只忽略不清除。
    /// </remarks>
    public HashSet<string> MutedSubjects { get; set; } = [];
}

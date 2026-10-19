using System;
using System.Collections.Generic;

namespace TCToolbox;

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

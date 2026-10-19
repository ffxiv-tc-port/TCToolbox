using System.Collections.Generic;

namespace TCToolbox;

/// <summary>一份「什麼時候吃什麼食物」的設定。</summary>
public sealed class FoodPreset
{
    /// <summary>食物道具編號（NQ 的 Item RowId）。</summary>
    public uint ItemId { get; set; }

    public bool IsHq { get; set; } = true;

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

    public List<FoodPreset> Presets { get; set; } = [];
}

using System.Collections.Generic;

namespace TCToolbox;

/// <summary>移動速度倍率（限白名單地圖）。</summary>
/// <remarks>
/// 🔴 <b>三道預設全部是「不動作」</b>：模組本身預設關（不在 <c>EnabledModules</c> 裡）、
/// 倍率預設 1.00（＝原速）、白名單預設是空集合。三者任一沒被使用者主動改過，
/// 這個模組對遊戲就是完全無影響的。
/// </remarks>
public sealed class MovementSpeedMultiplierConfig
{
    /// <summary>移動速度倍率。1.00 ＝ 原速。</summary>
    /// <remarks>
    /// 🔴 讀取端一律會夾在 <c>MovementSpeedMultiplier.MinMultiplier</c>～<c>MaxMultiplier</c>
    /// （1.00～1.50）之間，所以就算有人手改設定檔寫了 10，實際生效的仍然是 1.50。
    /// <b>夾限不能只做在 UI 上</b>——滑桿擋得住使用者，擋不住文字檔。
    /// </remarks>
    public float Multiplier { get; set; } = 1.0f;

    /// <summary>
    /// 允許生效的地圖（<c>TerritoryType</c> id，即 <c>ClientState.TerritoryType</c>）。副本內外都算。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>空集合＝任何地圖都不生效</b>，這是刻意的預設，不要改成「空＝全部」。
    /// 「沒設定就到處生效」正是這個設定要避免的東西。
    /// 🔴 初始式刻意留空：<c>GetPluginConfig()</c> 對集合欄位是併入，初始式放東西使用者就刪不掉。
    /// </remarks>
    public HashSet<uint> TerritoryWhitelist { get; set; } = [];

    /// <summary>
    /// 舊版的副本白名單（<c>ContentFinderCondition</c> 列號）。<b>不再用於判斷</b>，只留著備查。
    /// </summary>
    /// <remarks>
    /// 設定 v5 升級時已換成地圖併進 <see cref="TerritoryWhitelist"/>（<c>Configuration.MigrateSpeedWhitelistToTerritories</c>）。
    /// 🔴 這是使用者的資料：不要刪這個欄位、也不要清空它；舊版插件讀的仍是它。
    /// </remarks>
    public HashSet<uint> DutyWhitelist { get; set; } = [];

    /// <summary>只在非戰鬥中生效。</summary>
    /// <remarks>
    /// 📌 預設 <c>true</c>：戰鬥中的位移最容易被伺服器端注意到，
    /// 而副本裡真正想跑快的多半是打完之後趕路的那一段。
    /// </remarks>
    public bool OnlyOutOfCombat { get; set; } = true;

    /// <summary>附近有其他玩家時強制停用（倍率回到原速）。</summary>
    /// <remarks>
    /// 📌 預設 <c>true</c>（安全選項）：舊設定檔沒有這個鍵時反序列化不會覆寫初始式，
    /// 所以既有使用者更新後同樣是開著的。
    /// </remarks>
    public bool PauseWhenOthersNearby { get; set; } = true;

    /// <summary>判定「附近」的半徑（米）；0＝物件表裡只要有其他玩家就停。</summary>
    /// <remarks>讀取端一律經過 <c>NearbyPlayerGuard.EffectiveRadius</c>：NaN、負值當 0（最保守），上限 200。</remarks>
    public float OthersNearbyRadius { get; set; } = 50f;

    /// <summary>隊伍成員不算「其他玩家」。</summary>
    /// <remarks>📌 預設 <c>false</c>（隊友也算）：多人副本裡隊友就是會看到你的人。</remarks>
    public bool IgnorePartyMembers { get; set; }
}

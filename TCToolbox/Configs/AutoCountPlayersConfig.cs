using System.Collections.Generic;
using TCToolbox.Core;

namespace TCToolbox;

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
/// 🔴 <b>所有新增欄位的預設值都等於「這個條件不啟用」</b>，唯一的例外是
/// <see cref="MatchName"/>＝<c>true</c>——那正是多條件化之前的既有行為。
/// 舊設定檔沒有這些鍵，反序列化不會覆寫欄位初始值，因此升級後行為逐字不變。
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

    /// <summary>
    /// 命中時請「塔塔露誇獎」(TataruPraise) 念一句（情境「玩家警示」）。
    /// </summary>
    /// <remarks>
    /// 📌 預設 <c>true</c>，但這<b>不是</b>行為回退：TataruPraise 沒安裝、或它自己的總開關關著、
    /// 或「玩家警示」情境裡沒有已合成語音的句子時，這條路是完全安靜的 no-op
    /// （見 <see cref="TCToolbox.Core.TataruPraiseIpc"/>：每次都先問它的 <c>IsAvailableFor</c>）。
    /// </remarks>
    public bool TataruPraiseOnMatch = true;

    /// <summary>同一位玩家再次觸發的冷卻（秒）。</summary>
    public int CooldownSeconds = 300;

    // ── 以下為多條件判定（預設值一律等於既有行為）──────────────

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
    /// 台服的 <c>OnlineStatus</c> 表裡<b>列 2 與列 3 的名稱都是「遊戲管理員」</b>，
    /// 所以偵測 GM 要兩個都收。
    /// </remarks>
    public HashSet<uint> OnlineStatuses { get; set; } = [];

    /// <summary>啟用「公會簡稱」條件。</summary>
    public bool MatchCompanyTag;

    /// <summary>公會簡稱比對樣式（空字串＝條件視為未啟用）。</summary>
    public string CompanyTagPattern = string.Empty;

    /// <summary>公會簡稱以正規表達式比對。</summary>
    public bool CompanyTagUseRegex = true;

    /// <summary>啟用「距離」條件。</summary>
    public bool MatchMaxDistance;

    /// <summary>距離條件的上限（公尺）：玩家與自己的距離小於等於此值即命中。</summary>
    public float MaxDistance = 30f;
}

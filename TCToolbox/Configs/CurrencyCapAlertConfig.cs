using System.Collections.Generic;

namespace TCToolbox;

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
    /// 資訊列說明與設定畫面上，同時列出其他角色的軍票（讀 AutoRetainer 記下的數量）。
    /// </summary>
    /// <remarks>📌 預設關閉：沒裝 AutoRetainer 的人開了也只會看到「讀不到」。</remarks>
    public bool ShowOtherCharacterSeals;

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

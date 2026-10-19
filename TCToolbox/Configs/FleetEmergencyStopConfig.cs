using TCToolbox.Core;

namespace TCToolbox;

public sealed class FleetEmergencyStopConfig
{
    /// <summary>觸發急停的熱鍵。預設未綁定。</summary>
    public HotkeyConfig Hotkey { get; set; } = new();

    /// <summary>在聊天視窗顯示結果摘要（以及逐項失敗）。</summary>
    /// <remarks>
    /// 📌 預設開啟：急停多半是用熱鍵按的，那時候主視窗是關著的——
    /// 沒有這一行的話，使用者完全沒有「我剛剛真的按到了」的回饋。
    /// </remarks>
    public bool AnnounceInChat = true;

    /// <summary>急停之後自動把結果視窗打開。</summary>
    public bool OpenResultWindow = true;

    /// <summary>
    /// 急停之後，所有共用 <see cref="TCToolbox.Core.AutomationGate"/> 的無人值守迴圈要靜多久（秒）。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>0＝不冷卻</b>：急停照樣把正在跑的批次停掉，但下一個到期的無人值守迴圈可以立刻重開。
    /// 那是使用者可以選的，只是預設不這麼做。
    /// ⚠️ 讀取端會自己夾範圍（見 <c>AutomationGate.EmergencyStopCooldownSeconds</c>），
    /// 所以手改設定檔填進負數或天文數字也不會讓迴圈永遠醒不過來。
    /// </remarks>
    public int EmergencyStopCooldownSeconds { get; set; } = 60;
}

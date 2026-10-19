namespace TCToolbox;

/// <summary>狩獵列車顯示到 Mappy。</summary>
/// <remarks>
/// 📌 這裡沒有「總開關」欄位——<b>模組本身的啟用狀態就是總開關</b>
/// （與 TC Toolbox 其他模組一致：預設關閉，由使用者自己勾）。
/// </remarks>
public sealed class HuntTrainOnMappyConfig
{
    /// <summary>已擊殺的目標也畫出來（用另一顆圖示）。</summary>
    /// <remarks>
    /// 📌 預設 <c>false</c>：地圖上只留「還沒打的」最好認。想看列車進度的人再自己打開。
    /// </remarks>
    public bool ShowDead { get; set; }

    /// <summary>向 Hunt Helper 重新拉一次清單的間隔（秒）。</summary>
    /// <remarks>
    /// 📌 Hunt Helper 每標記一隻怪都會廣播 <c>HH.channel.MarkSeen</c>，本模組收到就會即時重拉，
    /// 所以這個值只是「沒有任何通知時的保底」，不必調小。
    /// </remarks>
    public int RefreshSeconds { get; set; } = 5;

    /// <summary>存活目標的圖示 id。0＝用內建預設值。</summary>
    /// <remarks>
    /// ⚠️ 做成可設定的理由：圖示的<b>存在</b>可以離線驗證，<b>長什麼樣子</b>不行。
    /// 🔴 <b>初始器一定要是 0，不能寫具體常數。</b>寫具體常數的話任何一次 Save 就把當下的編號烙死，
    /// 之後修正 <c>Default…IconId</c> 對<b>所有既有使用者靜默無效</b>，哨兵機制形同虛設。
    /// </remarks>
    public uint AliveIconId { get; set; }

    /// <summary>按「前往」時允許 Lifestream 用飛行坐騎跑最後一段。</summary>
    /// <remarks>
    /// 📌 預設開啟。這一格<b>不會</b>讓你被卡住：Lifestream 端在區域不可飛、或起飛失敗時
    /// 會自己退回地面路線，不會因此讓整趟請求失敗。
    /// ⚠️ 它也<b>不會</b>替你上坐騎——本外掛不新增自動化。沒乘坐騎時就是走過去。
    /// </remarks>
    public bool FlyWhenTravelling { get; set; } = true;
    /// <summary>已擊殺目標的圖示 id。0＝用內建預設值（理由同 <see cref="AliveIconId"/>）。</summary>
    public uint DeadIconId { get; set; }
}

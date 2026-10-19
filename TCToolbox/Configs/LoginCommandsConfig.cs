namespace TCToolbox;

/// <summary>登入後執行自訂指令。</summary>
public sealed class LoginCommandsConfig
{
    /// <summary>
    /// 要執行的指令，一行一條。
    /// </summary>
    /// <remarks>
    /// 📌 預設空字串＝<b>開了模組也什麼都不會做</b>。這是刻意的：這個模組唯一的行為就是
    /// 執行使用者寫的東西，沒寫就沒有預設動作可言，任何「範例指令」放進預設值都會變成
    /// 一條真的被送出去的指令。
    /// </remarks>
    public string Commands = string.Empty;

    /// <summary>
    /// 角色資料就緒之後再等這麼久（毫秒）才跑第一條。
    /// </summary>
    /// <remarks>
    /// 📌 預設 5000ms。太短的話很多外掛的指令還沒註冊，送出去只會得到「查無此指令」，
    /// 而那個失敗<b>不會重試</b>——寧可慢，不要靜默漏跑。
    /// </remarks>
    public int InitialDelayMs = 5_000;

    /// <summary>每條指令之間的間隔（毫秒）。</summary>
    public int IntervalMs = 1_000;

    /// <summary>
    /// AutoRetainer 正在作業時整輪略過。
    /// </summary>
    /// <remarks>
    /// 📌 預設開啟：AutoRetainer 的多角色模式自己會反覆登入登出，那種登入不是「使用者要開始玩了」，
    /// 此時插一輪指令進去多半只會打斷它。AutoRetainer 沒安裝時這格沒有作用。
    /// </remarks>
    public bool SkipWhenAutoRetainerBusy = true;

    /// <summary>執行時在聊天欄顯示一行（記錄一律會寫，不受這格影響）。</summary>
    /// <remarks>
    /// 📌 預設 <c>true</c>：登入之後突然有一串指令自己跑起來，這一行是使用者唯一看得到的
    /// 「這是我自己設定的」證據。
    /// </remarks>
    public bool NotifyInChat = true;
}

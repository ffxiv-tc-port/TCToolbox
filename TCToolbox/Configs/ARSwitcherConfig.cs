namespace TCToolbox;

/// <summary>AutoRetainer 角色切換。</summary>
public sealed class ARSwitcherConfig
{
    /// <summary>
    /// 點擊伺服器資訊列的項目就切換角色（左鍵＝下一個、右鍵＝上一個）。
    /// </summary>
    /// <remarks>
    /// 🔴 預設 <c>false</c>，與上游 CBT 的行為<b>刻意不同</b>：
    /// 切換角色等於<b>登出再登入</b>，而那顆圖示就在時鐘旁邊——手滑點到的代價是整個角色被登出。
    /// 預設關閉時點擊只會開啟 TC Toolbox 設定視窗，切換一律走指令。
    /// </remarks>
    public bool SwitchOnDtrClick;

    /// <summary>
    /// 資訊列與設定畫面上顯示各角色「最早回來的那位僱員」還要多久（讀 AutoRetainer）。
    /// </summary>
    /// <remarks>📌 預設關閉：開著會每 30 秒問 AutoRetainer 一輪，而且資訊列的文字會變長。</remarks>
    public bool ShowVentureCountdown;
}

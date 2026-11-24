namespace TCToolbox;

/// <summary>前線目標面板的設定。</summary>
/// <remarks>缺鍵時沿用這裡的初始值（Dalamud 設定讀取是併入，不是整份取代），所以舊設定檔升上來＝面板原本的行為、疊加關。</remarks>
public sealed class FrontlineObjectivePanelConfig
{
    /// <summary>把目標畫在遊戲的小地圖上。</summary>
    public bool MinimapOverlay { get; set; }

    /// <summary>面板裡的俯視小圖。</summary>
    public bool ShowMap { get; set; } = true;

    /// <summary>面板裡列出待機中的冰（榮譽野）。</summary>
    public bool ShowIdle { get; set; }
}

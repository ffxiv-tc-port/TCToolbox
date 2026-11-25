namespace TCToolbox;

/// <summary>前線目標面板的設定。</summary>
/// <remarks>缺鍵時沿用這裡的初始值（Dalamud 設定讀取是併入，不是整份取代）；舊版小地圖疊加的鍵留在檔裡只會被忽略。</remarks>
public sealed class FrontlineObjectivePanelConfig
{
    /// <summary>面板裡的俯視小圖。</summary>
    public bool ShowMap { get; set; } = true;

    /// <summary>面板裡列出待機中的冰（榮譽野）。</summary>
    public bool ShowIdle { get; set; }

    /// <summary>俯視小圖上照隊伍顏色畫出其他玩家。</summary>
    public bool ShowPlayers { get; set; } = true;

    /// <summary>分數領先的敵隊玩家點的放大倍率。</summary>
    public float LeaderEnemyScale { get; set; } = 1.4f;

    /// <summary>中立（還沒有隊伍擁有、現在能拿）的目標的放大倍率。</summary>
    public float NeutralObjectiveScale { get; set; } = 1.6f;
}

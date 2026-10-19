namespace TCToolbox;

public sealed class AutoRetargetConfig
{
    /// <summary>搜尋敵人的最大距離（公尺）。</summary>
    public float MaxDistance = 30f;

    /// <summary>輪詢間隔（毫秒）。</summary>
    public int PollIntervalMs = 300;

    /// <summary>迷失者／迷失少女優先（會搶走既有目標）。</summary>
    public bool PrioritizeForlorn = true;

    /// <summary>只在戰鬥中生效。</summary>
    public bool OnlyInCombat;
}

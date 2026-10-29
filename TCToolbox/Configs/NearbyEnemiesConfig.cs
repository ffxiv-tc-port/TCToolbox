namespace TCToolbox;

/// <summary>周邊敵人的設定。🔴 類別名與 namespace 定了就不要改（存檔 <c>$type</c> 對不上＝設定重置）。</summary>
public sealed class NearbyEnemiesConfig
{
    /// <summary>戰鬥中隱藏資訊列項目。預設不隱藏：戰鬥正是要用的時候。</summary>
    public bool HideInCombat;

    /// <summary>列出的半徑（米，算到敵人碰撞圈邊緣）。</summary>
    public float MaxDistance = 50f;

    /// <summary>也列出首領可以選取的部位。</summary>
    public bool IncludeParts;

    /// <summary>不看敵對旗標（也列出衛兵、友方戰鬥 NPC）；同時是敵對旗標語意不對時的退路。</summary>
    public bool IncludeNonHostile;
}

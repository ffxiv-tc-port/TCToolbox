namespace TCToolbox;

public sealed class MoveGearsNotInSetConfig
{
    /// <summary>
    /// 連套裝指定的「投影來源」裝備一起保護。預設開啟：搞錯的方向是「少搬幾件」，
    /// 而反方向（搬走還在用的投影來源）要重新設定一輪外觀。
    /// </summary>
    public bool ProtectGlamourSources = true;
}

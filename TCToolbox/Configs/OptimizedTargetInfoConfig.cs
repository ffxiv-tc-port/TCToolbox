namespace TCToolbox;

public sealed class OptimizedTargetInfoConfig
{
    public bool ShowHp = true;
    public bool CompactNumbers = true;
    public bool ShowHpPercent;
    public bool ShowCastRemaining = true;
    public bool ShowClearFocusButton;
    public float TextScale = 0.9f;

    /// <summary>疊圖文字字級再乘上 addon 的介面縮放（false＝既有字級）。</summary>
    public bool ScaleWithHud;

    public float TargetOffsetX = 8f;
    public float TargetOffsetY = -18f;
    public float FocusOffsetX = 8f;
    public float FocusOffsetY = -18f;
    public float ClearFocusButtonOffsetX = -22f;
    public float ClearFocusButtonOffsetY = 0f;
}

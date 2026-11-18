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

    /// <summary>改用掛在目標情報 addon 上的遊戲文字節點顯示（false＝ImGui 疊圖）。</summary>
    public bool NativeText;

    /// <summary>原生文字的字級（addon 內單位，會再乘 HUD 縮放）。</summary>
    public int NativeFontSize = 16;

    public float TargetOffsetX = 8f;
    public float TargetOffsetY = -18f;
    public float FocusOffsetX = 8f;
    public float FocusOffsetY = -18f;
    public float ClearFocusButtonOffsetX = -22f;
    public float ClearFocusButtonOffsetY = 0f;
}

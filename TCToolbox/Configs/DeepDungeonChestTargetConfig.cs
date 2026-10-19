namespace TCToolbox;

public sealed class DeepDungeonChestTargetConfig
{
    /// <summary>鎖定最近寶箱的熱鍵。預設未綁定。</summary>
    public HotkeyConfig Hotkey { get; set; } = new();

    /// <summary>搜尋半徑（公尺）。</summary>
    public float MaxDistance = 50f;

    /// <summary>熱鍵觸發時在聊天視窗回報（指令觸發一律回報，不受這個開關影響）。</summary>
    public bool AnnounceInChat = true;

    /// <summary>不限深宮，任何區域都能鎖定寶箱。</summary>
    /// <remarks>📌 預設關閉＝只在深宮生效，與模組名稱一致。</remarks>
    public bool AnyArea;

    /// <summary>新寶箱出現就自動鎖定。</summary>
    /// <remarks>🔴 預設關閉：這是唯一一個會讓模組「自己動」的開關。</remarks>
    public bool AutoTargetNewChests;

    /// <summary>自動鎖定時，戰鬥中不動作。</summary>
    public bool SkipInCombat = true;

    /// <summary>自動鎖定的輪詢間隔（framework 幀數）。</summary>
    public int AutoPollTicks = 30;
}

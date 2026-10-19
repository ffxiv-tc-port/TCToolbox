namespace TCToolbox;

public sealed class AutoInventoryTransferConfig
{
    /// <summary>觸發用修飾鍵的 VirtualKey 值（0＝停用）。</summary>
    public int ModifierKeyCode;

    /// <summary>轉移後在聊天欄顯示訊息。</summary>
    public bool NotifyOnTransfer = true;
}

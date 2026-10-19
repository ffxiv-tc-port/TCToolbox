namespace TCToolbox;

/// <summary>
/// <see cref="Modules.InventoryDeclutter"/> 的設定。
/// ⚠️ 沒有「啟用」欄位是刻意的：模組本身預設就是關的，而且開著也要按按鈕才會動。
/// </summary>
public sealed class InventoryDeclutterConfig
{
    /// <summary>開始／停止整輪清倉的熱鍵。預設未綁定。</summary>
    public HotkeyConfig Hotkey { get; set; } = new();

    /// <summary>跑「陸行鳥鞍囊：寄放重複道具」這一段。</summary>
    public bool RunSaddlebag { get; set; } = true;

    /// <summary>跑「收藏櫃：可收納裝備一鍵存入」這一段。</summary>
    public bool RunCabinet { get; set; } = true;

    /// <summary>
    /// 單一段跑多久就整輪停下（毫秒）。
    /// </summary>
    /// <remarks>
    /// 📌 預設 5 分鐘，是<b>外層的保險絲</b>而不是正常節奏的上限：
    /// 真正卡住由各模組自己的單步逾時抓（它們都是 15 秒級）。
    /// 鞍囊那一段每件要走「開選單→點項目→等生效」三步、一輪上限 140 件，
    /// 訂太短會把一輪正常的寄放砍掉。
    /// </remarks>
    public int SegmentTimeoutMs { get; set; } = 300_000;

    /// <summary>結束時在聊天欄報告結果（記錄一律會寫，不受這格影響）。</summary>
    public bool NotifyOnFinish { get; set; } = true;
}

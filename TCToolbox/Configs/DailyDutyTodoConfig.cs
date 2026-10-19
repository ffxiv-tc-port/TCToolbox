namespace TCToolbox;

/// <summary><see cref="Modules.DailyDutyTodo"/> 的設定。</summary>
/// <remarks>
/// 📌 兩格都預設開啟：模組本身預設關閉，開了模組的人就是要看資訊列；
/// 「帶我去」按兩次比照同類清單（「前往」同樣是每列一顆、彼此只隔幾個像素）。
/// </remarks>
public sealed class DailyDutyTodoConfig
{
    /// <summary>在伺服器資訊列顯示還剩幾項。</summary>
    public bool ShowDtr { get; set; } = true;

    /// <summary>「帶我去」（與「交給 GBR 採一張」）要按兩次才真的送出。</summary>
    public bool ConfirmTravel { get; set; } = true;

    /// <summary>「交給 GBR 採一張」要採的寶物地圖道具 id；0＝還沒選（按鈕停用）。</summary>
    public uint TreasureMapItemId { get; set; }
}

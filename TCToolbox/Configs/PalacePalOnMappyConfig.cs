namespace TCToolbox;

/// <summary>Palace Pal 的陷阱與埋藏寶藏顯示到 Mappy。</summary>
public sealed class PalacePalOnMappyConfig
{
    public bool ShowTraps { get; set; } = true;

    public bool ShowHoards { get; set; } = true;

    /// <summary>向 Palace Pal 重新拿一次資料的間隔（秒）。</summary>
    /// <remarks>
    /// 📌 預設 5 秒。內容沒變的話只是比對一下字串，不會動到 Mappy 上的任何標記，
    /// 所以這個值不必調大。⚠️ 讀取時夾在 1～60 秒（舊設定檔裡的 0 或負數不會讓它每幀跑）。
    /// </remarks>
    public int RefreshSeconds { get; set; } = 5;

    /// <summary>陷阱的圖示 id。0＝用內建預設值（理由同 <see cref="QuestionableStepConfig.StepIconId"/>）。</summary>
    public uint TrapIconId { get; set; }

    /// <summary>埋藏寶藏的圖示 id。0＝用內建預設值。</summary>
    public uint HoardIconId { get; set; }

    /// <summary>「可能有」陷阱的圖示 id。0＝用內建預設值。</summary>
    public uint TrapUnconfirmedIconId { get; set; }

    /// <summary>「可能有」埋藏寶藏的圖示 id。0＝用內建預設值。</summary>
    public uint HoardUnconfirmedIconId { get; set; }

    /// <summary>把「現在真的看得到」的銀／金寶箱也畫到地圖上。</summary>
    /// <remarks>📌 預設關閉＝沿用本模組原本只畫陷阱／寶藏的行為。</remarks>
    public bool ShowVisibleCoffers { get; set; }

    /// <summary>銀寶箱的圖示 id。0＝用內建預設值。</summary>
    public uint SilverCofferIconId { get; set; }

    /// <summary>金寶箱的圖示 id。0＝用內建預設值。</summary>
    public uint GoldCofferIconId { get; set; }
}

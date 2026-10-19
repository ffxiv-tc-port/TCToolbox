using System.Collections.Generic;

namespace TCToolbox;

public sealed class AutoHideBannersConfig
{
    /// <summary>是否已套用過首次預設值（避免使用者清空後又被塞回來）。</summary>
    public bool Initialized;

    /// <summary>要屏蔽的橫幅圖示 ID。</summary>
    public HashSet<uint> HiddenBanners { get; set; } = [];

    /// <summary>設定畫面是否顯示橫幅預覽圖。</summary>
    public bool ShowPreview = true;
}

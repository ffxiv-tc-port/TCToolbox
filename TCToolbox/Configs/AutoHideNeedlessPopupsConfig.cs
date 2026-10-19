using System.Collections.Generic;

namespace TCToolbox;

public sealed class AutoHideNeedlessPopupsConfig
{
    /// <summary>是否已套用過首次預設值（避免使用者清空後又被塞回來）。</summary>
    public bool Initialized;

    /// <summary>要自動關閉的彈窗 addon 名稱。</summary>
    public HashSet<string> HiddenPopups { get; set; } = [];
}

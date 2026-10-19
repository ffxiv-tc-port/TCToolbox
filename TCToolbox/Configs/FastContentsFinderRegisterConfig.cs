using System.Collections.Generic;

namespace TCToolbox;

/// <summary>任務搜尋器快速登記面板的一筆收藏。</summary>
public sealed class FavoriteDuty
{
    /// <summary>ContentsId 類型：1＝隨機任務（ContentRoulette）、2＝一般副本（ContentFinderCondition）。</summary>
    public byte ContentType { get; set; }

    /// <summary>列號（依 <see cref="ContentType"/> 指向 ContentRoulette 或 ContentFinderCondition）。</summary>
    public uint Id { get; set; }

    /// <summary>收藏當下的名稱快取。查表查得到時以查表為準，這只是查不到時的備援顯示。</summary>
    public string Name { get; set; } = string.Empty;
}

/// <summary>任務搜尋器快速登記。</summary>
public sealed class FastContentsFinderRegisterConfig
{
    /// <summary>是否在任務搜尋器旁顯示快速登記面板。</summary>
    public bool ShowOverlay = true;

    /// <summary>開啟／選取副本後在聊天欄提示。</summary>
    public bool NotifyOnOpen = true;

    public System.Collections.Generic.List<FavoriteDuty> Favorites { get; set; } = new();
}

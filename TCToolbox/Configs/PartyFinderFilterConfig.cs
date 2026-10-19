using System.Collections.Generic;

namespace TCToolbox;

/// <summary>招募清單過濾器。</summary>
public sealed class PartyFinderFilterConfig
{
    /// <summary>隱藏重複招募（同副本＋同說明文字）。</summary>
    public bool FilterSameDescription = true;

    /// <summary>關鍵字比對為白名單（true＝只留命中的）；false＝黑名單（命中就藏）。</summary>
    public bool RegexIsWhitelist;

    /// <summary>關鍵字規則（正規表示式，比對招募人名稱＋說明）。</summary>
    public List<PartyFinderRegexRule> RegexRules { get; set; } = [];

    /// <summary>高難度副本：隊裡已有同職業就藏。</summary>
    public bool HighEndFilterSameJob = true;

    /// <summary>高難度副本：我這職能已滿／沒有空位收我就藏。</summary>
    /// <remarks>📌 預設關：這道過濾依賴載入時的職能欄位自我校準，且會靜默隱藏隊伍，讓使用者自己決定要不要開。</remarks>
    public bool HighEndFilterRoleCount;

    /// <summary>各職能上限（-1＝忽略），順序＝坦／純治／盾治／近戰／遠物／遠魔。</summary>
    /// <remarks>🔴 長度必須是 6；模組啟用時會正規化（長度不符就重設成預設）。</remarks>
    public int[] RoleCaps { get; set; } = [2, 1, 1, 2, 1, 2];
}

/// <summary>招募清單過濾器的一條關鍵字規則。</summary>
public sealed class PartyFinderRegexRule
{
    public bool Enabled = true;
    public string Pattern = string.Empty;
}

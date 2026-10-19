using System;
using System.Collections.Generic;

namespace TCToolbox;

/// <summary>公會特效自動使用。</summary>
public sealed class FcActionAutoUseConfig
{
    /// <summary>角色 ContentId → 要維持的公會特效（CompanyAction 列號）。</summary>
    /// <remarks>預設空＝開了模組也不會對任何角色動作。</remarks>
    public Dictionary<ulong, List<uint>> ActionsByCharacter { get; set; } = [];

    /// <summary>角色 ContentId → 「名稱＠伺服器」，只給設定畫面列出別的角色用。</summary>
    public Dictionary<ulong, string> CharacterNames { get; set; } = [];

    /// <summary>角色 ContentId → 特效 → 最後一次按下「是」的時間（UTC）。自動那條路據此不在 23 小時內重開（特效持續 24 小時）。</summary>
    public Dictionary<ulong, Dictionary<uint, DateTime>> LastUsedUtc { get; set; } = [];
}

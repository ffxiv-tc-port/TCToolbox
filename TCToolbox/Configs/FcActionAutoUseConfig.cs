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

    /// <summary>勾選的特效從身上消失、判定是到期時，自動從庫存再開一次（預設關）。</summary>
    public bool RenewOnExpiry { get; set; } = false;

    /// <summary>勾選的特效庫存剩這麼多個（含）以下時，每次登入在聊天提醒一次；0＝不提醒。</summary>
    public int LowStockThreshold { get; set; } = 1;

    /// <summary>角色 ContentId → 最後一次讀到的公會特效庫存。</summary>
    public Dictionary<ulong, FcStockRecord> StockByCharacter { get; set; } = [];

    /// <summary>角色 ContentId → 狀態編號 → 看到它在「確定沒有」之後出現的時間（UTC），給到期判斷當開始時間。</summary>
    public Dictionary<ulong, Dictionary<uint, DateTime>> ObservedStartUtc { get; set; } = [];
}

/// <summary>一次庫存讀數。數量照清單筆數算（假設每一份各佔一筆）。</summary>
public sealed class FcStockRecord
{
    public DateTime ReadUtc { get; set; }

    /// <summary>CompanyAction 列號 → 筆數；沒列出的＝0。</summary>
    public Dictionary<uint, int> Counts { get; set; } = [];
}

using System.Collections.Generic;

namespace TCToolbox;

/// <summary>商店快速購買（預設）。</summary>
public sealed class AutoShopPurchaseConfig
{
    /// <summary>已擷取的購買預設清單。</summary>
    public List<AutoShopPurchasePreset> Presets { get; set; } = [];
}

/// <summary>一個商店購買預設：在哪個視窗、點清單的哪一項。node id 與索引都是現地擷取的。</summary>
public sealed class AutoShopPurchasePreset
{
    public string Name { get; set; } = string.Empty;
    public string AddonName { get; set; } = string.Empty;
    public uint ListNodeId { get; set; }
    public int ClickIndex { get; set; }

    /// <summary>綁定的對象 NPC 名稱；非空時執行前會比對目前目標，不符即拒絕。</summary>
    public string TargetName { get; set; } = string.Empty;
}

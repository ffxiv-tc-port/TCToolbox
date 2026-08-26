using System;
using System.Collections.Generic;
using Dalamud.Game.Gui.ContextMenu;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.STD;
using Lumina.Excel.Sheets;
using ValueType = FFXIVClientStructs.FFXIV.Component.GUI.ValueType;

namespace TCToolbox.Core;

/// <summary>
/// 「使用者對哪個道具按了右鍵」的共用解析器。
/// 由 <see cref="Modules.CopyItemNameContextMenu"/> 與 <see cref="Modules.HuijiWikiContextMenu"/> 共用，
/// 內容是從前者原封不動搬過來的（行為一個字都沒改，只是換了位置好讓兩個模組共用）。
/// </summary>
/// <remarks>
/// 道具來源一律讀 ClientStructs 上<b>具名的</b> agent 欄位
/// （<c>AgentChatLog.ContextItemId</c>、<c>AgentRecipeNote.ContextMenuResultItemId</c> 之類），
/// 名稱查 Lumina <c>Item</c>／<c>EventItem</c> 表。零 hook、零特徵碼、不寫記憶體。
/// <para>
/// ⚠️ 與 DailyRoutines 原版的差異：DR 為了涵蓋更多視窗掛了三條特徵碼 hook
/// （MiragePrismBox／Achievement／MateriaAttach 的 <c>ReceiveEvent</c>）去攔截「使用者選了哪一格」。
/// 那三條在台服都沒驗過，而特徵碼解錯位址是靜默的。這裡改成只用具名欄位 ＋
/// <c>IGameGui.HoveredItem</c>（限白名單視窗）；涵蓋率略低，但沒有任何一條路徑會靜默解錯。
/// </para>
/// </remarks>
internal static unsafe class ItemContextResolver
{
    /// <summary>選單項目在原生選單的值表裡的起始索引（前 7 格是表頭）。</summary>
    private const int FirstEntryIndex = 7;

    /// <summary>
    /// 沒有具名欄位可讀、但「右鍵時一定正在指著某個道具」的視窗。
    /// 這些才准退回 <c>HoveredItem</c>——否則在非道具的選單（例如對玩家按右鍵）上會拿到殘留值。
    /// </summary>
    private static readonly HashSet<string> HoveredItemAddons = new(StringComparer.Ordinal)
    {
        "CabinetWithdraw",
        "CharacterInspect",
        "CollectablesShop",
        "ColorantColoring",
        "ContentsInfoDetail",
        "DailyQuestSupply",
        "FreeCompanyChest",
        "FreeCompanyCreditShop",
        "GrandCompanyExchange",
        "GrandCompanySupplyList",
        "HousingCatalogPreview",
        "HousingGoods",
        "InclusionShop",
        "ItemSearch",
        "MateriaAttach",
        "MiragePrismPrismBox",
        "MiragePrismPrismBoxCrystallize",
        "MJIDisposeShop",
        "ReconstructionBuyback",
        "Shop",
        "ShopExchangeCoin",
        "ShopExchangeCurrency",
        "ShopExchangeItem",
        "ShopExchangeItemDialog",
        "SkyIslandExchange",
        "SubmarinePartsMenu",
        "TripleTriadCoinExchange",
        "Tryon",
    };

    /// <summary>
    /// 依右鍵選單的型別分派，找出使用者按的是哪個道具。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>兩種選單的資料來源完全不同，不能共用一條路</b>：
    /// <see cref="ContextMenuType.Inventory"/> 的 agent 是 <c>AgentInventoryContext</c>，
    /// Dalamud 已經把「按到哪一格」包成 <see cref="MenuTargetInventory.TargetItem"/>；
    /// <see cref="ContextMenuType.Default"/> 的 agent 是 <c>AgentContext</c>，得逐視窗讀具名欄位。
    /// 背包那條<b>不能</b>改走 <see cref="TryResolveItem(string, out uint, out string)"/>——
    /// 那支對背包沒有具名欄位，又不在 <see cref="HoveredItemAddons"/> 裡，只會回 false。
    /// </remarks>
    public static bool TryResolveFromMenu(IMenuOpenedArgs args, out uint itemId, out string itemName)
    {
        itemId = 0;
        itemName = string.Empty;

        switch (args.MenuType)
        {
            case ContextMenuType.Inventory:
            {
                if (args.Target is not MenuTargetInventory { TargetItem: { } item }) return false;

                // GameInventoryItem.ItemId 走 InventoryItem::GetItemId()，帶 HQ／收藏品位移
                // （Dalamud 自己的 ItemUtil.GetBaseId 也是減 1,000,000／500,000），
                // 所以正規化與查名共用同一支。
                return TryGetItemName(item.ItemId, out itemId, out itemName);
            }

            case ContextMenuType.Default:
            {
                var addonName = args.AddonName;
                if (string.IsNullOrEmpty(addonName)) return false;

                // 對玩家／NPC 按右鍵時不該出現道具選項
                if (IsTargetingCharacter(args)) return false;

                return TryResolveItem(addonName, out itemId, out itemName);
            }

            default:
                return false;
        }
    }

    /// <summary>
    /// 找出這個視窗右鍵時對應的道具。
    /// 一律走具名欄位；沒有具名欄位的視窗才退回 <c>HoveredItem</c>，而且限白名單。
    /// </summary>
    public static bool TryResolveItem(string addonName, out uint itemId, out string itemName)
    {
        itemId = 0;
        itemName = string.Empty;

        var raw = ReadRawItemId(addonName);
        if (raw == 0) return false;

        return TryGetItemName(raw, out itemId, out itemName);
    }

    private static ulong ReadRawItemId(string addonName)
    {
        switch (addonName)
        {
            case "ChatLog":
            {
                var agent = AgentChatLog.Instance();
                return agent == null ? 0 : agent->ContextItemId;
            }

            case "RecipeNote":
            {
                var agent = AgentRecipeNote.Instance();
                return agent == null ? 0 : agent->ContextMenuResultItemId;
            }

            // 這幾扇視窗共用同一個「配方／採集結果」agent
            case "RecipeTree":
            case "RecipeMaterialList":
            case "JournalRewardItem":
            case "Gathering":
            case "GatheringMasterpiece":
            {
                var agent = AgentRecipeItemContext.Instance();
                return agent == null ? 0 : agent->ResultItemId;
            }

            case "Journal":
            {
                var agent = AgentQuestJournal.Instance();
                return agent == null ? 0 : agent->ContextMenuSelectedItemId;
            }

            case "Achievement":
            {
                var agent = AgentAchievement.Instance();
                return agent == null ? 0 : agent->ContextMenuSelectedItemId;
            }

            case "MiragePrismMiragePlate":
            {
                var agent = AgentMiragePrismPrismItemDetail.Instance();
                return agent == null ? 0 : agent->ItemId;
            }

            case "NeedGreed":
            {
                var agent = AgentLoot.Instance();
                return agent == null ? 0 : agent->HoveredItemId;
            }

            case "JournalAccept":
            {
                var agent = AgentJournalAccept.Instance();
                if (agent == null) return 0;
                return ReadRewardItemId(&agent->RewardItems, agent->ContextMenuSelectedRewardIndex);
            }

            case "GuildLeve":
            {
                var agent = AgentLeveQuest.Instance();
                if (agent == null) return 0;
                return ReadRewardItemId(&agent->RewardItems, agent->ContextMenuSelectedRewardIndex);
            }

            default:
                return HoveredItemAddons.Contains(addonName) ? Svc.GameGui.HoveredItem : 0;
        }
    }

    /// <summary>
    /// 讀報酬清單裡的第 index 件。
    /// 🔴 <b>兩軸都要驗</b>：<c>ContextMenuSelectedRewardIndex</c> 是有號整數，沒選中時是負數，
    /// 只比對上界（<c>Count &gt; index</c>）在「長度 &gt; 負數」時恆真，會直接讀到向量前面的記憶體。
    /// </summary>
    private static ulong ReadRewardItemId(StdVector<QuestRewardItem>* rewards, int index)
    {
        if (rewards == null || index < 0) return 0;
        if (rewards->First == null) return 0;

        var count = rewards->LongCount;
        if (count <= 0 || index >= count) return 0;

        return rewards->First[index].ItemId;
    }

    /// <summary>
    /// 把原始 ID 正規化成 Item 列並取名稱。
    /// ⚠️ 台服對未開放道具是「有列、名稱空字串」，所以名稱為空一律視同查不到。
    /// </summary>
    public static bool TryGetItemName(ulong rawId, out uint itemId, out string itemName)
    {
        itemId = 0;
        itemName = string.Empty;

        // 任務道具（EventItem）自成一張表，不做 HQ／收藏品的位移換算
        if (rawId >= 2_000_000)
        {
            var eventItem = Svc.Data.GetExcelSheet<EventItem>().GetRowOrDefault((uint)rawId);
            var eventName = eventItem?.Singular.ExtractText();
            if (string.IsNullOrWhiteSpace(eventName)) return false;

            itemId = (uint)rawId;
            itemName = eventName;
            return true;
        }

        var normalized = rawId switch
        {
            > 1_000_000 => rawId - 1_000_000, // HQ
            > 500_000 => rawId - 500_000,     // 收藏品
            _ => rawId,
        };

        if (normalized is 0 or > uint.MaxValue) return false;

        var row = Svc.Data.GetExcelSheet<Item>().GetRowOrDefault((uint)normalized);
        var name = row?.Name.ExtractText();
        if (string.IsNullOrWhiteSpace(name)) return false;

        itemId = (uint)normalized;
        itemName = name;
        return true;
    }

    /// <summary>
    /// 原生選單目前宣告了幾個項目。<c>-1</c>＝讀不到（agent 還沒起來，或頭格根本不是整數）。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>只對 <see cref="ContextMenuType.Default"/> 有意義。</b>讀的是 <c>AgentContext</c>，
    /// 而背包選單的 agent 是 <c>AgentInventoryContext</c>——背包選單開著的時候，
    /// <c>AgentContext</c> 裡留的是<b>上一次</b>非背包選單的殘值，拿來判斷會得到別的視窗的答案。
    /// </remarks>
    public static int NativeMenuItemCount()
    {
        var agent = AgentContext.Instance();
        if (agent == null) return -1;

        var menu = agent->CurrentContextMenu;
        if (menu == null) return -1;

        var entries = menu->EventParams;
        if (entries.Length <= FirstEntryIndex) return -1;

        // 🔴 頭格的型別<b>不能只認 <c>ValueType.Int</c></b>：Dalamud 自己組同一個陣列時
        // 對「項目數」明寫 <c>ChangeType(ValueType.UInt)</c>（見 ContextMenu.CreateEmptySubmenuContextMenuArray），
        // 讀的時候也一律走 <c>.UInt</c>。兩者在 AtkValue 裡是同一個聯合成員，差別只在型別標籤。
        // 只認 Int 的話，遊戲填 UInt 就會被讀成 0 → 判定「原生選單是空的」→ 去重永遠不生效，
        // 而且不報錯不崩潰，表現成「選單裡出現兩個一模一樣的項目」。
        var head = entries[0];
        return head.Type switch
        {
            ValueType.Int => head.Int,
            ValueType.UInt => head.UInt > int.MaxValue ? -1 : (int)head.UInt,
            _ => -1,
        };
    }

    /// <summary>
    /// 掃一遍原生選單目前的項目，看看有沒有同名的。
    /// 🔴 值表的長度是遊戲填的，<b>不能拿它直接算迴圈上界</b>——<c>EventParams</c> 是固定 33 格，
    /// 遊戲填的筆數加上 7 格表頭有可能超出去。這裡以 Span 自己的長度收斂。
    /// 🔴 同 <see cref="NativeMenuItemCount"/>：<b>只對 <see cref="ContextMenuType.Default"/> 有意義。</b>
    /// </summary>
    public static bool NativeMenuContains(string label)
    {
        var declared = NativeMenuItemCount();
        if (declared <= 0) return false;

        var agent = AgentContext.Instance();
        if (agent == null) return false;

        var menu = agent->CurrentContextMenu;
        if (menu == null) return false;

        var entries = menu->EventParams;
        if (entries.Length <= FirstEntryIndex) return false;

        var end = Math.Min(FirstEntryIndex + declared, entries.Length);

        for (var i = FirstEntryIndex; i < end; i++)
        {
            var value = entries[i];
            if (value.Type is not (ValueType.String or ValueType.ManagedString or ValueType.String8)) continue;
            if (value.String.Value == null) continue;

            if (string.Equals(value.String.ToString(), label, StringComparison.Ordinal)) return true;
        }

        return false;
    }

    /// <summary>
    /// 右鍵的目標是不是「玩家／NPC」而不是道具。
    /// 對玩家按右鍵時不該出現任何道具選項。
    /// </summary>
    public static bool IsTargetingCharacter(IMenuOpenedArgs args)
        => args.Target is MenuTargetDefault { TargetContentId: not 0 };
}

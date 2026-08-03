using System;
using System.Collections.Generic;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Interface.Utility.Raii;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.STD;
using Lumina.Excel.Sheets;
using TCToolbox.Core;
using ValueType = FFXIVClientStructs.FFXIV.Component.GUI.ValueType;

namespace TCToolbox.Modules;

/// <summary>
/// 在<b>背包以外</b>的道具右鍵選單補上「複製道具名」。
/// 機制：純 <c>IContextMenu.OnMenuOpened</c>；道具來源一律讀 ClientStructs 上<b>具名的</b> agent 欄位
/// （<c>AgentChatLog.ContextItemId</c>、<c>AgentRecipeNote.ContextMenuResultItemId</c> 之類），
/// 名稱查 Lumina <c>Item</c>／<c>EventItem</c> 表。零 hook、零特徵碼、不寫記憶體。
/// </summary>
/// <remarks>
/// 背包（<see cref="ContextMenuType.Inventory"/>）本來就有這個選項，所以完全不碰——
/// 這個模組只處理 <see cref="ContextMenuType.Default"/>。
/// <para>
/// 📌 台服 EXD 已驗證：<c>Addon</c> 159 ＝「複製道具名」（選單文字直接取這一列，跟遊戲用語一致）。
/// ⚠️ 台服對「尚未開放的道具」會保留列但把名稱留成空字串，所以判定不能用「查不查得到列」，
/// 必須檢查名稱內容——查得到列但名稱是空的一律當成不存在，不加選單項。
/// </para>
/// <para>
/// ⚠️ 與 DailyRoutines 原版的差異：DR 為了涵蓋更多視窗掛了三條特徵碼 hook
/// （MiragePrismBox／Achievement／MateriaAttach 的 <c>ReceiveEvent</c>）去攔截「使用者選了哪一格」。
/// 那三條在台服都沒驗過，而特徵碼解錯位址是靜默的。這裡改成只用具名欄位 ＋
/// <c>IGameGui.HoveredItem</c>（限白名單視窗）；涵蓋率略低，但沒有任何一條路徑會靜默解錯。
/// </para>
/// </remarks>
public sealed unsafe class CopyItemNameContextMenu : TcModule
{
    public override string InternalName => "CopyItemNameContextMenu";
    public override string DisplayName => "複製道具名（背包以外）";

    public override string Description =>
        "在製作手帳、聊天欄道具連結、任務報酬、成就、市場、商店等視窗的道具右鍵選單補上「複製道具名」。" +
        "背包本來就有這個選項，不重複加入；選單裡已經有同名項目時也會自動跳過。";

    public override bool HasConfigUI => true;

    /// <summary>選單文字所在的 <c>Addon</c> 列（台服＝「複製道具名」）。</summary>
    private const uint CopyItemNameAddonRow = 159;

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

    private string menuLabel = "複製道具名";

    private CopyItemNameContextMenuConfig Config => Plugin.Instance.Config.CopyItemName;

    protected override void OnEnable()
    {
        menuLabel = ResolveMenuLabel();
        Svc.ContextMenu.OnMenuOpened += OnMenuOpened;
    }

    protected override void OnDisable() => Svc.ContextMenu.OnMenuOpened -= OnMenuOpened;

    /// <summary>選單文字取自遊戲自己的 Addon 表；台服未開放的列會是空字串，那就退回寫死的字面值。</summary>
    private static string ResolveMenuLabel()
    {
        var text = Svc.Data.GetExcelSheet<Addon>()
                      .GetRowOrDefault(CopyItemNameAddonRow)?.Text.ExtractText();

        return string.IsNullOrWhiteSpace(text) ? "複製道具名" : text;
    }

    private void OnMenuOpened(IMenuOpenedArgs args)
    {
        try
        {
            // 背包的右鍵選單遊戲本來就有這一項，不碰。
            if (args.MenuType != ContextMenuType.Default) return;

            var addonName = args.AddonName;
            if (string.IsNullOrEmpty(addonName)) return;

            // 對玩家／NPC 按右鍵時不該出現道具選項
            if (args.Target is MenuTargetDefault { TargetContentId: not 0 }) return;

            if (!TryResolveItem(addonName, out var itemId, out var itemName)) return;

            // 原生選單已經有同一項時不重複加入
            if (NativeMenuContains(menuLabel)) return;

            args.AddMenuItem(new MenuItem
            {
                Name = menuLabel,
                PrefixChar = 'T',
                PrefixColor = 539,
                Priority = 0,
                OnClicked = _ => CopyToClipboard(itemId, itemName),
            });
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, $"[{InternalName}] 建立右鍵選單項目時發生例外（addon: {args.AddonName}）");
        }
    }

    private void CopyToClipboard(uint itemId, string itemName)
    {
        ImGui.SetClipboardText(itemName);

        if (Config.NotifyOnCopy)
            Svc.Chat.Print($"[TC Toolbox] 已複製道具名：{itemName}");

        if (Throttle.Pass("CopyItemName-Log", 5_000))
            Svc.Log.Information($"[{InternalName}] 已複製道具名：{itemName}（#{itemId}）");
    }

    /// <summary>
    /// 找出這個視窗右鍵時對應的道具。
    /// 一律走具名欄位；沒有具名欄位的視窗才退回 <c>HoveredItem</c>，而且限白名單。
    /// </summary>
    private static bool TryResolveItem(string addonName, out uint itemId, out string itemName)
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
    private static bool TryGetItemName(ulong rawId, out uint itemId, out string itemName)
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
    /// 掃一遍原生選單目前的項目，看看有沒有同名的。
    /// 🔴 值表的長度是遊戲填的，<b>不能拿它直接算迴圈上界</b>——<c>EventParams</c> 是固定 33 格，
    /// 遊戲填的筆數加上 7 格表頭有可能超出去。這裡以 Span 自己的長度收斂。
    /// </summary>
    private static bool NativeMenuContains(string label)
    {
        var agent = AgentContext.Instance();
        if (agent == null) return false;

        var menu = agent->CurrentContextMenu;
        if (menu == null) return false;

        var entries = menu->EventParams;
        if (entries.Length <= FirstEntryIndex) return false;

        var declared = entries[0].Type == ValueType.Int ? entries[0].Int : 0;
        if (declared <= 0) return false;

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

    public override void DrawConfig()
    {
        var notify = Config.NotifyOnCopy;
        if (ImGui.Checkbox("複製後在聊天欄顯示訊息", ref notify))
        {
            Config.NotifyOnCopy = notify;
            Plugin.Instance.Config.Save();
        }

        using (ImRaii.PushIndent())
            ImGui.TextDisabled($"選單文字取自遊戲介面用語：「{ResolveMenuLabel()}」。");
    }
}

using System;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Keys;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using TCToolbox.Core;
using CSFramework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;

namespace TCToolbox.Modules;

/// <summary>
/// 快速交易：交易視窗開著時，按住指定鍵右鍵點背包道具，自動選右鍵選單的「交易」。
/// </summary>
/// <remarks>
/// 契約：只在使用者那一下右鍵當幀動作；原生指標不跨幀保存；選單項在當幀照 Addon 表文字找。
/// 刻意不做：不直接呼叫放進交易格的函式（會跳過遊戲的可交易檢查）；
/// 不處理數量框與魔晶石／優質確認框，那些留給玩家自己按。
/// </remarks>
public sealed unsafe class QuickTrade : TcModule
{
    public override string InternalName => "QuickTrade";
    public override string DisplayName => "快速交易";

    public override string Description =>
        "和別人的交易視窗開著、而且還在挑選交易物品時，按住指定鍵再右鍵點背包裡的道具，" +
        "就自動選右鍵選單的「交易」把它放進交易視窗，等於替你點了那一下選單。" +
        "數量輸入、魔晶石或優質的確認框由遊戲照常詢問，仍要自己按。" +
        "不可交易的道具會在聊天欄提示。預設「無」＝停用。";

    public override ModuleCategory Category => ModuleCategory.Inventory;

    public override bool HasConfigUI => true;

    /// <summary>台服 <c>Addon#95</c>＝「交易」。</summary>
    private const uint TradeAddonRow = 95;

    private static readonly (int Code, string Label)[] SelectableKeys =
    [
        (0, "無（停用）"),
        ((int)VirtualKey.SHIFT, "SHIFT"),
        ((int)VirtualKey.CONTROL, "CTRL"),
        ((int)VirtualKey.MENU, "ALT"),
    ];

    private static readonly VirtualKey[] ModifierGroups = [VirtualKey.SHIFT, VirtualKey.CONTROL, VirtualKey.MENU];

    private static int ModifierGroup(int vk) => vk switch
    {
        0x10 or 0xA0 or 0xA1 => 0x10,
        0x11 or 0xA2 or 0xA3 => 0x11,
        0x12 or 0xA4 or 0xA5 => 0x12,
        _ => 0,
    };

    /// <summary>Shift／Ctrl／Alt 中，<paramref name="ownKeyCode"/> 那一組以外的有沒有按著。</summary>
    private static bool OtherModifierHeld(int ownKeyCode)
    {
        var own = ModifierGroup(ownKeyCode);
        foreach (var group in ModifierGroups)
        {
            if ((int)group != own && Svc.Keys[group]) return true;
        }

        return false;
    }

    private QuickTradeConfig Config => Plugin.Instance.Config.QuickTrade;

    protected override void OnEnable()
    {
        // 位址解不出來會擲例外：模組啟用失敗、不掛，插件照常載入。
        InventoryContextHook.Subscribe(OnItemMenuOpened);
    }

    protected override void OnDisable()
    {
        InventoryContextHook.Unsubscribe(OnItemMenuOpened);
    }

    private void OnItemMenuOpened(
        AgentInventoryContext* agent, InventoryType inventoryType, int slot, int a4, uint addonId)
    {
        var keyCode = Config.ModifierKeyCode;
        if (keyCode == 0 || agent == null) return;
        if (!Svc.Condition[ConditionFlag.TradeOpen]) return;

        var csFramework = CSFramework.Instance();
        if (csFramework == null || csFramework->WindowInactive) return;
        if (!Svc.Keys[(VirtualKey)keyCode]) return;

        if (OtherModifierHeld(keyCode))
        {
            Svc.Log.Debug($"[{InternalName}] 修飾鍵有按，但同時按著其他修飾鍵，不動作：{inventoryType}#{slot}");
            return;
        }

        var manager = InventoryManager.Instance();
        if (manager == null) return;

        if (manager->TradeLocalState != TradeState.SelectingTradeGoods)
        {
            Svc.Log.Debug($"[{InternalName}] 交易不在挑選物品階段（本機狀態={manager->TradeLocalState}），不動作。");
            return;
        }

        // 原生端開頭的封鎖格檢查成立時不重建選單、detour 照樣被呼叫 ⇒ 選單是上一次的，不動作。
        if (agent->TargetInventoryId != inventoryType || agent->TargetInventorySlotId != slot)
        {
            Svc.Log.Debug(
                $"[{InternalName}] 這次右鍵原生端沒有重建選單（目標 {inventoryType}#{slot}，" +
                $"選單是 {agent->TargetInventoryId}#{agent->TargetInventorySlotId}），不動作。");
            return;
        }

        // Items 為 null 而 Size > 0 時 GetInventorySlot 回的是非 null 的假指標，先判容器。
        var container = manager->GetInventoryContainer(inventoryType);
        if (container == null || container->Items == null) return;

        var item = manager->GetInventorySlot(inventoryType, slot);
        if (item == null || item->ItemId == 0)
        {
            Svc.Log.Information($"[{InternalName}] 讀不到右鍵的那一格，不動作：{inventoryType}#{slot}");
            return;
        }

        var name = ItemNames.Get(
            item->GetBaseItemId(), (item->Flags & InventoryItem.ItemFlags.HighQuality) != 0);

        var result = InventoryContextMenu.TryFireEntry(agent, TradeAddonRow, InternalName, out var label);
        switch (result)
        {
            case ContextMenuFireResult.Fired:
                Svc.Log.Debug($"[{InternalName}] 已選「{label}」：{inventoryType}#{slot}「{name}」");
                break;

            case ContextMenuFireResult.NotFound:
            case ContextMenuFireResult.Disabled:
                if (Throttle.Pass("QuickTrade-Untradable", 3_000))
                    Svc.Chat.PrintError($"[TC Toolbox] 「{name}」無法交易，沒有放進交易視窗。");
                break;

            case ContextMenuFireResult.Guarded:
                Svc.Log.Debug($"[{InternalName}] 同一個右鍵選單剛送過、還在關閉中，這一下不送。");
                break;

            default:
                Svc.Log.Information($"[{InternalName}] 沒有選到「{label}」（{result}）：{inventoryType}#{slot}「{name}」");
                break;
        }
    }

    public override void DrawConfig()
    {
        var currentIndex = 0;
        for (var i = 0; i < SelectableKeys.Length; i++)
        {
            if (SelectableKeys[i].Code == Config.ModifierKeyCode) currentIndex = i;
        }

        ImGui.SetNextItemWidth(180f);
        if (ImGui.BeginCombo("觸發鍵", SelectableKeys[currentIndex].Label))
        {
            for (var i = 0; i < SelectableKeys.Length; i++)
            {
                if (!ImGui.Selectable(SelectableKeys[i].Label, i == currentIndex)) continue;
                Config.ModifierKeyCode = SelectableKeys[i].Code;
                Plugin.Instance.Config.Save();
            }

            ImGui.EndCombo();
        }

        ImGui.TextWrapped(
            "用法：和對方開好交易視窗後，按住觸發鍵，右鍵點背包裡的道具，就會自動選「交易」放進視窗。" +
            "只在還沒按「確定條件」的挑選階段有效；鎖定之後不動作。");
        ImGui.TextWrapped(
            "右鍵時除了觸發鍵還按著別的 Shift／Ctrl／Alt 就不動作。" +
            "交易視窗開著時，「自動物品頁面轉移」不會動作，所以兩者可以設成同一顆鍵。");
        ImGui.TextColored(new System.Numerics.Vector4(1f, 0.8f, 0.35f, 1f),
                          "⚠ 觸發鍵請避開 Marketbuddy「快速上架」與 AutoRetainer「快速販賣」使用的鍵。");
    }
}

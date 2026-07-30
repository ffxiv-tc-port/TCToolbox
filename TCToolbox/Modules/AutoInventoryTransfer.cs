using System;
using System.Collections.Generic;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Game.Text;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using TCToolbox.Core;
using CSFramework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;

namespace TCToolbox.Modules;

/// <summary>
/// 自動物品頁面轉移：按住指定鍵右鍵點物品，直接把它搬到對應的另一個頁面。
/// 機制：hook 遊戲自己的「開啟物品右鍵選單」函式當觸發點（位址由 ClientStructs 解析，
/// 不自帶 sig），符合條件時呼叫 <c>InventoryManager.MoveItemSlot</c>（＝遊戲拖放用的同一條路徑）
/// 並關掉右鍵選單。零封包偽造、不寫記憶體、不做 patch。
/// 參考 DailyRoutines AutoInventoryTransfer 的用途重寫（API13、無 OmenTools 相依）。
/// </summary>
public sealed unsafe class AutoInventoryTransfer : TcModule
{
    public override string InternalName => "AutoInventoryTransfer";
    public override string DisplayName => "自動物品頁面轉移";

    public override string Description =>
        "按住指定鍵右鍵點物品，直接把它搬去對應頁面：背包⇄雇員、背包⇄部隊置物櫃、背包⇄陸行鳥鞍袋、" +
        "兵裝庫→背包。目的地沒有空位（或可疊的同款）時不動作並提示。預設「無」＝停用。";

    public override bool HasConfigUI => true;

    /// <summary>可選的修飾鍵（0＝停用）。</summary>
    private static readonly (int Code, string Label)[] SelectableKeys =
    [
        (0, "無（停用）"),
        ((int)VirtualKey.SHIFT, "SHIFT"),
        ((int)VirtualKey.CONTROL, "CTRL"),
        ((int)VirtualKey.MENU, "ALT"),
    ];

    private delegate void OpenForItemSlotDelegate(
        AgentInventoryContext* agent, InventoryType inventoryType, int slot, int a4, uint addonId);

    private Hook<OpenForItemSlotDelegate>? openForItemSlotHook;

    private static readonly InventoryType[] PlayerBags =
        [InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4];

    private static readonly InventoryType[] RetainerPages =
    [
        InventoryType.RetainerPage1, InventoryType.RetainerPage2, InventoryType.RetainerPage3,
        InventoryType.RetainerPage4, InventoryType.RetainerPage5, InventoryType.RetainerPage6,
        InventoryType.RetainerPage7,
    ];

    private static readonly InventoryType[] FreeCompanyPages =
    [
        InventoryType.FreeCompanyPage1, InventoryType.FreeCompanyPage2, InventoryType.FreeCompanyPage3,
        InventoryType.FreeCompanyPage4, InventoryType.FreeCompanyPage5,
    ];

    private static readonly InventoryType[] SaddleBags =
    [
        InventoryType.SaddleBag1, InventoryType.SaddleBag2,
        InventoryType.PremiumSaddleBag1, InventoryType.PremiumSaddleBag2,
    ];

    private static readonly InventoryType[] ArmoryContainers =
    [
        InventoryType.ArmoryMainHand, InventoryType.ArmoryOffHand, InventoryType.ArmoryHead,
        InventoryType.ArmoryBody, InventoryType.ArmoryHands, InventoryType.ArmoryWaist,
        InventoryType.ArmoryLegs, InventoryType.ArmoryFeets, InventoryType.ArmoryEar,
        InventoryType.ArmoryNeck, InventoryType.ArmoryWrist, InventoryType.ArmoryRings,
    ];

    /// <summary>同一格重複觸發的忽略視窗（毫秒）。</summary>
    private const int DuplicateGuardMs = 500;

    private InventoryType lastHandledSource = InventoryType.Invalid;
    private int lastHandledSlot = -1;
    private uint lastHandledItemId;
    private long lastHandledTick;

    private AutoInventoryTransferConfig Config => Plugin.Instance.Config.InventoryTransfer;

    protected override void OnEnable()
    {
        openForItemSlotHook = Svc.Hooks.HookFromAddress<OpenForItemSlotDelegate>(
            AgentInventoryContext.Addresses.OpenForItemSlot.Value, OpenForItemSlotDetour);
        openForItemSlotHook.Enable();
    }

    protected override void OnDisable()
    {
        openForItemSlotHook?.Dispose();
        openForItemSlotHook = null;

        lastHandledSource = InventoryType.Invalid;
        lastHandledSlot = -1;
        lastHandledItemId = 0;
        lastHandledTick = 0;
    }

    private void OpenForItemSlotDetour(
        AgentInventoryContext* agent, InventoryType inventoryType, int slot, int a4, uint addonId)
    {
        openForItemSlotHook!.Original(agent, inventoryType, slot, a4, addonId);

        try
        {
            HandleContextMenu(agent, inventoryType, slot);
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, $"[{InternalName}] 處理右鍵轉移時發生例外");
        }
    }

    private void HandleContextMenu(AgentInventoryContext* agent, InventoryType source, int slot)
    {
        if (Config.ModifierKeyCode == 0) return;
        if (CSFramework.Instance()->WindowInactive) return;
        if (!Svc.Keys[(VirtualKey)Config.ModifierKeyCode]) return;

        var manager = InventoryManager.Instance();
        if (manager == null) return;

        var item = manager->GetInventorySlot(source, slot);
        if (item == null || item->ItemId == 0) return;

        // ⚠️ 道具識別資料一定要在 MoveItemSlot 之前抓下來：MoveItemSlot 會同步清空來源格，
        // 之後再讀這個指標只會拿到空欄位（Item sheet 的 row 0 是有效列但名稱為空字串，
        // 所以連 null 判斷都不會觸發，訊息就變成「已轉移『』」）。
        var itemId = item->ItemId;
        var baseItemId = item->GetBaseItemId();
        var isHighQuality = (item->Flags & InventoryItem.ItemFlags.HighQuality) != 0;
        var displayName = ResolveItemName(baseItemId, isHighQuality);

        // 同一格、同一道具在極短時間內重複觸發只處理一次（防手滑連點與任何再進入路徑）
        if (lastHandledSource == source && lastHandledSlot == slot && lastHandledItemId == itemId &&
            Environment.TickCount64 - lastHandledTick < DuplicateGuardMs)
        {
            Svc.Log.Debug($"[{InternalName}] 略過重複觸發：{source}#{slot} itemId={itemId}");
            return;
        }

        lastHandledSource = source;
        lastHandledSlot = slot;
        lastHandledItemId = itemId;
        lastHandledTick = Environment.TickCount64;

        if (!TryResolveDestination(source, out var candidates, out var reason))
        {
            if (reason.Length > 0 && Throttle.Pass("AutoInventoryTransfer-NoDest", 3_000))
                Svc.Chat.Print($"[TC Toolbox] {reason}");
            return;
        }

        if (!TryFindTargetSlot(manager, candidates, item, out var destination, out var destinationSlot))
        {
            if (Throttle.Pass("AutoInventoryTransfer-Full", 3_000))
                Svc.Chat.PrintError($"[TC Toolbox] 目的地沒有空位也沒有可疊的同款道具，「{displayName}」未轉移。");
            return;
        }

        var result = manager->MoveItemSlot(source, (ushort)slot, destination, (ushort)destinationSlot);

        // MoveItemSlot 會同步更新本機容器（AutoDuty 的 AutoEquipHelper 就是靠「呼叫後立刻回讀
        // 目的地格」判定成功），所以這裡可以直接驗結果，不必等伺服器回應。
        // 兩邊都驗：疊加到既有堆疊時目的地本來就有同款道具，只驗目的地會恆真，
        // 所以真正的判準是「來源格已經不是這個道具了」。
        var moved = result == 0 &&
                    !IsItemAt(manager, source, slot, baseItemId) &&
                    IsItemAt(manager, destination, destinationSlot, baseItemId);

        Svc.Log.Debug($"[{InternalName}] {source}#{slot} → {destination}#{destinationSlot} " +
                      $"itemId={itemId} result={result} verified={moved}");

        if (!moved)
        {
            if (Throttle.Pass("AutoInventoryTransfer-Failed", 3_000))
                Svc.Chat.PrintError($"[TC Toolbox] 「{displayName}」轉移失敗（遊戲回傳 {result}），請改用手動拖放。");
            return;
        }

        if (Config.NotifyOnTransfer)
            Svc.Chat.Print($"[TC Toolbox] 已轉移「{displayName}」。");

        CloseContextMenu(agent);
    }

    /// <summary>道具名稱一律走 Lumina Item 表（台服自帶繁中），不讀 addon 上的文字。</summary>
    private static string ResolveItemName(uint baseItemId, bool isHighQuality)
    {
        var row = Svc.Data.GetExcelSheet<Item>().GetRowOrDefault(baseItemId);
        var name = row?.Name.ExtractText() ?? string.Empty;

        // Item 表的 row 0 是有效列但名稱為空，所以不能只判斷 null
        if (string.IsNullOrEmpty(name))
            return $"#{baseItemId}";

        return isHighQuality ? $"{name} {(char)SeIconChar.HighQuality}" : name;
    }

    private static bool IsItemAt(InventoryManager* manager, InventoryType type, int slot, uint baseItemId)
    {
        var item = manager->GetInventorySlot(type, slot);
        return item != null && item->GetBaseItemId() == baseItemId;
    }

    /// <summary>依來源容器與目前開著的視窗決定目的地候選（依序嘗試）。</summary>
    private static bool TryResolveDestination(
        InventoryType source, out List<InventoryType> candidates, out string reason)
    {
        candidates = [];
        reason = string.Empty;

        // 雇員／部隊置物櫃／鞍袋 → 背包
        if (Array.IndexOf(RetainerPages, source) >= 0 ||
            Array.IndexOf(FreeCompanyPages, source) >= 0 ||
            Array.IndexOf(SaddleBags, source) >= 0 ||
            Array.IndexOf(ArmoryContainers, source) >= 0)
        {
            candidates.AddRange(PlayerBags);
            return true;
        }

        if (Array.IndexOf(PlayerBags, source) < 0) return false;

        // 背包 → 目前開著的那個容器
        if (UiHelper.IsAddonReady("InventoryRetainer") || UiHelper.IsAddonReady("InventoryRetainerLarge"))
        {
            candidates.AddRange(RetainerPages);
            return true;
        }

        if (UiHelper.IsAddonReady("FreeCompanyChest"))
        {
            // 優先送到目前顯示的那一頁（AtkValues[1]!=0＝水晶頁，不接受一般道具）
            var chest = UiHelper.GetAddon("FreeCompanyChest");
            if (chest != null && chest->AtkValuesCount > 2)
            {
                if (chest->AtkValues[1].UInt != 0)
                {
                    reason = "部隊置物櫃目前在水晶頁，請切到道具頁再轉移。";
                    return false;
                }

                var pageIndex = (int)chest->AtkValues[2].UInt;
                if (pageIndex >= 0 && pageIndex < FreeCompanyPages.Length)
                    candidates.Add(FreeCompanyPages[pageIndex]);
            }

            foreach (var page in FreeCompanyPages)
            {
                if (!candidates.Contains(page)) candidates.Add(page);
            }

            return true;
        }

        if (UiHelper.IsAddonReady("InventoryBuddy"))
        {
            candidates.AddRange(SaddleBags);
            return true;
        }

        reason = "沒有開著可互轉的頁面（雇員、部隊置物櫃或陸行鳥鞍袋）。";
        return false;
    }

    /// <summary>找目的地的落點：優先可疊的同款道具，其次空格。</summary>
    private static bool TryFindTargetSlot(
        InventoryManager* manager,
        List<InventoryType> candidates,
        InventoryItem* source,
        out InventoryType destination,
        out int destinationSlot)
    {
        destination = InventoryType.Invalid;
        destinationSlot = -1;

        var sheetItem = Svc.Data.GetExcelSheet<Item>().GetRowOrDefault(source->GetBaseItemId());
        var stackSize = sheetItem?.StackSize ?? 1u;

        // 先找可以疊上去的
        if (stackSize > 1)
        {
            foreach (var type in candidates)
            {
                var container = manager->GetInventoryContainer(type);
                if (container == null || !container->IsLoaded) continue;

                for (var i = 0; i < container->Size; i++)
                {
                    var slot = container->GetInventorySlot(i);
                    if (slot == null || slot->ItemId == 0) continue;
                    if (slot->GetBaseItemId() != source->GetBaseItemId()) continue;
                    if (slot->Flags != source->Flags) continue;
                    if (slot->Quantity + source->Quantity > stackSize) continue;

                    destination = type;
                    destinationSlot = i;
                    return true;
                }
            }
        }

        foreach (var type in candidates)
        {
            var container = manager->GetInventoryContainer(type);
            if (container == null || !container->IsLoaded) continue;

            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot == null || slot->ItemId != 0) continue;

                destination = type;
                destinationSlot = i;
                return true;
            }
        }

        return false;
    }

    private static void CloseContextMenu(AgentInventoryContext* agent)
    {
        var addonId = agent->AgentInterface.GetAddonId();
        if (addonId == 0) return;

        var addon = AtkStage.Instance()->RaptureAtkUnitManager->GetAddonById((ushort)addonId);
        if (addon == null) return;

        agent->AgentInterface.Hide();
        addon->Close(true);
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

        var notify = Config.NotifyOnTransfer;
        if (ImGui.Checkbox("轉移後顯示聊天訊息", ref notify))
        {
            Config.NotifyOnTransfer = notify;
            Plugin.Instance.Config.Save();
        }

        ImGui.TextColored(new System.Numerics.Vector4(1f, 0.8f, 0.35f, 1f),
                          "⚠ 觸發鍵請避開 Marketbuddy 的「快速上架」與 AutoRetainer 的「快速販賣」鍵：\n" +
                          "三者都掛在同一個右鍵事件上，設成同一顆鍵會同時動作。");

        ImGui.TextDisabled("兵裝庫只支援「兵裝庫→背包」方向；反向請照常用裝備／整理流程。");
    }
}

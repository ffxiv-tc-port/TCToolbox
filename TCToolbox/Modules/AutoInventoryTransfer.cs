using System;
using Dalamud.Memory;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Game.Text;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using Dalamud.Plugin.Services;
using TCToolbox.Core;
using CSFramework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;

namespace TCToolbox.Modules;

/// <summary>
/// 自動物品頁面轉移：按住指定鍵右鍵點物品，直接把它搬到對應的另一個頁面。
/// 機制：hook 遊戲自己的「開啟物品右鍵選單」函式當觸發點（位址由 ClientStructs 解析，
/// 不自帶 sig），符合條件時執行搬移並關掉右鍵選單。零封包偽造、不寫記憶體、不做 patch。
///
/// ⚠️ 搬移用哪條路徑要看容器：
///  - **雇員**：走遊戲自己的雇員道具命令（見 <see cref="RetainerItemCommandDelegate"/>）。
///    `MoveItemSlot` 在這裡會「假成功」——本機更新了但伺服器根本沒收到。
///  - **鞍袋**：點遊戲右鍵選單自己的項目（見 <see cref="TryFireContextMenuEntry"/>）。
///    它同樣不能用 MoveItemSlot，而且**不走**雇員道具命令（實機驗證過）。
///  - **其他**（部隊置物櫃／兵裝庫）：仍用 <c>InventoryManager.MoveItemSlot</c>。
///    ⚠️ 部隊置物櫃也是伺服器權威容器，理論上有同樣的假成功風險，但尚未實測；
///    真的遇到就照鞍袋那條路加對應的 Addon row 即可。
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

    /// <summary>
    /// 🔴 雇員存取不能用 <c>InventoryManager.MoveItemSlot</c>。
    ///
    /// 2026-07-31 實機 log 證實：對 RetainerPage 呼叫 MoveItemSlot 會「成功」——回傳 0、
    /// 本機容器立刻更新、來源格清空 —— 但**伺服器從來沒收到**。決定性證據是 17:55 那批：
    ///   17:55:07  RetainerPage2#9 → Inventory1#20 itemId=45637 verified=True
    ///   17:55:47  RetainerPage2#9 → Inventory1#20 itemId=45637 verified=True   ← 40 秒後一模一樣
    /// 同一格、同一道具、同一目的地整批七筆重來，代表第一批完全沒生效、道具還在雇員身上，
    /// 只是本機狀態被樂觀改掉、直到下一次伺服器同步才還原。
    /// （所以先前那個 800ms 的延遲確認也抓不到——退回發生得比它晚太多。）
    ///
    /// 正解是走遊戲自己的雇員道具命令，也就是右鍵選單「取回／寄放」實際呼叫的函式。
    /// 特徵碼與 agent 取法都照抄 AutoRetainer（`Internal/Memory.cs`、`InventorySpaceManager.cs`），
    /// 那是**當天實測有效**的：log 裡 20:40 有 AutoRetainer 自己的自動化連續呼叫
    /// slot 0→6（由 NeoTaskManager 驅動，不是旁觀遊戲），證明特徵碼與 +40 偏移在台服 7.20 都對。
    /// </summary>
    private delegate void RetainerItemCommandDelegate(
        nint agentRetainerItemCommandModule, uint slot, InventoryType inventoryType,
        uint a4, RetainerItemCommand command);

    private const string RetainerItemCommandSig =
        "48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 48 83 EC 30 48 8B 5C 24 ?? 41 8B F0";

    private RetainerItemCommandDelegate? retainerItemCommand;

    private enum RetainerItemCommand : long
    {
        RetrieveFromRetainer = 0,
        EntrustToRetainer = 1,
    }

    /// <summary>雇員道具命令模組。⚠️ `+ 40` 是未文件化偏移，照抄 AutoRetainer 的實測值。</summary>
    private static nint GetAgentRetainerItemCommandModule()
    {
        var agentModule = AgentModule.Instance();
        if (agentModule == null) return 0;
        var agent = agentModule->GetAgentByInternalId(AgentId.Retainer);
        return agent == null ? 0 : (nint)agent + 40;
    }

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

    /// <summary>
    /// 等伺服器確認的轉移。雇員／部隊置物櫃／鞍袋是伺服器權威容器，MoveItemSlot 只是先改
    /// 本機狀態，伺服器拒絕時道具會彈回原處，所以要延遲一段時間再回頭確認。
    /// </summary>
    private readonly record struct PendingVerification(
        InventoryType Source, int Slot, uint BaseItemId, string DisplayName, long DeadlineTick);

    private readonly List<PendingVerification> pendingVerifications = [];

    /// <summary>等伺服器回應的時間。太短會誤報成功，太長則回饋遲鈍。</summary>
    private const int RollbackCheckDelayMs = 800;

    /// <summary>來源是不是伺服器權威的容器（本機容器之間的搬移不需要延遲確認）。</summary>
    private static bool IsServerAuthoritative(InventoryType source)
        => Array.IndexOf(RetainerPages, source) >= 0
        || Array.IndexOf(FreeCompanyPages, source) >= 0
        || Array.IndexOf(SaddleBags, source) >= 0;

    protected override void OnEnable()
    {
        openForItemSlotHook = Svc.Hooks.HookFromAddress<OpenForItemSlotDelegate>(
            AgentInventoryContext.Addresses.OpenForItemSlot.Value, OpenForItemSlotDetour);
        openForItemSlotHook.Enable();

        // 解析不到就讓 retainerItemCommand 留 null，雇員轉移會明確告知而不是靜默走錯路徑。
        if (Svc.SigScanner.TryScanText(RetainerItemCommandSig, out var retainerCmdAddr))
        {
            retainerItemCommand =
                Marshal.GetDelegateForFunctionPointer<RetainerItemCommandDelegate>(retainerCmdAddr);
            Svc.Log.Information($"[{InternalName}] 雇員道具命令位址 0x{retainerCmdAddr:X}");
        }
        else
        {
            Svc.Log.Warning($"[{InternalName}] 找不到雇員道具命令的特徵碼，雇員轉移將無法使用。");
        }

        Svc.Framework.Update += OnUpdate;
    }

    protected override void OnDisable()
    {
        Svc.Framework.Update -= OnUpdate;
        openForItemSlotHook?.Dispose();
        openForItemSlotHook = null;

        pendingVerifications.Clear();
        lastHandledSource = InventoryType.Invalid;
        lastHandledSlot = -1;
        lastHandledItemId = 0;
        lastHandledTick = 0;
    }

    private void OnUpdate(IFramework _)
    {
        if (pendingVerifications.Count == 0) return;

        var now = Environment.TickCount64;
        var manager = InventoryManager.Instance();

        for (var i = pendingVerifications.Count - 1; i >= 0; i--)
        {
            var p = pendingVerifications[i];
            if (now < p.DeadlineTick) continue;
            pendingVerifications.RemoveAt(i);

            if (manager == null) continue;

            // 道具又回到來源格 = 伺服器把它退回來了，先前那次「成功」是假的。
            if (IsItemAt(manager, p.Source, p.Slot, p.BaseItemId))
            {
                Svc.Log.Debug($"[{InternalName}] 伺服器退回：{p.Source}#{p.Slot} itemId={p.BaseItemId}");
                if (Throttle.Pass("AutoInventoryTransfer-RolledBack", 3_000))
                    Svc.Chat.PrintError(
                        $"[TC Toolbox] 「{p.DisplayName}」沒有真的轉移過去（伺服器已退回原處），請改用手動拖放。");
                continue;
            }

            if (Config.NotifyOnTransfer)
                Svc.Chat.Print($"[TC Toolbox] 已轉移「{p.DisplayName}」。");
        }
    }

    /// <summary>
    /// 走遊戲自己的雇員道具命令。與 MoveItemSlot 不同，這條會真的送到伺服器，
    /// 落點也由遊戲決定（取回進背包空位／寄放進雇員空位）。
    /// </summary>
    private void TransferViaRetainerCommand(
        AgentInventoryContext* agent, InventoryType source, int slot,
        uint itemId, string displayName, bool retrieving)
    {
        if (retainerItemCommand == null)
        {
            if (Throttle.Pass("AutoInventoryTransfer-NoRetainerCmd", 3_000))
                Svc.Chat.PrintError($"[TC Toolbox] 找不到雇員道具命令，「{displayName}」未轉移，請改用手動拖放。");
            return;
        }

        var module = GetAgentRetainerItemCommandModule();
        if (module == 0)
        {
            if (Throttle.Pass("AutoInventoryTransfer-NoRetainerAgent", 3_000))
                Svc.Chat.PrintError($"[TC Toolbox] 雇員視窗未就緒，「{displayName}」未轉移。");
            return;
        }

        var command = retrieving
            ? RetainerItemCommand.RetrieveFromRetainer
            : RetainerItemCommand.EntrustToRetainer;

        Svc.Log.Debug($"[{InternalName}] 雇員命令 {command}：{source}#{slot} itemId={itemId}");
        retainerItemCommand(module, (uint)slot, source, 0, command);

        CloseContextMenu(agent);

        if (Config.NotifyOnTransfer)
            Svc.Chat.Print($"[TC Toolbox] 已{(retrieving ? "取回" : "寄放")}「{displayName}」。");
    }

    /// <summary>
    /// 🔴 鞍袋也不能用 MoveItemSlot（同樣是假成功），但它**不走**雇員道具命令 ——
    /// 2026-07-31 請使用者手動取出一次，AutoRetainer 的 RetainerItemCommand hook
    /// 全程沒有任何輸出，而道具確實從 InventoryBuddy 移到了 InventoryExpansion。
    ///
    /// 所以改成「點玩家自己會點的那個選單項目」。判準用 Addon 表的 row id 去查**客戶端
    /// 自己的字串**，不是寫死翻譯，所以跟語言無關：
    ///   881 = 放入陸行鳥鞍囊 / 887 = 從陸行鳥鞍囊中取回
    /// 索引方式照抄 Artisan `Tasks/TaskSelectRetainer.cs`（台服實測有效）：選單實際佔用
    /// EventParams[ContexItemStartIndex .. +ContextItemCount]，**不能掃完 98 格再數字串** ——
    /// 那樣會掃到上一次選單的殘留，算出來的序號也不是 callback 要的列號。
    /// </summary>
    private const uint AddonRowDepositToSaddlebag = 881;
    private const uint AddonRowRetrieveFromSaddlebag = 887;
    // 部隊置物櫃：2950「取出」與 2951「放入儲物櫃」在 Addon 表裡相鄰＝同一個選單區塊。
    private const uint AddonRowRetrieveFromFcChest = 2950;
    private const uint AddonRowDepositToFcChest = 2951;

    private bool TryFireContextMenuEntry(AgentInventoryContext* agent, uint addonRowId, string displayName)
    {
        var wanted = Svc.Data.GetExcelSheet<Addon>()?.GetRowOrDefault(addonRowId)?.Text.ExtractText().Trim();
        if (string.IsNullOrEmpty(wanted))
        {
            Svc.Log.Warning($"[{InternalName}] 讀不到 Addon#{addonRowId} 的字串，無法比對選單項目。");
            return false;
        }

        var startIndex = Math.Clamp(agent->ContexItemStartIndex, 0, 98);
        var itemCount = Math.Clamp(agent->ContextItemCount, 0, 98 - startIndex);

        var index = -1;
        var labels = new string[itemCount];
        for (var entry = 0; entry < itemCount; entry++)
        {
            var v = agent->EventParams[startIndex + entry];
            if (v.Type is not FFXIVClientStructs.FFXIV.Component.GUI.ValueType.String
                and not FFXIVClientStructs.FFXIV.Component.GUI.ValueType.ManagedString)
                continue;

            var ptr = v.String.Value;
            if (ptr == null) continue;
            labels[entry] = MemoryHelper.ReadSeStringNullTerminated((nint)ptr).TextValue.Trim();
            if (index == -1 && labels[entry] == wanted) index = entry;
        }

        if (index == -1)
        {
            Svc.Log.Warning(
                $"[{InternalName}] 右鍵選單裡找不到「{wanted}」，「{displayName}」未轉移。" +
                $"選單有 {itemCount} 項（起點 EventParams[{startIndex}]）：" +
                string.Join(" | ", labels));
            if (Throttle.Pass("AutoInventoryTransfer-NoMenuEntry", 3_000))
                Svc.Chat.PrintError($"[TC Toolbox] 右鍵選單裡找不到「{wanted}」，「{displayName}」未轉移。");
            return false;
        }

        // 被收進次選單的項目不能直接用這個序號觸發（那是主選單的列號）。
        if ((agent->ContextItemSubmenuMask & (1u << index)) != 0)
        {
            Svc.Log.Warning($"[{InternalName}] 「{wanted}」在次選單裡（submenu mask），無法直接觸發。");
            if (Throttle.Pass("AutoInventoryTransfer-Submenu", 3_000))
                Svc.Chat.PrintError($"[TC Toolbox] 「{wanted}」被收在次選單裡，請改用手動拖放。");
            return false;
        }

        if (agent->IsContextItemDisabled(index))
        {
            Svc.Log.Warning($"[{InternalName}] 選單項目 {index}（{wanted}）是停用狀態。");
            if (Throttle.Pass("AutoInventoryTransfer-MenuDisabled", 3_000))
                Svc.Chat.PrintError($"[TC Toolbox] 「{wanted}」目前無法使用，「{displayName}」未轉移。");
            return false;
        }

        var addonId = agent->AgentInterface.GetAddonId();
        var addon = addonId == 0
            ? null
            : AtkStage.Instance()->RaptureAtkUnitManager->GetAddonById((ushort)addonId);
        if (addon == null)
        {
            Svc.Log.Warning($"[{InternalName}] 取不到右鍵選單 addon，「{displayName}」未轉移。");
            return false;
        }

        Svc.Log.Debug($"[{InternalName}] 觸發選單項目 {index}（{wanted}）給「{displayName}」");

        var values = stackalloc AtkValue[5];
        for (var i = 0; i < 5; i++)
        {
            values[i].Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int;
            values[i].Int = 0;
        }
        values[1].Int = index;
        addon->FireCallback(5, values, true);
        return true;
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

        // 2026-08-01：部隊置物櫃「取出」時完全沒有任何輸出，連下面「讀不到來源格」那行都沒有，
        // 代表在更前面就 return 了。這一行記在修飾鍵檢查**之前**，才分得出是
        //「hook 沒被呼叫」還是「修飾鍵沒按到」。只有右鍵才會觸發，不會洗版。
        var modifierHeld = Svc.Keys[(VirtualKey)Config.ModifierKeyCode];
        Svc.Log.Debug($"[{InternalName}] 右鍵選單開啟：{source}#{slot} 修飾鍵={(modifierHeld ? "有按" : "沒按")}");

        if (!modifierHeld) return;

        var manager = InventoryManager.Instance();
        if (manager == null) return;

        var item = manager->GetInventorySlot(source, slot);

        // 2026-07-31：部隊置物櫃「取出」完全沒有任何輸出（存入正常），代表在這之前就 return 了，
        // 不是搬移失敗。這行把遊戲實際傳進來的容器與格號記下來，才能分辨是
        //「hook 沒被呼叫」還是「讀不到那一格」。右鍵才會觸發，不會洗版。
        if (item == null || item->ItemId == 0)
        {
            Svc.Log.Debug($"[{InternalName}] 讀不到來源格：{source}#{slot}（item={(item == null ? "null" : "ItemId=0")}）");
            return;
        }

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

        // 🔴 雇員方向優先走遊戲自己的道具命令（見 RetainerItemCommandDelegate 的說明）。
        // 這條路徑由遊戲決定落點，所以不需要我們自己挑目的地格。
        var sourceIsRetainer = Array.IndexOf(RetainerPages, source) >= 0;
        var retainerWindowOpen = GetAgentRetainerItemCommandModule() != 0
            && (Svc.GameGui.GetAddonByName("InventoryRetainer", 1).Address != nint.Zero
                || Svc.GameGui.GetAddonByName("InventoryRetainerLarge", 1).Address != nint.Zero);

        if (sourceIsRetainer || (retainerWindowOpen && Array.IndexOf(PlayerBags, source) >= 0))
        {
            TransferViaRetainerCommand(agent, source, slot, itemId, displayName, sourceIsRetainer);
            return;
        }

        // 🔴 鞍袋方向走右鍵選單（見 TryFireContextMenuEntry）。MoveItemSlot 在這裡同樣是假成功。
        var sourceIsSaddleBag = Array.IndexOf(SaddleBags, source) >= 0;
        var saddleBagWindowOpen = Svc.GameGui.GetAddonByName("InventoryBuddy", 1).Address != nint.Zero;

        if (sourceIsSaddleBag || (saddleBagWindowOpen && Array.IndexOf(PlayerBags, source) >= 0))
        {
            if (TryFireContextMenuEntry(
                    agent,
                    sourceIsSaddleBag ? AddonRowRetrieveFromSaddlebag : AddonRowDepositToSaddlebag,
                    displayName)
                && Config.NotifyOnTransfer)
            {
                Svc.Chat.Print($"[TC Toolbox] 已{(sourceIsSaddleBag ? "取回" : "放入")}「{displayName}」。");
            }
            return;
        }

        // 部隊置物櫃：兩個方向的可用路徑不同，2026-08-01 實機釐清。
        //
        //  取出（置物櫃 → 背包）：右鍵選單裡**有**「取出」，走選單。
        //  存入（背包 → 置物櫃）：遊戲**根本沒有**這個選單項——實機證實從背包右鍵時
        //    主選單只有「自動整理 | 二級指令」兩項，使用者也確認平常是用拖的。
        //    所以存入走不了選單，維持 MoveItemSlot（使用者回報這個方向能動）。
        //
        // ⚠️ 別再嘗試把存入改成點選單了，那個項目不存在。
        var sourceIsFreeCompany = Array.IndexOf(FreeCompanyPages, source) >= 0;

        if (sourceIsFreeCompany)
        {
            if (TryFireContextMenuEntry(agent, AddonRowRetrieveFromFcChest, displayName)
                && Config.NotifyOnTransfer)
            {
                Svc.Chat.Print($"[TC Toolbox] 已取出「{displayName}」。");
            }
            return;
        }

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

        // 立即檢查：MoveItemSlot 會同步更新本機容器，所以這一步能擋掉「呼叫當下就沒成功」。
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

        CloseContextMenu(agent);

        // 🔴 立即檢查通過「不代表真的拿到了」。
        // 雇員／部隊置物櫃／鞍袋是伺服器權威的容器：MoveItemSlot 只是樂觀地先改本機狀態，
        // 伺服器若拒絕（或稍後重新同步），道具會彈回原處 —— 而我們早就印了「已轉移」。
        // 實機徵狀就是「有時候顯示有拿到，實際上沒拿出來」（2026-07-31 使用者回報）。
        //
        // ⚠️ 原本這裡引用 AutoDuty AutoEquipHelper「呼叫後立刻回讀就能判定」當依據，
        //    但那個先例搬的是裝備欄↔兵裝庫，都是本機容器，本機更新即最終狀態。
        //    跨伺服器容器不適用。
        if (IsServerAuthoritative(source))
        {
            pendingVerifications.Add(new PendingVerification(
                source, slot, baseItemId, displayName,
                Environment.TickCount64 + RollbackCheckDelayMs));
            return;
        }

        if (Config.NotifyOnTransfer)
            Svc.Chat.Print($"[TC Toolbox] 已轉移「{displayName}」。");
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

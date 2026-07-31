using System;
using Dalamud.Memory;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Game.Gui.ContextMenu;
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
/// 零封包偽造、不寫記憶體、不做 patch。
///
/// ⚠️ 觸發點有**兩個**，因為單靠任一個都蓋不全（2026-08-01 實機釐清）：
///  1. hook <c>AgentInventoryContext.OpenForItemSlot</c>：同步、資訊最完整，
///     但**只有玩家背包會走這個函式**。實機 log 全部的觸發紀錄清一色是 Inventory*，
///     部隊置物櫃右鍵**一次都沒有**進來過。
///  2. Dalamud 的 <c>IContextMenu.OnMenuOpened</c>：它掛在 <c>RaptureAtkModule</c> vtable[22]，
///     只要開的是 ContextMenu 且 agent 是 AgentInventoryContext 就會觸發，**跟哪個函式開的無關**，
///     所以部隊置物櫃也蓋得到（同一份 log 裡 InventoryTools 就是靠這個看到 FreeCompanyChest 的）。
///     ⚠️ 它在 addon 真正開起來**之前**觸發，所以只能先記下來、下一個 framework tick 再執行。
///
/// 兩者對背包會重複觸發，靠 <see cref="DuplicateGuardMs"/> 的同格同物去重：
/// hook 是同步的、先跑完並設好 lastHandled*，延後那筆就會被擋掉。
///
/// ⚠️ 搬移用哪條路徑要看容器：
///  - **雇員**：走遊戲自己的雇員道具命令（見 <see cref="RetainerItemCommandDelegate"/>）。
///    `MoveItemSlot` 在這裡會「假成功」——本機更新了但伺服器根本沒收到。
///  - **鞍袋**：點遊戲右鍵選單自己的項目（見 <see cref="TryFireContextMenuEntry"/>）。
///    它同樣不能用 MoveItemSlot，而且**不走**雇員道具命令（實機驗證過）。
///  - **其他**（部隊置物櫃／兵裝庫）：仍用 <c>InventoryManager.MoveItemSlot</c>。
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
    /// 本機狀態，伺服器拒絕時道具會彈回原處。
    /// </summary>
    private readonly record struct PendingVerification(
        InventoryType Source, int Slot,
        InventoryType Destination, int DestinationSlot,
        uint BaseItemId, string DisplayName, long DeadlineTick);

    private readonly List<PendingVerification> pendingVerifications = [];

    /// <summary>
    /// 🔴 觀察退回的時間長度。**不是**「等這麼久再看一眼」——是「持續盯到這麼久為止」。
    ///
    /// 原本寫 800ms 而且只在期限到的那一刻檢查一次，這是錯的工具：2026-08-01 實機那筆
    /// 部隊置物櫃存入，伺服器的拒絕（「無法保存道具，其他玩家正在使用儲物櫃。」）
    /// 是在 **5.1 秒後**才到的（02:04:52.166 搬移 → 02:04:57.281 錯誤訊息），
    /// 800ms 的窗口從頭到尾都在「還沒退回」的狀態，必然誤報成功。
    /// ⚠️ 這跟先前修雇員時踩過的是同一個坑，別再把它調回短窗口。
    /// </summary>
    private const int RollbackWatchMs = 12_000;

    /// <summary>
    /// 這次搬移需不需要盯退回。**兩邊都要看**：來源是權威容器（取出）固然要，
    /// 目的地是權威容器（存入）一樣要——原本只驗來源，所以「背包→部隊置物櫃」
    /// 這個方向完全不進確認流程，伺服器退回時我們早就印了「已轉移」。
    /// </summary>
    private static bool IsServerAuthoritative(InventoryType type)
        => Array.IndexOf(RetainerPages, type) >= 0
        || Array.IndexOf(FreeCompanyPages, type) >= 0
        || Array.IndexOf(SaddleBags, type) >= 0;

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

        // 第二個觸發點：部隊置物櫃不走 OpenForItemSlot，只有這條蓋得到（見類別說明）。
        Svc.ContextMenu.OnMenuOpened += OnMenuOpened;

        Svc.Framework.Update += OnUpdate;
    }

    protected override void OnDisable()
    {
        Svc.Framework.Update -= OnUpdate;
        Svc.ContextMenu.OnMenuOpened -= OnMenuOpened;
        openForItemSlotHook?.Dispose();
        openForItemSlotHook = null;

        pendingMenu = null;
        pendingVerifications.Clear();
        lastHandledSource = InventoryType.Invalid;
        lastHandledSlot = -1;
        lastHandledItemId = 0;
        lastHandledTick = 0;
    }

    /// <summary>
    /// 由 <see cref="OnMenuOpened"/> 記下、下一個 framework tick 才執行的右鍵選單。
    /// ⚠️ 不能在 OnMenuOpened 當下就做事：那時 ContextMenu addon 還沒開起來，
    /// <c>agent-&gt;AgentInterface.GetAddonId()</c> 拿到的是上一個選單（或 0）。
    /// </summary>
    private (InventoryType Source, int Slot)? pendingMenu;

    private void OnMenuOpened(IMenuOpenedArgs args)
    {
        if (Config.ModifierKeyCode == 0) return;

        if (args.MenuType == ContextMenuType.Inventory)
        {
            if (args.Target is not MenuTargetInventory inv || inv.TargetItem is not { } item)
            {
                Svc.Log.Information(
                    $"[{InternalName}] 收到道具右鍵選單（addon={args.AddonName ?? "?"}）但讀不到目標道具。");
                return;
            }

            // GameInventoryType 的數值與 InventoryType 完全一致（Inventory1=0 … FreeCompanyPage1=20000）。
            pendingMenu = ((InventoryType)(ushort)item.ContainerType, (int)item.InventorySlot);
            return;
        }

        // 🔴 部隊置物櫃的右鍵選單**不是道具選單**。
        // 2026-08-01 實機證實：Dalamud 只在 agent 是 AgentInventoryContext 時才標成
        // ContextMenuType.Inventory，而部隊置物櫃走的是 AgentContext（一般選單），
        // 所以 MenuType 是 Default、MenuTargetInventory 拿不到、`OpenForItemSlot` 也不會被呼叫
        // ——兩條路同時斷在同一個原因上，這就是它一直沒反應的真正理由。
        //
        // 一般選單給不了容器與格號，只好從「滑鼠正懸停在哪個道具上」反推。
        // ⚠️ 不去讀 AgentContext 的選單項目來點：那需要未經驗證的索引對應，
        //    而部隊置物櫃的選單裡有「丟棄」，點錯一格的代價太高。
        if (args.AddonName != "FreeCompanyChest") return;

        if (!TryResolveHoveredFreeCompanySlot(out var source, out var slot))
        {
            Svc.Log.Information(
                $"[{InternalName}] 部隊置物櫃右鍵，但對不出懸停的格號（HoveredItem={Svc.GameGui.HoveredItem}）。");
            return;
        }

        pendingMenu = (source, slot);
    }

    /// <summary>
    /// 用 <c>GameGui.HoveredItem</c> 反推部隊置物櫃裡的來源格。
    /// 同款道具有多份時取第一個——對「把這個拿出來」而言彼此可互換。
    /// </summary>
    private static bool TryResolveHoveredFreeCompanySlot(out InventoryType source, out int slot)
    {
        source = InventoryType.Invalid;
        slot = -1;

        var hovered = Svc.GameGui.HoveredItem;
        if (hovered == 0) return false;

        // HoveredItem 的 HQ 是 +1000000（和 InventoryItem.ItemId 的編碼方式不同）。
        var wantedId = (uint)(hovered % 1_000_000);
        var wantHq = hovered >= 1_000_000;
        if (wantedId == 0) return false;

        var manager = InventoryManager.Instance();
        if (manager == null) return false;

        foreach (var page in FreeCompanyPages)
        {
            var container = manager->GetInventoryContainer(page);
            if (container == null || !container->IsLoaded) continue;

            for (var i = 0; i < container->Size; i++)
            {
                var item = container->GetInventorySlot(i);
                if (item == null || item->ItemId == 0) continue;
                if (item->GetBaseItemId() != wantedId) continue;
                if (((item->Flags & InventoryItem.ItemFlags.HighQuality) != 0) != wantHq) continue;

                source = page;
                slot = i;
                return true;
            }
        }

        return false;
    }

    private void OnUpdate(IFramework _)
    {
        if (pendingMenu is { } menu)
        {
            pendingMenu = null;
            var agent = AgentInventoryContext.Instance();
            if (agent != null)
            {
                try
                {
                    HandleContextMenu(agent, menu.Source, menu.Slot);
                }
                catch (Exception ex)
                {
                    Svc.Log.Error(ex, $"[{InternalName}] 處理延後的右鍵轉移時發生例外");
                }
            }
        }

        if (pendingVerifications.Count == 0) return;

        var now = Environment.TickCount64;
        var manager = InventoryManager.Instance();
        if (manager == null) return;

        for (var i = pendingVerifications.Count - 1; i >= 0; i--)
        {
            var p = pendingVerifications[i];

            // 🔴 每一幀都看，不是等到期限才看一眼。退回可能要好幾秒才回來，
            // 但也可能一秒內就回來——只在期限那一刻取樣，兩種都會漏。
            //
            // 兩個方向的退回長得不一樣，所以兩個都驗：
            //   取出被退回 → 道具**回到來源格**
            //   存入被退回 → 道具**從目的地格消失**（部隊置物櫃 2026-08-01 實機就是這個）
            var backAtSource = IsItemAt(manager, p.Source, p.Slot, p.BaseItemId);
            var goneFromDestination = !IsItemAt(manager, p.Destination, p.DestinationSlot, p.BaseItemId);

            if (backAtSource || goneFromDestination)
            {
                pendingVerifications.RemoveAt(i);
                Svc.Log.Warning(
                    $"[{InternalName}] 伺服器退回：{p.Source}#{p.Slot} → {p.Destination}#{p.DestinationSlot} " +
                    $"itemId={p.BaseItemId} 回到來源={backAtSource} 目的地消失={goneFromDestination} " +
                    $"（搬移後 {RollbackWatchMs - (p.DeadlineTick - now)}ms）");

                if (Throttle.Pass("AutoInventoryTransfer-RolledBack", 3_000))
                    Svc.Chat.PrintError(
                        $"[TC Toolbox] 「{p.DisplayName}」沒有真的轉移過去（伺服器已退回），請改用手動拖放。");
                continue;
            }

            // 盯滿了都沒退回才算數，靜默移除（成功訊息在搬移當下就印過了）。
            if (now >= p.DeadlineTick)
                pendingVerifications.RemoveAt(i);
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

    // ⚠️ 部隊置物櫃刻意不走 TryFireContextMenuEntry：它的右鍵選單是 AgentContext 的一般選單，
    // 而那個函式讀的是 AgentInventoryContext 的 EventParams，索引對不上；
    // 而且那個選單裡有「丟棄」，點錯一格的代價太高。這兩個 row id 目前只給診斷用。
    // （2026-08-01 用台服 7.20 的 Addon 表核對過：2950=「取出」、2951=「放入儲物櫃」。）
    private const uint AddonRowChestRetrieve = 2950;
    private const uint AddonRowChestDeposit = 2951;

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

        // 選單長相一律記下來（Information 級，因為使用者的記錄等級會濾掉 DBG）。
        // 「▸」標的是被收進二級指令的項目——那是最可能讓找不到的原因，
        // 而且從錯誤訊息裡看不出來，只能靠這行分辨「沒有這個項目」與「在二級選單裡」。
        var dump = new string[itemCount];
        for (var entry = 0; entry < itemCount; entry++)
        {
            var inSubmenu = (agent->ContextItemSubmenuMask & (1u << entry)) != 0;
            dump[entry] = $"{entry}{(inSubmenu ? "▸" : "")}:{labels[entry]}";
        }

        Svc.Log.Information(
            $"[{InternalName}] 找「{wanted}」→ {(index == -1 ? "沒找到" : $"第 {index} 項")}；" +
            $"選單 {itemCount} 項（起點 EventParams[{startIndex}]，▸＝二級指令）：{string.Join(" | ", dump)}");

        if (index == -1)
        {
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

        // 2026-08-01：這行救了一次診斷——部隊置物櫃右鍵時它**完全沒出現**，
        // 而它記在修飾鍵檢查之前，所以直接證明是「hook 根本沒被呼叫」而不是「修飾鍵沒按到」，
        // 才找到 OpenForItemSlot 不是置物櫃入口這件事。留著，並改成 Information
        // （使用者的記錄等級會濾掉 DBG，DBG 只是這台機器剛好開著）。只有右鍵才觸發，不會洗版。
        var modifierHeld = Svc.Keys[(VirtualKey)Config.ModifierKeyCode];
        Svc.Log.Information($"[{InternalName}] 右鍵選單開啟：{source}#{slot} 修飾鍵={(modifierHeld ? "有按" : "沒按")}");

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

        // 🔴 部隊置物櫃：MoveItemSlot **兩個方向都不成立**，2026-08-01 實機定案。
        //
        //   02:48:59 FreeCompanyPage1#0 → Inventory1#26  verified=True
        //   02:49:09 伺服器退回：回到來源=True（10625ms）
        //   02:49:39 FreeCompanyPage1#0 → Inventory1#5   verified=True
        //   02:49:43 伺服器退回：回到來源=True（3922ms）
        //   存入方向同樣連兩次被退回（置物櫃歷史查無該筆，是真退回不是誤判）。
        //
        // ⚠️ 上一版賭「取出不需要確認對話框所以 MoveItemSlot 可能成立」——賭錯了。
        // 部隊置物櫃跟雇員／鞍袋一樣是伺服器權威容器，MoveItemSlot 只會假成功。
        //
        // 🔴 2026-08-01：「改用 ExecuteCommand(405 MoveItemBetweenInventory)」這條路**已排除**，
        //    不要再嘗試。三個獨立方向都指向同一結論：
        //
        //    1. OmenTools 雖然定義了 `InventoryCommand.Move(來源, 目標)`，但**全 GitHub 沒有任何
        //       呼叫點**（`gh search code "InventoryCommand.Move"` 只有無關的 Unity 專案）。
        //    2. DailyRoutines 自己的 AutoInventoryTransfer（我們這個模組的原型，原始碼公開在
        //       `Dalamud-DailyRoutines/DailyRoutines.ModulesPublic:Interface/AutoInventoryTransfer.cs`）
        //       **根本沒用 ExecuteCommand**，它就是點右鍵選單項目（比對 Addon 97/98/881/887）；
        //       而且它的 `IsInventoryOpen()` **完全沒有列入部隊置物櫃**——上游也不支援這個容器。
        //    3. 對台服 7.20 `ffxiv_dx11.exe` 離線反編譯：405 在整個 .text 只有 **2 個**呼叫點，
        //       **兩個都是 param2=0**（不是 OmenTools 註解說的「param1=來源, param2=目標」）。
        //       其中一個位在 0x14083ed4d 這個函式裡，它用一個「是否已請求過」的 bitmask 當守衛，
        //       失敗路徑印的是 LogMessage #1860「獲得公會儲物櫃資料失敗。」——
        //       也就是說 **405 是「向伺服器請求載入置物櫃頁面資料」，不是搬移道具**。
        //
        //    我們實機看到的退回訊息是 LogMessage #1873「無法保存道具，其他玩家正在使用儲物櫃。」，
        //    那是伺服器端的拒絕；405 不會、也不該改變它。
        //
        // 唯一剩下的路仍是點遊戲自己的選單項目，但那是 AgentContext 的一般選單
        // （不是 AgentInventoryContext），索引對應**還是沒驗證過**，而**那個選單裡有「丟棄」**。
        //
        // ⚠️ 索引基準目前有兩個互相矛盾的來源，差 1，而差 1 就是點到隔壁那項：
        //     Dalamud 自己的 ContextMenu.cs：addon 的 AtkValues 前 **7** 格是表頭（SetupGenericMenu(7,…)），
        //     OmenTools 的 AddonContextMenuEvent：讀的是 AtkValues[i + **8**]。
        //   在這個差異被實機資料解決以前，這一版只傾印、**不點任何東西**。
        if (Array.IndexOf(FreeCompanyPages, source) >= 0
            || (Array.IndexOf(PlayerBags, source) >= 0 && UiHelper.IsAddonReady("FreeCompanyChest")))
        {
            DumpContextMenuLayout(displayName);
            if (Throttle.Pass("AutoInventoryTransfer-FcUnsupported", 3_000))
            {
                Svc.Chat.PrintError(
                    $"[TC Toolbox] 部隊置物櫃目前只能手動拖放，「{displayName}」未轉移。" +
                    "（遊戲不接受這個容器的程式化搬移，選單內容已記進記錄檔供後續修正）");
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

        if (Config.NotifyOnTransfer)
            Svc.Chat.Print($"[TC Toolbox] 已轉移「{displayName}」。");

        // 🔴 立即檢查通過「不代表真的搬過去了」。
        // 雇員／部隊置物櫃／鞍袋是伺服器權威的容器：MoveItemSlot 只是樂觀地先改本機狀態，
        // 伺服器若拒絕，道具會彈回原處 —— 而我們早就印了「已轉移」。
        //
        // ⚠️ 原本這裡引用 AutoDuty AutoEquipHelper「呼叫後立刻回讀就能判定」當依據，
        //    但那個先例搬的是裝備欄↔兵裝庫，都是本機容器，本機更新即最終狀態。
        //    跨伺服器容器不適用。
        //
        // 上面的「已轉移」照樣先印（畫面上道具確實動了，不印反而更困惑），
        // 真的被退回時再補一則錯誤訊息蓋掉它。
        if (IsServerAuthoritative(source) || IsServerAuthoritative(destination))
        {
            pendingVerifications.Add(new PendingVerification(
                source, slot, destination, destinationSlot, baseItemId, displayName,
                Environment.TickCount64 + RollbackWatchMs));
        }
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

    /// <summary>讀 AtkValue 的字串內容；不是字串型別或指標為 null 就回 null。</summary>
    private static string? ReadAtkString(AtkValue v)
    {
        if (v.Type is not FFXIVClientStructs.FFXIV.Component.GUI.ValueType.String
            and not FFXIVClientStructs.FFXIV.Component.GUI.ValueType.ManagedString)
            return null;

        var ptr = v.String.Value;
        if (ptr == null) return null;
        return MemoryHelper.ReadSeStringNullTerminated((nint)ptr).TextValue.Trim();
    }

    /// <summary>
    /// 傾印部隊置物櫃右鍵選單的**兩份**資料。⚠️ 只讀不點，不碰任何道具。
    ///
    /// 這個函式存在的唯一目的，是用一次實機右鍵把「選單項目的索引基準」定死，
    /// 因為現有兩個來源互相矛盾（差 1），而在有「丟棄」的選單裡差 1 會毀掉道具：
    ///   - Dalamud `ContextMenu.cs` 的 `SetupGenericMenu(7, …)`：addon AtkValues 前 7 格是表頭
    ///   - OmenTools `AddonContextMenuEvent`：讀 `AtkValues[i + 8]`
    ///
    /// 所以兩邊都印：
    ///   (A) AgentContext.CurrentContextMenu-&gt;EventParams[0..32] ＋ 兩個遮罩
    ///   (B) ContextMenu **addon 自己**的 AtkValues[0..Count]，含型別
    /// 有了 (B) 就能直接看出 `AtkValues[0]` 的項目數、字串區塊實際從第幾格開始，
    /// 以及「取出」「放入儲物櫃」落在哪個絕對索引 —— 不用再猜。
    /// </summary>
    private void DumpContextMenuLayout(string displayName)
    {
        var sheet = Svc.Data.GetExcelSheet<Addon>();
        var wantRetrieve = sheet?.GetRowOrDefault(AddonRowChestRetrieve)?.Text.ExtractText().Trim() ?? "";
        var wantDeposit = sheet?.GetRowOrDefault(AddonRowChestDeposit)?.Text.ExtractText().Trim() ?? "";

        // ---- (A) agent 側 ----
        var agentContext = AgentContext.Instance();
        var menu = agentContext == null ? null : agentContext->CurrentContextMenu;
        if (menu == null)
        {
            Svc.Log.Information($"[{InternalName}] 「{displayName}」：拿不到 AgentContext 的目前選單。");
        }
        else
        {
            var entries = new List<string>();
            for (var i = 0; i < 33; i++)
            {
                var text = ReadAtkString(menu->EventParams[i]);
                if (string.IsNullOrEmpty(text)) continue;

                var disabled = (menu->ContextItemDisabledMask & (1u << i)) != 0 ? "✖" : "";
                var submenu = (menu->ContextSubMenuMask & (1u << i)) != 0 ? "▸" : "";
                entries.Add($"[{i}]{disabled}{submenu}{text}");
            }

            Svc.Log.Information(
                $"[{InternalName}] (A) AgentContext.EventParams（✖＝停用 ▸＝二級指令）：" +
                string.Join(" | ", entries));
        }

        // ---- (B) addon 側：這份才是 Callback 索引真正對應的陣列 ----
        var addon = UiHelper.GetAddon("ContextMenu");
        if (addon == null || addon->AtkValues == null)
        {
            Svc.Log.Information($"[{InternalName}] (B) 拿不到 ContextMenu addon 的 AtkValues。");
            return;
        }

        var count = addon->AtkValuesCount;
        if (count == 0)
        {
            Svc.Log.Information($"[{InternalName}] (B) ContextMenu addon 的 AtkValuesCount=0，沒有東西可讀。");
            return;
        }

        // ⚠️ 只有在 count>0 之後才讀得起 [0]，否則就是讀不屬於我們的記憶體。
        var declared = addon->AtkValues[0].Type == FFXIVClientStructs.FFXIV.Component.GUI.ValueType.UInt
            ? addon->AtkValues[0].UInt
            : 0u;

        var dump = new List<string>();
        var hitRetrieve = -1;
        var hitDeposit = -1;
        for (var i = 0; i < count && i < 64; i++)
        {
            var v = addon->AtkValues[i];
            var text = ReadAtkString(v);
            dump.Add(text == null ? $"[{i}]{v.Type}" : $"[{i}]\"{text}\"");

            if (text == null) continue;
            if (hitRetrieve < 0 && wantRetrieve.Length > 0 && text == wantRetrieve) hitRetrieve = i;
            if (hitDeposit < 0 && wantDeposit.Length > 0 && text == wantDeposit) hitDeposit = i;
        }

        Svc.Log.Information(
            $"[{InternalName}] (B) ContextMenu addon AtkValues（AtkValuesCount={count} 宣告項目數={declared}）：" +
            string.Join(" ", dump));

        // 把結論直接算好印出來，省掉人工對照。兩種基準都列，實機一比就知道哪個對。
        static string Interpret(int absolute) =>
            absolute < 0 ? "沒找到" : $"絕對索引 {absolute} → 基準7 時項目#{absolute - 7}／基準8 時項目#{absolute - 8}";

        Svc.Log.Information(
            $"[{InternalName}] (B) 「{wantRetrieve}」(Addon#{AddonRowChestRetrieve})：{Interpret(hitRetrieve)}；" +
            $"「{wantDeposit}」(Addon#{AddonRowChestDeposit})：{Interpret(hitDeposit)}");
    }

    private static void CloseContextMenu(AgentInventoryContext* agent)
    {
        // 兩種選單都要關得掉：道具選單掛在 AgentInventoryContext，
        // 部隊置物櫃那種一般選單掛在 AgentContext。
        // ⚠️ 原本只問 AgentInventoryContext 要 addon id，一般選單那條路拿到 0 就直接 return，
        // 所以實機看到的是「道具搬走了但右鍵選單還開著」。
        var addonId = agent->AgentInterface.GetAddonId();
        AgentInterface* owner = &agent->AgentInterface;

        if (addonId == 0)
        {
            var agentContext = AgentContext.Instance();
            if (agentContext == null) return;
            addonId = agentContext->AgentInterface.GetAddonId();
            owner = &agentContext->AgentInterface;
        }

        if (addonId == 0) return;

        var addon = AtkStage.Instance()->RaptureAtkUnitManager->GetAddonById((ushort)addonId);
        if (addon == null) return;

        owner->Hide();
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

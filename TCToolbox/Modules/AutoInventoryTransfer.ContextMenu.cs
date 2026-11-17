using System;
using System.IO;
using Dalamud.Memory;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
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

public sealed unsafe partial class AutoInventoryTransfer
{
    /// <summary>
    /// 由 <see cref="OnMenuOpened"/> 記下、下一個 framework tick 才執行的右鍵選單。
    /// ⚠️ 不能在 OnMenuOpened 當下就做事：那時 ContextMenu addon 還沒開起來，
    /// <c>agent-&gt;AgentInterface.GetAddonId()</c> 拿到的是上一個選單（或 0）。
    /// <para><see cref="HoverBaseItemId"/>／<see cref="HoverHighQuality"/> 只在
    /// <see cref="IsChestHover"/> 為 true 時有意義，而且**必須在右鍵當下就抓**：
    /// 它是「使用者到底點了什麼」的唯一可信快照，延後一幀滑鼠可能已經移開了。</para>
    /// </summary>
    private readonly record struct PendingMenu(
        InventoryType Source, int Slot,
        bool IsChestHover, uint HoverBaseItemId, bool HoverHighQuality);

    private PendingMenu? pendingMenu;

    /// <summary>修飾鍵沒按的右鍵選單次數，攢滿 <see cref="UnmodifiedMenuSummaryMs"/> 才寫一行彙總。只在框架執行緒讀寫。</summary>
    /// <remarks>停用時刻意不寫掉也不歸零：<c>OnDisable</c> 不一定在框架執行緒上。</remarks>
    private int unmodifiedMenuCount;
    private long unmodifiedMenuWindowStart;
    private const long UnmodifiedMenuSummaryMs = 10 * 60_000;

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
            pendingMenu = new PendingMenu(
                (InventoryType)(ushort)item.ContainerType, (int)item.InventorySlot,
                IsChestHover: false, HoverBaseItemId: 0, HoverHighQuality: false);
            return;
        }

        // 🔴 公會儲物櫃的右鍵選單**不是道具選單**。
        // 一般選單給不了容器與格號，只好從「滑鼠正懸停在哪個道具上」反推。
        // ⚠️ 不去讀 AgentContext 的選單項目來點：那需要未經驗證的索引對應，
        //    而公會儲物櫃的選單裡有「丟棄」，點錯一格的代價太高。
        if (args.AddonName != "FreeCompanyChest") return;

        if (!TryResolveHoveredFreeCompanySlot(out var source, out var slot, out var hoverId, out var hoverHq))
        {
            Svc.Log.Information(
                $"[{InternalName}] 公會儲物櫃右鍵，但對不出懸停的格號（HoveredItem={Svc.GameGui.HoveredItem}）。");
            return;
        }

        pendingMenu = new PendingMenu(source, slot, IsChestHover: true, hoverId, hoverHq);
    }

    /// <summary>
    /// 用 <c>GameGui.HoveredItem</c> 反推公會儲物櫃裡的來源格。
    /// 同款道具有多份時取第一個——對「把這個拿出來」而言彼此可互換。
    /// <para>⚠️ 這是交叉驗證的 (B) 側。它精確到**道具**、不精確到格號，
    /// 所以真正拿去搬的格號取自 (A) 側，見 <see cref="TryResolveChestSource"/>。</para>
    /// </summary>
    private static bool TryResolveHoveredFreeCompanySlot(
        out InventoryType source, out int slot, out uint baseItemId, out bool highQuality)
    {
        source = InventoryType.Invalid;
        slot = -1;
        baseItemId = 0;
        highQuality = false;

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
            if (!InventoryScan.TryGet(manager, page, out var container)) continue;

            for (var i = 0; i < container->Size; i++)
            {
                var item = container->GetInventorySlot(i);
                if (item == null || item->ItemId == 0) continue;
                if (item->GetBaseItemId() != wantedId) continue;
                if (((item->Flags & InventoryItem.ItemFlags.HighQuality) != 0) != wantHq) continue;

                source = page;
                slot = i;
                baseItemId = wantedId;
                highQuality = wantHq;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 決定「右鍵點的到底是置物櫃哪一格」：(A) agent 裡記的容器與格號、(B) 滑鼠懸停的道具，
    /// **兩個獨立來源都要成立、而且要互相吻合**才回 true。🔴 這個「必須一致」**不是保險絲，是核心安全設計**：
    /// 那兩個偏移雖然在台服二進位裡佐證過大小與重設值，但「我們讀到的是這次右鍵寫進去的、
    /// 而不是上一次的殘留或別的欄位」**無法離線證明**。
    /// 也就是說「偏移是錯的」的後果是**不動作**，不是搬錯道具。
    /// </summary>
    private static bool TryResolveChestSource(
        nint agent, uint hoverBaseItemId, bool hoverHighQuality,
        out InventoryType source, out int slot, out string diagnosis)
    {
        source = InventoryType.Invalid;
        slot = -1;

        var agentType = (InventoryType)(*(uint*)(agent + ChestContextInventoryTypeOffset));
        var agentSlot = *(short*)(agent + ChestContextInventorySlotOffset);

        if (Array.IndexOf(FreeCompanyPages, agentType) < 0)
        {
            diagnosis = $"(A) 容器={agentType}({(uint)agentType}) 不是公會儲物櫃分頁";
            return false;
        }

        var manager = InventoryManager.Instance();
        if (manager == null)
        {
            diagnosis = "(A) 拿不到 InventoryManager";
            return false;
        }

        if (!InventoryScan.TryGet(manager, agentType, out var container))
        {
            diagnosis = $"(A) 容器 {agentType} 未載入（或格位陣列尚未配置）";
            return false;
        }

        if (agentSlot < 0 || agentSlot >= container->Size)
        {
            diagnosis = $"(A) 格號 {agentSlot} 超出 {agentType} 範圍（Size={container->Size}）";
            return false;
        }

        var item = container->GetInventorySlot(agentSlot);
        if (item == null || item->ItemId == 0)
        {
            diagnosis = $"(A) {agentType}#{agentSlot} 是空格";
            return false;
        }

        var agentBaseItemId = item->GetBaseItemId();
        var agentHighQuality = (item->Flags & InventoryItem.ItemFlags.HighQuality) != 0;

        if (agentBaseItemId != hoverBaseItemId || agentHighQuality != hoverHighQuality)
        {
            diagnosis =
                $"(A) {agentType}#{agentSlot} 是 itemId={agentBaseItemId} hq={agentHighQuality}，" +
                $"但 (B) 懸停的是 itemId={hoverBaseItemId} hq={hoverHighQuality} —— 不一致";
            return false;
        }

        source = agentType;
        slot = agentSlot;
        diagnosis = $"(A)(B) 一致：{agentType}#{agentSlot} itemId={agentBaseItemId} hq={agentHighQuality}";
        return true;
    }

    private void OnUpdate(IFramework _)
    {
        FlushUnmodifiedMenuSummary();

        if (pendingMenu is { } menu)
        {
            pendingMenu = null;
            var agent = AgentInventoryContext.Instance();
            if (agent != null)
            {
                try
                {
                    HandleContextMenu(agent, menu);
                }
                catch (Exception ex)
                {
                    Svc.Log.Error(ex, $"[{InternalName}] 處理延後的右鍵轉移時發生例外");
                }
            }
        }

        if (pendingVerifications.Count == 0)
        {
            DrainChestQueue();
            return;
        }

        var now = Environment.TickCount64;
        var manager = InventoryManager.Instance();
        if (manager == null) return;

        for (var i = pendingVerifications.Count - 1; i >= 0; i--)
        {
            var p = pendingVerifications[i];
            var elapsed = now - p.StartTick;

            // ── MoveItemInChest：非同步請求，成功的長相是「道具離開來源格」 ──
            //
            // 🔑 這一組 log 就是判斷「只裝 MoveItemInChest 夠不夠」的依據
            //    （我們刻意沒裝 SendInventoryRefresh 的 op-lock hook，見 TransferViaChest 的說明）。
            //    看到「已生效」＝夠了；看到「逾時未生效」＝不夠，那時才需要重新評估 op-lock。
            if (p.Kind == VerificationKind.ChestDeparture)
            {
                // 只比 itemId 分不出「真的沒搬走」跟「搬走一部分」／「同款另一疊回填進來」：
                // 數量要用「沒有少掉」而不是「不相等」，否則背包格被塞進新東西會被誤判成已生效。
                var stillAtSource =
                    TryReadSlot(manager, p.Source, p.Slot, out var curItemId, out var curQty, out var curFlags)
                    && curItemId == p.BaseItemId && curFlags == p.SourceFlags && curQty >= p.SourceQuantity;

                if (!stillAtSource)
                {
                    pendingVerifications.RemoveAt(i);
                    chestSettleUntilTick = now + ChestSettleMs;

                    var landed = p.Destination != InventoryType.Invalid &&
                                 IsItemAt(manager, p.Destination, p.DestinationSlot, p.BaseItemId);
                    var where = p.Destination == InventoryType.Invalid
                        ? "（落點由遊戲決定）"
                        : $"{p.Destination}#{p.DestinationSlot} 落在指定格={landed}";

                    Svc.Log.Debug(
                        $"[{InternalName}] 置物櫃搬移已生效：{p.Source}#{p.Slot} → {where} " +
                        $"itemId={p.BaseItemId} 耗時={elapsed}ms 送出次數={p.SendCount} " +
                        $"來源格現況={DescribeSlot(manager, p.Source, p.Slot)}（送出時 {p.BaseItemId}×{p.SourceQuantity}）");

                    if (Config.NotifyOnTransfer)
                        Svc.Chat.Print($"[TC Toolbox] 已轉移「{p.DisplayName}」。");
                    continue;
                }

                if (now < p.DeadlineTick) continue;

                pendingVerifications.RemoveAt(i);
                Svc.Log.Warning(
                    $"[{InternalName}] 置物櫃搬移逾時未生效：{p.Source}#{p.Slot} itemId={p.BaseItemId} " +
                    $"送出次數={p.SendCount} 在途={CountChestDepartures()} " +
                    $"來源格現況={DescribeSlot(manager, p.Source, p.Slot)}（送出時 {p.BaseItemId}×{p.SourceQuantity}） " +
                    $"——{RollbackWatchMs}ms 後來源格內容沒變，MoveItemInChest 沒有被伺服器受理。");

                if (Throttle.Pass("AutoInventoryTransfer-ChestTimeout", 3_000))
                    Svc.Chat.PrintError(
                        $"[TC Toolbox] 「{p.DisplayName}」沒有轉移成功（儲物櫃沒有回應），請改用手動拖放。");
                continue;
            }

            // 🔴 每一幀都看，不是等到期限才看一眼。退回可能要好幾秒才回來，
            // 但也可能一秒內就回來——只在期限那一刻取樣，兩種都會漏。
            // 兩個方向的退回長得不一樣，所以兩個都驗：
            //   取出被退回 → 道具**回到來源格**
            //   存入被退回 → 道具**從目的地格消失**
            var backAtSource = IsItemAt(manager, p.Source, p.Slot, p.BaseItemId);
            var goneFromDestination = !IsItemAt(manager, p.Destination, p.DestinationSlot, p.BaseItemId);

            if (backAtSource || goneFromDestination)
            {
                pendingVerifications.RemoveAt(i);
                Svc.Log.Warning(
                    $"[{InternalName}] 伺服器退回：{p.Source}#{p.Slot} → {p.Destination}#{p.DestinationSlot} " +
                    $"itemId={p.BaseItemId} 回到來源={backAtSource} 目的地消失={goneFromDestination} " +
                    $"（搬移後 {elapsed}ms）");

                if (Throttle.Pass("AutoInventoryTransfer-RolledBack", 3_000))
                    Svc.Chat.PrintError(
                        $"[TC Toolbox] 「{p.DisplayName}」沒有真的轉移過去（伺服器已退回），請改用手動拖放。");
                continue;
            }

            // 盯滿了都沒退回才算數，靜默移除（成功訊息在搬移當下就印過了）。
            if (now >= p.DeadlineTick)
                pendingVerifications.RemoveAt(i);
        }

        DrainChestQueue();
    }

    /// <summary>每 <see cref="UnmodifiedMenuSummaryMs"/> 把「修飾鍵沒按」的右鍵次數寫成一行 Debug；這段時間一次都沒有就不寫。</summary>
    private void FlushUnmodifiedMenuSummary()
    {
        var now = Environment.TickCount64;
        if (unmodifiedMenuWindowStart == 0) unmodifiedMenuWindowStart = now;
        if (now - unmodifiedMenuWindowStart < UnmodifiedMenuSummaryMs) return;

        if (unmodifiedMenuCount > 0)
            Svc.Log.Debug(
                $"[{InternalName}] 右鍵選單開啟：修飾鍵沒按 {unmodifiedMenuCount} 次（最近 {(now - unmodifiedMenuWindowStart) / 60_000} 分鐘）");

        unmodifiedMenuCount = 0;
        unmodifiedMenuWindowStart = now;
    }

    private void OpenForItemSlotDetour(
        AgentInventoryContext* agent, InventoryType inventoryType, int slot, int a4, uint addonId)
    {
        // 原函式已由 InventoryContextHook 先跑完。
        try
        {
            // 原函式開頭的「封鎖格」檢查成立時直接返回、不重建選單，detour 照樣被呼叫 ⇒ 選單是上一次的，不動作。
            if (agent->TargetInventoryId != inventoryType || agent->TargetInventorySlotId != slot)
            {
                Svc.Log.Debug(
                    $"[{InternalName}] 這次右鍵原生端沒有重建選單（目標 {inventoryType}#{slot}，" +
                    $"選單是 {agent->TargetInventoryId}#{agent->TargetInventorySlotId}），不動作。");
                return;
            }

            HandleContextMenu(agent, new PendingMenu(
                inventoryType, slot, IsChestHover: false, HoverBaseItemId: 0, HoverHighQuality: false));
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, $"[{InternalName}] 處理右鍵轉移時發生例外");
        }
    }

    private void HandleContextMenu(AgentInventoryContext* agent, PendingMenu menu)
    {
        var source = menu.Source;
        var slot = menu.Slot;

        if (Config.ModifierKeyCode == 0) return;

        // 交易視窗開著時右鍵屬於「快速交易」，同一顆鍵不兩邊都接。
        if (Svc.Condition[ConditionFlag.TradeOpen]) return;

        var csFramework = CSFramework.Instance();
        if (csFramework == null || csFramework->WindowInactive) return;

        // 這裡記在修飾鍵檢查與下面那道「略過重複觸發」去重**之前**：分辨「hook 根本沒被呼叫」與「修飾鍵沒按到」。
        // 有按才逐筆寫 Debug；沒按的只計數，每 10 分鐘彙總一行（見 FlushUnmodifiedMenuSummary），
        // 鑑別力不變、量少一個數量級。
        var modifierHeld = Svc.Keys[(VirtualKey)Config.ModifierKeyCode];
        if (modifierHeld)
            Svc.Log.Debug($"[{InternalName}] 右鍵選單開啟：{source}#{slot} 修飾鍵=有按");
        else
            unmodifiedMenuCount++;

        if (!modifierHeld) return;

        // AutoRetainer 快捷出售與 Marketbuddy 快速上架也在同一支開選單函式上、用修飾鍵觸發；兩邊同時選會打在關閉中的選單上。
        // ⇒ 同時還按著 Shift／Ctrl／Alt 中其他的（不算自己那一組）就不動作。
        if (OtherModifierHeld(Config.ModifierKeyCode))
        {
            Svc.Log.Debug($"[{InternalName}] 修飾鍵有按，但同時按著其他修飾鍵，不動作：{source}#{slot}");
            return;
        }

        // 🔴 置物櫃「取出」方向：右鍵選單給不了格號，所以在動任何東西之前先做 (A)(B) 交叉驗證。
        // 兩邊算出來的道具不一致就整段放棄 —— 寧可不動作，也不要搬錯道具。
        if (menu.IsChestHover)
        {
            // ⚠️ 一定要先確定 agent 非 null 再去讀它的欄位。
            // 少了這一步就會變成對 0x1B2C 這種低位址解參考，那是自找的存取違規。
            var chestAgent = ResolveFreeCompanyChestAgent();
            if (chestAgent == null)
            {
                Svc.Log.Information($"[{InternalName}] 置物櫃 agent 未就緒，不動作。");
                return;
            }

            if (!TryResolveChestSource(
                    (nint)chestAgent, menu.HoverBaseItemId, menu.HoverHighQuality,
                    out var chestSource, out var chestSlot, out var diagnosis))
            {
                Svc.Log.Information($"[{InternalName}] 置物櫃來源格交叉驗證失敗，不動作：{diagnosis}");
                return;
            }

            Svc.Log.Information($"[{InternalName}] 置物櫃來源格交叉驗證通過：{diagnosis}");
            source = chestSource;
            slot = chestSlot;
        }

        var manager = InventoryManager.Instance();
        if (manager == null) return;

        var containerReady = InventoryScan.TryGetIgnoringLoaded(manager, source, out _);
        var item = containerReady ? manager->GetInventorySlot(source, slot) : null;

        // 要使用者回報的診斷：把遊戲實際傳進來的容器與格號記下來，才能分辨是
        // 「hook 沒被呼叫」還是「讀不到那一格」。右鍵才會觸發，不會洗版。
        if (item == null || item->ItemId == 0)
        {
            // 格位陣列正常且 ItemId=0＝空格，右鍵逐格掃過時是常態，只進 Debug。
            var message =
                $"[{InternalName}] 讀不到來源格，不動作：{source}#{slot}" +
                $"（item={(item == null ? "null" : "ItemId=0")}、格位陣列={(containerReady ? "ok" : "未配置")}）";
            if (containerReady && item != null)
                Svc.Log.Debug(message);
            else
                Svc.Log.Information(message);
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

        // 過了去重才印：一次右鍵＝一筆 Debug（每轉一件就一行，量大；轉移失敗另有 Warning／聊天訊息）。
        // 修飾鍵沒按、或同格 500ms 內的重複觸發都已經在上面 return 掉了。
        Svc.Log.Debug(
            $"[{InternalName}] 右鍵轉移觸發：{source}#{slot} itemId={itemId}「{displayName}」");

        // 🔴 僱員方向優先走遊戲自己的道具命令（見 RetainerItemCommandDelegate 的說明）。
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

        // 🔴 鞍袋方向走右鍵選單（見 TryFireContextMenuEntry）——實機驗證過會動的那條。
        // ⚠️ 不要改成 MoveItemSlot：即使帶了 a6: true 也**沒有實機證據**，先測再說。
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

        // 🔴 公會儲物櫃：走 AgentFreeCompanyChest::MoveItemInChest（見 MoveItemInChestDelegate）。
        // ⚠️ 兩條已經排除、不要再回頭嘗試的路：
        //  - `ExecuteCommand(405)`：那是「請求載入置物櫃頁面資料」的前置動作，不是搬移手段。
        //  - 點原生右鍵選單項目：那是 AgentContext 的一般選單，索引基準有兩個互相矛盾的來源，
        //    而**那個選單裡有「丟棄」**，差 1 就是點到隔壁那項。
        var sourceIsChest = Array.IndexOf(FreeCompanyPages, source) >= 0;
        if (sourceIsChest
            || (Array.IndexOf(PlayerBags, source) >= 0 && UiHelper.IsAddonReady("FreeCompanyChest")))
        {
            TransferViaChest(agent, source, slot, baseItemId, displayName, sourceIsChest);
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

        // 🔴 <c>a6: true</c> 絕對不能省。省略＝預設 false＝遊戲只改本機容器、**一個封包都不送**，
        // 畫面上道具會動但伺服器不知道，下次同步就彈回原處（見類別說明）。
        // 這條路徑實務上只有「兵裝庫→背包」走得到（僱員／鞍袋／置物櫃都在上面提早 return），還沒有實機命中的紀錄。
        var result = manager->MoveItemSlot(source, (ushort)slot, destination, (ushort)destinationSlot, a6: true);

        // 立即檢查：MoveItemSlot 會同步更新本機容器（a6 只影響送不送封包，不影響本機同步更新），
        // 所以這一步能擋掉「呼叫當下就沒成功」。
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

        // 🔴 立即檢查通過「不代表真的搬過去了」。本機容器是同步就改好的，
        // 但封包送出後伺服器仍可能拒絕（僱員／公會儲物櫃／鞍袋），道具會彈回原處
        // —— 而我們早就印了「已轉移」。
        // 上面的「已轉移」照樣先印（畫面上道具確實動了，不印反而更困惑），
        // 真的被退回時再補一則錯誤訊息蓋掉它。
        if (NeedsRollbackWatch(source) || NeedsRollbackWatch(destination))
        {
            var startTick = Environment.TickCount64;
            pendingVerifications.Add(new PendingVerification(
                VerificationKind.MoveItemSlotRollback,
                source, slot, destination, destinationSlot, baseItemId, displayName,
                startTick, startTick + RollbackWatchMs,
                0, 0, 1));
        }
    }

    private static void CloseContextMenu(AgentInventoryContext* agent)
    {
        // 兩種選單都要關得掉：道具選單掛在 AgentInventoryContext，
        // 公會儲物櫃那種一般選單掛在 AgentContext。
        // ⚠️ 只問 AgentInventoryContext 要 addon id 的話，一般選單那條路拿到 0 就直接 return，
        // 表現是「道具搬走了但右鍵選單還開著」。
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

        // 判空集中在 UiHelper.GetAddonById（AtkStage.Instance() 與 RaptureAtkUnitManager 兩層都可能 null）。
        var addon = UiHelper.GetAddonById(addonId);
        if (addon == null) return;

        owner->Hide();
        addon->Close(true);
    }
}

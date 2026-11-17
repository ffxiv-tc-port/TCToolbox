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
    /// 走遊戲自己的僱員道具命令。與 MoveItemSlot 不同，這條會真的送到伺服器，
    /// 落點也由遊戲決定（取回進背包空位／寄放進僱員空位）。
    /// </summary>
    private void TransferViaRetainerCommand(
        AgentInventoryContext* agent, InventoryType source, int slot,
        uint itemId, string displayName, bool retrieving)
    {
        if (retainerItemCommand == null)
        {
            if (Throttle.Pass("AutoInventoryTransfer-NoRetainerCmd", 3_000))
                Svc.Chat.PrintError($"[TC Toolbox] 找不到僱員道具命令，「{displayName}」未轉移，請改用手動拖放。");
            return;
        }

        var module = GetAgentRetainerItemCommandModule();
        if (module == 0)
        {
            if (Throttle.Pass("AutoInventoryTransfer-NoRetainerAgent", 3_000))
                Svc.Chat.PrintError($"[TC Toolbox] 僱員視窗未就緒，「{displayName}」未轉移。");
            return;
        }

        var command = retrieving
            ? RetainerItemCommand.RetrieveFromRetainer
            : RetainerItemCommand.EntrustToRetainer;

        Svc.Log.Debug($"[{InternalName}] 僱員命令 {command}：{source}#{slot} itemId={itemId}");
        retainerItemCommand(module, (uint)slot, source, 0, command);

        CloseContextMenu(agent);

        if (Config.NotifyOnTransfer)
            Svc.Chat.Print($"[TC Toolbox] 已{(retrieving ? "取回" : "寄放")}「{displayName}」。");
    }

    /// <summary>
    /// 🔴 鞍袋走遊戲自己的右鍵選單項目。它**不走**僱員道具命令 ——
    /// 所以改成「點玩家自己會點的那個選單項目」。判準用 Addon 表的 row id 去查**客戶端
    /// 自己的字串**，不是寫死翻譯，所以跟語言無關：
    ///   881 = 放入陸行鳥鞍囊 / 887 = 從陸行鳥鞍囊中取回
    /// </summary>
    private const uint AddonRowDepositToSaddlebag = 881;
    private const uint AddonRowRetrieveFromSaddlebag = 887;

    // ⚠️ 公會儲物櫃刻意不走 TryFireContextMenuEntry：它的右鍵選單是 AgentContext 的一般選單，
    // 而那個函式讀的是 AgentInventoryContext 的 EventParams，索引對不上；
    // 而且那個選單裡有「丟棄」，點錯一格的代價太高。置物櫃走 TransferViaChest。

    /// <remarks>
    /// 📌 判斷順序、索引算法與選單傾印診斷都在
    /// <see cref="InventoryContextMenu.TryFireEntry"/>（與「道具快速拆分」共用），
    /// 這裡只剩「哪種失敗印哪一句聊天訊息」——那部分是轉移專用的措辭。
    /// </remarks>
    private bool TryFireContextMenuEntry(AgentInventoryContext* agent, uint addonRowId, string displayName)
    {
        var result = InventoryContextMenu.TryFireEntry(agent, addonRowId, InternalName, out var wanted);

        switch (result)
        {
            case ContextMenuFireResult.Fired:
                return true;

            case ContextMenuFireResult.NotFound:
                if (Throttle.Pass("AutoInventoryTransfer-NoMenuEntry", 3_000))
                    Svc.Chat.PrintError($"[TC Toolbox] 右鍵選單裡找不到「{wanted}」，「{displayName}」未轉移。");
                return false;

            case ContextMenuFireResult.InSubmenu:
                if (Throttle.Pass("AutoInventoryTransfer-Submenu", 3_000))
                    Svc.Chat.PrintError($"[TC Toolbox] 「{wanted}」被收在次選單裡，請改用手動拖放。");
                return false;

            case ContextMenuFireResult.Disabled:
                if (Throttle.Pass("AutoInventoryTransfer-MenuDisabled", 3_000))
                    Svc.Chat.PrintError($"[TC Toolbox] 「{wanted}」目前無法使用，「{displayName}」未轉移。");
                return false;

            case ContextMenuFireResult.Guarded:
                // 同一扇選單實例剛送過、還在關閉中：這一下不送。使用者再點一次即可，不當成失敗。
                if (Throttle.Pass("AutoInventoryTransfer-MenuGuarded", 3_000))
                    Svc.Chat.PrintError($"[TC Toolbox] 剛剛才對同一個右鍵選單送出過，「{displayName}」這一下沒送，請再試一次。");
                return false;

            // LabelUnavailable／AddonUnavailable 只寫 Warning、不對使用者說話（共用端已寫過）。
            default:
                return false;
        }
    }

    /// <summary>
    /// 取公會儲物櫃 agent。取法與遊戲自己的拖放處理常式一致
    /// （GetAgentByInternalId → 當成 MoveItemInChest 的 this）。
    /// </summary>
    private static AgentInterface* ResolveFreeCompanyChestAgent()
    {
        var agentModule = AgentModule.Instance();
        if (agentModule == null) return null;

        var agent = agentModule->GetAgentByInternalId(AgentId.FreeCompanyChest);
        return agent == null || !agent->IsAgentActive() ? null : agent;
    }

    /// <summary>
    /// 用 <c>AgentFreeCompanyChest::MoveItemInChest</c> 搬移。兩個方向的呼叫形狀**不一樣**，
    /// 而且兩種都是照抄遊戲自己的呼叫點，不是自己發明的。
    /// ⚠️ **刻意不安裝 <c>SendInventoryRefresh</c> 的 op-lock hook。**
    /// DailyRoutines 與 FreeCompanyChestHelper 兩邊都有裝，但那個 hook 是**取代原函式**
    /// （detour 裡不呼叫 Original），而我們**無法離線證明**「拿掉它就搬不動」或「裝了它沒有副作用」。
    /// 在還沒有證據以前就去攔截遊戲的庫存刷新，風險比它可能解決的問題大。
    /// </summary>
    private void TransferViaChest(
        AgentInventoryContext* menuAgent, InventoryType source, int slot,
        uint baseItemId, string displayName, bool withdrawing)
    {
        if (moveItemInChest == null)
        {
            if (Throttle.Pass("AutoInventoryTransfer-NoChestFn", 3_000))
                Svc.Chat.PrintError($"[TC Toolbox] 找不到儲物櫃搬移函式，「{displayName}」未轉移，請改用手動拖放。");
            return;
        }

        if (ResolveFreeCompanyChestAgent() == null)
        {
            Svc.Log.Warning($"[{InternalName}] 置物櫃 agent 未就緒，「{displayName}」未轉移。");
            if (Throttle.Pass("AutoInventoryTransfer-NoChestAgent", 3_000))
                Svc.Chat.PrintError($"[TC Toolbox] 公會儲物櫃視窗未就緒，「{displayName}」未轉移。");
            return;
        }

        // menuAgent 只在這一幀有效，排隊路徑帶不過去（跨幀保存原生指標會靜默換人或懸空），
        // 所以選單在分派之前就關掉，兩條路徑的收尾因此一致。
        CloseContextMenu(menuAgent);

        // 一次只讓一筆在途：實機量到同時在途愈多、請求被伺服器丟掉的比例愈高。
        // 佇列不空時一律排隊（佇列是唯一出口），否則不必等的取出會插到等待中的存入前面。
        if (chestQueue.Count > 0 || CountChestDepartures() > 0 || MustSettleBefore(withdrawing))
        {
            EnqueueChestMove(source, slot, baseItemId, displayName, withdrawing);
            return;
        }

        FireChestMove(source, slot, baseItemId, displayName, withdrawing, 1);
    }

    /// <summary>
    /// 真的送出一筆置物櫃搬移。<b>呼叫端要保證此刻沒有其他在途的置物櫃搬移</b>。
    /// 置物櫃 agent 與來源格都在這一幀重查，不吃呼叫端的快取。
    /// <paramref name="queuedSendCount"/>＝這筆累積的點擊次數（最小 1），只進診斷。
    /// </summary>
    private void FireChestMove(
        InventoryType source, int slot, uint baseItemId, string displayName,
        bool withdrawing, int queuedSendCount)
    {
        if (moveItemInChest == null) return;

        var chestAgent = ResolveFreeCompanyChestAgent();
        if (chestAgent == null)
        {
            Svc.Log.Warning($"[{InternalName}] 置物櫃 agent 未就緒，「{displayName}」未轉移。");
            return;
        }

        InventoryType destination;
        int destinationSlot;

        if (withdrawing)
        {
            destination = InventoryType.Invalid;
            destinationSlot = 0;
        }
        else
        {
            if (!TryResolveDestination(source, out var candidates, out var reason))
            {
                if (reason.Length > 0 && Throttle.Pass("AutoInventoryTransfer-NoDest", 3_000))
                    Svc.Chat.Print($"[TC Toolbox] {reason}");
                return;
            }

            var manager = InventoryManager.Instance();
            var containerReady = InventoryScan.TryGetIgnoringLoaded(manager, source, out _);
            var sourceItem = containerReady ? manager->GetInventorySlot(source, slot) : null;

            // 讀不到來源格與「置物櫃沒有位子」是兩種原因，訊息不能共用：
            // 合在一起時使用者看到的一律是「沒有空位」，真正發生的事反而看不見。
            if (manager == null || sourceItem == null)
            {
                Svc.Log.Information(
                    $"[{InternalName}] 讀不到來源格，無法挑目的地：{source}#{slot} " +
                    $"itemId={baseItemId}「{displayName}」（manager={(manager == null ? "null" : "ok")}" +
                    $"、格位陣列={(manager == null ? "未查" : containerReady ? "ok" : "未配置")}）");

                if (Throttle.Pass("AutoInventoryTransfer-NoSourceItem", 3_000))
                    Svc.Chat.PrintError(
                        $"[TC Toolbox] 讀不到「{displayName}」所在的格位（{source} 第 {slot} 格），未轉移。");
                return;
            }

            if (!TryFindTargetSlot(manager, candidates, sourceItem, out destination, out destinationSlot))
            {
                if (Throttle.Pass("AutoInventoryTransfer-Full", 3_000))
                    Svc.Chat.PrintError($"[TC Toolbox] 公會儲物櫃沒有空位也沒有可疊的同款道具，「{displayName}」未轉移。");
                return;
            }
        }

        // 遊戲自己在轉呼叫這顆函式之前也做同一道白名單檢查：
        // 來源或目的地必須是置物櫃分頁，否則就不該走這條路。免費的 fail-closed，照抄。
        if (Array.IndexOf(FreeCompanyPages, source) < 0 &&
            Array.IndexOf(FreeCompanyPages, destination) < 0)
        {
            Svc.Log.Warning($"[{InternalName}] {source} → {destination} 兩邊都不是置物櫃分頁，放棄。");
            return;
        }

        var sourceManager = InventoryManager.Instance();
        if (sourceManager == null ||
            !TryReadSlot(sourceManager, source, slot, out _, out var sourceQuantity, out var sourceFlags))
        {
            Svc.Log.Warning($"[{InternalName}] 來源格 {source}#{slot} 已經讀不到內容，「{displayName}」未轉移。");
            return;
        }

        // 同一格同一件重複點：兩筆觀察記錄盯的是**同一個條件**，分不出來也沒意義。
        // 合併成一筆：起算點留第一次送出（耗時才是真的），期限跟著最新一次送出往後推。
        var now = Environment.TickCount64;
        var startTick = now;
        var sendCount = queuedSendCount;
        for (var i = pendingVerifications.Count - 1; i >= 0; i--)
        {
            var q = pendingVerifications[i];
            if (q.Kind != VerificationKind.ChestDeparture ||
                q.Source != source || q.Slot != slot || q.BaseItemId != baseItemId) continue;

            startTick = q.StartTick;
            sendCount = q.SendCount + queuedSendCount;
            pendingVerifications.RemoveAt(i);
            break;
        }

        Svc.Log.Information(
            $"[{InternalName}] 置物櫃搬移送出（{(withdrawing ? "取出" : "存入")}）：{source}#{slot} → " +
            $"{(withdrawing ? "（落點由遊戲決定）" : $"{destination}#{destinationSlot}")} itemId={baseItemId}×{sourceQuantity} " +
            $"送出次數={sendCount} 在途={CountChestDepartures() + 1}");

        moveItemInChest((nint)chestAgent, source, (uint)slot, destination, (uint)destinationSlot);

        // ⚠️ 這裡**故意不印**「已轉移」。MoveItemInChest 是非同步請求，呼叫當下本機什麼都還沒變，
        // 先報成功等於事情還沒發生就宣告成功。
        // 成功訊息改由 ChestDeparture 觀察器在道具真的離開來源格時才印。
        pendingVerifications.Add(new PendingVerification(
            VerificationKind.ChestDeparture,
            source, slot, destination, destinationSlot, baseItemId, displayName,
            startTick, now + RollbackWatchMs,
            sourceQuantity, sourceFlags, sendCount));
    }

    /// <summary>
    /// 把一筆搬移排進佇列。同一格同一件重複點只累加次數，不排第二筆。
    /// </summary>
    private void EnqueueChestMove(
        InventoryType source, int slot, uint baseItemId, string displayName, bool withdrawing)
    {
        for (var i = 0; i < chestQueue.Count; i++)
        {
            var q = chestQueue[i];
            if (q.Source != source || q.Slot != slot || q.BaseItemId != baseItemId) continue;

            chestQueue[i] = q with { SendCount = q.SendCount + 1 };
            Svc.Log.Information(
                $"[{InternalName}] 置物櫃搬移已在佇列中，累加點擊：{source}#{slot} " +
                $"itemId={baseItemId} 點擊次數={q.SendCount + 1} 佇列={chestQueue.Count}");
            return;
        }

        if (chestQueue.Count >= ChestQueueMax)
        {
            if (Throttle.Pass("AutoInventoryTransfer-ChestQueueFull", 3_000))
                Svc.Chat.PrintError(
                    $"[TC Toolbox] 儲物櫃搬移已排到 {ChestQueueMax} 筆，「{displayName}」沒有排進去，請等前面搬完再試。");
            return;
        }

        chestQueue.Add(new QueuedChestMove(source, slot, baseItemId, displayName, withdrawing, 1));
        Svc.Log.Information(
            $"[{InternalName}] 置物櫃搬移排隊中：{source}#{slot} itemId={baseItemId}「{displayName}」 " +
            $"佇列={chestQueue.Count} 在途={CountChestDepartures()}");
    }

    /// <summary>
    /// 前一筆確認（或逾時）之後才送下一筆；下一筆是存入的話，確認之後還要再等 <see cref="ChestSettleMs"/>。
    /// 送出前重讀來源格：排隊期間使用者可能自己動過那一格，itemId 對不上就跳過，
    /// 免得把後來補進同一格的另一件道具搬走。
    /// </summary>
    private void DrainChestQueue()
    {
        if (chestQueue.Count == 0 || CountChestDepartures() > 0 || MustSettleBefore(chestQueue[0].Withdrawing)) return;

        if (ResolveFreeCompanyChestAgent() == null)
        {
            Svc.Log.Information($"[{InternalName}] 置物櫃已關閉，取消排隊中的 {chestQueue.Count} 筆搬移。");
            chestQueue.Clear();
            return;
        }

        var next = chestQueue[0];
        chestQueue.RemoveAt(0);

        var manager = InventoryManager.Instance();
        if (manager == null ||
            !TryReadSlot(manager, next.Source, next.Slot, out var curItemId, out _, out _) ||
            curItemId != next.BaseItemId)
        {
            Svc.Log.Information(
                $"[{InternalName}] 排隊中的搬移已失效，跳過：{next.Source}#{next.Slot} " +
                $"itemId={next.BaseItemId}「{next.DisplayName}」 現況=" +
                (manager == null ? "讀不到背包" : DescribeSlot(manager, next.Source, next.Slot)));
            return;
        }

        FireChestMove(next.Source, next.Slot, next.BaseItemId, next.DisplayName,
            next.Withdrawing, next.SendCount);
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
        // 刻意不連 IsLoaded 一起擋：它為 false 時 Items 仍可能有效，擋掉會把既有判斷改成「讀不到」。
        if (!InventoryScan.TryGetIgnoringLoaded(manager, type, out _)) return false;

        var item = manager->GetInventorySlot(type, slot);
        return item != null && item->GetBaseItemId() == baseItemId;
    }

    /// <summary>讀出一格的內容。空格（itemId 0）一律回 <c>false</c>。</summary>
    private static bool TryReadSlot(
        InventoryManager* manager, InventoryType type, int slot,
        out uint baseItemId, out int quantity, out uint flags)
    {
        baseItemId = 0;
        quantity = 0;
        flags = 0;

        // 不看 IsLoaded 的理由同 IsItemAt。
        if (!InventoryScan.TryGetIgnoringLoaded(manager, type, out _)) return false;

        var item = manager->GetInventorySlot(type, slot);
        if (item == null || item->ItemId == 0) return false;

        baseItemId = item->GetBaseItemId();
        quantity = item->Quantity;
        flags = (uint)item->Flags;
        return true;
    }

    /// <summary>診斷用：把一格的現況寫成一段可讀的字串。</summary>
    private static string DescribeSlot(InventoryManager* manager, InventoryType type, int slot)
        => TryReadSlot(manager, type, slot, out var id, out var qty, out var flags)
            ? $"{id}×{qty}(flags={flags})"
            : "空格";

    /// <summary>目前還在等置物櫃回應的筆數；大於 0 時新的搬移一律排隊、不直接送。</summary>
    private int CountChestDepartures()
    {
        var n = 0;
        foreach (var p in pendingVerifications)
            if (p.Kind == VerificationKind.ChestDeparture) n++;
        return n;
    }

    /// <summary>依來源容器與目前開著的視窗決定目的地候選（依序嘗試）。</summary>
    private static bool TryResolveDestination(
        InventoryType source, out List<InventoryType> candidates, out string reason)
    {
        candidates = [];
        reason = string.Empty;

        // 僱員／公會儲物櫃／鞍袋／兵裝庫 → 背包
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

            // 🔴 光是判 chest 與長度還不夠：AtkValuesSpan 自己不判 AtkValues 這個欄位，Span 的建構子也不驗指標。
            // addon 拆解時 AtkValues 會先被釋放成 null、AtkValuesCount 卻可能還留著殘值 ⇒ 合法建構出長度非零的 Span，
            // 索引下去才對位址 0 解參考（AVE，try/catch 攔不到）。
            // ⇒ 讀不到就跳過「優先目前分頁」這段，照下面的迴圈把所有分頁都列為候選。
            if (chest != null && chest->AtkValues != null)
            {
                var values = chest->AtkValuesSpan;
                if (values.Length > 2)
                {
                    if (values[1].UInt != 0)
                    {
                        reason = "公會儲物櫃目前在水晶頁，請切到道具頁再轉移。";
                        return false;
                    }

                    var pageIndex = (int)values[2].UInt;
                    if (pageIndex >= 0 && pageIndex < FreeCompanyPages.Length)
                        candidates.Add(FreeCompanyPages[pageIndex]);
                }
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

        reason = "沒有開著可互轉的頁面（僱員、公會儲物櫃或陸行鳥鞍袋）。";
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
                if (!InventoryScan.TryGet(manager, type, out var container)) continue;

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
            if (!InventoryScan.TryGet(manager, type, out var container)) continue;

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
}

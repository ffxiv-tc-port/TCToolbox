using System;
using System.Collections.Generic;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Text;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 背包堆疊合併：把玩家背包（第 1～4 頁）裡同一款道具的零散堆疊併成滿堆。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 <b>純手動觸發</b>：開著模組但不去按按鈕，遊戲行為完全不變。
/// 刻意<b>不</b>掛「開啟背包就自動合併」——上游 CBT 的 <c>AutoMerge</c> 是掛在
/// 背包 addon 開啟事件上的，那等於每次開背包都對伺服器連送一串搬移封包，
/// 而使用者沒有任何辦法知道那一串是誰送的、也沒有辦法中途喊停。
/// </para>
/// <para>
/// 🔴🔴 <c>MoveItemSlot</c> 的第 6 個參數 <c>a6</c> <b>一定要帶 <c>true</c></b>。
/// 省略＝預設 <c>false</c>＝遊戲只改本機容器、<b>一個封包都不送</b>：
/// 畫面上堆疊會漂亮地併起來，但伺服器不知道，關掉背包／換區之後就全部散開。
/// 失敗形式是<b>靜默</b>的，而且要隔一段時間才看得出來。
/// （完整的旗標鑑識紀錄見 <see cref="AutoInventoryTransfer"/> 的型別註解。）
/// </para>
/// <para>
/// ⚠️ 上游那份還有兩個問題，這裡都沒有照抄：
/// <list type="bullet">
/// <item>它在判斷 <c>item-&gt;ItemId == 0</c> <b>之前</b>就拿 ItemId 去查表
/// （<c>Sheet[item-&gt;ItemId].StackSize</c>），空格靠「row 0 的 StackSize 剛好不等於 0」
/// 才沒有炸開——這是巧合不是設計。這裡一律先確認格子有東西再查表。</item>
/// <item>它把整個群組的其他堆疊<b>全部</b>搬向「第一個」堆疊，中間不重讀狀態。
/// 第一個堆疊填滿之後，後面每一次呼叫都是無效搬移，而它不會發現。
/// 這裡改成<b>每一步都重新從活的容器算下一步</b>，滿了就自然換一個目的地。</item>
/// </list>
/// </para>
/// </remarks>
public sealed unsafe class AutoMerge : TcModule
{
    public override string InternalName => "AutoMerge";

    public override string DisplayName => "背包堆疊合併";

    public override string Description =>
        "把背包裡同一款道具的零散堆疊併成滿堆，空出格子。純手動：按下按鈕才會動，" +
        "而且一次只搬一格、可以隨時中止。只處理背包第 1～4 頁，不碰雇員／鞍袋／部隊置物櫃。";

    public override ModuleCategory Category => ModuleCategory.Inventory;

    /// <inheritdoc/>
    /// <remarks>開著不按按鈕＝遊戲行為完全不變，所以是手動觸發。</remarks>
    public override bool IsManualTrigger => true;

    public override bool HasConfigUI => true;

    /// <summary>只處理玩家自己的四頁背包。</summary>
    /// <remarks>
    /// 📌 刻意不含水晶頁：水晶／魔晶石本來就是每種一格、遊戲自己保證不會分裂，沒有東西可合併。
    /// 也不含雇員／鞍袋／部隊置物櫃——那三個容器伺服器有可能拒絕，
    /// 需要的是 <see cref="AutoInventoryTransfer"/> 那一整套退回偵測，不是這裡的單純合併。
    /// </remarks>
    private static readonly InventoryType[] PlayerBags =
        [InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4];

    /// <summary>
    /// 一次合併作業最多搬幾格。
    /// </summary>
    /// <remarks>
    /// 🔴 這是<b>失控保險絲</b>，不是效能調校。正常情況下背包 140 格全滿也用不到一半，
    /// 真的撞到上限代表「搬了但狀態沒變」的情況我們沒偵測到，寧可停下來讓使用者再按一次。
    /// </remarks>
    private const int MaxMovesPerRun = 400;

    /// <summary>
    /// 連續幾次「搬了但兩邊數量都沒變」就中止。
    /// </summary>
    /// <remarks>
    /// <c>MoveItemSlot</c> 會<b>同步</b>更新本機容器，所以一次成功的合併一定看得到數量變化。
    /// 完全沒變代表遊戲拒絕了這次搬移（而它的回傳值不見得會說），
    /// 再叫下去只會用同一組來源／目的地無限重試。
    /// </remarks>
    private const int StuckAbortThreshold = 3;

    private AutoMergeConfig Config => Plugin.Instance.Config.Merge;

    private bool running;
    private int movesDone;
    private int stuckCount;
    private long runStartTick;

    /// <summary>這一趟合併過程中被動過的道具名稱（去重後用於結束時的報告）。</summary>
    private readonly List<string> touchedItems = [];

    /// <summary>上一次結束時的摘要，顯示在設定畫面上。</summary>
    private string lastSummary = string.Empty;

    protected override void OnEnable()
    {
        ResetRun();
        Svc.Framework.Update += OnFrameworkUpdate;
    }

    protected override void OnDisable()
    {
        Svc.Framework.Update -= OnFrameworkUpdate;
        ResetRun();
        lastSummary = string.Empty;
    }

    private void ResetRun()
    {
        running = false;
        movesDone = 0;
        stuckCount = 0;
        runStartTick = 0;
        touchedItems.Clear();
    }

    /// <summary>
    /// 一次合併搬移的完整描述。
    /// </summary>
    /// <remarks>
    /// 🔴 只存容器與格號這種<b>純數值</b>，不存 <c>InventoryItem*</c>。
    /// 指標跨幀就可能失效，而這裡每一步之間都會過一個 framework tick。
    /// </remarks>
    private readonly record struct MergeStep(
        InventoryType Source, ushort SourceSlot, int SourceQuantity,
        InventoryType Destination, ushort DestinationSlot, int DestinationQuantity,
        uint BaseItemId, bool HighQuality, string DisplayName);

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (!running) return;

        // 每一步之間留間隔：合併是一連串真的會送出去的搬移封包，
        // 塞太快沒有好處（使用者也看不清楚發生了什麼）。
        if (!Throttle.Pass("AutoMerge-Step", Math.Max(50, Config.StepIntervalMs))) return;

        if (GetBlockedReason() is { } reason)
        {
            FinishRun($"已中止：{reason}");
            return;
        }

        if (movesDone >= MaxMovesPerRun)
        {
            FinishRun($"已達單次上限（{MaxMovesPerRun} 次搬移）而停止，需要的話請再按一次。");
            return;
        }

        var manager = InventoryManager.Instance();
        if (manager == null)
        {
            FinishRun("已中止：讀不到背包資料。");
            return;
        }

        if (!TryFindNextMerge(manager, out var step))
        {
            FinishRun(movesDone == 0
                ? "背包裡沒有可以合併的零散堆疊。"
                : $"完成：合併了 {movesDone} 次。");
            return;
        }

        ExecuteStep(manager, step);
    }

    private void ExecuteStep(InventoryManager* manager, in MergeStep step)
    {
        // 🔴 a6: true 絕對不能省 —— 見型別註解。省略＝只改本機、關背包就散開。
        var result = manager->MoveItemSlot(
            step.Source, step.SourceSlot, step.Destination, step.DestinationSlot, a6: true);

        // MoveItemSlot 同步更新本機容器，所以成功的話這裡立刻看得到數量變化。
        var newSource = ReadQuantity(manager, step.Source, step.SourceSlot, step.BaseItemId, step.HighQuality);
        var newDestination =
            ReadQuantity(manager, step.Destination, step.DestinationSlot, step.BaseItemId, step.HighQuality);

        var changed = newSource != step.SourceQuantity || newDestination != step.DestinationQuantity;

        if (!changed)
        {
            stuckCount++;
            Svc.Log.Warning(
                $"[{InternalName}] 搬移沒有造成任何變化（第 {stuckCount} 次）：" +
                $"{step.Source}#{step.SourceSlot}({step.SourceQuantity}) → " +
                $"{step.Destination}#{step.DestinationSlot}({step.DestinationQuantity}) " +
                $"itemId={step.BaseItemId} 回傳={result}");

            if (stuckCount >= StuckAbortThreshold)
                FinishRun($"已中止：連續 {StuckAbortThreshold} 次搬移沒有生效（遊戲拒絕了合併）。");

            return;
        }

        stuckCount = 0;
        movesDone++;

        if (!touchedItems.Contains(step.DisplayName))
            touchedItems.Add(step.DisplayName);

        Svc.Log.Debug(
            $"[{InternalName}] {step.Source}#{step.SourceSlot} → {step.Destination}#{step.DestinationSlot} " +
            $"「{step.DisplayName}」 {step.SourceQuantity}+{step.DestinationQuantity} → " +
            $"{newSource}+{newDestination} 回傳={result}");
    }

    /// <summary>
    /// 從<b>活的</b>容器算出下一步要搬哪一格。
    /// </summary>
    /// <remarks>
    /// 每一步都重算而不是一次排好整份計畫，理由是合併的結果會改變後續的最佳解：
    /// 目的地填滿之後就該換下一個目的地，而預先排好的計畫看不到這件事
    /// （上游那份就是這樣，填滿之後剩下的呼叫全是無效搬移）。
    /// 重算的成本是掃 140 格，一步一次、間隔數百毫秒，完全不是瓶頸。
    /// </remarks>
    private bool TryFindNextMerge(InventoryManager* manager, out MergeStep step)
    {
        step = default;

        // 群組鍵＝(道具, 是否優質)。優質與普通品是兩種不同的堆疊，不能互相合併。
        var groups = new Dictionary<(uint ItemId, bool HighQuality), List<(InventoryType Container, ushort Slot, int Quantity)>>();

        // 用一份獨立的清單維持穩定的走訪順序（Dictionary 的列舉順序不保證）。
        // 順序穩定 → 同樣的背包狀態每次都挑到同一步，出事時可重現。
        var groupOrder = new List<(uint ItemId, bool HighQuality)>();

        foreach (var bag in PlayerBags)
        {
            var container = manager->GetInventoryContainer(bag);
            if (container == null || !container->IsLoaded) continue;

            for (var i = 0; i < container->Size; i++)
            {
                var item = container->GetInventorySlot(i);

                // 🔴 先確認這一格真的有東西，才可以拿 ItemId 去查表（上游把順序寫反了）。
                if (item == null || item->ItemId == 0) continue;

                // 蒐藏品各自帶有蒐集價值，兩份的價值不同就不是同一種東西，遊戲也不讓它們疊。
                if ((item->Flags & InventoryItem.ItemFlags.Collectable) != 0) continue;

                var baseItemId = item->GetBaseItemId();
                if (baseItemId == 0) continue;

                var stackSize = GetStackSize(baseItemId);
                if (stackSize <= 1) continue;

                var quantity = item->Quantity;

                // 已經滿堆的格子兩個角色都當不了：它不能當來源（搬走等於製造新的碎片），
                // 也不能當目的地（塞不進去）。
                if (quantity <= 0 || quantity >= stackSize) continue;

                var highQuality = (item->Flags & InventoryItem.ItemFlags.HighQuality) != 0;
                var key = (baseItemId, highQuality);

                if (!groups.TryGetValue(key, out var list))
                {
                    list = [];
                    groups[key] = list;
                    groupOrder.Add(key);
                }

                list.Add((bag, (ushort)i, quantity));
            }
        }

        foreach (var key in groupOrder)
        {
            var list = groups[key];
            if (list.Count < 2) continue;

            // 目的地挑<b>最多</b>的那一堆、來源挑<b>最少</b>的那一堆：
            // 這樣每一次搬移都盡可能清掉一整格，總搬移次數最少。
            var destIndex = 0;
            var srcIndex = 0;
            for (var i = 1; i < list.Count; i++)
            {
                if (list[i].Quantity > list[destIndex].Quantity) destIndex = i;
                if (list[i].Quantity < list[srcIndex].Quantity) srcIndex = i;
            }

            // 全部一樣多時上面兩個會指到同一格，改挑任一個不同的格子。
            if (destIndex == srcIndex)
                srcIndex = destIndex == 0 ? 1 : 0;

            var dest = list[destIndex];
            var src = list[srcIndex];

            step = new MergeStep(
                src.Container, src.Slot, src.Quantity,
                dest.Container, dest.Slot, dest.Quantity,
                key.ItemId, key.HighQuality, ResolveItemName(key.ItemId, key.HighQuality));
            return true;
        }

        return false;
    }

    /// <summary>讀某一格目前的數量；那一格已經不是這個道具就回 0。</summary>
    private static int ReadQuantity(
        InventoryManager* manager, InventoryType container, ushort slot, uint baseItemId, bool highQuality)
    {
        var item = manager->GetInventorySlot(container, slot);
        if (item == null || item->ItemId == 0) return 0;
        if (item->GetBaseItemId() != baseItemId) return 0;
        if (((item->Flags & InventoryItem.ItemFlags.HighQuality) != 0) != highQuality) return 0;
        return item->Quantity;
    }

    private static int GetStackSize(uint baseItemId)
    {
        var row = Svc.Data.GetExcelSheet<Item>().GetRowOrDefault(baseItemId);
        return row == null ? 0 : (int)row.Value.StackSize;
    }

    /// <summary>道具名稱一律走 Lumina Item 表（台服自帶繁中），不讀 addon 上的文字。</summary>
    private static string ResolveItemName(uint baseItemId, bool highQuality)
    {
        var row = Svc.Data.GetExcelSheet<Item>().GetRowOrDefault(baseItemId);
        var name = row?.Name.ExtractText() ?? string.Empty;

        // Item 表的 row 0 是有效列但名稱為空，所以不能只判斷 null。
        if (string.IsNullOrEmpty(name))
            return $"#{baseItemId}";

        return highQuality ? $"{name} {(char)SeIconChar.HighQuality}" : name;
    }

    /// <summary>
    /// 現在不能合併的原因；<c>null</c>＝可以。
    /// </summary>
    /// <remarks>
    /// 📌 這些狀態下背包本身是鎖住或正在同步的，硬搬只會得到一串被伺服器忽略的請求。
    /// 開始前檢查一次，每一步之間也再檢查一次——使用者可能按下按鈕之後就走進副本傳送點。
    /// </remarks>
    private static string? GetBlockedReason()
    {
        if (Svc.Objects.LocalPlayer == null)
            return "目前不在遊戲中。";

        if (Svc.Condition[ConditionFlag.BetweenAreas] || Svc.Condition[ConditionFlag.BetweenAreas51])
            return "正在讀取地圖。";

        if (Svc.Condition[ConditionFlag.WatchingCutscene] ||
            Svc.Condition[ConditionFlag.OccupiedInCutSceneEvent])
            return "正在播放過場動畫。";

        if (Svc.Condition[ConditionFlag.OccupiedSummoningBell])
            return "正在使用傳喚鈴（背包這時候由雇員視窗接管）。";

        return null;
    }

    public override void DrawConfig()
    {
        var blockedReason = GetBlockedReason();

        if (running)
        {
            if (ImGui.Button("停止合併"))
                FinishRun($"已由使用者停止（已完成 {movesDone} 次搬移）。");

            ImGui.SameLine();
            ImGui.TextColored(new System.Numerics.Vector4(1f, 0.85f, 0.35f, 1f),
                $"合併中… 已搬移 {movesDone} 次");
        }
        else
        {
            var blocked = blockedReason != null;
            if (blocked) ImGui.BeginDisabled();

            var clicked = ImGui.Button("開始合併背包堆疊");

            if (blocked) ImGui.EndDisabled();

            // ⚠️ 停用中的項目預設不回報 hover，要 AllowWhenDisabled 才問得到，
            //    否則「按鈕灰掉又沒有說明」就是純粹的靜默失敗。
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            {
                ImGui.SetTooltip(blockedReason ??
                    "掃描背包第 1～4 頁，把同一款道具的零散堆疊併起來。\n" +
                    "一次搬一格，過程中可以隨時按「停止合併」。");
            }

            if (clicked && !blocked)
                StartRun();
        }

        ImGui.Separator();

        var interval = Config.StepIntervalMs;
        if (ImGui.SliderInt("每次搬移的間隔（毫秒）##autoMergeInterval", ref interval, 100, 1_000))
        {
            Config.StepIntervalMs = interval;
            Plugin.Instance.Config.Save();
        }

        var notify = Config.NotifyOnFinish;
        if (ImGui.Checkbox("結束時在聊天欄報告##autoMergeNotify", ref notify))
        {
            Config.NotifyOnFinish = notify;
            Plugin.Instance.Config.Save();
        }

        if (lastSummary.Length > 0)
            ImGui.TextDisabled($"上次結果：{lastSummary}");

        ImGui.TextDisabled("只處理背包第 1～4 頁；蒐藏品與不可疊放的道具會跳過。");
    }

    private void StartRun()
    {
        ResetRun();
        running = true;
        runStartTick = Environment.TickCount64;
        lastSummary = string.Empty;

        // 讓第一步立刻執行，不必先等一個間隔。
        Throttle.Reset("AutoMerge-Step");

        // 使用者回報用的定錨點：這一行是唯一能證明「這串搬移是他自己按的」的證據。
        Svc.Log.Information($"[{InternalName}] 使用者手動開始合併背包堆疊。");
    }

    private void FinishRun(string summary)
    {
        var elapsed = runStartTick == 0 ? 0 : Environment.TickCount64 - runStartTick;
        var items = touchedItems.Count == 0
            ? string.Empty
            : $"（{string.Join("、", touchedItems.Count <= 6 ? touchedItems : touchedItems.GetRange(0, 6))}" +
              $"{(touchedItems.Count > 6 ? $" 等 {touchedItems.Count} 款" : string.Empty)}）";

        lastSummary = summary;

        Svc.Log.Information(
            $"[{InternalName}] {summary} 搬移 {movesDone} 次、耗時 {elapsed}ms{items}");

        if (Config.NotifyOnFinish && movesDone > 0)
            Svc.Chat.Print($"[TC Toolbox] 背包堆疊合併：{summary}{items}");
        else if (Config.NotifyOnFinish)
            Svc.Chat.Print($"[TC Toolbox] 背包堆疊合併：{summary}");

        ResetRun();
    }
}

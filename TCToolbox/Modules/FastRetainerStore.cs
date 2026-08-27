using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 雇員存取加速：在背包／雇員背包的道具右鍵選單補上「同款一次全部寄放／取回」。
/// 走遊戲自己的雇員道具命令（<see cref="RetainerItemTransfer"/>），一次一格、逐格送到伺服器。
/// 零封包偽造、零記憶體 patch。參考 DailyRoutines <c>FastRetainerStore</c> 重寫。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 <b>與 DR 原版最大的差異：不用 <c>MoveItemSlot</c>。</b>DR 是對雇員頁呼叫
/// <c>MoveItemSlot(a6: true)</c>，但那條在台服<b>沒有實機證據</b>、而且對雇員的已知失敗形式是
/// 「假成功、伺服器退回」（見 <see cref="RetainerItemTransfer"/> 的說明）。這裡改走
/// AutoInventoryTransfer 實機驗證過的雇員道具命令。
/// </para>
/// <para>
/// 🔴 <b>fail-closed</b>：特徵碼解析不到（<see cref="RetainerItemTransfer.IsAvailable"/> 為
/// <c>false</c>）就<b>不加</b>選單項，也不會靜默無效地假裝搬了。
/// </para>
/// <para>
/// 📌 觸發方式是右鍵選單項目，<b>開著不去點就完全不動</b>——因此標記為手動觸發。
/// </para>
/// </remarks>
public sealed unsafe class FastRetainerStore : TcModule
{
    public override string InternalName => "FastRetainerStore";

    public override string DisplayName => "雇員存取加速（同款全部）";

    public override string Description =>
        "同時開著背包與雇員背包時，對道具按右鍵會多出「同款一次全部寄放／取回」。" +
        "走遊戲自己的雇員道具命令逐格送到伺服器（不是只改本機、也不會被退回）。開著不點就不會動。";

    public override ModuleCategory Category => ModuleCategory.Inventory;

    /// <inheritdoc/>
    /// <remarks>開著不點右鍵選單項＝遊戲行為完全不變。</remarks>
    public override bool IsManualTrigger => true;

    public override bool HasConfigUI => true;

    /// <summary>玩家背包的 addon 名（右鍵這些視窗＝寄放到雇員）。</summary>
    private static readonly HashSet<string> PlayerAddonNames =
        new(StringComparer.Ordinal) { "Inventory", "InventoryLarge", "InventoryExpansion" };

    /// <summary>雇員背包的 addon 名（右鍵這些視窗＝從雇員取回）。</summary>
    private static readonly HashSet<string> RetainerAddonNames =
        new(StringComparer.Ordinal) { "InventoryRetainer", "InventoryRetainerLarge" };

    /// <summary>失控保險絲：一次最多搬幾格（雇員頁 7×35 也遠小於此）。</summary>
    private const int MaxMovesPerRun = 400;

    private readonly TaskQueue queue = new() { DefaultTimeoutMs = 15_000 };

    private FastRetainerStoreConfig Config => Plugin.Instance.Config.FastRetainerStore;

    private bool running;
    private bool runToRetainer;
    private int movesDone;
    private int plannedMoves;
    private string lastSummary = string.Empty;

    protected override void OnEnable()
    {
        queue.OnTimeout = step => FinishRun($"逾時中止：{step}");
        Svc.ContextMenu.OnMenuOpened += OnMenuOpened;
        Svc.Framework.Update += OnFrameworkUpdate;
    }

    protected override void OnDisable()
    {
        Svc.ContextMenu.OnMenuOpened -= OnMenuOpened;
        Svc.Framework.Update -= OnFrameworkUpdate;
        queue.Abort();
        running = false;
        lastSummary = string.Empty;
    }

    private void OnFrameworkUpdate(IFramework framework) => queue.Tick();

    private void OnMenuOpened(IMenuOpenedArgs args)
    {
        try
        {
            if (args.MenuType != ContextMenuType.Inventory) return;
            if (args.Target is not MenuTargetInventory { TargetItem: { } targetItem }) return;

            var addonName = args.AddonName ?? string.Empty;

            bool toRetainer;
            if (PlayerAddonNames.Contains(addonName)) toRetainer = true;
            else if (RetainerAddonNames.Contains(addonName)) toRetainer = false;
            else return;

            // 特徵碼解析不到就不加（fail-closed，不假裝可用）。
            if (!RetainerItemTransfer.IsAvailable) return;

            // 兩邊視窗都要開著、雇員也要就緒，否則命令沒有意義。
            if (!IsPlayerInventoryOpen() || !IsRetainerInventoryOpen()) return;
            if (!RetainerItemTransfer.IsRetainerReady()) return;

            var baseId = targetItem.BaseItemId;
            var isHq = targetItem.IsHq;
            var isCollectable = targetItem.IsCollectable;

            // 目的地容器裡真的能放才提供（有同款可疊、或有空位）。避免加了選項按下去卻沒空間。
            var sourceBags = toRetainer ? RetainerItemTransfer.PlayerBags : RetainerItemTransfer.RetainerBags;
            var targetBags = toRetainer ? RetainerItemTransfer.RetainerBags : RetainerItemTransfer.PlayerBags;

            var matchingSource = CountMatching(sourceBags, baseId, isHq, isCollectable);
            if (matchingSource == 0) return;
            if (!HasRoom(targetBags, baseId, isHq, isCollectable)) return;

            args.AddMenuItem(new MenuItem
            {
                Name = toRetainer ? "同款一次全部寄放雇員" : "同款一次全部取回背包",
                PrefixChar = 'T',
                PrefixColor = 539,
                Priority = 0,
                OnClicked = _ => StartRun(baseId, isHq, isCollectable, toRetainer),
            });
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, $"[{InternalName}] 建立右鍵選單項目時發生例外（addon: {args.AddonName}）");
        }
    }

    private static bool IsPlayerInventoryOpen() =>
        UiHelper.IsAddonReady("Inventory") || UiHelper.IsAddonReady("InventoryLarge") ||
        UiHelper.IsAddonReady("InventoryExpansion");

    private static bool IsRetainerInventoryOpen() =>
        UiHelper.IsAddonReady("InventoryRetainer") || UiHelper.IsAddonReady("InventoryRetainerLarge");

    /// <summary>DR 的 IsSameItem：正規化雇員／背包格的實際 id 後，與目標的（base、HQ、收藏品）比對。</summary>
    private static bool IsSameItem(InventoryItem* slot, uint baseId, bool isHq, bool isCollectable)
    {
        var rawId = slot->GetItemId();
        if (rawId == 0) return false;

        var slotHq = (slot->Flags & InventoryItem.ItemFlags.HighQuality) != 0;
        var slotCollectable = (slot->Flags & InventoryItem.ItemFlags.Collectable) != 0;

        var slotBase = rawId;
        if (slotCollectable) slotBase %= 500_000;
        else if (slotHq) slotBase %= 1_000_000;

        return slotBase == baseId && slotHq == isHq && slotCollectable == isCollectable;
    }

    private static int CountMatching(InventoryType[] bags, uint baseId, bool isHq, bool isCollectable)
    {
        var manager = InventoryManager.Instance();
        if (manager == null) return 0;

        var count = 0;
        foreach (var bag in bags)
        {
            var container = manager->GetInventoryContainer(bag);
            // 🔴 判的是 Items 不是 GetInventorySlot 的回傳值：Items 為 null 而 Size > 0 時，
            //    GetInventorySlot 回的是「null + 偏移」這種非 null 的假指標，下面的判空一定通過，
            //    解參考就是攔不到的 AVE（corrupted-state exception，try/catch 無效）。
            //    樣板同 DiscardList.ScanMatches／TriadCardRecycle 的背包掃描。
            if (container == null || !container->IsLoaded || container->Items == null) continue;

            for (var i = 0; i < container->Size; i++)
            {
                var item = container->GetInventorySlot(i);
                if (item != null && IsSameItem(item, baseId, isHq, isCollectable)) count++;
            }
        }

        return count;
    }

    /// <summary>目的地容器裡有沒有可疊的同款（未滿）或空格。</summary>
    private static bool HasRoom(InventoryType[] bags, uint baseId, bool isHq, bool isCollectable)
    {
        var manager = InventoryManager.Instance();
        if (manager == null) return false;

        var itemRow = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Item>().GetRowOrDefault(baseId);
        var stackSize = itemRow?.StackSize ?? 1;

        foreach (var bag in bags)
        {
            var container = manager->GetInventoryContainer(bag);
            // 🔴 Items 為 null 而 Size > 0 時，GetInventorySlot 回的是非 null 的假指標（理由同本檔上一處）。
            if (container == null || !container->IsLoaded || container->Items == null) continue;

            for (var i = 0; i < container->Size; i++)
            {
                var item = container->GetInventorySlot(i);
                if (item == null || item->GetItemId() == 0) return true; // 空格
                if (IsSameItem(item, baseId, isHq, isCollectable) && item->Quantity < stackSize) return true;
            }
        }

        return false;
    }

    /// <summary>快照目前來源容器裡符合的（容器, 格號）。容器不會在搬移中重排，所以快照後逐格送是安全的。</summary>
    private static List<(InventoryType Bag, int Slot)> SnapshotMatching(
        InventoryType[] bags, uint baseId, bool isHq, bool isCollectable)
    {
        var result = new List<(InventoryType, int)>();
        var manager = InventoryManager.Instance();
        if (manager == null) return result;

        foreach (var bag in bags)
        {
            var container = manager->GetInventoryContainer(bag);
            // 🔴 Items 為 null 而 Size > 0 時，GetInventorySlot 回的是非 null 的假指標（理由同本檔上一處）。
            if (container == null || !container->IsLoaded || container->Items == null) continue;

            for (var i = 0; i < container->Size; i++)
            {
                var item = container->GetInventorySlot(i);
                if (item != null && IsSameItem(item, baseId, isHq, isCollectable))
                    result.Add((bag, item->Slot));
            }
        }

        return result;
    }

    private void StartRun(uint baseId, bool isHq, bool isCollectable, bool toRetainer)
    {
        if (running || queue.IsBusy) return;

        var sourceBags = toRetainer ? RetainerItemTransfer.PlayerBags : RetainerItemTransfer.RetainerBags;
        var slots = SnapshotMatching(sourceBags, baseId, isHq, isCollectable);
        if (slots.Count == 0)
        {
            Svc.Chat.Print("[TC Toolbox] 沒有可搬移的道具。");
            return;
        }

        if (slots.Count > MaxMovesPerRun)
            slots = slots.GetRange(0, MaxMovesPerRun);

        running = true;
        runToRetainer = toRetainer;
        movesDone = 0;
        plannedMoves = slots.Count;
        lastSummary = string.Empty;

        var itemName = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Item>()
            .GetRowOrDefault(baseId)?.Name.ExtractText() ?? $"#{baseId}";

        Svc.Log.Information(
            $"[{InternalName}] 使用者手動開始{(toRetainer ? "寄放" : "取回")}「{itemName}」，共 {plannedMoves} 格。");

        var interval = Math.Max(100, Config.StepIntervalMs);
        var throttleKey = $"FastRetainerStore-Step";
        Throttle.Reset(throttleKey);

        foreach (var (bag, slot) in slots)
        {
            queue.Enqueue($"{(toRetainer ? "寄放" : "取回")} {bag}#{slot}", () =>
            {
                if (!IsPlayerInventoryOpen() || !IsRetainerInventoryOpen())
                {
                    FinishRun("已中止：背包或雇員視窗已關閉。");
                    return null;
                }

                if (!Throttle.Pass(throttleKey, interval)) return false;

                if (!RetainerItemTransfer.Move(bag, slot, toRetainer))
                {
                    FinishRun("已中止：雇員道具命令目前不可用。");
                    return null;
                }

                movesDone++;
                return true;
            }, 15_000);
        }

        queue.Enqueue("完成", () =>
        {
            FinishRun($"完成：已送出 {movesDone}/{plannedMoves} 格{(runToRetainer ? "寄放" : "取回")}命令。");
            return true;
        });
    }

    private void FinishRun(string summary)
    {
        lastSummary = summary;
        running = false;
        queue.Abort();

        Svc.Log.Information($"[{InternalName}] {summary}");
        if (Config.NotifyOnFinish)
            Svc.Chat.Print($"[TC Toolbox] 雇員存取：{summary}");
    }

    public override void DrawConfig()
    {
        if (!RetainerItemTransfer.IsAvailable)
        {
            ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f),
                "⚠ 雇員道具命令特徵碼解析失敗，本功能無法使用（台服改版後可能失效，請回報）。");
        }
        else
        {
            ImGui.TextDisabled("同時開著背包與雇員背包時，對道具按右鍵即可看到選項。");
        }

        if (running)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(1f, 0.85f, 0.35f, 1f), $"搬移中… {movesDone}/{plannedMoves}");
        }

        ImGui.Separator();

        var interval = Config.StepIntervalMs;
        if (ImGui.SliderInt("每格之間的間隔（毫秒）##fastRetainerStore", ref interval, 100, 1_000))
        {
            Config.StepIntervalMs = interval;
            Plugin.Instance.Config.Save();
        }

        var notify = Config.NotifyOnFinish;
        if (ImGui.Checkbox("結束時在聊天欄報告##fastRetainerStore", ref notify))
        {
            Config.NotifyOnFinish = notify;
            Plugin.Instance.Config.Save();
        }

        if (lastSummary.Length > 0)
            ImGui.TextDisabled($"上次結果：{lastSummary}");
    }
}

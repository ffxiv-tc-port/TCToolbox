using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 兵裝庫非套裝裝備取回背包：把兵裝庫裡「沒有被任何一套套裝用到」的裝備搬回背包，方便一次處理掉。
/// 手動按鈕，一次一件，不自動執行。
///
/// 🔴🔴 <b><c>MoveItemSlot</c> 的第 6 個參數 <c>a6</c> 才是「送不送封包」的總開關。</b>
/// <c>a6 == false</c>（**預設值，省略不寫就是它**）→ 遊戲只更新本機容器並刷新 UI，
/// **一個封包都不送**；畫面上道具會動，但伺服器不知道，下次同步就彈回原處。
/// 所以這裡一定要顯式寫 <c>a6: true</c>。
/// 這個結論是 2026-08-02 對台服 7.20 二進位鑑識定案的，細節寫在
/// <see cref="AutoInventoryTransfer"/> 的型別註解裡，不要在這裡重複判斷。
///
/// 🔴 <b>不事先算好一整串來源格再逐一搬。</b>每一輪重新掃兵裝庫、重新確認那一格還是同一件才動手 ——
/// 兵裝庫在道具離開後會不會把後面的往前遞補，我們無法離線證明，
/// 而若會遞補，事先算好的格號從第二件起就指向別的裝備，且那個錯法是靜默的。
///
/// 參考 DailyRoutines <c>AutoMoveGearsNotInSet</c> 的用途重寫（API13、無 OmenTools／KamiToolKit 相依）。
/// DR 是把按鈕掛到兵裝庫原生視窗上並用聊天指令觸發；這裡改成設定面板按鈕，不動原生 addon。
/// </summary>
public sealed unsafe class MoveGearsNotInSet : TcModule
{
    public override string InternalName => "MoveGearsNotInSet";
    public override string DisplayName => "兵裝庫：非套裝裝備取回背包";

    public override string Description =>
        "手動按鈕：把兵裝庫裡沒有被任何一套套裝（裝備套組）用到的裝備搬回背包，方便一次賣掉或分解。" +
        "背包空位不足時會先停下並提示，不會搬到一半卡住。不會自動執行。";

    public override ModuleCategory Category => ModuleCategory.Inventory;

    public override bool HasConfigUI => true;

    /// <summary>
    /// 12 個兵裝庫容器。⚠️ 不含 <c>EquippedItems</c> —— 身上穿著的不在處理範圍內。
    /// </summary>
    private static readonly InventoryType[] ArmoryContainers =
    [
        InventoryType.ArmoryMainHand, InventoryType.ArmoryOffHand, InventoryType.ArmoryHead,
        InventoryType.ArmoryBody, InventoryType.ArmoryHands, InventoryType.ArmoryWaist,
        InventoryType.ArmoryLegs, InventoryType.ArmoryFeets, InventoryType.ArmoryEar,
        InventoryType.ArmoryNeck, InventoryType.ArmoryWrist, InventoryType.ArmoryRings,
    ];

    private static readonly InventoryType[] PlayerBags =
    [
        InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4,
    ];

    private readonly TaskQueue queue = new() { DefaultTimeoutMs = 15_000 };

    /// <summary>本輪搬不動的道具（含 HQ 編碼），避免對同一件無限重試。</summary>
    private readonly HashSet<uint> failedItems = [];

    private int movedCount;

    /// <summary>
    /// 一次執行的回合上限（保險絲，不是業務規則）。兵裝庫每個容器 50 格 × 12 遠低於這個數。
    /// TaskQueue 的逾時只管單一步驟，管不到「每步都很快但永遠跑不完」。
    /// </summary>
    private const int MaxIterations = 1_000;

    private int iterations;

    private int? previewCandidates;
    private int previewEmptySlots;
    private string previewReason = string.Empty;

    private MoveGearsNotInSetConfig Config => Plugin.Instance.Config.MoveGearsNotInSet;

    protected override void OnEnable()
    {
        queue.OnTimeout = step =>
        {
            Svc.Log.Information($"[{InternalName}] 流程在「{step}」逾時中止，本輪已搬移 {movedCount} 件。");
            Svc.Chat.PrintError($"[TC Toolbox] 兵裝庫取回逾時已停止（本輪已搬移 {movedCount} 件）。");
        };

        Svc.Framework.Update += OnUpdate;
    }

    protected override void OnDisable()
    {
        Svc.Framework.Update -= OnUpdate;
        queue.Abort();
        failedItems.Clear();
        movedCount = 0;
        previewCandidates = null;
        previewReason = string.Empty;
    }

    private void OnUpdate(IFramework _) => queue.Tick();

    // ──────────────────────────── 資料 ────────────────────────────

    /// <summary>
    /// 收集所有套裝用到的道具 ID。
    ///
    /// <para>⚠️ 套裝裡的 <c>ItemId</c> 對 HQ 用的是 <c>+1000000</c> 編碼，
    /// 而 <c>InventoryItem.ItemId</c> **不含**那個編碼（HQ 在 <c>Flags</c> 裡）。
    /// 所以比對時要自己把兵裝庫那一側加回去，不能直接比。</para>
    ///
    /// <para>🔑 刻意**不**用 <c>GearsetFlag.Exists</c> 過濾沒建立的套裝欄位：
    /// 萬一那個旗標判斷有誤，過濾掉的會是「其實有效的套裝」→ 它的裝備被當成沒人用而搬走。
    /// 不過濾的話最壞情況只是「有些裝備被殘留資料保護住、沒被搬走」——
    /// 這個方向的錯誤使用者按第二次就好，反方向要重新裝備一輪。</para>
    /// </summary>
    private HashSet<uint> BuildProtectedItemIds(out int gearsetItemCount)
    {
        var protectedIds = new HashSet<uint>();
        gearsetItemCount = 0;

        var module = RaptureGearsetModule.Instance();
        if (module == null) return protectedIds;

        var entries = module->Entries;
        for (var i = 0; i < entries.Length; i++)
        {
            ref var entry = ref entries[i];
            var items = entry.Items;

            for (var j = 0; j < items.Length; j++)
            {
                var itemId = items[j].ItemId;
                if (itemId != 0)
                {
                    protectedIds.Add(itemId);
                    gearsetItemCount++;
                }

                // 套裝的「投影來源」通常放在投影台，不在兵裝庫；但真的有實體在兵裝庫時
                // 搬走會讓那套的外觀失效。多保護一層，錯的方向是「少搬」。
                if (Config.ProtectGlamourSources)
                {
                    var glamourId = items[j].GlamourId;
                    if (glamourId != 0) protectedIds.Add(glamourId);
                }
            }
        }

        return protectedIds;
    }

    /// <summary>兵裝庫某一格對套裝而言的識別碼（HQ 補回 <c>+1000000</c>）。</summary>
    private static uint GearsetKey(InventoryItem* item) =>
        (item->Flags & InventoryItem.ItemFlags.HighQuality) != 0
            ? item->ItemId + 1_000_000
            : item->ItemId;

    /// <summary>掃兵裝庫，回傳第一件不在套裝裡的裝備。<paramref name="total"/> 是候選總數。</summary>
    private bool TryFindCandidate(
        InventoryManager* manager, HashSet<uint> protectedIds,
        out InventoryType container, out int slot, out uint key, out string name, out int total)
    {
        container = InventoryType.Invalid;
        slot = -1;
        key = 0;
        name = string.Empty;
        total = 0;

        var found = false;

        foreach (var type in ArmoryContainers)
        {
            var inventory = manager->GetInventoryContainer(type);
            if (inventory == null || !inventory->IsLoaded) continue;

            for (var i = 0; i < inventory->Size; i++)
            {
                var item = inventory->GetInventorySlot(i);
                if (item == null || item->ItemId == 0) continue;

                var itemKey = GearsetKey(item);
                if (protectedIds.Contains(itemKey)) continue;

                total++;
                if (found || failedItems.Contains(itemKey)) continue;

                container = type;
                slot = i;
                key = itemKey;
                name = ItemNames.Get(item->ItemId, (item->Flags & InventoryItem.ItemFlags.HighQuality) != 0);
                found = true;
            }
        }

        return found;
    }

    private static bool TryFindEmptyBagSlot(
        InventoryManager* manager, out InventoryType destination, out int destinationSlot)
    {
        destination = InventoryType.Invalid;
        destinationSlot = -1;

        foreach (var type in PlayerBags)
        {
            var inventory = manager->GetInventoryContainer(type);
            if (inventory == null || !inventory->IsLoaded) continue;

            for (var i = 0; i < inventory->Size; i++)
            {
                var item = inventory->GetInventorySlot(i);
                if (item == null || item->ItemId != 0) continue;

                destination = type;
                destinationSlot = i;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 這一刻能不能動作。
    /// ⚠️ <c>BetweenAreas</c> 期間庫存查詢會回 0 —— 那會被解讀成「沒有東西要搬」或「背包全空」，
    /// 兩個都是危險的誤判，所以在這裡就擋掉。
    /// </summary>
    private static bool TryReady(out string reason)
    {
        reason = string.Empty;

        if (!Svc.ClientState.IsLoggedIn || Svc.Objects.LocalPlayer == null || Svc.Condition[ConditionFlag.BetweenAreas])
        {
            reason = "正在切換區域，請稍後再試。";
            return false;
        }

        if (InventoryManager.Instance() == null)
        {
            reason = "取不到庫存資料。";
            return false;
        }

        if (RaptureGearsetModule.Instance() == null)
        {
            reason = "取不到套裝資料。";
            return false;
        }

        return true;
    }

    // ──────────────────────────── 流程 ────────────────────────────

    private void Start()
    {
        if (queue.IsBusy) return;

        if (!TryReady(out var reason))
        {
            Svc.Chat.PrintError($"[TC Toolbox] {reason}");
            return;
        }

        var manager = InventoryManager.Instance();
        var protectedIds = BuildProtectedItemIds(out var gearsetItemCount);

        // 🔑 校準：套裝裡一件裝備都沒有，代表資料還沒載入（或讀錯了），
        // 而不是「你真的沒有任何套裝」。這種時候整個兵裝庫都會被當成候選，
        // 一按下去就把所有裝備搬進背包 —— 這是本模組最貴的失敗形式，所以硬擋。
        if (gearsetItemCount == 0)
        {
            Svc.Log.Information($"[{InternalName}] 套裝資料裡一件裝備都沒有，拒絕執行（避免把整個兵裝庫搬空）。");
            Svc.Chat.PrintError(
                "[TC Toolbox] 讀不到任何套裝內容，為避免把整個兵裝庫搬空，已拒絕執行。" +
                "請先開一次「套裝」視窗再試。");
            return;
        }

        failedItems.Clear();
        movedCount = 0;
        iterations = 0;

        if (!TryFindCandidate(manager, protectedIds, out _, out _, out _, out _, out var total) || total == 0)
        {
            Svc.Chat.Print("[TC Toolbox] 兵裝庫裡沒有不屬於任何套裝的裝備。");
            return;
        }

        var empty = (int)manager->GetEmptySlotsInBag();
        if (empty < total)
        {
            // 🔴 「先停下並提示」＝不要開始。搬到一半停下來，使用者還得自己找出剩下哪些沒搬。
            Svc.Log.Information($"[{InternalName}] 背包空位不足：需要 {total} 格，只有 {empty} 格，未執行。");
            Svc.Chat.PrintError(
                $"[TC Toolbox] 背包空位不足：需要 {total} 格，目前只有 {empty} 格。已停下，未搬移任何東西。");
            return;
        }

        Svc.Log.Information(
            $"[{InternalName}] 開始搬移：候選 {total} 件、背包空位 {empty} 格、套裝保護 {protectedIds.Count} 個道具 ID。");

        EnqueueNext();
    }

    private void EnqueueNext()
    {
        queue.Enqueue("尋找下一件", () =>
        {
            if (++iterations > MaxIterations)
            {
                Svc.Log.Information(
                    $"[{InternalName}] 回合數超過上限 {MaxIterations}，強制中止（本輪已搬移 {movedCount} 件）。請回報。");
                Svc.Chat.PrintError($"[TC Toolbox] 流程異常反覆，已強制停止（本輪已搬移 {movedCount} 件）。");
                return null;
            }

            if (!TryReady(out var reason))
            {
                Svc.Log.Information($"[{InternalName}] 中止：{reason}（本輪已搬移 {movedCount} 件）");
                Svc.Chat.PrintError($"[TC Toolbox] {reason}");
                return null;
            }

            var manager = InventoryManager.Instance();
            var protectedIds = BuildProtectedItemIds(out var gearsetItemCount);
            if (gearsetItemCount == 0)
            {
                Svc.Log.Information($"[{InternalName}] 套裝資料在流程中變成空的，中止。");
                Svc.Chat.PrintError("[TC Toolbox] 套裝資料讀不到了，已停止搬移。");
                return null;
            }

            if (!TryFindCandidate(manager, protectedIds, out var container, out var slot, out var key,
                                  out var name, out _))
            {
                Svc.Chat.Print($"[TC Toolbox] 兵裝庫取回完成：本輪共搬移 {movedCount} 件。");
                Svc.Log.Information($"[{InternalName}] 完成，本輪共搬移 {movedCount} 件。");
                return true;
            }

            queue.Enqueue($"搬移 {name}", () =>
            {
                if (!Throttle.Pass("MoveGearsNotInSet-Move", 150)) return false;

                var mgr = InventoryManager.Instance();
                if (mgr == null) return null;

                // 快照可能過期：那一格已經不是同一件就重新掃，不冒險搬。
                var item = mgr->GetInventorySlot(container, slot);
                if (item == null || item->ItemId == 0 || GearsetKey(item) != key)
                {
                    Svc.Log.Information($"[{InternalName}] {container}#{slot} 內容已變動，重新掃描。");
                    return true;
                }

                if (!TryFindEmptyBagSlot(mgr, out var destination, out var destinationSlot))
                {
                    Svc.Log.Information($"[{InternalName}] 背包已滿，中止（本輪已搬移 {movedCount} 件）。");
                    Svc.Chat.PrintError(
                        $"[TC Toolbox] 背包已滿，已停止（本輪已搬移 {movedCount} 件）。請清出空間後再按一次。");
                    return null;
                }

                // 🔴 a6: true 絕對不能省 —— 省略＝只改本機、不送封包（見型別註解）。
                var result = mgr->MoveItemSlot(
                    container, (ushort)slot, destination, (ushort)destinationSlot, a6: true);

                // MoveItemSlot 會同步更新本機容器（a6 只影響送不送封包），
                // 所以呼叫完立刻回讀就能擋掉「當場沒成功」。
                var after = mgr->GetInventorySlot(container, slot);
                var moved = result == 0 &&
                            (after == null || after->ItemId == 0 || GearsetKey(after) != key);

                if (!moved)
                {
                    failedItems.Add(key);
                    Svc.Log.Information(
                        $"[{InternalName}] 搬移失敗（回傳 {result}）：{container}#{slot} → " +
                        $"{destination}#{destinationSlot}「{name}」，本輪跳過這件道具。");
                    return true;
                }

                movedCount++;
                Svc.Log.Debug(
                    $"[{InternalName}] {container}#{slot} → {destination}#{destinationSlot}「{name}」");
                return true;
            }, 10_000);

            queue.EnqueueDelay(120, "間隔");
            EnqueueNext();
            return true;
        }, 15_000);
    }

    // ──────────────────────────── UI ────────────────────────────

    private void RefreshPreview()
    {
        if (!TryReady(out var reason))
        {
            previewCandidates = null;
            previewReason = reason;
            return;
        }

        var manager = InventoryManager.Instance();
        var protectedIds = BuildProtectedItemIds(out var gearsetItemCount);

        if (gearsetItemCount == 0)
        {
            previewCandidates = null;
            previewReason = "讀不到任何套裝內容";
            return;
        }

        previewReason = string.Empty;
        TryFindCandidate(manager, protectedIds, out _, out _, out _, out _, out var total);
        previewCandidates = total;
        previewEmptySlots = (int)manager->GetEmptySlotsInBag();
    }

    public override void DrawConfig()
    {
        if (Throttle.Pass("MoveGearsNotInSet-Preview", 1_000)) RefreshPreview();

        var protectGlamour = Config.ProtectGlamourSources;
        if (ImGui.Checkbox("同時保護套裝指定的投影來源裝備", ref protectGlamour))
        {
            Config.ProtectGlamourSources = protectGlamour;
            Plugin.Instance.Config.Save();
            Throttle.Reset("MoveGearsNotInSet-Preview");
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "預設開啟：套裝裡設為「投影來源」的裝備即使本體在兵裝庫也不搬走。\n" +
                "關閉後完全比照 DailyRoutines，只看套裝的本體裝備。");
        }

        ImGui.Spacing();

        ImGui.AlignTextToFramePadding();
        if (previewCandidates is { } count)
        {
            ImGui.TextUnformatted($"不屬於任何套裝的裝備：{count} 件　背包空位：{previewEmptySlots} 格");
            if (count > previewEmptySlots)
            {
                ImGui.TextColored(new Vector4(1f, 0.45f, 0.4f, 1f),
                                  $"空位不足 {count - previewEmptySlots} 格，按下去會直接停下不搬。");
            }
        }
        else
        {
            ImGui.TextDisabled($"不屬於任何套裝的裝備：？（{previewReason}）");
        }

        ImGui.Spacing();

        using (ImRaii.Disabled(queue.IsBusy || previewCandidates is null or 0))
        {
            if (ImGui.Button("開始取回##gears-not-in-set"))
                Start();
        }

        ImGui.SameLine();
        using (ImRaii.Disabled(!queue.IsBusy))
        {
            if (ImGui.Button("停止##gears-not-in-set"))
            {
                queue.Abort();
                Svc.Chat.Print($"[TC Toolbox] 已停止（本輪已搬移 {movedCount} 件）。");
            }
        }

        if (queue.IsBusy)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"{queue.CurrentStep}（已搬移 {movedCount} 件）");
        }

        ImGui.TextColored(new Vector4(1f, 0.8f, 0.35f, 1f),
                          "⚠ 判斷依據只有「套裝」內容 —— 沒存進任何套裝的愛用裝備也會被算成非套裝裝備。\n" +
                          "　 執行前請先確認上面那個件數符合預期。");
    }
}

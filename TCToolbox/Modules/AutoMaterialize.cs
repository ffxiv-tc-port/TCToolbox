using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 自動精製魔晶石：開著「精製魔晶石」視窗按開始後，把背包＋兵裝庫裡所有精鍊度 100% 的裝備
/// 一件一件精製完。
/// 機制：呼叫遊戲自己的精製函式（與點擊清單同一條路徑，回傳碼直接當成功判定），
/// 精製確認對話框在流程進行中自動確認。零 hook、不寫記憶體、不做 patch。
/// 參考 DailyRoutines AutoMaterialize 設計重寫（API13、無 OmenTools／KamiToolKit 相依）。
/// </summary>
public sealed unsafe class AutoMaterialize : TcModule
{
    public override string InternalName => "AutoMaterialize";
    public override string DisplayName => "自動精製魔晶石";

    public override string Description =>
        "開啟「精製魔晶石」視窗後會出現一鍵按鈕：自動把背包與兵裝庫裡精鍊度已滿的裝備逐件精製，" +
        "並自動確認精製對話框。背包滿、戰鬥中或視窗關閉時自動停止。";

    public override ModuleCategory Category => ModuleCategory.Inventory;

    /// <summary>
    /// 精製視窗上按了按鈕才跑；開著不按，一件裝備都不會被精製。
    /// （確認對話框的自動確認只在流程跑著的時候才作用，見 <c>OnMaterializeDialog</c> 的
    /// <c>queue.IsBusy</c> 閘門——所以「開著」本身不會去點任何東西。）
    /// </summary>
    public override bool IsManualTrigger => true;

    /// <summary>遊戲的魔晶石精製函式（sig 已對台服 7.20 主程式離線驗證，唯一命中）。</summary>
    private const string ExtractMateriaSignature =
        "48 89 5C 24 ?? 48 89 74 24 ?? 57 48 83 EC ?? 41 0F BF F8 8B DA 48 8B F1 45 33 C0";

    /// <summary>回傳 0＝成功；其餘為遊戲的拒絕碼（3/4＝道具不符、9＝狀態不允許、34＝當前無法使用）。</summary>
    private delegate int ExtractMateriaDelegate(nint unused, InventoryType inventoryType, uint slot);

    private ExtractMateriaDelegate? extractMateria;

    private static readonly InventoryType[] SearchContainers =
    [
        InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4,
        InventoryType.ArmoryMainHand, InventoryType.ArmoryOffHand, InventoryType.ArmoryHead,
        InventoryType.ArmoryBody, InventoryType.ArmoryHands, InventoryType.ArmoryLegs,
        InventoryType.ArmoryFeets, InventoryType.ArmoryEar, InventoryType.ArmoryNeck,
        InventoryType.ArmoryWrist, InventoryType.ArmoryRings,
    ];

    private readonly TaskQueue queue = new() { DefaultTimeoutMs = 20_000 };

    private int extractedCount;

    protected override void OnEnable()
    {
        var address = Svc.SigScanner.ScanText(ExtractMateriaSignature);
        extractMateria = Marshal.GetDelegateForFunctionPointer<ExtractMateriaDelegate>(address);

        queue.OnTimeout = step => Svc.Chat.PrintError($"[TC Toolbox] 精製流程逾時，已停止：{step}");

        Svc.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "Materialize", OnMaterializeFinalize);
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "MaterializeDialog", OnMaterializeDialog);
        Svc.Framework.Update += OnUpdate;
        Svc.PluginInterface.UiBuilder.Draw += DrawOverlay;
    }

    protected override void OnDisable()
    {
        Svc.AddonLifecycle.UnregisterListener(OnMaterializeFinalize);
        Svc.AddonLifecycle.UnregisterListener(OnMaterializeDialog);
        Svc.Framework.Update -= OnUpdate;
        Svc.PluginInterface.UiBuilder.Draw -= DrawOverlay;

        queue.Abort();
        extractMateria = null;
    }

    private void OnUpdate(IFramework framework) => queue.Tick();

    private void OnMaterializeFinalize(AddonEvent type, AddonArgs args)
    {
        if (!queue.IsBusy) return;
        queue.Abort();
        Svc.Chat.Print($"[TC Toolbox] 精製視窗已關閉，停止精製（本輪已精製 {extractedCount} 件）。");
    }

    /// <summary>只有在我們的流程進行中才自動確認，手動精製不會被搶操作。</summary>
    private void OnMaterializeDialog(AddonEvent type, AddonArgs args)
    {
        if (!queue.IsBusy) return;

        var addon = (FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)args.Addon.Address;
        if (addon == null) return;

        UiHelper.FireCallback(addon, true, 0);
    }

    private void DrawOverlay()
    {
        var addon = UiHelper.GetAddon("Materialize");
        if (!UiHelper.IsReady(addon)) return;

        const ImGuiWindowFlags flags = ImGuiWindowFlags.AlwaysAutoResize |
                                       ImGuiWindowFlags.NoTitleBar |
                                       ImGuiWindowFlags.NoCollapse |
                                       ImGuiWindowFlags.NoSavedSettings;

        if (ImGui.Begin("###TCToolboxMaterialize", flags))
        {
            ImGui.SetWindowPos(new Vector2(addon->GetX() + 6, addon->GetY() - ImGui.GetWindowSize().Y - 4));

            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(new Vector4(1f, 0.85f, 0.35f, 1f), DisplayName);

            ImGui.SameLine();
            using (ImRaii.Disabled(queue.IsBusy))
            {
                if (ImGui.Button("開始##materialize"))
                    Start();
            }

            ImGui.SameLine();
            if (ImGui.Button("停止##materialize"))
            {
                queue.Abort();
                Svc.Chat.Print($"[TC Toolbox] 已手動停止精製（本輪已精製 {extractedCount} 件）。");
            }

            if (queue.IsBusy)
            {
                ImGui.SameLine();
                ImGui.TextDisabled(queue.CurrentStep ?? string.Empty);
            }
        }

        ImGui.End();
    }

    private void Start()
    {
        if (queue.IsBusy) return;
        extractedCount = 0;
        EnqueueNext();
    }

    private void EnqueueNext()
    {
        queue.Enqueue("尋找下一件精鍊度已滿的裝備", () =>
        {
            if (!UiHelper.IsAddonReady("Materialize")) return null;

            if (Svc.Condition[ConditionFlag.InCombat] || Svc.Condition[ConditionFlag.Mounted])
            {
                Svc.Chat.PrintError("[TC Toolbox] 戰鬥中或騎乘中無法精製，已停止。");
                return null;
            }

            var manager = InventoryManager.Instance();
            if (manager == null) return null;

            if (manager->GetEmptySlotsInBag() == 0)
            {
                Svc.Chat.PrintError("[TC Toolbox] 背包已滿，無法繼續精製。");
                return null;
            }

            if (!TryFindNextItem(manager, out var container, out var slot, out var itemId))
            {
                Svc.Chat.Print($"[TC Toolbox] 精製作業已完成：本輪共精製 {extractedCount} 件。");
                return true;
            }

            var itemName = Svc.Data.GetExcelSheet<Item>().GetRowOrDefault(itemId)?.Name.ExtractText()
                           ?? $"#{itemId}";

            queue.Enqueue($"精製 {itemName}", () =>
            {
                if (extractMateria == null) return null;

                // 精製動作進行中（Occupied39）就等它結束再送下一次
                if (Svc.Condition[ConditionFlag.Occupied39]) return false;
                if (!Throttle.Pass("AutoMaterialize-Extract", 400)) return false;

                var result = extractMateria(nint.Zero, container, slot);
                if (result != 0)
                {
                    Svc.Log.Debug($"[{InternalName}] 精製被拒（回傳碼 {result}），稍後重試：{itemName}");
                    return false;
                }

                extractedCount++;
                return true;
            }, 20_000);

            // 等精製動作結束（含自動確認對話框）。
            // 🔴 必須先延遲再檢查：Occupied39 不是送出那一幀立起來的，
            // 「送出→立刻 EnqueueWait」會直接通過等於沒等（RepairAllContainers 同型缺陷，a23c7bc 修法）。
            queue.EnqueueDelay(300, "等精製狀態立起");
            queue.EnqueueWait("等待精製完成", () => !Svc.Condition[ConditionFlag.Occupied39], 20_000);
            EnqueueNext();
            return true;
        }, 15_000);
    }

    private static bool TryFindNextItem(
        InventoryManager* manager,
        out InventoryType container,
        out uint slot,
        out uint itemId)
    {
        container = InventoryType.Invalid;
        slot = 0;
        itemId = 0;

        var itemSheet = Svc.Data.GetExcelSheet<Item>();

        foreach (var type in SearchContainers)
        {
            var inventory = manager->GetInventoryContainer(type);
            // 🔴 判的是 Items 不是 GetInventorySlot 的回傳值：Items 為 null 而 Size > 0 時，
            //    GetInventorySlot 回的是「null + 偏移」這種非 null 的假指標，下面的判空一定通過，
            //    解參考就是攔不到的 AVE（corrupted-state exception，try/catch 無效）。
            //    樣板同 DiscardList.ScanMatches／TriadCardRecycle 的背包掃描。
            if (inventory == null || !inventory->IsLoaded || inventory->Items == null) continue;

            for (var i = 0; i < inventory->Size; i++)
            {
                var item = inventory->GetInventorySlot(i);
                if (item == null || item->ItemId == 0) continue;

                // 精鍊度滿值 10000（同一欄位在收藏品上是收藏價值，故必須先排除非裝備）
                if (item->SpiritbondOrCollectability < 10_000) continue;

                var baseItemId = item->GetBaseItemId();
                var row = itemSheet.GetRowOrDefault(baseItemId);
                if (row == null || row.Value.EquipSlotCategory.RowId == 0) continue;

                container = type;
                slot = (uint)i;
                itemId = baseItemId;
                return true;
            }
        }

        return false;
    }
}

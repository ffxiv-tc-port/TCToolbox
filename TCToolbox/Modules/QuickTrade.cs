using System;
using System.Collections.Generic;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using TCToolbox.Core;
using CSFramework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;

namespace TCToolbox.Modules;

/// <summary>
/// 快速交易：交易視窗開著時，按住指定鍵右鍵點背包道具，自動選右鍵選單的「交易」。
/// </summary>
/// <remarks>
/// 契約：由使用者那一下右鍵觸發；當幀送不出去只記容器＋格號＋道具 id，<see cref="RetryWindowMs"/> 內
/// 每幀重查目標格、道具、視窗名、可見、項目文字後再送，送出一次就停；原生指標不跨幀保存。
/// 刻意不做：不直接呼叫放進交易格的函式（會跳過遊戲的可交易檢查）；不自己重開右鍵選單
/// （使用者關掉的選單不替他打開）；不處理數量框與魔晶石／優質確認框，那些留給玩家自己按。
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

    private const string ContextMenuAddonName = "ContextMenu";

    /// <summary>右鍵當幀送不出去時，繼續逐幀重試的時間窗（毫秒）。</summary>
    private const long RetryWindowMs = 500;

    /// <summary>重試幀數上限（低幀率時時間窗先到；高幀率時這個先到）。</summary>
    private const int MaxRetryFrames = 60;

    /// <summary>送出後盯交易欄的時間（毫秒），只用來寫診斷，不影響動作。</summary>
    private const long VerifyWindowMs = 1_500;

    /// <summary>交易欄可放道具的格數（第 6 格是 Gil）。</summary>
    private const int TradeItemSlots = 5;

    private const int MaxVerifications = 8;

    /// <summary>等待送出的那一下右鍵。只在框架執行緒讀寫；只存 id，不存指標。</summary>
    private sealed class PendingTrade
    {
        public required InventoryType Container { get; init; }
        public required int Slot { get; init; }
        public required uint ItemId { get; init; }
        public required string Name { get; init; }
        public required long StartedAt { get; init; }
        public int Frames { get; set; }
        public string LastShape { get; set; } = "尚未嘗試";
        public bool MenuLogged { get; set; }
    }

    private sealed record FiredTrade(
        InventoryType Container, int Slot, uint ItemId, string Name, int Baseline, long FiredAt);

    private PendingTrade? pending;
    private readonly List<FiredTrade> verifications = [];
    private static bool tradeConfirmedOnce;

    protected override void OnEnable()
    {
        // 位址解不出來會擲例外：模組啟用失敗、不掛，插件照常載入。
        InventoryContextHook.Subscribe(OnItemMenuOpened);
        Svc.Framework.Update += OnUpdate;
    }

    protected override void OnDisable()
    {
        Svc.Framework.Update -= OnUpdate;
        InventoryContextHook.Unsubscribe(OnItemMenuOpened);
        pending = null;
        verifications.Clear();
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

        if (pending != null)
        {
            Svc.Log.Information(
                $"[{InternalName}] 上一下右鍵還沒送出就換了下一下，放棄上一下：{pending.Container}#{pending.Slot}" +
                $"「{pending.Name}」（最後狀態：{pending.LastShape}，重試 {pending.Frames} 幀）");
        }

        pending = new PendingTrade
        {
            Container = inventoryType,
            Slot = slot,
            ItemId = item->ItemId,
            Name = name,
            StartedAt = Environment.TickCount64,
        };

        Step(agent, manager);
    }

    private enum StepOutcome
    {
        /// <summary>這一幀沒送出，但還可以再試。零值刻意給「再試」：忘了指派不會被當成已送出。</summary>
        Retry = 0,
        Fired = 1,
        Abandoned = 2,
    }

    /// <summary>對 <see cref="pending"/> 試送一次；送出或放棄時把它清掉。</summary>
    private void Step(AgentInventoryContext* agent, InventoryManager* manager)
    {
        var p = pending;
        if (p == null) return;

        var outcome = TryFire(p, agent, manager);
        if (outcome == StepOutcome.Retry) return;

        pending = null;
        if (outcome == StepOutcome.Fired && p.Frames > 0)
        {
            Svc.Log.Information(
                $"[{InternalName}] 右鍵當下沒送出（{p.LastShape}），重試第 {p.Frames} 幀送出：{p.Container}#{p.Slot}「{p.Name}」");
        }
    }

    private StepOutcome TryFire(PendingTrade p, AgentInventoryContext* agent, InventoryManager* manager)
    {
        if (agent == null || manager == null)
        {
            p.LastShape = "取不到右鍵選單或背包管理器";
            return StepOutcome.Retry;
        }

        if (agent->TargetInventoryId != p.Container || agent->TargetInventorySlotId != p.Slot)
            return Abandon(p, $"右鍵選單已換成 {agent->TargetInventoryId}#{agent->TargetInventorySlotId}");

        // Items 為 null 而 Size > 0 時 GetInventorySlot 回的是非 null 的假指標，先判容器。
        var container = manager->GetInventoryContainer(p.Container);
        if (container == null || container->Items == null) return Abandon(p, "背包容器讀不到");

        var item = manager->GetInventorySlot(p.Container, p.Slot);
        if (item == null || item->ItemId != p.ItemId) return Abandon(p, "那一格的道具已經變了");

        var addonId = agent->AgentInterface.GetAddonId();
        var addon = UiHelper.GetAddonById(addonId);
        if (addon == null)
        {
            p.LastShape = $"取不到右鍵選單視窗（addonId={addonId}）";
            return StepOutcome.Retry;
        }

        var addonName = UiHelper.ReadAddonName(addon);
        if (addonName != ContextMenuAddonName)
        {
            p.LastShape = $"右鍵選單視窗名稱是「{addonName}」不是 {ContextMenuAddonName}（addonId={addonId}）";
            return StepOutcome.Retry;
        }

        if (!UiHelper.IsReady(addon))
        {
            p.LastShape = $"右鍵選單視窗還沒顯示（addonId={addonId}）";
            return StepOutcome.Retry;
        }

        var baseline = CountInTrade(manager, p.ItemId);
        var result = InventoryContextMenu.TryFireEntry(
            agent, TradeAddonRow, InternalName, out var label, logMenu: !p.MenuLogged);
        p.MenuLogged = true;

        switch (result)
        {
            case ContextMenuFireResult.Fired:
                Svc.Log.Debug($"[{InternalName}] 已選「{label}」：{p.Container}#{p.Slot}「{p.Name}」");
                if (verifications.Count >= MaxVerifications) verifications.RemoveAt(0);
                verifications.Add(new FiredTrade(
                    p.Container, p.Slot, p.ItemId, p.Name, baseline, Environment.TickCount64));
                return StepOutcome.Fired;

            case ContextMenuFireResult.NotFound:
            case ContextMenuFireResult.Disabled:
                if (Throttle.Pass("QuickTrade-Untradable", 3_000))
                    Svc.Chat.PrintError($"[TC Toolbox] 「{p.Name}」無法交易，沒有放進交易視窗。");
                return StepOutcome.Abandoned;

            case ContextMenuFireResult.Guarded:
                p.LastShape = "同一個右鍵選單剛送過、還沒觀察到它收起";
                return StepOutcome.Retry;

            case ContextMenuFireResult.AddonUnavailable:
                p.LastShape = "送出當下取不到右鍵選單視窗";
                return StepOutcome.Retry;

            default:
                return Abandon(p, $"沒有選到「{label}」（{result}）");
        }
    }

    private StepOutcome Abandon(PendingTrade p, string why)
    {
        p.LastShape = why;
        Svc.Log.Information(
            $"[{InternalName}] 放棄送出「交易」：{why}：{p.Container}#{p.Slot}「{p.Name}」（重試 {p.Frames} 幀）");
        return StepOutcome.Abandoned;
    }

    private void OnUpdate(IFramework _)
    {
        if (pending == null && verifications.Count == 0) return;

        var manager = InventoryManager.Instance();
        var tradeSelecting = Svc.Condition[ConditionFlag.TradeOpen] &&
                             manager != null && manager->TradeLocalState == TradeState.SelectingTradeGoods;

        var p = pending;
        if (p != null)
        {
            if (!tradeSelecting)
            {
                pending = null;
                Abandon(p, "交易視窗已關或已不在挑選物品階段");
            }
            else
            {
                p.Frames++;
                Step(AgentInventoryContext.Instance(), manager);

                if (pending == p &&
                    (Environment.TickCount64 - p.StartedAt > RetryWindowMs || p.Frames >= MaxRetryFrames))
                {
                    pending = null;
                    Svc.Log.Information(
                        $"[{InternalName}] {RetryWindowMs} ms 內沒送出「交易」：{p.Container}#{p.Slot}「{p.Name}」" +
                        $"（最後狀態：{p.LastShape}，重試 {p.Frames} 幀）");
                }
            }
        }

        if (verifications.Count > 0) CheckVerifications(manager);
    }

    /// <summary>送出後的結果診斷（不影響動作）：交易欄多出該道具＝成功；出現數量框／確認框＝交給遊戲；逾時寫一行。</summary>
    private void CheckVerifications(InventoryManager* manager)
    {
        var now = Environment.TickCount64;
        var tradeOpen = Svc.Condition[ConditionFlag.TradeOpen] && manager != null;
        var dialog = UiHelper.IsAddonReady("InputNumeric") || UiHelper.IsAddonReady("SelectYesno");

        for (var i = verifications.Count - 1; i >= 0; i--)
        {
            var v = verifications[i];
            if (!tradeOpen || dialog)
            {
                verifications.RemoveAt(i);
                continue;
            }

            var count = CountInTrade(manager, v.ItemId);
            if (count > v.Baseline)
            {
                verifications.RemoveAt(i);
                if (!tradeConfirmedOnce)
                {
                    tradeConfirmedOnce = true;
                    Svc.Log.Information(
                        $"[{InternalName}] 交易欄已出現「{v.Name}」（送出後 {now - v.FiredAt} ms）。這一行每次遊戲只寫一次。");
                }

                continue;
            }

            if (now - v.FiredAt <= VerifyWindowMs) continue;

            verifications.RemoveAt(i);
            Svc.Log.Information(
                $"[{InternalName}] 已選「交易」但 {VerifyWindowMs} ms 內交易欄沒有多出「{v.Name}」：{v.Container}#{v.Slot}" +
                $"（本機狀態={manager->TradeLocalState}，交易欄同道具 {count} 格）");
        }
    }

    /// <summary>本機交易欄（不含 Gil 格）裡道具 id 等於 <paramref name="itemId"/> 的格數。</summary>
    private static int CountInTrade(InventoryManager* manager, uint itemId)
    {
        if (manager == null || itemId == 0) return 0;

        var slots = manager->TradeItemsLocal;
        var n = 0;
        for (var i = 0; i < TradeItemSlots && i < slots.Length; i++)
        {
            if (slots[i].ItemId == itemId) n++;
        }

        return n;
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

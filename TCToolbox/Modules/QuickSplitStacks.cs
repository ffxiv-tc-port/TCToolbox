using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 道具右鍵選單補上「快速拆分」：輸入數量後直接拆，不必自己點「拆分」再拉一次滑桿。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 <b>走的是遊戲原生的拆分流程，不直接改容器。</b>三步驟全部是使用者自己也做得到的操作：
/// <list type="number">
/// <item><c>AgentInventoryContext::OpenForItemSlot</c> 把那一格的右鍵選單重新開起來
/// （Dalamud 在我們的選單項被點下的當下就把選單關掉了，所以必須自己開回來）。</item>
/// <item>在選單裡找 <c>Addon#92</c>（台服＝<b>「拆分」</b>，2026-08-25 對
/// <c>exd-tc/7.20/Addon.csv</c> 查證）並點下去——比對用的是遊戲自己的字串，跟語言無關。</item>
/// <item>對彈出的 <c>InputNumeric</c> 送出「確定＋數量」。</item>
/// </list>
/// </para>
/// <para>
/// 🔴 <b>刻意不用 <c>InventoryManager::SplitItem</c>。</b>那支函式確實存在（CS 有宣告），
/// 但它<b>沒有</b>像 <c>MoveItemSlot</c> 那樣的「要不要送封包」旗標可以檢查，
/// 而我們沒有任何離線證據能證明它會把結果送到伺服器。同一個坑
/// （<c>MoveItemSlot</c> 省略 <c>a6</c> ＝只改本機、畫面上動了但伺服器不知道）
/// 已經在 <see cref="AutoInventoryTransfer"/> 上踩過一次。
/// </para>
/// <para>
/// 📌 選單項目的判斷順序、索引算法與診斷輸出共用
/// <see cref="InventoryContextMenu.TryFireEntry"/>——那是
/// <see cref="AutoInventoryTransfer"/> 已經實機驗證過的那條路徑。
/// </para>
/// <para>
/// ⚠️ <b>與 DailyRoutines <c>AutoSplitStacks</c> 的差異</b>：
/// <list type="bullet">
/// <item>DR 拿到道具 ID 之後<b>重新掃一遍背包</b>去找哪一格有這個道具；
/// 這裡直接用右鍵當下 Dalamud 給的容器＋格號，不會拆到另一疊。</item>
/// <item>DR 的「快速拆分」拆完會<b>自己再排一輪</b>（不斷重複拆到失敗為止）。
/// 這裡一次只拆一次——名字叫「快速拆分」而行為是「一直拆」是會嚇到人的。</item>
/// <item>DR 另外做了一整套「預設拆分清單」與 <c>/pdrsplit</c> 指令。這裡沒做：
/// 那是另一個功能，混在同一個模組裡會讓開關的語意變得不清楚。</item>
/// </list>
/// </para>
/// </remarks>
public sealed unsafe class QuickSplitStacks : TcModule
{
    public override string InternalName => "QuickSplitStacks";
    public override string DisplayName => "道具快速拆分";

    public override string Description =>
        "在可疊道具的右鍵選單補上「快速拆分」：填一個數字按確定就拆好，不必自己點「拆分」再拉滑桿。" +
        "走的是遊戲原生的拆分流程（重開右鍵選單 → 點「拆分」 → 填數量），不直接改背包資料。" +
        "只在背包、陸行鳥鞍囊、雇員背包出現，且該容器要有空格。";

    public override ModuleCategory Category => ModuleCategory.Inventory;

    public override bool HasConfigUI => true;

    /// <summary>台服 <c>Addon#92</c>＝「拆分」（2026-08-25 對 <c>exd-tc/7.20/Addon.csv</c> 查證）。</summary>
    private const uint SplitAddonRow = 92;

    /// <summary>遊戲拆分數量輸入框的 addon 名。</summary>
    private const string InputNumericAddon = "InputNumeric";

    /// <summary>玩家背包四頁。</summary>
    private static readonly InventoryType[] PlayerBags =
    [
        InventoryType.Inventory1, InventoryType.Inventory2,
        InventoryType.Inventory3, InventoryType.Inventory4,
    ];

    /// <summary>陸行鳥鞍囊（含贈禮版）。</summary>
    private static readonly InventoryType[] SaddleBags =
    [
        InventoryType.SaddleBag1, InventoryType.SaddleBag2,
        InventoryType.PremiumSaddleBag1, InventoryType.PremiumSaddleBag2,
    ];

    /// <summary>雇員背包七頁。</summary>
    private static readonly InventoryType[] RetainerBags =
    [
        InventoryType.RetainerPage1, InventoryType.RetainerPage2, InventoryType.RetainerPage3,
        InventoryType.RetainerPage4, InventoryType.RetainerPage5, InventoryType.RetainerPage6,
        InventoryType.RetainerPage7,
    ];

    /// <summary>使用者按了「快速拆分」之後、還沒填完數字的那一格。</summary>
    /// <remarks>
    /// 🔴 <b>存的是容器＋格號＋道具 ID，不是原生指標。</b>從按下選單到按下確定之間隔了好幾幀，
    /// 期間背包隨時可能被重排；跨幀保存 <c>InventoryItem*</c> 是艦隊紅線。
    /// 真的要動手時再重查一次，對不上就中止。
    /// </remarks>
    private sealed record Pending(
        InventoryType Container, int Slot, uint ItemId, string ItemName, int Quantity, uint OwnerAddonId);

    private Pending? pending;

    private int amountInput = 1;

    private bool popupOpening;

    private Vector2 popupPos;

    private readonly TaskQueue queue = new() { DefaultTimeoutMs = 5_000 };

    private QuickSplitStacksConfig Config => Plugin.Instance.Config.QuickSplitStacks;

    protected override void OnEnable()
    {
        queue.OnTimeout = step => Svc.Chat.PrintError($"[TC Toolbox] 快速拆分逾時，已停止：{step}");

        Svc.ContextMenu.OnMenuOpened += OnMenuOpened;
        Svc.Framework.Update += OnUpdate;
        Svc.PluginInterface.UiBuilder.Draw += DrawPopup;

        Svc.Log.Information($"[{InternalName}] 模組啟用：可疊道具的右鍵選單會多出「快速拆分」。");
    }

    protected override void OnDisable()
    {
        Svc.ContextMenu.OnMenuOpened -= OnMenuOpened;
        Svc.Framework.Update -= OnUpdate;
        Svc.PluginInterface.UiBuilder.Draw -= DrawPopup;

        queue.Abort();
        pending = null;
        popupOpening = false;
    }

    private void OnUpdate(IFramework framework) => queue.Tick();

    private void OnMenuOpened(IMenuOpenedArgs args)
    {
        try
        {
            if (args.MenuType != ContextMenuType.Inventory) return;
            if (args.Target is not MenuTargetInventory { TargetItem: { } item }) return;

            // 只有一個的東西沒有東西可拆。
            if (item.Quantity <= 1) return;

            var container = (InventoryType)(ushort)item.ContainerType;
            if (FamilyOf(container) == null) return;

            if (!ItemContextResolver.TryGetItemName(item.ItemId, out var baseId, out var itemName)) return;

            // 🔴 addon id 取自 Dalamud 交給我們的「父視窗」（背包／鞍囊／雇員背包本體），
            //    不是右鍵選單自己。OpenForItemSlot 要的正是這個父視窗。
            var ownerAddonId = ReadAddonId(args.AddonPtr);
            if (ownerAddonId == 0) return;

            var slot = (int)item.InventorySlot;
            var quantity = item.Quantity;

            args.AddMenuItem(new MenuItem
            {
                Name = "快速拆分",
                PrefixChar = 'T',
                PrefixColor = 539,
                Priority = 0,
                OnClicked = _ => BeginSplit(
                    new Pending(container, slot, item.ItemId, itemName, quantity, ownerAddonId), baseId),
            });
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, $"[{InternalName}] 建立右鍵選單項目時發生例外（addon: {args.AddonName}）");
        }
    }

    private static uint ReadAddonId(nint addonPtr)
    {
        if (addonPtr == nint.Zero) return 0;
        return ((FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)addonPtr)->Id;
    }

    private void BeginSplit(Pending target, uint baseId)
    {
        queue.Abort();
        pending = target;
        popupOpening = true;
        popupPos = ImGui.GetMousePos();

        // 記住上次填的數字很方便（「每次都拆 99」是常見用法），但不能超過這一疊的上限。
        amountInput = Math.Clamp(Config.RememberAmount ? Config.LastAmount : 1, 1, Math.Max(1, target.Quantity - 1));

        Svc.Log.Information(
            $"[{InternalName}] 使用者選了快速拆分：{target.ItemName}（#{baseId}）" +
            $"{target.Container}#{target.Slot} 共 {target.Quantity} 個。");
    }

    private void DrawPopup()
    {
        if (pending is not { } target) return;

        const string title = "快速拆分###TCToolboxQuickSplit";

        if (popupOpening)
        {
            ImGui.SetNextWindowPos(popupPos + new Vector2(12f, 12f), ImGuiCond.Always);
            ImGui.SetNextWindowFocus();
            popupOpening = false;
        }

        var open = true;
        const ImGuiWindowFlags flags = ImGuiWindowFlags.AlwaysAutoResize |
                                       ImGuiWindowFlags.NoCollapse |
                                       ImGuiWindowFlags.NoSavedSettings;

        if (ImGui.Begin(title, ref open, flags))
        {
            ImGui.TextUnformatted($"{target.ItemName} × {target.Quantity}");
            ImGui.Separator();

            var max = Math.Max(1, target.Quantity - 1);

            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted("拆出數量");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(120f);
            if (ImGui.InputInt("###amount", ref amountInput))
                amountInput = Math.Clamp(amountInput, 1, max);

            ImGui.SameLine();
            if (ImGui.Button($"一半（{target.Quantity / 2}）")) amountInput = Math.Clamp(target.Quantity / 2, 1, max);

            ImGui.TextDisabled($"可填 1 ～ {max}（不能把整疊拆走）。");

            // 🔴 鍵盤捷徑一律先問「焦點在不在這扇視窗上」。少了這道，只要視窗還畫著，
            //    使用者在<b>別的地方</b>按 Enter 就會把拆分送出去——而且那是靜默的。
            var focused = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);

            ImGui.Spacing();
            if (ImGui.Button("確定##quick-split") || (focused && ImGui.IsKeyPressed(ImGuiKey.Enter)))
            {
                amountInput = Math.Clamp(amountInput, 1, max);
                Config.LastAmount = amountInput;
                Plugin.Instance.Config.Save();
                StartQueue(target, amountInput);
                pending = null;
            }

            ImGui.SameLine();
            if (ImGui.Button("取消##quick-split") || (focused && ImGui.IsKeyPressed(ImGuiKey.Escape)))
                pending = null;
        }

        ImGui.End();

        if (!open) pending = null;
    }

    private void StartQueue(Pending target, int amount)
    {
        queue.Abort();

        queue.Enqueue("重新開啟右鍵選單", () =>
        {
            var manager = InventoryManager.Instance();
            if (manager == null) return null;

            // 🔴 跨幀之後一律重查：使用者填數字的期間背包可能被整理過。
            if (!SlotStillHolds(manager, target, amount, out var reason))
            {
                Svc.Chat.PrintError($"[TC Toolbox] 快速拆分取消：{reason}");
                Svc.Log.Information($"[{InternalName}] 中止：{reason}");
                return null;
            }

            if (!HasFreeSlot(manager, target.Container))
            {
                Svc.Chat.PrintError("[TC Toolbox] 快速拆分取消：這個容器已經沒有空格，拆不出新的一疊。");
                Svc.Log.Information($"[{InternalName}] 中止：{target.Container} 家族沒有空格。");
                return null;
            }

            // AgentInventoryContext 是 [Agent] ⇒ 合法回 null，必須判空。
            var agent = AgentInventoryContext.Instance();
            if (agent == null) return null;

            if (UiHelper.GetAddonById(target.OwnerAddonId) == null)
            {
                Svc.Chat.PrintError("[TC Toolbox] 快速拆分取消：原本的背包視窗已經關了。");
                Svc.Log.Information($"[{InternalName}] 中止：owner addon {target.OwnerAddonId} 已消失。");
                return null;
            }

            agent->OpenForItemSlot(target.Container, target.Slot, 0, target.OwnerAddonId);
            return true;
        });

        queue.EnqueueWait("等待右鍵選單就緒", () =>
        {
            var agent = AgentInventoryContext.Instance();
            return agent != null && agent->ContextItemCount > 0;
        });

        queue.Enqueue("點「拆分」", () =>
        {
            var agent = AgentInventoryContext.Instance();
            if (agent == null) return null;

            var result = InventoryContextMenu.TryFireEntry(agent, SplitAddonRow, InternalName, out var label);
            if (result == ContextMenuFireResult.Fired) return true;

            var why = result switch
            {
                ContextMenuFireResult.NotFound => $"右鍵選單裡找不到「{label}」",
                ContextMenuFireResult.InSubmenu => $"「{label}」被收在次選單裡",
                ContextMenuFireResult.Disabled => $"「{label}」目前是停用狀態",
                ContextMenuFireResult.AddonUnavailable => "取不到右鍵選單視窗",
                _ => "讀不到遊戲的「拆分」用語",
            };

            Svc.Chat.PrintError($"[TC Toolbox] 快速拆分失敗：{why}，請改用手動拆分。");
            return null;
        });

        queue.EnqueueWait("等待數量輸入框", () => UiHelper.IsAddonReady(InputNumericAddon));

        queue.Enqueue("送出數量", () =>
        {
            var addon = UiHelper.GetAddon(InputNumericAddon);
            if (!UiHelper.IsReady(addon)) return false;

            // 值的形狀照 DailyRoutines 實作：單一 Int＝要拆出來的數量。
            UiHelper.FireCallback(addon, true, amount);

            if (Config.NotifyOnSplit)
                Svc.Chat.Print($"[TC Toolbox] 已拆出「{target.ItemName}」× {amount}。");

            Svc.Log.Information(
                $"[{InternalName}] 已送出拆分：{target.ItemName} {target.Container}#{target.Slot} × {amount}。");
            return true;
        });
    }

    /// <summary>這一格現在還是不是原來那個道具、而且數量夠拆。</summary>
    private static bool SlotStillHolds(InventoryManager* manager, Pending target, int amount, out string reason)
    {
        // 🔴 GetInventoryContainer／GetInventorySlot 都是合法回 null 的成員函式，
        //    裸接著 -> 就是攔不到的 AVE。
        var container = manager->GetInventoryContainer(target.Container);
        if (container == null)
        {
            reason = "讀不到那個容器";
            return false;
        }

        if (target.Slot < 0 || target.Slot >= container->Size)
        {
            reason = "格號超出容器範圍";
            return false;
        }

        var slot = container->GetInventorySlot(target.Slot);
        if (slot == null)
        {
            reason = "讀不到那一格";
            return false;
        }

        if (slot->ItemId != target.ItemId)
        {
            reason = "那一格的東西已經換了（背包被整理過？）";
            return false;
        }

        if (slot->GetQuantity() <= amount)
        {
            reason = $"數量只剩 {slot->GetQuantity()} 個，拆不出 {amount} 個";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>這個容器所屬的家族裡還有沒有空格。</summary>
    private static bool HasFreeSlot(InventoryManager* manager, InventoryType type)
    {
        var family = FamilyOf(type);
        if (family == null) return false;

        foreach (var candidate in family)
        {
            var container = manager->GetInventoryContainer(candidate);
            if (container == null) continue;

            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot != null && slot->ItemId == 0) return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 這個容器屬於哪一組「拆出來的新一疊會落進去」的容器。
    /// </summary>
    /// <remarks>
    /// 🔴 回 <see langword="null"/>＝<b>不支援</b>，右鍵選單就不會出現「快速拆分」。
    /// 刻意採白名單而不是黑名單：沒被列到的容器（裝備欄、兵裝庫、部隊置物櫃、投影台……）
    /// 語意各不相同，猜錯的代價是使用者對著一個沒把握的容器按下去。
    /// </remarks>
    private static InventoryType[]? FamilyOf(InventoryType type)
    {
        if (Array.IndexOf(PlayerBags, type) >= 0) return PlayerBags;
        if (Array.IndexOf(SaddleBags, type) >= 0) return SaddleBags;
        if (Array.IndexOf(RetainerBags, type) >= 0) return RetainerBags;
        return null;
    }

    public override void DrawConfig()
    {
        var notify = Config.NotifyOnSplit;
        if (ImGui.Checkbox("拆分後顯示聊天訊息", ref notify))
        {
            Config.NotifyOnSplit = notify;
            Plugin.Instance.Config.Save();
        }

        var remember = Config.RememberAmount;
        if (ImGui.Checkbox("記住上次填的數量", ref remember))
        {
            Config.RememberAmount = remember;
            Plugin.Instance.Config.Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"目前記住的是 {Config.LastAmount}。關掉的話每次都從 1 開始。");

        ImGui.Spacing();
        ImGui.TextDisabled("只在背包、陸行鳥鞍囊、雇員背包的可疊道具上出現。");
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "裝備欄、兵裝庫、部隊置物櫃、投影台的右鍵選單刻意不加：\n" +
                "那些容器的拆分語意各不相同，猜錯的代價是對著沒把握的容器按下去。\n" +
                "整個流程走的是遊戲原生的「拆分」選單，不直接改背包資料。");
        }
    }
}

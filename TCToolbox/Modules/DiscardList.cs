using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 道具丟棄清單：把想丟的道具維護成一份清單，模組列出背包裡符合的道具，由使用者逐件或整批發起丟棄。
/// </summary>
/// <remarks>
/// <para>
/// 🔴🔴 <b>永遠不會替你按下確認框。</b>發起丟棄之後跳出來的是<b>遊戲自己的</b>「確定要捨棄…嗎？」
/// 確認框，本模組<b>完全不碰它</b>——是/否一律由你自己按。誤丟不可逆，所以這個模組刻意把
/// 「決定要丟」（你在清單上按）與「真的丟下去」（你在遊戲確認框上按是）拆成兩個都要人動手的步驟。
/// 上游 DailyRoutines 的 <c>AutoDiscard</c> 會自動點掉那個確認框、還能匯入別人的丟棄清單，
/// <b>那兩件事都刻意不做</b>。
/// </para>
/// <para>
/// 🔑 <b>走的是遊戲原生的右鍵「捨棄」（<c>Addon</c> 第 91 列＝台服「捨棄」）。</b>流程與你自己
/// 右鍵那件道具、點「捨棄」完全一樣：
/// <list type="number">
/// <item>對那一格呼叫 <c>AgentInventoryContext.OpenForItemSlot</c>（與本專案 <c>QuickSplitStacks</c>
/// 用的是同一支、台服實測有效的函式）叫出右鍵選單。</item>
/// <item>用 <see cref="InventoryContextMenu.TryFireEntry"/> 在選單裡<b>比對「捨棄」這個字串</b>找到那一項並點下去
/// —— <b>比對字串而不是寫死序號</b>，所以不會因為選單少一項就點到隔壁的「販賣」。</item>
/// <item>遊戲跳出它自己的確認框，<b>由你按是/否</b>。</item>
/// </list>
/// 刻意<b>不</b>用 <c>AgentInventoryContext.DiscardItem</c> 那支直呼函式：它在 FFXIVClientStructs
/// 是特徵碼綁定的，台服 7.20 是否對得上未經驗證，對不上就是呼叫空位址崩潰；而
/// <c>OpenForItemSlot</c>＋選單字串比對這條路徑本專案已經在用、而且失敗形式是「找不到就不動作」。
/// </para>
/// <para>
/// 🔴 <b>不保存任何原生指標、只掃主背包四袋。</b>每次要動手的那一刻才重新向
/// <c>InventoryManager</c> 取那一格，並確認裡面還是同一件道具（背包被整理過的話就跳過）。
/// 只掃 <c>Inventory1</c>~<c>Inventory4</c>——不碰裝備欄、兵裝庫、鞍袋，所以正在穿的裝備、
/// 收在別處的東西都不可能被列進來、更不可能被丟。
/// </para>
/// <para>
/// 📌 <b>整批也只是把你排進佇列，逐一跳確認框讓你按。</b>絕不會一次堆出一排確認框，
/// 也絕不會替你連按——下一件要等你把上一件的確認框回應掉（是或否都行）才會發起。
/// </para>
/// </remarks>
public sealed unsafe class DiscardList : TcModule
{
    public override string InternalName => "DiscardList";

    public override string DisplayName => "道具丟棄清單";

    public override string Description =>
        "把想丟的道具維護成一份清單，模組列出背包裡符合的道具，由你逐件或整批發起丟棄。" +
        "走遊戲原生的「捨棄」流程，每一件都會跳出遊戲自己的確認框由你親自按是/否——" +
        "永遠不會替你確認，也不做匯入別人清單。";

    public override ModuleCategory Category => ModuleCategory.Inventory;

    /// <summary>開著不去操作它，遊戲行為完全不變。</summary>
    public override bool IsManualTrigger => true;

    public override bool HasConfigUI => true;

    /// <summary>台服「捨棄」在 <c>Addon</c> 表的列號（本檔開頭已離線核對＝「捨棄」）。</summary>
    private const uint DiscardAddonRow = 91;

    private const string SelectYesnoAddon = "SelectYesno";

    /// <summary>找「目前開著的背包視窗」用的候選名字，由大到小排（優先用整合式的那個）。</summary>
    private static readonly string[] InventoryAddonNames =
        ["InventoryExpansion", "InventoryLarge", "Inventory"];

    /// <summary>只掃這四袋主背包，其他容器一律不碰。</summary>
    private static readonly InventoryType[] ScannedBags =
        [InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4];

    private DiscardListConfig Config => Plugin.Instance.Config.DiscardList;

    private readonly TaskQueue queue = new() { DefaultTimeoutMs = 15_000 };

    /// <summary>解析好的「捨棄」字串，只用來在 UI 上顯示與佐證這個客戶端認得這個詞。</summary>
    private string discardLabel = string.Empty;

    /// <summary>還沒發起的丟棄目標。</summary>
    private Queue<Match> pendingDiscards = new();

    private int initiatedCount;

    private int skippedCount;

    private string lastResult = string.Empty;

    // 道具搜尋（新增到清單用）的快取，只在輸入改變時重算。
    private string itemSearchInput = string.Empty;

    private List<Item> itemSearchResults = [];

    /// <summary>背包裡一件符合清單的道具（當幀快照，不留指標）。</summary>
    private readonly record struct Match(
        InventoryType Container, short Slot, uint BaseId, string Name, int Quantity, bool HighQuality);

    protected override void OnEnable()
    {
        discardLabel = Svc.Data.GetExcelSheet<Addon>()
                          .GetRowOrDefault(DiscardAddonRow)?.Text.ExtractText().Trim() ?? string.Empty;

        Svc.Log.Information(
            $"[{InternalName}] 「捨棄」字串判準（Addon #{DiscardAddonRow}）＝" +
            (discardLabel.Length > 0 ? $"「{discardLabel}」" : "（查不到，這個客戶端無法比對選單）"));

        Svc.Framework.Update += OnUpdate;

        queue.OnTimeout = step =>
        {
            lastResult = $"逾時中止於「{step}」";
            Svc.Log.Information($"[{InternalName}] 步驟逾時，整輪中止：{step}");
        };
    }

    protected override void OnDisable()
    {
        Svc.Framework.Update -= OnUpdate;
        queue.Abort();
        pendingDiscards.Clear();
    }

    private void OnUpdate(IFramework framework) => queue.Tick();

    #region 掃背包

    /// <summary>
    /// 掃主背包四袋，回傳目前在場、且在使用者清單裡的道具。
    /// </summary>
    /// <remarks>🔴 每次呼叫重新取容器與格子，不留任何指標；回傳純值快照。</remarks>
    private List<Match> ScanMatches()
    {
        var result = new List<Match>();

        var wanted = Config.Items;
        if (wanted.Count == 0) return result;

        var manager = InventoryManager.Instance();
        if (manager == null) return result;

        foreach (var bag in ScannedBags)
        {
            var container = manager->GetInventoryContainer(bag);

            // 🔴 判的是 Items 不是 GetInventorySlot 的回傳值：Items 為 null 而 Size > 0 時，
            //    GetInventorySlot 回的是「null + 偏移」這種非 null 的假指標，下面的
            //    slot != null 一定通過，讀 slot->ItemId 就是攔不到的 AVE。
            //    樣板同 TriadCardRecycle.cs 的兩處背包掃描。
            if (container == null || !container->IsLoaded || container->Items == null) continue;

            var size = container->Size;
            for (var i = 0; i < size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot == null) continue;

                var rawId = slot->ItemId;
                if (rawId == 0) continue;

                // baseId = 去掉 HQ 位移。HQ 道具的 ItemId 是 baseId + 1_000_000。
                var baseId = rawId % 1_000_000;
                if (!wanted.Contains(baseId)) continue;

                result.Add(new Match(
                    bag,
                    slot->Slot,
                    baseId,
                    ItemNames.Get(baseId),
                    slot->Quantity,
                    (slot->Flags & InventoryItem.ItemFlags.HighQuality) != 0));
            }
        }

        return result;
    }

    #endregion

    #region 丟棄流程

    private void StartDiscarding(IEnumerable<Match> targets)
    {
        if (queue.IsBusy) return;

        if (discardLabel.Length == 0)
        {
            lastResult = "這個客戶端查不到「捨棄」字串，拒絕執行";
            Svc.Log.Information(
                $"[{InternalName}] 拒絕執行：Addon 第 {DiscardAddonRow} 列在這個客戶端是空的，" +
                "無法在右鍵選單裡認出「捨棄」那一項。");
            return;
        }

        pendingDiscards = new Queue<Match>(targets);
        initiatedCount = 0;
        skippedCount = 0;

        if (pendingDiscards.Count == 0)
        {
            lastResult = "沒有符合的道具";
            return;
        }

        lastResult = string.Empty;
        Svc.Log.Information(
            $"[{InternalName}] 開始逐件丟棄，共 {pendingDiscards.Count} 件。" +
            "每一件都會跳出遊戲自己的確認框，需要你逐一按下「是」才會真的丟掉。");

        EnqueueIteration();
    }

    /// <summary>
    /// 排一輪「處理下一件」。做法照 <see cref="LetterCollectAll"/>：一輪只處理一件、動態排下一輪，
    /// 完全不預先攤平索引。
    /// </summary>
    private void EnqueueIteration()
    {
        // 🔴 先等上一件的確認框被你回應掉（是或否都行）再處理下一件——絕不同時堆兩個確認框。
        //    逾時給得很長，因為這一步等的是「人」，不是遊戲。
        queue.EnqueueWait("等待上一個確認框關閉", () => !UiHelper.IsAddonReady(SelectYesnoAddon), 180_000);

        queue.Enqueue("開啟捨棄選單", () =>
        {
            if (pendingDiscards.Count == 0)
            {
                Finish();
                return null;
            }

            var m = pendingDiscards.Dequeue();

            var manager = InventoryManager.Instance();
            if (manager == null)
            {
                Finish("背包管理器不可用");
                return null;
            }

            // 🔴 動手前重新取這一格、確認還是同一件道具（跨幀之間背包可能被整理過）。
            var slot = manager->GetInventorySlot(m.Container, m.Slot);
            if (slot == null || slot->ItemId % 1_000_000 != m.BaseId)
            {
                skippedCount++;
                Svc.Log.Information(
                    $"[{InternalName}] 跳過 {m.Name}（{m.Container}#{m.Slot}）：這一格的內容已經和剛才不一樣了。");
                EnqueueIteration();
                return true;
            }

            if (!TryGetActiveInventoryAddonId(out var ownerId))
            {
                Finish("背包視窗已關閉");
                return null;
            }

            var agent = AgentInventoryContext.Instance();
            if (agent == null)
            {
                Finish("背包右鍵選單代理不可用");
                return null;
            }

            agent->OpenForItemSlot(m.Container, m.Slot, 0, ownerId);

            // 選單開了，接著等它就緒 → 點「捨棄」→ 稍待 → 排下一件。
            queue.EnqueueWait("等待右鍵選單就緒", () =>
            {
                var a = AgentInventoryContext.Instance();
                return a != null && a->ContextItemCount > 0;
            }, 5_000);

            queue.Enqueue("點『捨棄』", () =>
            {
                var a = AgentInventoryContext.Instance();
                if (a == null)
                {
                    Finish("右鍵選單代理消失");
                    return null;
                }

                var result = InventoryContextMenu.TryFireEntry(a, DiscardAddonRow, InternalName, out var label);
                // 守衛擋下＝同一扇選單實例剛送過、還在關閉中：這一輪沒送，下一 tick 再來（步驟逾時兜底）。
                if (result == ContextMenuFireResult.Guarded) return false;
                if (result == ContextMenuFireResult.Fired)
                {
                    initiatedCount++;
                    Svc.Log.Information(
                        $"[{InternalName}] 已對 {m.Name}（{m.Container}#{m.Slot}）叫出遊戲的「{label}」確認框，" +
                        "等你按下是/否。");
                    return true;
                }

                // 沒點成：把選單關掉、記一筆、繼續下一件（不整輪中止）。
                skippedCount++;
                var why = result switch
                {
                    ContextMenuFireResult.NotFound => $"右鍵選單裡沒有「{label}」這一項",
                    ContextMenuFireResult.InSubmenu => $"「{label}」被收在次選單裡",
                    ContextMenuFireResult.Disabled => $"「{label}」目前是停用狀態（這件可能不能丟）",
                    ContextMenuFireResult.AddonUnavailable => "取不到右鍵選單視窗",
                    _ => "讀不到遊戲的「捨棄」用語",
                };
                Svc.Log.Information($"[{InternalName}] 跳過 {m.Name}（{m.Container}#{m.Slot}）：{why}。");
                CloseContextMenu();
                return true;
            });

            queue.EnqueueDelay(Math.Max(100, Config.StepIntervalMs));
            queue.Enqueue("排下一件", () =>
            {
                EnqueueIteration();
                return true;
            });

            return true;
        });
    }

    private void Finish(string? reason = null)
    {
        pendingDiscards.Clear();

        lastResult = reason == null
            ? $"完成：已對 {initiatedCount} 件叫出確認框" + (skippedCount > 0 ? $"、跳過 {skippedCount} 件" : "")
            : $"{reason}：已對 {initiatedCount} 件叫出確認框" + (skippedCount > 0 ? $"、跳過 {skippedCount} 件" : "");

        Svc.Log.Information($"[{InternalName}] {lastResult}。（真正丟掉幾件取決於你在確認框上按了幾次是。）");

        if (Config.NotifyInChat)
            Svc.Chat.Print($"[TC Toolbox] 丟棄清單：{lastResult}。");
    }

    /// <summary>找目前開著的背包視窗，回傳它的 addon id。找不到＝背包沒開。</summary>
    private static bool TryGetActiveInventoryAddonId(out uint id)
    {
        id = 0;
        foreach (var name in InventoryAddonNames)
        {
            var addon = UiHelper.GetAddon(name);
            if (UiHelper.IsReady(addon))
            {
                id = addon->Id;
                return true;
            }
        }

        return false;
    }

    private static void CloseContextMenu()
    {
        var agent = AgentInventoryContext.Instance();
        if (agent == null) return;

        var addon = UiHelper.GetAddonById(agent->AgentInterface.GetAddonId());
        if (addon != null)
            addon->Close(true);
    }

    #endregion

    #region 設定 UI

    public override void DrawConfig()
    {
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
        ImGui.TextDisabled(
            "維護一份「想丟的道具」清單，模組會列出背包（主四袋）裡符合的道具讓你發起丟棄。" +
            "走的是遊戲原生的「捨棄」：每一件都會跳出遊戲自己的確認框，是/否一律由你親自按。");
        ImGui.PopTextWrapPos();

        ImGui.TextColored(new Vector4(1f, 0.75f, 0.4f, 1f),
            "本模組永遠不會替你按確認框，也不會匯入別人的清單。丟掉的道具無法復原。");

        if (discardLabel.Length == 0)
        {
            ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f),
                $"這個客戶端的 Addon 第 {DiscardAddonRow} 列是空的，無法在選單裡認出「捨棄」，功能已鎖住。");
        }

        ImGui.Spacing();
        ImGui.Separator();

        DrawListEditor();

        ImGui.Spacing();
        ImGui.Separator();

        DrawInventoryMatches();

        ImGui.Spacing();
        ImGui.Separator();

        ImGui.SetNextItemWidth(200f);
        var interval = Config.StepIntervalMs;
        if (ImGui.SliderInt("每步間隔（毫秒）", ref interval, 100, 1_000))
            Config.StepIntervalMs = Math.Clamp(interval, 0, 5_000);
        if (ImGui.IsItemDeactivatedAfterEdit())
            Plugin.Instance.Config.Save();

        var notify = Config.NotifyInChat;
        if (ImGui.Checkbox("結束時在聊天欄報告", ref notify))
        {
            Config.NotifyInChat = notify;
            Plugin.Instance.Config.Save();
        }
    }

    private void DrawListEditor()
    {
        ImGui.TextUnformatted($"丟棄清單（{Config.Items.Count} 項）");

        // 目前清單：逐項可移除。
        using (var child = ImRaii.Child("DiscardListItems", new Vector2(-1f, 120f), true))
        {
            if (child)
            {
                if (Config.Items.Count == 0)
                {
                    ImGui.TextDisabled("清單是空的。用下面的搜尋框把想丟的道具加進來。");
                }
                else
                {
                    // 複製一份來列，避免在列舉當中改動集合。
                    foreach (var baseId in Config.Items.ToArray())
                    {
                        using (ImRaii.PushId((int)baseId))
                        {
                            if (ImGui.SmallButton("移除"))
                            {
                                Config.Items.Remove(baseId);
                                Plugin.Instance.Config.Save();
                            }
                        }

                        ImGui.SameLine();
                        ImGui.TextUnformatted(ItemNames.Get(baseId));
                    }
                }
            }
        }

        // 搜尋新增。
        ImGui.SetNextItemWidth(260f);
        if (ImGui.InputTextWithHint("##DiscardSearch", "搜尋道具名稱以新增…", ref itemSearchInput, 100))
            RecomputeSearch();

        if (itemSearchResults.Count > 0)
        {
            using var child = ImRaii.Child("DiscardSearchResults", new Vector2(-1f, 140f), true);
            if (child)
            {
                foreach (var item in itemSearchResults)
                {
                    if (Config.Items.Contains(item.RowId)) continue;

                    using (ImRaii.PushId((int)item.RowId))
                    {
                        if (ImGui.SmallButton("新增"))
                        {
                            Config.Items.Add(item.RowId);
                            Plugin.Instance.Config.Save();
                        }
                    }

                    ImGui.SameLine();
                    ImGui.TextUnformatted($"{item.Name.ExtractText()}  (#{item.RowId})");
                }
            }
        }
        else if (itemSearchInput.Trim().Length >= 2)
        {
            ImGui.TextDisabled("沒有符合的道具。");
        }
    }

    private void RecomputeSearch()
    {
        var q = itemSearchInput.Trim();
        if (q.Length < 2)
        {
            itemSearchResults = [];
            return;
        }

        var sheet = Svc.Data.GetExcelSheet<Item>();
        if (sheet == null)
        {
            itemSearchResults = [];
            return;
        }

        itemSearchResults = sheet
            .Where(x =>
            {
                var name = x.Name.ExtractText();
                if (string.IsNullOrEmpty(name)) return false;
                // 略過水晶（3）與金錢（4）這類根本不能丟的排序分類，減少雜訊。
                if (x.ItemSortCategory.RowId is 3 or 4) return false;
                return name.Contains(q, StringComparison.OrdinalIgnoreCase);
            })
            .Take(50)
            .ToList();
    }

    private void DrawInventoryMatches()
    {
        var busy = queue.IsBusy;

        List<Match> matches;
        try
        {
            matches = ScanMatches();
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, $"[{InternalName}] 掃描背包失敗");
            matches = [];
        }

        var inventoryOpen = TryGetActiveInventoryAddonId(out _);

        ImGui.TextUnformatted($"背包裡符合的道具：{matches.Count} 件");
        if (!inventoryOpen)
            ImGui.TextDisabled("（發起丟棄前請先在遊戲裡打開背包視窗。）");

        using (ImRaii.Disabled(busy || !inventoryOpen || matches.Count == 0 || discardLabel.Length == 0))
        {
            // 整批：更謹慎，要按住 Ctrl 才會啟動（每一件仍然會跳確認框讓你按）。
            var ctrl = ImGui.GetIO().KeyCtrl;
            if (ImGui.Button(ctrl ? "全部逐一丟棄" : "全部逐一丟棄（按住 Ctrl）") && ctrl)
                StartDiscarding(matches);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "把上面所有符合的道具排進佇列，逐一叫出遊戲的「捨棄」確認框。\n" +
                "每一件都要你自己在確認框上按「是」才會真的丟掉，下一件會等你回應完上一件才發起。");

        ImGui.SameLine();
        using (ImRaii.Disabled(!busy))
        {
            if (ImGui.Button("停止"))
            {
                queue.Abort();
                pendingDiscards.Clear();
                lastResult = $"已手動停止（已對 {initiatedCount} 件叫出確認框）";
                Svc.Log.Information($"[{InternalName}] 使用者手動停止。已對 {initiatedCount} 件叫出確認框。");
            }
        }

        if (busy)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"執行中：{queue.CurrentStep}");
        }
        else if (lastResult.Length > 0)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(lastResult);
        }

        // 逐件列出，各自一顆「丟棄」按鈕。
        using var listChild = ImRaii.Child("DiscardMatches", new Vector2(-1f, 160f), true);
        if (!listChild) return;

        for (var i = 0; i < matches.Count; i++)
        {
            var m = matches[i];
            using (ImRaii.PushId(i))
            using (ImRaii.Disabled(busy || !inventoryOpen || discardLabel.Length == 0))
            {
                if (ImGui.SmallButton("丟棄"))
                    StartDiscarding([m]);
            }

            ImGui.SameLine();
            var hq = m.HighQuality ? " (HQ)" : "";
            ImGui.TextUnformatted($"{m.Name}{hq} x{m.Quantity}  [{m.Container}#{m.Slot}]");
        }
    }

    #endregion
}

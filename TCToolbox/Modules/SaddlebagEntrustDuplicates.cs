using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Memory;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using TCToolbox.Core;
using ValueType = FFXIVClientStructs.FFXIV.Component.GUI.ValueType;

namespace TCToolbox.Modules;

/// <summary>
/// 陸行鳥鞍囊：寄放重複道具 —— 把背包裡「鞍囊中已經有同一款」的道具整堆放進鞍囊。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 <b>純手動觸發</b>：開著模組但不去按按鈕，遊戲行為完全不變。
/// 上游 PandorasBox <c>EntrustChocoboDuplicates</c> 是在鞍囊視窗上疊一顆按鈕、按下去就把
/// 整個背包掃一遍並把所有搬移一次排進佇列；這裡改成 TC Toolbox 既有的手動模組慣例
/// （設定面板上的按鈕 ＋ 可隨時停止 ＋ 每一步都重新從活的容器算）。
/// </para>
/// <para>
/// 🔴🔴 <b>搬移路徑刻意沿用 <see cref="AutoInventoryTransfer"/> 那條「點遊戲自己的右鍵選單項目」</b>，
/// <b>不用</b> <c>InventoryManager.MoveItemSlot</c>：
/// <list type="bullet">
/// <item>2026-07-31 實機驗證過鞍囊<b>不走</b>雇員道具命令，而右鍵選單那條是來回驗證過會動的。</item>
/// <item><c>MoveItemSlot</c> 的第 6 個參數 <c>a6</c> 省略＝預設 <c>false</c>＝<b>只改本機、一個封包都不送</b>；
/// 就算補上 <c>a6: true</c>，它對鞍囊<b>沒有實機證據</b>。要換路徑請先實測。</item>
/// </list>
/// </para>
/// <para>
/// 🔴 <b>選單項目一律用台服 <c>Addon</c> 表的字串比對，不用寫死的事件序號。</b>
/// 上游用的是 <c>AgentInventoryContext-&gt;EventIds[e] == 56</c> ＋ <c>Callback.Fire(menu, true, 0, e - 7, …)</c>
/// ——兩個寫死的魔術數字（56 與 -7），台服完全沒有可離線驗證的依據。
/// 這裡改成找標籤等於 <c>Addon</c> 第 881 列的那一項，離線核對過台服 7.20：
/// 881＝「放入陸行鳥鞍囊」、887＝「從陸行鳥鞍囊中取回」、886＝「將指定數量放入陸行鳥鞍囊」。
/// <b>逐字相等</b>比對，所以不會誤觸 886 那個「指定數量」的版本。
/// </para>
/// <para>
/// 🔴 找不到、被收進次選單、或項目是停用狀態時<b>一律不動作</b>並記錄原因（fail-closed）。
/// 那個右鍵選單裡有「捨棄」，寧可整個功能不能用，也不要按到隔壁那一項。
/// </para>
/// </remarks>
public sealed unsafe class SaddlebagEntrustDuplicates : TcModule
{
    public override string InternalName => "SaddlebagEntrustDuplicates";

    public override string DisplayName => "陸行鳥鞍囊：寄放重複道具";

    public override string Description =>
        "手動按鈕：把背包裡「鞍囊中已經有同一款」的道具整堆放進陸行鳥鞍囊。" +
        "需要同時開著鞍囊視窗與背包視窗；一次放一件、可隨時停止。獨占道具與收藏品會跳過。不會自動執行。";

    public override ModuleCategory Category => ModuleCategory.Inventory;

    /// <inheritdoc/>
    /// <remarks>開著不按按鈕＝遊戲行為完全不變，所以是手動觸發。</remarks>
    public override bool IsManualTrigger => true;

    public override bool HasConfigUI => true;

    /// <summary>「放入陸行鳥鞍囊」。台服 7.20 離線核對過（<c>exd-tc/7.20/Addon.csv</c>）。</summary>
    private const uint AddonRowDepositToSaddlebag = 881;

    private static readonly InventoryType[] PlayerBags =
        [InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4];

    /// <summary>四個鞍囊分頁。沒訂閱豪華鞍囊的人後兩個容器不會載入，會自動被跳過。</summary>
    private static readonly InventoryType[] SaddleBags =
    [
        InventoryType.SaddleBag1, InventoryType.SaddleBag2,
        InventoryType.PremiumSaddleBag1, InventoryType.PremiumSaddleBag2,
    ];

    /// <summary>一次執行最多寄放幾件（失控保險絲，不是業務規則）。</summary>
    private const int MaxItemsPerRun = 140;

    /// <summary>回合上限。正常情況一回合處理一件，永遠碰不到。</summary>
    private const int MaxIterations = 500;

    /// <summary>送出寄放之後等伺服器生效的時間上限（毫秒）。</summary>
    /// <remarks>
    /// ⚠️ 期限到了<b>不是</b>整輪失敗，而是「這一格跳過、換下一格」——見等待步驟的說明。
    /// </remarks>
    private const int WaitForDepositMs = 5_000;

    private readonly TaskQueue queue = new() { DefaultTimeoutMs = 15_000 };

    /// <summary>本輪已被跳過的來源格（放不進去、選單找不到…），避免無限重試同一格。</summary>
    private readonly HashSet<Candidate> rejected = [];

    private SaddlebagEntrustDuplicatesConfig Config => Plugin.Instance.Config.SaddlebagEntrust;

    private int movedCount;
    private int iterations;
    private long runStartTick;
    private string lastSummary = string.Empty;

    /// <summary>
    /// 上一步「點選單項目」有沒有真的按下去。
    /// 🔴 沒按下去就<b>不能</b>進等待流程——那會讓每一次「這一格放不進去」都用逾時收場，
    /// 而 <see cref="TaskQueue"/> 的逾時是<b>整條佇列中止</b>，一件失敗就毀掉整輪。
    /// </summary>
    private bool lastFireAccepted;

    /// <summary>面板上顯示的預估件數。⚠️ 算不出來時是 <c>null</c>（畫成「？」），不是 0。</summary>
    private int? previewCount;

    private string previewReason = string.Empty;

    /// <summary>
    /// 一件候選道具。
    /// 🔴 只存<b>純數值</b>，不存 <c>InventoryItem*</c>——每一步之間都會過一個 framework tick。
    /// </summary>
    private readonly record struct Candidate(
        InventoryType Container, int Slot, uint BaseItemId, bool HighQuality);

    protected override void OnEnable()
    {
        ResetRun();
        queue.OnTimeout = step =>
        {
            Svc.Log.Information(
                $"[{InternalName}] 流程在「{step}」逾時中止，本輪已寄放 {movedCount} 件。");
            Svc.Chat.PrintError(
                $"[TC Toolbox] 陸行鳥鞍囊寄放：等待逾時，已停止（本輪已寄放 {movedCount} 件）。");
        };
        Svc.Framework.Update += OnFrameworkUpdate;
    }

    protected override void OnDisable()
    {
        Svc.Framework.Update -= OnFrameworkUpdate;
        ResetRun();
        lastSummary = string.Empty;
        previewCount = null;
        previewReason = string.Empty;
    }

    private void ResetRun()
    {
        queue.Abort();
        rejected.Clear();
        movedCount = 0;
        iterations = 0;
        runStartTick = 0;
        lastFireAccepted = false;
    }

    private void OnFrameworkUpdate(IFramework framework) => queue.Tick();

    // ── 候選挑選 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 鞍囊裡目前有哪些款式。鍵是 (道具ID, 是否優質)，
    /// <see cref="SaddlebagEntrustDuplicatesConfig.MatchQuality"/> 關掉時優質一律當成 false。
    /// </summary>
    private HashSet<(uint ItemId, bool HighQuality)> BuildSaddlebagKeys(InventoryManager* manager)
    {
        var keys = new HashSet<(uint, bool)>();

        foreach (var bag in SaddleBags)
        {
            var container = manager->GetInventoryContainer(bag);
            if (container == null || !container->IsLoaded) continue;

            for (var i = 0; i < container->Size; i++)
            {
                var item = container->GetInventorySlot(i);

                // 🔴 先確認這一格真的有東西才拿 ItemId 去用（上游把順序寫反，靠 row 0 剛好沒炸）。
                if (item == null || item->ItemId == 0) continue;

                var baseItemId = item->GetBaseItemId();
                if (baseItemId == 0) continue;

                var hq = Config.MatchQuality && (item->Flags & InventoryItem.ItemFlags.HighQuality) != 0;
                keys.Add((baseItemId, hq));
            }
        }

        return keys;
    }

    /// <summary>
    /// 掃出背包裡「鞍囊已經有同一款」的道具。順序穩定（容器序 → 格號），出事時可重現。
    /// </summary>
    private List<Candidate> FindCandidates(InventoryManager* manager)
    {
        var result = new List<Candidate>();
        var saddleKeys = BuildSaddlebagKeys(manager);
        if (saddleKeys.Count == 0) return result;

        foreach (var bag in PlayerBags)
        {
            var container = manager->GetInventoryContainer(bag);
            if (container == null || !container->IsLoaded) continue;

            for (var i = 0; i < container->Size; i++)
            {
                var item = container->GetInventorySlot(i);
                if (item == null || item->ItemId == 0) continue;

                var baseItemId = item->GetBaseItemId();
                if (baseItemId == 0) continue;

                // 收藏品各自帶收藏價值，是不能互相取代的東西，也不該被批次搬走。
                if ((item->Flags & InventoryItem.ItemFlags.Collectable) != 0) continue;

                var hq = (item->Flags & InventoryItem.ItemFlags.HighQuality) != 0;
                var key = (baseItemId, Config.MatchQuality && hq);
                if (!saddleKeys.Contains(key)) continue;

                // 獨占道具（IsUnique）只能持有一件，搬進鞍囊沒有意義而且遊戲多半直接拒絕。
                var sheetItem = Svc.Data.GetExcelSheet<Item>().GetRowOrDefault(baseItemId);
                if (sheetItem is { IsUnique: true }) continue;

                result.Add(new Candidate(bag, i, baseItemId, hq));
            }
        }

        return result;
    }

    // ── 執行流程 ─────────────────────────────────────────────────────────────

    private void StartRun()
    {
        if (queue.IsBusy) return;

        ResetRun();
        runStartTick = Environment.TickCount64;
        lastSummary = string.Empty;

        Svc.Log.Information($"[{InternalName}] 使用者手動開始寄放重複道具。");
        EnqueueNext();
    }

    private void EnqueueNext()
    {
        queue.Enqueue("尋找下一件", () =>
        {
            if (++iterations > MaxIterations)
            {
                FinishRun($"已中止：回合數超過上限 {MaxIterations}（流程一直挑到同一件卻放不進去）。");
                return null;
            }

            if (movedCount >= MaxItemsPerRun)
            {
                FinishRun($"已達單次上限（{MaxItemsPerRun} 件）而停止，需要的話請再按一次。");
                return null;
            }

            if (GetBlockedReason() is { } reason)
            {
                FinishRun($"已中止：{reason}");
                return null;
            }

            var manager = InventoryManager.Instance();
            if (manager == null)
            {
                FinishRun("已中止：讀不到背包資料。");
                return null;
            }

            Candidate? pick = null;
            foreach (var candidate in FindCandidates(manager))
            {
                if (rejected.Contains(candidate)) continue;
                pick = candidate;
                break;
            }

            if (pick is not { } target)
            {
                FinishRun(movedCount == 0
                    ? "背包裡沒有鞍囊已經有的同款道具。"
                    : $"完成：已寄放 {movedCount} 件。");
                return null;
            }

            var label = ItemNames.Get(target.BaseItemId, target.HighQuality);
            var quantityBefore = ReadQuantity(manager, target);

            queue.Enqueue($"開啟「{label}」的右鍵選單", () =>
            {
                var agent = AgentInventoryContext.Instance();
                var inventoryAddonId = GetInventoryAddonId();

                if (agent == null || inventoryAddonId == 0)
                {
                    Svc.Log.Information(
                        $"[{InternalName}] 右鍵選單前置未就緒（agent={(agent == null ? "null" : "ok")}、" +
                        $"背包 addon id={inventoryAddonId}），跳過 {target.Container}#{target.Slot}。");
                    rejected.Add(target);
                    return true;
                }

                agent->OpenForItemSlot(target.Container, target.Slot, 0, inventoryAddonId);
                return true;
            }, 5_000);

            // 選單 addon 不是同一幀就建好的，一定要隔一段時間再去讀它。
            queue.EnqueueDelay(250, "等待選單開啟");

            queue.Enqueue($"點選「放入陸行鳥鞍囊」（{label}）", () =>
            {
                var agent = AgentInventoryContext.Instance();
                lastFireAccepted = agent != null &&
                                   TryFireContextMenuEntry(agent, AddonRowDepositToSaddlebag, label);

                if (!lastFireAccepted)
                {
                    // 這一格按不動（選單裡沒有那一項／被停用／收在次選單）。記進 rejected 換下一格，
                    // **不要**讓它掉進等待流程用逾時收場。
                    rejected.Add(target);
                    CloseContextMenu();
                }

                return true;
            }, 5_000);

            // ⚠️ 這一步刻意<b>自己算期限</b>而不是靠 TaskQueue 的逾時：
            //    伺服器拒絕（鞍囊滿了、道具不能放）時我們要跳過這一格繼續跑，
            //    而 TaskQueue 逾時的語意是「整條佇列中止」。
            DateTime? waitUntil = null;
            queue.Enqueue($"等待「{label}」寄放生效", () =>
            {
                if (!lastFireAccepted) return true;

                waitUntil ??= DateTime.UtcNow.AddMilliseconds(WaitForDepositMs);

                var live = InventoryManager.Instance();
                if (live != null)
                {
                    // 成功的長相：那一格已經不是這件道具，或數量變少了。
                    var now = ReadQuantity(live, target);
                    if (now < 0 || (quantityBefore >= 0 && now < quantityBefore))
                    {
                        movedCount++;
                        Svc.Log.Debug(
                            $"[{InternalName}] 已寄放「{label}」：{target.Container}#{target.Slot} " +
                            $"{quantityBefore} → {(now < 0 ? "（已清空）" : now.ToString())}");
                        return true;
                    }
                }

                if (DateTime.UtcNow < waitUntil.Value) return false;

                // 期限到了還沒動＝這一格放不進去（多半是鞍囊滿了或該道具不能放）。
                rejected.Add(target);
                Svc.Log.Information(
                    $"[{InternalName}] 「{label}」（{target.Container}#{target.Slot}）送出後 " +
                    $"{WaitForDepositMs}ms 內數量沒有變化，跳過這一格。" +
                    "常見原因是陸行鳥鞍囊已滿，或這款道具不允許放入。");
                return true;
            }, WaitForDepositMs + 5_000);

            queue.EnqueueDelay(Math.Max(100, Config.StepIntervalMs), "間隔");
            EnqueueNext();
            return true;
        }, 15_000);
    }

    /// <summary>某一格現在的數量；那一格已經不是這件道具就回 <c>-1</c>（＝已經離開了）。</summary>
    private static int ReadQuantity(InventoryManager* manager, in Candidate target)
    {
        var item = manager->GetInventorySlot(target.Container, target.Slot);
        if (item == null || item->ItemId == 0) return -1;
        if (item->GetBaseItemId() != target.BaseItemId) return -1;
        if (((item->Flags & InventoryItem.ItemFlags.HighQuality) != 0) != target.HighQuality) return -1;
        return item->Quantity;
    }

    /// <summary>背包視窗的 addon id（0＝沒開）。三種背包版面都由 agent 自己回報，不必逐個猜名字。</summary>
    private static uint GetInventoryAddonId()
    {
        var agentModule = AgentModule.Instance();
        if (agentModule == null) return 0;

        var agent = agentModule->GetAgentByInternalId(AgentId.Inventory);
        return agent == null ? 0 : agent->GetAddonId();
    }

    /// <summary>
    /// 在道具右鍵選單裡找標籤等於 <c>Addon</c> 某一列的項目並點下去。
    /// </summary>
    /// <remarks>
    /// 📌 作法與索引基準沿用 <see cref="AutoInventoryTransfer"/> 裡實機驗證過的那份：
    /// 選單實際佔用 <c>EventParams[ContexItemStartIndex .. +ContextItemCount]</c>，
    /// callback 要的是<b>相對於起點的序號</b>，不是絕對索引，也不能掃完 98 格再數字串
    /// （那樣會掃到上一次選單的殘留）。
    /// <para>🔴 三道 fail-closed：找不到、在次選單裡、項目停用——都回 <c>false</c> 什麼都不做。</para>
    /// </remarks>
    private bool TryFireContextMenuEntry(AgentInventoryContext* agent, uint addonRowId, string displayName)
    {
        var wanted = Svc.Data.GetExcelSheet<Addon>()?.GetRowOrDefault(addonRowId)?.Text.ExtractText().Trim();
        if (string.IsNullOrEmpty(wanted))
        {
            Svc.Log.Information($"[{InternalName}] 讀不到 Addon#{addonRowId} 的字串，無法比對選單項目。");
            return false;
        }

        var startIndex = Math.Clamp(agent->ContexItemStartIndex, 0, 98);
        var itemCount = Math.Clamp(agent->ContextItemCount, 0, 98 - startIndex);

        var index = -1;
        var labels = new string[itemCount];
        for (var entry = 0; entry < itemCount; entry++)
        {
            var v = agent->EventParams[startIndex + entry];
            if (v.Type is not ValueType.String and not ValueType.ManagedString) continue;

            var ptr = v.String.Value;
            if (ptr == null) continue;

            labels[entry] = MemoryHelper.ReadSeStringNullTerminated((nint)ptr).TextValue.Trim();

            // 🔴 逐字相等，不是 Contains——「將指定數量放入陸行鳥鞍囊」(Addon#886) 含有同樣的字首。
            if (index == -1 && labels[entry] == wanted) index = entry;
        }

        if (index == -1)
        {
            // 選單長相一律記下來（Information 級，使用者的記錄等級會濾掉 DBG）。
            // 「▸」標的是被收進二級指令的項目——那是最可能讓找不到的原因，從錯誤訊息看不出來。
            var dump = new string[itemCount];
            for (var entry = 0; entry < itemCount; entry++)
            {
                var inSubmenu = (agent->ContextItemSubmenuMask & (1u << entry)) != 0;
                dump[entry] = $"{entry}{(inSubmenu ? "▸" : string.Empty)}:{labels[entry]}";
            }

            Svc.Log.Information(
                $"[{InternalName}] 選單裡找不到「{wanted}」，跳過「{displayName}」；" +
                $"選單 {itemCount} 項（起點 EventParams[{startIndex}]，▸＝二級指令）：{string.Join(" | ", dump)}");
            return false;
        }

        if ((agent->ContextItemSubmenuMask & (1u << index)) != 0)
        {
            Svc.Log.Information($"[{InternalName}] 「{wanted}」被收在次選單裡，無法直接觸發，跳過「{displayName}」。");
            return false;
        }

        if (agent->IsContextItemDisabled(index))
        {
            Svc.Log.Information($"[{InternalName}] 「{wanted}」目前是停用狀態，跳過「{displayName}」。");
            return false;
        }

        var addonId = agent->AgentInterface.GetAddonId();
        var addon = addonId == 0
            ? null
            : AtkStage.Instance()->RaptureAtkUnitManager->GetAddonById((ushort)addonId);
        if (addon == null)
        {
            Svc.Log.Information($"[{InternalName}] 取不到右鍵選單 addon，跳過「{displayName}」。");
            return false;
        }

        var values = stackalloc AtkValue[5];
        for (var i = 0; i < 5; i++)
        {
            values[i].Type = ValueType.Int;
            values[i].Int = 0;
        }

        values[1].Int = index;
        addon->FireCallback(5, values, true);
        return true;
    }

    /// <summary>找不到項目時把選單收掉，不要留一個開著的右鍵選單卡在畫面上。</summary>
    private static void CloseContextMenu()
    {
        var agent = AgentInventoryContext.Instance();
        if (agent == null) return;

        var addonId = agent->AgentInterface.GetAddonId();
        if (addonId == 0) return;

        var addon = AtkStage.Instance()->RaptureAtkUnitManager->GetAddonById((ushort)addonId);
        if (addon == null) return;

        agent->AgentInterface.Hide();
        addon->Close(true);
    }

    // ── 狀態與 UI ────────────────────────────────────────────────────────────

    /// <summary>現在不能寄放的原因；<c>null</c>＝可以。</summary>
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

        // 🔴 鞍囊視窗沒開的話，右鍵選單裡根本不會有「放入陸行鳥鞍囊」這一項。
        if (!UiHelper.IsAddonReady("InventoryBuddy"))
            return "請先開啟「陸行鳥鞍囊」視窗。";

        if (GetInventoryAddonId() == 0)
            return "請同時開啟背包視窗（右鍵選單要從背包開）。";

        return null;
    }

    private void RefreshPreview()
    {
        if (GetBlockedReason() is { } reason)
        {
            previewCount = null;
            previewReason = reason;
            return;
        }

        var manager = InventoryManager.Instance();
        if (manager == null)
        {
            previewCount = null;
            previewReason = "讀不到背包資料";
            return;
        }

        previewReason = string.Empty;
        previewCount = FindCandidates(manager).Count;
    }

    public override void DrawConfig()
    {
        // 面板開著時每秒重算一次即可；每幀掃 140＋280 格只是徒增配置。
        if (!queue.IsBusy && Throttle.Pass("SaddlebagEntrustDuplicates-Preview", 1_000)) RefreshPreview();

        // 「不知道」要在列上看得見：算不出來時畫「？」與原因，不畫 0。
        ImGui.AlignTextToFramePadding();
        if (previewCount is { } count)
            ImGui.TextUnformatted($"可寄放的重複道具：{count} 件");
        else
            ImGui.TextDisabled($"可寄放的重複道具：？（{previewReason}）");

        ImGui.Spacing();

        if (queue.IsBusy)
        {
            if (ImGui.Button("停止寄放##saddlebagEntrust"))
                FinishRun($"已由使用者停止（已寄放 {movedCount} 件）。");

            ImGui.SameLine();
            ImGui.TextColored(new Vector4(1f, 0.85f, 0.35f, 1f),
                              $"{queue.CurrentStep}（已寄放 {movedCount} 件）");
        }
        else
        {
            var blocked = previewCount is null or 0;
            if (blocked) ImGui.BeginDisabled();

            var clicked = ImGui.Button("開始寄放##saddlebagEntrust");

            if (blocked) ImGui.EndDisabled();

            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            {
                ImGui.SetTooltip(previewCount is null
                    ? previewReason
                    : "把背包裡「鞍囊已經有同一款」的道具整堆放進鞍囊。\n" +
                      "一次一件，走的是遊戲自己的右鍵選單項目，過程中可以隨時停止。");
            }

            if (clicked && !blocked)
                StartRun();
        }

        ImGui.Separator();

        var matchQuality = Config.MatchQuality;
        if (ImGui.Checkbox("優質與普通品分開算##saddlebagEntrustHq", ref matchQuality))
        {
            Config.MatchQuality = matchQuality;
            Plugin.Instance.Config.Save();
            Throttle.Reset("SaddlebagEntrustDuplicates-Preview");
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "開啟（預設）：鞍囊裡有普通品時，背包的優質品不算重複，不會被放進去。\n" +
                "關閉：只看道具編號，優質與普通品互相視為同一款（＝上游 PandorasBox 的行為）。");
        }

        var interval = Config.StepIntervalMs;
        if (ImGui.SliderInt("每件之間的間隔（毫秒）##saddlebagEntrustInterval", ref interval, 200, 2_000))
        {
            Config.StepIntervalMs = interval;
            Plugin.Instance.Config.Save();
        }

        var notify = Config.NotifyOnFinish;
        if (ImGui.Checkbox("結束時在聊天欄報告##saddlebagEntrustNotify", ref notify))
        {
            Config.NotifyOnFinish = notify;
            Plugin.Instance.Config.Save();
        }

        if (lastSummary.Length > 0)
            ImGui.TextDisabled($"上次結果：{lastSummary}");

        ImGui.TextColored(new Vector4(1f, 0.8f, 0.35f, 1f),
                          "⚠ 需要同時開著「陸行鳥鞍囊」與背包視窗；獨占道具與收藏品一律跳過。");
    }

    private void FinishRun(string summary)
    {
        queue.Abort();

        var elapsed = runStartTick == 0 ? 0 : Environment.TickCount64 - runStartTick;
        lastSummary = summary;

        Svc.Log.Information($"[{InternalName}] {summary} 共寄放 {movedCount} 件、耗時 {elapsed}ms");

        if (Config.NotifyOnFinish)
            Svc.Chat.Print($"[TC Toolbox] 陸行鳥鞍囊寄放：{summary}");

        rejected.Clear();
        movedCount = 0;
        iterations = 0;
        runStartTick = 0;
        lastFireAccepted = false;
        Throttle.Reset("SaddlebagEntrustDuplicates-Preview");
    }
}

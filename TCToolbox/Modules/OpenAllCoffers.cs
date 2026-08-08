using System;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 在可整疊開啟的箱類道具右鍵選單補上「全部開啟」，一件一件開到完。
/// </summary>
/// <remarks>
/// <para>
/// 🔴🔴 <b>不能照抄上游的 <c>ItemAction.RowId is 388 or 367 or 2462</c>。</b>
/// 台服 7.20 離線比對 <c>exd-tc/7.20/Item.csv</c> 的實數：
/// <list type="bullet">
/// <item><c>ItemAction</c>＝388 共 488 件，其中 <b>470 件是箱子</b>（<c>ItemUICategory</c>＝61 雜貨），
/// 但另外 <b>18 件是藥品</b>（<c>ItemUICategory</c>＝44）——禦火藥／禦冰藥／禦風藥／禦土藥那一系列。</item>
/// <item>更糟的是<b>疊放數的分佈剛好相反</b>：那 470 個箱子絕大多數 <c>StackSize</c>＝1，
/// 過不了「可疊放才顯示」這一關；而 18 個藥品全部是 999。
/// 也就是說上游的判斷式在台服實際命中的 52 件裡，<b>有 18 件（35%）是藥水</b>，
/// 按下「全部開啟」的結果是<b>整疊灌下去</b>。</item>
/// </list>
/// 所以這裡多一道 <c>ItemUICategory != 44</c>（藥品）。
/// </para>
/// <para>
/// ⚠️⚠️ <b>這是上游本來就有的缺陷，不是台服的資料差異。</b>特別寫清楚是為了防止未來同步上游時
/// 有人「發現我們多了一個條件」而把它拿掉。<c>ItemAction</c> 388 在國際服同樣混著這批藥品，
/// 只是沒有人回報過而已。<b>要改這個判斷式之前，先重跑一次上面那個統計。</b>
/// </para>
/// <para>
/// 📌 開啟的方式走 <c>ActionManager.UseAction(ActionType.Item, ...)</c>（艦隊先例：Artisan 的
/// <c>ActionManagerEx.UseItem</c>），<b>不</b>照抄上游那套「開右鍵選單再 FireCallback 第 0 項」——
/// 那假設選單第一項一定是「使用」，而選單內容會隨道具與情境變動，點錯一項的代價太高。
/// </para>
/// <para>🔴 逐件之間有間隔，而且每一件都重新確認狀態；隨時可以按停止。</para>
/// </remarks>
public sealed unsafe class OpenAllCoffers : TcModule
{
    public override string InternalName => "OpenAllCoffers";

    public override string DisplayName => "箱類「全部開啟」";

    public override string Description =>
        "在可整疊開啟的箱子右鍵選單補上「全部開啟」，一件一件開到整疊開完。" +
        "逐件之間有間隔，背包快滿或狀態不允許時會自動停下。藥品不會出現這個選項。";

    public override ModuleCategory Category => ModuleCategory.Inventory;

    /// <inheritdoc/>
    /// <remarks>開著但不去點那個選單項＝遊戲行為完全不變。</remarks>
    public override bool IsManualTrigger => true;

    public override bool HasConfigUI => true;

    /// <summary>
    /// 「可整疊開啟」的 <c>ItemAction</c> 列號。
    /// </summary>
    /// <remarks>
    /// 台服 7.20 實查（僅列 <c>StackSize</c>&gt;1、也就是真的會出現這個選單項的）：
    /// <list type="bullet">
    /// <item>367 → 九宮幻卡卡包 19 件（雜貨）＋ 季節活動彩蛋 9 件</item>
    /// <item>388 → 禮物盒／僱員的寶箱／特殊配給貨箱等 5 件（<b>另有 18 件藥品，靠
    /// <see cref="PotionCategory"/> 擋掉</b>）</item>
    /// <item>2462 → 慶典禮物箱 1 件</item>
    /// </list>
    /// </remarks>
    private static readonly uint[] OpenableItemActions = [367u, 388u, 2462u];

    /// <summary>
    /// 藥品的 <c>ItemUICategory</c> 列號（台服 7.20 實查：44 ＝「藥品」）。
    /// </summary>
    /// <remarks>
    /// 🔴 這一行就是上游 bug 的修補點，理由見型別註解。移除它會讓「全部開啟」出現在
    /// 禦火藥那一系列上，而按下去是把整疊藥水喝掉。
    /// </remarks>
    private const uint PotionCategory = 44;

    /// <summary>
    /// 連續幾次「叫了但數量沒減少」就中止。
    /// </summary>
    /// <remarks>
    /// <c>UseAction</c> 回傳 true 不代表真的開成功（伺服器可能拒絕）。
    /// 用「數量有沒有變少」當唯一判準，因為那是唯一不會騙人的訊號。
    /// </remarks>
    private const int StuckAbortThreshold = 5;

    /// <summary>低於這個背包空格數就停手。</summary>
    /// <remarks>
    /// 📌 箱子開出來的東西要有地方放。留 2 格餘裕是因為有些箱子一次給多件，
    /// 而「背包滿了」的失敗在遊戲裡只是一則錯誤訊息，很容易連續撞好幾次都沒人注意。
    /// </remarks>
    private const uint MinimumFreeSlots = 2;

    private OpenAllCoffersConfig Config => Plugin.Instance.Config.OpenAllCoffers;

    private bool running;
    private uint targetItemId;
    private string targetName = string.Empty;
    private int openedCount;
    private int stuckCount;
    private int lastRemaining;
    private string lastSummary = string.Empty;

    protected override void OnEnable()
    {
        ResetRun();
        lastSummary = string.Empty;

        Svc.ContextMenu.OnMenuOpened += OnMenuOpened;
        Svc.Framework.Update += OnFrameworkUpdate;
    }

    protected override void OnDisable()
    {
        Svc.Framework.Update -= OnFrameworkUpdate;
        Svc.ContextMenu.OnMenuOpened -= OnMenuOpened;

        ResetRun();
        lastSummary = string.Empty;
    }

    private void ResetRun()
    {
        running = false;
        targetItemId = 0;
        targetName = string.Empty;
        openedCount = 0;
        stuckCount = 0;
        lastRemaining = -1;
    }

    private void OnMenuOpened(IMenuOpenedArgs args)
    {
        try
        {
            if (args.MenuType != ContextMenuType.Inventory) return;
            if (args.Target is not MenuTargetInventory inv || inv.TargetItem is not { } item) return;

            var itemId = item.ItemId;
            if (!IsOpenableStack(itemId, out var name)) return;

            args.AddMenuItem(new MenuItem
            {
                Name = "全部開啟",
                PrefixChar = 'T',
                PrefixColor = 539,
                Priority = 0,
                OnClicked = _ => StartRun(itemId, name),
            });
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, $"[{InternalName}] 建立右鍵選單項目時發生例外（addon: {args.AddonName}）");
        }
    }

    /// <summary>
    /// 這件道具該不該出現「全部開啟」。
    /// </summary>
    /// <remarks>
    /// 三個條件缺一不可：可疊放（不然「全部」沒有意義）、<c>ItemAction</c> 在白名單裡、
    /// <b>而且不是藥品</b>（見 <see cref="PotionCategory"/>）。
    /// </remarks>
    private static bool IsOpenableStack(uint itemId, out string name)
    {
        name = string.Empty;
        if (itemId == 0) return false;

        var row = Svc.Data.GetExcelSheet<Item>().GetRowOrDefault(itemId);
        if (row == null) return false;

        var item = row.Value;

        // 只有可疊放的才談得上「全部開啟」。
        if (item.StackSize <= 1) return false;

        if (Array.IndexOf(OpenableItemActions, item.ItemAction.RowId) < 0) return false;

        // 🔴 上游缺這一道，台服會讓 18 件禦X藥系列冒出「全部開啟」。
        if (item.ItemUICategory.RowId == PotionCategory) return false;

        var itemName = item.Name.ExtractText();

        // Item 表 row 0 是有效列但名稱為空，所以不能只判斷查不查得到。
        if (string.IsNullOrEmpty(itemName)) return false;

        name = itemName;
        return true;
    }

    private void StartRun(uint itemId, string name)
    {
        ResetRun();

        running = true;
        targetItemId = itemId;
        targetName = name;
        lastSummary = string.Empty;

        // 讓第一件立刻開始，不必先等一個間隔。
        Throttle.Reset("OpenAllCoffers-Step");

        // 使用者回報用的定錨點：證明這一串開啟是他自己按的、對象是哪一款道具。
        Svc.Log.Information($"[{InternalName}] 使用者手動開始全部開啟「{name}」（itemId={itemId}）。");
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (!running) return;

        if (!Throttle.Pass("OpenAllCoffers-Step", Math.Max(200, Config.StepIntervalMs))) return;

        if (GetBlockedReason() is { } reason)
        {
            FinishRun($"已停止：{reason}");
            return;
        }

        var inventory = InventoryManager.Instance();
        var actionManager = ActionManager.Instance();
        if (inventory == null || actionManager == null)
        {
            FinishRun("已停止：讀不到遊戲狀態。");
            return;
        }

        var remaining = inventory->GetInventoryItemCount(targetItemId);
        if (remaining <= 0)
        {
            FinishRun($"完成：開了 {openedCount} 個「{targetName}」。");
            return;
        }

        // 背包要有地方放開出來的東西。
        var freeSlots = inventory->GetEmptySlotsInBag();
        if (freeSlots < MinimumFreeSlots)
        {
            FinishRun($"已停止：背包空格不足（剩 {freeSlots} 格），已開 {openedCount} 個。");
            return;
        }

        // 還在讀條或動畫鎖裡就等下一輪，不算失敗。
        if (Svc.Condition[ConditionFlag.Casting]) return;
        if (actionManager->AnimationLock > 0f) return;

        // 遊戲說現在不能用這件道具（冷卻、狀態不符…）→ 等，連續太多次才放棄。
        if (actionManager->GetActionStatus(ActionType.Item, targetItemId) != 0)
        {
            stuckCount++;
            if (stuckCount >= StuckAbortThreshold)
                FinishRun($"已停止：遊戲目前不允許使用「{targetName}」，已開 {openedCount} 個。");
            return;
        }

        // 數量有沒有真的變少，是唯一不會騙人的成功判準（UseAction 回 true 不代表伺服器受理）。
        if (lastRemaining >= 0)
        {
            if (remaining < lastRemaining)
            {
                openedCount += lastRemaining - remaining;
                stuckCount = 0;
            }
            else
            {
                stuckCount++;
                if (stuckCount >= StuckAbortThreshold)
                {
                    FinishRun($"已停止：連續 {StuckAbortThreshold} 次沒有開成功，已開 {openedCount} 個。");
                    return;
                }
            }
        }

        lastRemaining = remaining;

        // extraParam: 65535 ＝「從背包裡挑一件」，照艦隊先例（Artisan ActionManagerEx.UseItem）。
        actionManager->UseAction(ActionType.Item, targetItemId, extraParam: 65535);
    }

    /// <summary>現在不能繼續開的原因；<c>null</c>＝可以。</summary>
    private static string? GetBlockedReason()
    {
        if (Svc.Objects.LocalPlayer == null)
            return "目前不在遊戲中。";

        if (Svc.Condition[ConditionFlag.BetweenAreas] || Svc.Condition[ConditionFlag.BetweenAreas51])
            return "正在讀取地圖。";

        if (Svc.Condition[ConditionFlag.WatchingCutscene] ||
            Svc.Condition[ConditionFlag.OccupiedInCutSceneEvent])
            return "正在播放過場動畫。";

        if (Svc.Condition[ConditionFlag.OccupiedInQuestEvent] || Svc.Condition[ConditionFlag.Occupied33])
            return "正在進行中的事件裡。";

        if (Svc.Condition[ConditionFlag.InCombat])
            return "戰鬥中。";

        return null;
    }

    private void FinishRun(string summary)
    {
        lastSummary = summary;

        Svc.Log.Information($"[{InternalName}] {summary}（itemId={targetItemId}）");

        if (Config.NotifyOnFinish)
            Svc.Chat.Print($"[TC Toolbox] {summary}");

        ResetRun();
    }

    public override void DrawConfig()
    {
        if (running)
        {
            if (ImGui.Button("停止"))
                FinishRun($"已由使用者停止（已開 {openedCount} 個「{targetName}」）。");

            ImGui.SameLine();
            ImGui.TextColored(new System.Numerics.Vector4(1f, 0.85f, 0.35f, 1f),
                $"開啟中…「{targetName}」已開 {openedCount} 個");
        }
        else
        {
            ImGui.TextDisabled("在背包裡對可整疊開啟的箱子按右鍵，選單裡會多一項「全部開啟」。");
        }

        ImGui.Separator();

        var interval = Config.StepIntervalMs;
        if (ImGui.SliderInt("每件之間的間隔（毫秒）##openAllCoffersInterval", ref interval, 200, 2_000))
        {
            Config.StepIntervalMs = interval;
            Plugin.Instance.Config.Save();
        }

        var notify = Config.NotifyOnFinish;
        if (ImGui.Checkbox("結束時在聊天欄報告##openAllCoffersNotify", ref notify))
        {
            Config.NotifyOnFinish = notify;
            Plugin.Instance.Config.Save();
        }

        if (lastSummary.Length > 0)
            ImGui.TextDisabled($"上次結果：{lastSummary}");

        ImGui.TextDisabled("藥品不會出現這個選項（上游的判斷式會把禦火藥那一系列一起算進來）。");
        ImGui.TextDisabled($"背包空格少於 {MinimumFreeSlots} 格、進入戰鬥或過場時會自動停下。");
    }
}

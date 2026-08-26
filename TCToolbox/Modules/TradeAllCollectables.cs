using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 收藏品一鍵全交：在「收藏品交易」視窗開著時，連續按遊戲自己的「交易」鈕，
/// 把目前分頁裡所有可交易的收藏品一次交完。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 <b>純手動觸發</b>：開著模組但不去按按鈕，遊戲行為完全不變。刻意<b>不</b>做成
/// 「開啟視窗就自動交易」——收藏品交易是不可逆的道具消耗，必須由使用者當下按下才開始。
/// </para>
/// <para>
/// 📌 <b>怎麼按下「交易」</b>：取 <c>CollectablesShop</c> 上 <b>node id 51</b> 的按鈕元件，
/// 然後<b>重播那顆按鈕自己的事件</b>（<see cref="UiHelper.ClickButton"/>）。
/// 這條路徑刻意<b>不用</b>寫死的 callback 序號，也<b>不用</b>特徵碼：
/// <list type="bullet">
/// <item>上游 PandorasBox <c>TradeAllCollectibles</c> 用的是
/// <c>Callback.Fire(addon, false, 15, 0u)</c>——15 是寫死的事件序號，台服沒有任何可離線驗證的依據，
/// 而且它還把原生的交易鈕藏起來換成自己的 ImGui 按鈕（<c>NodeList[2]</c>，那是<b>索引</b>不是 node id）。</item>
/// <item>DailyRoutines <c>AutoCollectableExchange</c> 用的是特徵碼掃出來的
/// <c>HandInCollectables(agent)</c>——一樣是台服未驗證的寫死位元組樣式。</item>
/// </list>
/// node id 51 則有<b>兩個互相獨立的來源</b>指向同一個東西：ECommons 的
/// <c>AddonMaster.CollectablesShop.TradeButton =&gt; GetComponentButtonById(51)</c>，
/// 以及 DailyRoutines 對同一個 addon 取 <c>GetNodeById(51u)</c> 當「交易」鈕來隱藏。
/// 台服 <c>Addon</c> 表第 531 列的字串正是「交易」（離線核對 <c>exd-tc/7.20/Addon.csv</c>），
/// 與 DR 拿來當按鈕標籤的那一列一致。
/// <para>
/// ⚠️ 即使如此，node id 仍然是「下次改版可能失效」的東西，所以解析不到時<b>整個功能停用並明講</b>
/// （<see cref="GetBlockedReason"/>），不會安靜地什麼都不做。
/// </para>
/// </para>
/// <para>
/// 🔴 <b>不自動確認任何對話框。</b>流程中只要出現 <c>SelectYesno</c> 就立刻停下並提示使用者，
/// 絕不代按「是」——收藏品交易一旦有確認框，那個框問的內容我們無法離線證明。
/// </para>
/// </remarks>
public sealed unsafe class TradeAllCollectables : TcModule
{
    public override string InternalName => "TradeAllCollectables";

    public override string DisplayName => "收藏品一鍵全交";

    public override string Description =>
        "手動按鈕：「收藏品交易」視窗開著時，連續按遊戲自己的「交易」鈕，把目前分頁裡的收藏品全部交完。" +
        "一次交一件、可隨時停止；跳出確認框就會停下來讓你自己看。不會自動執行。";

    public override ModuleCategory Category => ModuleCategory.Inventory;

    /// <inheritdoc/>
    /// <remarks>開著不按按鈕＝遊戲行為完全不變，所以是手動觸發。</remarks>
    public override bool IsManualTrigger => true;

    public override bool HasConfigUI => true;

    private const string ShopAddon = "CollectablesShop";

    /// <summary>「交易」按鈕的 node id。見型別註解的兩個獨立來源。</summary>
    private const uint TradeButtonNodeId = 51;

    /// <summary>
    /// 一次執行最多交幾件。
    /// </summary>
    /// <remarks>
    /// 🔴 這是<b>失控保險絲</b>不是業務規則：背包四頁總共 140 格，全部塞滿收藏品也只有 140 件。
    /// 真的撞到上限代表「按了但狀態沒變」我們沒偵測到，寧可停下讓使用者再按一次。
    /// </remarks>
    private const int MaxTradesPerRun = 200;

    /// <summary>連續幾次「按了但背包裡的收藏品數量沒變」就中止。</summary>
    /// <remarks>
    /// 🔑 這是本模組唯一不依賴 node id 的判準：交易成功一定會讓背包裡少一件收藏品。
    /// 完全沒變代表那一下按不動（或按到的根本不是交易鈕），再按下去只是空轉。
    /// </remarks>
    private const int StuckAbortThreshold = 3;

    private static readonly InventoryType[] PlayerBags =
        [InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4];

    private TradeAllCollectablesConfig Config => Plugin.Instance.Config.TradeAllCollectables;

    private bool running;
    private int tradesDone;
    private int stuckCount;
    private int lastCollectableCount = -1;
    private long runStartTick;
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
        tradesDone = 0;
        stuckCount = 0;
        lastCollectableCount = -1;
        runStartTick = 0;
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (!running) return;

        // 每一步之間留間隔：交易是真的送到伺服器的請求，太快沒有好處，
        // 使用者也需要來得及按「停止」。
        if (!Throttle.Pass("TradeAllCollectables-Step", Math.Max(100, Config.StepIntervalMs))) return;

        if (GetBlockedReason() is { } reason)
        {
            FinishRun($"已中止：{reason}");
            return;
        }

        if (tradesDone >= MaxTradesPerRun)
        {
            FinishRun($"已達單次上限（{MaxTradesPerRun} 件）而停止，需要的話請再按一次。");
            return;
        }

        var addon = UiHelper.GetAddon(ShopAddon);
        if (!UiHelper.IsReady(addon))
        {
            FinishRun("已中止：「收藏品交易」視窗已關閉。");
            return;
        }

        var button = addon->GetComponentButtonById(TradeButtonNodeId);

        // 🔴 先確認 OwnerNode 非 null 再問 IsEnabled —— CS 的 IsEnabled 直接解參考
        //    AtkComponentBase.OwnerNode，沒有任何判空。順序寫反就是自找的存取違規。
        if (button == null || button->AtkComponentBase.OwnerNode == null)
        {
            FinishRun($"已中止：找不到「交易」按鈕（node id {TradeButtonNodeId}）。");
            Svc.Log.Information(
                $"[{InternalName}] CollectablesShop 上取不到 node id {TradeButtonNodeId} 的按鈕元件" +
                $"（button={(button == null ? "null" : "OwnerNode=null")}）——台服的 node id 可能與參考來源不同，請回報。");
            return;
        }

        if (!button->IsEnabled)
        {
            FinishRun(tradesDone == 0
                ? "目前沒有可以交易的收藏品（「交易」鈕是停用狀態）。"
                : $"完成：已交出 {tradesDone} 件。");
            return;
        }

        // 交易前先記下背包裡的收藏品件數，交易後拿它判斷這一下到底有沒有生效。
        var before = CountCollectables();

        if (!UiHelper.ClickButton(addon, button))
        {
            FinishRun("已中止：「交易」按鈕目前按不動。");
            return;
        }

        tradesDone++;

        // ⚠️ 這裡不能馬上比對 before/after —— 交易是送給伺服器的請求，背包不會在同一幀更新。
        //    改成拿「上一輪按下之前的件數」跟「這一輪按下之前的件數」比：
        //    中間隔了一整個 StepIntervalMs，伺服器來得及回。
        if (lastCollectableCount >= 0 && before >= lastCollectableCount)
        {
            stuckCount++;
            Svc.Log.Information(
                $"[{InternalName}] 按下交易後背包收藏品件數沒有減少（第 {stuckCount} 次）：" +
                $"上一輪 {lastCollectableCount} → 這一輪 {before}。");

            if (stuckCount >= StuckAbortThreshold)
            {
                FinishRun($"已中止：連續 {StuckAbortThreshold} 次按下交易都沒有生效。");
                return;
            }
        }
        else
        {
            stuckCount = 0;
        }

        lastCollectableCount = before;
    }

    /// <summary>背包第 1～4 頁裡帶「收藏品」旗標的道具件數。</summary>
    /// <remarks>
    /// ⚠️ 這是<b>件數</b>不是堆疊數：收藏品每件各自帶收藏價值，遊戲本來就不讓它們疊在一起。
    /// 讀不到 <c>InventoryManager</c> 時回 <c>-1</c>（而不是 0）——
    /// 「不知道」跟「真的沒有」必須分得開，否則卡住判斷會誤觸發。
    /// </remarks>
    private static int CountCollectables()
    {
        var manager = InventoryManager.Instance();
        if (manager == null) return -1;

        var count = 0;
        foreach (var bag in PlayerBags)
        {
            var container = manager->GetInventoryContainer(bag);
            if (container == null || !container->IsLoaded) continue;

            for (var i = 0; i < container->Size; i++)
            {
                var item = container->GetInventorySlot(i);
                if (item == null || item->ItemId == 0) continue;
                if ((item->Flags & InventoryItem.ItemFlags.Collectable) != 0) count++;
            }
        }

        return count;
    }

    /// <summary>現在不能交易的原因；<c>null</c>＝可以。</summary>
    private static string? GetBlockedReason()
    {
        if (Svc.Objects.LocalPlayer == null)
            return "目前不在遊戲中。";

        if (Svc.Condition[ConditionFlag.BetweenAreas] || Svc.Condition[ConditionFlag.BetweenAreas51])
            return "正在讀取地圖。";

        if (Svc.Condition[ConditionFlag.WatchingCutscene] ||
            Svc.Condition[ConditionFlag.OccupiedInCutSceneEvent])
            return "正在播放過場動畫。";

        // 🔴 出現任何確認框就停手。我們無法離線證明那個框問的是什麼，
        //    而代按「是」的代價是不可逆的道具操作。
        if (UiHelper.IsAddonReady("SelectYesno"))
            return "跳出了確認框，請自行確認後再按一次。";

        if (!UiHelper.IsAddonReady(ShopAddon))
            return "請先開啟「收藏品交易」視窗。";

        return null;
    }

    public override void DrawConfig()
    {
        var blockedReason = GetBlockedReason();

        if (running)
        {
            if (ImGui.Button("停止交易##tradeAllCollectables"))
                FinishRun($"已由使用者停止（已交出 {tradesDone} 件）。");

            ImGui.SameLine();
            ImGui.TextColored(new Vector4(1f, 0.85f, 0.35f, 1f), $"交易中… 已交出 {tradesDone} 件");
        }
        else
        {
            var blocked = blockedReason != null;
            if (blocked) ImGui.BeginDisabled();

            var clicked = ImGui.Button("開始全部交易##tradeAllCollectables");

            if (blocked) ImGui.EndDisabled();

            // ⚠️ 停用中的項目預設不回報 hover，要 AllowWhenDisabled 才問得到，
            //    否則「按鈕灰掉又沒有說明」就是純粹的靜默失敗。
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            {
                ImGui.SetTooltip(blockedReason ??
                    "連續按下「收藏品交易」視窗自己的「交易」鈕，直到那顆鈕變成停用為止。\n" +
                    "只處理目前選中的職業分頁——換分頁請自己切，再按一次。");
            }

            if (clicked && !blocked)
                StartRun();
        }

        ImGui.Separator();

        var interval = Config.StepIntervalMs;
        if (ImGui.SliderInt("每件之間的間隔（毫秒）##tradeAllCollectablesInterval", ref interval, 200, 2_000))
        {
            Config.StepIntervalMs = interval;
            Plugin.Instance.Config.Save();
        }

        var notify = Config.NotifyOnFinish;
        if (ImGui.Checkbox("結束時在聊天欄報告##tradeAllCollectablesNotify", ref notify))
        {
            Config.NotifyOnFinish = notify;
            Plugin.Instance.Config.Save();
        }

        if (lastSummary.Length > 0)
            ImGui.TextDisabled($"上次結果：{lastSummary}");

        ImGui.TextColored(new Vector4(1f, 0.8f, 0.35f, 1f),
                          "⚠ 只交出目前分頁（職業）的收藏品，而且會交完為止——確定分頁對了再按。");
    }

    private void StartRun()
    {
        ResetRun();
        running = true;
        runStartTick = Environment.TickCount64;
        lastSummary = string.Empty;

        // 讓第一步立刻執行，不必先等一個間隔。
        Throttle.Reset("TradeAllCollectables-Step");

        // 使用者回報用的定錨點：這一行是唯一能證明「這串交易是他自己按的」的證據。
        Svc.Log.Information(
            $"[{InternalName}] 使用者手動開始一鍵全交，背包目前收藏品 {CountCollectables()} 件。");
    }

    private void FinishRun(string summary)
    {
        var elapsed = runStartTick == 0 ? 0 : Environment.TickCount64 - runStartTick;
        lastSummary = summary;

        Svc.Log.Information($"[{InternalName}] {summary} 共交出 {tradesDone} 件、耗時 {elapsed}ms");

        if (Config.NotifyOnFinish)
            Svc.Chat.Print($"[TC Toolbox] 收藏品一鍵全交：{summary}");

        ResetRun();
    }
}

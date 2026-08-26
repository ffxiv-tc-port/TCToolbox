using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using TCToolbox.Core;
using ValueType = FFXIVClientStructs.FFXIV.Component.GUI.ValueType;

namespace TCToolbox.Modules;

/// <summary>
/// 部隊合建（工房專案／潛水艇零件）素材一鍵全交。
/// 機制：解析 SubmarinePartsMenu 的 AtkValues 取得可交納素材，逐項對
/// AgentId.CompanyCraftMaterial 送出合成事件，再自動填入並送出 Request 交納視窗，零 hook。
/// 參考 DailyRoutines AutoFCWSDeliver 設計重寫（API13、自帶 Request 填交，不依賴其他模組）。
/// </summary>
public sealed unsafe class AutoFCWSDeliver : TcModule
{
    public override string InternalName => "AutoFCWSDeliver";
    public override string DisplayName => "合建素材一鍵全交";
    public override string Description => "開啟部隊工房「合建」素材交納視窗時，顯示一鍵全交按鈕：自動逐項交納所有數量足夠的素材（含自動填入交納視窗與確認），交完或按停止為止。";

    public override ModuleCategory Category => ModuleCategory.Company;

    /// <summary>合建視窗上按了「開始」才跑；開著不按，一件素材都不會交出去。</summary>
    public override bool IsManualTrigger => true;

    private readonly TaskQueue queue = new();

    private int fillSlotCursor;
    private int deliveredCount;

    /// <summary>合建視窗最後一次 Finalize 的時刻；遊戲交納後會重建它，短暫消失不代表使用者關了視窗。</summary>
    private DateTime? menuGoneSince;

    /// <summary>視窗重建的容忍時間。超過就當作真的被關掉。</summary>
    private const double MenuRebuildGraceSeconds = 3;

    /// <summary>
    /// 這一項的 <c>Request</c> 交納視窗有沒有真的出現過。
    /// </summary>
    /// <remarks>
    /// 🔴 沒有這個旗標的話 <see cref="ConfirmRequest"/> 會把「視窗從頭到尾沒出現」也算成交納成功
    /// ——overlay 上「已交 N 項」持續攀升而實際上一項都沒交出去。
    /// </remarks>
    private bool requestSeen;

    /// <summary>
    /// 上一輪解析結果的指紋（首項 Index ＋ 清單長度），用來偵測「零進展」。
    /// </summary>
    private (uint Index, int Count)? lastParseFingerprint;

    /// <summary>連續幾輪解析結果完全相同了。</summary>
    private int noProgressRounds;

    /// <summary>這一趟排進佇列的項數（<see cref="MaxItemsPerRun"/> 的計數）。</summary>
    private int enqueuedItems;

    /// <summary>
    /// 連續幾輪解析結果完全相同就判定「交納沒有生效」並中止。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>這整條批次迴圈原本沒有任何無進展保險絲。</b>解析步驟會無條件遞迴重排下一輪，
    /// 而送出 agent 事件被台服靜默拒絕（台服的拒絕常常完全沒有徵兆）或 <c>Request</c> 視窗
    /// 因任何原因沒開時，<see cref="FillRequest"/> 把「視窗沒出現」當成「已完成」回 true、
    /// <see cref="ConfirmRequest"/> 接著把它計成已交納，下一輪又解析到同一批素材
    /// ⇒ 每約 0.7 秒重送一次 agent 事件，永不自停。
    /// 逾時保底管不到：每一步都很快就「完成」了。
    /// 📌 同類模組（AutoMerge／OpenAllCoffers／TradeAllCollectables）本來就都有這兩道保險絲。
    /// </remarks>
    private const int MaxNoProgressRounds = 3;

    /// <summary>單趟交納的項數上限（合建素材實際上限約數十項，這個數綽綽有餘）。</summary>
    private const int MaxItemsPerRun = 100;

    private sealed record DeliverItem(uint Index, uint ItemId, uint Count);

    /// <summary>
    /// 取得工房領地，兩種失效都擋掉。
    /// <c>HousingManager.Instance()</c> 是 <c>[StaticAddress(..., isPointer: true)]</c>：
    /// 特徵碼失效時擲 <see cref="InvalidOperationException"/>，單例尚未建立時回 null——
    /// 後者直接解參考就是 AccessViolation，try/catch 攔不到，所以兩道都要。
    /// </summary>
    private static WorkshopTerritory* GetWorkshopTerritory()
    {
        try
        {
            var housing = HousingManager.Instance();
            return housing == null ? null : housing->WorkshopTerritory;
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, "[AutoFCWSDeliver] 取得工房領地失敗（多半是特徵碼失效）");
            return null;
        }
    }

    protected override void OnEnable()
    {
        queue.OnTimeout = step => Svc.Chat.PrintError($"[TC Toolbox] 合建交納步驟逾時，已停止：{step}");

        Svc.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "SubmarinePartsMenu", OnMenuFinalize);
        Svc.Framework.Update += OnUpdate;
        Svc.PluginInterface.UiBuilder.Draw += DrawOverlay;
    }

    protected override void OnDisable()
    {
        Svc.AddonLifecycle.UnregisterListener(OnMenuFinalize);
        Svc.Framework.Update -= OnUpdate;
        Svc.PluginInterface.UiBuilder.Draw -= DrawOverlay;
        queue.Abort();
    }

    private void OnUpdate(IFramework framework) => queue.Tick();

    private void OnMenuFinalize(AddonEvent type, AddonArgs args)
    {
        // 交完一項之後遊戲會把這個 addon 整個重建，而重建的第一步就是 Finalize。
        // 舊碼在這裡直接 Abort，於是每次交納都在跟重建賽跑——實機 log 顯示同一批素材
        // 交到第 2、5、8、11、33 項都可能停，數字沒有規律，正是這種競態的樣子。
        // 這裡只記錄時刻，真正的判斷交給下面的等待邏輯：回得來就繼續，回不來才算關閉。
        if (!queue.IsBusy) return;

        menuGoneSince ??= DateTime.UtcNow;
        Svc.Log.Information(
            $"[AutoFCWSDeliver] 合建視窗 Finalize，目前步驟「{queue.CurrentStep}」，本輪已交 {deliveredCount} 項，等待重建中");
    }

    private void DrawOverlay()
    {
        var addon = UiHelper.GetAddon("SubmarinePartsMenu");
        if (!UiHelper.IsReady(addon)) return;

        const ImGuiWindowFlags flags = ImGuiWindowFlags.AlwaysAutoResize |
                                       ImGuiWindowFlags.NoTitleBar |
                                       ImGuiWindowFlags.NoCollapse |
                                       ImGuiWindowFlags.NoSavedSettings;

        if (ImGui.Begin("###TCToolboxFCWSDeliver", flags))
        {
            ImGui.SetWindowPos(new Vector2(addon->GetX() + 6, addon->GetY() - ImGui.GetWindowSize().Y - 4));

            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(new Vector4(1f, 0.85f, 0.35f, 1f), DisplayName);

            ImGui.SameLine();
            using (Dalamud.Interface.Utility.Raii.ImRaii.Disabled(queue.IsBusy))
            {
                if (ImGui.Button("開始##fcws"))
                    Start();
            }

            ImGui.SameLine();
            if (ImGui.Button("停止##fcws"))
            {
                queue.Abort();
                Svc.Chat.Print($"[TC Toolbox] 已手動停止交納（本輪已交 {deliveredCount} 項）。");
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

        if (GetWorkshopTerritory() == null)
        {
            Svc.Chat.PrintError("[TC Toolbox] 必須在部隊工房內才能使用合建交納。");
            return;
        }

        deliveredCount = 0;
        menuGoneSince = null;
        requestSeen = false;
        lastParseFingerprint = null;
        noProgressRounds = 0;
        enqueuedItems = 0;
        EnqueueNextItem();
    }

    private void EnqueueNextItem()
    {
        queue.Enqueue("解析可交納素材", () =>
        {
            var addon = UiHelper.GetAddon("SubmarinePartsMenu");
            if (!UiHelper.IsReady(addon))
            {
                // 視窗不在有兩種可能：遊戲正在重建它（交完一項後必然發生），或使用者真的關了。
                // 前者只需要等幾幀，所以先給一段寬限；逾時保底仍在（本步 10 秒）。
                menuGoneSince ??= DateTime.UtcNow;
                if ((DateTime.UtcNow - menuGoneSince.Value).TotalSeconds < MenuRebuildGraceSeconds)
                    return false;

                Svc.Chat.Print($"[TC Toolbox] 合建視窗已關閉，停止交納（本輪已交 {deliveredCount} 項）。");
                Svc.Log.Information(
                    $"[AutoFCWSDeliver] 視窗消失超過 {MenuRebuildGraceSeconds} 秒未重建，判定為關閉");
                return null;
            }

            menuGoneSince = null; // 回來了，重新計時
            var items = ParseDeliverables(addon);
            if (items.Count == 0)
            {
                Svc.Chat.Print($"[TC Toolbox] 合建素材交納完成：本輪共交納 {deliveredCount} 項（其餘素材數量不足或已交滿）。");
                return true;
            }

            var item = items[0];

            // ── 保險絲一：零進展偵測 ───────────────────────────────────────────
            // 解析結果與上一輪完全相同 ＝ 上一輪那一項其實沒交出去。
            // 這是「agent 事件被靜默拒絕」唯一看得出來的地方（台服的拒絕沒有任何回饋）。
            var fingerprint = (item.Index, items.Count);
            if (lastParseFingerprint == fingerprint)
            {
                noProgressRounds++;
                if (noProgressRounds >= MaxNoProgressRounds)
                {
                    Svc.Chat.PrintError(
                        $"[TC Toolbox] 交納沒有生效（連續 {noProgressRounds} 輪清單完全沒變），已停止。" +
                        $"本輪實際交出 {deliveredCount} 項。");
                    Svc.Log.Information(
                        $"[AutoFCWSDeliver] 零進展保險絲觸發：連續 {noProgressRounds} 輪解析到同一批素材" +
                        $"（首項 Index={item.Index}、清單長度={items.Count}），判定交納事件沒有生效。");
                    return null;
                }
            }
            else
            {
                lastParseFingerprint = fingerprint;
                noProgressRounds = 0;
            }

            // ── 保險絲二：單趟項數上限 ─────────────────────────────────────────
            if (enqueuedItems >= MaxItemsPerRun)
            {
                Svc.Chat.PrintError($"[TC Toolbox] 已達單趟交納上限 {MaxItemsPerRun} 項，先停下來（本輪已交 {deliveredCount} 項）。");
                Svc.Log.Information($"[AutoFCWSDeliver] 觸及 MaxItemsPerRun={MaxItemsPerRun}，中止本趟。");
                return null;
            }

            enqueuedItems++;

            // 隱藏值：Type 帶 ItemId（去 HQ 位）、Int64 高 32 位帶數量——與遊戲原生點擊列時送出的事件相同
            var hidden = new AtkValue
            {
                Type = (ValueType)(item.ItemId % 500_000),
                Int64 = (long)item.Count << 32,
            };
            UiHelper.SendAgentEvent(AgentId.CompanyCraftMaterial, 0, 0, item.Index, item.Count, hidden);

            fillSlotCursor = 1;
            requestSeen = false;
            queue.EnqueueDelay(500, "等待交納視窗");
            queue.Enqueue("填入交納視窗", FillRequest, 10_000);
            queue.Enqueue("送出交納並等待完成", ConfirmRequest, 15_000);
            EnqueueNextItem();
            return true;
        }, 10_000);
    }

    /// <summary>把 Request 交納視窗的每個欄位填入對應道具（TextAdvance ExecRequestFill 同款事件形狀）。</summary>
    private bool? FillRequest()
    {
        var request = UiHelper.GetAddon("Request");
        if (!UiHelper.IsReady(request)) return true; // 沒出現交納視窗（或已被交出）

        // 記下「這一項的交納視窗真的出現過」——ConfirmRequest 靠它區分
        // 「交完了所以視窗關了」與「從頭到尾沒開過」。這兩件事在那裡長得一模一樣。
        requestSeen = true;

        var contextIcon = UiHelper.GetAddon("ContextIconMenu");
        if (UiHelper.IsReady(contextIcon))
        {
            // 道具選單開著 → 選第一個（即對應素材）
            UiHelper.FireCallback(contextIcon, false, 0, 0, 1021003, 0, 0);
            fillSlotCursor++;
            return false;
        }

        var entryCount = ((AddonRequest*)request)->EntryCount;
        if (fillSlotCursor > entryCount) return true;

        if (Throttle.Pass("AutoFCWSDeliver-FillSlot", 150))
            UiHelper.FireCallback(request, false, 2, fillSlotCursor - 1, 0, 0);
        return false;
    }

    /// <summary>按下交納並等待視窗（含 HQ 確認）全部關閉。</summary>
    private bool? ConfirmRequest()
    {
        if (GetWorkshopTerritory() == null) return null;

        var yesno = UiHelper.GetAddon("SelectYesno");
        if (UiHelper.IsReady(yesno))
        {
            // 交納確認（例如包含 HQ 道具）
            if (Throttle.Pass("AutoFCWSDeliver-Yesno", 300))
                UiHelper.FireCallback(yesno, true, 0);
            return false;
        }

        var request = UiHelper.GetAddon("Request");
        if (UiHelper.IsReady(request))
        {
            if (Throttle.Pass("AutoFCWSDeliver-HandOver", 500))
                UiHelper.ClickButton(request, ((AddonRequest*)request)->HandOverButton);
            return false;
        }

        // 🔴 只有「交納視窗真的出現過」才算交出去了。視窗從頭到尾沒開＝這一項根本沒交，
        //    盲目遞增會讓 overlay 的「已交 N 項」在零交納的情況下持續攀升，
        //    把使用者的唯一回饋管道變成假訊號。真正的收口交給解析步驟的零進展保險絲。
        if (!requestSeen)
        {
            if (Throttle.Pass("AutoFCWSDeliver-NoRequest", 10_000))
            {
                Svc.Log.Information(
                    "[AutoFCWSDeliver] 交納視窗未出現，本項不計入已交納" +
                    "（多半是 agent 事件被拒絕；連續數輪沒進展會由保險絲中止）。");
            }

            return true;
        }

        deliveredCount++;
        return true;
    }

    /// <summary>解析合建視窗目前可交納（數量足夠且未交滿）的素材清單。</summary>
    private static List<DeliverItem> ParseDeliverables(AtkUnitBase* addon)
    {
        var result = new List<DeliverItem>();

        // 🔴 光是判 addon 與長度還不夠。AtkValuesSpan 的實作是
        // new Span<AtkValue>(AtkValues, AtkValuesCount)，它自己不判 AtkValues 這個欄位，
        // 而 Span 的建構子也不驗指標。addon 拆解時 AtkValues 會先被釋放成 null、
        // AtkValuesCount 卻可能還留著殘值，這個組合會合法建構出一個長度非零的 Span，
        // 連 Span 自己的邊界檢查都會放行，一直到真的索引下去才對位址 0 解參考 ＝
        // AccessViolationException（corrupted-state exception，try/catch 攔不到）。
        // ⇒ 必須另外自判 AtkValues 欄位；讀不到就回空清單（與下面既有的長度不足同一條路）。
        if (addon == null || addon->AtkValues == null) return result;

        var agent = AgentCompanyCraftMaterial.Instance();
        if (agent == null) return result;

        var supplyCount = 0;
        foreach (var supply in agent->SupplyItems)
        {
            if (supply != 0)
                supplyCount++;
        }

        if (supplyCount == 0) return result;

        var values = addon->AtkValuesSpan;
        if (values.Length <= 132 + supplyCount) return result;

        for (var i = 0; i < supplyCount; i++)
        {
            var itemId = values[12 + i].UInt;
            if (itemId == 0) continue; // 此欄無道具

            var completed = values[132 + i].UInt;
            if (completed == 1) continue; // 已交滿

            var required = values[60 + i].UInt;
            var owned = values[72 + i].UInt;
            if (owned < required) continue; // 持有數不足

            result.Add(new DeliverItem((uint)i, itemId, required));
        }

        return result;
    }
}

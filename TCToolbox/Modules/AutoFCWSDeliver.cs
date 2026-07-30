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

    private readonly TaskQueue queue = new();

    private int fillSlotCursor;
    private int deliveredCount;

    private sealed record DeliverItem(uint Index, uint ItemId, uint Count);

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
        // 合建視窗關閉（含階段完成後自動關閉）→ 停止流程
        if (queue.IsBusy)
        {
            queue.Abort();
            Svc.Chat.Print($"[TC Toolbox] 合建視窗已關閉，停止交納（本輪已交 {deliveredCount} 項）。");
        }
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
            ImGui.TextColored(new Vector4(1f, 0.85f, 0.35f, 1f), "合建素材一鍵全交");

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

        if (HousingManager.Instance()->WorkshopTerritory == null)
        {
            Svc.Chat.PrintError("[TC Toolbox] 必須在部隊工房內才能使用合建交納。");
            return;
        }

        deliveredCount = 0;
        EnqueueNextItem();
    }

    private void EnqueueNextItem()
    {
        queue.Enqueue("解析可交納素材", () =>
        {
            var addon = UiHelper.GetAddon("SubmarinePartsMenu");
            if (!UiHelper.IsReady(addon)) return null; // PreFinalize 已處理訊息

            var items = ParseDeliverables(addon);
            if (items.Count == 0)
            {
                Svc.Chat.Print($"[TC Toolbox] 合建素材交納完成：本輪共交納 {deliveredCount} 項（其餘素材數量不足或已交滿）。");
                return true;
            }

            var item = items[0];

            // 隱藏值：Type 帶 ItemId（去 HQ 位）、Int64 高 32 位帶數量——與遊戲原生點擊列時送出的事件相同
            var hidden = new AtkValue
            {
                Type = (ValueType)(item.ItemId % 500_000),
                Int64 = (long)item.Count << 32,
            };
            UiHelper.SendAgentEvent(AgentId.CompanyCraftMaterial, 0, 0, item.Index, item.Count, hidden);

            fillSlotCursor = 1;
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
        if (HousingManager.Instance()->WorkshopTerritory == null) return null;

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

        deliveredCount++;
        return true;
    }

    /// <summary>解析合建視窗目前可交納（數量足夠且未交滿）的素材清單。</summary>
    private static List<DeliverItem> ParseDeliverables(AtkUnitBase* addon)
    {
        var result = new List<DeliverItem>();

        var agent = AgentCompanyCraftMaterial.Instance();
        if (agent == null) return result;

        var supplyCount = 0;
        foreach (var supply in agent->SupplyItems)
        {
            if (supply != 0)
                supplyCount++;
        }

        if (supplyCount == 0) return result;
        if (addon->AtkValuesCount <= 132 + supplyCount) return result;

        for (var i = 0; i < supplyCount; i++)
        {
            var itemId = addon->AtkValues[12 + i].UInt;
            if (itemId == 0) continue; // 此欄無道具

            var completed = addon->AtkValues[132 + i].UInt;
            if (completed == 1) continue; // 已交滿

            var required = addon->AtkValues[60 + i].UInt;
            var owned = addon->AtkValues[72 + i].UInt;
            if (owned < required) continue; // 持有數不足

            result.Add(new DeliverItem((uint)i, itemId, required));
        }

        return result;
    }
}

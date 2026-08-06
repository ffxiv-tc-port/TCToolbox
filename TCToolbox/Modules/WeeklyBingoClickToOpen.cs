using System;
using System.Collections.Generic;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Events;
using Dalamud.Game.Addon.Events.EventDataTypes;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface.Utility.Raii;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 在天書（<c>WeeklyBingo</c>）的格子上點一下，直接開任務搜尋器到對應的副本／輪盤。
/// </summary>
/// <remarks>
/// 機制：<c>WeeklyBingo</c> 開啟時對 16 個格子按鈕各掛一個 <see cref="AddonEventType.ButtonClick"/>，
/// 點下去時查 <c>PlayerState</c> 的天書狀態，交給
/// <see cref="WeeklyBingoDutyResolver"/> 解析成副本／輪盤，再呼叫
/// <c>AgentContentsFinder::OpenRegularDuty</c> / <c>OpenRouletteDuty</c>。
/// 只是「幫你把搜尋器開到那一項」，不排隊、不送封包。
/// <para>
/// 🔴 <b>對照表沒有寫死。</b>DailyRoutines 同名模組那張 38 筆的硬表在台服至少 5 筆是錯的
/// （兩筆會開成<b>零式</b>），詳見 <see cref="WeeklyBingoDutyResolver"/> 的說明。
/// 這裡改用資料驅動的完全比對，<b>對不到就不開</b>，只在 log 與聊天欄說明原因。
/// </para>
/// <para>
/// 🔴 <b>不用 <c>NodeId - 12</c> 反推格子編號。</b>DR 是這樣做的，基準只要不同就會靜默開錯格子。
/// 這裡每個格子註冊自己的處理器（Dalamud 會給每次註冊獨立的 param key），
/// 點下去之後再<b>重新取得 addon</b> 核對節點指標和格子編號對得上才動作。
/// </para>
/// <para>
/// ⚠️ 只在格子狀態是「未完成」時動作。已完成待貼貼紙／重置模式下的點擊是遊戲自己的功能，不介入。
/// </para>
/// </remarks>
public sealed unsafe class WeeklyBingoClickToOpen : TcModule
{
    public override string InternalName => "WeeklyBingoClickToOpen";
    public override string DisplayName => "天書格子點擊直接開副本";

    public override string Description =>
        "在天書（探險者筆記）的格子上點一下，就把任務搜尋器開到對應的副本或輪盤，不用自己去找。" +
        "對照表是從遊戲資料表即時比對出來的，比對不到的格子一律不開（會在聊天欄說明原因），不會賭一個「可能是」的副本。";

    public override bool HasConfigUI => true;

    private const string AddonName = "WeeklyBingo";

    /// <summary>天書固定 16 格。</summary>
    private const int SlotCount = 16;

    private readonly IAddonEventHandle?[] handles = new IAddonEventHandle[SlotCount];

    private WeeklyBingoClickToOpenConfig Config => Plugin.Instance.Config.WeeklyBingoClickToOpen;

    protected override void OnEnable()
    {
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, AddonName, OnPostSetup);
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, AddonName, OnPreFinalize);

        // 模組是在天書已經開著的狀態下才被啟用的話，PostSetup 不會再來一次。
        if (UiHelper.IsAddonReady(AddonName))
            RegisterSlotEvents();
    }

    protected override void OnDisable()
    {
        Svc.AddonLifecycle.UnregisterListener(OnPostSetup);
        Svc.AddonLifecycle.UnregisterListener(OnPreFinalize);
        RemoveSlotEvents();
    }

    private void OnPostSetup(AddonEvent type, AddonArgs args) => RegisterSlotEvents();

    private void OnPreFinalize(AddonEvent type, AddonArgs args) => RemoveSlotEvents();

    private void RegisterSlotEvents()
    {
        // 先清乾淨再掛，避免 PostSetup 連來兩次時留下孤兒 handle。
        RemoveSlotEvents();

        var addon = (AddonWeeklyBingo*)UiHelper.GetAddon(AddonName);
        if (!UiHelper.IsReady((AtkUnitBase*)addon)) return;

        var registered = 0;
        for (var i = 0; i < SlotCount; i++)
        {
            var node = GetSlotNode(addon, i);
            if (node == null) continue;

            // 每格自己的處理器：Dalamud 依 param key 分派，所以格子編號是「註冊當下就綁死」的，
            // 不需要（也不可以）從 NodeId 反推。
            var slot = i;
            handles[i] = Svc.AddonEvent.AddEvent(
                (nint)addon,
                (nint)node,
                AddonEventType.ButtonClick,
                (eventType, data) => OnSlotClick(slot, eventType, data));

            if (handles[i] != null) registered++;
        }

        if (registered != SlotCount && Throttle.Pass($"{InternalName}-Register", 60_000))
            Svc.Log.Information($"[{InternalName}] 只掛上 {registered}/{SlotCount} 個格子的點擊事件（其餘格子的按鈕節點取不到）。");
    }

    private void RemoveSlotEvents()
    {
        for (var i = 0; i < SlotCount; i++)
        {
            if (handles[i] is not { } handle) continue;

            try
            {
                Svc.AddonEvent.RemoveEvent(handle);
            }
            catch (Exception ex)
            {
                Svc.Log.Warning(ex, $"[{InternalName}] 移除第 {i + 1} 格的點擊事件失敗");
            }
            finally
            {
                handles[i] = null;
            }
        }
    }

    /// <summary>取某一格的按鈕節點。指標<b>不保存</b>，每次都重取。</summary>
    private static AtkResNode* GetSlotNode(AddonWeeklyBingo* addon, int slot)
    {
        if (addon == null || slot is < 0 or >= SlotCount) return null;

        var button = addon->DutySlotList[slot].DutyButton;
        if (button == null) return null;

        var owner = button->AtkComponentBase.OwnerNode;
        return owner == null ? null : &owner->AtkResNode;
    }

    private void OnSlotClick(int slot, AddonEventType eventType, AddonEventData data)
    {
        try
        {
            HandleSlotClick(slot, data);
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, $"[{InternalName}] 處理第 {slot + 1} 格的點擊時發生例外");
        }
    }

    private void HandleSlotClick(int slot, AddonEventData data)
    {
        // 副本中不要把搜尋器叫出來。
        if (Svc.Condition[ConditionFlag.BoundByDuty] ||
            Svc.Condition[ConditionFlag.BoundByDuty56] ||
            Svc.Condition[ConditionFlag.BoundByDuty95])
            return;

        // 重新取得 addon（絕不跨幀保存原生指標），並核對「我以為的格子」真的是「被點的那個節點」。
        var addon = (AddonWeeklyBingo*)UiHelper.GetAddon(AddonName);
        if (!UiHelper.IsReady((AtkUnitBase*)addon)) return;

        var node = GetSlotNode(addon, slot);
        if (node == null || (nint)node != data.NodeTargetPointer)
        {
            Svc.Log.Information(
                $"[{InternalName}] 第 {slot + 1} 格的節點對不上被點擊的節點，已略過（原生版面可能改了）。");
            return;
        }

        var playerState = PlayerState.Instance();
        if (playerState == null || !playerState->HasWeeklyBingoJournal) return;

        if (playerState->GetWeeklyBingoTaskStatus(slot) != PlayerState.WeeklyBingoTaskStatus.Open)
            return; // 已完成／待貼貼紙的格子交給遊戲自己處理。

        var orderRowId = (uint)playerState->WeeklyBingoOrderData[slot];
        if (orderRowId == 0) return;

        var target = WeeklyBingoDutyResolver.Resolve(orderRowId);
        var description = WeeklyBingoDutyResolver.GetDescription(orderRowId);

        if (!target.IsResolved)
        {
            // 🔴 對不到就不開。使用者跑 LogLevel 2，所以診斷寫 Information。
            Svc.Log.Information(
                $"[{InternalName}] 第 {slot + 1} 格（WeeklyBingoOrderData {orderRowId}「{description}」）對不到副本：{target.Reason}");

            if (Config.NotifyWhenUnresolved && Throttle.Pass($"{InternalName}-Unresolved-{orderRowId}", 5_000))
                Svc.Chat.PrintError($"[TC Toolbox] 「{description}」對不到可以直接開啟的副本，請自行到任務搜尋器選擇。");

            return;
        }

        var agent = AgentContentsFinder.Instance();
        if (agent == null) return;

        switch (target.Kind)
        {
            case BingoTargetKind.Roulette:
                agent->OpenRouletteDuty((byte)target.Id);
                break;
            case BingoTargetKind.Duty:
                agent->OpenRegularDuty(target.Id);
                break;
            default:
                return;
        }

        Svc.Log.Information(
            $"[{InternalName}] 第 {slot + 1} 格「{description}」→ {(target.Kind == BingoTargetKind.Roulette ? "輪盤" : "副本")} {target.Id}「{target.Name}」");

        if (Config.NotifyOnOpen)
            Svc.Chat.Print($"[TC Toolbox] 已開啟「{target.Name}」（天書：{description}）。");
    }

    public override void DrawConfig()
    {
        var notifyOpen = Config.NotifyOnOpen;
        if (ImGui.Checkbox("開啟副本時在聊天欄顯示", ref notifyOpen))
        {
            Config.NotifyOnOpen = notifyOpen;
            Plugin.Instance.Config.Save();
        }

        var notifyUnresolved = Config.NotifyWhenUnresolved;
        if (ImGui.Checkbox("對不到副本時在聊天欄說明", ref notifyUnresolved))
        {
            Config.NotifyWhenUnresolved = notifyUnresolved;
            Plugin.Instance.Config.Save();
        }

        using (ImRaii.PushIndent())
            ImGui.TextDisabled("關掉的話對不到時就完全沒有反應，只有 log 會留下記錄。");

        ImGui.Separator();
        DrawCurrentBook();
    }

    /// <summary>
    /// 列出目前這本天書的 16 格與各自解析到什麼，並提供直接開啟的按鈕。
    /// </summary>
    /// <remarks>
    /// 這一段同時是<b>備援路徑</b>：萬一原生格子的點擊事件在某次改版後掛不上去，
    /// 從這裡照樣點得開。⚠️ 對不到的格子畫成灰字的「—」而不是留白或 0，
    /// 「不知道」本身要在列上看得見。
    /// </remarks>
    private void DrawCurrentBook()
    {
        var playerState = PlayerState.Instance();
        if (playerState == null || !playerState->HasWeeklyBingoJournal)
        {
            ImGui.TextDisabled("目前沒有天書（探險者筆記）。");
            return;
        }

        ImGui.TextDisabled("目前這本天書的 16 格（灰字「—」＝對不到，點了不會有反應）：");

        using var table = ImRaii.Table("##bingo-slots", 4,
            ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.SizingStretchProp);
        if (!table) return;

        ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed, 28f);
        ImGui.TableSetupColumn("天書項目", ImGuiTableColumnFlags.WidthStretch, 1.2f);
        ImGui.TableSetupColumn("會開啟", ImGuiTableColumnFlags.WidthStretch, 1.4f);
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 56f);
        ImGui.TableHeadersRow();

        for (var slot = 0; slot < SlotCount; slot++)
        {
            var orderRowId = (uint)playerState->WeeklyBingoOrderData[slot];
            if (orderRowId == 0) continue;

            var status = playerState->GetWeeklyBingoTaskStatus(slot);
            var description = WeeklyBingoDutyResolver.GetDescription(orderRowId);
            var target = WeeklyBingoDutyResolver.Resolve(orderRowId);

            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            ImGui.Text((slot + 1).ToString());

            ImGui.TableNextColumn();
            if (status == PlayerState.WeeklyBingoTaskStatus.Open)
                ImGui.Text(description.Length > 0 ? description : "（無敘述）");
            else
                ImGui.TextDisabled($"{(description.Length > 0 ? description : "（無敘述）")}（已完成）");

            ImGui.TableNextColumn();
            if (target.IsResolved)
            {
                ImGui.Text(target.Name);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(target.Kind == BingoTargetKind.Roulette
                        ? $"輪盤 ContentRoulette {target.Id}"
                        : $"副本 ContentFinderCondition {target.Id}");
            }
            else
            {
                ImGui.TextDisabled("—");
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(target.Reason);
            }

            ImGui.TableNextColumn();
            using (ImRaii.Disabled(!target.IsResolved))
            {
                if (ImGui.SmallButton($"開啟##bingo-{slot}"))
                    OpenFromConfigUi(target, description);
            }
        }
    }

    private void OpenFromConfigUi(BingoTarget target, string description)
    {
        if (!target.IsResolved) return;

        var agent = AgentContentsFinder.Instance();
        if (agent == null) return;

        if (target.Kind == BingoTargetKind.Roulette)
            agent->OpenRouletteDuty((byte)target.Id);
        else
            agent->OpenRegularDuty(target.Id);

        Svc.Log.Information($"[{InternalName}] 從設定畫面開啟「{target.Name}」（天書：{description}）。");
    }
}

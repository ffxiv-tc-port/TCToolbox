using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 商店快速購買（預設）：把「開哪個商店、點清單裡第幾項」記成一個預設，之後一鍵重播那次點擊，
/// 直接叫出該道具的購買對話框，省去每次翻頁找同一件東西。
/// </summary>
/// <remarks>
/// <para>
/// 🔴🔴 <b>花錢的最後確認一律由使用者自己按。</b>本模組<b>只</b>把清單項目的點擊重播出來
/// （＝叫出遊戲自己的購買／數量對話框），<b>絕不代按任何確認框</b>，也<b>不</b>做「買完自動再買下一輪」
/// 的事件驅動接手鏈。數量與最終「購買」都在遊戲自己的對話框裡由人決定。
/// 這是與 DailyRoutines 原版<b>最關鍵的差異</b>：DR 會自動點掉 <c>SelectYesno</c>／
/// <c>ShopExchangeItemDialog</c> 並用背包刷新事件驅動迴圈買 N 次——那條在本艦隊是紅線
/// （市場自動化一律手動觸發、禁止事件驅動、花錢由人按），<b>刻意不照抄</b>。
/// </para>
/// <para>
/// 📌 <b>預設在台服現地錄製，不含任何寫死的國際服偏移。</b>node id 與清單索引是使用者開著台服商店
/// 當場掃描擷取的，所以天生對得上台服版面。唯一的脆弱點是「清單索引是位置」——商店排序若變動，
/// 同一個索引會指到別的道具；因此預設可綁定「對象 NPC 名稱」，名稱不符就拒絕執行。
/// </para>
/// <para>
/// 🔴 純唯讀＋重播既有事件：不 hook、不寫記憶體 patch、不送封包偽造。
/// </para>
/// </remarks>
public sealed unsafe class AutoShopPurchase : TcModule
{
    public override string InternalName => "AutoShopPurchase";

    public override string DisplayName => "商店快速購買（預設）";

    public override string Description =>
        "把「開哪個商店、點清單第幾項」記成預設，之後一鍵重播那次點擊叫出購買對話框。" +
        "數量與最終購買確認一律在遊戲自己的對話框裡由你自己按——本模組不會自動花錢、也不會連續自動買。";

    public override ModuleCategory Category => ModuleCategory.Inventory;

    /// <inheritdoc/>
    /// <remarks>要按「執行」才會動＝手動觸發。</remarks>
    public override bool IsManualTrigger => true;

    public override bool HasConfigUI => true;

    private const int MaxIndexButtons = 200;

    private AutoShopPurchaseConfig Config => Plugin.Instance.Config.AutoShopPurchase;

    // 擷取用暫存欄位。
    private string captureName = string.Empty;
    private string captureAddon = string.Empty;
    private uint captureNodeId;
    private int captureIndex = -1;
    private bool captureTargetBound;

    private readonly List<ScannedAddon> scanned = [];

    private sealed record ScannedAddon(string AddonName, List<(uint NodeId, int Length)> Lists);

    protected override void OnEnable() { }

    protected override void OnDisable() => scanned.Clear();

    // ── 執行 ────────────────────────────────────────────────────────────────

    /// <summary>重播預設的清單點擊。回傳失敗原因；<c>null</c>＝已送出。</summary>
    private string? RunPreset(AutoShopPurchasePreset preset)
    {
        var addon = UiHelper.GetAddon(preset.AddonName);
        if (!UiHelper.IsReady(addon))
            return $"「{preset.AddonName}」視窗沒有開著。";

        if (!string.IsNullOrWhiteSpace(preset.TargetName))
        {
            var current = Svc.Targets.Target?.Name.TextValue ?? string.Empty;
            if (current != preset.TargetName)
                return $"目前的對象不是「{preset.TargetName}」，已拒絕執行。";
        }

        var list = addon->GetComponentListById(preset.ListNodeId);
        if (list == null)
            return $"找不到清單節點 {preset.ListNodeId}（版面可能已變）。";

        if (preset.ClickIndex < 0 || preset.ClickIndex >= list->ListLength)
            return $"索引 {preset.ClickIndex} 超出清單範圍（目前 {list->ListLength} 項）。";

        // 🔴 同一扇商店視窗、同一格，在它走完生命週期前只重播一次（2 秒逾時兜底）：
        //    ListItemClick 是輸入事件，對關閉中的視窗送就是攔不到的存取違規。
        if (!AddonPressGuard.TryBeginPress(addon, $"list:{preset.ListNodeId}:{preset.ClickIndex}"))
            return "剛剛才對同一項送出過點擊，請稍候再試。";

        // 重播那一項的點擊——叫出遊戲自己的購買對話框。不代按任何確認框。
        list->DispatchItemEvent(preset.ClickIndex, AtkEventType.ListItemClick);
        Svc.Log.Information(
            $"[{InternalName}] 執行預設「{preset.Name}」：{preset.AddonName} 清單 {preset.ListNodeId} 第 {preset.ClickIndex} 項。");
        return null;
    }

    // ── 掃描（現地擷取 node id）────────────────────────────────────────────

    private void RescanFocused()
    {
        scanned.Clear();

        var manager = RaptureAtkUnitManager.Instance();
        if (manager == null) return;

        var entries = manager->FocusedUnitsList.Entries;
        for (var e = 0; e < entries.Length; e++)
        {
            var addon = entries[e].Value;
            if (!UiHelper.IsReady(addon)) continue;

            // addon 的 NodeList 已經是所有頂層節點的平面列表；遞迴只用來鑽進「元件節點」
            // 各自的子 NodeList（那些內部節點不在頂層列表裡）。
            var ids = new HashSet<uint>();
            for (var i = 0; i < addon->UldManager.NodeListCount; i++)
                CollectNodeIds(addon->UldManager.NodeList[i], ids, 0);

            var lists = new List<(uint, int)>();
            foreach (var id in ids)
            {
                var list = addon->GetComponentListById(id);
                if (list != null) lists.Add((id, list->ListLength));
            }

            if (lists.Count > 0)
                scanned.Add(new ScannedAddon(addon->NameString, lists));
        }
    }

    /// <summary>遞迴收集節點 id（含元件節點的子樹）。深度上限防環。</summary>
    private static void CollectNodeIds(AtkResNode* node, HashSet<uint> ids, int depth)
    {
        if (node == null || depth > 24) return;
        if (!ids.Add(node->NodeId)) return;

        var componentNode = node->GetAsAtkComponentNode();
        if (componentNode != null && componentNode->Component != null)
        {
            var uld = &componentNode->Component->UldManager;
            for (var i = 0; i < uld->NodeListCount; i++)
                CollectNodeIds(uld->NodeList[i], ids, depth + 1);
        }
    }

    // ── UI ──────────────────────────────────────────────────────────────────

    public override void DrawConfig()
    {
        ImGui.TextColored(new Vector4(1f, 0.8f, 0.35f, 1f),
            "花錢的最後確認一律由你自己按：本模組只把清單點擊重播出來叫出購買對話框，不自動買、不連續買。");
        ImGui.Separator();

        DrawPresetTable();

        ImGui.Separator();
        ImGui.TextDisabled("擷取新預設：先開著商店視窗，按下方「掃描目前商店視窗」。");
        DrawCaptureSection();
    }

    private void DrawPresetTable()
    {
        if (Config.Presets.Count == 0)
        {
            ImGui.TextDisabled("（尚無預設）");
            return;
        }

        using var table = ImRaii.Table("AutoShopPurchasePresets", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg);
        if (!table) return;

        ImGui.TableSetupColumn("名稱");
        ImGui.TableSetupColumn("視窗");
        ImGui.TableSetupColumn("路徑");
        ImGui.TableSetupColumn("對象");
        ImGui.TableSetupColumn("操作", ImGuiTableColumnFlags.WidthFixed, 140f);
        ImGui.TableHeadersRow();

        AutoShopPurchasePreset? toRemove = null;

        for (var i = 0; i < Config.Presets.Count; i++)
        {
            var preset = Config.Presets[i];
            using var id = ImRaii.PushId(i);

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(preset.Name);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(preset.AddonName);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{preset.ListNodeId} → {preset.ClickIndex}");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(string.IsNullOrWhiteSpace(preset.TargetName) ? "（不限）" : preset.TargetName);
            ImGui.TableNextColumn();

            if (ImGui.Button("執行"))
            {
                var reason = RunPreset(preset);
                if (reason != null) Svc.Chat.PrintError($"[TC Toolbox] {reason}");
            }

            ImGui.SameLine();
            using (ImRaii.Disabled(!ImGui.IsKeyDown(ImGuiKey.LeftCtrl)))
            {
                if (ImGui.Button("刪除")) toRemove = preset;
            }

            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("按住 Ctrl 才能刪除。");
        }

        if (toRemove != null)
        {
            Config.Presets.Remove(toRemove);
            Plugin.Instance.Config.Save();
        }
    }

    private void DrawCaptureSection()
    {
        if (ImGui.Button("掃描目前商店視窗"))
            RescanFocused();

        if (scanned.Count == 0)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("（尚未掃描，或聚焦中的視窗沒有清單）");
        }

        foreach (var addon in scanned)
        {
            if (!ImGui.CollapsingHeader($"{addon.AddonName}（{addon.Lists.Count} 個清單）###scan_{addon.AddonName}"))
                continue;

            foreach (var (nodeId, length) in addon.Lists)
            {
                ImGui.TextDisabled($"清單 {nodeId}（{length} 項）— 點索引可即時試按（會叫出購買對話框，尚未購買）：");

                var shown = length > MaxIndexButtons ? MaxIndexButtons : length;
                for (var i = 0; i < shown; i++)
                {
                    if (i % 12 != 0) ImGui.SameLine();

                    if (ImGui.Button($"{i:D2}##{addon.AddonName}_{nodeId}_{i}"))
                        CaptureAndPreview(addon.AddonName, nodeId, i);
                }

                if (length > MaxIndexButtons)
                    ImGui.TextDisabled($"（只顯示前 {MaxIndexButtons} 項）");
            }
        }

        if (captureIndex < 0) return;

        ImGui.Separator();
        ImGui.Text($"已擷取：{captureAddon} 清單 {captureNodeId} 第 {captureIndex} 項");

        var name = captureName;
        if (ImGui.InputText("預設名稱##capname", ref name, 64))
            captureName = name;

        if (ImGui.Checkbox("綁定目前對象 NPC（名稱不符就拒絕執行）##capbind", ref captureTargetBound)) { }
        if (captureTargetBound)
        {
            ImGui.SameLine();
            var target = Svc.Targets.Target?.Name.TextValue ?? string.Empty;
            ImGui.TextDisabled(string.IsNullOrWhiteSpace(target) ? "（目前沒有目標）" : target);
        }

        using (ImRaii.Disabled(string.IsNullOrWhiteSpace(captureName)))
        {
            if (ImGui.Button("新增預設##capadd"))
            {
                Config.Presets.Add(new AutoShopPurchasePreset
                {
                    Name = captureName,
                    AddonName = captureAddon,
                    ListNodeId = captureNodeId,
                    ClickIndex = captureIndex,
                    TargetName = captureTargetBound ? (Svc.Targets.Target?.Name.TextValue ?? string.Empty) : string.Empty,
                });
                Plugin.Instance.Config.Save();

                captureName = string.Empty;
                captureIndex = -1;
            }
        }
    }

    /// <summary>擷取一個索引，並即時試按一次（叫出對話框讓使用者確認是不是想要的道具）。</summary>
    private void CaptureAndPreview(string addonName, uint nodeId, int index)
    {
        captureAddon = addonName;
        captureNodeId = nodeId;
        captureIndex = index;

        var addon = UiHelper.GetAddon(addonName);
        if (!UiHelper.IsReady(addon)) return;

        var list = addon->GetComponentListById(nodeId);
        if (list == null || index >= list->ListLength) return;

        // 同上：同一扇窗、同一格只重播一次（守衛擋下＝剛送過，這次略過）。
        if (!AddonPressGuard.TryBeginPress(addon, $"list:{nodeId}:{index}")) return;

        list->DispatchItemEvent(index, AtkEventType.ListItemClick);
    }
}

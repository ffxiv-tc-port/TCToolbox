using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 小隊列表顯示目標標記：把場上的目標標記（攻擊1、綁2、止3…）同步畫在小隊列表對應隊員的欄位上。
/// 機制：輪詢 <see cref="MarkingController"/> 的標記表 + AgentHUD 的隊員 EntityId 對位，
/// 用 ImGui 疊圖畫在 _PartyList 節點的螢幕座標上——不注入也不改寫任何原生節點，
/// 所以沒有 addon finalize 期間的生命週期風險。零 hook。
/// 標記圖示走 Lumina Marker 表（不寫死圖示 ID）。
/// DR 原版閉源（開源同族模組 AutoDisplayMarkerInPartyList 用 hook + KamiToolKit 原生節點），此處以疊圖重寫。
/// </summary>
public sealed unsafe class MarkerInPartyList : TcModule
{
    public override string InternalName => "MarkerInPartyList";
    public override string DisplayName => "小隊列表顯示目標標記";

    public override string Description =>
        "把場上的目標標記（攻擊1／綁2／止3 等）同步顯示在小隊列表對應隊員的名字前，" +
        "不必回頭看場上光柱就知道誰被標了。可另外隱藏小隊列表原本的隊員序號避免重疊。";

    public override bool HasConfigUI => true;

    private const int MaxPartyRows = 8;

    /// <summary>小隊列表隊員欄位的節點 ID 起點（10..17）。</summary>
    private const uint PartyRowNodeIdBase = 10;

    /// <summary>隊員欄位內的序號文字節點 ID。</summary>
    private const uint MemberNumberNodeId = 16;

    /// <summary>列 index → 標記圖示 ID（0＝無標記）。</summary>
    private readonly uint[] rowMarkerIcon = new uint[MaxPartyRows];

    private bool numbersHidden;

    private MarkerInPartyListConfig Config => Plugin.Instance.Config.MarkerInPartyList;

    protected override void OnEnable()
    {
        Svc.PluginInterface.UiBuilder.Draw += DrawOverlay;
    }

    protected override void OnDisable()
    {
        Svc.PluginInterface.UiBuilder.Draw -= DrawOverlay;

        // 一定要把序號還原，否則要重載外掛才會回來
        SetMemberNumbersVisible(true);
        System.Array.Clear(rowMarkerIcon);
    }

    private void DrawOverlay()
    {
        var addon = UiHelper.GetAddon("_PartyList");
        if (!UiHelper.IsReady(addon))
        {
            if (numbersHidden) SetMemberNumbersVisible(true);
            return;
        }

        RefreshMarkers();

        var anyMarker = false;
        for (var i = 0; i < MaxPartyRows; i++)
        {
            if (rowMarkerIcon[i] != 0) anyMarker = true;
        }

        SetMemberNumbersVisible(!(Config.HideMemberNumbers && anyMarker));

        if (!anyMarker) return;

        var drawList = ImGui.GetBackgroundDrawList();
        var scale = addon->Scale;
        var size = Config.IconSize * scale;

        for (var i = 0; i < MaxPartyRows; i++)
        {
            var iconId = rowMarkerIcon[i];
            if (iconId == 0) continue;

            var node = addon->GetNodeById(PartyRowNodeIdBase + (uint)i);
            if (node == null || !node->IsVisible()) continue;

            var wrap = GameIcons.TryGet(iconId);
            if (wrap == null) continue;

            var origin = new Vector2(
                node->ScreenX + (Config.OffsetX * scale),
                node->ScreenY + (Config.OffsetY * scale));

            drawList.AddImage(wrap.Handle, origin, origin + new Vector2(size, size));
        }
    }

    /// <summary>重掃場上標記並映射到小隊列表列 index。</summary>
    private void RefreshMarkers()
    {
        System.Array.Clear(rowMarkerIcon);

        var controller = MarkingController.Instance();
        var agentHud = AgentHUD.Instance();
        if (controller == null || agentHud == null) return;

        var markerSheet = Svc.Data.GetExcelSheet<Marker>();
        var markers = controller->Markers;

        for (var markerIndex = 0; markerIndex < markers.Length; markerIndex++)
        {
            var objectId = markers[markerIndex].ObjectId;
            if (objectId is 0 or 0xE000_0000) continue;

            // 標記表 row = 標記索引 + 1（row 0 是空白列）
            var row = markerSheet.GetRowOrDefault((uint)(markerIndex + 1));
            if (row == null || row.Value.Icon == 0) continue;

            if (!TryFindPartyRow(agentHud, objectId, out var rowIndex)) continue;

            rowMarkerIcon[rowIndex] = (uint)row.Value.Icon;
        }
    }

    private static bool TryFindPartyRow(AgentHUD* agentHud, uint entityId, out int rowIndex)
    {
        rowIndex = -1;

        var count = agentHud->PartyMemberCount;
        if (count > MaxPartyRows) count = MaxPartyRows;

        for (var i = 0; i < count; i++)
        {
            if (agentHud->PartyMembers[i].EntityId != entityId) continue;
            rowIndex = i;
            return true;
        }

        return false;
    }

    /// <summary>切換小隊列表原生序號文字節點的可見性（唯一會動到原生節點的地方，停用時必還原）。</summary>
    private void SetMemberNumbersVisible(bool visible)
    {
        // 不能靠快取狀態提早返回：小隊列表 addon 重建（換區／重組隊）後節點會回到預設可見，
        // 快取若說「已隱藏」就再也不會補上，所以每次都逐節點比對實際狀態。
        var addon = UiHelper.GetAddon("_PartyList");
        if (!UiHelper.IsReady(addon))
        {
            // addon 不在，狀態視為已還原（addon 重建時節點本來就是預設可見）
            numbersHidden = false;
            return;
        }

        for (var i = 0u; i < MaxPartyRows; i++)
        {
            var node = addon->GetNodeById(PartyRowNodeIdBase + i);
            if (node == null) continue;

            var component = node->GetComponent();
            if (component == null) continue;

            var textNode = component->UldManager.SearchNodeById(MemberNumberNodeId);
            if (textNode == null) continue;
            if (textNode->IsVisible() == visible) continue;

            textNode->ToggleVisibility(visible);
        }

        numbersHidden = !visible;
    }

    public override void DrawConfig()
    {
        ImGui.SetNextItemWidth(180f);
        var size = Config.IconSize;
        if (ImGui.SliderFloat("圖示大小", ref size, 12f, 48f, "%.0f"))
        {
            Config.IconSize = size;
            Plugin.Instance.Config.Save();
        }

        ImGui.SetNextItemWidth(180f);
        var offset = new Vector2(Config.OffsetX, Config.OffsetY);
        if (ImGui.InputFloat2("圖示偏移（X／Y）", ref offset))
        {
            Config.OffsetX = offset.X;
            Config.OffsetY = offset.Y;
            Plugin.Instance.Config.Save();
        }

        var hideNumbers = Config.HideMemberNumbers;
        if (ImGui.Checkbox("有標記時隱藏小隊列表的隊員序號", ref hideNumbers))
        {
            Config.HideMemberNumbers = hideNumbers;
            Plugin.Instance.Config.Save();
            if (!hideNumbers) SetMemberNumbersVisible(true);
        }

        ImGui.TextDisabled("跨界（Cross-world）小隊的非同組成員無法對位，屬已知限制。");
    }
}

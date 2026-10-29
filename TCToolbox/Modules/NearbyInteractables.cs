using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 周邊可互動物件：資訊列顯示半徑內可互動（遊戲判定可選取）的 NPC／物件／寶箱等數量，
/// 清單照距離排序，點一列＝選取為目標。只在點選時選取，不自動選取、不互動。
/// </summary>
/// <remarks>
/// 判準＝種類白名單＋<c>IsTargetable</c>＋（可關）名稱非空。種類一律用 CS 的數值：
/// Dalamud 列舉 0x0E 之後的名字與 CS 對不上。與寶藏判準相同的 EventObj（深宮銀／金寶箱）歸進「寶箱」。
/// </remarks>
public sealed class NearbyInteractables : NearbyObjectListModule
{
    public override string InternalName => "NearbyInteractables";
    public override string DisplayName => "周邊可互動物件";

    public override string Description =>
        "在伺服器資訊列顯示附近可互動的 NPC／物件／寶箱／乙太之光等的數量，滑鼠移上顯示最近幾個，" +
        "點擊開啟照距離排序的清單視窗，點清單上的一列即可選取為目標。只在你點選時才選取，不會自動選取或互動。";

    public override ModuleCategory Category => ModuleCategory.Misc;

    private const byte KindEventNpc = 3;
    private const byte KindTreasure = 4;
    private const byte KindAetheryte = 5;
    private const byte KindGatheringPoint = 6;
    private const byte KindEventObj = 7;
    private const byte KindRetainer = 10;
    private const byte KindAreaObject = 11;
    private const byte KindHousing = 12;
    private const byte KindReactionEventObject = 14;
    private const byte KindCardStand = 16;

    private const byte CatNpc = 1;
    private const byte CatEventObj = 2;
    private const byte CatTreasure = 3;
    private const byte CatAetheryte = 4;
    private const byte CatGathering = 5;
    private const byte CatHousing = 6;
    private const byte CatRetainer = 7;
    private const byte CatOther = 8;

    private static readonly string[] CategoryLabels =
        ["?", "NPC", "物件", "寶箱", "乙太之光", "採集點", "住宅物件", "僱員", "其他"];

    private static readonly string HexagonText = SeIconChar.Hexagon.ToIconString();

    private static readonly NearbyColumn[] ColumnSet =
    [
        new("種類", 72f),
        new("名稱", 0f),
        new("距離", 60f, "角色中心到物件中心的直線距離"),
    ];

    private byte acceptedCategory;

    private NearbyInteractablesConfig Config => Plugin.Instance.Config.NearbyInteractables;

    protected override string DtrTitle => "TC Toolbox 周邊可互動物件";
    protected override string WindowId => "###TCToolboxNearbyInteractables";
    protected override string PollKey => "NearbyInteractables-Poll";
    protected override int PollIntervalMs => 500;
    protected override string Noun => "可互動物件";
    protected override string CountUnit => "個";
    protected override ref float MaxDistanceField => ref Config.MaxDistance;
    protected override ref bool HideInCombatField => ref Config.HideInCombat;
    protected override NearbyColumn[] Columns => ColumnSet;
    protected override bool HideUnnamed => Config.HideUnnamed;

    protected override string? NothingSelectedReason
    {
        get
        {
            var c = Config;
            var any = c.ShowNpc || c.ShowEventObj || c.ShowTreasure || c.ShowAetheryte ||
                      c.ShowGatheringPoint || c.ShowHousing || c.ShowRetainer || c.ShowOther;
            return any ? null : "沒有勾選任何種類（清單視窗上方可以勾選）。";
        }
    }

    protected override void OnEnable()
    {
        ChestIdentity.EnsureBuilt();
        base.OnEnable();
    }

    protected override bool KindWanted(byte kind)
    {
        var c = Config;
        return kind switch
        {
            KindEventNpc => c.ShowNpc,
            KindEventObj => c.ShowEventObj || c.ShowTreasure,
            KindTreasure => c.ShowTreasure,
            KindAetheryte => c.ShowAetheryte,
            KindGatheringPoint => c.ShowGatheringPoint,
            KindHousing => c.ShowHousing,
            KindRetainer => c.ShowRetainer,
            KindAreaObject or KindReactionEventObject or KindCardStand => c.ShowOther,
            _ => false,
        };
    }

    protected override bool Accept(IGameObject obj, byte kind)
    {
        var category = kind switch
        {
            KindEventNpc => CatNpc,
            KindEventObj => ChestIdentity.IsChest(obj) ? CatTreasure : CatEventObj,
            KindTreasure => CatTreasure,
            KindAetheryte => CatAetheryte,
            KindGatheringPoint => CatGathering,
            KindHousing => CatHousing,
            KindRetainer => CatRetainer,
            _ => CatOther,
        };

        if (!CategoryShown(category)) return false;
        if (!obj.IsTargetable) return false;

        acceptedCategory = category;
        return true;
    }

    protected override void Fill(NearbyRow row, IGameObject obj, ulong localId) => row.Category = acceptedCategory;

    private bool CategoryShown(byte category)
    {
        var c = Config;
        return category switch
        {
            CatNpc => c.ShowNpc,
            CatEventObj => c.ShowEventObj,
            CatTreasure => c.ShowTreasure,
            CatAetheryte => c.ShowAetheryte,
            CatGathering => c.ShowGatheringPoint,
            CatHousing => c.ShowHousing,
            CatRetainer => c.ShowRetainer,
            _ => c.ShowOther,
        };
    }

    private static string CategoryLabel(byte category) =>
        category < CategoryLabels.Length ? CategoryLabels[category] : CategoryLabels[0];

    protected override SeString BuildDtrText(string countText) => new(new TextPayload(HexagonText + countText));

    protected override int TooltipKey(NearbyRow row) => row.Category;

    protected override void AppendTooltipLine(StringBuilder sb, NearbyRow row) =>
        sb.Append('[').Append(CategoryLabel(row.Category)).Append("] ").Append(row.Name);

    protected override string FirstCellText(NearbyRow row) => CategoryLabel(row.Category);

    protected override void DrawOtherCells(NearbyRow row)
    {
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(row.Name);
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(row.DistanceText);
    }

    protected override void DrawWindowFilters()
    {
        var c = Config;
        FilterCheckbox("NPC", ref c.ShowNpc, "可以對話的 NPC（任務、商店等）");
        ImGui.SameLine();
        FilterCheckbox("物件", ref c.ShowEventObj, "場景上可以調查的物件（任務物件、風脈泉等）");
        ImGui.SameLine();
        FilterCheckbox("寶箱", ref c.ShowTreasure, "寶箱，含深層迷宮的銀／金寶箱");
        ImGui.SameLine();
        FilterCheckbox("乙太之光", ref c.ShowAetheryte, null);
        FilterCheckbox("採集點", ref c.ShowGatheringPoint, "目前職業採不了的採集點，遊戲通常不讓選取，就不會出現");
        ImGui.SameLine();
        FilterCheckbox("住宅物件", ref c.ShowHousing, "房屋裡的傳喚鈴、家具等");
        ImGui.SameLine();
        FilterCheckbox("僱員", ref c.ShowRetainer, "傳喚出來的僱員");
        ImGui.SameLine();
        FilterCheckbox("其他", ref c.ShowOther, "區域物件、反應物件、卡片台等用途較雜的種類");
    }

    private void FilterCheckbox(string label, ref bool field, string? tooltip)
    {
        if (ImGui.Checkbox(label, ref field))
        {
            Plugin.Instance.Config.Save();
            RequestRepoll();
        }

        if (tooltip != null && ImGui.IsItemHovered())
            ImGui.SetTooltip(tooltip);
    }

    protected override void DrawExtraConfig()
    {
        var c = Config;
        FilterCheckbox("不列沒有名字的物件", ref c.HideUnnamed, "沒有名字的物件多半是輔助用的隱形物件，選了也看不出是什麼。");

        ImGui.TextUnformatted("種類（清單視窗上方也可以勾）：");
        DrawWindowFilters();
    }
}

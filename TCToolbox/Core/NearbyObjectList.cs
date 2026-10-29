using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Interface.Utility;
using Dalamud.Plugin.Services;
using CSGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace TCToolbox.Core;

/// <summary>清單的一列。只放純量與字串，實例由物件池每輪重填，只在填它的那一輪有效。</summary>
/// <remarks>
/// 🔴 不放 <see cref="IGameObject"/> 也不放位址：要用時以 <see cref="ObjectIndex"/> 重查，
/// 並核對 <see cref="GameObjectId"/>／<see cref="BaseId"/>／<see cref="Kind"/> 四項。
/// 客戶端物件的 GameObjectId 會重複（同 BaseId 的兩個 EventObj），所以不能只用 id 重查。
/// </remarks>
public sealed class NearbyRow
{
    public ushort ObjectIndex;
    public ulong GameObjectId;
    public uint BaseId;

    /// <summary>原始 ObjectKind 數值（CS 的編號；Dalamud 列舉 0x0E 之後的名字與 CS 對不上）。</summary>
    public byte Kind;

    /// <summary>模組自己的分類（可互動清單的種類），沒用到就是 0。</summary>
    public byte Category;

    public string Name = string.Empty;
    public float Distance;
    public string DistanceText = string.Empty;
    public string LevelText = string.Empty;
    public string HpText = string.Empty;
    public byte Level;
    public bool TargetsMe;
    public bool InCombat;

    /// <summary>點選時重查對不上＝已經不在了；只影響這一輪的顯示，下一輪重填時清掉。</summary>
    public bool Gone;
}

/// <summary>清單視窗的一欄。<see cref="Width"/> ≤ 0＝伸縮欄。</summary>
public readonly record struct NearbyColumn(string Header, float Width, string? Tooltip = null);

/// <summary>
/// 「周邊物件清單」模組的共用底座：輪詢 ObjectTable、物件池、名稱快取、照距離排序、資訊列（DTR）、清單視窗、點選選取。
/// 子類別只決定「篩哪些物件」與「顯示哪些欄位」。
/// </summary>
/// <remarks>
/// 契約：輪詢在 <c>Framework.Update</c>、清單視窗在 <c>UiBuilder.Draw</c>，兩者都在框架執行緒；
/// 不跨幀保存任何原生指標或 ObjectTable 包裝。只在使用者點清單列時選取目標，不自動選取、不互動。
/// </remarks>
public abstract unsafe class NearbyObjectListModule : TcModule
{
    public override bool HasConfigUI => true;

    public override bool IsManualTrigger => true;

    private const int DtrPreviewCount = 5;
    private const int MaxCachedTenths = 2000;
    private const int MaxCachedInt = 999;
    private const int MaxLoggedMisses = 64;

    private static readonly Vector4 ColorGone = new(0.55f, 0.55f, 0.55f, 1f);
    private static readonly Vector4 ColorWarn = new(1f, 0.65f, 0.25f, 1f);
    private static readonly string?[] DistanceTexts = new string?[MaxCachedTenths + 1];
    private static readonly string?[] IntTexts = new string?[MaxCachedInt + 1];
    private static readonly Comparison<NearbyRow> ByDistance = static (a, b) =>
    {
        var c = a.Distance.CompareTo(b.Distance);
        return c != 0 ? c : a.ObjectIndex.CompareTo(b.ObjectIndex);
    };

    private readonly List<NearbyRow> rows = [];
    private readonly List<NearbyRow> pool = [];
    private readonly List<NearbyRow> visible = [];
    private NameEntry?[] names = [];

    private IDtrBarEntry? dtrEntry;
    private bool windowOpen;
    private string search = string.Empty;
    private readonly byte[] searchBuffer = new byte[256];
    private bool hasPlayer;
    private bool noneSelected;
    private uint pollSerial;
    private uint visibleSerial = uint.MaxValue;
    private string visibleSearch = string.Empty;
    private string windowTitle = string.Empty;
    private int windowTitleCount = -1;

    private int dtrTextCount = int.MinValue;
    private int dtrTooltipCount = int.MinValue;
    private int dtrTooltipRadius;
    private int dtrTooltipSummary;
    private readonly string[] dtrTooltipNames = new string[DtrPreviewCount];
    private readonly int[] dtrTooltipKeys = new int[DtrPreviewCount];

    private bool pendingActive;
    private ushort pendingIndex;
    private ulong pendingId;
    private string pendingName = string.Empty;
    private readonly HashSet<(ushort Index, ulong Id)> loggedMisses = [];

    /// <summary>名稱與解析當下的 64 位元組原文；位元組沒變就沿用字串，鍵＝ObjectIndex。</summary>
    private sealed class NameEntry
    {
        public readonly byte[] Bytes = new byte[64];
        public string Text = string.Empty;
        public bool Valid;
    }

    /// <summary>存進 Dalamud 的 DTR 排序／隱藏設定，定了就不要改。</summary>
    protected abstract string DtrTitle { get; }

    /// <summary>視窗標題 <c>###</c> 之後的固定 ID（含 <c>###</c>）。</summary>
    protected abstract string WindowId { get; }

    protected abstract string PollKey { get; }

    protected abstract int PollIntervalMs { get; }

    /// <summary>使用者看得到的名詞（「可互動物件」「敵人」）與量詞（「個」「隻」）。</summary>
    protected abstract string Noun { get; }

    protected abstract string CountUnit { get; }

    protected abstract ref float MaxDistanceField { get; }

    protected abstract ref bool HideInCombatField { get; }

    protected abstract NearbyColumn[] Columns { get; }

    /// <summary>非 null＝篩選全關：資訊列顯示「—」、清單顯示這句原因。</summary>
    protected virtual string? NothingSelectedReason => null;

    protected virtual bool HideUnnamed => false;

    /// <summary>只看種類的快速篩選；過了才讀位置與呼叫 <see cref="Accept"/>。</summary>
    protected abstract bool KindWanted(byte kind);

    /// <summary>完整篩選（含可否選取）。<paramref name="obj"/> 只在這次呼叫內有效。</summary>
    protected abstract bool Accept(IGameObject obj, byte kind);

    protected virtual float DistanceTo(IGameObject obj, Vector3 origin) => Vector3.Distance(origin, obj.Position);

    /// <summary>填子類別自己的欄位；<paramref name="obj"/> 只在這次呼叫內有效。</summary>
    protected virtual void Fill(NearbyRow row, IGameObject obj, ulong localId)
    {
    }

    /// <summary>每輪排序之後呼叫（已確定有自己的角色）。</summary>
    protected virtual void AfterPoll()
    {
    }

    protected abstract SeString BuildDtrText(string countText);

    /// <summary>提示裡這一列除了名稱以外的輸入；變了才重組提示。</summary>
    protected abstract int TooltipKey(NearbyRow row);

    protected abstract void AppendTooltipLine(StringBuilder sb, NearbyRow row);

    /// <summary>提示的附加摘要與它的輸入（例如以我為目標的數量）；預設沒有。</summary>
    protected virtual int TooltipSummaryKey => 0;

    protected virtual void AppendTooltipSummary(StringBuilder sb)
    {
    }

    protected abstract string FirstCellText(NearbyRow row);

    /// <summary>第二欄之後的格子（呼叫時已在第一欄之後）。</summary>
    protected abstract void DrawOtherCells(NearbyRow row);

    /// <summary>清單視窗上方的篩選列；預設沒有。</summary>
    protected virtual void DrawWindowFilters()
    {
    }

    /// <summary>設定頁面上子類別自己的選項（畫在共用選項之後）。</summary>
    protected virtual void DrawExtraConfig()
    {
    }

    protected IReadOnlyList<NearbyRow> Rows => rows;

    protected float MaxDistance => Math.Clamp(MaxDistanceField, 5f, 200f);

    /// <summary>設定改了之後讓下一幀就重新輪詢。</summary>
    protected void RequestRepoll() => Throttle.Reset(PollKey);

    protected override void OnEnable()
    {
        names = new NameEntry?[Svc.Objects.Length];
        dtrTextCount = int.MinValue;
        dtrTooltipCount = int.MinValue;

        dtrEntry = Svc.DtrBar.Get(DtrTitle);
        dtrEntry.Shown = false;
        dtrEntry.OnClick = ev =>
        {
            if (ev.ClickType == MouseClickType.Right)
                Plugin.Instance.ToggleMainWindow();
            else
                windowOpen = !windowOpen;
        };

        Svc.Framework.Update += OnUpdate;
        Svc.PluginInterface.UiBuilder.Draw += DrawWindow;
    }

    protected override void OnDisable()
    {
        Svc.Framework.Update -= OnUpdate;
        Svc.PluginInterface.UiBuilder.Draw -= DrawWindow;

        dtrEntry?.Remove();
        dtrEntry = null;
        rows.Clear();
        pool.Clear();
        visible.Clear();
        names = [];
        loggedMisses.Clear();
        pendingActive = false;
        windowOpen = false;
        visibleSerial = uint.MaxValue;
        windowTitleCount = -1;
    }

    private void OnUpdate(IFramework framework)
    {
        if (dtrEntry == null) return;
        if (!Throttle.Pass(PollKey, PollIntervalMs)) return;

        pollSerial++;
        rows.Clear();

        var local = Svc.Objects.LocalPlayer;
        if (local == null)
        {
            hasPlayer = false;
            pendingActive = false;
            dtrEntry.Shown = false;
            return;
        }

        hasPlayer = true;
        var origin = local.Position;
        var localId = local.GameObjectId;
        var localIndex = local.ObjectIndex;
        var max = MaxDistance;
        noneSelected = NothingSelectedReason != null;

        if (!noneSelected)
        {
            var used = 0;
            foreach (var obj in Svc.Objects)
            {
                var kind = (byte)obj.ObjectKind;
                if (!KindWanted(kind)) continue;
                if (obj.ObjectIndex == localIndex) continue;

                var distance = DistanceTo(obj, origin);
                if (distance > max) continue;
                if (!Accept(obj, kind)) continue;

                var name = ReadName(obj);
                if (HideUnnamed && name.Length == 0) continue;

                if (used == pool.Count) pool.Add(new NearbyRow());
                var row = pool[used++];
                row.ObjectIndex = obj.ObjectIndex;
                row.GameObjectId = obj.GameObjectId;
                row.BaseId = obj.BaseId;
                row.Kind = kind;
                row.Category = 0;
                row.Name = name;
                row.Distance = distance;
                row.DistanceText = DistanceText(distance);
                row.Gone = false;
                Fill(row, obj, localId);
                rows.Add(row);
            }

            rows.Sort(ByDistance);
        }

        CheckPendingSelection();
        AfterPoll();
        UpdateDtr();
    }

    /// <summary>名稱位元組和上次解析時一樣就沿用字串，避免每輪 <c>Name</c> 解析的配置。</summary>
    private string ReadName(IGameObject obj)
    {
        var address = obj.Address;
        int index = obj.ObjectIndex;
        if (address == nint.Zero || index >= names.Length) return obj.Name.TextValue;

        var entry = names[index] ??= new NameEntry();
        var bytes = ((CSGameObject*)address)->Name;
        if (entry.Valid && bytes.SequenceEqual(entry.Bytes)) return entry.Text;

        // 先解析再記位元組：解析擲例外時不留下「看過這組位元組」的紀錄。
        var text = obj.Name.TextValue;
        bytes.CopyTo(entry.Bytes);
        entry.Text = text;
        entry.Valid = true;
        return text;
    }

    private void CheckPendingSelection()
    {
        if (!pendingActive) return;
        pendingActive = false;

        var target = Svc.Targets.Target;
        if (target != null && target.GameObjectId == pendingId && target.ObjectIndex == pendingIndex) return;

        if (loggedMisses.Count >= MaxLoggedMisses) loggedMisses.Clear();
        if (!loggedMisses.Add((pendingIndex, pendingId))) return;

        var current = target == null ? "無" : $"{target.Name.TextValue}（{target.GameObjectId:X}）";
        Svc.Log.Information(
            $"[{InternalName}] 點選「{pendingName}」（{pendingId:X}，第 {pendingIndex} 格）後目標沒有換過去，目前目標：{current}。" +
            "可能是遊戲目前不允許選取，或被其他插件改回。");
    }

    private void TrySelect(NearbyRow row)
    {
        var obj = Svc.Objects[row.ObjectIndex];
        if (obj == null || obj.GameObjectId != row.GameObjectId || obj.BaseId != row.BaseId ||
            (byte)obj.ObjectKind != row.Kind || !obj.IsTargetable)
        {
            row.Gone = true;
            return;
        }

        Svc.Targets.Target = obj;
        pendingActive = true;
        pendingIndex = row.ObjectIndex;
        pendingId = row.GameObjectId;
        pendingName = row.Name;
    }

    #region 資訊列

    private void UpdateDtr()
    {
        if (dtrEntry == null) return;

        if (HideInCombatField && Svc.Condition[ConditionFlag.InCombat])
        {
            dtrEntry.Shown = false;
            return;
        }

        dtrEntry.Shown = true;

        var count = noneSelected ? -1 : rows.Count;
        if (dtrTextCount != count)
        {
            dtrTextCount = count;
            dtrEntry.Text = BuildDtrText(noneSelected ? "—" : IntText(count));
        }

        var radius = (int)MathF.Round(MaxDistance);
        if (DtrTooltipIsCurrent(count, radius)) return;
        RememberDtrTooltipInputs(count, radius);

        var sb = new StringBuilder();
        sb.Append("TC Toolbox — 周邊").Append(Noun).Append("（半徑 ").Append(radius).Append(" 米）\n");
        if (noneSelected)
        {
            sb.Append(NothingSelectedReason).Append('\n');
        }
        else if (rows.Count == 0)
        {
            sb.Append("附近沒有").Append(Noun).Append('\n');
        }
        else
        {
            var shown = Math.Min(rows.Count, DtrPreviewCount);
            for (var i = 0; i < shown; i++)
            {
                AppendTooltipLine(sb, rows[i]);
                sb.Append('\n');
            }

            if (rows.Count > DtrPreviewCount)
                sb.Append("…等共 ").Append(rows.Count).Append(' ').Append(CountUnit).Append('\n');
            AppendTooltipSummary(sb);
        }

        sb.Append("左鍵：開啟／關閉清單\n右鍵：開啟／關閉 TC Toolbox 設定");
        dtrEntry.Tooltip = sb.ToString();
    }

    private bool DtrTooltipIsCurrent(int count, int radius)
    {
        if (dtrTooltipCount != count || dtrTooltipRadius != radius || dtrTooltipSummary != TooltipSummaryKey)
            return false;

        var shown = Math.Min(rows.Count, DtrPreviewCount);
        for (var i = 0; i < shown; i++)
        {
            var row = rows[i];
            if (!string.Equals(row.Name, dtrTooltipNames[i], StringComparison.Ordinal) ||
                TooltipKey(row) != dtrTooltipKeys[i])
                return false;
        }

        return true;
    }

    private void RememberDtrTooltipInputs(int count, int radius)
    {
        dtrTooltipCount = count;
        dtrTooltipRadius = radius;
        dtrTooltipSummary = TooltipSummaryKey;

        var shown = Math.Min(rows.Count, DtrPreviewCount);
        for (var i = 0; i < shown; i++)
        {
            dtrTooltipNames[i] = rows[i].Name;
            dtrTooltipKeys[i] = TooltipKey(rows[i]);
        }
    }

    #endregion

    #region 清單視窗

    private void DrawWindow()
    {
        if (!windowOpen) return;

        if (windowTitleCount != rows.Count)
        {
            windowTitleCount = rows.Count;
            windowTitle = $"{DisplayName} ({rows.Count}){WindowId}";
        }

        ImGui.SetNextWindowSize(new Vector2(440, 360) * ImGuiHelpers.GlobalScale, ImGuiCond.FirstUseEver);
        if (ImGui.Begin(windowTitle, ref windowOpen))
            DrawWindowBody();
        ImGui.End();
    }

    private void DrawWindowBody()
    {
        DrawWindowFilters();

        // 位元組緩衝版：字串版每幀都會重建一個字串，這裡只在內容改了才轉。
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.InputTextWithHint("##search", "搜尋名稱…", searchBuffer))
        {
            var end = Array.IndexOf(searchBuffer, (byte)0);
            search = Encoding.UTF8.GetString(searchBuffer, 0, end < 0 ? searchBuffer.Length : end);
        }

        if (!hasPlayer)
        {
            ImGui.TextDisabled("讀不到自己的角色（換區或登出中），清單暫停更新。");
            return;
        }

        if (noneSelected)
        {
            ImGui.TextColored(ColorWarn, NothingSelectedReason ?? string.Empty);
            return;
        }

        RefreshVisible();
        if (visible.Count == 0)
        {
            ImGui.TextDisabled(rows.Count == 0 ? "半徑內沒有符合條件的項目。" : "沒有符合搜尋的項目。");
            return;
        }

        var target = Svc.Targets.Target;
        var targetId = target?.GameObjectId ?? 0;
        var targetIndex = target != null ? target.ObjectIndex : -1;

        var columns = Columns;
        if (!ImGui.BeginTable("##nearby", columns.Length,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.ScrollY))
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        foreach (var column in columns)
        {
            if (column.Width > 0)
                ImGui.TableSetupColumn(column.Header, ImGuiTableColumnFlags.WidthFixed, column.Width * ImGuiHelpers.GlobalScale);
            else
                ImGui.TableSetupColumn(column.Header, ImGuiTableColumnFlags.WidthStretch);
        }

        DrawHeaderRow(columns);

        // Begin 把這個區域變數的位址記進 ImGui（全部插件共用）；離開迴圈的每一條路都要 End。
        var clipper = default(ImGuiListClipper);
        clipper.Begin(visible.Count);
        try
        {
            while (clipper.Step())
            {
                for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                    DrawRow(visible[i], targetId, targetIndex);
            }
        }
        finally
        {
            clipper.End();
        }

        ImGui.EndTable();
    }

    private static void DrawHeaderRow(NearbyColumn[] columns)
    {
        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
        for (var i = 0; i < columns.Length; i++)
        {
            if (!ImGui.TableSetColumnIndex(i)) continue;
            ImGui.TableHeader(columns[i].Header);
            if (columns[i].Tooltip is { } tip && ImGui.IsItemHovered())
                ImGui.SetTooltip(tip);
        }
    }

    private void DrawRow(NearbyRow row, ulong targetId, int targetIndex)
    {
        ImGui.TableNextRow();
        ImGui.PushID(row.ObjectIndex);
        var gone = row.Gone;
        if (gone) ImGui.PushStyleColor(ImGuiCol.Text, ColorGone);

        ImGui.TableNextColumn();
        var isTarget = row.GameObjectId == targetId && row.ObjectIndex == targetIndex;
        if (ImGui.Selectable(FirstCellText(row), isTarget, ImGuiSelectableFlags.SpanAllColumns))
            TrySelect(row);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(row.Gone ? "已經不在了（下一輪更新時會從清單移除）" : "點擊選取為目標");

        DrawOtherCells(row);

        if (gone) ImGui.PopStyleColor();
        ImGui.PopID();
    }

    private void RefreshVisible()
    {
        if (visibleSerial == pollSerial && string.Equals(visibleSearch, search, StringComparison.Ordinal)) return;

        visibleSerial = pollSerial;
        visibleSearch = search;
        visible.Clear();
        var filter = !string.IsNullOrWhiteSpace(search);
        foreach (var row in rows)
        {
            if (filter && !row.Name.Contains(search, StringComparison.OrdinalIgnoreCase)) continue;
            visible.Add(row);
        }
    }

    #endregion

    #region 設定

    public override void DrawConfig()
    {
        var enabled = IsEnabled;
        if (!enabled) ImGui.BeginDisabled();
        if (ImGui.Button(windowOpen ? "關閉清單視窗" : "開啟清單視窗"))
            windowOpen = !windowOpen;
        if (!enabled) ImGui.EndDisabled();
        if (!enabled && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("模組啟用後才能開啟清單。");

        ref var hide = ref HideInCombatField;
        if (ImGui.Checkbox("戰鬥中隱藏資訊列項目", ref hide))
            Plugin.Instance.Config.Save();

        ImGui.SetNextItemWidth(200f * ImGuiHelpers.GlobalScale);
        ref var distance = ref MaxDistanceField;
        if (ImGui.SliderFloat("半徑（米）", ref distance, 5f, 200f, "%.0f"))
        {
            distance = Math.Clamp(distance, 5f, 200f);
            RequestRepoll();
        }

        if (ImGui.IsItemDeactivatedAfterEdit())
            Plugin.Instance.Config.Save();

        DrawExtraConfig();
    }

    #endregion

    #region 數字文字快取

    /// <summary>「12.3m」；0～200 米每 0.1 米只建一次字串。</summary>
    protected static string DistanceText(float meters)
    {
        var tenths = (int)MathF.Round(meters * 10f);
        if (tenths < 0) tenths = 0;
        if (tenths > MaxCachedTenths) return meters.ToString("0.0", CultureInfo.InvariantCulture) + "m";

        return DistanceTexts[tenths] ??= (tenths / 10f).ToString("0.0", CultureInfo.InvariantCulture) + "m";
    }

    protected static string IntText(int value)
    {
        if (value < 0 || value > MaxCachedInt) return value.ToString(CultureInfo.InvariantCulture);

        return IntTexts[value] ??= value.ToString(CultureInfo.InvariantCulture);
    }

    #endregion
}

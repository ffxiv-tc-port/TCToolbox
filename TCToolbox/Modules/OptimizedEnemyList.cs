using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Arrays;
using FFXIVClientStructs.FFXIV.Component.GUI;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 敵對列表界面最佳化：在敵對列表每一列旁疊上體力數值／百分比、詠唱技名與剩餘秒數，
/// 以及「這隻正在打你」的標示。
/// ⚠️ 與 DR 原版的差異：DR 的 OptimizedEnemyList 核心是 3 個 MemoryPatch（改寫遊戲程式碼），
/// 屬紅線一律不抄。本模組只做「顯示已存在的資訊」：資料全部讀自遊戲自己餵給敵對列表 UI 的
/// NumberArray／StringArray 與 ObjectTable，不改任何行為、不動記憶體、零 hook；
/// UI 走 ImGui 疊圖，不注入原生節點（避開 addon finalize 生命週期風險）。
/// </summary>
public sealed unsafe class OptimizedEnemyList : TcModule
{
    public override string InternalName => "OptimizedEnemyList";
    public override string DisplayName => "敵對列表界面最佳化";

    public override string Description =>
        "在敵對列表每列疊上體力數值／百分比、對方正在詠唱的技名與剩餘秒數，並標示哪一隻正以你為目標。" +
        "純顯示，不改變任何遊戲行為（DR 原版用記憶體 patch 實作的部分一律不抄）。";

    public override bool HasConfigUI => true;

    private const int MaxRows = 8;

    /// <summary>敵對列表 8 列在 UldManager.NodeList 的索引範圍（艦隊生產環境已驗證的慣例）。</summary>
    private const int RowNodeIndexStart = 4;
    private const int RowNodeIndexEnd = 11;

    private sealed record RowInfo(
        int Slot,
        uint EntityId,
        int RemainingHpPercent,
        string CastName,
        float CastRemaining,
        uint CurrentHp,
        uint MaxHp,
        bool TargetingLocalPlayer);

    private readonly List<RowInfo> rows = [];

    private OptimizedEnemyListConfig Config => Plugin.Instance.Config.EnemyList;

    protected override void OnEnable()
    {
        Svc.PluginInterface.UiBuilder.Draw += DrawOverlay;
    }

    protected override void OnDisable()
    {
        Svc.PluginInterface.UiBuilder.Draw -= DrawOverlay;
        rows.Clear();
    }

    private void DrawOverlay()
    {
        var addon = (AddonEnemyList*)UiHelper.GetAddon("_EnemyList");
        if (addon == null || !UiHelper.IsReady(&addon->AtkUnitBase)) return;

        CollectRows();
        if (rows.Count == 0) return;

        var rowPositions = ResolveRowPositions(&addon->AtkUnitBase);
        if (rowPositions == null) return;

        var scale = addon->AtkUnitBase.Scale;
        var drawList = ImGui.GetBackgroundDrawList();
        var fontSize = ImGui.GetFontSize() * Config.TextScale;

        foreach (var row in rows)
        {
            if (row.Slot < 0 || row.Slot >= rowPositions.Length) continue;

            var position = rowPositions[row.Slot];
            if (position.X <= 0 && position.Y <= 0) continue;

            var origin = position + new Vector2(Config.OffsetX * scale, Config.OffsetY * scale);
            var line = BuildLine(row);
            if (line.Length == 0) continue;

            var color = row.TargetingLocalPlayer && Config.HighlightTargetingYou
                            ? ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 0.45f, 0.4f, 1f))
                            : ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 0.9f));

            // 先畫一層深色描邊，讓文字在任何底色上都看得清楚
            var shadow = ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 0.85f));
            drawList.AddText(ImGui.GetFont(), fontSize, origin + new Vector2(1, 1), shadow, line);
            drawList.AddText(ImGui.GetFont(), fontSize, origin, color, line);
        }
    }

    private string BuildLine(RowInfo row)
    {
        var parts = new List<string>(3);

        if (Config.ShowHp && row.MaxHp > 0)
        {
            parts.Add(Config.CompactNumbers
                          ? $"{FormatCompact(row.CurrentHp)}/{FormatCompact(row.MaxHp)}"
                          : $"{row.CurrentHp:N0}/{row.MaxHp:N0}");
        }

        if (Config.ShowHpPercent)
            parts.Add($"{row.RemainingHpPercent}%");

        if (Config.ShowCast && row.CastName.Length > 0)
        {
            parts.Add(row.CastRemaining > 0f
                          ? $"{row.CastName} {row.CastRemaining:0.0}s"
                          : row.CastName);
        }

        if (Config.HighlightTargetingYou && row.TargetingLocalPlayer)
            parts.Add("◀");

        return string.Join("  ", parts);
    }

    /// <summary>中文簡寫（萬／億），台服玩家習慣的表達。</summary>
    private static string FormatCompact(uint value) => value switch
    {
        >= 100_000_000 => $"{value / 100_000_000f:0.##}億",
        >= 10_000 => $"{value / 10_000f:0.##}萬",
        _ => value.ToString(),
    };

    private void CollectRows()
    {
        rows.Clear();

        var numbers = EnemyListNumberArray.Instance();
        var strings = EnemyListStringArray.Instance();
        if (numbers == null) return;

        var localPlayerId = Svc.Objects.LocalPlayer?.EntityId ?? 0u;

        for (var i = 0; i < MaxRows; i++)
        {
            var entry = numbers->Enemies[i];
            if (!entry.ActiveInList) continue;

            var entityId = (uint)entry.EntityId;
            if (entityId is 0 or 0xE000_0000) continue;

            var castName = string.Empty;
            if (strings != null)
            {
                var ptr = strings->Members[i].Castname.Value;
                if (ptr != null)
                    castName = Dalamud.Memory.MemoryHelper.ReadSeStringNullTerminated((nint)ptr).TextValue;
            }

            uint currentHp = 0;
            uint maxHp = 0;
            var castRemaining = 0f;
            var targetingLocal = false;

            if (Svc.Objects.SearchByEntityId(entityId) is IBattleChara battleChara)
            {
                currentHp = battleChara.CurrentHp;
                maxHp = battleChara.MaxHp;

                if (battleChara.IsCasting)
                    castRemaining = Math.Max(0f, battleChara.TotalCastTime - battleChara.CurrentCastTime);

                targetingLocal = localPlayerId != 0 && battleChara.TargetObjectId == localPlayerId;
            }

            rows.Add(new RowInfo(i, entityId, entry.RemainingHPPercent, castName, castRemaining,
                                 currentHp, maxHp, targetingLocal));
        }
    }

    /// <summary>
    /// 取 8 列的螢幕座標。NodeList 的排列順序不保證，因此依 ScreenY 由上而下排序後
    /// 才對應到敵對列表的列序（第 0 列在最上）。
    /// </summary>
    private static Vector2[]? ResolveRowPositions(AtkUnitBase* addon)
    {
        if (addon->UldManager.NodeList == null) return null;
        if (addon->UldManager.NodeListCount <= RowNodeIndexEnd) return null;

        var found = new List<(float Y, Vector2 Position)>(MaxRows);
        for (var i = RowNodeIndexStart; i <= RowNodeIndexEnd; i++)
        {
            var node = addon->UldManager.NodeList[i];
            if (node == null || !node->IsVisible()) continue;
            found.Add((node->ScreenY, new Vector2(node->ScreenX, node->ScreenY)));
        }

        if (found.Count == 0) return null;

        found.Sort((a, b) => a.Y.CompareTo(b.Y));

        var result = new Vector2[MaxRows];
        for (var i = 0; i < found.Count && i < MaxRows; i++)
            result[i] = found[i].Position;

        return result;
    }

    public override void DrawConfig()
    {
        var showHp = Config.ShowHp;
        if (ImGui.Checkbox("顯示體力數值", ref showHp))
        {
            Config.ShowHp = showHp;
            Plugin.Instance.Config.Save();
        }

        ImGui.SameLine();
        var compact = Config.CompactNumbers;
        if (ImGui.Checkbox("用萬／億簡寫", ref compact))
        {
            Config.CompactNumbers = compact;
            Plugin.Instance.Config.Save();
        }

        var showPercent = Config.ShowHpPercent;
        if (ImGui.Checkbox("顯示體力百分比", ref showPercent))
        {
            Config.ShowHpPercent = showPercent;
            Plugin.Instance.Config.Save();
        }

        var showCast = Config.ShowCast;
        if (ImGui.Checkbox("顯示詠唱技名與剩餘秒數", ref showCast))
        {
            Config.ShowCast = showCast;
            Plugin.Instance.Config.Save();
        }

        var highlight = Config.HighlightTargetingYou;
        if (ImGui.Checkbox("標示正以你為目標的敵人", ref highlight))
        {
            Config.HighlightTargetingYou = highlight;
            Plugin.Instance.Config.Save();
        }

        ImGui.SetNextItemWidth(180f);
        var textScale = Config.TextScale;
        if (ImGui.SliderFloat("文字大小倍率", ref textScale, 0.6f, 2f, "%.2f"))
        {
            Config.TextScale = textScale;
            Plugin.Instance.Config.Save();
        }

        ImGui.SetNextItemWidth(180f);
        var offset = new Vector2(Config.OffsetX, Config.OffsetY);
        if (ImGui.InputFloat2("文字偏移（X／Y）", ref offset))
        {
            Config.OffsetX = offset.X;
            Config.OffsetY = offset.Y;
            Plugin.Instance.Config.Save();
        }

        ImGui.TextDisabled("DR 原版靠 3 個記憶體 patch 才做到的部分（改寫遊戲程式碼）一律不抄；\n本模組只顯示遊戲自己已經算好的資訊。");
    }
}

using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Config;
using Dalamud.Interface.Utility.Raii;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Arrays;
using FFXIVClientStructs.FFXIV.Component.GUI;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 敵對列表界面最佳化：在敵對列表每一列的**外側**疊上體力數值／百分比、詠唱剩餘秒數，
/// 以及「這隻正在打你」的標示。
/// 版面原則：疊圖只畫在整列矩形之外（預設右側），不覆蓋原生的名稱、體力條、體力%與詠唱列；
/// 原生詠唱列本來就會顯示技名，所以預設只補「原生沒有」的剩餘秒數。
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
        "在敵對列表每列的外側顯示體力數值／百分比與詠唱剩餘秒數，並標示哪一隻正以你為目標。" +
        "技名交給遊戲原生的詠唱列（原生沒開時才自動補上），不會蓋住任何原生內容。" +
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

    /// <summary>一列在螢幕上的矩形（已含節點鏈的累積縮放）。</summary>
    private readonly record struct RowRect(Vector2 TopLeft, Vector2 Size);

    /// <summary>
    /// 遊戲設定「敵對列表顯示詠唱列」。
    /// 原生詠唱技名**不是必然顯示**——這個選項關掉時整條詠唱列（含技名）都不會出現，
    /// 所以「只印秒數」不能寫死，否則把設定關掉的人就完全看不到技名。
    /// 走 Dalamud 的具名列舉（<c>UiConfigOption.EnemyListCastbarEnable</c>，CS 的 ConfigOption 914）
    /// 而不是魔術字串，避免打錯字後靜默失效。
    /// </summary>
    private const UiConfigOption CastbarOption = UiConfigOption.EnemyListCastbarEnable;

    private static readonly string[] CastDisplayLabels =
        ["不顯示", "只顯示秒數", "技名＋秒數", "自動（依遊戲設定）"];

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

        var rowRects = ResolveRowRects(&addon->AtkUnitBase);
        if (rowRects == null) return;

        var drawList = ImGui.GetBackgroundDrawList();
        var baseFontSize = ImGui.GetFontSize();
        var fontSize = baseFontSize * Config.TextScale;
        var textScale = fontSize / baseFontSize;
        var showCastName = ShouldShowCastName();

        foreach (var row in rows)
        {
            if (row.Slot < 0 || row.Slot >= rowRects.Length) continue;

            var rect = rowRects[row.Slot];
            if (rect.Size.X <= 0f) continue;

            var line = BuildLine(row, showCastName);
            if (line.Length == 0) continue;

            var textSize = ImGui.CalcTextSize(line) * textScale;

            // 疊圖一律畫在整列的「外側」——原生列裡已經有名稱、體力條、體力%、詠唱列，
            // 任何畫在列內部的座標都會壓到原生內容（舊版預設 (4,20) 正好落在詠唱列上）。
            var x = Config.AnchorRight
                        ? rect.TopLeft.X + rect.Size.X + Config.OffsetX
                        : rect.TopLeft.X - textSize.X - Config.OffsetX;

            // 垂直置中對齊該列，跟原生列高無關地保持對位
            var y = rect.TopLeft.Y + ((rect.Size.Y - textSize.Y) / 2f) + Config.OffsetY;

            var origin = new Vector2(x, y);

            var color = row.TargetingLocalPlayer && Config.HighlightTargetingYou
                            ? ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 0.45f, 0.4f, 1f))
                            : ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 0.9f));

            // 先畫一層深色描邊，讓文字在任何底色上都看得清楚
            var shadow = ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 0.85f));
            drawList.AddText(ImGui.GetFont(), fontSize, origin + new Vector2(1, 1), shadow, line);
            drawList.AddText(ImGui.GetFont(), fontSize, origin, color, line);
        }
    }

    /// <summary>
    /// 決定要不要連技名一起印。
    /// 自動模式下讀遊戲設定：原生詠唱列開著就只印秒數（不重複），關著才補上技名。
    /// 讀不到設定時一律當作「原生有顯示」，寧可少印也不要疊字。
    /// </summary>
    private bool ShouldShowCastName()
    {
        switch (Config.CastDisplay)
        {
            case CastDisplayMode.NameAndSeconds:
                return true;
            case CastDisplayMode.SecondsOnly:
            case CastDisplayMode.Off:
                return false;
            default:
                try
                {
                    return Svc.GameConfig.TryGet(CastbarOption, out uint enabled) && enabled == 0;
                }
                catch
                {
                    return false;
                }
        }
    }

    private string BuildLine(RowInfo row, bool showCastName)
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

        if (Config.CastDisplay != CastDisplayMode.Off && row.CastName.Length > 0)
        {
            var seconds = row.CastRemaining > 0f ? $"{row.CastRemaining:0.0}s" : string.Empty;

            if (showCastName)
                parts.Add(seconds.Length > 0 ? $"{row.CastName} {seconds}" : row.CastName);
            else if (seconds.Length > 0)
                parts.Add(seconds);
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
    /// 取 8 列在螢幕上的矩形。NodeList 的排列順序不保證，因此依 ScreenY 由上而下排序後
    /// 才對應到敵對列表的列序（第 0 列在最上）。
    /// 尺寸算法沿用 Pictomancy <c>AddonClipper.ClipAtkNodeRectangle</c> 的生產作法：
    /// <c>ScreenX/ScreenY</c> 已是絕對座標，寬高則要乘上節點鏈一路累積的縮放。
    /// </summary>
    private static RowRect[]? ResolveRowRects(AtkUnitBase* addon)
    {
        if (addon->UldManager.NodeList == null) return null;
        if (addon->UldManager.NodeListCount <= RowNodeIndexEnd) return null;

        var found = new List<RowRect>(MaxRows);
        for (var i = RowNodeIndexStart; i <= RowNodeIndexEnd; i++)
        {
            var node = addon->UldManager.NodeList[i];
            if (node == null || !node->IsVisible()) continue;

            var scale = GetCumulativeScale(node);
            var size = new Vector2(node->Width * scale.X, node->Height * scale.Y);

            // 節點寬度取不到時退回 addon 自身寬度，至少不會把文字疊回列內
            if (size.X <= 0f)
                size.X = addon->GetScaledWidth(true);
            if (size.Y <= 0f)
                size.Y = ImGui.GetFontSize();

            found.Add(new RowRect(new Vector2(node->ScreenX, node->ScreenY), size));
        }

        if (found.Count == 0) return null;

        found.Sort((a, b) => a.TopLeft.Y.CompareTo(b.TopLeft.Y));

        var result = new RowRect[MaxRows];
        for (var i = 0; i < found.Count && i < MaxRows; i++)
            result[i] = found[i];

        return result;
    }

    /// <summary>節點鏈一路往上累乘的縮放（同 Pictomancy 的 GetNodeScale）。</summary>
    private static Vector2 GetCumulativeScale(AtkResNode* node)
    {
        if (node == null) return Vector2.One;

        var scale = new Vector2(node->ScaleX, node->ScaleY);
        var parent = node->ParentNode;
        while (parent != null)
        {
            scale *= new Vector2(parent->ScaleX, parent->ScaleY);
            parent = parent->ParentNode;
        }

        return scale;
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

        ImGui.SetNextItemWidth(240f);
        var castIndex = (int)Config.CastDisplay;
        if (ImGui.Combo("詠唱顯示", ref castIndex, CastDisplayLabels, CastDisplayLabels.Length))
        {
            Config.CastDisplay = (CastDisplayMode)castIndex;
            Plugin.Instance.Config.Save();
        }

        using (ImRaii.PushIndent())
        {
            ImGui.TextDisabled(Config.CastDisplay switch
            {
                CastDisplayMode.Auto =>
                    "依遊戲設定自動判斷：原生「敵對列表詠唱列」開著時只印秒數（避免技名重複），\n關著時才連技名一起印。",
                CastDisplayMode.SecondsOnly => "只印剩餘秒數，技名交給原生詠唱列顯示。",
                CastDisplayMode.NameAndSeconds => "技名與秒數都印；原生詠唱列若也開著會看到兩份技名。",
                _ => "完全不顯示詠唱資訊。",
            });
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

        var anchorRight = Config.AnchorRight;
        if (ImGui.Checkbox("顯示在列的右側（取消＝顯示在左側）", ref anchorRight))
        {
            Config.AnchorRight = anchorRight;
            Plugin.Instance.Config.Save();
        }

        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("文字一律畫在整列的外側，不會蓋到原生的名稱、體力條與詠唱列。\n敵對列表若貼在畫面右緣，把這個取消改畫在左側。");

        ImGui.SetNextItemWidth(180f);
        var offset = new Vector2(Config.OffsetX, Config.OffsetY);
        if (ImGui.InputFloat2("額外偏移（X／Y）", ref offset))
        {
            Config.OffsetX = offset.X;
            Config.OffsetY = offset.Y;
            Plugin.Instance.Config.Save();
        }

        using (ImRaii.PushIndent())
            ImGui.TextDisabled("X 是「離開列邊緣多遠」（兩側都是正值往外），Y 是相對垂直置中的微調。");

        if (ImGui.Button("還原預設版面"))
        {
            Config.AnchorRight = true;
            Config.OffsetX = 6f;
            Config.OffsetY = 0f;
            Config.TextScale = 0.9f;
            Plugin.Instance.Config.Save();
        }

        ImGui.TextDisabled("DR 原版靠 3 個記憶體 patch 才做到的部分（改寫遊戲程式碼）一律不抄；\n本模組只顯示遊戲自己已經算好的資訊。");
    }
}

using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Component.GUI;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 目標情報界面最佳化：在目標／焦點目標情報上疊出實際體力數值與詠唱剩餘秒數，
/// 並提供一鍵清除焦點目標。
/// 機制：資料讀 ObjectTable（遊戲已算好的體力與詠唱時間），UI 走 ImGui 疊圖定位在
/// 對應 addon 的螢幕座標上；零 hook、不注入原生節點、不寫記憶體。
/// ⚠️ 與 DR 原版的差異：DR 另有「放大自身狀態效果」與整組節點外觀改寫，需要原生節點重排，
/// 本模組只做「顯示既有數值」的子集（見設定說明）。
/// </summary>
public sealed unsafe class OptimizedTargetInfo : TcModule
{
    public override string InternalName => "OptimizedTargetInfo";
    public override string DisplayName => "目標情報界面最佳化";

    public override string Description =>
        "在目標情報上顯示實際體力數值（可用萬／億簡寫）與百分比，以及目標正在詠唱技能的剩餘秒數；" +
        "焦點目標同樣支援，並可在焦點目標旁顯示一鍵清除按鈕。純顯示，不改變任何遊戲行為。";

    public override bool HasConfigUI => true;

    /// <summary>分離模式與合併模式的主目標情報 addon（只有一個會是可見的）。</summary>
    private static readonly string[] MainTargetAddons = ["_TargetInfoMainTarget", "_TargetInfo"];

    private const string FocusTargetAddon = "_FocusTargetInfo";

    private OptimizedTargetInfoConfig Config => Plugin.Instance.Config.TargetInfo;

    protected override void OnEnable()
    {
        Svc.PluginInterface.UiBuilder.Draw += DrawOverlay;
    }

    protected override void OnDisable()
    {
        Svc.PluginInterface.UiBuilder.Draw -= DrawOverlay;
    }

    private void DrawOverlay()
    {
        DrawTarget();
        DrawFocusTarget();
    }

    private void DrawTarget()
    {
        if (Svc.Targets.Target is not IBattleChara target) return;

        AtkUnitBase* addon = null;
        foreach (var name in MainTargetAddons)
        {
            var candidate = UiHelper.GetAddon(name);
            if (!UiHelper.IsReady(candidate)) continue;
            addon = candidate;
            break;
        }

        if (addon == null) return;

        var origin = new Vector2(
            addon->GetX() + (Config.TargetOffsetX * addon->Scale),
            addon->GetY() + (Config.TargetOffsetY * addon->Scale));

        DrawInfoText(target, origin);
    }

    private void DrawFocusTarget()
    {
        var addon = UiHelper.GetAddon(FocusTargetAddon);
        if (!UiHelper.IsReady(addon)) return;

        var origin = new Vector2(
            addon->GetX() + (Config.FocusOffsetX * addon->Scale),
            addon->GetY() + (Config.FocusOffsetY * addon->Scale));

        if (Svc.Targets.FocusTarget is IBattleChara focus)
            DrawInfoText(focus, origin);

        if (!Config.ShowClearFocusButton) return;

        const ImGuiWindowFlags flags = ImGuiWindowFlags.AlwaysAutoResize |
                                       ImGuiWindowFlags.NoTitleBar |
                                       ImGuiWindowFlags.NoCollapse |
                                       ImGuiWindowFlags.NoScrollbar |
                                       ImGuiWindowFlags.NoSavedSettings |
                                       ImGuiWindowFlags.NoBackground;

        if (ImGui.Begin("###TCToolboxClearFocus", flags))
        {
            ImGui.SetWindowPos(new Vector2(
                                   addon->GetX() + (Config.ClearFocusButtonOffsetX * addon->Scale),
                                   addon->GetY() + (Config.ClearFocusButtonOffsetY * addon->Scale)));

            if (ImGui.SmallButton("✕##clearfocus"))
                Svc.Targets.FocusTarget = null;

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("清除焦點目標");
        }

        ImGui.End();
    }

    private void DrawInfoText(IBattleChara chara, Vector2 origin)
    {
        var parts = new List<string>(3);

        if (Config.ShowHp && chara.MaxHp > 0)
        {
            parts.Add(Config.CompactNumbers
                          ? $"{FormatCompact(chara.CurrentHp)}/{FormatCompact(chara.MaxHp)}"
                          : $"{chara.CurrentHp:N0}/{chara.MaxHp:N0}");
        }

        if (Config.ShowHpPercent && chara.MaxHp > 0)
            parts.Add($"{chara.CurrentHp * 100f / chara.MaxHp:0.#}%");

        if (Config.ShowCastRemaining && chara.IsCasting)
        {
            var remaining = Math.Max(0f, chara.TotalCastTime - chara.CurrentCastTime);
            parts.Add($"{remaining:0.0}s");
        }

        if (parts.Count == 0) return;

        var text = string.Join("  ", parts);
        var drawList = ImGui.GetBackgroundDrawList();
        var fontSize = ImGui.GetFontSize() * Config.TextScale;

        drawList.AddText(ImGui.GetFont(), fontSize, origin + new Vector2(1, 1),
                         ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 0.85f)), text);
        drawList.AddText(ImGui.GetFont(), fontSize, origin,
                         ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 0.95f)), text);
    }

    /// <summary>中文簡寫（萬／億）。</summary>
    private static string FormatCompact(uint value) => value switch
    {
        >= 100_000_000 => $"{value / 100_000_000f:0.##}億",
        >= 10_000 => $"{value / 10_000f:0.##}萬",
        _ => value.ToString(),
    };

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

        var showCast = Config.ShowCastRemaining;
        if (ImGui.Checkbox("顯示詠唱剩餘秒數", ref showCast))
        {
            Config.ShowCastRemaining = showCast;
            Plugin.Instance.Config.Save();
        }

        var showClear = Config.ShowClearFocusButton;
        if (ImGui.Checkbox("焦點目標旁顯示清除按鈕", ref showClear))
        {
            Config.ShowClearFocusButton = showClear;
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
        var targetOffset = new Vector2(Config.TargetOffsetX, Config.TargetOffsetY);
        if (ImGui.InputFloat2("目標文字偏移", ref targetOffset))
        {
            Config.TargetOffsetX = targetOffset.X;
            Config.TargetOffsetY = targetOffset.Y;
            Plugin.Instance.Config.Save();
        }

        ImGui.SetNextItemWidth(180f);
        var focusOffset = new Vector2(Config.FocusOffsetX, Config.FocusOffsetY);
        if (ImGui.InputFloat2("焦點目標文字偏移", ref focusOffset))
        {
            Config.FocusOffsetX = focusOffset.X;
            Config.FocusOffsetY = focusOffset.Y;
            Plugin.Instance.Config.Save();
        }

        ImGui.SetNextItemWidth(180f);
        var buttonOffset = new Vector2(Config.ClearFocusButtonOffsetX, Config.ClearFocusButtonOffsetY);
        if (ImGui.InputFloat2("清除焦點按鈕偏移", ref buttonOffset))
        {
            Config.ClearFocusButtonOffsetX = buttonOffset.X;
            Config.ClearFocusButtonOffsetY = buttonOffset.Y;
            Plugin.Instance.Config.Save();
        }

        ImGui.TextDisabled("DR 原版的「放大自身狀態效果」與整組節點外觀改寫未移植（需要重排原生節點）。\n" +
                           "偏移值請依自己的 HUD 版面微調——目標情報的位置與大小每個人都不一樣。");
    }
}

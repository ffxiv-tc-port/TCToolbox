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
/// 機制：資料讀 ObjectTable（遊戲已算好的體力與詠唱時間），UI 預設走 ImGui 疊圖定位在
/// 對應 addon 的螢幕座標上；選用「以原生文字顯示（實驗）」時改掛 KamiToolKit 文字節點。零 hook、不寫記憶體。
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

    public override ModuleCategory Category => ModuleCategory.Combat;

    public override bool HasConfigUI => true;

    /// <summary>分離模式與合併模式的主目標情報 addon（只有一個會是可見的）。</summary>
    private static readonly string[] MainTargetAddons = ["_TargetInfoMainTarget", "_TargetInfo"];

    private const string FocusTargetAddon = "_FocusTargetInfo";

    private OptimizedTargetInfoConfig Config => Plugin.Instance.Config.TargetInfo;

    private readonly OverlayTextMemo targetText = new();
    private readonly OverlayTextMemo focusText = new();
    private readonly System.Text.StringBuilder textBuilder = new();

    private readonly OverlayFont targetFont = new();

    /// <summary>只在跟著介面縮放時用：兩個 addon 的縮放可能不同，共用一份字會每幀互相觸發重建。</summary>
    private readonly OverlayFont focusFont = new();

    /// <summary>「文字大小倍率」滑桿最後一次被拖著的幀；拖著時不新建字型。</summary>
    private int textScaleActiveFrame = -100;

    private static readonly NativeTextState NativeHidden = new(string.Empty, Vector2.Zero, 0);

    /// <summary>原生文字的三格（兩個主目標 addon＋焦點）；null＝目前沒用原生文字。只在框架執行緒讀寫。</summary>
    private NativeTextSlot[]? nativeSlots;

    /// <summary>本次遊戲內原生文字出過錯：不再重試，一律疊圖。</summary>
    private bool nativeFailed;

    private bool nativeThreadWarned;
    private bool active;

    private readonly OverlayTextMemo nativeTargetText = new();
    private readonly OverlayTextMemo nativeFocusText = new();

    public override ModuleNotice? RowNotice =>
        Config.NativeText && nativeFailed
            ? new ModuleNotice(ModuleNoticeLevel.Warning, "原生文字無法使用，已改回疊圖",
                               "本次遊戲內「以原生文字顯示（實驗）」出過錯，已自動改回疊圖顯示；原因寫在 dalamud.log（搜尋 TCToolbox）。")
            : null;

    // 原生文字不在這裡啟動：插件載入時呼叫 OnEnable 的不是框架執行緒，留給第一次 DrawOverlay（主執行緒）。
    protected override void OnEnable()
    {
        active = true;
        Svc.PluginInterface.UiBuilder.Draw += DrawOverlay;
    }

    protected override void OnDisable()
    {
        active = false;
        Svc.PluginInterface.UiBuilder.Draw -= DrawOverlay;
        targetText.Forget();
        focusText.Forget();
        targetFont.Dispose();
        focusFont.Dispose();

        if (Svc.Framework.IsInFrameworkUpdateThread)
            StopNative();
        else
            Svc.Framework.RunOnFrameworkThread(() =>
            {
                if (!active) StopNative();
            });
    }

    private void DrawOverlay()
    {
        SyncNative();
        DrawTarget();
        DrawFocusTarget();
    }

    /// <summary>照設定開／關原生文字；出過錯的格子在這裡收掉。</summary>
    private void SyncNative()
    {
        if (nativeSlots != null)
        {
            foreach (var slot in nativeSlots)
            {
                if (!slot.Faulted) continue;
                nativeFailed = true;
                break;
            }
        }

        var want = active && Config.NativeText && !nativeFailed;
        if (want == (nativeSlots != null)) return;

        if (!Svc.Framework.IsInFrameworkUpdateThread)
        {
            if (!nativeThreadWarned)
                Svc.Log.Warning("[OptimizedTargetInfo] 繪製不在框架執行緒上，原生文字不切換，維持疊圖");
            nativeThreadWarned = true;
            return;
        }

        if (want)
            StartNative();
        else
            StopNative();
    }

    private void StartNative()
    {
        if (KamiNative.Ensure() == null)
        {
            nativeFailed = true;
            return;
        }

        var slots = new List<NativeTextSlot>(3);
        try
        {
            foreach (var name in MainTargetAddons)
                slots.Add(new NativeTextSlot(name, TargetNativeState));
            slots.Add(new NativeTextSlot(FocusTargetAddon, FocusNativeState));
            nativeSlots = slots.ToArray();
            Svc.Log.Information("[OptimizedTargetInfo] 以原生文字顯示：已啟用");
        }
        catch (Exception ex)
        {
            foreach (var slot in slots)
                slot.Dispose();
            nativeFailed = true;
            Svc.Log.Warning(ex, "[OptimizedTargetInfo] 以原生文字顯示：啟用失敗，改回疊圖");
        }
    }

    private void StopNative()
    {
        var slots = nativeSlots;
        nativeSlots = null;
        if (slots == null) return;

        foreach (var slot in slots)
        {
            try
            {
                slot.Dispose();
            }
            catch (Exception ex)
            {
                Svc.Log.Warning(ex, "[OptimizedTargetInfo] 拆除原生文字時發生例外");
            }
        }

        nativeTargetText.Forget();
        nativeFocusText.Forget();
        Svc.Log.Information("[OptimizedTargetInfo] 以原生文字顯示：已停用");
    }

    /// <summary>這個 addon 上的字已經由原生節點畫了（疊圖就不畫）。</summary>
    private bool NativeShowsOn(AtkUnitBase* addon)
    {
        if (nativeSlots == null) return false;
        foreach (var slot in nativeSlots)
        {
            if (!slot.Faulted && slot.IsAttachedTo((nint)addon)) return true;
        }

        return false;
    }

    private byte NativeFontSize => (byte)Math.Clamp(Config.NativeFontSize, 8, 64);

    private NativeTextState TargetNativeState() =>
        Svc.Targets.Target is IBattleChara target
            ? new NativeTextState(InfoText(target, nativeTargetText),
                                  new Vector2(Config.TargetOffsetX, Config.TargetOffsetY), NativeFontSize)
            : NativeHidden;

    private NativeTextState FocusNativeState() =>
        Svc.Targets.FocusTarget is IBattleChara focus
            ? new NativeTextState(InfoText(focus, nativeFocusText),
                                  new Vector2(Config.FocusOffsetX, Config.FocusOffsetY), NativeFontSize)
            : NativeHidden;

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

        if (addon == null || NativeShowsOn(addon)) return;

        var origin = new Vector2(
            addon->GetX() + (Config.TargetOffsetX * addon->Scale),
            addon->GetY() + (Config.TargetOffsetY * addon->Scale));

        DrawInfoText(target, origin, targetText, targetFont, addon->Scale);
    }

    private void DrawFocusTarget()
    {
        var addon = UiHelper.GetAddon(FocusTargetAddon);
        if (!UiHelper.IsReady(addon)) return;

        var origin = new Vector2(
            addon->GetX() + (Config.FocusOffsetX * addon->Scale),
            addon->GetY() + (Config.FocusOffsetY * addon->Scale));

        if (Svc.Targets.FocusTarget is IBattleChara focus && !NativeShowsOn(addon))
            DrawInfoText(focus, origin, focusText, Config.ScaleWithHud ? focusFont : targetFont, addon->Scale);

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

    private void DrawInfoText(IBattleChara chara, Vector2 origin, OverlayTextMemo memo, OverlayFont font, float hudScale)
    {
        var text = InfoText(chara, memo);
        if (text.Length == 0) return;

        var scale = OverlayTextScale.Effective(Config.TextScale, Config.ScaleWithHud, hudScale);
        font.Request(scale, ImGui.GetFrameCount() - textScaleActiveFrame <= 1);

        // 字級在推入字型之前量：預設字型的大小 × 倍率，和改版前的字一樣大。
        var fontPx = ImGui.GetFontSize() * scale;
        using var pushed = font.Push();
        OverlayFont.DrawOutlined(ImGui.GetBackgroundDrawList(), fontPx, origin,
                                 ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 0.95f)), text);
    }

    /// <summary>
    /// 要畫的那行字（沒有東西可畫回空字串）。設定、體力、詠唱秒數畫出來的字都和上一幀相同時沿用上一幀的字串。
    /// </summary>
    private string InfoText(IBattleChara chara, OverlayTextMemo memo)
    {
        var showHp = Config.ShowHp;
        var compact = Config.CompactNumbers;
        var showPercent = Config.ShowHpPercent;
        var hp = chara.CurrentHp;
        var maxHp = chara.MaxHp;
        var casting = Config.ShowCastRemaining && chara.IsCasting;
        var remaining = casting ? Math.Max(0f, chara.TotalCastTime - chara.CurrentCastTime) : 0f;

        Span<char> seconds = stackalloc char[OverlayTextMemo.SecondsCapacity];
        var secondsLength = 0;
        var reusable = !casting || OverlayTextMemo.FormatSeconds(remaining, seconds, out secondsLength);
        var options = (showHp ? 1 : 0) | (compact ? 2 : 0) | (showPercent ? 4 : 0) | (casting ? 8 : 0);

        if (reusable && memo.TryGet(options, hp, maxHp, 0, string.Empty, seconds[..secondsLength], out var cached))
            return cached;

        var sb = textBuilder.Clear();
        var parts = 0;

        if (showHp && maxHp > 0)
        {
            OverlayTextMemo.NextPart(sb, ref parts);
            if (compact)
                sb.Append(FormatCompact(hp)).Append('/').Append(FormatCompact(maxHp));
            else
                sb.Append($"{hp:N0}/{maxHp:N0}");
        }

        if (showPercent && maxHp > 0)
        {
            OverlayTextMemo.NextPart(sb, ref parts);
            sb.Append($"{hp * 100f / maxHp:0.#}%");
        }

        if (casting)
        {
            OverlayTextMemo.NextPart(sb, ref parts);
            sb.Append($"{remaining:0.0}s");
        }

        var text = parts == 0 ? string.Empty : sb.ToString();

        if (reusable)
            memo.Store(options, hp, maxHp, 0, string.Empty, seconds[..secondsLength], text);
        else
            memo.Forget();

        return text;
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
        var nativeText = Config.NativeText;
        if (ImGui.Checkbox("以原生文字顯示（實驗）", ref nativeText))
        {
            Config.NativeText = nativeText;
            Plugin.Instance.Config.Save();
        }

        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("文字改由遊戲介面本身繪製：使用遊戲字型與外框，跟著 HUD 配置的縮放與隱藏，\n" +
                             "會被上層的遊戲視窗蓋住。清除焦點按鈕仍是疊圖。\n" +
                             "實驗功能：本次遊戲內出過錯就自動改回疊圖，原因寫在 dalamud.log。");
        }

        if (Config.NativeText)
        {
            ImGui.SetNextItemWidth(UiScale.Px(180f));
            var nativeFontSize = Config.NativeFontSize;
            if (ImGui.SliderInt("原生文字大小", ref nativeFontSize, 10, 32))
            {
                Config.NativeFontSize = nativeFontSize;
                Plugin.Instance.Config.Save();
            }
        }

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

        ImGui.SetNextItemWidth(UiScale.Px(180f));
        var textScale = Config.TextScale;
        if (ImGui.SliderFloat("文字大小倍率", ref textScale, 0.6f, 2f, "%.2f"))
        {
            Config.TextScale = textScale;
            Plugin.Instance.Config.Save();
        }

        if (ImGui.IsItemActive())
            textScaleActiveFrame = ImGui.GetFrameCount();

        var scaleWithHud = Config.ScaleWithHud;
        if (ImGui.Checkbox("文字大小跟著目標情報的介面縮放", ref scaleWithHud))
        {
            Config.ScaleWithHud = scaleWithHud;
            Plugin.Instance.Config.Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("勾選後，文字會隨 HUD 配置裡目標情報的縮放一起放大縮小。\n不勾＝維持原本的字級，只看上面的倍率。");

        ImGui.SetNextItemWidth(UiScale.Px(180f));
        var targetOffset = new Vector2(Config.TargetOffsetX, Config.TargetOffsetY);
        if (ImGui.InputFloat2("目標文字偏移", ref targetOffset))
        {
            Config.TargetOffsetX = targetOffset.X;
            Config.TargetOffsetY = targetOffset.Y;
            Plugin.Instance.Config.Save();
        }

        ImGui.SetNextItemWidth(UiScale.Px(180f));
        var focusOffset = new Vector2(Config.FocusOffsetX, Config.FocusOffsetY);
        if (ImGui.InputFloat2("焦點目標文字偏移", ref focusOffset))
        {
            Config.FocusOffsetX = focusOffset.X;
            Config.FocusOffsetY = focusOffset.Y;
            Plugin.Instance.Config.Save();
        }

        ImGui.SetNextItemWidth(UiScale.Px(180f));
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

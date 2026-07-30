using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 任務搜索器設置最佳化：把「任務設定」二級選單的每個開關做成搜索器視窗上方的一排圖示按鈕。
/// 機制：讀取目前設定（<see cref="ContentsFinder"/> 結構 + UiConfig），呼叫遊戲自己的
/// 「套用搜索器設定」函式送出（與原生設定視窗按確定同一條路徑），不寫任何記憶體、不做 patch。
/// UI 走 ImGui 疊圖（不注入原生節點），tooltip 文字全部取自 Lumina Addon 表（台服自帶繁中）。
/// 參考 DailyRoutines OptimizedDutyFinderSetting 設計重寫（API13、不依賴 KamiToolKit）。
/// </summary>
public sealed unsafe class OptimizedDutyFinderSetting : TcModule
{
    public override string InternalName => "OptimizedDutyFinderSetting";
    public override string DisplayName => "任務搜索器設置最佳化";

    public override string Description =>
        "在任務搜索器／討伐殲滅戰搜索器視窗上方直接顯示各項設定的圖示按鈕（中途參戰、解除限制、等級同步、" +
        "最低品級、超越之力無效化、自由探索、練級隨機限制、分配方式），點一下即切換，不必再開二級設定視窗。";

    public override bool HasConfigUI => true;

    /// <summary>遊戲的「套用搜索器設定」函式（sig 已對台服 7.20 主程式離線驗證，唯一命中）。</summary>
    private const string SetContentsFinderSettingsSignature =
        "E8 ?? ?? ?? ?? 49 8B 06 45 33 FF 49 8B CE 45 89 7E 20 FF 50 28 B0 01";

    private delegate void SetContentsFinderSettingsDelegate(byte* data, UIModule* module);

    private SetContentsFinderSettingsDelegate? setContentsFinderSettings;

    private enum Setting
    {
        LanguageJa = 0,
        LanguageEn = 1,
        LanguageDe = 2,
        LanguageFr = 3,
        LootRule = 4,
        JoinPartyInProgress = 5,
        UnrestrictedParty = 6,
        LevelSync = 7,
        MinimumItemLevel = 8,
        SilenceEcho = 9,
        ExplorerMode = 10,
        LimitedLevelingRoulette = 11,
    }

    private const int SettingCount = 12;

    private static readonly (Setting Setting, uint IconId, uint AddonRow)[] ToggleButtons =
    [
        (Setting.JoinPartyInProgress, 60644, 2519),
        (Setting.UnrestrictedParty, 60641, 10008),
        (Setting.LevelSync, 60649, 12696),
        (Setting.MinimumItemLevel, 60642, 10010),
        (Setting.SilenceEcho, 60647, 12691),
        (Setting.ExplorerMode, 60648, 13038),
        (Setting.LimitedLevelingRoulette, 60640, 13030),
    ];

    private static readonly (Setting Setting, uint AddonRow)[] LanguageButtons =
    [
        (Setting.LanguageJa, 4266),
        (Setting.LanguageEn, 4267),
        (Setting.LanguageDe, 4268),
        (Setting.LanguageFr, 4269),
    ];

    private static readonly Dictionary<Setting, string> LanguageConfigKeys = new()
    {
        [Setting.LanguageJa] = "ContentsFinderUseLangTypeJA",
        [Setting.LanguageEn] = "ContentsFinderUseLangTypeEN",
        [Setting.LanguageDe] = "ContentsFinderUseLangTypeDE",
        [Setting.LanguageFr] = "ContentsFinderUseLangTypeFR",
    };

    private const string JoinInProgressConfigKey = "ContentsFinderSupplyEnable";

    private static readonly string[] TargetAddons = ["ContentsFinder", "RaidFinder"];

    private OptimizedDutyFinderSettingConfig Config => Plugin.Instance.Config.DutyFinderSetting;

    protected override void OnEnable()
    {
        var address = Svc.SigScanner.ScanText(SetContentsFinderSettingsSignature);
        setContentsFinderSettings =
            System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer<SetContentsFinderSettingsDelegate>(address);

        Svc.PluginInterface.UiBuilder.Draw += DrawOverlay;
    }

    protected override void OnDisable()
    {
        Svc.PluginInterface.UiBuilder.Draw -= DrawOverlay;
        setContentsFinderSettings = null;
    }

    private void DrawOverlay()
    {
        FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase* addon = null;
        foreach (var name in TargetAddons)
        {
            var candidate = UiHelper.GetAddon(name);
            if (!UiHelper.IsReady(candidate)) continue;
            addon = candidate;
            break;
        }

        if (addon == null) return;
        if (ContentsFinder.Instance() == null) return;

        const ImGuiWindowFlags flags = ImGuiWindowFlags.AlwaysAutoResize |
                                       ImGuiWindowFlags.NoTitleBar |
                                       ImGuiWindowFlags.NoCollapse |
                                       ImGuiWindowFlags.NoScrollbar |
                                       ImGuiWindowFlags.NoSavedSettings;

        if (ImGui.Begin("###TCToolboxDutyFinderSettings", flags))
        {
            ImGui.SetWindowPos(new Vector2(addon->GetX() + 8, addon->GetY() - ImGui.GetWindowSize().Y - 2));

            var size = Config.ButtonSize;

            for (var i = 0; i < ToggleButtons.Length; i++)
            {
                var (setting, iconId, addonRow) = ToggleButtons[i];
                if (i > 0) ImGui.SameLine();

                using var id = ImRaii.PushId((int)setting);

                var value = GetValue(setting);
                var dimmed = value == 0;

                // 等級同步在「解除限制」關閉時本來就無意義，畫得更暗一點
                if (setting == Setting.LevelSync && GetValue(Setting.UnrestrictedParty) == 0)
                    dimmed = true;

                if (GameIcons.IconButton(iconId, GameIcons.AddonText(addonRow), size, dimmed))
                    Toggle(setting);

                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(GameIcons.AddonText(addonRow));
            }

            // 分配方式（三段循環：通常／僅限貪婪／隊長分配）
            {
                using var id = ImRaii.PushId("loot");
                var lootRule = GetValue(Setting.LootRule);
                var lootIcon = lootRule == 2 ? 60646u : 60645u;
                var lootRow = lootRule switch { 2 => 10024u, 1 => 10023u, _ => 10022u };

                ImGui.SameLine();
                if (GameIcons.IconButton(lootIcon, GameIcons.AddonText(lootRow), size, false))
                    Toggle(Setting.LootRule);

                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(GameIcons.AddonText(lootRow));
            }

            if (Config.ShowLanguageButtons && IsLanguageConfigAvailable())
            {
                foreach (var (setting, addonRow) in LanguageButtons)
                {
                    using var id = ImRaii.PushId((int)setting);
                    ImGui.SameLine();

                    var enabled = GetValue(setting) != 0;
                    using (ImRaii.PushColor(ImGuiCol.Text,
                                            enabled
                                                ? new Vector4(1f, 1f, 1f, 1f)
                                                : new Vector4(1f, 1f, 1f, 0.4f)))
                    {
                        if (ImGui.Button(GameIcons.AddonText(addonRow), new Vector2(0, size)))
                            Toggle(setting);
                    }
                }
            }
        }

        ImGui.End();
    }

    private static bool IsLanguageConfigAvailable()
    {
        try
        {
            foreach (var key in LanguageConfigKeys.Values)
            {
                if (!Svc.GameConfig.UiConfig.TryGet(key, out uint _)) return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static byte GetValue(Setting setting)
    {
        var finder = ContentsFinder.Instance();
        if (finder == null) return 0;

        try
        {
            switch (setting)
            {
                case Setting.LanguageJa:
                case Setting.LanguageEn:
                case Setting.LanguageDe:
                case Setting.LanguageFr:
                    return Svc.GameConfig.UiConfig.TryGet(LanguageConfigKeys[setting], out uint lang)
                               ? (byte)lang
                               : (byte)0;
                case Setting.JoinPartyInProgress:
                    return Svc.GameConfig.UiConfig.TryGet(JoinInProgressConfigKey, out uint supply)
                               ? (byte)supply
                               : (byte)0;
                case Setting.LootRule: return (byte)finder->LootRules;
                case Setting.UnrestrictedParty: return finder->IsUnrestrictedParty ? (byte)1 : (byte)0;
                case Setting.LevelSync: return finder->IsLevelSync ? (byte)1 : (byte)0;
                case Setting.MinimumItemLevel: return finder->IsMinimalIL ? (byte)1 : (byte)0;
                case Setting.SilenceEcho: return finder->IsSilenceEcho ? (byte)1 : (byte)0;
                case Setting.ExplorerMode: return finder->IsExplorerMode ? (byte)1 : (byte)0;
                case Setting.LimitedLevelingRoulette: return finder->IsLimitedLevelingRoulette ? (byte)1 : (byte)0;
                default: return 0;
            }
        }
        catch
        {
            return 0;
        }
    }

    private void Toggle(Setting setting)
    {
        if (setting is Setting.LanguageJa or Setting.LanguageEn or Setting.LanguageDe or Setting.LanguageFr)
        {
            // 至少要留一種語言，全部關掉會排不進隊伍
            var enabledLanguages = 0;
            foreach (var (langSetting, _) in LanguageButtons)
                enabledLanguages += GetValue(langSetting) != 0 ? 1 : 0;

            if (enabledLanguages <= 1 && GetValue(setting) != 0)
            {
                Svc.Chat.PrintError("[TC Toolbox] 至少必須保留一種語言。");
                return;
            }
        }

        var uiModule = UIModule.Instance();
        if (uiModule == null || setContentsFinderSettings == null) return;

        // 遊戲的設定套用函式吃一個 27 位元組的陣列：前 12 是新值、接著 12 是舊值鏡像、最後一格是「送出」旗標
        var data = new byte[27];
        for (var i = 0; i < SettingCount; i++)
        {
            var value = GetValue((Setting)i);
            data[i] = value;
            data[i + SettingCount] = value;
        }

        data[(int)setting] = setting == Setting.LootRule
                                 ? (byte)((data[(int)setting] + 1) % 3)
                                 : (byte)(data[(int)setting] == 0 ? 1 : 0);
        data[26] = 1;

        try
        {
            fixed (byte* dataPtr = data)
                setContentsFinderSettings(dataPtr, uiModule);
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, $"[{InternalName}] 套用搜索器設定失敗");
        }
    }

    public override void DrawConfig()
    {
        ImGui.SetNextItemWidth(180f);
        var size = Config.ButtonSize;
        if (ImGui.SliderFloat("按鈕大小", ref size, 20f, 48f, "%.0f"))
        {
            Config.ButtonSize = size;
            Plugin.Instance.Config.Save();
        }

        var showLanguages = Config.ShowLanguageButtons;
        if (ImGui.Checkbox("同時顯示語言按鈕（日／英／德／法）", ref showLanguages))
        {
            Config.ShowLanguageButtons = showLanguages;
            Plugin.Instance.Config.Save();
        }
    }
}

using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Interface.Utility.Raii;
using FFXIVClientStructs.FFXIV.Component.GUI;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 自動隱藏多餘彈窗：把勾選的系統彈窗（推薦任務、資訊中心、同好會通知、成就資訊、新手指南…）
/// 一出現就關掉。
/// 機制：訂閱該 addon 的 PreDraw，先把根節點設為不可見（避免出現一幀閃爍）再走遊戲自己的
/// <c>Close</c> 與 <c>FireCloseCallback</c> 收尾——與按下視窗右上角關閉鈕同一條路徑。
/// 不 hook、不寫記憶體、不做 patch。
/// 參考 DailyRoutines AutoHideNeedlessPopups 設計重寫（API13、無 OmenTools 相依）。
/// </summary>
/// <remarks>
/// 與 DR 原版的差異：DR 是「七個視窗一起擋」的單一總開關，這裡改成**每個視窗各自勾選**。
/// 原因是這七個 addon 裡有好幾個同時也是「你自己從選單點得開」的正常視窗
/// （新手指南、操作指南、使用條款），一旦擋掉就永遠打不開——不該綁在同一個開關上。
/// </remarks>
public sealed unsafe class AutoHideNeedlessPopups : TcModule
{
    public override string InternalName => "AutoHideNeedlessPopups";
    public override string DisplayName => "自動隱藏多餘彈窗";

    public override string Description =>
        "勾選的系統彈窗一出現就自動關閉（推薦任務、資訊中心、同好會通知、成就資訊、新手指南、" +
        "操作指南、使用條款）。每一種各自獨立勾選。";

    public override ModuleCategory Category => ModuleCategory.Misc;

    public override bool HasConfigUI => true;

    private sealed record PopupInfo(string AddonName, string Name, string Note, bool ManuallyOpenable);

    /// <summary>
    /// 可屏蔽的彈窗清單。
    /// <para>
    /// addon 名稱取自 DR 實機 DLL；顯示名依台服 Addon 表用語對齊
    /// （3530「推薦任務」、5504／8090「資訊中心」、840–847 與 3390「新手指南」、12800「同好會」）。
    /// </para>
    /// </summary>
    private static readonly PopupInfo[] Popups =
    [
        new("RecommendList", "推薦任務",
            "登入或完成任務後自動跳出的推薦任務清單。", true),
        new("WebLauncher", "資訊中心",
            "遊戲內的官方公告／活動宣傳視窗。", true),
        new("_NotificationCircleBook", "同好會通知",
            "同好會（Circle）的通知彈窗。", false),
        new("AchievementInfo", "成就資訊",
            "成就即將達成／達成時跳出的成就資訊視窗。", true),
        new("HowTo", "新手指南提示",
            "初次遇到某個系統時跳出的教學提示（勾了「不再顯示」也還會有新的）。", false),
        new("PlayGuide", "新手指南視窗",
            "系統選單裡的新手指南瀏覽器本體。", true),
        new("LicenseViewer", "使用條款",
            "軟體使用條款／授權說明視窗。", true),
    ];

    /// <summary>首次啟用時預設勾選：只勾「你自己不會主動去開」的那兩個，避免把正常入口一起擋死。</summary>
    private static readonly string[] DefaultHiddenPopups = ["RecommendList", "_NotificationCircleBook"];

    private static readonly HashSet<string> KnownAddonNames = BuildKnownNames();

    private static HashSet<string> BuildKnownNames()
    {
        var set = new HashSet<string>(Popups.Length, StringComparer.Ordinal);
        foreach (var popup in Popups)
            set.Add(popup.AddonName);
        return set;
    }

    private AutoHideNeedlessPopupsConfig Config => Plugin.Instance.Config.HideNeedlessPopups;

    protected override void OnEnable()
    {
        if (!Config.Initialized)
        {
            Config.Initialized = true;
            foreach (var name in DefaultHiddenPopups)
                Config.HiddenPopups.Add(name);
            Plugin.Instance.Config.Save();
        }

        // 一次註冊全部七個，實際要不要關在 detour 裡依勾選判斷——
        // 這樣使用者在設定畫面改勾選就即時生效，不必重開模組。
        foreach (var popup in Popups)
            Svc.AddonLifecycle.RegisterListener(AddonEvent.PreDraw, popup.AddonName, OnPreDraw);
    }

    protected override void OnDisable() => Svc.AddonLifecycle.UnregisterListener(OnPreDraw);

    private void OnPreDraw(AddonEvent type, AddonArgs args)
    {
        try
        {
            if (!KnownAddonNames.Contains(args.AddonName)) return;
            if (!Config.HiddenPopups.Contains(args.AddonName)) return;

            var addon = (AtkUnitBase*)args.Addon.Address;
            if (addon == null) return;

            // 先隱藏根節點：Close() 本身有收合動畫，不先藏起來會露出一到兩幀
            var root = addon->RootNode;
            if (root != null)
                root->ToggleVisibility(false);

            // 🔴 Close＋FireCloseCallback 對同一實例每幀重送沒有意義：這裡掛的是 PreDraw，
            //    關閉中的實例若還會被 Draw，就會對正在拆的窗連打 callback。守衛用多次互動窗的
            //    15 幀逃生口（沒關成才再關一次，寫 Debug 不洗版）；根節點隱藏仍然每幀做（純旗標，不碰 callback）。
            if (!AddonPressGuard.TryBeginRoutinePress(args.AddonName, addon)) return;

            addon->Close(false);
            addon->FireCloseCallback();
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, $"[{InternalName}] 關閉彈窗失敗：{args.AddonName}");
        }
    }

    public override void DrawConfig()
    {
        ImGui.TextDisabled("勾起來＝該視窗一出現就立刻關掉。");
        ImGui.TextColored(new Vector4(1f, 0.75f, 0.4f, 1f),
                          "注意：標了［也能手動開］的視窗勾起來之後，你自己從選單點開也會被關掉。");

        ImGui.Spacing();

        if (ImGui.Button("還原預設"))
        {
            Config.HiddenPopups.Clear();
            foreach (var name in DefaultHiddenPopups)
                Config.HiddenPopups.Add(name);
            Plugin.Instance.Config.Save();
        }

        ImGui.SameLine();
        if (ImGui.Button("全部取消"))
        {
            Config.HiddenPopups.Clear();
            Plugin.Instance.Config.Save();
        }

        ImGui.Separator();

        foreach (var popup in Popups)
        {
            using var id = ImRaii.PushId(popup.AddonName);

            var hidden = Config.HiddenPopups.Contains(popup.AddonName);
            if (ImGui.Checkbox(popup.Name, ref hidden))
            {
                if (hidden)
                    Config.HiddenPopups.Add(popup.AddonName);
                else
                    Config.HiddenPopups.Remove(popup.AddonName);
                Plugin.Instance.Config.Save();
            }

            ImGui.SameLine();
            ImGui.TextDisabled($"({popup.AddonName})");

            if (popup.ManuallyOpenable)
            {
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(1f, 0.75f, 0.4f, 1f), "［也能手動開］");
            }

            using (ImRaii.PushIndent())
                ImGui.TextDisabled(popup.Note);
        }
    }
}

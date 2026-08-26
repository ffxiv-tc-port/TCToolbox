using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text.RegularExpressions;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Component.GUI;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 招募說明可選取：招募詳細視窗打開時，在旁邊開一塊面板把那段說明文字用可選取／可複製的形式再顯示一次，
/// 並把裡面的網址做成可點的連結。
/// </summary>
/// <remarks>
/// <para>
/// 遊戲原生的招募說明文字<b>不能反白複製</b>，也不能點裡面的網址。這個模組讀出那段文字
/// （<c>LookingForGroupDetail</c> 的說明文字節點），放進一個貼在詳細視窗旁邊的小面板：
/// 面板裡是一個唯讀輸入框（可以反白、Ctrl+C 複製任意片段），下面把偵測到的網址列成可點的連結。
/// 純 ImGui 疊圖，零 hook、零特徵碼、不寫記憶體。參考 DailyRoutines <c>SelectableRecruitmentText</c> 重寫。
/// </para>
/// <para>
/// 🔴 <b>只做純網址連結。</b>DR 原版另外把 bilibili 的 <c>BV…</c> 番號與 5–11 位數字（QQ 群號）
/// 也做成連結／複製鈕，那是對岸專屬、在台服沒有意義，一律不做。數字要複製的話，面板裡的文字本來就能反白複製。
/// </para>
/// <para>
/// ⚠️ <b>面板貼在詳細視窗右邊，不覆蓋在原生文字上。</b>DR 原版是精準疊在原生文字的位置上做「無縫替換」，
/// 那要對齊字型與多個節點座標，版面一變就歪。這裡改成獨立小面板（UI 慣例：試改原生失敗就換獨立視窗，不深挖），
/// 座標只依賴詳細視窗根節點的右緣，取不到就不顯示，不會崩。
/// </para>
/// <para>
/// 📌 說明文字取自節點 id <see cref="DescriptionTextNodeId"/>（招募詳細視窗的說明文字節點）。
/// 節點 <b>ID</b> 是遊戲資料、跨語系穩定；取不到（回 null）時面板不顯示，屬可偵測的安全失敗。
/// </para>
/// </remarks>
public sealed unsafe class SelectableRecruitmentText : TcModule
{
    public override string InternalName => "SelectableRecruitmentText";
    public override string DisplayName => "招募說明可選取";

    public override string Description =>
        "招募詳細視窗打開時，在旁邊開一塊面板把說明文字用可反白複製的形式再顯示一次，並把裡面的網址做成可點的連結。" +
        "純疊圖，不掛 hook、不送封包。";

    public override ModuleCategory Category => ModuleCategory.Combat;

    private const string DetailAddon = "LookingForGroupDetail";

    /// <summary>招募詳細視窗裡「說明」文字節點的 id。</summary>
    private const uint DescriptionTextNodeId = 20;

    private static readonly Regex UrlRegex = new(
        @"(https?://[^\s]+)|(www\.[^\s]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private bool detailOpen;

    // 每幀由節點重讀；InputTextMultiline 需要一個後端字串（唯讀不會被寫回）。
    private string currentText = string.Empty;
    private readonly List<string> currentUrls = [];

    protected override void OnEnable()
    {
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, DetailAddon, OnDetailToggle);
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, DetailAddon, OnDetailToggle);
        Svc.PluginInterface.UiBuilder.Draw += DrawPanel;

        detailOpen = UiHelper.IsAddonReady(DetailAddon);
    }

    protected override void OnDisable()
    {
        Svc.PluginInterface.UiBuilder.Draw -= DrawPanel;
        Svc.AddonLifecycle.UnregisterListener(OnDetailToggle);

        detailOpen = false;
        currentText = string.Empty;
        currentUrls.Clear();
    }

    private void OnDetailToggle(AddonEvent type, AddonArgs args)
    {
        detailOpen = type == AddonEvent.PostSetup;
        if (!detailOpen)
        {
            currentText = string.Empty;
            currentUrls.Clear();
        }
    }

    private void DrawPanel()
    {
        if (!detailOpen) return;

        var addon = UiHelper.GetAddon(DetailAddon);
        if (!UiHelper.IsReady(addon)) return;

        var textNode = addon->GetTextNodeById(DescriptionTextNodeId);
        if (textNode == null) return;

        var text = textNode->NodeText.ToString();
        if (string.IsNullOrWhiteSpace(text)) return;

        if (text != currentText)
        {
            currentText = text;
            RefreshUrls(text);
        }

        // 面板貼在詳細視窗右緣；取不到根節點就不顯示（安全失敗）。
        var root = addon->RootNode;
        if (root == null) return;

        var scale = addon->Scale;
        var anchorX = addon->GetX() + (root->Width * scale);
        var anchorY = addon->GetY();

        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar |
                                       ImGuiWindowFlags.NoCollapse |
                                       ImGuiWindowFlags.NoScrollbar |
                                       ImGuiWindowFlags.NoSavedSettings |
                                       ImGuiWindowFlags.AlwaysAutoResize;

        ImGui.SetNextWindowPos(new Vector2(anchorX + 4, anchorY), ImGuiCond.Always);
        ImGui.SetNextWindowSizeConstraints(new Vector2(220, 0), new Vector2(360, 640));

        if (ImGui.Begin("###TCToolboxSelectableRecruitmentText", flags))
        {
            ImGui.TextDisabled("招募說明（可反白複製）");

            var lines = Math.Clamp(currentText.Split('\n').Length + 1, 3, 14);
            var size = new Vector2(340, ImGui.GetTextLineHeight() * lines);
            var buffer = currentText;
            ImGui.InputTextMultiline("##pfDescCopy", ref buffer, 4096, size, ImGuiInputTextFlags.ReadOnly);

            if (currentUrls.Count > 0)
            {
                ImGui.Separator();
                ImGui.TextDisabled("連結：");
                foreach (var url in currentUrls)
                {
                    if (ImGui.Selectable(url))
                        Util.OpenLink(url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? url : "http://" + url);

                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("用預設瀏覽器開啟");
                }
            }
        }

        ImGui.End();
    }

    private void RefreshUrls(string text)
    {
        currentUrls.Clear();
        foreach (Match m in UrlRegex.Matches(text))
        {
            var url = m.Value.TrimEnd('.', ',', '，', '。', ')', '）', '、');
            if (url.Length > 0 && !currentUrls.Contains(url))
                currentUrls.Add(url);
        }
    }

    public override void DrawConfig()
    {
        ImGui.TextWrapped(
            "招募詳細視窗打開時會自動在它右邊顯示一塊面板，內含可反白複製的說明文字與可點的網址。此模組沒有其他設定。");
    }
}

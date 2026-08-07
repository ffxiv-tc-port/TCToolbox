using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using TCToolbox.Core;

namespace TCToolbox.Windows;

public sealed class MainWindow : Window
{
    private readonly Plugin plugin;

    public MainWindow(Plugin plugin) : base("TC Toolbox###TCToolboxMain")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(560, 320),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public override void Draw()
    {
        ImGui.TextDisabled("台服雜項 QoL 模組集。每個模組獨立開關（預設關閉），變更即時生效並自動存檔。");
        ImGui.Separator();
        ImGui.Spacing();

        using var tabs = ImRaii.TabBar("##TCToolboxCategories");
        if (!tabs) return;

        foreach (var category in ModuleCategoryInfo.DisplayOrder)
            DrawCategoryTab(category);

        DrawAllTab();
    }

    /// <summary>畫一個分類分頁。</summary>
    /// <remarks>
    /// 🔴 分頁標題帶了「已啟用/總數」，那是<b>會變的字串</b>——所以 ImGui 的 id 必須靠
    /// <c>###</c> 後面那段固定的英文名撐住。用中文標題當 id 的話，使用者每勾一個模組
    /// 數字就變、ImGui 就當成一個全新的分頁，<b>畫面會跳回第一頁</b>。
    /// </remarks>
    private void DrawCategoryTab(ModuleCategory category)
    {
        // 計數要在建立分頁之前算完（標題需要它）。39 個模組的走訪成本可以忽略。
        var total = 0;
        var enabled = 0;
        foreach (var module in plugin.Modules)
        {
            if (module.Category != category) continue;
            total++;
            if (module.IsEnabled) enabled++;
        }

        var id = ModuleCategoryInfo.Id(category);

        using var tab = ImRaii.TabItem($"{ModuleCategoryInfo.Title(category)} ({enabled}/{total})###tab-{id}");
        if (!tab) return;

        // 分頁內容自己捲動，否則整條分頁列會跟著捲走。
        using var child = ImRaii.Child($"##scroll-{id}", Vector2.Zero, false);
        if (!child) return;

        foreach (var module in plugin.Modules)
        {
            if (module.Category != category) continue;
            DrawModuleRow(module);
        }
    }

    /// <summary>
    /// 「全部」分頁：維持改成分頁之前的那條長清單。
    /// </summary>
    /// <remarks>
    /// 📌 這頁是<b>安全網</b>，不是備援畫面：
    /// <list type="bullet">
    /// <item>使用者原本就習慣一條長清單，想不起某個模組被分到哪一頁時不會卡住。</item>
    /// <item>萬一有模組的 <see cref="ModuleCategory"/> 沒被列進
    /// <see cref="ModuleCategoryInfo.DisplayOrder"/>，它在分類分頁上會完全消失而且不報錯——
    /// 這頁是它唯一還看得到的地方。</item>
    /// </list>
    /// </remarks>
    private void DrawAllTab()
    {
        var enabled = 0;
        foreach (var module in plugin.Modules)
        {
            if (module.IsEnabled) enabled++;
        }

        using var tab = ImRaii.TabItem($"全部 ({enabled}/{plugin.Modules.Count})###tab-All");
        if (!tab) return;

        using var child = ImRaii.Child("##scroll-All", Vector2.Zero, false);
        if (!child) return;

        foreach (var module in plugin.Modules)
            DrawModuleRow(module);
    }

    /// <summary>畫一個模組列：勾選框、顯示名、列上提示、描述、設定。</summary>
    /// <remarks>
    /// 分類分頁與「全部」分頁共用這一份。同一個模組同時出現在兩頁不會撞 id——
    /// 一次只有一個分頁是展開的，而且兩邊各自在不同的子視窗裡。
    /// ⚠️ 代價是「設定」TreeNode 的展開狀態兩頁不共用（ImGui 的 id 含所在視窗）。
    /// </remarks>
    private void DrawModuleRow(TcModule module)
    {
        using var id = ImRaii.PushId(module.InternalName);

        var enabled = module.IsEnabled;
        if (ImGui.Checkbox($"##enable-{module.InternalName}", ref enabled))
            plugin.SetModuleEnabled(module, enabled);

        ImGui.SameLine();
        ImGui.TextUnformatted(module.DisplayName);

        DrawRowNotice(module);

        using (ImRaii.PushIndent())
        {
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
            ImGui.TextDisabled(module.Description);
            ImGui.PopTextWrapPos();

            if (module.IsEnabled && module.HasConfigUI)
            {
                if (ImGui.TreeNodeEx($"設定###cfg-{module.InternalName}", ImGuiTreeNodeFlags.None))
                {
                    module.DrawConfig();
                    ImGui.TreePop();
                }
            }
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
    }

    private static readonly Vector4 NoticeWarnColor = new(1f, 0.65f, 0.25f, 1f);
    private static readonly Vector4 NoticeUnknownColor = new(0.68f, 0.68f, 0.68f, 1f);

    /// <summary>
    /// 畫模組列上的提示。
    /// </summary>
    /// <remarks>
    /// 🔴 <see cref="TcModule.RowNotice"/> 的實作可能要做 I/O（例如去讀別的外掛的設定檔），
    /// 而這裡是 ImGui 的 Draw 路徑：<b>擲一次例外，Dalamud 就把整個 <c>UiBuilder.Draw</c>
    /// 設成 null，介面到重開遊戲前都不會回來</b>。所以整段包 try，失敗就當作沒有提示。
    /// </remarks>
    private static void DrawRowNotice(TcModule module)
    {
        ModuleNotice? notice;
        try
        {
            notice = module.RowNotice;
        }
        catch (Exception ex)
        {
            if (Throttle.Pass($"MainWindow-RowNotice-{module.InternalName}", 60_000))
                Svc.Log.Information($"[TCToolbox] 模組 {module.InternalName} 的列上提示計算失敗，本次不顯示：{ex.Message}");
            return;
        }

        if (notice is not { } value || string.IsNullOrEmpty(value.Text)) return;

        ImGui.SameLine();
        ImGui.TextColored(
            value.Level == ModuleNoticeLevel.Warning ? NoticeWarnColor : NoticeUnknownColor,
            value.Text);

        if (!string.IsNullOrEmpty(value.Tooltip) && ImGui.IsItemHovered())
            ImGui.SetTooltip(value.Tooltip);
    }
}

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

        foreach (var module in plugin.Modules)
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

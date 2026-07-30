using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

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
}

using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using TCToolbox.Modules;

namespace TCToolbox.Windows;

/// <summary>
/// 「換 World 與副本區」的獨立視窗。
/// </summary>
/// <remarks>
/// 🔴 這扇視窗由 <see cref="WorldTravelPanel"/> 模組在啟用時掛進共用的 WindowSystem、
/// 停用時拆掉。<b>模組關掉之後不會留在畫面上。</b>
/// 🔴 <c>Draw()</c> 是 ImGui 路徑，<b>不得擲例外</b>。這裡只呼叫面板本體，
/// 而面板本體只讀已經在 <c>Framework.Update</c> 算好的欄位——不做 IPC、不碰原生記憶體。
/// </remarks>
public sealed class WorldTravelWindow : Window
{
    private readonly WorldTravelPanel module;

    public WorldTravelWindow(WorldTravelPanel module)
        : base("換 World 與副本區###TCToolboxWorldTravel")
    {
        this.module = module;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(520, 260),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public override void Draw()
    {
        module.DrawPanel();

        ImGui.Spacing();
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
        ImGui.TextDisabled("滑鼠移到按鈕上可以看到那一趟會發生什麼（會不會跨資料中心、會不會先傳回原始 World）。");
        ImGui.PopTextWrapPos();
    }
}

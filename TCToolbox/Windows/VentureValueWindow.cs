using System.Numerics;
using Dalamud.Interface.Windowing;
using TCToolbox.Core;
using TCToolbox.Modules;

namespace TCToolbox.Windows;

/// <summary>「僱員探險收益」的獨立視窗。</summary>
/// <remarks>
/// 由 <see cref="VentureValueAdvisor"/> 在啟用時掛進 WindowSystem、停用時拆掉。
/// <c>Draw()</c> 是 ImGui 路徑、不得擲例外：只讀 <c>Framework.Update</c> 算好的欄位，不做 IPC。
/// </remarks>
public sealed class VentureValueWindow : Window
{
    private readonly VentureValueAdvisor module;

    public VentureValueWindow(VentureValueAdvisor module)
        : base("僱員探險收益###TCToolboxVentureValue")
    {
        this.module = module;
    }

    public override void PreDraw()
    {
        SizeConstraints = UiScale.MinWindowSize(640, 240);
        base.PreDraw();
    }

    public override void OnOpen() => module.OnWindowOpened();

    public override void Draw() => module.DrawPanel();
}

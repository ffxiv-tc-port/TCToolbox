using System.Numerics;
using Dalamud.Interface.Windowing;
using TCToolbox.Modules;

namespace TCToolbox.Windows;

/// <summary>
/// 「天書奇談換格建議」的獨立視窗。
/// </summary>
/// <remarks>
/// 🔴 由 <see cref="WeeklyBingoAdvisor"/> 模組在啟用時掛進共用的 WindowSystem、停用時拆掉，
/// 所以<b>模組關掉之後不會留在畫面上</b>。
/// 🔴 <c>Draw()</c> 是 ImGui 路徑，<b>不得擲例外</b>：這裡只呼叫面板本體，
/// 而面板本體只讀已經在 <c>Framework.Update</c> 算好的欄位——不做 IPC、不碰原生記憶體。
/// </remarks>
public sealed class WeeklyBingoAdvisorWindow : Window
{
    private readonly WeeklyBingoAdvisor module;

    public WeeklyBingoAdvisorWindow(WeeklyBingoAdvisor module)
        : base("天書奇談換格建議###TCToolboxWeeklyBingoAdvisor")
    {
        this.module = module;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(620, 320),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public override void Draw() => module.DrawPanel();
}

using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;

namespace TCToolbox.Core;

/// <summary>
/// 寫死的設計像素依目前字級換算：值 × <c>ImGui.GetFontSize()</c> ÷ Dalamud 出廠字級。
/// <c>GetFontSize()</c> 已含全域縮放，所以不再乘 GlobalScale；出廠字級時 <c>Px(x)</c>＝x×GlobalScale。
/// 只在繪製期間呼叫（量的是當下推入的字型）；負值（-1＝填滿）與 0 不要傳進來。
/// 只用在自己的獨立視窗與設定頁；貼在原生視窗旁、位置照原生視窗算的按鈕與面板維持寫死像素。
/// </summary>
public static class UiScale
{
    // Dalamud 出廠字級（internal InterfaceManager.DefaultFontSizePx）；不用 UiBuilder.DefaultFontSizePx：那是使用者改過的字級，當分母會抵銷放大。
    private const float FactoryFontSizePx = 12f * 4f / 3f;

    public static float Px(float designPixels) => designPixels * ImGui.GetFontSize() / FactoryFontSizePx;

    public static Vector2 Px(float designX, float designY) => new(Px(designX), Px(designY));

    /// <summary>
    /// 給 Dalamud <see cref="Window"/> 的最小尺寸：Window 套用時會再乘 GlobalScale，這裡先除掉。
    /// 要在 <c>PreDraw</c> 呼叫（字級可在執行中改，建構子裡也還沒有繪製中的字型）。
    /// </summary>
    public static Window.WindowSizeConstraints MinWindowSize(float designX, float designY)
    {
        var factor = ImGui.GetFontSize() / (FactoryFontSizePx * ImGuiHelpers.GlobalScale);
        return new Window.WindowSizeConstraints
        {
            MinimumSize = new Vector2(designX * factor, designY * factor),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }
}

using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using TCToolbox.Modules;

namespace TCToolbox.Windows;

/// <summary>
/// 「換 World 與副本區」的獨立視窗。
/// </summary>
/// <remarks>
/// <para>
/// 📌 <b>為什麼是獨立視窗而不是只塞進主視窗的模組列</b>：換 World 與換副本區是「路過想到就按一下」
/// 的動作，要人先開主視窗、找到分頁、展開模組設定才按得到，等於沒做。
/// 這扇視窗預設關閉，由 <c>/tcworld</c> 或設定裡的按鈕打開。
/// </para>
/// <para>
/// 🔴 這扇視窗由 <see cref="WorldTravelPanel"/> 模組在啟用時掛進共用的 WindowSystem、
/// 停用時拆掉。<b>模組關掉之後不會留在畫面上。</b>
/// </para>
/// <para>
/// 🔴 <c>Draw()</c> 是 ImGui 路徑，<b>不得擲例外</b>。這裡只呼叫面板本體，
/// 而面板本體只讀已經在 <c>Framework.Update</c> 算好的欄位——不做 IPC、不碰原生記憶體。
/// </para>
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

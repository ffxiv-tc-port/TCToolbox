using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using TCToolbox.Modules;

namespace TCToolbox.Windows;

/// <summary>
/// 全艦隊急停的結果視窗：一顆大按鈕 ＋ 每個對象的三態結果。
/// </summary>
/// <remarks>
/// 🔴 這扇視窗由 <see cref="FleetEmergencyStop"/> 模組在啟用時掛進共用的 WindowSystem、
/// 停用時拆掉。<b>模組關掉之後不會留在畫面上。</b>
/// 🔴 <c>Draw()</c> 是 ImGui 路徑，<b>不得擲例外</b>：Dalamud 的視窗錯誤閂鎖會把它換成錯誤面板。
/// 這裡只讀已經算好的結果清單，不做 I/O、不碰原生記憶體。
/// </remarks>
public sealed class FleetStopWindow : Window
{
    private readonly FleetEmergencyStop module;

    public FleetStopWindow(FleetEmergencyStop module)
        : base("全艦隊急停###TCToolboxFleetStop")
    {
        this.module = module;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(360, 220),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public override void Draw()
    {
        module.DrawBigButton();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // 📌 放在大按鈕與「上次結果」之間：這一格回答的是「我按之前該知道什麼」
        //    與「我按了之後為什麼還在動」，兩個問題都發生在這扇視窗開著的時候。
        ControlStatusPanel.Draw();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        FleetEmergencyStop.DrawResults();

        ImGui.Spacing();
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
        ImGui.TextDisabled("滑鼠移到每一列上可以看到細節（呼叫了哪些端點、失敗訊息）。");
        ImGui.PopTextWrapPos();
    }
}

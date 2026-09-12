using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using TCToolbox.Core;

namespace TCToolbox.Windows;

/// <summary>
/// 「現在是誰在控制」面板：一格唯讀顯示，急停視窗與急停模組的設定面板共用同一份。
/// </summary>
/// <remarks>
/// 🔴 <b>三態一律留在列上，包括「不知道」。</b>問不到的項目畫成灰色的「？」而不是
/// 「沒有人壓著」——後者會讓一扇其實什麼都沒問到的面板看起來一切正常。
/// 細節（問了哪些端點、錯誤訊息）放 tooltip，那是「起疑才查」的東西。
/// </remarks>
internal static class ControlStatusPanel
{
    /// <summary>有人正在控制（＝你要找的那一行）。</summary>
    private static readonly Vector4 ActiveColor = new(1f, 0.65f, 0.25f, 1f);

    /// <summary>問到了，而且沒人在控制。</summary>
    private static readonly Vector4 IdleColor = new(0.42f, 0.85f, 0.45f, 1f);

    /// <summary>問不到。<b>不是</b>「沒有」。</summary>
    private static readonly Vector4 UnknownColor = new(0.62f, 0.62f, 0.62f, 1f);

    public static void Draw()
    {
        ImGui.TextDisabled("現在是誰在控制（唯讀，不會改任何東西）");

        try
        {
            foreach (var row in FleetControlStatus.Snapshot())
                DrawRow(row);
        }
        catch (Exception ex)
        {
            ImGui.TextColored(UnknownColor, "狀態查詢失敗，本次不顯示。");

            if (Throttle.Pass("ControlStatusPanel-Draw", 60_000))
                Svc.Log.Information($"[ControlStatusPanel] 狀態查詢失敗：{ex}");
        }
    }

    private static void DrawRow(ControlStatusRow row)
    {
        var color = row.State switch
        {
            ControlState.Active => ActiveColor,
            ControlState.Idle => IdleColor,
            _ => UnknownColor,
        };

        ImGui.TextDisabled($"{row.Subject}：");
        ImGui.SameLine();
        ImGui.TextColored(color, row.Text);

        if (!string.IsNullOrEmpty(row.Detail) && ImGui.IsItemHovered())
            ImGui.SetTooltip(row.Detail);
    }
}

using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using TCToolbox.Modules;

namespace TCToolbox.Windows;

/// <summary>
/// 「製作清單缺料與採買」的獨立視窗。
/// </summary>
/// <remarks>
/// 🔴 這扇視窗由 <see cref="CraftShoppingList"/> 模組在啟用時掛進共用的 WindowSystem、
/// 停用時拆掉。<b>模組關掉之後不會留在畫面上。</b>
/// 🔴 <c>Draw()</c> 是 ImGui 路徑，<b>不得擲例外</b>。這裡只呼叫面板本體，
/// 而面板本體只讀已經在 <c>Framework.Update</c> 算好的欄位——不做 IPC、不碰原生記憶體。
/// </remarks>
public sealed class CraftShoppingListWindow : Window
{
    private readonly CraftShoppingList module;

    public CraftShoppingListWindow(CraftShoppingList module)
        : base("製作清單缺料與採買###TCToolboxCraftShoppingList")
    {
        this.module = module;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(820, 360),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public override void Draw()
    {
        module.DrawPanel();

        ImGui.Spacing();
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
        ImGui.TextDisabled(
            "滑鼠移到任何一格上都有說明：哪些成品要用到這個材料、商人的完整清單與代價、"
            + "價格是什麼時候看到的。灰色的「?」一律代表「不知道」，不是 0。");
        ImGui.PopTextWrapPos();
    }
}

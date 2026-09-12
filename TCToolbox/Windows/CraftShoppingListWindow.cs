using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using TCToolbox.Modules;

namespace TCToolbox.Windows;

/// <summary>
/// 「製作清單缺料與採買」的獨立視窗。
/// </summary>
/// <remarks>
/// <para>
/// 📌 <b>為什麼是獨立視窗而不是塞進主視窗的模組列</b>：這是一張要一邊看一邊按的表
/// （七欄、每列兩顆按鈕），擠在模組設定的縮排裡根本讀不了；而且它要在使用者跑去採買的
/// 那段時間一直開著。這扇視窗預設關閉，由 <c>/tcshop</c> 或設定裡的按鈕打開。
/// </para>
/// <para>
/// 🔴 <b>刻意不動任何既有清單視窗的版面。</b>AllaganTools 與 Item Vendor Location 各有
/// 自己的視窗，這一扇只回答「缺什麼、哪裡買」，細節一律按鈕跳去對方那邊看。
/// </para>
/// <para>
/// 🔴 這扇視窗由 <see cref="CraftShoppingList"/> 模組在啟用時掛進共用的 WindowSystem、
/// 停用時拆掉。<b>模組關掉之後不會留在畫面上。</b>
/// </para>
/// <para>
/// 🔴 <c>Draw()</c> 是 ImGui 路徑，<b>不得擲例外</b>。這裡只呼叫面板本體，
/// 而面板本體只讀已經在 <c>Framework.Update</c> 算好的欄位——不做 IPC、不碰原生記憶體。
/// </para>
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

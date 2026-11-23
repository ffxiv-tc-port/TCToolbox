using System.Numerics;
using Dalamud.Interface.Windowing;
using TCToolbox.Modules;

namespace TCToolbox.Windows;

/// <summary>「角色總覽」的獨立視窗。</summary>
/// <remarks>
/// 由 <see cref="CharacterOverview"/> 在啟用時掛進 WindowSystem、停用時拆掉。
/// <c>Draw()</c> 是 ImGui 路徑、不得擲例外：只讀 <c>Framework.Update</c> 算好的列，不做 IPC、不碰原生記憶體。
/// </remarks>
public sealed class CharacterOverviewWindow : Window
{
    private readonly CharacterOverview module;

    public CharacterOverviewWindow(CharacterOverview module)
        : base("角色總覽###TCToolboxCharacterOverview")
    {
        this.module = module;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(560, 200),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public override void OnOpen() => module.OnWindowOpened();

    public override void Draw() => module.DrawPanel();
}

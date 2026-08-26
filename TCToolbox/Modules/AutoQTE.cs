using System.Diagnostics;
using System.Runtime.InteropServices;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Component.GUI;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 自動完成 QTE（連打／長按／按鈕）。
/// 機制：QTE 系 addon PostDraw 時對遊戲視窗送出空白鍵按鍵訊息並清除 UI 焦點，零 hook。
/// 參考 DailyRoutines AutoQTE 設計重寫（API13、無 OmenTools 相依）。
/// </summary>
public sealed unsafe class AutoQTE : TcModule
{
    public override string InternalName => "AutoQTE";
    public override string DisplayName => "自動 QTE";
    public override string Description => "副本／劇情中出現 QTE（連打、長按、按鈕）時自動連發空白鍵完成，不必手動狂按。";

    public override ModuleCategory Category => ModuleCategory.Misc;

    private static readonly string[] QteAddonNames = ["_QTEKeep", "_QTEMash", "_QTEKeepTime", "_QTEButton"];

    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_KEYUP = 0x0101;
    private const nuint VK_SPACE = 0x20;

    private nint gameWindowHandle;

    protected override void OnEnable()
    {
        gameWindowHandle = Process.GetCurrentProcess().MainWindowHandle;
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostDraw, QteAddonNames, OnQtePostDraw);
    }

    protected override void OnDisable()
    {
        Svc.AddonLifecycle.UnregisterListener(OnQtePostDraw);
    }

    private void OnQtePostDraw(AddonEvent type, AddonArgs args)
    {
        var addon = (AtkUnitBase*)args.Addon.Address;
        if (addon == null || !addon->IsVisible) return;

        // 每 40ms 一次（約每 2~3 幀），足以完成連打並避免訊息洪流
        if (!Throttle.Pass("AutoQTE-Press", 40)) return;

        if (gameWindowHandle == 0)
            gameWindowHandle = Process.GetCurrentProcess().MainWindowHandle;
        if (gameWindowHandle == 0) return;

        PostMessageW(gameWindowHandle, WM_KEYDOWN, VK_SPACE, 0);
        PostMessageW(gameWindowHandle, WM_KEYUP, VK_SPACE, 0);

        // 🔴 AtkStage.Instance() 是 isPointer:true 的靜態位址，可能回 null；ClearFocus() 是
        //    原生成員函式，對 null 呼叫是攔不到的 AVE。判空樣板同 ClickToMove.IsCursorOverGameUi。
        //    這是 QTE addon 的繪製後回呼（節流過的熱路徑），不記 log；取不到就只是這次不清焦點，
        //    按鍵已經送出去了，功能不受影響。
        var stage = AtkStage.Instance();
        if (stage == null) return;
        stage->ClearFocus();
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool PostMessageW(nint hWnd, uint msg, nuint wParam, nint lParam);
}

using System;
using KamiToolKit;

namespace TCToolbox.Core;

/// <summary>
/// 全插件共用的 KamiToolKit <see cref="NativeController"/>：第一次有模組要原生節點時才建，插件卸載時釋放。
/// </summary>
/// <remarks>契約：所有成員只在框架執行緒呼叫。</remarks>
internal static class KamiNative
{
    private static NativeController? controller;
    private static bool initFailed;
    private static bool abandonedNodes;

    /// <summary>取得（必要時建立）控制器；建立失敗只試一次，之後一律回 <see langword="null"/>。</summary>
    public static NativeController? Ensure()
    {
        if (controller != null || initFailed) return controller;

        try
        {
            controller = new NativeController(Svc.PluginInterface);
        }
        catch (Exception ex)
        {
            initFailed = true;
            Svc.Log.Warning(ex, "[TCToolbox] KamiToolKit 原生節點初始化失敗，原生文字改回疊圖顯示");
        }

        return controller;
    }

    public static NativeController? Controller => controller;

    /// <summary>有節點因為 addon 已被釋放而只能放著不碰。</summary>
    public static void MarkAbandoned() => abandonedNodes = true;

    public static void ForceTeardown()
    {
        // 刻意不呼叫 Dispose：它會強制拆所有還掛著的節點，而被放棄的節點父節點已經被釋放。
        if (!abandonedNodes) controller?.Dispose();
        controller = null;
    }
}

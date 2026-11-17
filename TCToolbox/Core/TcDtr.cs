using System;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;

namespace TCToolbox.Core;

/// <summary>資訊列（DTR）條目的共用小工具：左右鍵分派、圖示＋文字、移除。</summary>
/// <remarks>
/// 本插件的左右鍵慣例：右鍵一律開關 TC Toolbox 設定，左鍵做項目自己的事；要改慣例只改 <see cref="Click"/>。
/// 只在框架執行緒呼叫（<see cref="IDtrBarEntry"/> 的成員都是）。
/// </remarks>
internal static class TcDtr
{
    /// <summary>左鍵做 <paramref name="onLeft"/>、其他鍵開關 TC Toolbox 設定；<paramref name="onLeft"/> 為 null＝左右鍵都開關設定。</summary>
    public static Action<DtrInteractionEvent> Click(Action? onLeft)
    {
        if (onLeft == null)
            return static _ => Plugin.Instance.ToggleMainWindow();

        return ev =>
        {
            if (ev.ClickType != MouseClickType.Left) Plugin.Instance.ToggleMainWindow();
            else onLeft();
        };
    }

    /// <summary>資訊列常用的「一顆圖示＋一段文字」。</summary>
    public static SeString IconText(BitmapFontIcon icon, string text) =>
        new(new IconPayload(icon), new TextPayload(text));

    /// <summary>移除條目並清掉參考；可重複呼叫。</summary>
    public static void Remove(ref IDtrBarEntry? entry)
    {
        entry?.Remove();
        entry = null;
    }
}

using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ManagedFontAtlas;

namespace TCToolbox.Core;

/// <summary>
/// 疊圖文字用的字型：照倍率用 Dalamud 預設字型建出原生大小的字，放大不再是點陣拉伸。
/// </summary>
/// <remarks>
/// 契約：只在繪製執行緒使用；擁有者停用時一定要 <see cref="Dispose"/>。
/// 新字建好前沿用舊字（或預設字型）以呼叫端要的大小畫，所以字級切換不會有空白幀。
/// </remarks>
internal sealed class OverlayFont : IDisposable
{
    private IFontHandle? current;
    private float currentScale;
    private IFontHandle? pending;
    private float pendingScale;

    /// <summary>要求以 <paramref name="scale"/>（相對預設字型）建字；<paramref name="hold"/> 為真時只沿用不新建。</summary>
    public void Request(float scale, bool hold)
    {
        scale = OverlayTextScale.Quantize(scale);
        if (pending is { Available: true })
        {
            current?.Dispose();
            current = pending;
            currentScale = pendingScale;
            pending = null;
        }

        float? existing = pending != null ? pendingScale : current != null ? currentScale : null;
        if (!OverlayTextScale.ShouldRebuild(scale, existing, hold)) return;

        pending?.Dispose();
        pendingScale = scale;
        pending = Svc.PluginInterface.UiBuilder.FontAtlas.NewDelegateFontHandle(
            e => e.OnPreBuild(tk => tk.AddDalamudDefaultFont(-scale)));
    }

    /// <summary>推入已建好的字；還沒建好回 <see langword="null"/>（照用目前字型）。</summary>
    public IDisposable? Push() => current is { Available: true } handle ? handle.Push() : null;

    public void Dispose()
    {
        pending?.Dispose();
        pending = null;
        current?.Dispose();
        current = null;
    }

    /// <summary>
    /// 以 <paramref name="fontPx"/> 像素畫一行帶黑色外框的字（目前推入的字型）。
    /// </summary>
    public static void DrawOutlined(ImDrawListPtr drawList, float fontPx, Vector2 origin, uint color, string text)
    {
        var font = ImGui.GetFont();
        var outline = ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 0.85f));
        var t = OverlayTextScale.OutlineThickness(fontPx);

        for (var dx = -1; dx <= 1; dx++)
        {
            for (var dy = -1; dy <= 1; dy++)
            {
                if (dx == 0 && dy == 0) continue;
                drawList.AddText(font, fontPx, origin + new Vector2(dx * t, dy * t), outline, text);
            }
        }

        drawList.AddText(font, fontPx, origin, color, text);
    }
}

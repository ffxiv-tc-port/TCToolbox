using System;

namespace TCToolbox.Core;

/// <summary>疊圖文字字級的純計算（不碰 Dalamud／遊戲，離線可驗）。</summary>
internal static class OverlayTextScale
{
    /// <summary>字型控制代碼的倍率精度；倍率相同（到這個精度）就沿用已建好的字。</summary>
    public const float Step = 0.01f;

    /// <summary>相對 Dalamud 預設字型的倍率。不跟 HUD 縮放時恆等於 <paramref name="textScale"/>（＝既有字級）。</summary>
    public static float Effective(float textScale, bool followHud, float hudScale) =>
        followHud && hudScale > 0f && float.IsFinite(hudScale) ? textScale * hudScale : textScale;

    public static float Quantize(float scale) => MathF.Round(scale / Step) * Step;

    /// <summary>要不要新建字型：拖滑桿中（<paramref name="hold"/>）不建，倍率和已建／建置中的相同也不建。</summary>
    public static bool ShouldRebuild(float wanted, float? builtOrPending, bool hold)
    {
        if (hold || wanted <= 0f || !float.IsFinite(wanted)) return false;
        return builtOrPending is not { } existing || MathF.Abs(existing - wanted) >= Step / 2f;
    }

    /// <summary>外框寬度（像素）：字越大框越粗，最少 1。</summary>
    public static float OutlineThickness(float fontPx) => MathF.Max(1f, MathF.Round(fontPx / 18f));
}

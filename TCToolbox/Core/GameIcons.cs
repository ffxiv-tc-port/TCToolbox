using System.Collections.Generic;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;

namespace TCToolbox.Core;

/// <summary>遊戲圖示／貼圖取得（只取當幀可用的 wrap，不跨幀保存——跨幀共享即時 wrap 會崩潰）。</summary>
public static class GameIcons
{
    /// <summary>取得遊戲圖示的當幀貼圖，尚未載入完成時回傳 null。</summary>
    public static IDalamudTextureWrap? TryGet(uint iconId)
    {
        if (iconId == 0) return null;
        return Svc.Textures.GetFromGameIcon(new GameIconLookup(iconId)).TryGetWrap(out var wrap, out _)
                   ? wrap
                   : null;
    }

    /// <summary>取得遊戲檔案貼圖的當幀 wrap，尚未載入完成時回傳 null。</summary>
    public static IDalamudTextureWrap? TryGetFromPath(string gamePath)
    {
        if (string.IsNullOrEmpty(gamePath)) return null;
        return Svc.Textures.GetFromGame(gamePath).TryGetWrap(out var wrap, out _) ? wrap : null;
    }

    private static readonly Dictionary<uint, string?> LanguageIconPathCache = [];

    /// <summary>
    /// 取得「有語言子資料夾」的圖示（例如橫幅圖 120031）。
    /// 台服客端實際存在的語言資料夾以 <see cref="Dalamud.Plugin.Services.IDataManager.FileExists"/> 實測，
    /// 不預設任何語言代碼；找不到就回傳 null 讓呼叫端退回文字顯示。
    /// </summary>
    public static IDalamudTextureWrap? TryGetLanguageIcon(uint iconId)
    {
        if (!LanguageIconPathCache.TryGetValue(iconId, out var path))
        {
            path = ResolveLanguageIconPath(iconId);
            LanguageIconPathCache[iconId] = path;
        }

        return path == null ? null : TryGetFromPath(path);
    }

    private static readonly string[] LanguageFolders = ["cht", "chs", "ja", "en", "de", "fr", "ko"];

    private static string? ResolveLanguageIconPath(uint iconId)
    {
        var folder = iconId / 1000 * 1000;

        foreach (var lang in LanguageFolders)
        {
            var hiRes = $"ui/icon/{folder:D6}/{lang}/{iconId:D6}_hr1.tex";
            if (Svc.Data.FileExists(hiRes)) return hiRes;

            var normal = $"ui/icon/{folder:D6}/{lang}/{iconId:D6}.tex";
            if (Svc.Data.FileExists(normal)) return normal;
        }

        // 沒有語言子資料夾的情況
        var plainHiRes = $"ui/icon/{folder:D6}/{iconId:D6}_hr1.tex";
        if (Svc.Data.FileExists(plainHiRes)) return plainHiRes;

        var plain = $"ui/icon/{folder:D6}/{iconId:D6}.tex";
        return Svc.Data.FileExists(plain) ? plain : null;
    }

    /// <summary>Addon 表字串（台服自帶繁中）。</summary>
    public static string AddonText(uint rowId)
    {
        var row = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Addon>().GetRowOrDefault(rowId);
        return row?.Text.ExtractText() ?? string.Empty;
    }

    /// <summary>畫一個圖示按鈕；貼圖還沒載入時退回文字按鈕。</summary>
    public static bool IconButton(uint iconId, string fallbackLabel, float size, bool dimmed)
    {
        var wrap = TryGet(iconId);
        if (wrap == null)
            return ImGui.Button($"{fallbackLabel}##icon{iconId}", new System.Numerics.Vector2(size, size));

        var tint = dimmed
                       ? new System.Numerics.Vector4(1f, 1f, 1f, 0.4f)
                       : new System.Numerics.Vector4(1f, 1f, 1f, 1f);

        return ImGui.ImageButton(wrap.Handle, new System.Numerics.Vector2(size, size),
                                 System.Numerics.Vector2.Zero, System.Numerics.Vector2.One, 0,
                                 new System.Numerics.Vector4(0f, 0f, 0f, 0f), tint);
    }
}

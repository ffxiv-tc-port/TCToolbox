using System;
using Dalamud.Interface.Utility.Raii;
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
    /// <para>
    /// ⚠️ 台服的語言子資料夾代碼是 <c>tc</c>——不是 cht／chs，也不在 Dalamud 的 ClientLanguage
    /// 列舉裡，所以 <c>ITextureProvider.GetFromGameIcon(new GameIconLookup(id, language: …))</c>
    /// 對這類圖示永遠取不到，必須直接指定原始路徑。
    /// </para>
    /// <para>
    /// 實證方式（2026-07-31）：離線解析
    /// <c>D:\FINAL FANTASY XIV TC\game\sqpack\ffxiv\060000.win32.index</c> 的資料夾雜湊表
    /// （index1 存 檔名雜湊＋資料夾雜湊，可獨立驗證資料夾路徑），逐一比對候選字串——
    /// 台服 ui/icon 底下唯一存在的語言子資料夾就是 <c>tc</c>
    /// （120000/tc 1268 檔、121000/tc 601 檔、128000/tc 824 檔；ja／en／de／fr／chs／cht／ko 全部不存在）。
    /// </para>
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

    /// <summary>台服實測結果 <c>tc</c> 排第一；其餘留作改版後的保險，最後才試無語言路徑。</summary>
    private static readonly string[] LanguageFolders = ["tc", "cht", "chs", "ja", "en", "de", "fr", "ko"];

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

    /// <summary>
    /// 「圖示編號」設定控制項：畫出目前的圖示 ＋ 一個輸入框 ＋ 一顆「預設」。
    /// </summary>
    /// <param name="label">欄位標籤。</param>
    /// <param name="current">目前的設定值（<b>哨兵 0＝跟隨內建預設值</b>）。</param>
    /// <param name="defaultValue">內建預設值。</param>
    /// <param name="apply">寫回設定（傳 0 代表「跟隨內建預設值」）。</param>
    /// <remarks>
    /// <para>
    /// 📌 <b>存在的理由</b>：圖示的<b>存在</b>可以離線驗證（直讀 sqpack 的 index），
    /// <b>長什麼樣子</b>不行。所以凡是寫死圖示編號的地方都該讓使用者自己換，
    /// 而且把圖示直接畫出來讓他對照——不然「這顆圖示在台服長得不對」只能等改版。
    /// </para>
    /// <para>
    /// 🔴 <b>清空／輸入 0 一律寫回哨兵 0，不要寫具體常數。</b>寫具體常數等於把編號烙死：
    /// 日後修正內建預設值時，對<b>所有既有使用者靜默無效</b>，哨兵機制形同虛設。
    /// </para>
    /// </remarks>
    public static void DrawIconIdSetting(string label, uint current, uint defaultValue, Action<uint> apply)
    {
        using var id = ImRaii.PushId(label);

        // 哨兵 0 ＝ 跟隨內建預設值；畫圖與輸入框一律顯示「實際會用的編號」，不要讓使用者看到 0。
        var effective = current is 0 ? defaultValue : current;

        var wrap = TryGet(effective);
        if (wrap != null)
        {
            ImGui.Image(wrap.Handle, new System.Numerics.Vector2(24f, 24f));
            ImGui.SameLine();
        }

        ImGui.SetNextItemWidth(120f);
        var value = (int)effective;
        if (ImGui.InputInt(label, ref value))
            apply(value <= 0 ? 0u : (uint)value);

        ImGui.SameLine();
        if (ImGui.SmallButton($"預設（目前 {defaultValue}）")) apply(0);

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                $"寫回「跟隨內建預設值」（目前是 {defaultValue}）。" + Environment.NewLine
                + "跟隨的意思是：日後內建預設值若有修正，你會自動吃到。");
        }
    }
}

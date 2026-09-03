using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace TCToolbox.Core;

/// <summary>某個外掛的某項功能與本模組重疊與否。</summary>
/// <remarks>
/// 🔴 <b>零值刻意是「不知道」</b>：漏判成 <see cref="NotInstalled"/> 等於靜默宣稱「沒有衝突」，
/// 而那正是使用者最不希望被騙的方向。
/// </remarks>
public enum ConflictState
{
    /// <summary>對方裝著，但讀不到它的設定 —— 無法判定。</summary>
    Unknown = 0,

    /// <summary>對方沒裝，或裝了但沒載入 —— 確定不重疊。</summary>
    NotInstalled = 1,

    /// <summary>對方裝著，那項功能是關的。</summary>
    Inactive = 2,

    /// <summary>對方裝著，那項功能開著 —— 正在重疊。</summary>
    Active = 3,
}

/// <summary>
/// 偵測 SimpleTweaks 是否裝著、以及它的某個 tweak 是不是開著。
/// </summary>
/// <remarks>
/// <para>
/// 為什麼需要這個：TCToolbox 有幾個模組與 SimpleTweaks 的 tweak 做同一件事，
/// 而「兩邊都開著」的失敗形式是<b>靜默的</b>——功能看起來正常，只是改了設定沒反應
/// （被另一邊擋住了）。使用者沒有辦法從遊戲裡看出是誰在擋。
/// </para>
/// <para>
/// 判定方式分兩段，兩段都可能失敗，而<b>兩種失敗要分得出來</b>：
/// <list type="number">
/// <item>對方有沒有載入 → 走 Dalamud 的 <c>InstalledPlugins</c>，不會失敗。</item>
/// <item>那個 tweak 有沒有開 → 讀對方的設定檔 <c>SimpleTweaksPlugin.json</c> 的
/// <c>EnabledTweaks</c> 陣列。檔案不存在／格式變了／讀取失敗 ⇒ <see cref="ConflictState.Unknown"/>，
/// <b>不是</b> <see cref="ConflictState.Inactive"/>。</item>
/// </list>
/// </para>
/// <para>
/// ⚠️ 設定檔是<b>對方在記憶體裡持有、變更時才寫回</b>的，所以這裡讀到的是「上次存檔的狀態」。
/// SimpleTweaks 在使用者切換 tweak 時就會存檔，所以延遲頂多是幾秒。
/// </para>
/// <para>
/// 🔴 這條路徑會被 ImGui 的 Draw 呼叫到 —— <b>絕對不能擲例外</b>
/// （Draw 擲一次例外，Dalamud 就把整個 <c>UiBuilder.Draw</c> 拆掉到重開遊戲為止）。
/// 所有 I/O 與解析都包在 try 裡，失敗一律降級成 <see cref="ConflictState.Unknown"/>。
/// </para>
/// </remarks>
public static class SimpleTweaksProbe
{
    /// <summary>SimpleTweaks 的外掛內部名（同時也是它設定檔的檔名主體）。</summary>
    public const string PluginInternalName = "SimpleTweaksPlugin";

    private const string ConfigFileName = PluginInternalName + ".json";

    /// <summary>重新確認的最短間隔（毫秒）。Draw 每幀都會問，這裡是唯一的節流。</summary>
    private const int RefreshIntervalMs = 5_000;

    /// <summary>上一次真的去探測的時間戳（<see cref="Environment.TickCount64"/>）。</summary>
    /// <remarks>
    /// 🔴 <b>初值不可以用 <c>long.MinValue</c> 當哨兵。</b>下面的節流寫成
    /// <c>now - lastProbeAt &lt; RefreshIntervalMs</c>，而 <c>now - long.MinValue</c> 會溢位成
    /// 巨大的<b>負數</b>，恆小於間隔 ⇒ <see cref="Refresh"/> 永遠在第一行就 return，
    /// 而 <c>lastProbeAt</c> 又只在那個 early-return 之後才被賦值 ⇒ 永遠不會被更新。
    /// 結果是整個 SimpleTweaks 衝突偵測從未執行過：<c>installed</c> 恆 false、
    /// <see cref="Query"/> 恆回 <see cref="ConflictState.NotInstalled"/>、
    /// 連這個類別特地要顯示的 <see cref="ConflictState.Unknown"/> 也永不出現
    /// ——正是它存在要防的那個靜默方向。
    /// 🔑 正解是讓哨兵<b>不要參與減法</b>：初值取 <c>-RefreshIntervalMs</c>，
    /// 因為 <c>TickCount64</c> 非負，<c>now - (-間隔) = now + 間隔 &gt;= 間隔</c> 恆成立
    /// ⇒ 首次必放行，而且永遠不會溢位。
    /// </remarks>
    private static long lastProbeAt = -RefreshIntervalMs;

    private static bool installed;

    /// <summary>已啟用 tweak 的鍵集合；<c>null</c> 代表讀不到（＝不知道）。</summary>
    private static HashSet<string>? enabledTweakKeys;

    /// <summary>最近一次讀不到設定檔的原因，拿去給使用者看。</summary>
    private static string lastError = string.Empty;

    /// <summary>設定檔的指紋（大小＋最後寫入時間），沒變就不重新解析。</summary>
    private static (long Length, DateTime WriteTimeUtc) lastFingerprint;

    /// <summary>設定檔的完整路徑（給提示文字用；解析不出來時是空字串）。</summary>
    public static string ConfigPath { get; private set; } = string.Empty;

    /// <summary>最近一次讀不到設定檔的原因（沒有問題時是空字串）。</summary>
    public static string LastError => lastError;

    /// <summary>
    /// 查詢某個 tweak 目前的狀態。
    /// </summary>
    /// <param name="tweakKey">
    /// tweak 的鍵，<b>不含提供者前綴</b>（例如 <c>HideUnwantedBanner</c>）。
    /// 設定檔裡的條目長成 <c>UiAdjustments@HideUnwantedBanner</c> 或裸鍵 <c>FixTarget</c>，
    /// 少數還帶 <c>::2</c> 之類的後綴——比對前一律正規化掉，見 <see cref="NormalizeTweakKey"/>。
    /// </param>
    public static ConflictState Query(string tweakKey)
    {
        Refresh();

        if (!installed) return ConflictState.NotInstalled;
        if (enabledTweakKeys == null) return ConflictState.Unknown;

        return enabledTweakKeys.Contains(NormalizeTweakKey(tweakKey))
            ? ConflictState.Active
            : ConflictState.Inactive;
    }

    private static void Refresh()
    {
        var now = Environment.TickCount64;
        if (now - lastProbeAt < RefreshIntervalMs) return;
        lastProbeAt = now;

        installed = IsPluginLoaded(PluginInternalName);
        if (!installed)
        {
            enabledTweakKeys = null;
            lastError = string.Empty;
            return;
        }

        ReloadConfig();
    }

    private static bool IsPluginLoaded(string internalName)
    {
        try
        {
            foreach (var plugin in Svc.PluginInterface.InstalledPlugins)
            {
                if (!plugin.IsLoaded) continue;
                if (string.Equals(plugin.InternalName, internalName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch (Exception ex)
        {
            // 問不到就當成沒裝（不會有誤報的警告），但把原因留在記錄裡。
            Svc.Log.Error(ex, $"[SimpleTweaksProbe] 列舉已安裝外掛失敗，視為 {internalName} 未安裝。");
        }

        return false;
    }

    private static void ReloadConfig()
    {
        try
        {
            // 本外掛自己的設定檔就躺在 pluginConfigs 底下，同一層即是所有外掛的設定檔。
            var dir = Svc.PluginInterface.ConfigFile.Directory;
            if (dir == null)
            {
                Fail("取不到設定檔目錄");
                return;
            }

            var path = Path.Combine(dir.FullName, ConfigFileName);
            ConfigPath = path;

            var info = new FileInfo(path);
            if (!info.Exists)
            {
                Fail("設定檔不存在");
                return;
            }

            var fingerprint = (info.Length, info.LastWriteTimeUtc);
            if (enabledTweakKeys != null && fingerprint == lastFingerprint) return;

            byte[] bytes;
            // 🔴 對方可能正在寫這個檔 —— FileShare 一定要放到 ReadWrite，否則會週期性地
            //    讀失敗，表現成「狀態一下知道一下不知道」。
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var buffer = new MemoryStream())
            {
                stream.CopyTo(buffer);
                bytes = buffer.ToArray();
            }

            var keys = ParseEnabledTweaks(bytes);
            if (keys == null)
            {
                Fail("設定檔裡找不到 EnabledTweaks 陣列");
                return;
            }

            enabledTweakKeys = keys;
            lastFingerprint = fingerprint;
            lastError = string.Empty;
        }
        catch (Exception ex)
        {
            Fail(ex.Message);
        }
    }

    /// <summary>
    /// 從設定檔位元組取出 <c>EnabledTweaks</c>。找不到那個鍵時回 <c>null</c>（＝不知道），
    /// 不回空集合——空集合會被誤讀成「確定都沒開」。
    /// </summary>
    private static HashSet<string>? ParseEnabledTweaks(byte[] bytes)
    {
        // 這個檔實測沒有 BOM，但別的機器上可能有；多剝一次不會出錯。
        var span = bytes.AsSpan();
        if (span.Length >= 3 && span[0] == 0xEF && span[1] == 0xBB && span[2] == 0xBF)
            span = span[3..];

        using var doc = JsonDocument.Parse(span.ToArray());
        if (!doc.RootElement.TryGetProperty("EnabledTweaks", out var array)) return null;
        if (array.ValueKind != JsonValueKind.Array) return null;

        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) continue;
            var value = item.GetString();
            if (string.IsNullOrWhiteSpace(value)) continue;
            keys.Add(NormalizeTweakKey(value));
        }

        return keys;
    }

    /// <summary>
    /// 把設定檔裡的條目正規化成純 tweak 鍵。
    /// </summary>
    /// <remarks>
    /// 實測使用者的設定檔同時存在三種寫法（2026-08-07 直讀 <c>SimpleTweaksPlugin.json</c>）：
    /// <c>ImprovedCraftingLog</c>（裸鍵）、<c>UiAdjustments@HideUnwantedBanner</c>（帶提供者前綴）、
    /// <c>UiAdjustments@StopCraftingButton::2</c>（帶後綴，出現在黑名單欄位）。
    /// 只比對其中一種寫法會靜默漏掉其他兩種。
    /// </remarks>
    private static string NormalizeTweakKey(string entry)
    {
        var value = entry;

        var at = value.LastIndexOf('@');
        if (at >= 0 && at + 1 < value.Length) value = value[(at + 1)..];

        var suffix = value.IndexOf("::", StringComparison.Ordinal);
        if (suffix >= 0) value = value[..suffix];

        return value.Trim();
    }

    /// <summary>
    /// 產生「本模組與 SimpleTweaks 某個 tweak 重疊」的標準提示，語氣與版面統一。
    /// </summary>
    /// <param name="tweakKey">
    /// tweak 的鍵，不含提供者前綴（例如 <c>QuickSellItems</c>）。
    /// 比對前會正規化，所以設定檔寫成 <c>UiAdjustments@X</c> 或裸鍵都吃得到。
    /// </param>
    /// <param name="tweakLabel">給人看的 tweak 名稱，含完整識別（例如 <c>UiAdjustments@ImprovedDutyFinderSettings</c>）。</param>
    /// <param name="detail">這兩邊到底怎麼撞在一起——每個模組自己講，因為撞法都不一樣。</param>
    /// <remarks>
    /// <para>
    /// 🔴 <b>只提示、不代決</b>：不自動關掉任何一邊，也不擋使用者啟用。
    /// 替使用者裁決「留哪一邊」不是這裡該做的事。
    /// </para>
    /// <para>
    /// 🔴 三種回傳只有兩種會顯示，而「不知道」<b>一定看得見</b>——
    /// 把未知畫成「沒事」等於靜默宣稱沒有衝突，那正是使用者最不希望被騙的方向。
    /// SimpleTweaks 沒裝、或那個 tweak 確認是關的，就回 <c>null</c>（列面保持乾淨）。
    /// </para>
    /// <para>
    /// ⚠️ 這條路徑在 ImGui 的 Draw 上被呼叫，<see cref="Query"/> 內部所有 I/O 都已包 try，
    /// 這裡只做字串組裝，不會擲例外。
    /// </para>
    /// </remarks>
    public static ModuleNotice? BuildNotice(string tweakKey, string tweakLabel, string detail) =>
        Query(tweakKey) switch
        {
            ConflictState.Active => new ModuleNotice(
                ModuleNoticeLevel.Warning,
                "! 與 SimpleTweaks 重複",
                $"SimpleTweaks 也裝著，而且它的「{tweakLabel}」是開著的。\n" +
                "\n" +
                detail +
                "\n\n" +
                "建議只留一邊：到 SimpleTweaks 把那個 tweak 關掉，或把這個模組關掉。"),

            ConflictState.Unknown => new ModuleNotice(
                ModuleNoticeLevel.Unknown,
                "? SimpleTweaks 狀態未知",
                $"SimpleTweaks 裝著，但讀不到它的設定檔，無法判斷「{tweakLabel}」是不是也開著。\n" +
                $"原因：{(string.IsNullOrEmpty(lastError) ? "（未記錄）" : lastError)}\n" +
                $"設定檔：{(string.IsNullOrEmpty(ConfigPath) ? "（路徑取不到）" : ConfigPath)}\n" +
                "\n" +
                "如果那個 tweak 其實是開著的：\n" +
                detail),

            _ => null,
        };

    private static void Fail(string reason)
    {
        enabledTweakKeys = null;
        lastError = reason;
        lastFingerprint = default;

        // Information 級：使用者跑 LogLevel 1，這句是事後回頭查「為什麼一直顯示未知」的唯一線索。
        if (Throttle.Pass("SimpleTweaksProbe-Fail", 60_000))
            Svc.Log.Information($"[SimpleTweaksProbe] 讀不到 SimpleTweaks 設定（{reason}），重疊偵測顯示為未知。路徑：{ConfigPath}");
    }
}

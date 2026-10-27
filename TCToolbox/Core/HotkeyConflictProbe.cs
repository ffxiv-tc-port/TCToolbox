using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using Dalamud.Game.ClientState.Keys;

namespace TCToolbox.Core;

/// <summary>按著這顆鍵會發生什麼。</summary>
public enum HotkeyTrigger
{
    /// <summary>不知道——<b>不參與撞鍵判定</b>，只在別的撞鍵的 tooltip 裡列出來。</summary>
    /// <remarks>零值刻意是這個：漏判成「按著就生效」會生出假警告，漏判成「要配合別的動作」
    /// 則是替沒查證過的東西背書，兩個都比誠實說「不知道」糟。</remarks>
    Unknown = 0,

    /// <summary>光按著這顆鍵就生效。</summary>
    HoldAlone = 1,

    /// <summary>還要配合點擊或別的鍵才生效。</summary>
    WithAction = 2,
}

/// <summary>
/// 資料表的一列：某個外掛的某個設定欄位是一顆「按著才生效」的鍵。
/// </summary>
/// <param name="PluginLabel">給人看的外掛短名（顯示在提示裡）。</param>
/// <param name="ConfigFileName">設定檔檔名；<b>空字串＝本外掛自己</b>，改從 <paramref name="InMemory"/> 取值。</param>
/// <param name="JsonPath">設定檔裡的路徑，最後一段是欄位名。BMR 的鍵名本身含有 <c>.</c>，所以只能用陣列不能用點字串。</param>
/// <param name="Purpose">這顆鍵按著會做什麼（給人看的一句話）。</param>
/// <param name="Trigger">按著它會發生什麼。<b>只有 <see cref="HotkeyTrigger.HoldAlone"/> 參與撞鍵判定。</b></param>
/// <param name="EnableGate">這條路徑的布林值為 <c>false</c> 時整列不算數（例如 GBR 的「只在按著時顯示」總開關）。</param>
/// <param name="InMemory">本外掛自己的取值來源（<c>null</c>＝從設定檔讀）。</param>
/// <param name="Note">補充說明，<b>只放 tooltip</b>。列上只放得下「誰跟誰撞」，細節留給滑鼠移上去看。</param>
public sealed record HotkeyBindingSpec(
    string PluginLabel,
    string ConfigFileName,
    string[] JsonPath,
    string Purpose,
    HotkeyTrigger Trigger,
    string[]? EnableGate = null,
    Func<int?>? InMemory = null,
    string Note = "")
{
    /// <summary>資料表裡的唯一識別（拿來查「已知相容」名單）。</summary>
    public string Id => $"{PluginLabel}/{JsonPath[^1]}";
}

/// <summary>讀到的一筆綁定：規格加上解析出來的鍵名。</summary>
public sealed record HotkeyBindingRead(HotkeyBindingSpec Spec, string KeyName);

/// <summary>同一顆鍵上有兩個以上「按著就生效」的綁定。</summary>
/// <param name="KeyName">正規化後的鍵名（例如 <c>Alt</c>）。</param>
/// <param name="Colliding">真的撞在一起的那幾筆（都是按著就生效）。</param>
/// <param name="AlsoOnSameKey">同一顆鍵上但還要配合別的動作才生效的那幾筆（只放 tooltip）。</param>
public sealed record HotkeyConflict(
    string KeyName,
    IReadOnlyList<HotkeyBindingRead> Colliding,
    IReadOnlyList<HotkeyBindingRead> AlsoOnSameKey);

/// <summary>
/// 跨外掛熱鍵撞鍵偵測：讀別的外掛的設定檔，找出同一顆鍵被兩個以上功能用掉的情形。
/// </summary>
/// <remarks>
/// 🔴🔴 <b>唯讀。</b>遊戲跑著的時候外掛在記憶體裡持有自己的設定、關閉時整份寫回，
/// 從外面寫 <c>pluginConfigs</c> 會在關遊戲那一刻靜默消失。這裡只開 <see cref="FileAccess.Read"/>，
/// 而且<b>不自動改任何綁定</b>——撞鍵只提示，留哪一邊是使用者的決定。
/// 🔴 <see cref="Conflicts"/> 是在 ImGui 的 Draw 路徑上被讀的，所以那裡<b>只讀一個已經算好的快照</b>，
/// 不做 I/O、不擲例外；真正的 I/O 只發生在 <see cref="Refresh"/>（啟動時與每次登入各一次）。
/// </remarks>
public static class HotkeyConflictProbe
{
    /// <summary>
    /// 要檢查的綁定。<b>新增一列就等於支援一個新外掛</b>，不必動下面的邏輯。
    /// </summary>
    /// <remarks>
    /// 🔴 每一列的欄位名與路徑都必須對著<b>真的設定檔</b>核過。鍵名打錯的失敗形式是
    /// 「那一列靜默消失」，與「那個外掛沒裝」長得一模一樣。
    /// 📌 值的編碼有兩種，解析時看 JSON 型別自動分辨：數字＝Dalamud <c>VirtualKey</c>（0＝沒綁），
    /// 字串＝對方自己的列舉名（BMR 的 <c>None/Ctrl/Alt/Shift/M12</c>）。
    /// 📌 用途查不到的欄位收得進來，但一律標成 <see cref="HotkeyTrigger.Unknown"/>：
    /// 猜一個用途寫上去比漏掉更糟，而「不知道」本身是有用的資訊。
    /// </remarks>
    public static readonly HotkeyBindingSpec[] Table =
    [
        new("BMR", "BossModReborn.json",
            ["Payload", "BossMod.ActionTweaksConfig", "PauseAutoMoveKey"],
            "暫停自動移動", Trigger: HotkeyTrigger.HoldAlone,
            Note: "按著時 BMR 完全不注入移動輸入，操作權整個交回使用者。"),

        new("BMR", "BossModReborn.json",
            ["Payload", "BossMod.ActionTweaksConfig", "MoveEscapeHatch"],
            "詠唱中仍可自己移動", Trigger: HotkeyTrigger.HoldAlone),

        new("BMR", "BossModReborn.json",
            ["Payload", "BossMod.Global.DeepDungeon.AutoDDConfig", "ForceTravelKey"],
            "深層迷宮強制趕路", Trigger: HotkeyTrigger.HoldAlone),

        new("YesAlready", "YesAlready.json",
            ["ForcedYesKey"],
            "強制推進對話", Trigger: HotkeyTrigger.HoldAlone,
            EnableGate: ["Enabled"],
            Note: "按著時 YesAlready 會繞過它自己的所有規則（含區域限制）直接推進當下的對話框。"),

        new("YesAlready", "YesAlready.json",
            ["ForcedTalkKey"],
            "強制推進 Talk 視窗", Trigger: HotkeyTrigger.HoldAlone,
            EnableGate: ["SeparateForcedKeys"]),

        new("YesAlready", "YesAlready.json",
            ["DisableKey"],
            "暫時停用 YesAlready", Trigger: HotkeyTrigger.HoldAlone,
            EnableGate: ["Enabled"]),

        new("GBR", "GatherbuddyReborn.json",
            ["GatherWindowHoldKey"],
            "按著才顯示採集視窗", Trigger: HotkeyTrigger.HoldAlone,
            EnableGate: ["OnlyShowGatherWindowHoldingKey"]),

        new("Marketbuddy", "Marketbuddy.json",
            ["QuickListKeyCode"],
            "背包右鍵選單快速上架", Trigger: HotkeyTrigger.WithAction),

        new("DailyRoutines", "DailyRoutines.json",
            ["ConflictKey"],
            "打斷模組自動操作", Trigger: HotkeyTrigger.HoldAlone,
            Note: "DailyRoutines 設定裡叫「打斷快速鍵」。按著時，有讀這顆鍵的模組會中斷正在進行的自動操作；"
                  + "少數模組把它當成「按著再點」的修飾鍵。實際影響看你在 DailyRoutines 開了哪些模組。"),

        new("Deliveroo", "Deliveroo.json",
            ["QuickTurnInKey"],
            "籌備任務點道具直接交納", Trigger: HotkeyTrigger.WithAction),

        new("TC Toolbox", string.Empty,
            ["ClickToMove", "ModifierKeyCode"],
            "點擊移動", Trigger: HotkeyTrigger.WithAction,
            InMemory: () => Plugin.Instance?.Config.ClickToMove.ModifierKeyCode),

        new("TC Toolbox", string.Empty,
            ["InventoryTransfer", "ModifierKeyCode"],
            "背包快速搬運", Trigger: HotkeyTrigger.WithAction,
            InMemory: () => Plugin.Instance?.Config.InventoryTransfer.ModifierKeyCode),

        new("TC Toolbox", string.Empty,
            ["QuickTrade", "ModifierKeyCode"],
            "快速交易", Trigger: HotkeyTrigger.WithAction,
            InMemory: () => Plugin.Instance?.Config.QuickTrade.ModifierKeyCode),
    ];

    /// <summary>
    /// 同一顆鍵上刻意相容、不該報警的組合（以 <see cref="HotkeyBindingSpec.Id"/> 成對列出）。
    /// </summary>
    /// <remarks>
    /// BMR 自己的設定說明逐字寫著這兩格都選 Alt「也不衝突」：一個解除的是「BMR 擋住你自己的移動」，
    /// 另一個停的是「BMR 自己在走」，兩格都設 Alt 正是它建議的組合。
    /// </remarks>
    private static readonly (string A, string B)[] KnownCompatible =
    [
        ("BMR/MoveEscapeHatch", "BMR/PauseAutoMoveKey"),
    ];

    /// <summary>撞鍵結果快照。Draw 路徑只讀這個欄位，永遠不為 <c>null</c>。</summary>
    /// <remarks>整份取代、不就地改，所以讀取端不必加鎖。</remarks>
    private static volatile IReadOnlyList<HotkeyConflict> conflicts = [];

    /// <summary>上一次掃描的摘要（給 tooltip 用：讀到幾條、略過幾條）。</summary>
    private static volatile string scanSummary = string.Empty;

    private static volatile int bindingsRead;

    /// <summary>掃描至少成功跑完一次。</summary>
    /// <remarks>沒有這個旗標的話，「已檢查 0 條」會在還沒掃之前就先說謊一次。</remarks>
    private static volatile bool hasScanned;

    /// <summary>1＝正在掃描。同時間只跑一次，第二個呼叫直接放棄。</summary>
    private static int refreshing;

    /// <summary>已經寫過記錄的結果指紋，內容沒變就不重複寫。</summary>
    private static volatile string loggedSignature = string.Empty;

    private static bool watching;

    /// <summary>目前偵測到的撞鍵（空清單＝沒有，或還沒掃過）。</summary>
    public static IReadOnlyList<HotkeyConflict> Conflicts => conflicts;

    public static string ScanSummary => scanSummary;

    /// <summary>上一次掃描讀到的綁定條數（0＝都沒讀到，那些外掛沒裝或都沒綁鍵）。</summary>
    public static int BindingsRead => bindingsRead;

    /// <summary>掃描跑過至少一次；<c>false</c> 時畫面上不該出現任何結論。</summary>
    public static bool HasScanned => hasScanned;

    /// <summary>掛上登入事件並立刻掃一次。重複呼叫是安全的。</summary>
    public static void EnsureWatching()
    {
        if (watching) return;
        watching = true;
        Svc.ClientState.Login += OnLogin;
        Refresh();
    }

    /// <summary>拆掉登入事件（外掛卸載時一定要呼叫）。</summary>
    public static void ForceTeardown()
    {
        if (!watching) return;
        watching = false;
        Svc.ClientState.Login -= OnLogin;
    }

    private static void OnLogin() => Refresh();

    /// <summary>
    /// 重新掃描一次。<b>會做檔案 I/O</b>，只在啟動與登入時呼叫，不要放進每幀路徑。
    /// </summary>
    public static void Refresh()
    {
        if (Interlocked.Exchange(ref refreshing, 1) == 1) return;

        try
        {
            var reads = new List<HotkeyBindingRead>();
            var skipped = 0;
            var cache = new Dictionary<string, JsonDocument?>(StringComparer.OrdinalIgnoreCase);

            try
            {
                foreach (var spec in Table)
                {
                    var key = Resolve(spec, cache);
                    if (key == null) skipped++;
                    else reads.Add(new HotkeyBindingRead(spec, key));
                }
            }
            finally
            {
                foreach (var doc in cache.Values) doc?.Dispose();
            }

            var found = Detect(reads);
            conflicts = found;
            bindingsRead = reads.Count;
            hasScanned = true;
            scanSummary = $"掃描 {Table.Length} 條綁定：讀到 {reads.Count} 條、略過 {skipped} 條（插件沒裝、沒綁鍵，或該功能關著）。";

            Report(found);
        }
        catch (Exception ex)
        {
            // 這條路徑失敗不影響任何功能，只是少一個提示；留一行讓「怎麼都不顯示」查得到原因。
            Svc.Log.Information($"[HotkeyConflict] 掃描失敗，本次不提示：{ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref refreshing, 0);
        }
    }

    /// <summary>取出一列的鍵名；<c>null</c>＝這一列不算數（檔不在、鍵不在、沒綁、功能關著）。</summary>
    private static string? Resolve(HotkeyBindingSpec spec, Dictionary<string, JsonDocument?> cache)
    {
        if (spec.InMemory != null)
        {
            var code = spec.InMemory();
            return code == null ? null : CanonicalFromVirtualKey(code.Value);
        }

        var doc = Load(spec.ConfigFileName, cache);
        if (doc == null) return null;

        if (spec.EnableGate != null)
        {
            // 🔴 閘門鍵不存在時當成「關著」：GBR 實測 OnlyShowGatherWindowHoldingKey=false，
            //    不看閘門就會把一顆根本不會生效的 Alt 報成撞鍵。
            if (!TryFind(doc.RootElement, spec.EnableGate, out var gate)) return null;
            if (gate.ValueKind != JsonValueKind.True) return null;
        }

        if (!TryFind(doc.RootElement, spec.JsonPath, out var value)) return null;

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetInt32(out var n) ? CanonicalFromVirtualKey(n) : null,
            JsonValueKind.String => CanonicalFromName(value.GetString()),
            _ => null,
        };
    }

    private static JsonDocument? Load(string fileName, Dictionary<string, JsonDocument?> cache)
    {
        if (cache.TryGetValue(fileName, out var cached)) return cached;

        JsonDocument? doc = null;
        try
        {
            // 本外掛自己的設定檔就躺在 pluginConfigs 底下，同一層即是所有外掛的設定檔。
            var dir = Svc.PluginInterface.ConfigFile.Directory;
            if (dir != null)
            {
                var path = Path.Combine(dir.FullName, fileName);
                if (File.Exists(path))
                {
                    // 🔴 對方可能正在寫這個檔，FileShare 一定要放到 ReadWrite；
                    //    而且只開 Read——寫進去的東西會在對方關閉時被整份覆蓋掉。
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var buffer = new MemoryStream();
                    stream.CopyTo(buffer);
                    doc = JsonDocument.Parse(StripBom(buffer.ToArray()));
                }
            }
        }
        catch (Exception ex)
        {
            // 讀不到就當成「沒裝」，不打擾使用者。
            Svc.Log.Debug($"[HotkeyConflict] 讀 {fileName} 失敗：{ex.Message}");
        }

        cache[fileName] = doc;
        return doc;
    }

    private static byte[] StripBom(byte[] bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF
            ? bytes[3..]
            : bytes;

    private static bool TryFind(JsonElement root, string[] path, out JsonElement found)
    {
        found = root;
        foreach (var segment in path)
        {
            if (found.ValueKind != JsonValueKind.Object) return false;
            if (!found.TryGetProperty(segment, out found)) return false;
        }

        return true;
    }

    /// <summary>
    /// Dalamud <c>VirtualKey</c> 數值轉成正規化鍵名；<c>null</c>＝沒綁。
    /// </summary>
    /// <remarks>
    /// 左右版本（LSHIFT/RSHIFT 之類）要和不分邊的那顆併成同一個名字，否則
    /// 「一邊寫 18、一邊寫 164」會被判成兩顆不同的鍵而靜默漏報。
    /// </remarks>
    private static string? CanonicalFromVirtualKey(int code) => code switch
    {
        0 => null,
        16 or 160 or 161 => "Shift",
        17 or 162 or 163 => "Ctrl",
        18 or 164 or 165 => "Alt",
        _ => HotkeyUi.Describe((VirtualKey)code),
    };

    /// <summary>對方列舉名轉成正規化鍵名；<c>null</c>＝沒綁。</summary>
    private static string? CanonicalFromName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        return name.Trim().ToLowerInvariant() switch
        {
            "none" or "no_key" => null,
            "alt" or "menu" or "lmenu" or "rmenu" => "Alt",
            "ctrl" or "control" or "lcontrol" or "rcontrol" => "Ctrl",
            "shift" or "lshift" or "rshift" => "Shift",
            "m12" => "滑鼠左＋右鍵",
            _ => name.Trim(),
        };
    }

    private static IReadOnlyList<HotkeyConflict> Detect(List<HotkeyBindingRead> reads)
    {
        var byKey = new Dictionary<string, List<HotkeyBindingRead>>(StringComparer.Ordinal);
        foreach (var read in reads)
        {
            if (!byKey.TryGetValue(read.KeyName, out var list))
                byKey[read.KeyName] = list = [];
            list.Add(read);
        }

        var result = new List<HotkeyConflict>();
        foreach (var (keyName, list) in byKey)
        {
            var holdAlone = new List<HotkeyBindingRead>();
            var others = new List<HotkeyBindingRead>();
            foreach (var read in list)
                (read.Spec.Trigger == HotkeyTrigger.HoldAlone ? holdAlone : others).Add(read);

            if (holdAlone.Count < 2) continue;

            // 判準是「有沒有任何一對不在已知相容名單裡」——三筆裡有一對相容不代表沒事。
            var colliding = new List<HotkeyBindingRead>();
            for (var i = 0; i < holdAlone.Count; i++)
            {
                for (var j = i + 1; j < holdAlone.Count; j++)
                {
                    if (IsKnownCompatible(holdAlone[i].Spec.Id, holdAlone[j].Spec.Id)) continue;
                    if (!colliding.Contains(holdAlone[i])) colliding.Add(holdAlone[i]);
                    if (!colliding.Contains(holdAlone[j])) colliding.Add(holdAlone[j]);
                }
            }

            if (colliding.Count < 2) continue;

            LeadWithCrossPlugin(colliding);
            result.Add(new HotkeyConflict(keyName, colliding, others));
        }

        return result;
    }

    /// <summary>
    /// 把「兩個<b>不同外掛</b>」的那一對挪到最前面。
    /// </summary>
    /// <remarks>
    /// 列上只放得下兩個名字，而跨外掛那一對才是使用者自己看不出來的
    /// （同一個外掛的兩顆鍵在它自己的設定畫面上就並排著）。找不到跨外掛的對就維持原順序。
    /// </remarks>
    private static void LeadWithCrossPlugin(List<HotkeyBindingRead> colliding)
    {
        for (var i = 0; i < colliding.Count; i++)
        {
            for (var j = i + 1; j < colliding.Count; j++)
            {
                if (string.Equals(colliding[i].Spec.PluginLabel, colliding[j].Spec.PluginLabel, StringComparison.Ordinal))
                    continue;

                (colliding[0], colliding[i]) = (colliding[i], colliding[0]);
                // 🔴 上面那一手可能把 j 位置的那筆換走了（i==0 時不會，但 j==0 不可能）——
                //    所以第二次交換要重新找，不能直接用 j。
                var partner = colliding.FindIndex(1, x => !string.Equals(x.Spec.PluginLabel, colliding[0].Spec.PluginLabel, StringComparison.Ordinal));
                if (partner > 1) (colliding[1], colliding[partner]) = (colliding[partner], colliding[1]);
                return;
            }
        }
    }

    private static bool IsKnownCompatible(string a, string b)
    {
        foreach (var (x, y) in KnownCompatible)
        {
            if ((x == a && y == b) || (x == b && y == a)) return true;
        }

        return false;
    }

    /// <summary>列上看得見的那一句。</summary>
    public static string DescribeShort(HotkeyConflict conflict)
    {
        var first = conflict.Colliding[0].PluginLabelWithPurpose();
        var second = conflict.Colliding[1].PluginLabelWithPurpose();

        return conflict.Colliding.Count > 2
            ? $"熱鍵衝突：{first}、{second} 等 {conflict.Colliding.Count} 項都綁 {conflict.KeyName}"
            : $"熱鍵衝突：{first} 與 {second} 都綁 {conflict.KeyName}";
    }

    /// <summary>tooltip 裡的完整說明。</summary>
    public static string DescribeLong(HotkeyConflict conflict)
    {
        var sb = new StringBuilder();
        sb.Append("同一顆 ").Append(conflict.KeyName).AppendLine(" 上「按著就生效」的功能：");

        foreach (var read in conflict.Colliding)
        {
            sb.Append("　· ").Append(read.PluginLabelWithPurpose());
            sb.Append("　（").Append(DescribeSource(read.Spec)).AppendLine("）");
            if (!string.IsNullOrEmpty(read.Spec.Note))
                sb.Append("　　").AppendLine(read.Spec.Note);
        }

        AppendGroup(sb, conflict.AlsoOnSameKey, HotkeyTrigger.WithAction,
            "同一顆鍵上還有這些，但它們要配合點擊或別的鍵才生效：");
        AppendGroup(sb, conflict.AlsoOnSameKey, HotkeyTrigger.Unknown,
            "同一顆鍵上還有這些，但它們的用途還沒查證，所以不列入判定：");

        sb.AppendLine();
        sb.AppendLine("按著這顆鍵時，上面每一項都會同時發生。要分開就到各自的插件設定裡改一邊。");
        sb.AppendLine("🔴 TC Toolbox 只讀別的插件的設定檔，不會自動改動任何綁定。");

        if (!string.IsNullOrEmpty(scanSummary))
            sb.Append(scanSummary);

        return sb.ToString();
    }

    /// <summary>把同一種 <see cref="HotkeyTrigger"/> 的條目列成一段；一條都沒有就連標題都不印。</summary>
    private static void AppendGroup(
        StringBuilder sb, IReadOnlyList<HotkeyBindingRead> reads, HotkeyTrigger trigger, string title)
    {
        var any = false;
        foreach (var read in reads)
        {
            if (read.Spec.Trigger != trigger) continue;

            if (!any)
            {
                sb.AppendLine();
                sb.AppendLine(title);
                any = true;
            }

            sb.Append("　· ").Append(read.PluginLabelWithPurpose());
            sb.Append("　（").Append(DescribeSource(read.Spec)).AppendLine("）");
        }
    }

    private static string DescribeSource(HotkeyBindingSpec spec) =>
        string.IsNullOrEmpty(spec.ConfigFileName)
            ? $"本插件設定：{string.Join(" · ", spec.JsonPath)}"
            : $"{spec.ConfigFileName}：{spec.JsonPath[^1]}";

    private static void Report(IReadOnlyList<HotkeyConflict> found)
    {
        if (found.Count == 0)
        {
            loggedSignature = string.Empty;
            return;
        }

        var sb = new StringBuilder();
        foreach (var conflict in found)
        {
            sb.Append(conflict.KeyName).Append('=');
            foreach (var read in conflict.Colliding) sb.Append(read.Spec.Id).Append(',');
            sb.Append(';');
        }

        var signature = sb.ToString();
        if (string.Equals(signature, loggedSignature, StringComparison.Ordinal)) return;
        loggedSignature = signature;

        // Information 級：事後回頭確認「偵測到底有沒有跑」的錨。
        Svc.Log.Information($"[HotkeyConflict] {scanSummary} 發現 {found.Count} 組撞鍵。");
        foreach (var conflict in found)
        {
            var names = new List<string>();
            foreach (var read in conflict.Colliding) names.Add(read.PluginLabelWithPurpose());
            Svc.Log.Information(
                $"[HotkeyConflict] {conflict.KeyName}：{string.Join("、", names)} —— 按著這顆鍵時它們會同時發生。");
        }
    }
}

/// <summary>顯示用的小工具。</summary>
public static class HotkeyBindingReadExtensions
{
    /// <summary>「BMR 暫停自動移動」這種寫法。</summary>
    public static string PluginLabelWithPurpose(this HotkeyBindingRead read) =>
        $"{read.Spec.PluginLabel} {read.Spec.Purpose}";
}

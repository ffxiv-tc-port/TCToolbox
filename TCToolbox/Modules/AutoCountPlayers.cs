using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 周邊玩家數量統計：伺服器資訊列（DTR）顯示人數，點擊開啟清單懸浮窗。
/// 另含偵測規則：玩家出現且<b>條件命中</b>時通知並執行斜線指令。
/// 機制：Framework 輪詢 ObjectTable 枚舉，零 hook（不抄 DR 的 InfoProxy hook 部分）。
/// </summary>
/// <remarks>
/// 偵測條件是多選可組合的（見 <see cref="PlayerWatchRule"/>）：名稱、線上狀態、部隊標籤、距離。
/// 全部欄位都在同一次輪詢裡從當幀的 <see cref="IPlayerCharacter"/> 取出來就丟掉，
/// <b>不跨幀保存任何原生物件</b>——存的是 EntityId，要用時再 <c>SearchByEntityId</c> 重查。
/// </remarks>
public sealed class AutoCountPlayers : TcModule
{
    public override string InternalName => "AutoCountPlayers";
    public override string DisplayName => "周邊玩家統計";
    public override string Description => "在伺服器資訊列顯示周邊玩家數量，滑鼠移上顯示名單，點擊開啟可搜尋的清單視窗（點名單可選取目標）。可設定偵測規則：玩家出現且條件命中（名稱／線上狀態／部隊標籤／距離，可多選組合）時通知並執行指令。";

    public override ModuleCategory Category => ModuleCategory.Misc;

    public override bool HasConfigUI => true;

    /// <summary>
    /// 單次輪詢取下來的玩家快照。
    /// <para><c>Job</c> 是職業全名（台服 ClassJob 表自帶繁中），不是縮寫。</para>
    /// <para><c>OnlineStatusId</c> 是 <c>OnlineStatus</c> 的列號（0＝無狀態）。</para>
    /// </summary>
    private sealed record PlayerInfo(
        uint EntityId, string Name, string World, uint JobIconId, string Job, float Distance,
        uint OnlineStatusId, string CompanyTag);

    private readonly List<PlayerInfo> players = [];
    private IDtrBarEntry? dtrEntry;

    /// <summary>
    /// 職業圖示 ID ＝ <c>62100 + ClassJob 列號</c>。
    /// <para>
    /// 依據：①艦隊裡四處已出貨的先例一致（ECommons <c>ExcelJobHelper.GetIcon</c>、
    /// AutoRetainer <c>RetainerTable</c>、WrathCombo <c>Icons.GetJobIcon</c>、Splatoon）
    /// ②2026-08-06 離線直讀台服 <c>060000.win32.index</c> 求證：
    /// <c>ui/icon/062000/062100.tex</c>～<c>062146.tex</c> 全部存在，062099 與 062147 都不存在——
    /// 區塊邊界剛好對齊台服 ClassJob 表的 46 列（0～45），所以本式對整張表都落在有效範圍內。
    /// </para>
    /// <para>⚠️ ClassJob 表**沒有** Icon 欄，這個對應關係只能靠慣例，不是資料驅動的。</para>
    /// </summary>
    private const uint JobIconBase = 62100;

    /// <summary>圖示區塊的最後一列（62146 是區塊最後一張圖）。</summary>
    private const uint JobIconMaxRow = 46;

    /// <summary>DTR 提示裡最多預覽幾個玩家（超過的以「…等共 N 人」帶過）。</summary>
    private const int DtrPreviewCount = 5;

    /// <summary>資訊列圖示：一群人＝周邊玩家。</summary>
    private const BitmapFontIcon DtrIcon = BitmapFontIcon.GroupFinder;

    /// <summary>
    /// 「遊戲管理員」的 <c>OnlineStatus</c> 列號。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>兩個都要收</b>：離線比對台服 <c>exd-tc/7.20/OnlineStatus.csv</c>（全表 48 列，0–47），
    /// 列 2 與列 3 的 <c>Name</c> 都是「遊戲管理員」，差別只在圖示（61524／61532）與 <c>List</c> 旗標
    /// （列 2 為 True、列 3 為 False）。為什麼分成兩列沒有公開資料，所以不猜、兩個都認。
    /// <para>
    /// ⚠️ 這裡寫死列號是刻意的：UI 上會把列號旁邊的<b>遊戲自己的名稱</b>一起印出來，
    /// 台服哪天重新編號的話使用者會直接看到名稱對不上，不是靜默失效。
    /// </para>
    /// </remarks>
    private static readonly uint[] GameMasterStatusIds = [2u, 3u];

    private bool windowOpen;
    private string search = string.Empty;

    // 偵測規則的執行期狀態（不進存檔）
    /// <summary>
    /// 正規表達式快取，<b>以樣式字串為鍵</b>（不是以規則為鍵）——同一條規則現在有名稱與部隊標籤
    /// 兩個獨立樣式，用規則當鍵會互相蓋掉。
    /// </summary>
    private readonly Dictionary<string, (Regex? Regex, string? Error)> regexCache = [];
    private readonly Dictionary<(PlayerWatchRule Rule, string Player), long> lastTrigger = [];
    private readonly HashSet<(PlayerWatchRule Rule, string Player)> activeTriggered = [];
    private readonly Dictionary<PlayerWatchRule, long> patternChangedAt = [];
    private readonly HashSet<uint> matchedEntityIds = [];
    private readonly Queue<List<string>> pendingCommands = new();

    /// <summary>條件命中集合的暫存容器（每次輪詢重用，避免每 500ms 配置新集合）。</summary>
    private readonly HashSet<(PlayerWatchRule Rule, string Player)> stillMatching = [];
    private readonly HashSet<PlayerWatchRule> evaluatedRules = [];

    /// <summary>
    /// <c>OnlineStatus</c> 的（列號, 名稱）清單，第一次用到才建。
    /// 名稱一律取自遊戲自己的表，不自建對照表。
    /// </summary>
    private static IReadOnlyList<(uint RowId, string Name)>? onlineStatusOptions;

    private static Dictionary<uint, string>? onlineStatusNames;

    private AutoCountPlayersConfig Config => Plugin.Instance.Config.CountPlayers;

    protected override void OnEnable()
    {
        dtrEntry = Svc.DtrBar.Get("TC Toolbox 周邊玩家");
        dtrEntry.Shown = true;
        dtrEntry.Text = new SeString(new IconPayload(DtrIcon), new TextPayload("0"));
        dtrEntry.Tooltip = "TC Toolbox — 周邊玩家\n左鍵：開啟周邊玩家清單\n右鍵：開啟 TC Toolbox 設定";
        // 左鍵＝開關名單視窗；右鍵＝開 TC Toolbox 主視窗。
        dtrEntry.OnClick = ev =>
        {
            if (ev.ClickType == MouseClickType.Right)
                Plugin.Instance.ToggleMainWindow();
            else
                windowOpen = !windowOpen;
        };

        Svc.Framework.Update += OnUpdate;
        Svc.PluginInterface.UiBuilder.Draw += DrawWindow;
    }

    protected override void OnDisable()
    {
        Svc.Framework.Update -= OnUpdate;
        Svc.PluginInterface.UiBuilder.Draw -= DrawWindow;

        dtrEntry?.Remove();
        dtrEntry = null;
        players.Clear();
        windowOpen = false;

        regexCache.Clear();
        lastTrigger.Clear();
        activeTriggered.Clear();
        patternChangedAt.Clear();
        matchedEntityIds.Clear();
        pendingCommands.Clear();
    }

    private void OnUpdate(IFramework framework)
    {
        if (dtrEntry == null) return;
        if (!Throttle.Pass("AutoCountPlayers-Poll", 500)) return;

        var localPlayer = Svc.Objects.LocalPlayer;
        if (localPlayer == null)
        {
            players.Clear();
            activeTriggered.Clear();
            matchedEntityIds.Clear();
            pendingCommands.Clear();
            dtrEntry.Shown = false;
            return;
        }

        players.Clear();
        foreach (var obj in Svc.Objects)
        {
            if (obj is not IPlayerCharacter pc) continue;
            if (pc.EntityId == localPlayer.EntityId) continue;

            var classJob = pc.ClassJob.ValueNullable;
            // 全名取自 ClassJob.Name（台服 Lumina 讀出來就是繁中），不自建對照表。
            var jobName = classJob?.Name.ExtractText() ?? string.Empty;
            var jobRow = classJob?.RowId ?? 0;

            players.Add(new PlayerInfo(
                pc.EntityId,
                pc.Name.TextValue,
                pc.HomeWorld.ValueNullable?.Name.ExtractText() ?? string.Empty,
                jobRow is >= 1 and <= JobIconMaxRow ? JobIconBase + jobRow : 0,
                // ClassJob 讀不到（或台服該列沒有名稱，例如列 0）才顯示「?」
                string.IsNullOrEmpty(jobName) ? "?" : jobName,
                Vector3.Distance(localPlayer.Position, pc.Position),
                // 只取列號，不在這裡查表——查表要用到 Lumina，留給 UI 端做就好。
                pc.OnlineStatus.RowId,
                pc.CompanyTag.TextValue));
        }

        players.Sort((a, b) => a.Distance.CompareTo(b.Distance));

        EvaluateWatchRules();
        PumpPendingCommands();

        // 戰鬥中只隱藏資訊列顯示，輪詢與偵測照常運作
        if (Config.HideInCombat && Svc.Condition[ConditionFlag.InCombat])
        {
            dtrEntry.Shown = false;
            return;
        }

        dtrEntry.Shown = true;
        // DTR 空間很擠：用圖示取代「周邊玩家: 」這個每次都一樣的前綴，只留數字。
        dtrEntry.Text = new SeString(new IconPayload(DtrIcon), new TextPayload(players.Count.ToString()));

        if (players.Count == 0)
        {
            dtrEntry.Tooltip = "TC Toolbox — 周邊玩家\n附近沒有其他玩家\n\n左鍵：開啟清單　右鍵：開啟設定";
        }
        else
        {
            var sb = new StringBuilder();
            foreach (var p in players.Take(DtrPreviewCount))
                sb.AppendLine($"[{p.Job}] {p.Name}{(string.IsNullOrEmpty(p.World) ? "" : $" @ {p.World}")}");
            if (players.Count > DtrPreviewCount)
                sb.AppendLine($"…等共 {players.Count} 人");
            sb.Append("左鍵：開啟周邊玩家清單\n右鍵：開啟 TC Toolbox 設定");
            dtrEntry.Tooltip = sb.ToString();
        }
    }

    #region 偵測規則

    /// <summary>
    /// 觸發語意：命中的玩家在場且尚未處理過→觸發；持續在場不重複觸發；
    /// 離場後再出現且超過冷卻→再次觸發。新增或修改規則對已在場的命中者立即生效。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 「已處理過」的記憶（<see cref="activeTriggered"/>）在兩種情況下解除：
    /// ①玩家離開視野 ②<b>玩家還在場，但這條規則已經不再命中他</b>。
    /// 第二種是多條件化才需要的——線上狀態與距離會在玩家不離場的情況下變動，
    /// 只看「有沒有離場」會讓狀態變回來時永遠不再響。
    /// 名稱條件不受影響（在場玩家的名稱不會變），所以既有的純名稱規則行為逐字相同。
    /// </para>
    /// </remarks>
    private void EvaluateWatchRules()
    {
        matchedEntityIds.Clear();

        var rules = Config.WatchRules;
        if (rules.Count == 0)
        {
            activeTriggered.Clear();
            return;
        }

        var now = Environment.TickCount64;
        var present = new HashSet<string>();

        stillMatching.Clear();
        evaluatedRules.Clear();
        foreach (var rule in rules)
        {
            if (rule.Enabled && HasActiveCondition(rule))
                evaluatedRules.Add(rule);
        }

        foreach (var p in players)
        {
            var key = $"{p.Name}@{p.World}";
            present.Add(key);

            foreach (var rule in rules)
            {
                if (!evaluatedRules.Contains(rule)) continue;
                if (!Matches(rule, p)) continue;

                matchedEntityIds.Add(p.EntityId);

                var triggerKey = (rule, key);
                stillMatching.Add(triggerKey);

                // 樣式剛在設定視窗改過（可能還在輸入中），沉澱一下再武裝
                if (patternChangedAt.TryGetValue(rule, out var changed) && now - changed < 1500) continue;

                if (!activeTriggered.Add(triggerKey)) continue;

                // 冷卻中：只標記在場，不執行（避免冷卻結束時對持續在場者補觸發）
                if (lastTrigger.TryGetValue(triggerKey, out var last) &&
                    now - last < rule.CooldownSeconds * 1000L)
                    continue;

                lastTrigger[triggerKey] = now;
                Trigger(rule, p);
            }
        }

        activeTriggered.RemoveWhere(tk =>
            !present.Contains(tk.Player) ||
            (evaluatedRules.Contains(tk.Rule) && !stillMatching.Contains(tk)));

        // 定期清掉過期的觸發紀錄與已刪除規則的殘留
        if (Throttle.Pass("AutoCountPlayers-PruneTriggers", 60_000))
        {
            foreach (var entry in lastTrigger.Where(kv =>
                         !rules.Contains(kv.Key.Rule) ||
                         now - kv.Value > Math.Max(3_600_000, kv.Key.Rule.CooldownSeconds * 2000L)).ToArray())
                lastTrigger.Remove(entry.Key);
            foreach (var rule in patternChangedAt.Keys.Where(r => !rules.Contains(r)).ToArray())
                patternChangedAt.Remove(rule);

            // 快取的鍵是樣式字串，只會隨著使用者打字累積；上限純粹是止血。
            if (regexCache.Count > 128) regexCache.Clear();
        }
    }

    /// <summary>
    /// 這條規則至少有一個條件「真的能判斷東西」。
    /// </summary>
    /// <remarks>
    /// 🔴 全部條件都沒啟用（或啟用了但內容是空的）＝<b>不觸發</b>，不是「命中所有人」。
    /// 多條件化之前的等價判斷是「樣式為空就跳過」，語意一致。
    /// </remarks>
    private static bool HasActiveCondition(PlayerWatchRule rule) =>
        (rule.MatchName && !string.IsNullOrWhiteSpace(rule.Pattern)) ||
        (rule.MatchOnlineStatus && rule.OnlineStatuses.Count > 0) ||
        (rule.MatchCompanyTag && !string.IsNullOrWhiteSpace(rule.CompanyTagPattern)) ||
        rule.MatchMaxDistance;

    /// <summary>
    /// 逐條件求值後依 <see cref="PlayerWatchRule.MatchMode"/> 合併。
    /// 沒有任何啟用中的條件時一律回 <c>false</c>。
    /// </summary>
    private bool Matches(PlayerWatchRule rule, PlayerInfo p)
    {
        var active = 0;
        var anyHit = false;
        var allHit = true;

        if (rule.MatchName && !string.IsNullOrWhiteSpace(rule.Pattern))
        {
            var hit = MatchesText(rule.Pattern, rule.UseRegex,
                                  rule.MatchWithWorld ? $"{p.Name}@{p.World}" : p.Name);
            active++;
            anyHit |= hit;
            allHit &= hit;
        }

        if (rule.MatchOnlineStatus && rule.OnlineStatuses.Count > 0)
        {
            var hit = rule.OnlineStatuses.Contains(p.OnlineStatusId);
            active++;
            anyHit |= hit;
            allHit &= hit;
        }

        if (rule.MatchCompanyTag && !string.IsNullOrWhiteSpace(rule.CompanyTagPattern))
        {
            var hit = MatchesText(rule.CompanyTagPattern, rule.CompanyTagUseRegex, p.CompanyTag);
            active++;
            anyHit |= hit;
            allHit &= hit;
        }

        if (rule.MatchMaxDistance)
        {
            var hit = p.Distance <= rule.MaxDistance;
            active++;
            anyHit |= hit;
            allHit &= hit;
        }

        if (active == 0) return false;
        return rule.MatchMode == WatchRuleMatchMode.All ? allHit : anyHit;
    }

    /// <summary>單一字串條件的比對（正規表達式或完全相符，都不分大小寫）。</summary>
    private bool MatchesText(string pattern, bool useRegex, string target)
    {
        if (!useRegex)
            return string.Equals(target, pattern.Trim(), StringComparison.OrdinalIgnoreCase);

        var regex = GetRegex(pattern).Regex;
        if (regex == null) return false;

        try
        {
            return regex.IsMatch(target);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private (Regex? Regex, string? Error) GetRegex(string pattern)
    {
        if (regexCache.TryGetValue(pattern, out var cached))
            return cached;

        (Regex?, string?) entry;
        try
        {
            entry = (new Regex(pattern,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(100)), null);
        }
        catch (ArgumentException ex)
        {
            entry = (null, ex.Message);
        }

        regexCache[pattern] = entry;
        return entry;
    }

    /// <summary>
    /// 通知訊息裡代表這條規則的字樣。
    /// </summary>
    /// <remarks>
    /// 🔴 只有名稱條件在作用時<b>逐字回傳樣式本身</b>——那是多條件化之前的訊息內容，
    /// 既有規則的聊天欄輸出必須一個字都不變。有其他條件參與時才改印條件摘要。
    /// </remarks>
    private static string DescribeRuleForNotice(PlayerWatchRule rule)
    {
        var onlyName = rule.MatchName && !string.IsNullOrWhiteSpace(rule.Pattern) &&
                       !(rule.MatchOnlineStatus && rule.OnlineStatuses.Count > 0) &&
                       !(rule.MatchCompanyTag && !string.IsNullOrWhiteSpace(rule.CompanyTagPattern)) &&
                       !rule.MatchMaxDistance;

        return onlyName ? rule.Pattern : DescribeConditions(rule);
    }

    /// <summary>把啟用中的條件列成一行人看得懂的摘要。沒有任何條件時明講「無」。</summary>
    private static string DescribeConditions(PlayerWatchRule rule)
    {
        var parts = new List<string>(4);

        if (rule.MatchName && !string.IsNullOrWhiteSpace(rule.Pattern))
            parts.Add($"名稱 {rule.Pattern}");
        if (rule.MatchOnlineStatus && rule.OnlineStatuses.Count > 0)
            parts.Add($"線上狀態 {DescribeOnlineStatuses(rule.OnlineStatuses)}");
        if (rule.MatchCompanyTag && !string.IsNullOrWhiteSpace(rule.CompanyTagPattern))
            parts.Add($"部隊標籤 {rule.CompanyTagPattern}");
        if (rule.MatchMaxDistance)
            parts.Add($"距離 ≤ {rule.MaxDistance:0.#}m");

        if (parts.Count == 0) return "無";

        var joiner = rule.MatchMode == WatchRuleMatchMode.All ? " 且 " : " 或 ";
        return string.Join(joiner, parts);
    }

    private void Trigger(PlayerWatchRule rule, PlayerInfo p)
    {
        if (rule.NotifyChat)
            Svc.Chat.Print($"[TC Toolbox] 偵測到玩家：{p.Name}{(string.IsNullOrEmpty(p.World) ? "" : $" @ {p.World}")}（規則：{DescribeRuleForNotice(rule)}）");

        if (string.IsNullOrWhiteSpace(rule.Command)) return;

        var lines = rule.Command.Split('\n')
            .Select(line => ApplyPlaceholders(line.Trim(), p))
            .Where(line => line.Length > 0)
            .ToList();
        if (lines.Count > 0)
            pendingCommands.Enqueue(lines);
    }

    private static string ApplyPlaceholders(string command, PlayerInfo p) => command
        .Replace("{name}", p.Name, StringComparison.OrdinalIgnoreCase)
        .Replace("{world}", p.World, StringComparison.OrdinalIgnoreCase)
        .Replace("{job}", p.Job, StringComparison.OrdinalIgnoreCase)
        .Replace("{status}", OnlineStatusName(p.OnlineStatusId), StringComparison.OrdinalIgnoreCase)
        .Replace("{fc}", p.CompanyTag, StringComparison.OrdinalIgnoreCase);

    /// <summary>每次輪詢只送一批指令，避免多名玩家同時命中時瞬間灌爆指令處理。</summary>
    private void PumpPendingCommands()
    {
        if (!pendingCommands.TryDequeue(out var lines)) return;
        foreach (var line in lines)
            ChatSender.ExecuteCommand(line);
    }

    #endregion

    #region OnlineStatus 查表

    /// <summary>
    /// <c>OnlineStatus</c> 的（列號, 名稱）清單，第一次用到才建，之後整個 session 重用。
    /// </summary>
    /// <remarks>
    /// ⚠️ 名稱為空的列（台服的列 0、以及未開放的列）直接不列出來——選了也沒有意義。
    /// 讀表整段包在 try 裡：這條路只在設定 UI 上用，<b>絕不能讓 Draw 擲例外</b>
    /// （ImGui Draw 擲一次例外，Dalamud 就把整個 UiBuilder.Draw 拆掉到重開遊戲為止）。
    /// </remarks>
    private static IReadOnlyList<(uint RowId, string Name)> OnlineStatusOptions
    {
        get
        {
            if (onlineStatusOptions != null) return onlineStatusOptions;

            var list = new List<(uint, string)>();
            try
            {
                foreach (var row in Svc.Data.GetExcelSheet<OnlineStatus>())
                {
                    var name = row.Name.ExtractText();
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    list.Add((row.RowId, name.Trim()));
                }
            }
            catch (Exception ex)
            {
                // 讀不到就是空清單：UI 會退成只能看列號，功能本身（比對列號）不受影響。
                Svc.Log.Error(ex, "[AutoCountPlayers] 讀取 OnlineStatus 表失敗，線上狀態條件只會顯示列號。");
            }

            onlineStatusOptions = list;
            onlineStatusNames = list.GroupBy(x => x.Item1)
                                    .ToDictionary(g => g.Key, g => g.First().Item2);
            return onlineStatusOptions;
        }
    }

    /// <summary>列號 → 名稱；查不到就回 <c>#列號</c>（不會回空字串，也不會擲例外）。</summary>
    private static string OnlineStatusName(uint rowId)
    {
        _ = OnlineStatusOptions;
        if (onlineStatusNames != null && onlineStatusNames.TryGetValue(rowId, out var name))
            return name;
        return $"#{rowId}";
    }

    /// <summary>
    /// 一組列號的顯示字樣。
    /// <para>
    /// 🔑 列號一定印出來：台服的列 2 與列 3 名稱一模一樣（都是「遊戲管理員」），
    /// 不帶列號的話使用者根本分不出自己選了哪一個、有沒有選全。
    /// </para>
    /// </summary>
    private static string DescribeOnlineStatuses(IReadOnlyCollection<uint> ids)
    {
        if (ids.Count == 0) return "（未選）";
        return string.Join("、", ids.OrderBy(x => x).Select(id => $"{OnlineStatusName(id)}(#{id})"));
    }

    #endregion

    private void DrawWindow()
    {
        if (!windowOpen) return;

        ImGui.SetNextWindowSize(new Vector2(420, 340), ImGuiCond.FirstUseEver);
        // 標題引用 DisplayName；### 之後的 ID 保持原字面值，視窗位置／大小的存檔才不會被重置。
        if (ImGui.Begin($"{DisplayName} ({players.Count})###TCToolboxCountPlayers", ref windowOpen))
        {
            ImGui.SetNextItemWidth(-1f);
            ImGui.InputTextWithHint("##search", "搜尋玩家名稱…", ref search, 64);

            if (ImGui.BeginTable("##playerTable", 4,
                    ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.ScrollY))
            {
                ImGui.TableSetupScrollFreeze(0, 1);
                // 職業欄只放圖示（行高大小），全名走列的 tooltip；圖示載不到才退回全名文字。
                // 欄寬取「圖示 + 邊距」與欄名寬度的較大者，免得欄名被切掉。
                var jobIconSize = ImGui.GetTextLineHeight();
                ImGui.TableSetupColumn("職業", ImGuiTableColumnFlags.WidthFixed,
                                       Math.Max(jobIconSize + ImGui.GetStyle().CellPadding.X * 2f,
                                                ImGui.CalcTextSize("職業").X));
                ImGui.TableSetupColumn("名稱", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("伺服器", ImGuiTableColumnFlags.WidthFixed, 80f);
                ImGui.TableSetupColumn("距離", ImGuiTableColumnFlags.WidthFixed, 56f);
                ImGui.TableHeadersRow();

                foreach (var p in players.ToArray())
                {
                    if (!string.IsNullOrWhiteSpace(search) &&
                        !p.Name.Contains(search, System.StringComparison.OrdinalIgnoreCase)) continue;

                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    // 🔴 wrap 只在當幀有效，絕不保存跨幀（跨幀共享即時 wrap ＝崩潰）。
                    var jobIcon = GameIcons.TryGet(p.JobIconId);
                    if (jobIcon != null)
                        ImGui.Image(jobIcon.Handle, new Vector2(jobIconSize, jobIconSize));
                    else
                        ImGui.TextUnformatted(p.Job);

                    ImGui.TableNextColumn();
                    var watched = matchedEntityIds.Contains(p.EntityId);
                    if (watched)
                        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.85f, 0.3f, 1f));
                    if (ImGui.Selectable($"{p.Name}##{p.EntityId}", false, ImGuiSelectableFlags.SpanAllColumns))
                    {
                        var obj = Svc.Objects.SearchByEntityId(p.EntityId);
                        if (obj != null)
                            Svc.Targets.Target = obj;
                    }
                    if (watched)
                        ImGui.PopStyleColor();
                    // Selectable 是 SpanAllColumns，所以停在職業圖示上也會走這條——
                    // 職業全名放在第一行，圖示看不懂時滑過去就有答案。
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(p.Job + "\n" +
                                         (watched ? "偵測規則命中｜點擊選取為目標" : "點擊選取為目標"));

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(p.World);

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted($"{p.Distance:0.0}m");
                }

                ImGui.EndTable();
            }
        }

        ImGui.End();
    }

    #region 設定 UI

    private static readonly string[] MatchModeLabels = ["任一條件命中", "所有條件都命中"];

    private static readonly Vector4 ColorError = new(1f, 0.4f, 0.4f, 1f);
    private static readonly Vector4 ColorWarn = new(1f, 0.65f, 0.25f, 1f);
    private static readonly Vector4 ColorInfo = new(0.55f, 0.85f, 1f, 1f);

    /// <summary>樣式／條件剛改過時記一筆時間，讓判定沉澱 1.5 秒再武裝（避免打字打到一半就觸發）。</summary>
    private void MarkRuleChanged(PlayerWatchRule rule) => patternChangedAt[rule] = Environment.TickCount64;

    public override void DrawConfig()
    {
        var hide = Config.HideInCombat;
        if (ImGui.Checkbox("戰鬥中隱藏資訊列項目", ref hide))
        {
            Config.HideInCombat = hide;
            Plugin.Instance.Config.Save();
        }

        ImGui.Separator();
        ImGui.TextUnformatted("玩家偵測規則");
        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "命中的玩家「出現」時觸發（進入視野；離開後再出現需超過冷卻時間才會再次觸發）。\n" +
                "一條規則可以同時設定多個條件（名稱／線上狀態／部隊標籤／距離），\n" +
                "由每條規則自己的「任一／全部」決定怎麼合併——沒勾的條件不參與判定。\n" +
                "指令欄每行一個斜線指令，可呼叫其他外掛（例如 /snd run 巨集名）。\n" +
                "支援佔位符：{name}＝玩家名、{world}＝伺服器、{job}＝職業全名、\n" +
                "　　　　　　{status}＝線上狀態、{fc}＝部隊標籤。\n" +
                "正規表達式不分大小寫、部分符合即命中（要整名相符請用 ^…$）。");

        var rules = Config.WatchRules;
        int? removeIndex = null;

        for (var i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            ImGui.PushID($"watchRule{i}");

            DrawRuleHeader(i, rule, ref removeIndex);

            using (ImRaii.PushIndent())
            {
                DrawNameCondition(rule);
                DrawOnlineStatusCondition(rule);
                DrawCompanyTagCondition(rule);
                DrawDistanceCondition(rule);
            }

            DrawRuleAction(rule);

            ImGui.Separator();
            ImGui.PopID();
        }

        if (removeIndex is { } idx)
        {
            patternChangedAt.Remove(rules[idx]);
            rules.RemoveAt(idx);
            Plugin.Instance.Config.Save();
        }

        if (ImGui.Button("新增規則"))
        {
            rules.Add(new PlayerWatchRule());
            Plugin.Instance.Config.Save();
        }
    }

    /// <summary>
    /// 規則標題列：啟用開關、序號、條件組合方式、刪除，外加<b>畫在列上的條件摘要</b>。
    /// </summary>
    /// <remarks>
    /// 🔑 摘要不放 tooltip：使用者要能一眼看出這條規則「現在靠什麼在判斷」。
    /// 一個條件都沒有時更要看得見——那代表這條規則完全不會觸發。
    /// </remarks>
    private void DrawRuleHeader(int index, PlayerWatchRule rule, ref int? removeIndex)
    {
        var enabled = rule.Enabled;
        if (ImGui.Checkbox("##enabled", ref enabled))
        {
            rule.Enabled = enabled;
            Plugin.Instance.Config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("啟用此規則");

        ImGui.SameLine();
        ImGui.TextUnformatted($"規則 {index + 1}");

        ImGui.SameLine();
        var multi = CountActiveConditions(rule) >= 2;
        if (!multi) ImGui.BeginDisabled();
        ImGui.SetNextItemWidth(150f);
        var modeIndex = (int)rule.MatchMode;
        if (ImGui.Combo("##matchMode", ref modeIndex, MatchModeLabels, MatchModeLabels.Length))
        {
            rule.MatchMode = (WatchRuleMatchMode)modeIndex;
            MarkRuleChanged(rule);
            Plugin.Instance.Config.Save();
        }
        if (!multi) ImGui.EndDisabled();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(multi
                ? "多個條件之間怎麼合併。\n「任一」＝警報語意，寧可多響；「全部」＝用來收斂範圍。"
                : "只有一個條件在作用，兩種合併方式結果相同。");

        ImGui.SameLine();
        if (ImGui.SmallButton("刪除"))
            removeIndex = index;

        using (ImRaii.PushIndent())
        {
            if (!HasActiveCondition(rule))
                ImGui.TextColored(ColorWarn, "條件：無 —— 這條規則不會觸發（下面至少要勾一項並填內容）");
            else
                ImGui.TextColored(ColorInfo, $"條件：{DescribeConditions(rule)}");
        }
    }

    private static int CountActiveConditions(PlayerWatchRule rule)
    {
        var n = 0;
        if (rule.MatchName && !string.IsNullOrWhiteSpace(rule.Pattern)) n++;
        if (rule.MatchOnlineStatus && rule.OnlineStatuses.Count > 0) n++;
        if (rule.MatchCompanyTag && !string.IsNullOrWhiteSpace(rule.CompanyTagPattern)) n++;
        if (rule.MatchMaxDistance) n++;
        return n;
    }

    private void DrawNameCondition(PlayerWatchRule rule)
    {
        var matchName = rule.MatchName;
        if (ImGui.Checkbox("名稱##condName", ref matchName))
        {
            rule.MatchName = matchName;
            MarkRuleChanged(rule);
            Plugin.Instance.Config.Save();
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(180f);
        var pattern = rule.Pattern;
        if (ImGui.InputTextWithHint("##pattern", "名稱樣式…", ref pattern, 128))
        {
            rule.Pattern = pattern;
            MarkRuleChanged(rule);
        }
        if (ImGui.IsItemDeactivatedAfterEdit())
            Plugin.Instance.Config.Save();

        ImGui.SameLine();
        var useRegex = rule.UseRegex;
        if (ImGui.Checkbox("正規表達式##name", ref useRegex))
        {
            rule.UseRegex = useRegex;
            MarkRuleChanged(rule);
            Plugin.Instance.Config.Save();
        }

        ImGui.SameLine();
        var withWorld = rule.MatchWithWorld;
        if (ImGui.Checkbox("含伺服器", ref withWorld))
        {
            rule.MatchWithWorld = withWorld;
            MarkRuleChanged(rule);
            Plugin.Instance.Config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("比對「名稱@伺服器」而非只有名稱");

        if (rule.MatchName && rule.UseRegex && !string.IsNullOrWhiteSpace(rule.Pattern))
        {
            var (_, error) = GetRegex(rule.Pattern);
            if (error != null)
                ImGui.TextColored(ColorError, $"正規表達式無效：{error}");
        }
    }

    /// <summary>
    /// 線上狀態條件。清單與名稱全部取自遊戲的 <c>OnlineStatus</c> 表，不自建對照表。
    /// </summary>
    private void DrawOnlineStatusCondition(PlayerWatchRule rule)
    {
        var matchStatus = rule.MatchOnlineStatus;
        if (ImGui.Checkbox("線上狀態##condStatus", ref matchStatus))
        {
            rule.MatchOnlineStatus = matchStatus;
            MarkRuleChanged(rule);
            Plugin.Instance.Config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("比對玩家頭上／名字旁的線上狀態（遊戲管理員、指導者、角色扮演中…）。\n" +
                             "這是名字比對之外最可靠的一條軸：名字可以取成任何樣子，狀態不行。");

        ImGui.SameLine();
        ImGui.SetNextItemWidth(300f);
        if (ImGui.BeginCombo("##onlineStatuses", DescribeOnlineStatuses(rule.OnlineStatuses)))
        {
            foreach (var (rowId, name) in OnlineStatusOptions)
            {
                var picked = rule.OnlineStatuses.Contains(rowId);
                if (ImGui.Checkbox($"{name}##st{rowId}", ref picked))
                {
                    if (picked) rule.OnlineStatuses.Add(rowId);
                    else rule.OnlineStatuses.Remove(rowId);
                    MarkRuleChanged(rule);
                    Plugin.Instance.Config.Save();
                }
                ImGui.SameLine();
                ImGui.TextDisabled($"#{rowId}");
            }

            if (OnlineStatusOptions.Count == 0)
                ImGui.TextDisabled("讀不到 OnlineStatus 表（詳見記錄）。");

            ImGui.EndCombo();
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("設為遊戲管理員"))
        {
            rule.MatchOnlineStatus = true;
            foreach (var id in GameMasterStatusIds)
                rule.OnlineStatuses.Add(id);
            MarkRuleChanged(rule);
            Plugin.Instance.Config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                $"一鍵勾起線上狀態條件並選入 {DescribeOnlineStatuses(GameMasterStatusIds)}。\n" +
                "台服這兩列的名稱都是「遊戲管理員」，兩個都要收才不會漏。\n" +
                "既有的名稱條件不會被動到——兩個條件會一起生效。");
    }

    private void DrawCompanyTagCondition(PlayerWatchRule rule)
    {
        var matchFc = rule.MatchCompanyTag;
        if (ImGui.Checkbox("部隊標籤##condFc", ref matchFc))
        {
            rule.MatchCompanyTag = matchFc;
            MarkRuleChanged(rule);
            Plugin.Instance.Config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("比對名字後面那個部隊縮寫（自由部隊 Tag）。\n沒有部隊的玩家是空字串。");

        ImGui.SameLine();
        ImGui.SetNextItemWidth(180f);
        var fcPattern = rule.CompanyTagPattern;
        if (ImGui.InputTextWithHint("##fcPattern", "部隊標籤樣式…", ref fcPattern, 64))
        {
            rule.CompanyTagPattern = fcPattern;
            MarkRuleChanged(rule);
        }
        if (ImGui.IsItemDeactivatedAfterEdit())
            Plugin.Instance.Config.Save();

        ImGui.SameLine();
        var fcRegex = rule.CompanyTagUseRegex;
        if (ImGui.Checkbox("正規表達式##fc", ref fcRegex))
        {
            rule.CompanyTagUseRegex = fcRegex;
            MarkRuleChanged(rule);
            Plugin.Instance.Config.Save();
        }

        if (rule.MatchCompanyTag && rule.CompanyTagUseRegex && !string.IsNullOrWhiteSpace(rule.CompanyTagPattern))
        {
            var (_, error) = GetRegex(rule.CompanyTagPattern);
            if (error != null)
                ImGui.TextColored(ColorError, $"部隊標籤正規表達式無效：{error}");
        }
    }

    private void DrawDistanceCondition(PlayerWatchRule rule)
    {
        var matchDistance = rule.MatchMaxDistance;
        if (ImGui.Checkbox("距離##condDistance", ref matchDistance))
        {
            rule.MatchMaxDistance = matchDistance;
            MarkRuleChanged(rule);
            Plugin.Instance.Config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("玩家與自己的直線距離小於等於設定值即命中。\n" +
                             "⚠️ 單獨啟用時，任何走近的玩家都會觸發——多半要搭配「所有條件都命中」使用。");

        ImGui.SameLine();
        ImGui.SetNextItemWidth(200f);
        var maxDistance = rule.MaxDistance;
        if (ImGui.SliderFloat("公尺以內##distance", ref maxDistance, 1f, 100f, "%.0f"))
            rule.MaxDistance = Math.Clamp(maxDistance, 0f, 1000f);
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            MarkRuleChanged(rule);
            Plugin.Instance.Config.Save();
        }
    }

    private void DrawRuleAction(PlayerWatchRule rule)
    {
        var command = rule.Command;
        if (ImGui.InputTextMultiline("##command", ref command, 1024,
                new Vector2(-1f, ImGui.GetTextLineHeight() * 3f)))
            rule.Command = command;
        if (ImGui.IsItemDeactivatedAfterEdit())
            Plugin.Instance.Config.Save();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("命中時執行的指令，每行一個，須以 / 開頭。\n例：/snd run 巨集名\n　　/echo 偵測到 {name} @ {world}（{status}）");

        var notify = rule.NotifyChat;
        if (ImGui.Checkbox("聊天欄通知", ref notify))
        {
            rule.NotifyChat = notify;
            Plugin.Instance.Config.Save();
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(80f);
        var cooldown = rule.CooldownSeconds;
        if (ImGui.InputInt("冷卻(秒)", ref cooldown, 0))
        {
            rule.CooldownSeconds = Math.Max(0, cooldown);
            Plugin.Instance.Config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("同一位玩家在這條規則上再次觸發的最短間隔。\n每條規則各自獨立計算，不與其他規則共用。");

        if (IsEnabled && rule.Enabled && HasActiveCondition(rule))
        {
            ImGui.SameLine();
            var matched = players.Where(p => Matches(rule, p)).ToList();
            ImGui.TextDisabled($"目前命中 {matched.Count} 人");
            if (matched.Count > 0 && ImGui.IsItemHovered())
            {
                var sb = new StringBuilder();
                foreach (var p in matched.Take(30))
                    sb.AppendLine($"[{p.Job}] {p.Name}{(string.IsNullOrEmpty(p.World) ? "" : $" @ {p.World}")} " +
                                  $"{p.Distance:0.0}m　{OnlineStatusName(p.OnlineStatusId)}" +
                                  (string.IsNullOrEmpty(p.CompanyTag) ? "" : $"　«{p.CompanyTag}»"));
                if (matched.Count > 30)
                    sb.AppendLine($"…等共 {matched.Count} 人");
                ImGui.SetTooltip(sb.ToString().TrimEnd('\n'));
            }
        }
    }

    #endregion
}

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
using Dalamud.Plugin.Services;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 周邊玩家數量統計：伺服器資訊列（DTR）顯示人數，點擊開啟清單懸浮窗。
/// 另含偵測規則：玩家出現且名稱命中（完全相符或正規表達式）時通知並執行斜線指令。
/// 機制：Framework 輪詢 ObjectTable 枚舉，零 hook（不抄 DR 的 InfoProxy hook 部分）。
/// </summary>
public sealed class AutoCountPlayers : TcModule
{
    public override string InternalName => "AutoCountPlayers";
    public override string DisplayName => "周邊玩家統計";
    public override string Description => "在伺服器資訊列顯示周邊玩家數量，滑鼠移上顯示名單，點擊開啟可搜尋的清單視窗（點名單可選取目標）。可設定偵測規則：玩家出現且名稱命中正規表達式時通知並執行指令。";

    public override bool HasConfigUI => true;

    /// <summary><c>Job</c> 是職業全名（台服 ClassJob 表自帶繁中），不是縮寫。</summary>
    private sealed record PlayerInfo(
        uint EntityId, string Name, string World, uint JobIconId, string Job, float Distance);

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
    private bool windowOpen;
    private string search = string.Empty;

    // 偵測規則的執行期狀態（不進存檔）
    private readonly Dictionary<PlayerWatchRule, (string Pattern, Regex? Regex, string? Error)> regexCache = [];
    private readonly Dictionary<(PlayerWatchRule Rule, string Player), long> lastTrigger = [];
    private readonly HashSet<(PlayerWatchRule Rule, string Player)> activeTriggered = [];
    private readonly Dictionary<PlayerWatchRule, long> patternChangedAt = [];
    private readonly HashSet<uint> matchedEntityIds = [];
    private readonly Queue<List<string>> pendingCommands = new();

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
                Vector3.Distance(localPlayer.Position, pc.Position)));
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

        foreach (var p in players)
        {
            var key = $"{p.Name}@{p.World}";
            present.Add(key);

            foreach (var rule in rules)
            {
                if (!rule.Enabled || string.IsNullOrWhiteSpace(rule.Pattern)) continue;
                if (!Matches(rule, p)) continue;

                matchedEntityIds.Add(p.EntityId);

                // 樣式剛在設定視窗改過（可能還在輸入中），沉澱一下再武裝
                if (patternChangedAt.TryGetValue(rule, out var changed) && now - changed < 1500) continue;

                var triggerKey = (rule, key);
                if (!activeTriggered.Add(triggerKey)) continue;

                // 冷卻中：只標記在場，不執行（避免冷卻結束時對持續在場者補觸發）
                if (lastTrigger.TryGetValue(triggerKey, out var last) &&
                    now - last < rule.CooldownSeconds * 1000L)
                    continue;

                lastTrigger[triggerKey] = now;
                Trigger(rule, p);
            }
        }

        activeTriggered.RemoveWhere(tk => !present.Contains(tk.Player));

        // 定期清掉過期的觸發紀錄與已刪除規則的殘留
        if (Throttle.Pass("AutoCountPlayers-PruneTriggers", 60_000))
        {
            foreach (var entry in lastTrigger.Where(kv =>
                         !rules.Contains(kv.Key.Rule) ||
                         now - kv.Value > Math.Max(3_600_000, kv.Key.Rule.CooldownSeconds * 2000L)).ToArray())
                lastTrigger.Remove(entry.Key);
            foreach (var rule in patternChangedAt.Keys.Where(r => !rules.Contains(r)).ToArray())
                patternChangedAt.Remove(rule);
        }
    }

    private bool Matches(PlayerWatchRule rule, PlayerInfo p)
    {
        var target = rule.MatchWithWorld ? $"{p.Name}@{p.World}" : p.Name;

        if (!rule.UseRegex)
            return string.Equals(target, rule.Pattern.Trim(), StringComparison.OrdinalIgnoreCase);

        var regex = GetRegex(rule).Regex;
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

    private (string Pattern, Regex? Regex, string? Error) GetRegex(PlayerWatchRule rule)
    {
        if (regexCache.TryGetValue(rule, out var cached) && cached.Pattern == rule.Pattern)
            return cached;

        (string, Regex?, string?) entry;
        try
        {
            entry = (rule.Pattern, new Regex(rule.Pattern,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(100)), null);
        }
        catch (ArgumentException ex)
        {
            entry = (rule.Pattern, null, ex.Message);
        }

        regexCache[rule] = entry;
        return entry;
    }

    private void Trigger(PlayerWatchRule rule, PlayerInfo p)
    {
        if (rule.NotifyChat)
            Svc.Chat.Print($"[TC Toolbox] 偵測到玩家：{p.Name}{(string.IsNullOrEmpty(p.World) ? "" : $" @ {p.World}")}（規則：{rule.Pattern}）");

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
        .Replace("{job}", p.Job, StringComparison.OrdinalIgnoreCase);

    /// <summary>每次輪詢只送一批指令，避免多名玩家同時命中時瞬間灌爆指令處理。</summary>
    private void PumpPendingCommands()
    {
        if (!pendingCommands.TryDequeue(out var lines)) return;
        foreach (var line in lines)
            ChatSender.ExecuteCommand(line);
    }

    #endregion

    private void DrawWindow()
    {
        if (!windowOpen) return;

        ImGui.SetNextWindowSize(new Vector2(420, 340), ImGuiCond.FirstUseEver);
        if (ImGui.Begin($"周邊玩家 ({players.Count})###TCToolboxCountPlayers", ref windowOpen))
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
                "指令欄每行一個斜線指令，可呼叫其他外掛（例如 /snd run 巨集名）。\n" +
                "支援佔位符：{name}＝玩家名、{world}＝伺服器、{job}＝職業全名。\n" +
                "正規表達式不分大小寫、部分符合即命中（要整名相符請用 ^…$）。");

        var rules = Config.WatchRules;
        int? removeIndex = null;

        for (var i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            ImGui.PushID($"watchRule{i}");

            var enabled = rule.Enabled;
            if (ImGui.Checkbox("##enabled", ref enabled))
            {
                rule.Enabled = enabled;
                Plugin.Instance.Config.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("啟用此規則");

            ImGui.SameLine();
            ImGui.SetNextItemWidth(180f);
            var pattern = rule.Pattern;
            if (ImGui.InputTextWithHint("##pattern", "名稱樣式…", ref pattern, 128))
            {
                rule.Pattern = pattern;
                patternChangedAt[rule] = Environment.TickCount64;
            }
            if (ImGui.IsItemDeactivatedAfterEdit())
                Plugin.Instance.Config.Save();

            ImGui.SameLine();
            var useRegex = rule.UseRegex;
            if (ImGui.Checkbox("正規表達式", ref useRegex))
            {
                rule.UseRegex = useRegex;
                patternChangedAt[rule] = Environment.TickCount64;
                Plugin.Instance.Config.Save();
            }

            ImGui.SameLine();
            var withWorld = rule.MatchWithWorld;
            if (ImGui.Checkbox("含伺服器", ref withWorld))
            {
                rule.MatchWithWorld = withWorld;
                patternChangedAt[rule] = Environment.TickCount64;
                Plugin.Instance.Config.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("比對「名稱@伺服器」而非只有名稱");

            ImGui.SameLine();
            if (ImGui.SmallButton("刪除"))
                removeIndex = i;

            if (rule.UseRegex && !string.IsNullOrWhiteSpace(rule.Pattern))
            {
                var (_, _, error) = GetRegex(rule);
                if (error != null)
                    ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), $"正規表達式無效：{error}");
            }

            var command = rule.Command;
            if (ImGui.InputTextMultiline("##command", ref command, 1024,
                    new Vector2(-1f, ImGui.GetTextLineHeight() * 3f)))
                rule.Command = command;
            if (ImGui.IsItemDeactivatedAfterEdit())
                Plugin.Instance.Config.Save();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("命中時執行的指令，每行一個，須以 / 開頭。\n例：/snd run 巨集名\n　　/echo 偵測到 {name} @ {world}");

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

            if (IsEnabled && rule.Enabled && !string.IsNullOrWhiteSpace(rule.Pattern))
            {
                ImGui.SameLine();
                var matched = players.Where(p => Matches(rule, p)).ToList();
                ImGui.TextDisabled($"目前命中 {matched.Count} 人");
                if (matched.Count > 0 && ImGui.IsItemHovered())
                {
                    var sb = new StringBuilder();
                    foreach (var p in matched.Take(30))
                        sb.AppendLine($"[{p.Job}] {p.Name}{(string.IsNullOrEmpty(p.World) ? "" : $" @ {p.World}")} {p.Distance:0.0}m");
                    if (matched.Count > 30)
                        sb.AppendLine($"…等共 {matched.Count} 人");
                    ImGui.SetTooltip(sb.ToString().TrimEnd('\n'));
                }
            }

            ImGui.Separator();
            ImGui.PopID();
        }

        if (removeIndex is { } idx)
        {
            regexCache.Remove(rules[idx]);
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
}

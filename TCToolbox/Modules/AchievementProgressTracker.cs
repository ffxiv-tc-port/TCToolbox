using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using TCToolbox.Core;
using AchievementSheet = Lumina.Excel.Sheets.Achievement;
using GameAchievement = FFXIVClientStructs.FFXIV.Client.Game.UI.Achievement;

namespace TCToolbox.Modules;

/// <summary>
/// 成就進度追蹤：把想盯的成就加進清單，按「重新整理」向伺服器問一次目前進度並記下來。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 <b>一次只問一筆，而且只有按按鈕才問。</b>伺服器對這種查詢的速率限制未知，
/// 所以刻意<b>不做</b>「開著就自己輪詢」——上游 Automaton 的 <c>AchievementTracker</c> 有一個
/// 「更新頻率（秒）」設定會定時把整份清單重問一遍，本模組沒有那個東西。
/// </para>
/// <para>
/// 📌 <b>不掛 hook。</b>上游是去 hook <c>ReceiveAchievementProgress</c> 拿回應，
/// 但遊戲把結果就寫在 <c>Achievement</c> 這個單例的四個欄位上，輪詢那四個欄位就夠了。
/// 台服 7.20 主程式離線反組譯實證（2026-08-19）：
/// <list type="bullet">
/// <item><c>RequestAchievementProgress</c>（0x140A1E030）第一件事就是
/// <c>mov dword [this+0x218], 1</c>，也就是把 <c>ProgressRequestState</c> 設成
/// <c>Requested</c>，然後送 <c>ExecuteCommand(1000, 成就編號)</c>。</item>
/// <item><c>ReceiveAchievementProgress</c>（0x140A1E430）整支只有四行：
/// <c>[this+0x218]=2</c>（<c>Loaded</c>）、<c>[this+0x21C]=成就編號</c>、
/// <c>[this+0x220]=目前進度</c>、<c>[this+0x224]=目標值</c>。</item>
/// </list>
/// ⇒ 「送出後等到 <c>State==Loaded</c> 且 <c>ProgressAchievementId</c> 等於我問的那一筆」
/// 是可靠的完成判定，<b>連續問同一筆也不會誤判</b>（送出那一刻 state 被打回 <c>Requested</c>）。
/// </para>
/// <para>
/// ⚠️ 遊戲只有<b>一組</b>進度欄位，所以「一次一筆」不只是保守，是機制本身的限制。
/// </para>
/// <para>
/// 🔴 沒查過的進度一律在列上顯示灰色的 <c>?</c>，<b>不畫成 0</b>——0 是一個看起來很正常的錯答案。
/// </para>
/// </remarks>
public sealed unsafe class AchievementProgressTracker : TcModule
{
    public override string InternalName => "AchievementProgressTracker";
    public override string DisplayName => "成就進度追蹤";

    public override string Description =>
        "把想盯的成就加進追蹤清單，按下重新整理時向伺服器查一次目前進度並記下來。" +
        "一次只查一筆、只有按按鈕才查，不會自己定時輪詢。";

    public override ModuleCategory Category => ModuleCategory.Misc;

    /// <summary>開著不按按鈕的話，一次查詢都不會送出。</summary>
    public override bool IsManualTrigger => true;

    public override bool HasConfigUI => true;

    private AchievementProgressTrackerConfig Config => Plugin.Instance.Config.AchievementTracker;

    private readonly TaskQueue queue = new() { DefaultTimeoutMs = 15_000 };

    /// <summary>成就名稱索引（只建一次；3825 列，每幀重算是浪費）。</summary>
    private List<(uint Id, string Name, byte Points)>? nameIndex;

    private readonly Dictionary<uint, string> nameById = [];

    private string searchFilter = string.Empty;

    /// <summary>
    /// 上一次重建搜尋結果時用的過濾字串（<c>null</c>＝還沒建過）。
    /// </summary>
    private string? lastFilter;

    private readonly List<(uint Id, string Name, byte Points)> searchResults = [];

    protected override void OnEnable()
    {
        queue.OnTimeout = step =>
        {
            Svc.Chat.PrintError($"[TC Toolbox] 成就進度查詢逾時：{step}");
            Svc.Log.Information($"[{InternalName}] 查詢逾時：{step}（伺服器沒有回應，進度維持原值）");
        };

        Svc.Framework.Update += OnUpdate;
        Svc.Log.Information($"[{InternalName}] 模組啟用：追蹤 {Config.Tracked.Count} 筆成就。");
    }

    protected override void OnDisable()
    {
        Svc.Framework.Update -= OnUpdate;
        queue.Abort();
    }

    private void OnUpdate(IFramework framework) => queue.Tick();

    public override ModuleNotice? RowNotice
    {
        get
        {
            var tracked = Config.Tracked;
            if (tracked.Count == 0) return null;

            var unknown = 0;
            foreach (var entry in tracked)
            {
                if (entry.UpdatedAtUtcTicks == 0) unknown++;
            }

            if (unknown == 0) return null;

            return new ModuleNotice(
                ModuleNoticeLevel.Unknown,
                $"{unknown}/{tracked.Count} 筆進度未知",
                "這些成就從來沒有查詢過，所以進度不是 0 而是不知道。開啟模組後按「全部重新整理」各查一次。");
        }
    }

    // ── 查詢流程 ────────────────────────────────────────────────────────────

    /// <summary>排入一筆查詢（送出 → 等回應 → 寫回設定）。</summary>
    private void EnqueueQuery(uint achievementId)
    {
        var label = DisplayNameOf(achievementId);

        queue.Enqueue($"查詢「{label}」", () =>
        {
            if (!Svc.ClientState.IsLoggedIn)
            {
                Svc.Log.Information($"[{InternalName}] 尚未登入，取消查詢。");
                return null;
            }

            // 🔴 每次重新取，不跨幀保存。
            var achievement = GameAchievement.Instance();
            if (achievement == null)
            {
                Svc.Log.Information($"[{InternalName}] 取不到 Achievement 單例，取消查詢。");
                return null;
            }

            // 送出本身另設節流：使用者連按按鈕時不要變成連珠炮。
            if (!Throttle.Pass("AchievementProgressTracker-Request", Math.Max(200, Config.RequestIntervalMs)))
                return false;

            achievement->RequestAchievementProgress(achievementId);
            Svc.Log.Information($"[{InternalName}] 已送出查詢：#{achievementId}「{label}」");
            return true;
        }, 15_000);

        queue.Enqueue($"等待「{label}」的回應", () =>
        {
            var achievement = GameAchievement.Instance();
            if (achievement == null) return null;

            if (achievement->ProgressRequestState != GameAchievement.AchievementState.Loaded) return false;
            if (achievement->ProgressAchievementId != achievementId) return false;

            StoreProgress(achievementId, achievement->ProgressCurrent, achievement->ProgressMax);
            return true;
        }, Math.Max(3_000, Config.ResponseTimeoutMs));
    }

    private void StoreProgress(uint achievementId, uint current, uint max)
    {
        foreach (var entry in Config.Tracked)
        {
            if (entry.Id != achievementId) continue;

            entry.Current = current;
            entry.Max = max;
            entry.UpdatedAtUtcTicks = DateTime.UtcNow.Ticks;
            Plugin.Instance.Config.Save();

            Svc.Log.Information(
                $"[{InternalName}] 收到進度：#{achievementId}「{DisplayNameOf(achievementId)}」{current}/{max}");
            return;
        }

        // 查詢途中被移除：不是錯誤，只是沒有地方可以寫。
        Svc.Log.Information($"[{InternalName}] 收到 #{achievementId} 的進度，但它已不在追蹤清單裡，忽略。");
    }

    private void RefreshAll()
    {
        if (queue.IsBusy) return;

        var queued = 0;
        foreach (var entry in Config.Tracked)
        {
            if (Config.SkipCompletedOnRefresh && IsCompleted(entry)) continue;
            EnqueueQuery(entry.Id);
            queued++;
        }

        Svc.Log.Information($"[{InternalName}] 全部重新整理：排入 {queued}/{Config.Tracked.Count} 筆（一次一筆依序送出）。");

        if (queued == 0)
            Svc.Chat.Print("[TC Toolbox] 追蹤清單裡沒有需要查詢的成就。");
    }

    /// <summary>
    /// 這一筆是不是已經達成。
    /// </summary>
    /// <remarks>
    /// ⚠️ 只用<b>已經查回來的</b>進度判斷，不去問遊戲的完成旗標——
    /// 那份資料要玩家開過成就視窗才會載入，沒載入時問到的是「全部未完成」，
    /// 而那個錯答案看起來完全正常。
    /// </remarks>
    private static bool IsCompleted(TrackedAchievementEntry entry) =>
        entry.UpdatedAtUtcTicks != 0 && entry.Max != 0 && entry.Current >= entry.Max;

    // ── UI ──────────────────────────────────────────────────────────────────

    private void EnsureNameIndex()
    {
        if (nameIndex != null) return;

        nameIndex = [];
        var sheet = Svc.Data.GetExcelSheet<AchievementSheet>();
        foreach (var row in sheet)
        {
            var name = row.Name.ExtractText();
            if (string.IsNullOrWhiteSpace(name)) continue;

            nameIndex.Add((row.RowId, name, row.Points));
            nameById[row.RowId] = name;
        }

        Svc.Log.Information($"[{InternalName}] 成就名稱索引建立完成：{nameIndex.Count} 筆。");
    }

    private string DisplayNameOf(uint achievementId) =>
        nameById.TryGetValue(achievementId, out var name) ? name : $"#{achievementId}";

    public override void DrawConfig()
    {
        EnsureNameIndex();

        ImGui.TextDisabled("一次只查一筆、只有按按鈕才查。伺服器速率限制未知，刻意不做定時輪詢。");

        DrawSearch();
        ImGui.Separator();
        DrawTrackedList();
        ImGui.Separator();
        DrawOptions();
    }

    private void DrawSearch()
    {
        ImGui.SetNextItemWidth(240f);
        var filter = searchFilter;
        if (ImGui.InputTextWithHint("##achievementSearch", "搜尋成就名稱…", ref filter, 64))
            searchFilter = filter;

        if (searchFilter != lastFilter)
        {
            lastFilter = searchFilter;
            RebuildSearchResults();
        }

        if (searchResults.Count == 0)
        {
            if (searchFilter.Length > 0)
                ImGui.TextDisabled("沒有符合的成就。");
            return;
        }

        using var child = ImRaii.Child("TCToolboxAchievementSearch",
                                       new Vector2(ImGui.GetContentRegionAvail().X, 150f), true);
        if (!child) return;

        foreach (var (id, name, points) in searchResults)
        {
            using var rowId = ImRaii.PushId((int)id);

            var alreadyTracked = IsTracked(id);
            using (ImRaii.Disabled(alreadyTracked))
            {
                if (ImGui.Button("＋"))
                {
                    Config.Tracked.Add(new TrackedAchievementEntry { Id = id });
                    Plugin.Instance.Config.Save();
                    Svc.Log.Information($"[{InternalName}] 加入追蹤：#{id}「{name}」");
                }
            }

            ImGui.SameLine();
            if (alreadyTracked)
                ImGui.TextDisabled($"{name}（已在清單）");
            else
                ImGui.Text($"{name}　{points} 點");
        }
    }

    private void RebuildSearchResults()
    {
        searchResults.Clear();
        if (nameIndex == null || searchFilter.Length == 0) return;

        foreach (var entry in nameIndex)
        {
            if (entry.Name.IndexOf(searchFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;

            searchResults.Add(entry);
            if (searchResults.Count >= 60) break;
        }
    }

    private bool IsTracked(uint achievementId)
    {
        foreach (var entry in Config.Tracked)
        {
            if (entry.Id == achievementId) return true;
        }

        return false;
    }

    private void DrawTrackedList()
    {
        if (Config.Tracked.Count == 0)
        {
            ImGui.TextDisabled("追蹤清單是空的。用上面的搜尋框找成就並按「＋」加入。");
            return;
        }

        using (ImRaii.Disabled(queue.IsBusy))
        {
            if (ImGui.Button("全部重新整理"))
                RefreshAll();
        }

        if (queue.IsBusy)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(queue.CurrentStep ?? string.Empty);

            ImGui.SameLine();
            if (ImGui.Button("停止查詢"))
                queue.Abort();
        }

        if (!ImGui.BeginTable("##achievementTracked", 3,
                              ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp))
            return;

        ImGui.TableSetupColumn("成就", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("進度", ImGuiTableColumnFlags.WidthFixed, 170f);
        ImGui.TableSetupColumn("操作", ImGuiTableColumnFlags.WidthFixed, 110f);
        ImGui.TableHeadersRow();

        TrackedAchievementEntry? removing = null;

        foreach (var entry in Config.Tracked)
        {
            using var rowId = ImRaii.PushId((int)entry.Id);

            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            ImGui.Text(DisplayNameOf(entry.Id));

            ImGui.TableNextColumn();
            DrawProgressCell(entry);

            ImGui.TableNextColumn();
            using (ImRaii.Disabled(queue.IsBusy))
            {
                if (ImGui.Button("重新整理"))
                    EnqueueQuery(entry.Id);
            }

            ImGui.SameLine();
            if (ImGui.Button("×"))
                removing = entry;
        }

        ImGui.EndTable();

        if (removing != null)
        {
            Config.Tracked.Remove(removing);
            Plugin.Instance.Config.Save();
            Svc.Log.Information($"[{InternalName}] 移除追蹤：#{removing.Id}");
        }
    }

    /// <summary>
    /// 進度欄。
    /// </summary>
    /// <remarks>
    /// 🔴 從未查詢過就畫灰色的 <c>?</c>。畫成 <c>0 / 0</c> 會讓人以為「查過了，只是還沒開始」，
    /// 而那是完全相反的意思。長字串（上次更新時間）放 tooltip，不占列上的位置。
    /// </remarks>
    private static void DrawProgressCell(TrackedAchievementEntry entry)
    {
        ImGui.AlignTextToFramePadding();

        if (entry.UpdatedAtUtcTicks == 0)
        {
            ImGui.TextDisabled("?");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("這一筆從來沒有查詢過，所以進度是不知道，不是 0。按右邊的「重新整理」查一次。");
            return;
        }

        var updated = new DateTime(entry.UpdatedAtUtcTicks, DateTimeKind.Utc).ToLocalTime();

        if (entry.Max == 0)
        {
            ImGui.TextDisabled($"{entry.Current} / ?");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"伺服器沒有回報目標值。\n上次更新：{updated:yyyy-MM-dd HH:mm}");
            return;
        }

        var ratio = Math.Clamp(entry.Current / (float)entry.Max, 0f, 1f);

        if (entry.Current >= entry.Max)
            ImGui.TextColored(new Vector4(0.45f, 0.9f, 0.45f, 1f), $"已達成（{entry.Current} / {entry.Max}）");
        else
            ImGui.Text($"{entry.Current} / {entry.Max}（{ratio * 100f:F1}%）");

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"上次更新：{updated:yyyy-MM-dd HH:mm}\n進度只在按下重新整理時才會向伺服器查詢。");
    }

    private void DrawOptions()
    {
        var skipCompleted = Config.SkipCompletedOnRefresh;
        if (ImGui.Checkbox("「全部重新整理」時略過已達成的", ref skipCompleted))
        {
            Config.SkipCompletedOnRefresh = skipCompleted;
            Plugin.Instance.Config.Save();
        }

        ImGui.SetNextItemWidth(160f);
        var interval = Config.RequestIntervalMs;
        if (ImGui.SliderInt("每筆之間的間隔（毫秒）", ref interval, 200, 5000))
        {
            Config.RequestIntervalMs = interval;
            Plugin.Instance.Config.Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("伺服器對成就進度查詢的速率限制未知，這個間隔刻意留得保守。");

        ImGui.SetNextItemWidth(160f);
        var timeout = Config.ResponseTimeoutMs;
        if (ImGui.SliderInt("等待回應逾時（毫秒）", ref timeout, 3000, 20000))
        {
            Config.ResponseTimeoutMs = timeout;
            Plugin.Instance.Config.Save();
        }
    }
}

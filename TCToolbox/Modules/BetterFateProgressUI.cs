using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using TCToolbox.Core;
using GameAchievement = FFXIVClientStructs.FFXIV.Client.Game.UI.Achievement;
using GameInventoryManager = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager;
using GameTelepo = FFXIVClientStructs.FFXIV.Client.Game.UI.Telepo;
using AgentMap = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentMap;
using ItemSheet = Lumina.Excel.Sheets.Item;
using TerritoryTypeSheet = Lumina.Excel.Sheets.TerritoryType;

namespace TCToolbox.Modules;

/// <summary>
/// 共鬥 F.A.T.E. 進度總覽：把 5.0／6.0／7.0 三個資料片各 6 張圖的「共鬥 F.A.T.E. 完成數」
/// 與雙色寶石庫存彙整成一個唯讀視窗，每張圖附「地圖」「傳送」兩顆手動按鈕。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 <b>進度資料只有按按鈕才向伺服器查，一次一筆、每秒一筆。</b>這幾張圖的完成數是記在
/// <b>成就進度</b>上的（5.0＝成就 2343–2348、6.0＝3022–3027、7.0＝3559–3564），
/// 而遊戲只有<b>一組</b>成就進度欄位，一次只能有一筆在途。DR 原版是開著就每 10 秒把 18 筆
/// 一次全射出去（<c>ExecuteCommand(1000, id)</c>×18），台服對這種查詢的速率限制未知，
/// 所以本模組比照 <see cref="AchievementProgressTracker"/>：<b>排隊、一次一筆、每秒一筆</b>，
/// 而且<b>只在開窗與按下重新整理時查</b>，不定時輪詢。
/// </para>
/// <para>
/// 🔴 沒查過的圖一律顯示灰色 <c>?</c>，<b>絕不畫成 0</b>——0 是一個看起來很正常的錯答案。
/// </para>
/// <para>
/// 🔴 <b>純顯示＋手動觸發。</b>「傳送」走遊戲原生 <c>Telepo.Teleport</c>（等同在地圖上點乙太之光傳送，
/// 戰鬥中／未解鎖會被遊戲自己擋下，那是對的），「地圖」開對應區域地圖。兩者都只在按下那一刻發生。
/// DR 原版把背景材質丟進 <c>Framework.Run</c> 跨幀持有 texture wrap，本模組<b>不畫那些背景圖</b>
/// （純文字版面），只用 <see cref="ITextureProvider"/> 即時取雙色寶石圖示（每幀取 wrap、不跨幀保存）。
/// </para>
/// </remarks>
public sealed unsafe class BetterFateProgressUI : TcModule
{
    public override string InternalName => "BetterFateProgressUI";
    public override string DisplayName => "共鬥 F.A.T.E. 進度總覽";

    public override string Description =>
        "把 5.0／6.0／7.0 各區的共鬥 F.A.T.E. 完成數與雙色寶石庫存彙整成一個唯讀視窗，附手動的「地圖」「傳送」。"
        + "進度只在開窗與按重新整理時向伺服器查一次，一次一筆、每秒一筆。";

    public override ModuleCategory Category => ModuleCategory.Combat;

    /// <summary>開著不開窗、不按按鈕，一次查詢都不會送出。</summary>
    public override bool IsManualTrigger => true;

    public override bool HasConfigUI => true;

    /// <summary>雙色寶石（Bicolor Gemstone）的道具 id。</summary>
    private const uint BicolorGemItemId = 26807;

    private static readonly Vector4 GoldColor = new(0.95f, 0.82f, 0.35f, 1f);
    private static readonly Vector4 DoneColor = new(0.45f, 0.9f, 0.45f, 1f);
    private static readonly Vector4 UnknownColor = new(0.68f, 0.68f, 0.68f, 1f);

    private BetterFateProgressConfig Config => Plugin.Instance.Config.BetterFateProgress;

    /// <summary>成就 id → 區域 <see cref="TerritoryTypeSheet"/> RowId，並帶資料片分組。</summary>
    private static readonly (uint AchievementId, uint ZoneId, int Expansion)[] ZoneTable =
    [
        // 5.0（漆黑的反叛者）
        (2343, 813, 0), (2344, 814, 0), (2345, 815, 0),
        (2346, 816, 0), (2347, 817, 0), (2348, 818, 0),
        // 6.0（曉月的終焉）
        (3022, 956, 1), (3023, 957, 1), (3024, 958, 1),
        (3025, 959, 1), (3026, 961, 1), (3027, 960, 1),
        // 7.0（黃金的遺產）
        (3559, 1187, 2), (3560, 1188, 2), (3561, 1189, 2),
        (3562, 1190, 2), (3563, 1191, 2), (3564, 1192, 2),
    ];

    private static readonly string[] ExpansionLabels = ["5.0", "6.0", "7.0"];

    /// <summary>一張圖解析後的顯示資訊（只建一次；名稱／mapId／aetheryteId 都是純值）。</summary>
    private readonly record struct ZoneInfo(
        uint AchievementId, uint ZoneId, int Expansion,
        string Name, uint MapId, uint AetheryteId);

    /// <summary>解析後的圖清單；<c>null</c>＝還沒建過。</summary>
    private List<ZoneInfo>? zones;

    /// <summary>成就 id → 已查回的進度；沒有鍵＝從未查過（顯示 <c>?</c>，不是 0）。</summary>
    private readonly Dictionary<uint, (uint Current, uint Max)> progress = [];

    private readonly TaskQueue queue = new() { DefaultTimeoutMs = 15_000 };

    private bool windowOpen;

    /// <summary>雙色寶石庫存與上限（每隔一段時間刷新一次；上限只讀一次）。</summary>
    private int gemCount = -1;
    private uint gemCap;
    private uint gemIconId;

    protected override void OnEnable()
    {
        windowOpen = false;
        gemCount = -1;

        queue.OnTimeout = step =>
            Svc.Log.Information($"[{InternalName}] 成就進度查詢逾時：{step}（進度維持原值）");

        Svc.Framework.Update += OnFrameworkUpdate;
        Svc.PluginInterface.UiBuilder.Draw += DrawWindow;

        Svc.Log.Information($"[{InternalName}] 模組啟用。");
    }

    protected override void OnDisable()
    {
        Svc.PluginInterface.UiBuilder.Draw -= DrawWindow;
        Svc.Framework.Update -= OnFrameworkUpdate;

        queue.Abort();
        windowOpen = false;
    }

    private void OnFrameworkUpdate(IFramework framework) => queue.Tick();

    // ── 資料解析 ───────────────────────────────────────────────────────────────

    private void EnsureZones()
    {
        if (zones != null) return;

        zones = [];
        var sheet = Svc.Data.GetExcelSheet<TerritoryTypeSheet>();

        foreach (var (achId, zoneId, expansion) in ZoneTable)
        {
            var row = sheet.GetRowOrDefault(zoneId);
            if (row is not { } zone) continue;

            var name = zone.PlaceName.ValueNullable?.Name.ExtractText() ?? $"區域 {zoneId}";
            var mapId = zone.Map.RowId;
            var aetheryteId = zone.Aetheryte.RowId;

            zones.Add(new ZoneInfo(achId, zoneId, expansion, name, mapId, aetheryteId));
        }

        // 雙色寶石的上限與圖示只讀一次。
        var gem = Svc.Data.GetExcelSheet<ItemSheet>().GetRowOrDefault(BicolorGemItemId);
        if (gem is { } gemRow)
        {
            gemCap = gemRow.StackSize;
            gemIconId = gemRow.Icon;
        }

        Svc.Log.Information($"[{InternalName}] 圖清單解析完成：{zones.Count}/{ZoneTable.Length} 張。");
    }

    // ── 查詢流程（比照 AchievementProgressTracker：一次一筆、每秒一筆）────────────

    private void RefreshAll()
    {
        if (queue.IsBusy) return;
        EnsureZones();
        if (zones == null) return;

        foreach (var zone in zones)
            EnqueueQuery(zone.AchievementId);

        Svc.Log.Information($"[{InternalName}] 重新整理：排入 {zones.Count} 筆成就進度查詢（依序、每秒一筆）。");
    }

    private void EnqueueQuery(uint achievementId)
    {
        queue.Enqueue($"查詢成就 #{achievementId}", () =>
        {
            if (!Svc.ClientState.IsLoggedIn) return null;

            var achievement = GameAchievement.Instance();
            if (achievement == null) return null;

            // 每秒一筆：台服對成就進度查詢的速率限制未知，刻意保守。
            if (!Throttle.Pass("BetterFateProgressUI-Request", Math.Max(200, Config.RequestIntervalMs)))
                return false;

            achievement->RequestAchievementProgress(achievementId);
            return true;
        }, 15_000);

        queue.Enqueue($"等待成就 #{achievementId} 回應", () =>
        {
            var achievement = GameAchievement.Instance();
            if (achievement == null) return null;

            if (achievement->ProgressRequestState != GameAchievement.AchievementState.Loaded) return false;
            if (achievement->ProgressAchievementId != achievementId) return false;

            progress[achievementId] = (achievement->ProgressCurrent, achievement->ProgressMax);
            return true;
        }, 8_000);
    }

    private void RefreshGemCount()
    {
        if (!Throttle.Pass("BetterFateProgressUI-GemCount", 2_000) && gemCount >= 0) return;

        var manager = GameInventoryManager.Instance();
        if (manager == null) return;

        gemCount = manager->GetInventoryItemCount(BicolorGemItemId, false, true, true, 0);
    }

    // ── 列上提示 ───────────────────────────────────────────────────────────────

    public override ModuleNotice? RowNotice
    {
        get
        {
            EnsureZones();
            if (zones == null || zones.Count == 0) return null;

            var unknown = 0;
            foreach (var zone in zones)
            {
                if (!progress.ContainsKey(zone.AchievementId)) unknown++;
            }

            if (unknown == 0) return null;

            return new ModuleNotice(
                ModuleNoticeLevel.Unknown,
                $"{unknown}/{zones.Count} 張圖進度未知",
                "這些圖的完成數從來沒有查詢過，所以顯示的不是 0 而是不知道。開窗或按「重新整理」各查一次。");
        }
    }

    // ── UI ────────────────────────────────────────────────────────────────────

    public override void DrawConfig()
    {
        var wasOpen = windowOpen;
        if (ImGui.Button(windowOpen ? "關閉進度視窗" : "開啟進度視窗"))
        {
            windowOpen = !windowOpen;
            if (!wasOpen && windowOpen && Config.AutoRefreshOnOpen)
                RefreshAll();
        }

        ImGui.TextDisabled("進度只在開窗與按「重新整理」時向伺服器查一次，一次一筆、每秒一筆。");

        var autoRefresh = Config.AutoRefreshOnOpen;
        if (ImGui.Checkbox("開窗時自動重新整理一次", ref autoRefresh))
        {
            Config.AutoRefreshOnOpen = autoRefresh;
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
            ImGui.SetTooltip("台服對成就進度查詢的速率限制未知，這個間隔刻意留得保守。");
    }

    private void DrawWindow()
    {
        if (!windowOpen) return;

        ImGui.SetNextWindowSize(new Vector2(560, 420), ImGuiCond.FirstUseEver);
        if (ImGui.Begin($"{DisplayName}###TCToolboxBetterFateProgressUI", ref windowOpen))
            DrawContent();
        ImGui.End();
    }

    private void DrawContent()
    {
        if (!Svc.ClientState.IsLoggedIn)
        {
            ImGui.TextDisabled("尚未登入。");
            return;
        }

        EnsureZones();
        if (zones == null || zones.Count == 0)
        {
            ImGui.TextDisabled("讀不到圖清單資料。");
            return;
        }

        RefreshGemCount();
        DrawHeader();
        ImGui.Separator();
        DrawTabs();
    }

    private void DrawHeader()
    {
        // 雙色寶石圖示 + 庫存/上限
        if (gemIconId != 0)
        {
            var lookup = new GameIconLookup(gemIconId);
            if (Svc.Textures.TryGetFromGameIcon(lookup, out var tex))
            {
                ImGui.Image(tex.GetWrapOrEmpty().Handle, new Vector2(24f, 24f));
                ImGui.SameLine();
            }
        }

        ImGui.AlignTextToFramePadding();
        if (gemCount < 0)
            ImGui.TextColored(UnknownColor, $"雙色寶石：?／{(gemCap > 0 ? gemCap.ToString() : "?")}");
        else
            ImGui.TextColored(GoldColor, $"雙色寶石：{gemCount}／{(gemCap > 0 ? gemCap.ToString() : "?")}");

        ImGui.SameLine();
        ImGui.Dummy(new Vector2(16f, 0f));
        ImGui.SameLine();

        using (ImRaii.Disabled(queue.IsBusy))
        {
            if (ImGui.Button("重新整理"))
                RefreshAll();
        }

        if (queue.IsBusy)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(queue.CurrentStep ?? string.Empty);
            ImGui.SameLine();
            if (ImGui.Button("停止"))
                queue.Abort();
        }
    }

    private void DrawTabs()
    {
        using var tabBar = ImRaii.TabBar("##fateProgressTabs");
        if (!tabBar) return;

        for (var exp = 0; exp < ExpansionLabels.Length; exp++)
        {
            using var tab = ImRaii.TabItem(ExpansionLabels[exp]);
            if (!tab) continue;

            DrawExpansionTable(exp);
        }
    }

    private void DrawExpansionTable(int expansion)
    {
        if (zones == null) return;

        if (!ImGui.BeginTable($"##fateProgress{expansion}", 4,
                              ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp))
            return;

        ImGui.TableSetupColumn("區域", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("進度", ImGuiTableColumnFlags.WidthFixed, 190f);
        ImGui.TableSetupColumn("地圖", ImGuiTableColumnFlags.WidthFixed, 52f);
        ImGui.TableSetupColumn("傳送", ImGuiTableColumnFlags.WidthFixed, 52f);
        ImGui.TableHeadersRow();

        foreach (var zone in zones)
        {
            if (zone.Expansion != expansion) continue;
            DrawZoneRow(zone);
        }

        ImGui.EndTable();
    }

    private void DrawZoneRow(in ZoneInfo zone)
    {
        using var rowId = ImRaii.PushId((int)zone.AchievementId);

        ImGui.TableNextRow();

        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(zone.Name);

        ImGui.TableNextColumn();
        DrawProgressCell(zone.AchievementId);

        ImGui.TableNextColumn();
        using (ImRaii.Disabled(zone.MapId == 0))
        {
            if (ImGui.Button("地圖"))
                OpenMap(zone);
        }

        ImGui.TableNextColumn();
        using (ImRaii.Disabled(zone.AetheryteId == 0))
        {
            if (ImGui.Button("傳送"))
                Teleport(zone);
        }
        if (zone.AetheryteId == 0 && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("這張圖沒有對應的乙太之光。");
    }

    /// <summary>
    /// 進度欄。🔴 從未查過就畫灰色 <c>?</c>，不畫成 0。
    /// </summary>
    private void DrawProgressCell(uint achievementId)
    {
        ImGui.AlignTextToFramePadding();

        if (!progress.TryGetValue(achievementId, out var p))
        {
            ImGui.TextColored(UnknownColor, "?");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("這張圖從來沒有查詢過，所以完成數是不知道，不是 0。按上面的「重新整理」查一次。");
            return;
        }

        var max = p.Max > 0 ? p.Max : 66u; // 伺服器沒回上限時退回已知的 66（6+60）。
        var current = p.Current;

        if (current >= max)
        {
            ImGui.TextColored(DoneColor, $"已達成（{current}／{max}）");
            return;
        }

        var ratio = Math.Clamp(current / (float)max, 0f, 1f);
        ImGui.ProgressBar(ratio, new Vector2(-1f, 0f), $"{current}／{max}（還差 {max - current}）");
    }

    // ── 手動動作 ───────────────────────────────────────────────────────────────

    private void OpenMap(in ZoneInfo zone)
    {
        var agent = AgentMap.Instance();
        if (agent == null)
        {
            Svc.Log.Information($"[{InternalName}] 取不到 AgentMap，無法開啟「{zone.Name}」的地圖。");
            return;
        }

        agent->OpenMapByMapId(zone.MapId, zone.ZoneId);
        Svc.Log.Information($"[{InternalName}] 使用者開啟地圖：「{zone.Name}」(map {zone.MapId})。");
    }

    /// <summary>
    /// 傳送到該區域的乙太之光。走遊戲原生 <c>Telepo.Teleport</c>——等同在地圖上點乙太之光傳送，
    /// 戰鬥中／未解鎖時遊戲會自己擋下（那是對的行為）。<b>只在按下時觸發，不自動。</b>
    /// </summary>
    private void Teleport(in ZoneInfo zone)
    {
        var telepo = GameTelepo.Instance();
        if (telepo == null)
        {
            Svc.Log.Information($"[{InternalName}] 取不到 Telepo，無法傳送到「{zone.Name}」。");
            return;
        }

        // 先刷新一次內部乙太之光清單，否則剛登入時清單可能是空的、Teleport 靜默失敗。
        telepo->UpdateAetheryteList();
        var ok = telepo->Teleport(zone.AetheryteId, 0);

        Svc.Log.Information(
            $"[{InternalName}] 使用者手動傳送 → 乙太之光 {zone.AetheryteId}「{zone.Name}」，遊戲接受={ok}。");

        if (!ok)
            Svc.Chat.Print($"[TC Toolbox] 無法傳送到「{zone.Name}」（可能在戰鬥中、尚未解鎖該乙太之光，或金幣不足）。");
    }
}

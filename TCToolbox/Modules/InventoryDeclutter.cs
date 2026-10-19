using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 一鍵清倉：按一次，依序把「陸行鳥鞍囊：寄放重複道具」與「收藏櫃：可收納裝備一鍵存入」各跑一輪。
/// </summary>
/// <remarks>
/// 🔴 <b>純手動</b>：開著不按按鈕，遊戲行為完全不變。
/// 🔴 去處只收<b>拿得回來</b>的那兩處。自動丟棄與幻化櫃整理<b>刻意不接</b>——那是處分不是清倉。
/// 📌 前置條件沒到的那一段是<b>跳過並寫明原因</b>，不是整輪失敗：那幾扇視窗不保證同時開得起來。
/// 🔴 本模組自己不碰任何原生結構，只轉呼叫兩個既有模組的執行入口，沒有第二份搬移邏輯。
/// </remarks>
public sealed class InventoryDeclutter : TcModule
{
    public override string InternalName => "InventoryDeclutter";

    public override string DisplayName => "背包一鍵清倉";

    public override string Description =>
        "按一顆按鈕，依序跑完「陸行鳥鞍囊：寄放重複道具」與「收藏櫃：可收納裝備一鍵存入」，" +
        "不必分別開兩個模組各按一次。兩段各自可勾選、各自有逾時；視窗沒開或那一段沒事可做就跳過" +
        "並寫明原因，任何一段沒有正常結束就停下來報一行。只搬去拿得回來的地方——" +
        "不丟棄、不碰市場、不動幻化櫃。不會自動執行。";

    public override ModuleCategory Category => ModuleCategory.Inventory;

    /// <inheritdoc/>
    /// <remarks>開著不按按鈕＝遊戲行為完全不變，所以是手動觸發。</remarks>
    public override bool IsManualTrigger => true;

    public override bool HasConfigUI => true;

    /// <summary>整輪的時間上限（毫秒）。到了就停，不重試。</summary>
    private const int MaxRoundMs = 30 * 60 * 1_000;

    /// <summary>
    /// 一段清倉工作。
    /// </summary>
    /// <remarks>
    /// 🔴 零值刻意留成 <see cref="None"/>：忘了指派時會走到「不認得的段落」那條路徑並停下來，
    /// 而不是安靜地跑成第一段。
    /// </remarks>
    private enum Segment
    {
        None = 0,
        Saddlebag = 1,
        Cabinet = 2,
    }

    private InventoryDeclutterConfig Config => Plugin.Instance.Config.Declutter;

    /// <summary>本輪要跑的段落。<b>整份換一個參考</b>，繪製執行緒只讀不改。</summary>
    private Segment[] plan = [];

    private int segmentIndex;

    /// <summary>目前這一段已經交給對應模組了嗎。<c>false</c>＝還沒開跑。</summary>
    private bool segmentStarted;

    private bool roundActive;
    private bool startRequested;
    private bool stopRequested;

    private long roundStartTick;
    private long segmentStartTick;

    private readonly List<string> results = [];

    private readonly HotkeyWatcher hotkey = new();

    /// <summary>這一輪是誰要求開的（指令／熱鍵）。空字串＝設定面板上的按鈕，不回聊天。</summary>
    private string announceSource = string.Empty;

    /// <summary>繪製端要看的進度字串。<b>只在框架執行緒整個換掉</b>，不共用可變集合。</summary>
    private string progressText = string.Empty;

    private string roundSummary = string.Empty;

    /// <summary>上一輪的結果摘要（指令的 status 用）。這次載入之後還沒跑過就是空字串。</summary>
    public string LastRoundSummary => roundSummary;

    /// <summary>整輪清倉正在跑嗎。全艦隊急停要點名停它，所以這一格是公開的。</summary>
    public bool IsRoundRunning => roundActive;

    /// <summary>整輪現在走到哪（給急停的回報用）。</summary>
    public string RoundProgressText => progressText.Length > 0 ? progressText : "準備中";

    /// <summary>全艦隊急停用：要求整輪停下來。</summary>
    /// <remarks>
    /// 🔴 <b>刻意只設旗標</b>，真正的收尾留給下一個 framework tick：急停可能從指令、熱鍵或
    /// 按鈕進來，後者是繪製執行緒，在那裡叫別的模組收尾並印聊天都不對。
    /// ⚠️ 共用閘門 <see cref="AutomationGate"/> 擋不住這一條：急停冷卻秒數可以設成 0。
    /// </remarks>
    public void RequestRoundStop()
    {
        if (roundActive) stopRequested = true;
    }

    /// <summary>指令／熱鍵的入口：沒在跑就開一輪，正在跑就要求停。</summary>
    /// <remarks>
    /// 🔴 只設旗標，真正的開跑與收尾留給下一個 framework tick。
    /// ⚠️ 只在主執行緒／框架執行緒呼叫（聊天指令派送與 <see cref="OnUpdate"/>）。
    /// </remarks>
    /// <param name="source">誰觸發的（寫進聊天回覆；出事時這是唯一分得出來的線索）。</param>
    /// <returns><see langword="true"/>＝這次是「開始」。</returns>
    public bool ToggleRoundExternally(string source)
    {
        if (StopRoundExternally(source)) return false;

        announceSource = source;
        startRequested = true;
        return true;
    }

    /// <summary>指令的停止入口。本來就沒在跑時回 <see langword="false"/> 且什麼都不做。</summary>
    public bool StopRoundExternally(string source)
    {
        if (!roundActive && !startRequested) return false;

        startRequested = false;
        stopRequested = true;
        Svc.Chat.Print($"[TC Toolbox] 一鍵清倉：已要求停止（{source}）。");
        return true;
    }

    protected override void OnEnable()
    {
        ResetRound();
        roundSummary = string.Empty;
        hotkey.Reset();
        Svc.Framework.Update += OnUpdate;
    }

    public override string? StopForFleetEmergency()
    {
        if (!IsRoundRunning) return null;

        var where = RoundProgressText;
        RequestRoundStop();
        return $"背包一鍵清倉（已要求中止於{where}）";
    }

    protected override void OnDisable()
    {
        Svc.Framework.Update -= OnUpdate;
        hotkey.Reset();

        // quiet＝不印聊天、也不去叫正在跑的那一段停下：模組是從繪製執行緒被關掉的，
        // 而使用者關掉的是這個外層，不是他自己開著的那個模組。
        if (roundActive) FinishRound("模組已關閉", quiet: true);

        ResetRound();
    }

    private void ResetRound()
    {
        roundActive = false;
        startRequested = false;
        stopRequested = false;
        segmentStarted = false;
        segmentIndex = 0;
        plan = [];
        roundStartTick = 0;
        segmentStartTick = 0;
        results.Clear();
        progressText = string.Empty;
        announceSource = string.Empty;
    }

    private void OnUpdate(IFramework framework)
    {
        if (hotkey.Poll(Config.Hotkey)) ToggleRoundExternally("熱鍵");

        if (stopRequested)
        {
            stopRequested = false;
            if (roundActive) FinishRound("已停止：使用者要求中止");
            return;
        }

        if (startRequested)
        {
            startRequested = false;
            StartRound();
            return;
        }

        if (roundActive) RoundStep();
    }

    // ── 開始 ─────────────────────────────────────────────────────────────────

    private void StartRound()
    {
        var source = announceSource;
        announceSource = string.Empty;

        StartRoundCore();

        // 指令與熱鍵那兩條路徑上設定面板通常是關著的：沒有這一句回覆，
        // 「沒開成」與「什麼都沒發生」在使用者眼裡完全一樣。
        if (source.Length == 0) return;

        if (roundActive)
            Svc.Chat.Print($"[TC Toolbox] 一鍵清倉開始（{source}）：{DescribePlan()}。");
        else
            Svc.Chat.PrintError($"[TC Toolbox] 一鍵清倉沒有開始（{source}）：{roundSummary}");
    }

    private void StartRoundCore()
    {
        results.Clear();
        segmentIndex = 0;
        segmentStarted = false;
        roundSummary = string.Empty;
        plan = BuildPlan();

        if (plan.Length == 0)
        {
            roundSummary = "兩段都沒有勾選，這一輪沒有事情可做。";
            return;
        }

        if (TryGetBlockedReason(out var blocked))
        {
            roundSummary = blocked;
            return;
        }

        // 🔴 兩支 IsBusy 端點語意不同，兩支都要問（見 AutoRetainerIpc 的說明）。
        //    AutoRetainer 開起僱員視窗時會接管背包，鞍囊那一段的右鍵選單就會點到別的東西。
        if (TryGetAutoRetainerBusyReason(out var arReason))
        {
            roundSummary = arReason;
            return;
        }

        roundActive = true;
        roundStartTick = Environment.TickCount64;
        UpdateProgressText("準備中");

        Svc.Log.Information(
            $"[{InternalName}] 使用者手動開始一鍵清倉：{plan.Length} 段（{DescribePlan()}）。");
    }

    private Segment[] BuildPlan()
    {
        var list = new List<Segment>(2);
        if (Config.RunSaddlebag) list.Add(Segment.Saddlebag);
        if (Config.RunCabinet) list.Add(Segment.Cabinet);
        return list.ToArray();
    }

    private string DescribePlan()
    {
        var names = new List<string>(plan.Length);
        foreach (var segment in plan) names.Add(DescribeSegment(segment));
        return string.Join("→", names);
    }

    /// <summary>現在有沒有理由不要開一輪（或不要繼續）。</summary>
    /// <remarks>
    /// 🔴 <b>刻意不整包用 <see cref="AutomationGate.TryGetBusyReason"/></b>：
    /// 那道閘門是給無人值守迴圈用的，連 <c>Occupied*</c> 與騎乘也算忙——
    /// 而「站在收藏櫃前、視窗開著」很可能正是那些旗標亮著的狀態，
    /// 整包套下去會讓這顆按鈕永遠拒絕而且沒有徵兆。
    /// 📌 各段自己的前置條件由各段自己判（鞍囊要兩扇視窗、收藏櫃要資料載好）。
    /// </remarks>
    private static bool TryGetBlockedReason(out string reason)
    {
        reason = string.Empty;

        if (!Svc.ClientState.IsLoggedIn || Svc.Objects.LocalPlayer == null)
        {
            reason = "目前不在遊戲中";
            return true;
        }

        if (AutomationGate.TryGetEmergencyStopReason(out var stopReason))
        {
            reason = stopReason;
            return true;
        }

        if (Svc.Condition[ConditionFlag.BetweenAreas] || Svc.Condition[ConditionFlag.BetweenAreas51])
        {
            reason = "正在讀取區域";
            return true;
        }

        if (Svc.Condition[ConditionFlag.LoggingOut])
        {
            reason = "正在登出";
            return true;
        }

        if (Svc.Condition[ConditionFlag.WatchingCutscene] ||
            Svc.Condition[ConditionFlag.WatchingCutscene78] ||
            Svc.Condition[ConditionFlag.OccupiedInCutSceneEvent])
        {
            reason = "正在播放過場動畫";
            return true;
        }

        if (Svc.Condition[ConditionFlag.InCombat])
        {
            reason = "戰鬥中";
            return true;
        }

        if (Svc.Condition[ConditionFlag.Unconscious])
        {
            reason = "已倒地";
            return true;
        }

        if (NavStop.IsEnforcing)
        {
            reason = "正在停止移動";
            return true;
        }

        if (ExternalNav.TryGetActiveMover(out var mover))
        {
            reason = $"{mover} 正在帶著角色移動";
            return true;
        }

        return false;
    }

    /// <summary>AutoRetainer 現在忙不忙。回 <see langword="true"/>＝現在不該開跑。</summary>
    private static bool TryGetAutoRetainerBusyReason(out string reason)
    {
        reason = string.Empty;

        if (AutoRetainerIpc.TryGetIsBusy(out var busy) && busy)
        {
            reason = "AutoRetainer 正在跑自己的作業，等它結束再按一次。";
            return true;
        }

        if (AutoRetainerIpc.TryGetSchedulerRunning(out var scheduled) && scheduled)
        {
            reason = "AutoRetainer 的排程或多角色模式開著，它會接管背包，請先把它關掉再按一次。";
            return true;
        }

        return false;
    }

    // ── 每幀推進一步 ─────────────────────────────────────────────────────────

    private void RoundStep()
    {
        if (TryGetBlockedReason(out var blocked))
        {
            FinishRound($"已停止：{blocked}");
            return;
        }

        var now = Environment.TickCount64;
        if (now - roundStartTick > MaxRoundMs)
        {
            FinishRound("已停止：整輪超過時間上限");
            return;
        }

        if (TryGetAutoRetainerBusyReason(out var arReason))
        {
            FinishRound($"已停止：{arReason}");
            return;
        }

        if (segmentIndex >= plan.Length)
        {
            FinishRound("完成");
            return;
        }

        var segment = plan[segmentIndex];

        if (!segmentStarted)
        {
            if (!TryStartSegment(segment, out var why))
            {
                // 各模組自己的阻擋原因有的帶句號、有的不帶，包進括號前先拿掉。
                why = why.TrimEnd('。');
                results.Add($"{DescribeSegment(segment)}：跳過（{why}）");
                Svc.Log.Information($"[{InternalName}] 跳過「{DescribeSegment(segment)}」：{why}");
                segmentIndex++;
                return;
            }

            segmentStarted = true;
            segmentStartTick = now;
            UpdateProgressText(DescribeSegment(segment));
            Svc.Log.Information($"[{InternalName}] 開始「{DescribeSegment(segment)}」。");
            return;
        }

        if (IsSegmentBusy(segment))
        {
            if (now - segmentStartTick <= Config.SegmentTimeoutMs) return;

            RequestSegmentStop(segment);
            FinishRound(
                $"已停止：「{DescribeSegment(segment)}」超過 {Config.SegmentTimeoutMs / 1_000} 秒還沒結束");
            return;
        }

        var aborted = ReadSegmentResult(segment, out var summary);
        results.Add($"{DescribeSegment(segment)}：{(summary.Length > 0 ? summary : "已結束")}");
        segmentStarted = false;
        segmentIndex++;

        // 🔴 不重試也不往下走：那一段沒正常結束時，後面那一段多半也踩在同一個原因上。
        if (aborted) FinishRound($"已停止：「{DescribeSegment(segment)}」沒有正常結束");
    }

    private void FinishRound(string reason, bool quiet = false)
    {
        // 這是整輪唯一的出口，跑到一半的那一段也要跟著停——不然急停只停到外層。
        if (!quiet && segmentStarted && segmentIndex < plan.Length)
            RequestSegmentStop(plan[segmentIndex]);

        roundActive = false;
        segmentStarted = false;
        progressText = string.Empty;

        roundSummary = results.Count > 0
            ? $"{reason}｜{string.Join("；", results)}"
            : reason;

        Svc.Log.Information($"[{InternalName}] {roundSummary}");

        if (!quiet && Config.NotifyOnFinish)
            Svc.Chat.Print($"[TC Toolbox] 一鍵清倉：{roundSummary}");
    }

    private void UpdateProgressText(string what) =>
        progressText = $"第 {Math.Min(segmentIndex + 1, plan.Length)}/{plan.Length} 段・{what}";

    // ── 各段的轉接 ───────────────────────────────────────────────────────────

    private static string DescribeSegment(Segment segment) => segment switch
    {
        Segment.Saddlebag => "鞍囊重複道具",
        Segment.Cabinet => "收藏櫃可收納裝備",
        _ => "不認得的段落",
    };

    private static T? FindModule<T>() where T : TcModule
    {
        foreach (var module in Plugin.Instance.Modules)
        {
            if (module is T typed) return typed;
        }

        return null;
    }

    private static bool TryStartSegment(Segment segment, out string reason)
    {
        reason = string.Empty;

        switch (segment)
        {
            case Segment.Saddlebag:
            {
                var module = FindModule<SaddlebagEntrustDuplicates>();
                if (module == null)
                {
                    reason = "找不到「陸行鳥鞍囊：寄放重複道具」模組";
                    return false;
                }

                if (!module.IsEnabled)
                {
                    reason = "「陸行鳥鞍囊：寄放重複道具」模組沒有開著";
                    return false;
                }

                return module.TryStartRunExternally(out reason);
            }

            case Segment.Cabinet:
            {
                var module = FindModule<CabinetStoreAll>();
                if (module == null)
                {
                    reason = "找不到「收藏櫃：可收納裝備一鍵存入」模組";
                    return false;
                }

                if (!module.IsEnabled)
                {
                    reason = "「收藏櫃：可收納裝備一鍵存入」模組沒有開著";
                    return false;
                }

                return module.TryStartRunExternally(out reason);
            }

            default:
                reason = "不認得的段落";
                return false;
        }
    }

    private static bool IsSegmentBusy(Segment segment) => segment switch
    {
        Segment.Saddlebag => FindModule<SaddlebagEntrustDuplicates>()?.IsRunBusy ?? false,
        Segment.Cabinet => FindModule<CabinetStoreAll>()?.IsRunBusy ?? false,
        _ => false,
    };

    /// <summary>讀出那一段的結果。回 <see langword="true"/>＝那一段沒有正常結束。</summary>
    private static bool ReadSegmentResult(Segment segment, out string summary)
    {
        switch (segment)
        {
            case Segment.Saddlebag:
            {
                var module = FindModule<SaddlebagEntrustDuplicates>();
                summary = module?.LastRunSummary ?? string.Empty;
                return module?.LastRunAborted ?? false;
            }

            case Segment.Cabinet:
            {
                var module = FindModule<CabinetStoreAll>();
                summary = module?.LastRunSummary ?? string.Empty;
                return module?.LastRunAborted ?? false;
            }

            default:
                summary = string.Empty;
                return true;
        }
    }

    private static void RequestSegmentStop(Segment segment)
    {
        switch (segment)
        {
            case Segment.Saddlebag:
                FindModule<SaddlebagEntrustDuplicates>()?.RequestStopExternally();
                break;

            case Segment.Cabinet:
                FindModule<CabinetStoreAll>()?.RequestStopExternally();
                break;
        }
    }

    // ── 狀態與 UI ────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    /// <remarks>清倉跑起來之後設定面板通常是收著的，所以進度要在模組列上看得見。</remarks>
    public override ModuleNotice? RowNotice =>
        roundActive
            ? new ModuleNotice(ModuleNoticeLevel.Warning, "清倉中",
                               $"{RoundProgressText}\n設定面板上有停止鈕；全艦隊急停也停得掉。")
            : null;

    public override void DrawConfig()
    {
        ImGui.TextUnformatted("按一次依序跑完下面勾選的段落。每一段都是那個模組自己的按鈕，這裡只負責排隊。");
        ImGui.Spacing();

        DrawSegmentToggle(
            "陸行鳥鞍囊：寄放重複道具##declutterSaddlebag",
            Config.RunSaddlebag,
            value =>
            {
                Config.RunSaddlebag = value;
                Plugin.Instance.Config.Save();
            },
            FindModule<SaddlebagEntrustDuplicates>()?.IsEnabled ?? false,
            "把背包裡「鞍囊已經有同一款」的道具整堆放進鞍囊。\n" +
            "需要同時開著陸行鳥鞍囊視窗與背包視窗，沒開就跳過這一段。");

        DrawSegmentToggle(
            "收藏櫃：可收納裝備一鍵存入##declutterCabinet",
            Config.RunCabinet,
            value =>
            {
                Config.RunCabinet = value;
                Plugin.Instance.Config.Save();
            },
            FindModule<CabinetStoreAll>()?.IsEnabled ?? false,
            "把背包與兵裝庫裡可以收進收藏櫃的裝備依序存入。\n" +
            "需要站在收藏櫃前並開著收藏櫃視窗，沒開就跳過這一段。\n" +
            "染色／武具投影／精煉度／魔晶石／耐久不足的一律由那個模組自己跳過。");

        ImGui.Spacing();

        if (roundActive)
        {
            if (ImGui.Button("停止清倉##declutterStop")) stopRequested = true;

            ImGui.SameLine();
            ImGui.TextColored(new Vector4(1f, 0.85f, 0.35f, 1f), RoundProgressText);
        }
        else
        {
            if (ImGui.Button("開始清倉##declutterStart")) startRequested = true;

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    "依序跑完勾選的段落。某一段的視窗沒開、模組沒開著、或沒東西可做時\n" +
                    "會跳過並寫明原因，不算失敗；真的跑到一半出事才會停下來。");
            }
        }

        ImGui.Spacing();
        ImGui.SetNextItemWidth(200f);
        var seconds = Math.Max(1, Config.SegmentTimeoutMs / 1_000);
        if (ImGui.SliderInt("單段時間上限（秒）##declutterTimeout", ref seconds, 60, 900))
        {
            Config.SegmentTimeoutMs = seconds * 1_000;
            Plugin.Instance.Config.Save();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "某一段跑超過這個時間就整輪停下來，不重試。\n" +
                "這是外層的保險絲：真的卡住由各模組自己的單步逾時抓。\n" +
                "鞍囊那一段件數多時本來就要幾分鐘，被砍掉的話請往上調。");
        }

        var notify = Config.NotifyOnFinish;
        if (ImGui.Checkbox("結束時在聊天欄報告##declutterNotify", ref notify))
        {
            Config.NotifyOnFinish = notify;
            Plugin.Instance.Config.Save();
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("熱鍵（按一次開始，跑的時候再按一次就是停止）");
        if (HotkeyUi.Draw("InventoryDeclutter", Config.Hotkey))
            Plugin.Instance.Config.Save();

        ImGui.TextDisabled($"聊天指令：{DeclutterCommand.Command}（ui／stop／status）");

        if (roundSummary.Length > 0)
            ImGui.TextDisabled($"上次結果：{roundSummary}");

        ImGui.TextColored(new Vector4(1f, 0.8f, 0.35f, 1f),
                          "⚠ 只搬去拿得回來的地方：不丟棄、不碰市場、不動幻化櫃與時裝管理。");
    }

    /// <summary>畫一段的勾選框；那個模組沒開著時在同一列講出來，不要讓使用者按了才發現。</summary>
    private static void DrawSegmentToggle(
        string label, bool value, Action<bool> setter, bool moduleEnabled, string tooltip)
    {
        var current = value;
        if (ImGui.Checkbox(label, ref current)) setter(current);

        if (ImGui.IsItemHovered()) ImGui.SetTooltip(tooltip);

        if (current && !moduleEnabled)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(1f, 0.55f, 0.35f, 1f), "（那個模組沒開著，會跳過）");
        }
    }
}

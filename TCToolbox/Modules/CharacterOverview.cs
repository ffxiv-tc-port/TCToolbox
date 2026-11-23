using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;
using TCToolbox.Core;
using TCToolbox.Windows;

namespace TCToolbox.Modules;

/// <summary>
/// 角色總覽：一頁列出 AutoRetainer 登錄的每個角色「下一件事何時到」，附「換到這角」；另可在分隊歸隊、訓練結束時提醒。
/// </summary>
/// <remarks>
/// 本檔假設與契約：
/// - 只讀。換角走 <see cref="CharacterSwitch.Request"/>（與 <c>/tcswitch</c> 同一條路），不自己登出登入。
/// - IPC 與遊戲讀取全在 <c>Framework.Update</c>；繪製只讀整份換掉的 <see cref="rows"/>。視窗關著時不打 AutoRetainer。
/// - 分隊狀態讀自玩家狀態（登入中的角色），存值不存指標；別的角色用上次登入時存下的值。
/// - 不知道的格子一律畫「?」，原因放滑鼠提示。
/// </remarks>
public sealed unsafe class CharacterOverview : TcModule
{
    public const string Command = "/tcchars";

    public override string InternalName => "CharacterOverview";

    public override string DisplayName => "角色總覽";

    public override string Description =>
        "一頁看 AutoRetainer 登錄的所有角色：僱員最早回來、潛水艇最早返航、分隊歸隊與能不能訓練、公會特效庫存、背包空格，" +
        "依「下一件事最早到」排序，每列一顆「換到這角」（與 /tcswitch 相同）。" +
        $"指令 {Command} 開啟視窗。另可選擇在分隊歸隊、訓練結束時於聊天欄提醒（預設關）。";

    public override ModuleCategory Category => ModuleCategory.Company;

    /// <inheritdoc/>
    /// <remarks>開著只是多一扇可開的視窗；提醒只印聊天，不動遊戲。</remarks>
    public override bool IsManualTrigger => true;

    public override bool HasConfigUI => true;

    private const string NeedAutoRetainerText = "需要 AutoRetainer";

    /// <summary>分隊解鎖任務「榮光與分隊同在」三國各一（遊戲自己顯示分隊狀態前也是查這三個）。</summary>
    private static readonly ushort[] SquadronUnlockQuests = [67925 & 0xFFFF, 67926 & 0xFFFF, 67927 & 0xFFFF];

    private const long PresenceIntervalMs = 2_000;
    private const long TimersIntervalMs = 30_000;
    private const long CharacterDataIntervalMs = 120_000;
    private const long SquadronIntervalMs = 5_000;

    /// <summary>換角後等這麼久才讀分隊狀態：剛登入時玩家狀態可能還是上一個角色或還沒到齊。</summary>
    private const long SquadronSettleMs = 10_000;

    private static readonly TimeSpan SquadronResaveInterval = TimeSpan.FromMinutes(30);

    private static readonly Vector4 ReadyColor = new(0.5f, 1f, 0.5f, 1f);
    private static readonly Vector4 WarnColor = new(1f, 0.75f, 0.35f, 1f);

    private CharacterOverviewConfig Config => Plugin.Instance.Config.CharacterOverview;

    private CharacterOverviewWindow? window;

    private enum Tone
    {
        Normal = 0,

        /// <summary>不知道或沒有（灰字）。</summary>
        Dim,

        /// <summary>現在就能做（綠字）。</summary>
        Ready,

        /// <summary>要注意（橘字）。</summary>
        Warn,
    }

    private readonly record struct Cell(string Text, Tone Tone, string Tip);

    /// <summary>畫面上的一列（Update 算好；繪製只讀）。</summary>
    private sealed record Row(ulong Cid, string FullName, bool IsCurrent, Cell Next, Cell Retainer, Cell Vessel,
        Cell Mission, Cell Training, Cell FcStock, Cell Inventory);

    /// <summary>AutoRetainer 那邊一個角色的資料（值複本）。</summary>
    private sealed record CharacterData(string Name, string World, uint? InventorySpace,
        IReadOnlySet<string>? EnabledVessels, DateTime ReadUtc);

    // ── 只在框架執行緒讀寫 ─────────────────────────────────────────────────

    private bool? arLoaded;
    private long nextPresenceAt;
    private long nextTimersAt;
    private long nextCharacterDataAt;
    private long nextSquadronAt;
    private bool refreshRequest;

    private List<ulong> roster = [];
    private Dictionary<ulong, AutoRetainerIpc.CharacterTimers> timers = [];
    private AutoRetainerIpc.TimersFailure timersFailure;
    private string timersError = string.Empty;
    private DateTime? timersReadUtc;

    private readonly Dictionary<ulong, CharacterData> characterData = [];
    private readonly Queue<ulong> characterDataQueue = new();
    private bool characterDataFailed;

    private string? switchBlocked = "尚未檢查";
    private string? pendingSwitch;

    private ulong observedCid;
    private long observedSince;
    private readonly Dictionary<ulong, DateTime> squadronSavedAt = [];

    private DateTime remindArmedUtc;
    private readonly HashSet<(ulong Cid, string Kind, DateTime EndUtc)> reminded = [];

    private readonly Dictionary<uint, string> fcActionNames = [];

    /// <summary>繪製用的列，整份換掉。</summary>
    private IReadOnlyList<Row> rows = [];

    public override ModuleNotice? RowNotice =>
        IsEnabled && arLoaded == false
            ? new ModuleNotice(ModuleNoticeLevel.Warning, NeedAutoRetainerText, "沒有偵測到 AutoRetainer（未安裝或未載入），角色清單來自它。")
            : null;

    protected override void OnEnable()
    {
        ResetState();
        remindArmedUtc = DateTime.UtcNow;

        window = new CharacterOverviewWindow(this);
        Plugin.Instance.WindowSystem.AddWindow(window);

        Svc.Commands.AddHandler(Command, new CommandInfo((_, _) => ToggleWindow())
        {
            HelpMessage = "開啟「角色總覽」視窗",
        });

        Svc.Framework.Update += OnUpdate;
    }

    protected override void OnDisable()
    {
        Svc.Framework.Update -= OnUpdate;
        Svc.Commands.RemoveHandler(Command);

        if (window != null)
        {
            // 外掛卸載時 Plugin.Dispose 先 RemoveAllWindows() 才停用模組，對不在清單裡的視窗 RemoveWindow 會擲例外。
            if (Plugin.Instance.WindowSystem.Windows.Contains(window))
                Plugin.Instance.WindowSystem.RemoveWindow(window);

            window.IsOpen = false;
            window = null;
        }

        ResetState();
    }

    private void ResetState()
    {
        arLoaded = null;
        nextPresenceAt = nextTimersAt = nextCharacterDataAt = nextSquadronAt = 0;
        refreshRequest = false;
        roster = [];
        timers = [];
        timersFailure = AutoRetainerIpc.TimersFailure.None;
        timersError = string.Empty;
        timersReadUtc = null;
        characterData.Clear();
        characterDataQueue.Clear();
        characterDataFailed = false;
        switchBlocked = "尚未檢查";
        pendingSwitch = null;
        observedCid = 0;
        observedSince = 0;
        squadronSavedAt.Clear();
        reminded.Clear();
        rows = [];
    }

    private void ToggleWindow()
    {
        if (window != null) window.IsOpen = !window.IsOpen;
    }

    internal void OnWindowOpened() => refreshRequest = true;

    private bool WindowOpen => window?.IsOpen == true;

    #region 每幀

    private void OnUpdate(IFramework framework)
    {
        try
        {
            Tick();
        }
        catch (Exception ex)
        {
            // 同一插件的 Update 處理器共用一個 try/catch：這裡擲出去會讓排在後面的模組這一幀全跳過。
            if (Throttle.Pass($"{InternalName}-Update", 60_000))
                Svc.Log.Information($"[{InternalName}] 更新時發生例外（已忽略）：{ex}");
        }
    }

    private void Tick()
    {
        var tick = Environment.TickCount64;

        if (pendingSwitch is { } target)
        {
            pendingSwitch = null;
            CharacterSwitch.Request(target, InternalName);
            nextPresenceAt = 0;
        }

        if (tick >= nextPresenceAt)
        {
            nextPresenceAt = tick + PresenceIntervalMs;
            arLoaded = PluginPresence.IsLoaded("AutoRetainer");
            if (WindowOpen)
                switchBlocked = arLoaded == false ? "沒有偵測到 AutoRetainer。" : CharacterSwitch.GetBlockedReason();
        }

        if (tick >= nextSquadronAt)
        {
            nextSquadronAt = tick + SquadronIntervalMs;
            RecordSquadron(tick);
            if (Config.RemindSquadron) CheckReminders();
        }

        if (!WindowOpen) return;

        if (arLoaded != false && (refreshRequest || tick >= nextTimersAt))
        {
            RefreshTimers(tick);
            if (refreshRequest || tick >= nextCharacterDataAt)
            {
                nextCharacterDataAt = tick + CharacterDataIntervalMs;
                characterDataQueue.Clear();
                foreach (var cid in roster) characterDataQueue.Enqueue(cid);
            }

            refreshRequest = false;
        }

        // 每幀最多問一個角色：每次都是整份角色資料的 JSON 來回。
        if (arLoaded != false && characterDataQueue.TryDequeue(out var next)) FetchCharacterData(next);

        if (Throttle.Pass($"{InternalName}-Rows", 1_000)) RebuildRows();
    }

    private void RefreshTimers(long tick)
    {
        nextTimersAt = tick + TimersIntervalMs;

        var list = AutoRetainerIpc.GetCharacterTimers(out var failure, out var error);
        timersFailure = failure;
        timersError = error;

        if (list != null)
        {
            timers = list.GroupBy(x => x.Cid).ToDictionary(g => g.Key, g => g.First());
            roster = list.Select(x => x.Cid).Distinct().ToList();
            timersReadUtc = DateTime.UtcNow;
            return;
        }

        // 這次沒答案就沿用上一份；端點根本不在（舊版）時退回只有角色清單。
        if (failure == AutoRetainerIpc.TimersFailure.NoAnswer) return;

        timers = [];
        timersReadUtc = null;
        roster = AutoRetainerIpc.GetRegisteredCharacters();
    }

    private void FetchCharacterData(ulong cid)
    {
        if (!AutoRetainerIpc.TryGetCharacterInfo(cid, out var info))
        {
            characterDataFailed = true;
            characterDataQueue.Clear();
            return;
        }

        characterDataFailed = false;
        if (info == null)
        {
            characterData.Remove(cid);
            return;
        }

        HashSet<string>? enabled = null;
        if (info.EnabledSubs != null || info.EnabledAirships != null)
        {
            enabled = [];
            if (info.EnabledSubs != null) enabled.UnionWith(info.EnabledSubs);
            if (info.EnabledAirships != null) enabled.UnionWith(info.EnabledAirships);
        }

        characterData[cid] = new CharacterData(info.Name ?? string.Empty, info.World ?? string.Empty,
            info.InventorySpace == uint.MaxValue ? null : info.InventorySpace, enabled, DateTime.UtcNow);
    }

    #endregion

    #region 分隊狀態與提醒

    /// <summary>讀登入中角色的分隊任務與訓練，有變才存檔。</summary>
    private void RecordSquadron(long tick)
    {
        var cid = Svc.PlayerState.ContentId;
        if (cid == 0 || !Svc.PlayerState.IsLoaded || !Svc.ClientState.IsLoggedIn)
        {
            observedCid = 0;
            return;
        }

        if (cid != observedCid)
        {
            observedCid = cid;
            observedSince = tick;
            return;
        }

        if (tick - observedSince < SquadronSettleMs) return;

        PlayerState* ps;
        try
        {
            ps = PlayerState.Instance();
        }
        catch (InvalidOperationException)
        {
            return;
        }

        if (ps == null) return;

        var missionId = ps->ActiveGcArmyExpedition;
        var missionEnd = (uint)ps->SquadronMissionCompletionTimestamp;
        var trainingId = ps->ActiveGcArmyTraining;
        var trainingEnd = (uint)ps->SquadronTrainingCompletionTimestamp;
        var unlocked = SquadronUnlocked();

        var store = Config.Squadron;
        var isNew = !store.TryGetValue(cid, out var s) || s == null;
        if (s == null)
        {
            s = new SquadronSnapshot();
            store[cid] = s;
        }

        var changed = isNew || s.MissionId != missionId || s.MissionEnd != missionEnd || s.TrainingId != trainingId ||
                      s.TrainingEnd != trainingEnd || s.Unlocked != unlocked;

        var now = DateTime.UtcNow;
        s.Character = $"{Svc.PlayerState.CharacterName}＠{Svc.PlayerState.HomeWorld.ValueNullable?.Name.ExtractText()}";
        s.MissionId = missionId;
        s.MissionEnd = missionEnd;
        s.TrainingId = trainingId;
        s.TrainingEnd = trainingEnd;
        s.Unlocked = unlocked;
        s.ReadUtc = now;

        if (!changed && squadronSavedAt.TryGetValue(cid, out var saved) && now - saved < SquadronResaveInterval) return;

        squadronSavedAt[cid] = now;
        Plugin.Instance.Config.Save();
        if (changed)
        {
            Svc.Log.Information(
                $"[{InternalName}] 分隊狀態（CID {cid:X}）：任務 {missionId}／結束 {missionEnd}、訓練 {trainingId}／結束 {trainingEnd}、已解鎖 {unlocked?.ToString() ?? "?"}");
        }
    }

    /// <summary>完成三國任一個分隊解鎖任務沒有；問不到回 <see langword="null"/>。</summary>
    private static bool? SquadronUnlocked()
    {
        try
        {
            foreach (var quest in SquadronUnlockQuests)
            {
                if (QuestManager.IsQuestComplete(quest)) return true;
            }

            return false;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>派遣確認框紀錄與玩家狀態快照的聯集，逐一取比較新的那份。</summary>
    private IEnumerable<(ulong Cid, string Label, SquadronView View)> SquadronViews(DateTime nowUtc)
    {
        var snaps = Config.Squadron;
        var records = Plugin.Instance.Config.SquadronRoutine.Returns;
        foreach (var cid in snaps.Keys.Union(records.Keys))
        {
            snaps.TryGetValue(cid, out var snap);
            records.TryGetValue(cid, out var record);
            var label = snap?.Character is { Length: > 0 } a ? a : record?.Character ?? $"CID {cid:X}";
            yield return (cid, label, CharacterOverviewModel.Squadron(snap, record, nowUtc));
        }
    }

    /// <summary>啟用（或打開提醒）之後才到期的歸隊／訓練結束，各提醒一次。</summary>
    private void CheckReminders()
    {
        var now = DateTime.UtcNow;
        var anyMission = false;
        var anyTraining = false;

        foreach (var (cid, label, v) in SquadronViews(now).ToList())
        {
            if (v.Mission is SquadronMissionState.Away or SquadronMissionState.Returned && v.MissionEndUtc is { } m &&
                m > remindArmedUtc && m <= now && reminded.Add((cid, "mission", m)))
            {
                anyMission = true;
                Svc.Chat.Print($"[TC Toolbox] {label} 的分隊歸隊了（{m.ToLocalTime():HH:mm}），可以去軍營領任務報告。");
                Svc.Log.Information($"[{InternalName}] 提醒：分隊歸隊（CID {cid:X}，{m.ToLocalTime():MM/dd HH:mm}）");
            }

            if (v.Training is SquadronTrainingState.Training or SquadronTrainingState.Done && v.TrainingEndUtc is { } t &&
                t > remindArmedUtc && t <= now && reminded.Add((cid, "training", t)))
            {
                anyTraining = true;
                Svc.Chat.Print($"[TC Toolbox] {label} 的分隊訓練結束了（{t.ToLocalTime():HH:mm}），可以去軍營看結果。");
                Svc.Log.Information($"[{InternalName}] 提醒：訓練結束（CID {cid:X}，{t.ToLocalTime():MM/dd HH:mm}）");
            }
        }

        if (Config.RemindWithTataru && (anyMission || anyTraining))
        {
            TataruPraiseIpc.TrySpeak(anyMission ? "分隊回來了，記得去軍營看看喔！" : "分隊訓練結束了，記得去軍營看看喔！",
                $"{InternalName} 分隊提醒");
        }
    }

    #endregion

    #region 組列

    private void RebuildRows()
    {
        var now = DateTime.UtcNow;
        var localCid = Svc.PlayerState.ContentId;
        var snaps = Config.Squadron;
        var records = Plugin.Instance.Config.SquadronRoutine.Returns;
        var stock = Plugin.Instance.Config.FcActionAutoUse.StockByCharacter;

        var built = new List<(Row Row, NextEvent? Next, bool Known, int Index)>(roster.Count);
        for (var i = 0; i < roster.Count; i++)
        {
            var cid = roster[i];
            timers.TryGetValue(cid, out var t);
            characterData.TryGetValue(cid, out var data);
            snaps.TryGetValue(cid, out var snap);
            records.TryGetValue(cid, out var record);
            stock.TryGetValue(cid, out var fc);

            var name = t?.Name is { Length: > 0 } n ? n : data?.Name ?? string.Empty;
            var world = t?.World is { Length: > 0 } w ? w : data?.World ?? string.Empty;
            var fullName = name.Length > 0 && world.Length > 0 ? $"{name}@{world}" : string.Empty;

            var (retainerCell, retainerEvent) = RetainerCell(t, now);
            var (vesselCell, vesselEvent) = VesselCell(t, data, now);
            var squadron = CharacterOverviewModel.Squadron(snap, record, now);
            var next = CharacterOverviewModel.Earliest([retainerEvent, vesselEvent, CharacterOverviewModel.SquadronEvent(squadron)]);
            var known = t != null || squadron.Mission != SquadronMissionState.Unknown;

            var row = new Row(cid, fullName, cid == localCid, NextCell(next, known, now), retainerCell, vesselCell,
                MissionCell(squadron, record, now), TrainingCell(squadron, now), FcCell(fc, now), InventoryCell(data, now));
            built.Add((row, next, known, i));
        }

        built.Sort((a, b) => CharacterOverviewModel.Compare(a.Next, a.Known, a.Index, b.Next, b.Known, b.Index));
        rows = built.Select(x => x.Row).ToList();
    }

    private static Cell NextCell(NextEvent? next, bool known, DateTime now)
    {
        if (next is not { } e)
        {
            return known
                ? new Cell("—", Tone.Dim, "知道的部分都沒有進行中的探險、航行或分隊任務。")
                : new Cell("?", Tone.Dim, "這個角色的資料都還沒讀到。");
        }

        var tip = $"{e.What}：{e.Utc.ToLocalTime():MM/dd HH:mm}";
        return CharacterOverviewModel.Remaining(e.Utc, now) is { } r
            ? new Cell($"{e.What} {r}", Tone.Normal, tip)
            : new Cell($"現在：{e.What}", Tone.Ready, tip);
    }

    private (Cell, NextEvent?) RetainerCell(AutoRetainerIpc.CharacterTimers? t, DateTime now)
    {
        if (t?.Retainers is not { } list)
            return (new Cell("?", Tone.Dim, TimersUnknownReason(t)), null);

        if (list.Length == 0)
            return (new Cell("—", Tone.Dim, "AutoRetainer 沒有記到這個角色的僱員。"), null);

        var (onVenture, earliest) = CharacterOverviewModel.Ventures(list.Select(x => (x.HasVenture, x.VentureEndsAt)).ToList());
        if (earliest is not { } e)
            return (new Cell("—", Tone.Dim, $"共 {list.Length} 位僱員，都沒有進行中的探險。"), null);

        var tip = $"探險中 {onVenture}／共 {list.Length} 位；最早 {e.ToLocalTime():MM/dd HH:mm} 回來。\n資料來自 AutoRetainer。";
        var cell = CharacterOverviewModel.Remaining(e, now) is { } r
            ? new Cell(r, Tone.Normal, tip)
            : new Cell("探險歸來", Tone.Ready, tip);
        return (cell, new NextEvent(e, "僱員"));
    }

    private (Cell, NextEvent?) VesselCell(AutoRetainerIpc.CharacterTimers? t, CharacterData? data, DateTime now)
    {
        if (t?.Submarines == null && t?.Airships == null)
            return (new Cell("?", Tone.Dim, TimersUnknownReason(t)), null);

        var all = new List<(string Name, uint ReturnTime, bool Airship)>();
        foreach (var v in t.Submarines ?? []) all.Add((v.Name, v.ReturnTime, false));
        foreach (var v in t.Airships ?? []) all.Add((v.Name, v.ReturnTime, true));

        if (all.Count == 0)
            return (new Cell("—", Tone.Dim, "AutoRetainer 沒有記到這個角色的潛水艇或飛空艇。"), null);

        var enabled = data?.EnabledVessels;
        var (voyaging, earliest, docked) = CharacterOverviewModel.Vessels(all.Select(x => (x.Name, x.ReturnTime)).ToList(), enabled);

        var sb = new StringBuilder();
        foreach (var (vname, ret, airship) in all.OrderBy(x => x.ReturnTime == 0 ? uint.MaxValue : x.ReturnTime))
        {
            sb.Append(airship ? "飛空艇 " : "潛水艇 ").Append(vname).Append('：');
            if (ret == 0)
                sb.Append(enabled == null ? "停在船塢" : enabled.Contains(vname) ? "停在船塢（有勾選派遣）" : "停在船塢（沒勾選派遣）");
            else
            {
                var at = CharacterOverviewModel.FromUnix(ret);
                sb.Append(CharacterOverviewModel.Remaining(at, now) is { } r ? $"{r}後返航" : "已返航")
                    .Append($"（{at.ToLocalTime():MM/dd HH:mm}）");
            }

            sb.Append('\n');
        }

        if (enabled == null)
            sb.Append("還沒讀到哪些有勾選給 AutoRetainer 派遣，停在船塢的不算進「下一件事」。\n");
        sb.Append("資料來自 AutoRetainer。");
        var tip = sb.ToString();

        NextEvent? arrival = earliest is { } e ? new NextEvent(e, "返航") : null;
        NextEvent? dock = docked > 0 ? new NextEvent(now, "潛水艇可出航") : null;
        var next = CharacterOverviewModel.Earliest([arrival, dock]);

        if (earliest is { } first && CharacterOverviewModel.Remaining(first, now) == null)
            return (new Cell("已返航", Tone.Ready, tip), next);
        if (docked > 0)
            return (new Cell($"可出航 {docked}", Tone.Ready, tip), next);
        if (earliest is { } soon)
            return (new Cell(CharacterOverviewModel.Remaining(soon, now) ?? "已返航", Tone.Normal, tip), next);

        return (new Cell("—", Tone.Dim, tip), null);
    }

    private string TimersUnknownReason(AutoRetainerIpc.CharacterTimers? t)
    {
        if (t != null) return "AutoRetainer 沒有這份資料。";

        return timersFailure switch
        {
            AutoRetainerIpc.TimersFailure.NoEndpoint => "AutoRetainer 沒有提供各角色的計時資料（版本太舊或未載入），請更新 AutoRetainer。",
            AutoRetainerIpc.TimersFailure.Failed => $"向 AutoRetainer 問各角色的計時資料失敗（{timersError}）。",
            _ => timersReadUtc == null ? "還沒向 AutoRetainer 問過，幾秒內會更新。" : "AutoRetainer 的計時資料裡沒有這個角色。",
        };
    }

    private static Cell MissionCell(in SquadronView v, SquadronReturnRecord? record, DateTime now)
    {
        var source = v.ReadUtc is { } read ? $"\n來源：{v.Source}，{CharacterOverviewModel.Age(read, now)}讀到。" : string.Empty;
        switch (v.Mission)
        {
            case SquadronMissionState.NotUnlocked:
                return new Cell("未解鎖", Tone.Dim, "讀到時這個角色還沒完成冒險者分隊的解鎖任務。" + source);
            case SquadronMissionState.Idle:
                return new Cell("待命", Tone.Normal, "讀到時沒有派遣中的分隊任務。" + source);
            case SquadronMissionState.Away when v.MissionEndUtc is { } end:
                return new Cell(CharacterOverviewModel.Remaining(end, now) ?? "已歸隊", Tone.Normal,
                    $"派遣中，{end.ToLocalTime():MM/dd HH:mm} 歸隊。" + source);
            case SquadronMissionState.Returned when v.MissionEndUtc is { } end:
                return new Cell("已歸隊", Tone.Ready, $"{end.ToLocalTime():MM/dd HH:mm} 已歸隊，任務報告還沒領（以讀到時為準）。" + source);
        }

        var tip = "還沒讀到這個角色的分隊狀態：開著這個模組登入這個角色一次就會記下，" +
                  "或在「國防聯軍分隊」模組開著時於軍營派遣。";
        if (record is { Confirmed: false })
            tip += $"\n（有一筆派遣確認框紀錄：{record.Mission}，{record.ReturnUtc.ToLocalTime():MM/dd HH:mm} 歸隊，但沒看到開始訊息，可能按了否。）";
        return new Cell("?", Tone.Dim, tip);
    }

    private static Cell TrainingCell(in SquadronView v, DateTime now)
    {
        var source = v.ReadUtc is { } read ? $"\n來源：{v.Source}，{CharacterOverviewModel.Age(read, now)}讀到。" : string.Empty;

        if (v.Mission == SquadronMissionState.NotUnlocked)
            return new Cell("—", Tone.Dim, "冒險者分隊還沒解鎖。" + source);
        if (v.Training == SquadronTrainingState.Training && v.TrainingEndUtc is { } end)
        {
            return new Cell($"訓練中 {CharacterOverviewModel.Remaining(end, now) ?? string.Empty}".TrimEnd(), Tone.Normal,
                $"訓練中，{end.ToLocalTime():MM/dd HH:mm} 結束；結束前不能再開訓練。" + source);
        }

        if (v.Mission == SquadronMissionState.Away)
            return new Cell("派遣中", Tone.Dim, "分隊任務進行中不能訓練，歸隊後才能開始。" + source);

        return v.CanTrain switch
        {
            true => new Cell("可以", Tone.Ready, "讀到時沒有進行中的任務或訓練。" + source),
            _ when v.Training == SquadronTrainingState.Done => new Cell("?", Tone.Dim,
                $"訓練已在 {v.TrainingEndUtc?.ToLocalTime():MM/dd HH:mm} 結束、結果還沒看；看完結果才確定能不能開下一次。" + source),
            _ when v.Mission == SquadronMissionState.Returned => new Cell("?", Tone.Dim,
                "分隊已歸隊、任務報告還沒領；領完報告才確定能不能訓練。" + source),
            _ when v.Training == SquadronTrainingState.Unknown && v.Source == "派遣確認框" => new Cell("?", Tone.Dim,
                "只有派遣確認框的紀錄，看不到訓練狀態；開著這個模組登入這個角色一次就會記下。" + source),
            _ => new Cell("?", Tone.Dim, "還沒讀到這個角色的分隊狀態。" + source),
        };
    }

    private Cell FcCell(FcStockRecord? record, DateTime now)
    {
        if (record == null)
        {
            return new Cell("?", Tone.Dim,
                "還沒讀到這個角色的公會特效庫存：要開「公會特效自動使用」模組，並在遊戲的公會特效分頁看過一次。");
        }

        var total = record.Counts?.Values.Sum() ?? 0;
        var sb = new StringBuilder($"公會特效庫存（約），{CharacterOverviewModel.Age(record.ReadUtc, now)}讀到：");
        if (record.Counts == null || total == 0)
        {
            sb.Append("\n沒有庫存。");
        }
        else
        {
            foreach (var (actionId, count) in record.Counts.OrderBy(x => x.Key))
                sb.Append('\n').Append(FcActionName(actionId)).Append('：').Append(count);
        }

        sb.Append("\n數量照庫存清單的筆數算，還沒在實機確認每一份是不是各佔一筆，所以標「約」。");
        return new Cell(total == 0 ? "0" : $"約 {total}", total == 0 ? Tone.Warn : Tone.Normal, sb.ToString());
    }

    private string FcActionName(uint actionId)
    {
        if (fcActionNames.TryGetValue(actionId, out var cached)) return cached;

        var name = Svc.Data.GetExcelSheet<CompanyAction>().GetRowOrDefault(actionId)?.Name.ExtractText() is { Length: > 0 } s
            ? s
            : $"#{actionId}";
        fcActionNames[actionId] = name;
        return name;
    }

    private Cell InventoryCell(CharacterData? data, DateTime now)
    {
        if (data == null)
        {
            return new Cell("?", Tone.Dim, characterDataFailed
                ? "向 AutoRetainer 問角色資料失敗。"
                : "還沒向 AutoRetainer 問到這個角色的資料，稍等一下會更新。");
        }

        if (data.InventorySpace is not { } space)
            return new Cell("?", Tone.Dim, "AutoRetainer 的角色資料裡沒有背包空格（版本太舊）。");

        return new Cell(space.ToString(), space <= 5 ? Tone.Warn : Tone.Normal,
            $"背包剩 {space} 格。AutoRetainer 記下的值：它在傳喚鈴前、自己工作時與登出時更新。\n" +
            $"{CharacterOverviewModel.Age(data.ReadUtc, now)}向 AutoRetainer 問到。");
    }

    #endregion

    #region 畫面

    internal void DrawPanel()
    {
        if (arLoaded == false)
        {
            ImGui.TextColored(WarnColor, NeedAutoRetainerText);
            ImGui.TextDisabled("角色清單、僱員與潛水艇的時間都來自 AutoRetainer；安裝並載入後這裡會自己更新。");
            return;
        }

        var now = DateTime.UtcNow;
        ImGui.TextDisabled(timersReadUtc is { } read
            ? $"共 {rows.Count} 個角色，依下一件事排序；{CharacterOverviewModel.Age(read, now)}向 AutoRetainer 更新。"
            : $"共 {rows.Count} 個角色；還沒向 AutoRetainer 問到計時資料。");
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "視窗開著時每 30 秒向 AutoRetainer 問一次；分隊狀態在角色登入時讀遊戲記下，別的角色用上次登入時的值。\n" +
                "滑鼠移到每一格上看細節；「?」＝不知道，原因也在滑鼠提示裡。");
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("立即更新##charOverviewRefresh")) refreshRequest = true;

        if (rows.Count == 0)
        {
            ImGui.TextDisabled("AutoRetainer 還沒有登錄任何角色（或還沒問到）。");
            return;
        }

        DrawTable(now);
    }

    private static readonly (string Name, string Tip, float Width)[] Columns =
    [
        ("角色", "AutoRetainer 登錄的角色。", 0f),
        ("下一件事", "這個角色接下來最早到的事；「現在」＝已經可以做了。", 130f),
        ("僱員", "最早回來的那位僱員還要多久。", 90f),
        ("潛水艇", "最早返航的潛水艇（含飛空艇）還要多久；停在船塢且有勾選派遣的算「可出航」。", 90f),
        ("分隊", "冒險者分隊任務：還要多久歸隊。", 90f),
        ("訓練", "現在能不能開始分隊訓練。", 100f),
        ("公會特效", "公會特效庫存（約）。", 70f),
        ("背包", "背包空格（AutoRetainer 記下的值）。", 50f),
        ("##switch", "", 90f),
    ];

    private void DrawTable(DateTime now)
    {
        if (!ImGui.BeginTable("##charOverview", Columns.Length,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.ScrollY))
            return;

        try
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            foreach (var (name, _, width) in Columns)
            {
                if (width > 0) ImGui.TableSetupColumn(name, ImGuiTableColumnFlags.WidthFixed, UiScale.Px(width));
                else ImGui.TableSetupColumn(name, ImGuiTableColumnFlags.WidthStretch);
            }

            ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
            for (var i = 0; i < Columns.Length; i++)
            {
                ImGui.TableSetColumnIndex(i);
                ImGui.PushID(i);
                ImGui.TableHeader(Columns[i].Name);
                ImGui.PopID();
                if (Columns[i].Tip.Length > 0 && ImGui.IsItemHovered()) ImGui.SetTooltip(Columns[i].Tip);
            }

            foreach (var row in rows)
            {
                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                var label = row.FullName.Length > 0 ? row.FullName : $"（讀不到名稱，CID {row.Cid:X}）";
                ImGui.TextUnformatted(row.IsCurrent ? $"{label}（目前）" : label);

                DrawCell(row.Next);
                DrawCell(row.Retainer);
                DrawCell(row.Vessel);
                DrawCell(row.Mission);
                DrawCell(row.Training);
                DrawCell(row.FcStock);
                DrawCell(row.Inventory);

                ImGui.TableNextColumn();
                DrawSwitchButton(row);
            }
        }
        finally
        {
            ImGui.EndTable();
        }
    }

    private static void DrawCell(in Cell cell)
    {
        ImGui.TableNextColumn();
        switch (cell.Tone)
        {
            case Tone.Dim: ImGui.TextDisabled(cell.Text); break;
            case Tone.Ready: ImGui.TextColored(ReadyColor, cell.Text); break;
            case Tone.Warn: ImGui.TextColored(WarnColor, cell.Text); break;
            default: ImGui.TextUnformatted(cell.Text); break;
        }

        if (cell.Tip.Length > 0 && ImGui.IsItemHovered()) ImGui.SetTooltip(cell.Tip);
    }

    private void DrawSwitchButton(Row row)
    {
        var reason = row.IsCurrent ? "這就是目前的角色。"
            : row.FullName.Length == 0 ? "讀不到這個角色的名稱，組不出切換要的「名稱@伺服器」。"
            : switchBlocked;

        var clicked = false;
        using (ImRaii.Disabled(reason != null))
        {
            clicked = ImGui.SmallButton($"換到這角##charOverviewSwitch{row.Cid}");
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            const string what = "請 AutoRetainer 切換到這個角色（會登出目前角色），與 /tcswitch 相同。";
            ImGui.SetTooltip(reason == null ? what : $"現在不能按：{reason}\n\n{what}");
        }

        if (clicked && reason == null) pendingSwitch = row.FullName;
    }

    public override void DrawConfig()
    {
        if (ImGui.Button("開啟角色總覽視窗##charOverviewOpen") && window != null) window.IsOpen = true;
        ImGui.SameLine();
        ImGui.TextDisabled($"或輸入 {Command}");

        if (arLoaded == false)
            ImGui.TextColored(WarnColor, "沒有偵測到 AutoRetainer —— 視窗裡不會有角色。");

        ImGui.Separator();

        var remind = Config.RemindSquadron;
        if (ImGui.Checkbox("分隊歸隊、訓練結束時在聊天欄提醒##charOverviewRemind", ref remind))
        {
            Config.RemindSquadron = remind;
            Plugin.Instance.Config.Save();
            if (remind) remindArmedUtc = DateTime.UtcNow;
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "每個角色的分隊歸隊、訓練結束時間一到，在聊天欄提醒一行（只提醒，不會做任何事）。\n" +
                "時間來自這個角色上次登入時讀到的分隊狀態，或「國防聯軍分隊」模組記下的派遣紀錄。\n" +
                "只提醒開啟之後才到的時間；遊戲關著時到期的不會補提醒。");
        }

        using (ImRaii.Disabled(!Config.RemindSquadron))
        {
            var tataru = Config.RemindWithTataru;
            if (ImGui.Checkbox("提醒時也請塔塔露誇獎念一句##charOverviewTataru", ref tataru))
            {
                Config.RemindWithTataru = tataru;
                Plugin.Instance.Config.Save();
            }
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("需要另外安裝並開啟「塔塔露誇獎」插件；沒裝時只印聊天。");
    }

    #endregion
}

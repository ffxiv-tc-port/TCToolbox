using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using TCToolbox.Core;
using TCToolbox.Windows;

namespace TCToolbox.Modules;

/// <summary>
/// DailyDuty 追蹤中、還沒做完的每日／每週／特殊任務，以及它不知道狀態的任務（另列「未知」）；
/// 有目的地的附「帶我去」，行程中可以取消；寶物地圖另附「交給 GBR 採一張」。
/// </summary>
/// <remarks>
/// 🔴 「帶我去」「交給 GBR 採一張」與「取消」只在使用者按下按鈕的那一次送出：按鈕只設待送欄位，
/// 下一次 <c>Framework.Update</c> 送一次就清掉。不輪詢、不重試、清單變動時不自動接續下一項。
/// 🔴 本檔欄位只在遊戲主執行緒讀寫（<c>Framework.Update</c> 與 ImGui 的 Draw）；
/// IPC 只在 <c>Framework.Update</c> 打，Draw 路徑只讀算好的欄位。
/// </remarks>
public sealed class DailyDutyTodo : TcModule
{
    public const string Command = "/tcdaily";

    /// <summary>清單與版本的輪詢間隔（毫秒）。DailyDuty 自己約半秒才重整一次狀態。</summary>
    private const long PollIntervalMs = 500;

    /// <summary>「帶我去」按第一次之後，等第二次的有效時間（毫秒）。過了自動解除。</summary>
    private const long ConfirmWindowMs = 5_000;

    /// <summary>列上與資訊列 tooltip 最多列出幾項（超過的以「…等共 N 項」帶過）。</summary>
    private const int TooltipPreviewCount = 12;

    /// <summary>DailyDuty 沒載入或版本不足時，列上逐字顯示這一句。</summary>
    private const string NeedDailyDutyText = "需要 DailyDuty（未載入／版本不足）";

    /// <summary>「未知」那一段的說明（標題與每一列共用）。</summary>
    private const string UnknownStatusTooltip =
        "DailyDuty 不知道這一項的狀態（例如沒有勾選要追蹤的項目），所以沒有算進未完成的數量。\n"
        + "請到 DailyDuty 裡這一項的「模組設定」確認；不想追蹤的話，在 DailyDuty 把這一項關掉就不會再列出來。";

    /// <summary>DailyDuty 寶物地圖模組的內部名稱（<c>ModuleName.TreasureMap</c>）。</summary>
    private const string TreasureMapModule = "TreasureMap";

    /// <summary>GatherBuddy Reborn 可採地圖清單的重問間隔（毫秒）。對方載入後內容就不變，只防對方重載。</summary>
    private const long MapListRefreshMs = 60_000;

    private const string NeedGbrText = "需要 GatherBuddy Reborn（未載入／版本不足）";

    private static readonly Vector4 WarnColor = new(1f, 0.72f, 0.35f, 1f);
    private static readonly Vector4 OkColor = new(0.42f, 0.85f, 0.45f, 1f);

    /// <summary>向 DailyDuty 問的結果。零值＝還沒問過，這時候畫面上不下任何結論。</summary>
    private enum Availability
    {
        NotPolled = 0,
        NotLoggedIn = 1,
        NotLoaded = 2,
        VersionTooOld = 3,
        Ready = 4,
    }

    /// <summary>清單上的一項。</summary>
    /// <param name="Name">DailyDuty 的內部名稱（動作一律用它）。</param>
    /// <param name="DisplayName">DailyDuty 介面語言下的名稱；空字串＝問不到。</param>
    /// <param name="Type">1 每日／2 每週／3 特殊；其他＝問不到。</param>
    /// <param name="CanTravel">此刻畫得出「帶我去」。</param>
    private readonly record struct TodoEntry(string Name, string DisplayName, int Type, bool CanTravel);

    public override string InternalName => "DailyDutyTodo";

    public override string DisplayName => "DailyDuty 未完成任務清單";

    public override string Description =>
        "列出 DailyDuty 追蹤中、今天／本週還沒做完的任務（名稱跟著 DailyDuty 的介面語言），"
        + "有目的地的附「帶我去」：按下去才交給 DailyDuty 出發，行程中可以按「取消」。"
        + "伺服器資訊列顯示還剩幾項，點一下開清單。需要另外安裝 DailyDuty（狀態介面第 2 版以上）。"
        + "DailyDuty 不知道狀態的任務（例如沒有勾選要追蹤的項目）另外列成「未知」（狀態介面第 3 版起）。"
        + "寶物地圖那一列另有「交給 GBR 採一張」：按下去才交給 GatherBuddy Reborn 照它自己的自動採集去採（需要它的狀態介面第 5 版以上，要採哪一種在這裡的設定選）。"
        + $"指令 {Command} 開啟獨立視窗。";

    public override ModuleCategory Category => ModuleCategory.Misc;

    public override bool HasConfigUI => true;

    /// <summary>開著只會列清單；角色只在你按下「帶我去」的那一刻才會動。</summary>
    public override bool IsManualTrigger => true;

    private static DailyDutyTodoConfig Config => Plugin.Instance.Config.DailyDutyTodo;

    private DailyDutyTodoWindow? window;
    private IDtrBarEntry? dtrEntry;

    private Availability availability;

    /// <summary>DailyDuty 回報的狀態介面版本（問不到時是 0）。</summary>
    private int apiVersion;

    /// <summary>上次以 Information 記下「已連上」的版本；0＝啟用後還沒記過，或記下斷線之後還沒重新連上。</summary>
    private int announcedVersion;

    private readonly List<TodoEntry> entries = [];

    /// <summary>有啟用、但 DailyDuty 不知道狀態的任務。對方第 2 版或端點問不到時恆為空。</summary>
    private readonly List<TodoEntry> unknownEntries = [];

    private bool travelRunning;
    private bool someoneElseMoving;
    private string mover = string.Empty;

    /// <summary>下一幀要送出的「帶我去」（內部名稱；空字串＝沒有）。</summary>
    private string pendingTravel = string.Empty;

    private bool pendingCancel;

    /// <summary>下一幀要交給 GBR 的寶物地圖道具 id；0＝沒有。</summary>
    private uint pendingMapGather;

    /// <summary>GatherBuddy Reborn 的狀態介面版本；0＝問不到（沒裝、沒載入）。</summary>
    private int gbrVersion;

    /// <summary>GBR 回報找得到採集點的寶物地圖（等級高的在前）與顯示名稱；版本不足或問不到時是空的。</summary>
    private readonly List<(uint ItemId, string Name)> mapChoices = [];

    private long lastMapListTick;
    private bool mapListKnown;

    /// <summary>被「按第一次」武裝起來的那一項（內部名稱；空字串＝沒有）。</summary>
    private string confirmKey = string.Empty;

    private long confirmUntil;

    private string lastActionText = string.Empty;
    private bool lastActionOk;

    private long lastPollTick;

    /// <summary>資訊列上次畫的內容；沒變就不重建。</summary>
    private string dtrSignature = string.Empty;

    /// <summary>上次算資訊列內容時用到的全部輸入（<see cref="dtrInputsKnown"/> 為 false＝還沒算過）。</summary>
    private bool dtrInputsKnown;
    private bool dtrInputShow;
    private Availability dtrInputAvailability;
    private int dtrInputVersion;
    private readonly List<TodoEntry> dtrInputEntries = [];
    private readonly List<TodoEntry> dtrInputUnknown = [];

    public override ModuleNotice? RowNotice
    {
        get
        {
            if (!IsEnabled) return null;

            return availability switch
            {
                Availability.NotPolled => null,
                Availability.NotLoggedIn => new ModuleNotice(
                    ModuleNoticeLevel.Unknown, "? 未登入，問不到", "登入之後才問得到 DailyDuty 的任務狀態。"),
                Availability.NotLoaded or Availability.VersionTooOld => new ModuleNotice(
                    ModuleNoticeLevel.Warning, NeedDailyDutyText, DescribeNeedDailyDuty()),
                _ => new ModuleNotice(ModuleNoticeLevel.Unknown, DescribeCounts(), BuildListTooltip()),
            };
        }
    }

    protected override void OnEnable()
    {
        Svc.Commands.AddHandler(Command, new CommandInfo(OnCommand)
        {
            HelpMessage = "開啟「DailyDuty 未完成任務清單」",
        });

        window = new DailyDutyTodoWindow(this);
        Plugin.Instance.WindowSystem.AddWindow(window);

        dtrEntry = Svc.DtrBar.Get("TC Toolbox DailyDuty 未完成任務");
        dtrEntry.Shown = false;
        dtrEntry.OnClick = ev =>
        {
            if (ev.ClickType != MouseClickType.Left) Plugin.Instance.ToggleMainWindow();
            else if (window != null) window.Toggle();
        };

        ResetState();
        Svc.Framework.Update += OnUpdate;
    }

    protected override void OnDisable()
    {
        Svc.Framework.Update -= OnUpdate;

        if (window != null)
        {
            // 🔴 外掛整個卸載時 Plugin.Dispose 先 RemoveAllWindows() 才停用模組，而對不在清單裡的視窗
            //    呼叫 RemoveWindow 會擲 ArgumentException。
            if (Plugin.Instance.WindowSystem.Windows.Contains(window))
                Plugin.Instance.WindowSystem.RemoveWindow(window);

            window.IsOpen = false;
            window = null;
        }

        Svc.Commands.RemoveHandler(Command);

        dtrEntry?.Remove();
        dtrEntry = null;

        ResetState();
    }

    private void ResetState()
    {
        availability = Availability.NotPolled;
        apiVersion = 0;
        announcedVersion = 0;
        entries.Clear();
        unknownEntries.Clear();
        travelRunning = false;
        someoneElseMoving = false;
        mover = string.Empty;
        pendingTravel = string.Empty;
        pendingCancel = false;
        pendingMapGather = 0;
        gbrVersion = 0;
        mapChoices.Clear();
        lastMapListTick = 0;
        mapListKnown = false;
        confirmKey = string.Empty;
        confirmUntil = 0;
        lastActionText = string.Empty;
        lastActionOk = false;
        lastPollTick = 0;
        dtrSignature = string.Empty;
        dtrInputsKnown = false;
        dtrInputEntries.Clear();
        dtrInputUnknown.Clear();
    }

    private void OnCommand(string command, string arguments) => OpenWindow();

    private const string DtrClickHelp = "\n\n左鍵：開啟／關閉未完成任務清單\n右鍵：開啟／關閉 TC Toolbox 設定";

    /// <summary>指令與設定裡的按鈕用：只開不關，已經開著就維持開著。</summary>
    private void OpenWindow()
    {
        if (window != null) window.IsOpen = true;
    }

    private void OnUpdate(IFramework framework)
    {
        // 🔴 使用者按下去的那一次一定要送出，不受輪詢節奏與視窗開不開影響。
        FlushPending();

        // 行程旗標可以每幀問：「取消」按鈕要跟得上 DailyDuty 真的出發或抵達的那一刻。
        if (availability == Availability.Ready)
            travelRunning = DailyDutyIpc.IsTravelRunning() ?? false;

        var now = Environment.TickCount64;
        if (now - lastPollTick < PollIntervalMs) return;
        lastPollTick = now;

        Refresh();
        if (Svc.ClientState.IsLoggedIn) RefreshGbr(now);
        UpdateDtr();
    }

    /// <summary>把使用者按下的「取消」或「帶我去」送出去，每一個按下各送一次。</summary>
    /// <remarks>同一幀兩個都有時取消贏（與 DailyDuty 端的語意一致）：出發請求直接丟掉。</remarks>
    private void FlushPending()
    {
        if (pendingCancel)
        {
            pendingCancel = false;
            pendingTravel = string.Empty;
            pendingMapGather = 0;
            SendCancel();
        }

        if (pendingMapGather != 0)
        {
            var itemId = pendingMapGather;
            pendingMapGather = 0;
            SendMapGather(itemId);
        }

        if (pendingTravel.Length == 0) return;

        var name = pendingTravel;
        pendingTravel = string.Empty;
        SendTravel(name);
    }

    private void SendTravel(string name)
    {
        var shown = FindDisplayName(name);

        if (!DailyDutyIpc.TryTravelToModule(name, out var code))
        {
            Report(false, $"呼叫失敗：DailyDuty 未載入或版本不足，沒有前往「{shown}」。");
            Svc.Log.Information($"[DailyDutyTodo] 使用者按下帶我去：{shown}（{name}），呼叫失敗（DailyDuty 未載入或版本不足）。");
            return;
        }

        var accepted = code == DailyDutyIpc.TravelRequested;
        Report(
            accepted,
            accepted
                ? $"已交給 DailyDuty 前往「{shown}」；沒有出發的話，原因會印在聊天視窗。"
                : $"DailyDuty 沒有接下「{shown}」：{DailyDutyIpc.DescribeTravelCode(code)}。");

        // 使用者回報用的定錨點：按了沒反應時，只有這一行分得出是誰沒接。
        Svc.Log.Information(
            $"[DailyDutyTodo] 使用者按下帶我去：{shown}（{name}），DailyDuty 回 {code}（{DailyDutyIpc.DescribeTravelCode(code)}）。");
    }

    private void SendCancel()
    {
        if (!DailyDutyIpc.TryCancelTravel(out var hadSomething, out var error))
        {
            var why = error.Length != 0 ? error : "DailyDuty 未載入或版本不足";
            Report(false, $"呼叫失敗（{why}），沒有取消任何東西。");
            Svc.Log.Information($"[DailyDutyTodo] 使用者按下取消，呼叫失敗（{why}）。");
            return;
        }

        Report(true, hadSomething ? "已要求 DailyDuty 取消目前的行程。" : "DailyDuty 回報現在沒有行程可以取消。");
        Svc.Log.Information(
            $"[DailyDutyTodo] 使用者按下取消，DailyDuty 回 {hadSomething}（{(hadSomething ? "有東西可以取消" : "沒有東西可以取消")}）。");
    }

    private void SendMapGather(uint itemId)
    {
        var shown = FindMapName(itemId);

        if (!GatherBuddyMapIpc.TryGatherTreasureMap(itemId, out var code))
        {
            Report(false, $"呼叫失敗：GatherBuddy Reborn 未載入或版本不足，沒有交出「{shown}」。");
            Svc.Log.Information($"[DailyDutyTodo] 使用者按下交給 GBR 採寶物地圖：{shown}（{itemId}），呼叫失敗（GBR 未載入或版本不足）。");
            return;
        }

        var accepted = code is GatherBuddyMapIpc.MapStarted or GatherBuddyMapIpc.MapAlreadyRunning;
        var text = accepted
            ? $"已交給 GatherBuddy Reborn 採一張「{shown}」：{GatherBuddyMapIpc.DescribeCode(code)}。要停就關掉它的自動採集。"
            : $"GatherBuddy Reborn 沒有接下「{shown}」：{GatherBuddyMapIpc.DescribeCode(code)}。";

        // 自動採集會把所有已啟用清單一起採，不只這張地圖：讓使用者知道還有別的。
        var others = accepted ? GatherBuddyMapIpc.GetActiveItemCount() - 1 : 0;
        if (others > 0) text += $"\n注意：GBR 已啟用的清單上另有 {others} 種道具，自動採集會一起採。";

        Report(accepted, text);
        Svc.Log.Information(
            $"[DailyDutyTodo] 使用者按下交給 GBR 採寶物地圖：{shown}（{itemId}），GBR 回 {code}（{GatherBuddyMapIpc.DescribeCode(code)}），其他啟用中的道具 {others} 種。");
    }

    /// <summary>問 GBR 的版本（每次輪詢）與可採地圖清單（每 <see cref="MapListRefreshMs"/> 毫秒，或版本一變）。</summary>
    private void RefreshGbr(long now)
    {
        var version = GatherBuddyMapIpc.TryGetVersion(out var v) ? v : 0;
        var versionChanged = version != gbrVersion;
        gbrVersion = version;

        if (version < GatherBuddyMapIpc.RequiredVersion)
        {
            mapChoices.Clear();
            mapListKnown = false;
            return;
        }

        if (!versionChanged && mapListKnown && now - lastMapListTick < MapListRefreshMs) return;
        lastMapListTick = now;

        if (!GatherBuddyMapIpc.TryGetGatherableMaps(out var ids))
        {
            mapListKnown = false;
            return;
        }

        mapChoices.Clear();
        foreach (var id in ids)
            mapChoices.Add((id, DescribeMapItem(id)));
        mapListKnown = true;
    }

    /// <summary>「陳舊的地圖G17（8 人）」；表裡查不到名字時退回道具編號。</summary>
    private static string DescribeMapItem(uint itemId)
    {
        var name = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Item>().GetRowOrDefault(itemId)?.Name.ExtractText();
        if (string.IsNullOrEmpty(name)) name = $"道具 {itemId}";

        foreach (var rank in Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.TreasureHuntRank>())
        {
            if (rank.ItemName.RowId != itemId) continue;
            return rank.MaxPartySize > 1 ? $"{name}（{rank.MaxPartySize} 人）" : $"{name}（單人）";
        }

        return name;
    }

    private string FindMapName(uint itemId)
    {
        foreach (var (id, name) in mapChoices)
        {
            if (id == itemId) return name;
        }

        return $"道具 {itemId}";
    }

    private bool IsMapChoice(uint itemId)
    {
        foreach (var (id, _) in mapChoices)
        {
            if (id == itemId) return true;
        }

        return false;
    }

    private void Report(bool ok, string text)
    {
        lastActionOk = ok;
        lastActionText = text;
    }

    private void Refresh()
    {
        someoneElseMoving = ExternalNav.TryGetActiveMover(out var who);
        mover = who;

        if (!Svc.ClientState.IsLoggedIn)
        {
            SetUnavailable(Availability.NotLoggedIn, 0);
            return;
        }

        if (!DailyDutyIpc.TryGetVersion(out var version))
        {
            SetUnavailable(Availability.NotLoaded, 0);
            return;
        }

        if (version < DailyDutyIpc.RequiredVersion)
        {
            SetUnavailable(Availability.VersionTooOld, version);
            return;
        }

        // 版本問得到、清單卻問不到＝對方正在卸載或端點出錯：這一輪當成沒載入，下一輪再問。
        if (!DailyDutyIpc.TryGetIncompleteModules(out var incomplete))
        {
            SetUnavailable(Availability.NotLoaded, version);
            return;
        }

        // 目的地清單問不到時當成全都沒有目的地：只少了按鈕，清單本身照畫。
        DailyDutyIpc.TryGetTravelModules(out var travelNames);
        var travelSet = new HashSet<string>(travelNames, StringComparer.OrdinalIgnoreCase);

        entries.Clear();
        foreach (var name in incomplete)
        {
            if (string.IsNullOrEmpty(name)) continue;

            entries.Add(new TodoEntry(
                name,
                DailyDutyIpc.GetModuleDisplayName(name),
                DailyDutyIpc.GetModuleType(name),
                travelSet.Contains(name)));
        }

        // 第 2 版沒有這支端點，連訂閱都不建；問不到時只少了「未知」那一段，其餘照畫。
        unknownEntries.Clear();
        if (version >= DailyDutyIpc.UnknownListVersion && DailyDutyIpc.TryGetUnknownModules(out var unknown))
        {
            foreach (var name in unknown)
            {
                // 已在未完成清單裡的不重複列：同名兩列會共用同一個 ImGui ID 與待送欄位。
                if (string.IsNullOrEmpty(name) || IndexOf(entries, name) >= 0 || IndexOf(unknownEntries, name) >= 0)
                    continue;

                unknownEntries.Add(new TodoEntry(
                    name,
                    DailyDutyIpc.GetModuleDisplayName(name),
                    DailyDutyIpc.GetModuleType(name),
                    travelSet.Contains(name)));
            }
        }

        travelRunning = DailyDutyIpc.IsTravelRunning() ?? false;
        SetAvailability(Availability.Ready, version);
    }

    private static int IndexOf(List<TodoEntry> list, string name)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i].Name, name, StringComparison.OrdinalIgnoreCase)) return i;
        }

        return -1;
    }

    private void SetUnavailable(Availability state, int version)
    {
        entries.Clear();
        unknownEntries.Clear();
        travelRunning = false;
        SetAvailability(state, version);
    }

    /// <summary>更新可用狀態；只在轉換的那一刻寫一行記錄（未登入那一格不寫，登入登出太頻繁）。</summary>
    /// <remarks>
    /// 「已連上」只在啟用後第一次連上、記下斷線之後重新連上，或版本改變時寫 Information；
    /// 換角／重新登入回到同一版本只寫 Debug（逐角色換角時每次登入都會走到這裡）。
    /// </remarks>
    private void SetAvailability(Availability state, int version)
    {
        var changed = state != availability || version != apiVersion;
        availability = state;
        apiVersion = version;
        if (!changed) return;

        switch (state)
        {
            case Availability.NotLoaded:
                announcedVersion = 0;
                Svc.Log.Information("[DailyDutyTodo] 問不到 DailyDuty 的狀態介面（沒裝、沒載入，或還沒註冊）。");
                break;
            case Availability.VersionTooOld:
                announcedVersion = 0;
                Svc.Log.Information(
                    $"[DailyDutyTodo] DailyDuty 的狀態介面是第 {version} 版，需要第 {DailyDutyIpc.RequiredVersion} 版以上。");
                break;
            case Availability.Ready when version != announcedVersion:
                announcedVersion = version;
                Svc.Log.Information($"[DailyDutyTodo] 已連上 DailyDuty 狀態介面第 {version} 版。");
                break;
            case Availability.Ready:
                Svc.Log.Debug($"[DailyDutyTodo] 重新登入後連回 DailyDuty 狀態介面第 {version} 版。");
                break;
        }
    }

    private string FindDisplayName(string name)
    {
        var i = IndexOf(entries, name);
        if (i >= 0) return ShownName(entries[i]);

        i = IndexOf(unknownEntries, name);
        return i >= 0 ? ShownName(unknownEntries[i]) : name;
    }

    private static string ShownName(TodoEntry entry) =>
        entry.DisplayName.Length != 0 ? entry.DisplayName : entry.Name;

    private string DescribeNeedDailyDuty() => availability == Availability.VersionTooOld
        ? $"目前的 DailyDuty 狀態介面是第 {apiVersion} 版，這個清單需要第 {DailyDutyIpc.RequiredVersion} 版以上；請把 DailyDuty 更新到最新版。"
        : "沒有偵測到 DailyDuty：請先安裝並啟用它。沒裝的話這裡不會列出任何任務，也沒有「帶我去」。";

    /// <summary>「每日 3・每週 2 未完成」這種短句；一項都沒有時換成完成的說法。有「未知」時接在後面。</summary>
    private string DescribeCounts()
    {
        var counts = entries.Count == 0 ? "沒有未完成的任務" : $"{DescribeCountParts()} 未完成";
        return unknownEntries.Count == 0 ? counts : $"{counts}・{unknownEntries.Count} 項狀態未知";
    }

    /// <summary>「每日 3・每週 2」：只列數量不為 0 的類型。<paramref name="withUnknownStatus"/> 時在最後接「未知 N」。</summary>
    private string DescribeCountParts(bool withUnknownStatus = false)
    {
        CountByType(out var daily, out var weekly, out var special, out var unknown);
        var sb = new StringBuilder();
        AppendCount(sb, "每日", daily);
        AppendCount(sb, "每週", weekly);
        AppendCount(sb, "特殊", special);
        AppendCount(sb, "類型不明", unknown);
        if (withUnknownStatus) AppendCount(sb, "未知", unknownEntries.Count);
        return sb.ToString();
    }

    private static void AppendCount(StringBuilder sb, string label, int count)
    {
        if (count == 0) return;
        if (sb.Length != 0) sb.Append('・');
        sb.Append(label).Append(' ').Append(count);
    }

    private void CountByType(out int daily, out int weekly, out int special, out int unknown)
    {
        daily = weekly = special = unknown = 0;
        foreach (var entry in entries)
        {
            switch (entry.Type)
            {
                case DailyDutyIpc.TypeDaily: daily++; break;
                case DailyDutyIpc.TypeWeekly: weekly++; break;
                case DailyDutyIpc.TypeSpecial: special++; break;
                default: unknown++; break;
            }
        }
    }

    /// <summary>列上與資訊列共用的 tooltip：逐項列出名稱，最多 <see cref="TooltipPreviewCount"/> 項。</summary>
    private string BuildListTooltip()
    {
        if (entries.Count == 0 && unknownEntries.Count == 0)
        {
            return "DailyDuty 回報沒有未完成的任務。\n"
                   + "這裡只看得到 DailyDuty 裡有啟用的任務；一個都沒啟用，或剛登入它還沒整理好狀態時，也會是空的。";
        }

        var sb = new StringBuilder();
        if (entries.Count == 0) sb.AppendLine("DailyDuty 回報沒有未完成的任務。");
        AppendPreview(sb, entries);

        if (unknownEntries.Count != 0)
        {
            sb.AppendLine("狀態未知（DailyDuty 不知道做完了沒，請到 DailyDuty 的「模組設定」確認）：");
            AppendPreview(sb, unknownEntries);
        }

        sb.Append($"清單與「帶我去」在 {Command} 視窗裡。");
        return sb.ToString();
    }

    private static void AppendPreview(StringBuilder sb, List<TodoEntry> list)
    {
        var shown = 0;
        foreach (var entry in list)
        {
            if (shown == TooltipPreviewCount)
            {
                sb.Append("…等共 ").Append(list.Count).AppendLine(" 項");
                break;
            }

            sb.Append("· ").Append(Label(entry));
            if (entry.CanTravel) sb.Append("（可帶我去）");
            sb.AppendLine();
            shown++;
        }
    }

    private static string Label(TodoEntry entry) =>
        entry.DisplayName.Length != 0 ? entry.DisplayName : $"{entry.Name}（名稱問不到）";

    /// <summary>伺服器資訊列：還剩幾項與幾項狀態未知；問不到時是「?」，全部做完或沒登入時收起來。</summary>
    private void UpdateDtr()
    {
        if (dtrEntry == null) return;

        var show = Config.ShowDtr;

        // 文字與提示只由下面記下的這幾樣決定：都和上次算的時候相同，組出來的內容必然也相同，不必重組。
        if (DtrInputsUnchanged(show)) return;
        RememberDtrInputs(show);

        string text;
        string tooltip;

        switch (availability)
        {
            case Availability.NotLoaded:
            case Availability.VersionTooOld:
                text = "待辦 ?";
                tooltip = $"TC Toolbox — {NeedDailyDutyText}\n{DescribeNeedDailyDuty()}{DtrClickHelp}";
                break;
            // 只剩「未知」時照樣顯示：收起來等於告訴使用者全部做完了。
            case Availability.Ready when entries.Count > 0 || unknownEntries.Count > 0:
                text = $"待辦 {DescribeCountParts(withUnknownStatus: true)}";
                tooltip = $"TC Toolbox — DailyDuty 未完成任務\n{BuildListTooltip()}{DtrClickHelp}";
                break;
            default:
                show = false;
                text = string.Empty;
                tooltip = string.Empty;
                break;
        }

        var signature = $"{show}|{text}|{tooltip}";
        if (signature == dtrSignature) return;
        dtrSignature = signature;

        dtrEntry.Shown = show;
        if (!show) return;

        dtrEntry.Text = new SeString(new TextPayload(text));
        dtrEntry.Tooltip = tooltip;
    }

    private bool DtrInputsUnchanged(bool show) =>
        dtrInputsKnown && dtrInputShow == show && dtrInputAvailability == availability && dtrInputVersion == apiVersion
        && SameEntries(dtrInputEntries, entries) && SameEntries(dtrInputUnknown, unknownEntries);

    private void RememberDtrInputs(bool show)
    {
        dtrInputsKnown = true;
        dtrInputShow = show;
        dtrInputAvailability = availability;
        dtrInputVersion = apiVersion;
        dtrInputEntries.Clear();
        dtrInputEntries.AddRange(entries);
        dtrInputUnknown.Clear();
        dtrInputUnknown.AddRange(unknownEntries);
    }

    private static bool SameEntries(List<TodoEntry> a, List<TodoEntry> b)
    {
        if (a.Count != b.Count) return false;

        for (var i = 0; i < a.Count; i++)
        {
            if (a[i] != b[i]) return false;
        }

        return true;
    }

    // ── UI ────────────────────────────────────────────────────────────────────

    public override void DrawConfig()
    {
        DrawPanel();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var showDtr = Config.ShowDtr;
        if (ImGui.Checkbox("在伺服器資訊列顯示還剩幾項", ref showDtr))
        {
            Config.ShowDtr = showDtr;
            Plugin.Instance.Config.Save();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "預設開啟。資訊列會顯示「待辦 每日 N・每週 M」，滑鼠移上列出是哪幾項，點一下開清單。\n"
                + "DailyDuty 不知道狀態的任務另外算成「未知 N」。\n"
                + "全部做完或沒登入時自動收起來；DailyDuty 沒載入或版本不足時顯示「待辦 ?」。");
        }

        var confirm = Config.ConfirmTravel;
        if (ImGui.Checkbox("「帶我去」要按兩次才出發", ref confirm))
        {
            Config.ConfirmTravel = confirm;
            Plugin.Instance.Config.Save();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "預設開啟。清單每一列都有一顆「帶我去」，彼此只隔幾個像素，\n"
                + "而按錯的代價是角色被傳送到別的地方去。「取消」永遠是按一次就生效。\n"
                + "寶物地圖那一列的「交給 GBR 採一張」也照這一格。");
        }

        ImGui.SameLine();
        if (ImGui.Button("開啟獨立視窗##DailyDutyTodoOpen"))
            OpenWindow();

        DrawMapChoiceConfig();
    }

    /// <summary>面板本體。設定區與獨立視窗共用同一份。</summary>
    internal void DrawPanel()
    {
        switch (availability)
        {
            case Availability.NotPolled:
                ImGui.TextDisabled("還在向 DailyDuty 詢問…");
                return;
            case Availability.NotLoggedIn:
                ImGui.TextDisabled("還沒登入，問不到 DailyDuty 的任務狀態。");
                return;
            case Availability.NotLoaded:
            case Availability.VersionTooOld:
                ImGui.TextColored(WarnColor, NeedDailyDutyText);
                ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
                ImGui.TextDisabled(DescribeNeedDailyDuty());
                ImGui.PopTextWrapPos();
                return;
        }

        DrawTravelStatus();
        DrawList();
        DrawFooter();
    }

    private void DrawTravelStatus()
    {
        if (travelRunning)
        {
            ImGui.TextColored(WarnColor, "DailyDuty 正在帶路。");
            ImGui.SameLine();
            if (ImGui.SmallButton("取消##DailyDutyTodoCancel"))
                pendingCancel = true;

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    "叫 DailyDuty 取消目前的行程與還沒執行的出發請求（不論是誰發起的）。\n"
                    + "只停不啟，按錯了也不會讓角色去任何地方。");
            }
        }
        else if (someoneElseMoving)
        {
            ImGui.TextColored(WarnColor, $"「{mover}」正在移動角色，「帶我去」先鎖住。");
        }

        if (lastActionText.Length == 0) return;

        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
        ImGui.TextColored(lastActionOk ? OkColor : WarnColor, lastActionText);
        ImGui.PopTextWrapPos();
    }

    private void DrawList()
    {
        if (entries.Count == 0 && unknownEntries.Count == 0)
        {
            ImGui.TextColored(OkColor, "DailyDuty 回報沒有未完成的任務。");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(BuildListTooltip());
            return;
        }

        // 有「未知」時不打綠字：那幾項可能還沒做，不能說成全部做完。
        if (entries.Count == 0)
            ImGui.TextUnformatted("DailyDuty 回報沒有未完成的任務，但下面幾項它不知道狀態。");

        DrawGroup("每日任務", DailyDutyIpc.TypeDaily);
        DrawGroup("每週任務", DailyDutyIpc.TypeWeekly);
        DrawGroup("特殊任務", DailyDutyIpc.TypeSpecial);
        DrawGroup("類型問不到", 0);
        DrawUnknownGroup();
    }

    /// <summary>DailyDuty 不知道狀態的任務：整列淡色加灰字「未知」，說明放 tooltip。</summary>
    private void DrawUnknownGroup()
    {
        if (unknownEntries.Count == 0) return;

        ImGui.Spacing();
        ImGui.TextUnformatted("狀態未知");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(UnknownStatusTooltip);

        foreach (var entry in unknownEntries)
            DrawEntry(entry, statusUnknown: true);
    }

    /// <summary>畫同一種類型的任務；<paramref name="type"/> 為 0 時收的是「不是 1／2／3 的」。</summary>
    private void DrawGroup(string title, int type)
    {
        var any = false;
        foreach (var entry in entries)
        {
            if (!InGroup(entry, type)) continue;

            if (!any)
            {
                ImGui.Spacing();
                ImGui.TextUnformatted(title);
                any = true;
            }

            DrawEntry(entry);
        }
    }

    private static bool InGroup(TodoEntry entry, int type) => type != 0
        ? entry.Type == type
        : entry.Type is not (DailyDutyIpc.TypeDaily or DailyDutyIpc.TypeWeekly or DailyDutyIpc.TypeSpecial);

    private void DrawEntry(TodoEntry entry, bool statusUnknown = false)
    {
        using var id = ImRaii.PushId(entry.Name);
        using var indent = ImRaii.PushIndent();

        if (entry.DisplayName.Length != 0)
        {
            DrawName(entry.DisplayName, statusUnknown);
        }
        else
        {
            DrawName(entry.Name, statusUnknown);
            ImGui.SameLine();
            ImGui.TextDisabled("(?)");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("DailyDuty 沒有回這一項的名稱，這裡顯示的是它的內部名稱。");
        }

        if (statusUnknown)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("? 未知");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(UnknownStatusTooltip);
        }

        if (!statusUnknown && string.Equals(entry.Name, TreasureMapModule, StringComparison.OrdinalIgnoreCase))
            DrawMapGatherButton();

        if (!entry.CanTravel) return;

        var blocked = travelRunning || someoneElseMoving || pendingTravel.Length != 0 || pendingMapGather != 0;
        var armed = !blocked && IsArmed(entry.Name);

        ImGui.SameLine();
        using (ImRaii.Disabled(blocked))
        {
            if (ImGui.SmallButton(armed ? "再按一次確認###go" : "帶我去###go"))
                OnTravelButton(entry.Name);
        }

        // 🔑 AllowWhenDisabled：按鈕灰掉的時候最需要那句「為什麼」。
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(BuildTravelTooltip(entry, armed));
    }

    private const string MapConfirmKey = "gbrmap:" + TreasureMapModule;

    private void DrawMapGatherButton()
    {
        var chosen = Config.TreasureMapItemId;
        var reason = MapGatherBlockReason(chosen);
        var blocked = reason.Length != 0;
        var armed = !blocked && IsArmed(MapConfirmKey);

        ImGui.SameLine();
        using (ImRaii.Disabled(blocked))
        {
            if (ImGui.SmallButton(armed ? "再按一次確認###gbrmap" : "交給 GBR 採一張###gbrmap"))
                OnMapGatherButton(chosen);
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(BuildMapTooltip(chosen, reason, armed));
    }

    /// <summary>「交給 GBR 採一張」此刻為什麼不能按；空字串＝可以按。</summary>
    private string MapGatherBlockReason(uint chosen)
    {
        if (gbrVersion == 0) return $"{NeedGbrText}：請先安裝並啟用 GatherBuddy Reborn。";
        if (gbrVersion < GatherBuddyMapIpc.RequiredVersion)
            return $"目前的 GatherBuddy Reborn 狀態介面是第 {gbrVersion} 版，這顆按鈕需要第 {GatherBuddyMapIpc.RequiredVersion} 版以上；請把它更新到最新版。";
        if (!mapListKnown) return "還在向 GatherBuddy Reborn 詢問可以採哪些寶物地圖…";
        if (chosen == 0) return "還沒選要採哪一種寶物地圖：到 TC Toolbox 設定的「DailyDuty 未完成任務清單」選一種。";
        if (!IsMapChoice(chosen)) return $"選的地圖（道具 {chosen}）GatherBuddy Reborn 找不到採集點，請重新選一種。";
        if (travelRunning) return "DailyDuty 正在帶路，先按上面的「取消」或等它到。";
        if (someoneElseMoving) return $"「{mover}」正在移動角色，先讓它停下來。";
        if (pendingMapGather != 0 || pendingTravel.Length != 0) return "上一個請求還沒送出。";
        return string.Empty;
    }

    private string BuildMapTooltip(uint chosen, string reason, bool armed)
    {
        if (reason.Length != 0) return reason;

        var text = $"交給 GatherBuddy Reborn 採一張「{FindMapName(chosen)}」：放進它的「來自其他插件」清單並開啟自動採集，\n"
                   + "之後照它自己的流程去採（會傳送、移動）。按一次只送一次；要停就關掉它的自動採集。\n"
                   + "它已啟用的清單上若還有別的道具，會一起採。";
        if (armed) return $"{text}\n再按一次就交出去（五秒內沒按就自動取消）。";
        return Config.ConfirmTravel ? $"{text}\n按第一次只是武裝，再按一次才真的交出去。" : text;
    }

    private void OnMapGatherButton(uint itemId)
    {
        if (!Config.ConfirmTravel || IsArmed(MapConfirmKey))
        {
            confirmKey = string.Empty;
            confirmUntil = 0;
            pendingMapGather = itemId;
            return;
        }

        confirmKey = MapConfirmKey;
        confirmUntil = Environment.TickCount64 + ConfirmWindowMs;
    }

    /// <summary>設定區的地圖選單；選項是 GBR 回報找得到採集點的那些。</summary>
    private void DrawMapChoiceConfig()
    {
        var chosen = Config.TreasureMapItemId;
        var preview = chosen == 0 ? "（還沒選）" : IsMapChoice(chosen) ? FindMapName(chosen) : $"道具 {chosen}（?）";

        ImGui.SetNextItemWidth(260f * ImGuiHelpers.GlobalScale);
        using (var combo = ImRaii.Combo("「交給 GBR 採一張」要採的寶物地圖##DailyDutyTodoMap", preview))
        {
            if (combo)
            {
                if (ImGui.Selectable("（還沒選）", chosen == 0)) SetMapChoice(0);

                foreach (var (id, name) in mapChoices)
                {
                    if (ImGui.Selectable($"{name}##{id}", id == chosen)) SetMapChoice(id);
                }
            }
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "未完成清單裡「寶物地圖」那一列的「交給 GBR 採一張」要採哪一種。\n"
                + "選項是 GatherBuddy Reborn 找得到採集點的寶物地圖（等級高的在前）；同一種地圖只能持有一張。\n"
                + (mapListKnown ? $"GBR 回報 {mapChoices.Count} 種。" : $"現在問不到 GBR：{MapGatherBlockReason(chosen)}"));
        }
    }

    private static void SetMapChoice(uint itemId)
    {
        Config.TreasureMapItemId = itemId;
        Plugin.Instance.Config.Save();
    }

    /// <summary>狀態未知的列畫成淡色（可能還沒做＝淡色，已確認未完成＝實色）。</summary>
    private static void DrawName(string text, bool statusUnknown)
    {
        if (!statusUnknown)
        {
            ImGui.TextUnformatted(text);
            return;
        }

        ImGui.TextDisabled(text);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(UnknownStatusTooltip);
    }

    private string BuildTravelTooltip(TodoEntry entry, bool armed)
    {
        if (travelRunning) return "DailyDuty 已經在帶路了，先按上面的「取消」或等它到。";
        if (someoneElseMoving) return $"「{mover}」正在移動角色，先讓它停下來。";

        var text = $"交給 DailyDuty 前往「{Label(entry)}」。按一次只送一次；出發不了的話，原因會印在聊天視窗。";
        if (armed) return $"{text}\n再按一次就出發（五秒內沒按就自動取消）。";
        return Config.ConfirmTravel ? $"{text}\n按第一次只是武裝，再按一次才真的出發。" : text;
    }

    /// <summary>「帶我去」按下：依設定直接送出，或先武裝、五秒內再按一次才送。</summary>
    private void OnTravelButton(string name)
    {
        if (!Config.ConfirmTravel || IsArmed(name))
        {
            confirmKey = string.Empty;
            confirmUntil = 0;
            pendingTravel = name;
            return;
        }

        confirmKey = name;
        confirmUntil = Environment.TickCount64 + ConfirmWindowMs;
    }

    private bool IsArmed(string name) =>
        string.Equals(confirmKey, name, StringComparison.Ordinal) && Environment.TickCount64 < confirmUntil;

    private static void DrawFooter()
    {
        ImGui.Spacing();
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
        ImGui.TextDisabled("「帶我去」由 DailyDuty 執行：按一次送一次，不會自己接著去下一項。");
        ImGui.PopTextWrapPos();
    }
}

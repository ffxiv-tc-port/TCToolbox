using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using TCToolbox.Core;
using TCToolbox.Windows;

namespace TCToolbox.Modules;

/// <summary>
/// 換 World 與副本區的面板：現在在哪個 World、第幾個副本區、這一區共有幾個，
/// 以及一鍵換 World／換副本區／回旅店與自宅。
/// </summary>
/// <remarks>
/// 🔴🔴 <b>紅線：全部走 Lifestream 的具名 IPC 端點，絕不用聊天指令 <c>/li</c>。</b>
/// 空參數的 <c>/li</c> 是跨界傳送。端點包裝在 <see cref="LifestreamTravel"/>，
/// 那個檔逐字記著每一支的簽章與失敗語意。
/// 🔴 <b>這個面板不做任何自動化。</b>每一個會讓角色移動的動作都必須是使用者當下按下去的；
/// 沒有排程、沒有「條件成立就自己換」、沒有失敗重試。開著但不去按它，遊戲行為完全不變
/// （<see cref="IsManualTrigger"/> 因此為 <see langword="true"/>）。
/// </remarks>
public sealed class WorldTravelPanel : TcModule
{
    public const string Command = "/tcworld";

    /// <summary>二次確認的有效時間（毫秒）。過了就自動解除，避免誤觸留在武裝狀態。</summary>
    private const long ConfirmWindowMs = 5_000;

    /// <summary>顯示用狀態的輪詢間隔（毫秒）。</summary>
    private const long PollIntervalMs = 250;

    /// <summary>「有沒有房子」這類較貴的查詢的輪詢間隔（毫秒）。</summary>
    private const long SlowPollIntervalMs = 2_000;

    /// <summary>面板多久沒被畫出來就停止輪詢（毫秒）。</summary>
    private const long IdleStopMs = 2_000;

    /// <summary>可前往清單為空時的重建重試間隔（毫秒）。</summary>
    private const long RebuildRetryMs = 5_000;

    /// <summary>
    /// Lifestream 端 <c>WorldDCGroupType.Region</c> 為 4 的資料中心，任何人都可以超域傳送過去。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>這個 4 是 <c>WorldDCGroupType.Region</c>，不是 <c>World.Region</c>。</b>兩張表都有一個叫
    /// <c>Region</c> 的欄位而且值域重疊——台服的 <c>World.Region</c> 剛好也是 4，
    /// 互套的話篩選條件會變成另一回事而且不會報錯。
    /// </remarks>
    private const uint AlwaysVisitableDcRegion = 4;

    private static readonly Vector4 WarnColor = new(1f, 0.72f, 0.35f, 1f);
    private static readonly Vector4 OkColor = new(0.42f, 0.85f, 0.45f, 1f);

    /// <summary>面板上要處理的捷徑。</summary>
    /// <remarks>
    /// ⚠️ <b>零值必須是「什麼都不做」。</b>這個欄位是用來記「下一幀要送出哪一個動作」的，
    /// 而「沒有待送出的動作」是它絕大多數時間的狀態——沒有零值的列舉會讓
    /// <c>default</c> 落在某一個真的會傳送角色的成員上。
    /// </remarks>
    private enum Shortcut
    {
        None = 0,
        LocalInn = 1,
        PrivateHouse = 2,
        FreeCompanyHouse = 3,
        Apartment = 4,
    }

    /// <summary>可前往清單上的一個 World。</summary>
    /// <param name="Id"><c>World</c> 表的列號。動作一律用它，不用名字。</param>
    /// <param name="Name">顯示用名稱（取自 <c>World</c> 表，沒有寫死任何中文世界名）。</param>
    /// <param name="CrossDc">要跨資料中心（超域傳送）才到得了。</param>
    private readonly record struct WorldEntry(uint Id, string Name, bool CrossDc);

    public override string InternalName => "WorldTravelPanel";

    public override string DisplayName => "換 World 與副本區";

    public override string Description =>
        "一面板看完「現在在哪個 World、第幾個副本區、這一區共有幾個」，並提供一鍵跨界傳送、" +
        "換副本區，以及回旅店／自宅（個人・公會・公寓）的捷徑。全部透過 Lifestream 的 IPC 端點執行，" +
        "不使用聊天指令。每個動作都要你親手按下，沒有任何自動化；" +
        $"別的外掛正在帶著角色移動時按鈕會變灰。指令 {Command} 開啟獨立視窗。";

    public override ModuleCategory Category => ModuleCategory.Misc;

    public override bool HasConfigUI => true;

    /// <summary>開著也不會自己動——它只在你按下去的那一刻做一次事。</summary>
    public override bool IsManualTrigger => true;

    private static WorldTravelPanelConfig Config => Plugin.Instance.Config.WorldTravel;

    private WorldTravelWindow? window;

    // ── 以下欄位只在遊戲主執行緒讀寫（Framework.Update 與 ImGui 的 Draw 都是主執行緒）──

    private readonly List<WorldEntry> worlds = [];

    /// <summary>可前往清單是照哪一個所在 World 建出來的（0＝還沒建過／已作廢）。</summary>
    private uint worldsBuiltFor;

    private bool lifestreamAvailable;
    private uint currentWorldId;
    private string currentWorldName = string.Empty;
    private uint homeWorldId;
    private string homeWorldName = string.Empty;
    private int currentInstance;
    private int instanceCount;
    private bool canChangeInstance;
    private bool someoneIsMoving;
    private string mover = string.Empty;
    private bool? hasPrivateHouse;
    private bool? hasFreeCompanyHouse;
    private bool? hasApartment;

    private long lastUiTick;
    private long lastPollTick;
    private long lastSlowPollTick;

    /// <summary>下一幀要送出的換 World 請求（0＝沒有）。</summary>
    private uint pendingWorldId;

    /// <summary>下一幀要送出的換副本區請求（0＝沒有）。</summary>
    private int pendingInstance;

    private Shortcut pendingShortcut;

    private bool pendingAbort;

    /// <summary>目前被「按第一次」武裝起來的按鈕；空字串＝沒有。</summary>
    private string confirmKey = string.Empty;

    private long confirmUntil;

    private string lastActionText = string.Empty;
    private bool lastActionOk;

    /// <summary>已經輪詢過至少一次（下面所有欄位才有意義）。</summary>
    /// <remarks>
    /// 🔴 <b>沒有這個旗標的話，面板打開的第一幀會誠懇地說謊。</b>
    /// 輪詢是由「面板被畫出來」觸發的，所以第一幀畫的一定是欄位的初始值——
    /// <c>lifestreamAvailable</c> 的初始值是 <see langword="false"/>，
    /// 於是每次打開面板都會先閃一下「未偵測到 Lifestream」。
    /// 那句話會被使用者當真（他會跑去檢查外掛清單），而它只是還沒查而已。
    /// </remarks>
    private bool polledOnce;

    /// <summary>
    /// 模組列上直接顯示：現在在哪個 World、第幾個副本區。
    /// </summary>
    /// <remarks>
    /// 🔑 <b>「不知道」要在列上看得見。</b>副本區總數 Lifestream 還沒學到時畫的是「?」不是 0——
    /// 畫成 0 會讓人以為這一區沒有副本區，而那是完全不同的一件事。
    /// 🔴 模組關著時回 <see langword="null"/>：關掉之後 <c>OnUpdate</c> 就不跑了，
    /// 欄位裡留的是停用當下的舊值——把過期的所在 World 畫在列上比什麼都不畫更糟。
    /// </remarks>
    public override ModuleNotice? RowNotice
    {
        get
        {
            if (!IsEnabled) return null;

            lastUiTick = Environment.TickCount64;

            // 還沒查過就什麼都不說：這一幀畫出去的任何結論都只是欄位的初始值。
            if (!polledOnce) return null;

            if (!lifestreamAvailable)
            {
                return new ModuleNotice(
                    ModuleNoticeLevel.Warning,
                    "未偵測到 Lifestream",
                    "這個面板的每一個動作都由 Lifestream 執行。請先安裝並啟用 Lifestream，\n"
                    + "面板上的按鈕才會有作用（現在按下去不會有任何事發生）。");
            }

            if (string.IsNullOrEmpty(currentWorldName)) return null;

            var instanceText = currentInstance == 0
                ? "此區無副本區"
                : $"第 {currentInstance} 副本區／共 {DescribeInstanceCount()}";

            return new ModuleNotice(
                ModuleNoticeLevel.Unknown,
                $"{currentWorldName} · {instanceText}",
                BuildStatusTooltip());
        }
    }

    protected override void OnEnable()
    {
        Svc.Commands.AddHandler(Command, new CommandInfo(OnCommand)
        {
            HelpMessage = "開啟「換 World 與副本區」面板",
        });

        window = new WorldTravelWindow(this);
        Plugin.Instance.WindowSystem.AddWindow(window);

        // 剛啟用時把快取清空：不要讓上一次啟用留下的舊值先閃一下。
        ResetState();

        Svc.Framework.Update += OnUpdate;
    }

    protected override void OnDisable()
    {
        Svc.Framework.Update -= OnUpdate;

        if (window != null)
        {
            // 🔴 WindowSystem.RemoveWindow 對「不在清單裡」的視窗會擲 ArgumentException，
            //    而外掛整個卸載時 Plugin.Dispose 是先 RemoveAllWindows() 再 module.Disable()。
            if (Plugin.Instance.WindowSystem.Windows.Contains(window))
                Plugin.Instance.WindowSystem.RemoveWindow(window);

            window.IsOpen = false;
            window = null;
        }

        Svc.Commands.RemoveHandler(Command);

        ResetState();
    }

    private void OnCommand(string command, string arguments)
    {
        if (window == null) return;

        // 指令打開面板的那一刻先讓輪詢醒過來，免得視窗開起來是一片「還不知道」。
        lastUiTick = Environment.TickCount64;
        window.IsOpen = true;
    }

    private void ResetState()
    {
        worlds.Clear();
        worldsBuiltFor = 0;
        lifestreamAvailable = false;
        currentWorldId = 0;
        currentWorldName = string.Empty;
        homeWorldId = 0;
        homeWorldName = string.Empty;
        currentInstance = 0;
        instanceCount = 0;
        canChangeInstance = false;
        someoneIsMoving = false;
        mover = string.Empty;
        hasPrivateHouse = null;
        hasFreeCompanyHouse = null;
        hasApartment = null;
        pendingWorldId = 0;
        pendingInstance = 0;
        pendingShortcut = Shortcut.None;
        pendingAbort = false;
        confirmKey = string.Empty;
        confirmUntil = 0;
        lastActionText = string.Empty;
        lastActionOk = false;
        polledOnce = false;
        lastSlowPollTick = 0;
    }

    private void OnUpdate(IFramework framework)
    {
        // 🔴 待送出的動作永遠優先，而且不受「有沒有人在看」影響：使用者已經按下去了，
        //    這一次呼叫一定要送出去（他可能按完就把視窗關掉了）。
        FlushPending();

        var now = Environment.TickCount64;
        if (now - lastUiTick > IdleStopMs) return;
        if (now - lastPollTick < PollIntervalMs) return;
        lastPollTick = now;

        Refresh(now);
    }

    /// <summary>把使用者按下的那一個動作真的送出去。</summary>
    /// <remarks>
    /// 📌 一次只送一個：面板上同時只可能有一個按鈕被按到，而且每個分支都會把待送出旗標清掉，
    /// 所以不會累積。
    /// </remarks>
    private void FlushPending()
    {
        if (pendingAbort)
        {
            pendingAbort = false;
            var sent = LifestreamTravel.TryAbort();
            Report(sent, sent ? "已要求 Lifestream 中止目前的行程。" : "呼叫失敗：Lifestream 未安裝或未載入。");
        }

        if (pendingWorldId != 0)
        {
            var id = pendingWorldId;
            pendingWorldId = 0;
            var name = FindWorldName(id);

            if (!LifestreamTravel.TryChangeWorld(id, out var accepted))
            {
                Report(false, $"呼叫失敗：Lifestream 未安裝或未載入，沒有前往{name}。");
            }
            else if (accepted)
            {
                Report(true, $"已交給 Lifestream 前往{name}；傳送要花幾分鐘，過程中請不要操作角色。");

                // 使用者回報用的定錨點：出事時這一行是唯一能證明「是這個面板送出去的」的證據。
                Svc.Log.Information(
                    $"[WorldTravelPanel] 使用者按下換 World：目標 {name}（列號 {id}），Lifestream 已接受。");
            }
            else
            {
                Report(false, $"Lifestream 沒有接下前往{name}的請求（它正在忙，或這個 World 不在可前往清單上）。");
            }

            // 換 World 之後清單一定要重建（同 DC／跨 DC 的分組會變）。
            worldsBuiltFor = 0;
        }

        if (pendingInstance != 0)
        {
            var number = pendingInstance;
            pendingInstance = 0;

            var sent = LifestreamTravel.TryChangeInstance(number);
            Report(
                sent,
                sent
                    ? $"已要求 Lifestream 換到第 {number} 副本區（它會自己走到乙太之光操作選單）。"
                    : "呼叫失敗：Lifestream 未安裝或未載入。");

            if (sent)
                Svc.Log.Information($"[WorldTravelPanel] 使用者按下換副本區：目標第 {number} 副本區。");
        }

        if (pendingShortcut != Shortcut.None)
        {
            var shortcut = pendingShortcut;
            pendingShortcut = Shortcut.None;
            RunShortcut(shortcut);
        }
    }

    private void RunShortcut(Shortcut shortcut)
    {
        switch (shortcut)
        {
            case Shortcut.LocalInn:
            {
                var sent = LifestreamTravel.TryGoToLocalInn();
                Report(
                    sent,
                    sent
                        ? "已要求 Lifestream 前往目前這個 World 的旅店房間。"
                        : "呼叫失敗：Lifestream 未安裝或未載入。");
                if (sent) Svc.Log.Information("[WorldTravelPanel] 使用者按下：回旅店（本 World）。");
                break;
            }

            case Shortcut.PrivateHouse:
                RunHouseShortcut(
                    LifestreamTravel.TryTeleportToPrivateHouse, "自宅（個人）", nameof(Shortcut.PrivateHouse));
                break;

            case Shortcut.FreeCompanyHouse:
                RunHouseShortcut(
                    LifestreamTravel.TryTeleportToFreeCompanyHouse, "自宅（公會）", nameof(Shortcut.FreeCompanyHouse));
                break;

            case Shortcut.Apartment:
                RunHouseShortcut(
                    LifestreamTravel.TryTeleportToApartment, "自宅（公寓）", nameof(Shortcut.Apartment));
                break;
        }
    }

    /// <summary>三個自宅捷徑的共用外殼（它們的簽章與失敗語意完全一樣）。</summary>
    private delegate bool HouseShortcut(out bool accepted);

    private void RunHouseShortcut(HouseShortcut call, string label, string logKey)
    {
        if (!call(out var accepted))
        {
            Report(false, $"呼叫失敗：Lifestream 未安裝或未載入，沒有前往{label}。");
            return;
        }

        if (!accepted)
        {
            Report(false, $"Lifestream 正在忙，沒有接下前往{label}的請求。");
            return;
        }

        var note = currentWorldId != 0 && homeWorldId != 0 && currentWorldId != homeWorldId
            ? $"已交給 Lifestream 前往{label}；⚠️ 目前不在原始 World，它會先把你跨界傳送回{homeWorldName}。"
            : $"已交給 Lifestream 前往{label}。";

        Report(true, note);
        Svc.Log.Information($"[WorldTravelPanel] 使用者按下：{label}（{logKey}），Lifestream 已接受。");
    }

    private void Report(bool ok, string text)
    {
        lastActionOk = ok;
        lastActionText = text;
    }

    private void Refresh(long now)
    {
        lifestreamAvailable = ExternalNav.IsLifestreamAvailable();
        polledOnce = true;

        var player = Svc.Objects.LocalPlayer;
        if (!Svc.ClientState.IsLoggedIn || player == null)
        {
            currentWorldId = 0;
            currentWorldName = string.Empty;
            homeWorldId = 0;
            homeWorldName = string.Empty;
            currentInstance = 0;
            instanceCount = 0;
            canChangeInstance = false;
            someoneIsMoving = false;
            mover = string.Empty;
            worlds.Clear();
            worldsBuiltFor = 0;
            return;
        }

        currentWorldId = player.CurrentWorld.RowId;
        currentWorldName = player.CurrentWorld.ValueNullable?.Name.ExtractText() ?? string.Empty;
        homeWorldId = player.HomeWorld.RowId;
        homeWorldName = player.HomeWorld.ValueNullable?.Name.ExtractText() ?? string.Empty;

        someoneIsMoving = ExternalNav.TryGetActiveMover(out var who);
        mover = who;

        currentInstance = LifestreamTravel.GetCurrentInstance();
        instanceCount = LifestreamTravel.GetNumberOfInstances();
        canChangeInstance = LifestreamTravel.CanChangeInstance();

        if (now - lastSlowPollTick >= SlowPollIntervalMs)
        {
            lastSlowPollTick = now;
            hasPrivateHouse = LifestreamTravel.HasPrivateHouse();
            hasFreeCompanyHouse = LifestreamTravel.HasFreeCompanyHouse();
            hasApartment = LifestreamTravel.HasApartment();
        }

        var needRebuild = worldsBuiltFor != currentWorldId
            || (worlds.Count == 0 && Throttle.Pass("WorldTravelPanel-RebuildEmpty", (int)RebuildRetryMs));

        if (needRebuild) RebuildWorlds(player.CurrentWorld.ValueNullable);
    }

    /// <summary>
    /// 重建「可以去哪些 World」的清單。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>不寫死任何中文 World 名。</b>顯示名一律取自 <c>World</c> 表的 <c>Name</c> 欄，
    /// 動作一律用列號。寫死名字的話，遊戲改字、或有人跑別的語系客戶端，就整張清單失效。
    /// </remarks>
    private void RebuildWorlds(World? currentWorld)
    {
        worlds.Clear();
        worldsBuiltFor = currentWorldId;

        if (currentWorld == null) return;

        var myDc = currentWorld.Value.DataCenter;
        var myDcId = myDc.RowId;
        var myDcRegion = (uint)(myDc.ValueNullable?.Region ?? 0);

        foreach (var world in Svc.Data.GetExcelSheet<World>())
        {
            if (world.RowId == 0) continue;

            var name = world.Name.ExtractText();
            if (string.IsNullOrWhiteSpace(name)) continue;

            var dc = world.DataCenter;
            if (dc.RowId == 0) continue;

            var region = (uint)(dc.ValueNullable?.Region ?? 0);
            var sameDc = dc.RowId == myDcId;

            // 先用便宜的條件把八百多列砍到幾十列，再去問 Lifestream。
            if (!sameDc && region != myDcRegion && region != AlwaysVisitableDcRegion) continue;

            // 🔑 兩個都問：理論上同 DC 的一定在 Worlds、跨 DC 的一定在 DCWorlds，
            //    但那是對方的內部分類，靠推論會在它改實作時靜默漏掉整組。
            var visitableSameDc = LifestreamTravel.CanVisitSameDc(name);
            var visitableCrossDc = !visitableSameDc && LifestreamTravel.CanVisitCrossDc(name);
            if (!visitableSameDc && !visitableCrossDc) continue;

            worlds.Add(new WorldEntry(world.RowId, name, visitableCrossDc));
        }

        // 依列號排序＝遊戲自己的 World 順序，跟角色選擇畫面看到的一致。
        worlds.Sort(static (a, b) => a.Id.CompareTo(b.Id));
    }

    private string FindWorldName(uint id)
    {
        foreach (var entry in worlds)
        {
            if (entry.Id == id) return entry.Name;
        }

        return $"World #{id}";
    }

    /// <summary>副本區總數的顯示字串。<b>不知道時是「?」不是 0。</b></summary>
    private string DescribeInstanceCount()
    {
        // 身處第 N 副本區本身就證明至少有 N 個，這個下限不需要任何額外查詢。
        var lowerBound = Math.Max(instanceCount, currentInstance);
        if (instanceCount > 0) return $"{instanceCount} 個";
        return lowerBound > 0 ? $"? 個（至少 {lowerBound}）" : "? 個";
    }

    private string BuildStatusTooltip()
    {
        var homeNote = currentWorldId == homeWorldId
            ? "目前在原始 World。"
            : $"目前不在原始 World（原始 World 是{homeWorldName}）。";

        var countNote = instanceCount > 0
            ? $"這一區共有 {instanceCount} 個副本區（Lifestream 已經在乙太之光的選單上數過）。"
            : "副本區總數還不知道：Lifestream 要在乙太之光開過一次「切換副本區」的選單才會記住。";

        return $"{homeNote}\n{countNote}\n所有動作都要親手按下，這個模組不會自己動。";
    }

    // ── UI ────────────────────────────────────────────────────────────────────

    public override void DrawConfig()
    {
        DrawPanel();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var confirm = Config.ConfirmTravel;
        if (ImGui.Checkbox("會移動角色的按鈕要按兩次才執行", ref confirm))
        {
            Config.ConfirmTravel = confirm;
            Plugin.Instance.Config.Save();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "預設開啟。跨界傳送要花好幾分鐘而且中途不能操作角色，誤觸的代價不低。\n"
                + "關掉之後按一下就會直接送出。\n"
                + "「中止」與「重新整理」永遠是按一次就生效（那兩個不會讓角色移動）。");
        }

        ImGui.SameLine();
        if (ImGui.Button("開啟獨立視窗##WorldTravelOpen") && window != null)
            window.IsOpen = true;
    }

    /// <summary>面板本體。設定區與獨立視窗共用同一份。</summary>
    internal void DrawPanel()
    {
        lastUiTick = Environment.TickCount64;

        // 🔴 只有在真的查過之後才敢說「未偵測到」——第一幀還沒查，那時候的 false 是初始值不是結論。
        if (polledOnce && !lifestreamAvailable)
        {
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
            ImGui.TextColored(
                WarnColor,
                "未偵測到 Lifestream。這個面板的每一個動作都是由 Lifestream 執行的，"
                + "請先安裝並啟用它；在那之前下面的按鈕按了不會有任何事發生。");
            ImGui.PopTextWrapPos();
            ImGui.Spacing();
        }

        DrawStatus();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawWorldButtons();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawInstanceButtons();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawShortcutButtons();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawFooter();
    }

    private void DrawStatus()
    {
        if (currentWorldId == 0)
        {
            ImGui.TextDisabled("還沒登入。");
            return;
        }

        ImGui.TextUnformatted("目前 World：");
        ImGui.SameLine();
        ImGui.TextUnformatted(currentWorldName);

        if (currentWorldId != homeWorldId)
        {
            ImGui.SameLine();
            ImGui.TextColored(WarnColor, $"（旅行中，原始 World：{homeWorldName}）");
        }
        else
        {
            ImGui.SameLine();
            ImGui.TextDisabled("（原始 World）");
        }

        ImGui.TextUnformatted("目前副本區：");
        ImGui.SameLine();

        if (currentInstance == 0)
        {
            ImGui.TextDisabled("這一區沒有副本區");
        }
        else if (instanceCount > 0)
        {
            ImGui.TextUnformatted($"第 {currentInstance} 個／共 {instanceCount} 個");
        }
        else
        {
            // 🔑 總數不知道時畫「?」不是 0。畫 0 會被讀成「這一區沒有副本區」，那是另一件事。
            ImGui.TextUnformatted($"第 {currentInstance} 個／共 ");
            ImGui.SameLine(0f, 0f);
            ImGui.TextDisabled("? 個");
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(BuildStatusTooltip());

        if (someoneIsMoving)
        {
            ImGui.TextColored(WarnColor, $"「{mover}」正在移動角色，會移動的按鈕都先鎖住。");
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    "插隊到別人的移動流程上會把它打斷（互動會鎖住角色、選單會吃掉按鍵）。\n"
                    + "要現在就接手的話，先按下面的「中止 Lifestream 目前的行程」，\n"
                    + "或用全艦隊急停（/tcstopall）把所有會自己動的外掛一起停下來。");
            }
        }
    }

    private void DrawWorldButtons()
    {
        ImGui.TextUnformatted("換 World");
        ImGui.SameLine();
        ImGui.TextDisabled("（同資料中心＝跨界傳送；跨資料中心＝超域傳送）");

        ImGui.SameLine(0f, 16f);
        if (ImGui.SmallButton("重新整理清單##WorldTravelRefresh"))
        {
            worldsBuiltFor = 0;
            Throttle.Reset("WorldTravelPanel-RebuildEmpty");
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "清單是問 Lifestream 拿的，而它要登入之後才建得出來。\n"
                + "剛進遊戲時清單是空的很正常，按這裡可以立刻重建。");
        }

        if (worlds.Count == 0)
        {
            ImGui.TextDisabled("Lifestream 沒有回報任何可前往的 World。");
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    "可能的原因：Lifestream 未安裝或未啟用、還沒登入完成、\n"
                    + "或它的可前往清單還沒建起來（登入後才會建）。\n"
                    + "這不是「這個帳號哪裡都不能去」的意思。");
            }

            return;
        }

        var buttonWidth = Math.Max(120f, (ImGui.GetContentRegionAvail().X - (3 * 8f)) / 4f);
        var column = 0;

        foreach (var entry in worlds)
        {
            if (column > 0) ImGui.SameLine();

            if (entry.Id == currentWorldId)
            {
                using (ImRaii.Disabled())
                {
                    ImGui.Button($"{entry.Name}（目前）##WorldTravelWorld{entry.Id}", new Vector2(buttonWidth, 0f));
                }
            }
            else
            {
                var label = entry.CrossDc ? $"{entry.Name}（跨 DC）" : entry.Name;
                var tooltip = entry.CrossDc
                    ? $"超域傳送到{entry.Name}（跨資料中心）。過程要好幾分鐘，中途請不要操作角色。"
                    : $"跨界傳送到{entry.Name}。過程要好幾分鐘，中途請不要操作角色。";

                if (TravelButton($"world{entry.Id}", label, tooltip, buttonWidth))
                    pendingWorldId = entry.Id;
            }

            column = (column + 1) % 4;
        }
    }

    private void DrawInstanceButtons()
    {
        ImGui.TextUnformatted("換副本區");
        ImGui.SameLine();
        ImGui.TextDisabled("（遊戲內稱「副本區」，社群多半叫它「分線」）");

        if (currentInstance == 0)
        {
            ImGui.TextDisabled("這一區沒有副本區，沒有東西可以換。");
            return;
        }

        // 已知的下限：Lifestream 數過就用它的數字，沒數過至少也有「我現在所在的這一個」。
        var known = Math.Max(instanceCount, currentInstance);

        if (instanceCount == 0)
        {
            ImGui.TextDisabled(
                $"副本區總數還不知道，只列得出已知的 {known} 個。"
                + "在乙太之光開一次「切換副本區」的選單，Lifestream 就會記住這一區有幾個。");
        }

        var blocked = someoneIsMoving || !canChangeInstance;

        for (var number = 1; number <= known; number++)
        {
            if (number > 1) ImGui.SameLine();

            if (number == currentInstance)
            {
                using (ImRaii.Disabled())
                {
                    ImGui.Button($"第 {number} 個（目前）##WorldTravelInst{number}");
                }

                continue;
            }

            var captured = number;
            if (TravelButton(
                    $"inst{number}",
                    $"第 {number} 個",
                    "Lifestream 會自己走到最近的乙太之光，開選單換到這個副本區。",
                    0f,
                    blocked))
            {
                pendingInstance = captured;
            }
        }

        if (!canChangeInstance && !someoneIsMoving)
        {
            ImGui.TextDisabled("現在換不了副本區。");
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    "Lifestream 的 CanChangeInstance 回報現在不行。常見原因：\n"
                    + "附近沒有可用的乙太之光、Lifestream 正在忙、角色正在事件或選單裡，\n"
                    + "或它自己的「顯示副本區切換器」選項是關的。");
            }
        }
    }

    private void DrawShortcutButtons()
    {
        ImGui.TextUnformatted("回家");

        var crossWorldWarning = currentWorldId != 0 && homeWorldId != 0 && currentWorldId != homeWorldId;

        var innTooltip =
            "回目前這個 World 的旅店房間。\n"
            + "📌 這一支刻意用 Lifestream 的「本 World 旅店」端點，不會把你送回原始 World。";

        if (TravelButton("inn", "旅店房間（本 World）", innTooltip, 0f))
            pendingShortcut = Shortcut.LocalInn;

        // 📌 旅店自成一行、三個自宅擠一行：四顆全排在同一行的話，
        //    視窗寬度只要小於約 640 就會被切掉一顆，而被切掉的那顆完全看不見。
        DrawHouseButton("home", "自宅（個人）", hasPrivateHouse, Shortcut.PrivateHouse, crossWorldWarning);

        ImGui.SameLine();
        DrawHouseButton("fc", "自宅（公會）", hasFreeCompanyHouse, Shortcut.FreeCompanyHouse, crossWorldWarning);

        ImGui.SameLine();
        DrawHouseButton("apt", "自宅（公寓）", hasApartment, Shortcut.Apartment, crossWorldWarning);
    }

    /// <summary>
    /// 三個自宅按鈕。
    /// </summary>
    /// <remarks>
    /// 🔑 <b>「查不到」不等於「沒有」，所以查不到時按鈕照樣可以按。</b>
    /// Lifestream 的 <c>HasApartment</c> 在「目前不在原始 World」時刻意回 null；
    /// 把 null 當成「沒有」而把按鈕鎖起來的話，人在別的 World 時三顆按鈕會一起變灰，
    /// 而那正是最想用它們的時候。查不到就在標籤上畫一個灰色的「?」，讓使用者自己判斷。
    /// </remarks>
    private void DrawHouseButton(string key, string label, bool? has, Shortcut shortcut, bool crossWorldWarning)
    {
        var shown = has switch
        {
            true => label,
            false => $"{label}（沒有）",
            _ => $"{label}（?）",
        };

        var ownership = has switch
        {
            true => "Lifestream 回報你有這一種房子。",
            false => "Lifestream 回報你沒有這一種房子——按下去多半只會得到一則錯誤訊息。",
            _ => "查不到有沒有這一種房子（Lifestream 未安裝、還沒登入完成，或目前不在原始 World）。",
        };

        var tooltip = crossWorldWarning
            ? $"{ownership}\n⚠️ 目前不在原始 World：這一支會先把你跨界傳送回{homeWorldName}，再去{label}。"
            : ownership;

        // 🔴 has == false 時<b>不</b>鎖按鈕：那個判定本身也可能是錯的（例如剛買下房子還沒同步），
        //    而按下去最壞的結果只是 Lifestream 印一行「找不到」。鎖起來反而讓人以為功能壞了。
        if (TravelButton(key, shown, tooltip, 0f))
            pendingShortcut = shortcut;
    }

    private void DrawFooter()
    {
        var lifestreamBusy = mover == "Lifestream";

        using (ImRaii.Disabled(!lifestreamBusy))
        {
            if (ImGui.Button("中止 Lifestream 目前的行程##WorldTravelAbort"))
                pendingAbort = true;
        }

        // 灰掉的時候更需要那句說明（見 TravelButton 裡同一條註解）。
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(
                lifestreamBusy
                    ? "叫 Lifestream 把手上的行程整個中止。只停不啟，按錯了不會弄壞任何東西。"
                    : "Lifestream 現在沒有在跑行程，沒有東西可以中止。");
        }

        ImGui.SameLine();
        ImGui.TextDisabled($"（要一次停下所有會自己動的外掛請用 {FleetEmergencyStop.Command}）");

        if (!string.IsNullOrEmpty(lastActionText))
        {
            ImGui.Spacing();
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
            ImGui.TextColored(lastActionOk ? OkColor : WarnColor, lastActionText);
            ImGui.PopTextWrapPos();
        }

        ImGui.Spacing();
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
        ImGui.TextDisabled(
            "所有動作都透過 Lifestream 的 IPC 端點執行，不使用聊天指令。\n"
            + "這個面板不會自己動：沒有排程、沒有條件觸發、沒有失敗重試。");
        ImGui.PopTextWrapPos();
    }

    /// <summary>
    /// 一顆「會讓角色移動」的按鈕：別人在移動時是灰的，且（依設定）要按兩次才送出。
    /// </summary>
    /// <param name="key">這顆按鈕的穩定識別字（同時當 ImGui id 與二次確認的鍵）。</param>
    /// <param name="label">按鈕上的字。</param>
    /// <param name="tooltip">滑鼠移上去的說明。</param>
    /// <param name="width">按鈕寬度；0＝自動。</param>
    /// <param name="extraBlocked">除了「別人在移動」之外的額外禁用條件。</param>
    /// <returns><see langword="true"/>＝這一幀要真的把動作送出去。</returns>
    /// <remarks>
    /// 🔴 <b>回傳 true 的那一刻不呼叫任何 IPC。</b>呼叫端只會設一個待送出旗標，
    /// 真正的呼叫在下一次 <c>Framework.Update</c>（理由見類別備註）。
    /// </remarks>
    private bool TravelButton(string key, string label, string tooltip, float width, bool extraBlocked = false)
    {
        var now = Environment.TickCount64;
        var armed = confirmKey == key && now < confirmUntil;
        var blocked = someoneIsMoving || extraBlocked;

        bool clicked;
        using (ImRaii.Disabled(blocked))
        {
            var shown = armed ? $"再按一次確認：{label}" : label;
            clicked = width > 0f
                ? ImGui.Button($"{shown}##WorldTravel-{key}", new Vector2(width, 0f))
                : ImGui.Button($"{shown}##WorldTravel-{key}");
        }

        // 🔑 <b>AllowWhenDisabled 是必要的，不是順手加的。</b>ImGui 對停用中的項目
        //    IsItemHovered() 一律回 false ⇒ 沒有這個旗標，「這顆為什麼是灰的」的說明
        //    就正好只在使用者需要它的時候不出現。
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(
                blocked
                    ? (someoneIsMoving ? $"「{mover}」正在移動角色，先讓它停下來。" : tooltip)
                    : armed
                        ? $"{tooltip}\n\n再按一次就會送出（五秒內沒按就自動取消）。"
                        : tooltip);
        }

        if (!clicked) return false;

        if (!Config.ConfirmTravel) return true;

        if (armed)
        {
            confirmKey = string.Empty;
            confirmUntil = 0;
            return true;
        }

        confirmKey = key;
        confirmUntil = now + ConfirmWindowMs;
        return false;
    }
}

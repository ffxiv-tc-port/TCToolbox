using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Fates;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// F.A.T.E. 總覽與導航：唯讀列出本區所有 F.A.T.E.（名稱、等級、進度、剩餘時間、距離），
/// 每一列附一顆「導航」按鈕——<b>按了才會</b>透過 vnavmesh IPC 走過去。
/// </summary>
/// <remarks>
/// 🔴 <b>本模組刻意不做自動化。</b>沒有自動接受、沒有自動打怪、沒有自動換下一個 F.A.T.E.、
/// 沒有任何「到了之後接手」的行為。設計約束來自使用者對 DR <c>AutoFate</c> 的裁決原話
/// ——「動作太明顯是自動化　需要人間管」。所以形狀是<b>顯示／追蹤 ＋ 手動觸發移動</b>，
/// 與孤樹無援的裁決同形。要加功能前請先確認沒有跨過這條線。
/// <para>
/// 🔴 <b>資料一律走 Dalamud 受管理的 <see cref="IFateTable"/>，不碰原生 FateManager。</b>
/// 而且 <c>IFate</c> 物件本身是<b>原生指標的包裝</b>（<c>Fate.Address</c> 在建構時就凍結、
/// 之後永不重新解析），所以<b>一個 <c>IFate</c> 參照都不跨幀保存</b>：每次 Draw 重新枚舉一次，
/// 當場抄成純受管理的 <see cref="FateSnapshot"/> 值型別再拿去畫。
/// 需要記住「使用者選了哪一個」時記的是 <see cref="FateSnapshot.FateId"/>（ushort），不是物件。
/// </para>
/// <para>
/// ⚠️ <c>IFateTable.IsValid()</c> 與 <c>Fate.IsValid()</c> <b>不是防護</b>：兩者的實作都只是
/// 「玩家資料載入了沒」（Dalamud/Game/ClientState/Fates/FateTable.cs:62-69），
/// 跟這一筆 F.A.T.E. 的記憶體還在不在完全無關。不要拿它當安全檢查。
/// </para>
/// </remarks>
public sealed class FateTracker : TcModule
{
    public override string InternalName => "FateTracker";
    public override string DisplayName => "F.A.T.E. 總覽與導航";

    public override string Description =>
        "唯讀列出本區的 F.A.T.E.：名稱、等級、進度、剩餘時間與距離，每列一顆「導航」按鈕（按了才走，需要 vnavmesh）。"
        + "不自動接受、不自動戰鬥、不自動換下一個——到了就停，接下來全部自己來。";

    public override ModuleCategory Category => ModuleCategory.Combat;

    /// <summary>
    /// 開著但不去按它，遊戲行為完全不變：清單是唯讀的，移動只有按下「導航」那一刻才發生。
    /// </summary>
    /// <remarks>
    /// 📌 本模組確實有掛 <see cref="IFramework.Update"/>，但那個處理常式在使用者沒按過
    /// 「停止」之前是<b>整段跳過</b>的（見 <see cref="OnFrameworkUpdate"/>），
    /// 所以仍然符合這個屬性的判準。
    /// </remarks>
    public override bool IsManualTrigger => true;

    public override bool HasConfigUI => true;

    /// <summary>剩餘時間低於這個秒數就用警示色（快結束了，值得在列上一眼看到）。</summary>
    private const long EndingSoonSeconds = 120;

    /// <summary>
    /// 使用者按下「停止」之後，持續補送停止指令的時間上限。
    /// </summary>
    /// <remarks>
    /// 🔴 為什麼需要這個：<c>vnavmesh.Path.Stop</c> <b>攔不住還在背景計算的路徑</b>——
    /// 算完之後 vnavmesh 會自己把路徑交給 FollowPath 開走，於是「按了停止、隔幾秒角色
    /// 自己走起來」。詳見 <see cref="ExternalNav.IsVnavmeshPathfindInProgress"/> 的說明。
    /// 這個補送窗口就是拿來蓋住那段空窗的；一旦確認既沒在算也沒在走就提早結束，
    /// 不會真的跑滿。
    /// </remarks>
    private static readonly TimeSpan StopEnforceWindow = TimeSpan.FromSeconds(15);

    /// <summary>
    /// 補送停止的<b>絕對</b>上限（自按下停止起算）。
    /// </summary>
    /// <remarks>
    /// 🔴 窗口到期時只要 vnavmesh 還在算路徑就會延展（見 <see cref="OnFrameworkUpdate"/>），
    /// 這條上限保證 IPC 端點若卡在 true 也一定會收工。與 <see cref="Core.NavStop"/> 同值。
    /// </remarks>
    private static readonly TimeSpan StopEnforceAbsoluteCap = TimeSpan.FromSeconds(30);

    private static readonly Vector4 WarnColor = new(1f, 0.65f, 0.25f, 1f);
    private static readonly Vector4 UnknownColor = new(0.68f, 0.68f, 0.68f, 1f);
    private static readonly Vector4 BonusColor = new(1f, 0.80f, 0.30f, 1f);

    /// <summary>
    /// 一筆 F.A.T.E. 在<b>某一幀</b>的純受管理快照。
    /// </summary>
    /// <remarks>
    /// 🔴 這個型別存在的唯一理由就是<b>切斷與原生記憶體的關係</b>：所有欄位都是值或已複製的
    /// 字串，畫的時候不會再回頭解參考任何指標。快照本身也不跨幀保留。
    /// </remarks>
    private readonly record struct FateSnapshot(
        ushort FateId,
        string Name,
        byte Level,
        byte MaxLevel,
        byte Progress,
        FateState State,
        bool HasBonus,
        long TimeRemaining,
        short Duration,
        int StartTimeEpoch,
        Vector3 Position,
        float Radius,
        string Objective,
        string Description,
        float? Distance);

    private readonly List<FateSnapshot> snapshot = [];

    private bool windowOpen;

    /// <summary>使用者這一趟導航的目標；0＝目前沒有由本模組發起的導航。</summary>
    /// <remarks>🔴 只記 id 與名字（字串已複製），<b>不記 <c>IFate</c>、不記位址</b>。</remarks>
    private ushort navTargetFateId;

    private string navTargetName = string.Empty;

    /// <summary>補送停止指令的截止時刻；<see cref="DateTime.MinValue"/>＝沒有在補送。</summary>
    private DateTime stopEnforceUntil = DateTime.MinValue;

    /// <summary>本輪補送是從什麼時候開始的（算 <see cref="StopEnforceAbsoluteCap"/> 用）。</summary>
    private DateTime stopEnforceStartedAt = DateTime.MinValue;

    // ── vnavmesh 探測的快取 ────────────────────────────────────────────────
    // ⚠️ 這裡快取是為了成本不是為了正確性：vnavmesh 沒安裝時每次 InvokeFunc 都會擲
    //    IpcNotReadyError，而「每幀擲一次例外」是實打實的開銷（例外建構＋堆疊擷取）。
    //    ExternalNav 本身刻意不快取（使用者可能中途裝／拆外掛），所以節流放在這裡。
    //    500ms 的陳舊度對使用者無感，而且失效模式是良性的：按鈕短暫可按 → TryMoveTo
    //    回 false → 我們照樣把原因說出來，不會靜默。
    private bool vnavInstalled;
    private bool vnavReady;
    private bool vnavPathRunning;
    private bool vnavPathfinding;

    protected override void OnEnable()
    {
        snapshot.Clear();
        navTargetFateId = 0;
        navTargetName = string.Empty;
        stopEnforceUntil = DateTime.MinValue;
        stopEnforceStartedAt = DateTime.MinValue;

        Svc.PluginInterface.UiBuilder.Draw += DrawWindow;
        Svc.Framework.Update += OnFrameworkUpdate;
    }

    protected override void OnDisable()
    {
        Svc.Framework.Update -= OnFrameworkUpdate;
        Svc.PluginInterface.UiBuilder.Draw -= DrawWindow;

        // 使用者在我們發起的導航還在跑的時候關掉模組——是我們讓他跑起來的，就由我們收掉，
        // 不要留一個沒有介面可以停的移動在那裡。
        // 📌 沒發起過就完全不呼叫：不要在拆卸路徑上對外掛做沒必要的 IPC。
        if (navTargetFateId != 0)
            ExternalNav.TryStopMovement();

        snapshot.Clear();
        navTargetFateId = 0;
        navTargetName = string.Empty;
        stopEnforceUntil = DateTime.MinValue;
        stopEnforceStartedAt = DateTime.MinValue;
        windowOpen = false;
    }

    /// <summary>
    /// 使用者按過「停止」之後的補送窗口；<b>沒按過就是一行比較後直接返回</b>。
    /// </summary>
    private void OnFrameworkUpdate(IFramework framework)
    {
        if (stopEnforceUntil == DateTime.MinValue) return;

        var now = DateTime.UtcNow;
        if (now >= stopEnforceUntil)
        {
            // 🔴 到期不能無條件放棄：路徑計算超過窗口長度時，vnavmesh 算完照樣把路徑交給
            //    FollowPath 開走 —— 就是這個補送窗口要修掉的那個 bug 原樣復發。
            //    「它存在的唯一理由仍然成立」時要延展，不是到期即棄。
            //    （同一道修法也套在共用的 Core/NavStop 上。）
            if (ExternalNav.IsVnavmeshPathfindInProgress() && now - stopEnforceStartedAt < StopEnforceAbsoluteCap)
            {
                stopEnforceUntil = now + StopEnforceWindow;
                return;
            }

            if (now - stopEnforceStartedAt >= StopEnforceAbsoluteCap)
            {
                Svc.Log.Information(
                    $"[FateTracker] 補送停止已達絕對上限 {StopEnforceAbsoluteCap.TotalSeconds:0} 秒"
                    + "（vnavmesh 仍回報正在計算路徑），停止補送。");
            }

            stopEnforceUntil = DateTime.MinValue;
            stopEnforceStartedAt = DateTime.MinValue;
            return;
        }

        if (!Throttle.Pass("FateTracker-StopEnforce", 100)) return;

        var pathfinding = ExternalNav.IsVnavmeshPathfindInProgress();
        var running = ExternalNav.IsVnavmeshPathRunning();

        if (running)
            ExternalNav.TryStopMovement();

        // 既沒在算路徑也沒在走＝真的停了，提早收工。
        if (!pathfinding && !running)
        {
            stopEnforceUntil = DateTime.MinValue;
            stopEnforceStartedAt = DateTime.MinValue;
        }
    }

    public override void DrawConfig()
    {
        if (ImGui.Button(windowOpen ? "關閉 F.A.T.E. 清單" : "開啟 F.A.T.E. 清單"))
            windowOpen = !windowOpen;

        ImGui.TextDisabled("清單本身是唯讀的；只有按下某一列的「導航」才會讓角色移動。");
        ImGui.TextDisabled("不會自動接受 F.A.T.E.、不會自動戰鬥、不會自動前往下一個。");
    }

    private void DrawWindow()
    {
        if (!windowOpen) return;

        ImGui.SetNextWindowSize(new Vector2(620, 340), ImGuiCond.FirstUseEver);
        // 標題引用 DisplayName；### 之後的 ID 保持原字面值，視窗位置／大小的存檔才不會被重置。
        if (ImGui.Begin($"{DisplayName}###TCToolboxFateTracker", ref windowOpen))
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

        RefreshVnavProbe();

        // 🔴 先把整份快照抄完（這一段可能因為切換區域等原因失敗），再開始畫。
        //    兩件事刻意分開：如果讀取炸在 BeginTable 與 EndTable 中間，ImGui 的堆疊就不平衡了，
        //    那比讀不到資料嚴重得多。
        var snapshotOk = TryBuildSnapshot();

        DrawHeader(snapshotOk);
        ImGui.Separator();

        if (!snapshotOk)
        {
            ImGui.TextColored(UnknownColor, "目前讀不到 F.A.T.E. 資料（可能正在切換區域）。");
            return;
        }

        if (snapshot.Count == 0)
        {
            ImGui.TextDisabled(Plugin.Instance.Config.FateTracker.ShowEnded
                                   ? "本區目前沒有 F.A.T.E.。"
                                   : "本區目前沒有進行中的 F.A.T.E.（已結束的預設不顯示）。");
            return;
        }

        DrawTable();
    }

    /// <summary>
    /// 重新枚舉 <see cref="IFateTable"/> 並抄成快照。
    /// </summary>
    /// <returns>false＝讀取過程出了例外，這一幀不要畫表。</returns>
    /// <remarks>
    /// ⚠️ <c>IFateTable</c> 的列舉器<b>可能吐出 null</b>
    /// （<c>FateTable.CreateFateReference</c> 在玩家資料未載入時回 null，
    /// 而列舉器是直接 <c>yield return this[i]</c>），所以一定要判 null。
    /// </remarks>
    private bool TryBuildSnapshot()
    {
        snapshot.Clear();

        var config = Plugin.Instance.Config.FateTracker;
        var playerPos = Svc.Objects.LocalPlayer?.Position;

        try
        {
            foreach (var fate in Svc.Fates)
            {
                if (fate == null) continue;

                var state = fate.State;
                if (!config.ShowEnded && state is FateState.Ended or FateState.Failed)
                    continue;

                float? distance = null;
                var position = fate.Position;
                if (playerPos is { } p)
                {
                    // 平面距離：垂直落差在很多地圖上大得離譜（空島、坑洞），
                    // 算進去只會讓「還有多遠」這個數字失去意義。
                    distance = Vector2.Distance(
                        new Vector2(p.X, p.Z), new Vector2(position.X, position.Z));
                }

                snapshot.Add(new FateSnapshot(
                    fate.FateId,
                    fate.Name.TextValue,
                    fate.Level,
                    fate.MaxLevel,
                    fate.Progress,
                    state,
                    fate.HasBonus,
                    fate.TimeRemaining,
                    fate.Duration,
                    fate.StartTimeEpoch,
                    position,
                    fate.Radius,
                    fate.Objective.TextValue,
                    fate.Description.TextValue,
                    distance));
            }
        }
        catch (Exception ex)
        {
            // 每幀都印會把記錄洗爆，所以節流；但等級用 Information——使用者跑 LogLevel 1，
            // Debug 收得到但單檔數十萬行會淹沒，而這一行是事後唯一能看出「清單為什麼是空的」的證據。
            if (Throttle.Pass("FateTracker-SnapshotError", 30_000))
                Svc.Log.Information(ex, "[FateTracker] 枚舉 F.A.T.E. 清單時發生例外，本幀跳過");

            snapshot.Clear();
            return false;
        }

        if (config.SortByDistance)
        {
            snapshot.Sort(static (a, b) =>
            {
                // 距離未知的排最後，彼此之間再用 id 保持穩定。
                var da = a.Distance ?? float.MaxValue;
                var db = b.Distance ?? float.MaxValue;
                var cmp = da.CompareTo(db);
                return cmp != 0 ? cmp : a.FateId.CompareTo(b.FateId);
            });
        }
        else
        {
            snapshot.Sort(static (a, b) => a.FateId.CompareTo(b.FateId));
        }

        return true;
    }

    /// <summary>把 vnavmesh 的四個狀態一次探完並快取（見欄位上的說明）。</summary>
    private void RefreshVnavProbe()
    {
        if (!Throttle.Pass("FateTracker-VnavProbe", 500)) return;

        vnavReady = ExternalNav.IsVnavmeshReady();

        // 網格就緒必然代表外掛在，可以省一次探測（也省掉一次例外）。
        vnavInstalled = vnavReady || ExternalNav.IsVnavmeshInstalled();

        // 外掛不在就不必問後面兩個——問了也只是白白擲兩個例外。
        vnavPathRunning = vnavInstalled && ExternalNav.IsVnavmeshPathRunning();
        vnavPathfinding = vnavInstalled && ExternalNav.IsVnavmeshPathfindInProgress();

        // 走到了（或被別的東西停掉了）就把目標清掉，狀態列跟著消失——那就是「已抵達」的信號。
        // 📌 這裡沒有競態：ExternalNav.TryMoveTo 一回 true，vnavmesh 端的 _pendingTask 就已經
        //    是非 null 了（AsyncMoveRequest.MoveTo 是同步設定的），所以「剛按下」這一刻
        //    vnavPathfinding 必為 true，不會被誤判成「已經結束」。
        if (navTargetFateId != 0
            && !vnavPathRunning
            && !vnavPathfinding
            && stopEnforceUntil == DateTime.MinValue)
        {
            navTargetFateId = 0;
        }
    }

    private void DrawHeader(bool snapshotOk)
    {
        var config = Plugin.Instance.Config.FateTracker;

        if (snapshotOk)
            ImGui.TextUnformatted($"本區 F.A.T.E.：{snapshot.Count}");
        else
            ImGui.TextColored(UnknownColor, "本區 F.A.T.E.：？");

        ImGui.SameLine();
        ImGui.Dummy(new Vector2(12f, 0f));
        ImGui.SameLine();

        var showEnded = config.ShowEnded;
        if (ImGui.Checkbox("顯示已結束", ref showEnded))
        {
            config.ShowEnded = showEnded;
            Plugin.Instance.Config.Save();
        }

        ImGui.SameLine();
        var sortByDistance = config.SortByDistance;
        if (ImGui.Checkbox("依距離排序", ref sortByDistance))
        {
            config.SortByDistance = sortByDistance;
            Plugin.Instance.Config.Save();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "預設關閉是刻意的：依距離排序時，你一走動列就會互換位置，\n"
                + "而每一列上都有一顆會讓角色真的跑起來的「導航」按鈕——\n"
                + "列在游標下跳動就等於按錯目標。距離那一欄本來就看得到。");
        }

        ImGui.SameLine();
        var allowFly = config.AllowFly;
        if (ImGui.Checkbox("允許飛行", ref allowFly))
        {
            config.AllowFly = allowFly;
            Plugin.Instance.Config.Save();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "讓 vnavmesh 用飛行路徑導航。\n"
                + "⚠️ 在還沒解鎖飛行的區域開這個，vnavmesh 會算不出路徑，\n"
                + "表現出來就是「按了沒反應」。");
        }

        DrawNavStatusLine();
    }

    /// <summary>
    /// 目前這趟導航的狀態＋停止按鈕。
    /// </summary>
    /// <remarks>
    /// 📌 停止按鈕<b>不以「正在移動」為顯示條件</b>：探測是 500ms 快取的，按下導航後
    /// 那半秒內按鈕會是灰的，而那恰好是最想反悔的半秒。<c>Path.Stop</c> 對「本來就沒在動」
    /// 是安全的無操作，所以只要外掛在就一直讓它可按。
    /// </remarks>
    private void DrawNavStatusLine()
    {
        if (!vnavInstalled)
        {
            ImGui.TextColored(UnknownColor, "未偵測到 vnavmesh，導航功能停用（清單仍可正常使用）。");
            return;
        }

        var enforcing = stopEnforceUntil != DateTime.MinValue;

        // 🔴 vnavmesh 是全艦隊共用的：Path.IsRunning 為真不代表那是「我們」發起的移動，
        //    也可能是 Lifestream／ICE／Questionable 正在跑。把別人的移動標成
        //    「移動中 →『某某 F.A.T.E.』」是在說謊，而且會誘使使用者在這裡按停止，
        //    莫名其妙打斷另一個外掛的工作。⇒ 只有 navTargetFateId 非 0 才敢署名，
        //    停止按鈕也只在那時候給。
        var ours = navTargetFateId != 0 || enforcing;

        if (ours)
        {
            if (enforcing)
                ImGui.TextColored(WarnColor, "正在停止移動…");
            else if (vnavPathfinding)
                ImGui.TextColored(WarnColor, $"正在計算路徑 → 「{navTargetName}」");
            else if (vnavPathRunning)
                ImGui.TextColored(WarnColor, $"移動中 → 「{navTargetName}」");
            else
                ImGui.TextDisabled($"上一個導航目標：「{navTargetName}」");

            ImGui.SameLine();
            if (ImGui.Button("停止移動###fateStop"))
                RequestStop();

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    "立刻停止 vnavmesh 的移動。\n"
                    + "走到目的地時 vnavmesh 本來就會自己停下來，這顆是給你中途反悔用的。\n"
                    + "⚠️ 若還在「計算路徑」階段，會持續補送停止指令直到確定停住"
                    + "（vnavmesh 的停止指令攔不住還沒算完的路徑）。");
            }
        }
        else if (vnavPathRunning || vnavPathfinding)
        {
            ImGui.TextDisabled("vnavmesh 正在移動中，但不是本模組發起的（其他外掛）。");
        }
        else if (!vnavReady)
        {
            ImGui.TextColored(UnknownColor, "vnavmesh 的導航網格尚未就緒（正在載入，或本區沒有網格）。");
        }
    }

    private void DrawTable()
    {
        if (!ImGui.BeginTable("##fateList", 6,
                              ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.ScrollY))
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("名稱", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("等級", ImGuiTableColumnFlags.WidthFixed, 48f);
        ImGui.TableSetupColumn("進度", ImGuiTableColumnFlags.WidthFixed, 96f);
        ImGui.TableSetupColumn("剩餘時間", ImGuiTableColumnFlags.WidthFixed, 68f);
        ImGui.TableSetupColumn("距離", ImGuiTableColumnFlags.WidthFixed, 60f);
        ImGui.TableSetupColumn("操作", ImGuiTableColumnFlags.WidthFixed, 56f);
        ImGui.TableHeadersRow();

        foreach (var fate in snapshot)
            DrawRow(fate);

        ImGui.EndTable();
    }

    private void DrawRow(in FateSnapshot fate)
    {
        // 同一個 F.A.T.E. 的 id 在本幀唯一，拿來當 ImGui 的 id 範圍最穩
        // （用列序號的話，清單一重排按鈕的狀態就會跟著錯位）。
        using var rowId = ImRaii.PushId(fate.FateId);

        ImGui.TableNextRow();

        // ① 名稱（＋額外獎勵標記）
        ImGui.TableNextColumn();
        var running = fate.State == FateState.Running;
        if (running)
            ImGui.TextUnformatted(fate.Name);
        else
            ImGui.TextDisabled(fate.Name);

        var nameHovered = ImGui.IsItemHovered();

        if (fate.HasBonus)
        {
            ImGui.SameLine();
            ImGui.TextColored(BonusColor, "★");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("額外獎勵");
        }

        if (nameHovered)
            ImGui.SetTooltip(BuildTooltip(fate));

        // ② 等級
        ImGui.TableNextColumn();
        ImGui.TextUnformatted($"Lv.{fate.Level}");

        // ③ 進度
        ImGui.TableNextColumn();
        if (fate.State == FateState.Preparation)
        {
            ImGui.TextDisabled("準備中");
        }
        else
        {
            // 📌 ProgressBar 的 overlay 是直接拿去畫的字串，不是格式字串，
            //    所以百分號寫一個就好（寫兩個會原樣顯示成 "50%%"）。
            var pct = fate.Progress > 100 ? (byte)100 : fate.Progress;
            ImGui.ProgressBar(pct / 100f, new Vector2(-1f, 0f), $"{fate.Progress}%");
        }

        // ④ 剩餘時間
        ImGui.TableNextColumn();
        if (fate.State == FateState.Preparation)
        {
            ImGui.TextDisabled("準備中");
        }
        else if (!TryGetRemainingSeconds(fate, out var remaining))
        {
            // 🔑「不知道」要在列上看得見——絕不畫成 0，那會被讀成「快結束了」。
            ImGui.TextColored(UnknownColor, "？");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("讀到的開始時間或持續時間不合理，無法算出可信的剩餘時間。");
        }
        else
        {
            var text = $"{remaining / 60}:{remaining % 60:00}";
            if (remaining <= EndingSoonSeconds)
                ImGui.TextColored(WarnColor, text);
            else
                ImGui.TextUnformatted(text);
        }

        // ⑤ 距離
        ImGui.TableNextColumn();
        if (fate.Distance is { } d)
        {
            ImGui.TextUnformatted($"{d:F0}");
        }
        else
        {
            ImGui.TextColored(UnknownColor, "？");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("讀不到自己的座標，無法計算距離。");
        }

        // ⑥ 導航
        ImGui.TableNextColumn();
        DrawNavButton(fate);
    }

    /// <summary>
    /// 每列的「導航」按鈕。不可用時<b>停用按鈕並在 tooltip 說明原因</b>——
    /// 不擲例外，也不做「按了沒反應」這種靜默失敗。
    /// </summary>
    private void DrawNavButton(in FateSnapshot fate)
    {
        var reason = GetNavBlockedReason(fate);
        var blocked = reason != null;

        if (blocked) ImGui.BeginDisabled();

        var clicked = ImGui.Button("導航");

        if (blocked) ImGui.EndDisabled();

        // ⚠️ 停用中的項目預設<b>不會</b>回報 hover，tooltip 會整個消失
        //    （這正是「按鈕停用＋tooltip 說明原因」最常見的失敗方式）。
        //    要拿 AllowWhenDisabled 才問得到。
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(reason ?? $"走到「{fate.Name}」。\n到達後就停下來——不會自動接受、不會自動戰鬥。");
        }

        if (clicked && !blocked)
            StartNavigation(fate);
    }

    /// <summary>導航按鈕不能按的原因；null＝可以按。</summary>
    private string? GetNavBlockedReason(in FateSnapshot fate)
    {
        if (!vnavInstalled)
            return "未偵測到 vnavmesh 外掛，無法自動移動。\n安裝並啟用 vnavmesh 後這顆按鈕就會亮起來。";

        if (!vnavReady)
            return "vnavmesh 的導航網格尚未就緒。\n可能正在背景載入，也可能這個區域沒有網格；稍候再試。";

        if (vnavPathfinding)
            return "vnavmesh 正在計算上一個路徑，這時候下新的指令會被它拒絕。\n請稍候，或先按上方的「停止移動」。";

        if (fate.State is FateState.Ended or FateState.Failed)
            return "這個 F.A.T.E. 已經結束了。";

        return null;
    }

    /// <summary>
    /// 下達一次導航指令。<b>就只有這一件事</b>——不排隊、不等抵達、不接後續動作。
    /// </summary>
    private void StartNavigation(in FateSnapshot fate)
    {
        // 先前那趟（如果有）就此作廢：使用者按了新的目標，意思就是不要舊的了。
        stopEnforceUntil = DateTime.MinValue;
        stopEnforceStartedAt = DateTime.MinValue;

        var config = Plugin.Instance.Config.FateTracker;

        if (!ExternalNav.TryMoveTo(fate.Position, config.AllowFly, out var started, DisplayName))
        {
            // IPC 整個打不通（外掛剛被拆掉之類）。快取狀態顯然過期了，強制下一幀重探。
            Throttle.Reset("FateTracker-VnavProbe");
            Svc.Chat.Print("[TC Toolbox] 無法呼叫 vnavmesh，導航未開始。");
            return;
        }

        if (!started)
        {
            // vnavmesh 收到了但拒絕了。已知的原因是它手上還有一個沒算完的路徑
            // （AsyncMoveRequest.MoveTo 在 _pendingTask 非 null 時直接回 false）。
            Throttle.Reset("FateTracker-VnavProbe");
            Svc.Chat.Print("[TC Toolbox] vnavmesh 拒絕了這次導航（多半是上一個路徑還在計算中），請稍候再試。");
            return;
        }

        navTargetFateId = fate.FateId;
        navTargetName = fate.Name;

        // 讓狀態列立刻反映「開始動了」，不要等 500ms 的快取到期。
        Throttle.Reset("FateTracker-VnavProbe");

        // 使用者回報用的定錨點：出事時這一行是唯一能證明「移動是他自己按的、目標是哪一個」的證據。
        Svc.Log.Information(
            $"[FateTracker] 使用者手動導航 → F.A.T.E. {fate.FateId}「{fate.Name}」"
            + $" 座標 {fate.Position:F1} 飛行={config.AllowFly}");
    }

    private void RequestStop()
    {
        ExternalNav.TryStopMovement();

        // 開啟補送窗口：光送一次擋不住還在計算中的路徑（見 StopEnforceWindow 的說明）。
        var stopNow = DateTime.UtcNow;

        // 絕對上限自「這一輪的第一次停止」起算：連按停止不會把上限一直往後推。
        if (stopEnforceStartedAt == DateTime.MinValue)
            stopEnforceStartedAt = stopNow;

        stopEnforceUntil = stopNow + StopEnforceWindow;
        Throttle.Reset("FateTracker-StopEnforce");
        Throttle.Reset("FateTracker-VnavProbe");

        navTargetFateId = 0;

        Svc.Log.Information("[FateTracker] 使用者手動停止導航");
    }

    /// <summary>
    /// 算出可信的剩餘秒數。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>不能直接信 <c>IFate.TimeRemaining</c>。</b>它的實作是
    /// <c>StartTimeEpoch + Duration - 現在的 Unix 時間</c>，所以 F.A.T.E. 還沒開始
    /// （<c>StartTimeEpoch</c> 是 0）時算出來的是一個以 1970 為基準的<b>幾十億的負數</b>，
    /// 而不是任何形式的錯誤。直接格式化會印出很荒謬的東西；夾成 0 更糟——
    /// 那會被讀成「剩 0 秒，快結束了」，剛好是事實的相反。
    /// </remarks>
    private static bool TryGetRemainingSeconds(in FateSnapshot fate, out long seconds)
    {
        seconds = 0;

        if (fate.StartTimeEpoch <= 0) return false;
        if (fate.Duration <= 0) return false;

        var remaining = fate.TimeRemaining;

        // 比總長還久＝現在時間比開始時間還早（還沒開跑，或時鐘對不上）。這時說不出剩多久。
        if (remaining > fate.Duration) return false;

        seconds = remaining < 0 ? 0 : remaining;
        return true;
    }

    private static string BuildTooltip(in FateSnapshot fate)
    {
        var lines = new List<string>
        {
            fate.Name,
            $"狀態：{DescribeState(fate.State)}",
            $"等級：{fate.Level}"
            + (fate.MaxLevel > fate.Level ? $"（上限 {fate.MaxLevel}）" : string.Empty),
        };

        if (fate.HasBonus)
            lines.Add("額外獎勵：可以獲得額外獎勵");

        if (!string.IsNullOrWhiteSpace(fate.Objective))
            lines.Add($"目標：{fate.Objective}");

        if (!string.IsNullOrWhiteSpace(fate.Description))
        {
            lines.Add(string.Empty);
            lines.Add(fate.Description);
        }

        lines.Add(string.Empty);
        lines.Add($"半徑：{fate.Radius:F0}");
        lines.Add($"座標：X {fate.Position.X:F0}　Y {fate.Position.Y:F0}　Z {fate.Position.Z:F0}");

        return string.Join("\n", lines);
    }

    private static string DescribeState(FateState state) => state switch
    {
        FateState.Preparation => "準備中",
        FateState.Running => "進行中",
        FateState.WaitingForEnd => "即將結束",
        FateState.Ended => "已結束",
        FateState.Failed => "已失敗",
        _ => "未知",
    };
}

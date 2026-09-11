using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// Questionable 目前步驟顯示到 Mappy ＋ 伺服器資訊列。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 <b>純顯示，零介入。</b>只呼叫 Questionable 的兩支唯讀端點
/// （<c>IsRunning</c>／<c>GetCurrentStepData</c>）；它同一個 provider 上那些會改變行為的端點
/// （<c>StartQuest</c>／<c>Stop</c>／<c>ImportQuestPriority</c>…）一支都不碰。
/// </para>
/// <para>
/// 📌 <b>解決的問題</b>：Questionable 在跑的時候，「它現在到底想幹嘛、要去哪裡」只寫在它自己的
/// 視窗裡。把目標位置畫到地圖上、把互動類型放進伺服器資訊列之後，不必再開第二扇視窗就看得出來。
/// </para>
/// <para>
/// 🔴 <b>預設關。</b>地圖上已經有別的東西在畫（NecroLens 之類的在世界上畫），
/// 要不要再多一個來源應該由使用者自己決定。
/// </para>
/// <para>
/// 🔴🔴 <b>每秒問一次，而且只在框架執行緒上問。</b>對方的端點包在它自己的
/// <c>IpcFrameworkGate</c> 裡，從繪製路徑呼叫會在主執行緒上等一個要靠主執行緒才跑得到的 tick。
/// 設定畫面上顯示的每一個數字都是這裡快取下來的。
/// </para>
/// </remarks>
public sealed class QuestionableStepOnMappy : TcModule
{
    public override string InternalName => "QuestionableStepOnMappy";

    public override string DisplayName => "任務進度顯示到地圖與資訊列";

    public override string Description =>
        "Questionable 在跑任務時，把它「目前這一步要去哪裡」畫成 Mappy 地圖上的一個標記，"
        + "並在伺服器資訊列顯示目前的互動類型（互動／移動／戰鬥／製作…）。"
        + "Questionable 在跑、而 AutoRetainer 的多角色模式也開著時會另外提醒你："
        + "AutoRetainer 換角色會把任務中斷在半路，而且它不會自己接回去。"
        + "純顯示：不下指令、不改 Questionable 或 AutoRetainer 的任何設定。"
        + "需要同時安裝 Questionable 與 Mappy。"
        + "預設關閉——地圖上要不要再多一個標記來源由你決定。";

    public override ModuleCategory Category => ModuleCategory.Misc;

    public override bool HasConfigUI => true;

    /// <summary>
    /// 放到 Mappy 的標記來源名稱。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>不要改。</b>Mappy 端拿這個字串當鍵記住「這個來源要不要顯示」，
    /// 改了之後使用者原本關掉的來源會變成一個永遠留在 Mappy 設定裡的孤兒。
    /// </remarks>
    public const string MarkerSource = "TCToolbox.QuestionableStep";

    /// <summary>輪詢間隔（毫秒）。</summary>
    /// <remarks>
    /// 📌 一秒一次。Questionable 換步驟是以秒計的，每幀問一次沒有任何好處，
    /// 而每一次都是一趟跨組件的 JSON 來回轉換。
    /// </remarks>
    private const int PollIntervalMs = 1_000;

    /// <summary>無條件全量重推的間隔（毫秒）——Mappy 可能在我們沒看見的時候被重載過。</summary>
    private const int ForceResyncIntervalMs = 60_000;

    /// <summary>設定畫面在模組關著時重新探測「對方在不在」的間隔（毫秒）。</summary>
    private const int UiProbeIntervalMs = 2_000;

    /// <summary>
    /// 目前步驟的預設圖示。
    /// </summary>
    /// <remarks>
    /// 📌 <b>語意有遊戲資料背書</b>：60428 是 <c>MapSymbol</c> 第 55 列的圖示，
    /// 該列的 <c>PlaceName</c> 在台服逐字是「大型任務」——也就是遊戲自己拿來標任務的那顆。
    /// ✅ 2026-09-08 以 <c>tools/sqpack/path_exists.py</c> 離線直讀台服 <c>060000.win32.index</c>
    /// 確認 <c>ui/icon/060000/060428.tex</c> 存在（校準閘門通過）。
    /// ⚠️ 圖示的「存在」與「長什麼樣子」是兩件事，所以仍然做成可設定的。
    /// </remarks>
    public const uint DefaultStepIconId = 60428;

    private uint EffectiveIcon => Config.StepIconId is 0 ? DefaultStepIconId : Config.StepIconId;

    /// <summary>橋接目前的狀態。<b>「不知道」是零值</b>。</summary>
    private enum BridgeState
    {
        /// <summary>還沒問過任何一次。</summary>
        Unknown = 0,

        /// <summary>Questionable 沒裝或還沒註冊 IPC。</summary>
        QuestionableMissing,

        /// <summary>Questionable 在，但目前沒有進行中的步驟。</summary>
        Idle,

        /// <summary>有步驟，一切正常。</summary>
        Running,
    }

    private QuestionableStepConfig Config => Plugin.Instance.Config.QuestionableStep;

    private readonly MappyMarkerPublisher publisher = new(MarkerSource);

    private readonly List<MappyMarkerPublisher.Marker> pending = [];

    private IDtrBarEntry? dtrEntry;

    // ── 給 UI 看的快取（只在框架執行緒上寫，繪製路徑只讀）────────────────────
    private BridgeState state = BridgeState.Unknown;
    private string lastQuestId = string.Empty;
    private byte lastSequence;
    private int lastStep;
    private string lastInteraction = string.Empty;
    private uint lastTerritory;
    private Vector3? lastPosition;
    private bool lastMarkerPlaced;

    /// <summary>
    /// Questionable 自己回報的「正在跑」。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>它的語意比字面寬</b>：提供端是
    /// <c>AutomationType != Manual || questController.IsRunning</c>
    /// （Questionable <c>External/QuestionableIpc.cs:120-122</c>，2026-09-11 逐字對照），
    /// 也就是<b>自動模式開著但還沒動手</b>，以及<b>手動按了一步、佇列還沒跑完</b>，兩種都算 true。
    /// <para>
    /// 🔑 <b>而且 <c>GetCurrentStepData</c> 分不出來，只會更寬</b>：它非 null 的條件是
    /// <c>CurrentQuest != null</c>，而 <c>CurrentQuest</c> 走
    /// <c>SimulatedQuest ?? NextQuest ?? GatheringQuest ?? StartedQuest</c>
    /// （<c>Controller/QuestController.cs:202-229</c>）——光是「追蹤著下一個任務」就成立，
    /// 完全沒在跑也會有步驟。所以「真的在自動跑」<b>沒有</b>更精確的端點可問，
    /// <c>IsRunning</c> 已經是最緊的那一個。
    /// </para>
    /// <para>
    /// 📌 用在提醒上，寬的方向是安全的：寧可在「自動模式開著但還沒動」時就先提醒，
    /// 也不要等它真的走到一半被登出，使用者才發現任務停在半路。
    /// </para>
    /// </remarks>
    private bool questionableRunning;

    /// <summary>AutoRetainer 多角色模式的三態；只在 <see cref="questionableRunning"/> 為真時才去問。</summary>
    private AutoRetainerIpc.MultiModeState arMultiMode = AutoRetainerIpc.MultiModeState.Unknown;

    /// <summary>上面那個三態是怎麼來的（放 tooltip，不占列上版面）。</summary>
    private string arMultiModeDetail = string.Empty;

    /// <summary>上一次真的推上去的內容簽章；空字串＝沒有標記。</summary>
    private string lastSignature = string.Empty;

    private bool mappyWasAvailable;

    protected override void OnEnable()
    {
        dtrEntry = Svc.DtrBar.Get("TC Toolbox 任務進度");
        dtrEntry.Shown = false;
        dtrEntry.Tooltip = "TC Toolbox — Questionable 目前步驟";
        dtrEntry.OnClick = _ => Plugin.Instance.ToggleMainWindow();

        Throttle.Reset(PollThrottleKey);
        Throttle.Reset(ForceResyncThrottleKey);
        lastSignature = string.Empty;
        state = BridgeState.Unknown;
        questionableRunning = false;
        arMultiMode = AutoRetainerIpc.MultiModeState.Unknown;
        arMultiModeDetail = string.Empty;

        Svc.Framework.Update += OnUpdate;

        Svc.Log.Information(
            $"[{InternalName}] 模組啟用：顯示地圖標記＝{Config.ShowMarker}、顯示資訊列＝{Config.ShowDtr}、"
            + $"圖示 {EffectiveIcon}（設定值 {Config.StepIconId}，0＝跟隨內建預設）");
    }

    protected override void OnDisable()
    {
        Svc.Framework.Update -= OnUpdate;

        // 🔴 停用（含外掛卸載）一定要把標記收乾淨，否則 Mappy 會一直畫著一個沒人再更新的舊標記。
        publisher.Clear();

        dtrEntry?.Remove();
        dtrEntry = null;

        lastSignature = string.Empty;
        mappyWasAvailable = false;
        lastMarkerPlaced = false;
        state = BridgeState.Unknown;
        lastPosition = null;
        lastInteraction = string.Empty;
        lastQuestId = string.Empty;
        questionableRunning = false;
        arMultiMode = AutoRetainerIpc.MultiModeState.Unknown;
        arMultiModeDetail = string.Empty;
    }

    private string PollThrottleKey => $"TCToolbox.{InternalName}.Poll";

    private string ForceResyncThrottleKey => $"TCToolbox.{InternalName}.ForceResync";

    private void OnUpdate(IFramework framework)
    {
        if (!Svc.ClientState.IsLoggedIn) return;
        if (!Throttle.Pass(PollThrottleKey, PollIntervalMs)) return;

        // 🔴 這一支只能在框架執行緒上呼叫（見 QuestionableIpc 的類別註解）。這裡就是。
        if (!QuestionableIpc.TryGetCurrentStep(out var step))
        {
            if (state != BridgeState.QuestionableMissing)
            {
                publisher.Clear();
                lastSignature = string.Empty;
                lastMarkerPlaced = false;
            }

            state = BridgeState.QuestionableMissing;

            // 🔑 Questionable 都不在了，「有沒有跟 AutoRetainer 撞在一起」這個問題本身消失，
            //    所以退回「不知道」而不是「沒有衝突」——兩者在畫面上是不同的東西。
            questionableRunning = false;
            arMultiMode = AutoRetainerIpc.MultiModeState.Unknown;
            arMultiModeDetail = string.Empty;

            UpdateDtr(null);
            return;
        }

        RefreshConflict();

        if (step == null)
        {
            if (state == BridgeState.Running)
            {
                // 剛跑完／被停下來：把標記收掉，不要留一個沒人維護的舊目標在地圖上。
                publisher.Clear();
                lastSignature = string.Empty;
                lastMarkerPlaced = false;
            }

            state = BridgeState.Idle;
            UpdateDtr(null);
            return;
        }

        state = BridgeState.Running;

        lastQuestId = step.QuestId;
        lastSequence = step.Sequence;
        lastStep = step.Step;
        lastInteraction = step.InteractionType;
        lastTerritory = step.TerritoryId;
        lastPosition = step.Position;

        UpdateDtr(step);
        SyncMarker(step);
    }

    // ── 「兩個自動化同時開著」的偵測 ────────────────────────────────────────

    /// <summary>
    /// 衝突提示的內文。列上只放一句話，理由放這裡。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>純顯示。</b>這個模組不會、也不該去關掉任何一邊——兩邊都是使用者自己開的，
    /// 而「哪一邊該讓」只有他知道（先跑完這段任務，還是先讓僱員輪一圈）。
    /// </remarks>
    private const string ConflictExplanation =
        "AutoRetainer 的多角色模式開著，而 Questionable 正在跑任務。\n"
        + "多角色模式輪到下一個角色時會把目前這個角色登出，Questionable 就停在半路，\n"
        + "而且它不會在新角色上自己接回去——回來之後要自己再按一次開始。\n"
        + "要跑完這段任務就先把多角色模式關掉；要讓 AutoRetainer 換角就先把 Questionable 停下來。\n"
        + "這個模組只顯示，不會替你關掉任何一邊。";

    /// <summary>兩個自動化確定同時開著。</summary>
    private bool HasConflict =>
        questionableRunning && arMultiMode == AutoRetainerIpc.MultiModeState.On;

    /// <summary>Questionable 在跑，但 AutoRetainer 那邊問不出來——「不知道」，不是「沒事」。</summary>
    private bool ConflictUnknown =>
        questionableRunning && arMultiMode == AutoRetainerIpc.MultiModeState.Unknown;

    /// <summary>
    /// 重新判斷「Questionable 在不在跑」與「AutoRetainer 的多角色模式開不開著」。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 📌 <b>只有 Questionable 在跑的時候才去問 AutoRetainer。</b>沒有人在跑任務時，
    /// 多角色模式開著完全是正常狀態，去問它只是多打一趟 IPC、多一個沒人要看的答案。
    /// </para>
    /// <para>
    /// 🔴 <b>沒裝 AutoRetainer 的人不該看到「？問不到」。</b>那不是「不知道」，
    /// 那是「這個問題不存在」——所以三態裡特別留了
    /// <see cref="AutoRetainerIpc.MultiModeState.NotInstalled"/>，畫面上什麼都不顯示。
    /// </para>
    /// <para>
    /// ⚠️ 在框架執行緒上呼叫（<see cref="OnUpdate"/> 裡），IPC 的實作跑在呼叫端的執行緒上。
    /// </para>
    /// </remarks>
    private void RefreshConflict()
    {
        // 🔑 走到這裡代表 Questionable 的 IPC 打得通（TryGetCurrentStep 已經成功回來了），
        //    所以這一支回 false 就是真的「沒在跑」，不是「問不到」。
        questionableRunning = QuestionableIpc.TryIsRunning(out var running) && running;

        if (!questionableRunning)
        {
            arMultiMode = AutoRetainerIpc.MultiModeState.Unknown;
            arMultiModeDetail = string.Empty;
            return;
        }

        var previous = arMultiMode;
        arMultiMode = AutoRetainerIpc.GetMultiModeState(out arMultiModeDetail);

        // 📌 只在狀態真的變了才寫記錄：每秒問一次，每次都寫會把記錄洗掉。
        //    🔴 Information 級：使用者跑 LogLevel 1，而「當時到底有沒有撞在一起」
        //       事後只能靠這一行回推。
        if (previous == arMultiMode) return;

        Svc.Log.Information(
            $"[{InternalName}] Questionable 正在跑任務；AutoRetainer 多角色模式＝{arMultiMode}"
            + $"（{arMultiModeDetail.Replace("\n", "；")}）");
    }

    // ── 伺服器資訊列 ────────────────────────────────────────────────────────

    /// <summary>更新資訊列那一格。<paramref name="step"/> 為 <c>null</c>＝沒有東西可說，藏起來。</summary>
    /// <remarks>
    /// 📌 <b>沒在跑任務時整格藏起來</b>，不是顯示一個「－」。那不是「不知道」，
    /// 是「這時候本來就沒有任務進度可談」——留一格空的只會佔位。
    /// </remarks>
    private void UpdateDtr(QuestionableIpc.StepData? step)
    {
        if (dtrEntry == null) return;

        var conflict = HasConflict;

        // 🔴 <b>衝突比「沒有步驟可說」重要。</b>Questionable 可能是「自動模式開著、還沒走到
        //    任何一步」——那正是最該提醒的時機（一換角就前功盡棄），所以這種情況照樣顯示。
        if (!Config.ShowDtr || (step == null && !conflict))
        {
            dtrEntry.Shown = false;
            return;
        }

        if (step == null)
        {
            dtrEntry.Text = new SeString(new TextPayload("任務自動化：與 AR 換角衝突"));
            dtrEntry.Tooltip = "TC Toolbox — 兩個自動化同時開著\n" + ConflictExplanation;
            dtrEntry.Shown = true;
            return;
        }

        var interaction = InteractionLabel(step.InteractionType);

        dtrEntry.Text = new SeString(new TextPayload(
            conflict ? $"跑任務中：{interaction}（與 AR 換角衝突）" : $"跑任務中：{interaction}"));

        var sb = new StringBuilder();
        sb.Append("TC Toolbox — Questionable 目前步驟");
        sb.Append('\n').Append("任務：").Append(string.IsNullOrEmpty(step.QuestId) ? "？" : step.QuestId);
        sb.Append('\n').Append("進度：第 ").Append(step.Sequence).Append(" 段第 ").Append(step.Step).Append(" 步");
        sb.Append('\n').Append("動作：").Append(interaction);

        // ⚠️ 翻譯過的話把原文一起附上：對照 Questionable 自己的視窗時看的是英文列舉名。
        if (!string.Equals(interaction, step.InteractionType, StringComparison.Ordinal))
            sb.Append('（').Append(step.InteractionType).Append('）');

        sb.Append('\n').Append("座標：").Append(DescribePosition(step));

        if (conflict)
            sb.Append('\n').Append('\n').Append(ConflictExplanation);

        dtrEntry.Tooltip = sb.ToString();
        dtrEntry.Shown = true;
    }

    private string DescribePosition(QuestionableIpc.StepData step)
    {
        if (step.Position is not { } pos) return "這一步沒有座標";

        var zone = ZoneName(step.TerritoryId);
        var where = zone.Length > 0 ? zone : $"區域 #{step.TerritoryId}";

        if (!MapCoords.TryWorldToMap(MapIdOf(step.TerritoryId), pos, out var map))
            return $"{where}（換算不出地圖座標）";

        return $"{where} "
               + map.X.ToString("F1", CultureInfo.InvariantCulture)
               + ", "
               + map.Y.ToString("F1", CultureInfo.InvariantCulture);
    }

    // ── 地圖標記 ────────────────────────────────────────────────────────────

    private void SyncMarker(QuestionableIpc.StepData step)
    {
        if (!Config.ShowMarker)
        {
            if (lastSignature.Length > 0)
            {
                publisher.Clear();
                lastSignature = string.Empty;
                lastMarkerPlaced = false;
            }

            return;
        }

        if (!MappyMarkerIpc.TryGetVersion(out var mappyVersion) || mappyVersion < MappyMarkerIpc.SupportedVersion)
        {
            mappyWasAvailable = false;

            // 🔴 作廢簽章＋忘掉 handle：Mappy 回來時它的標記表是空的，必須重放一次。
            lastSignature = string.Empty;
            publisher.Forget();
            lastMarkerPlaced = false;
            return;
        }

        if (!mappyWasAvailable)
        {
            lastSignature = string.Empty;
            publisher.Forget();
            mappyWasAvailable = true;
        }

        var mapId = MapIdOf(step.TerritoryId);

        if (step.Position is not { } pos || !MapCoords.TryWorldToMap(mapId, pos, out var map))
        {
            // 這一步沒有座標（換職業、等待玩家…）＝地圖上沒東西可畫。
            // 🔑 這不是錯誤，資訊列照樣會說「現在在做什麼」。
            if (lastSignature.Length > 0)
            {
                publisher.Publish([]);
                lastSignature = string.Empty;
            }

            lastMarkerPlaced = false;
            return;
        }

        // 🔑 鍵跨次穩定：同一個任務的同一步永遠是同一個鍵，換步驟才會動到標記。
        var key = $"{step.QuestId}:{step.Sequence}:{step.Step}";
        var signature = $"{key}|{mapId}|{map.X:F2}|{map.Y:F2}|{EffectiveIcon}";

        var forceFull = Throttle.Pass(ForceResyncThrottleKey, ForceResyncIntervalMs);
        if (!forceFull && signature == lastSignature) return;

        pending.Clear();
        pending.Add(new MappyMarkerPublisher.Marker(key, mapId, map, EffectiveIcon, BuildTooltip(step, map)));

        publisher.Publish(pending, forceFull);
        pending.Clear();

        lastMarkerPlaced = publisher.Placed > 0;

        // 📌 只有內容真的變了才寫記錄：每分鐘的保險重推全部都寫的話會洗掉別的行。
        if (signature != lastSignature)
        {
            // 🔴 Information 級：使用者跑 LogLevel 1。座標換算是這裡最可能靜默出錯的地方，
            //    事後只能靠這一行對證「當時標到哪裡去了」。
            Svc.Log.Information(
                $"[{InternalName}] 任務 {step.QuestId} 第 {step.Sequence}/{step.Step} 步（{step.InteractionType}）"
                + $"→ 地圖 {mapId} "
                + map.X.ToString("F1", CultureInfo.InvariantCulture) + ", "
                + map.Y.ToString("F1", CultureInfo.InvariantCulture)
                + (lastMarkerPlaced ? string.Empty : "（被 Mappy 拒絕）"));
        }

        lastSignature = signature;
    }

    private string BuildTooltip(QuestionableIpc.StepData step, Vector2 map)
    {
        var sb = new StringBuilder();
        sb.Append("Questionable：").Append(InteractionLabel(step.InteractionType));
        sb.Append('\n').Append("任務 ").Append(string.IsNullOrEmpty(step.QuestId) ? "？" : step.QuestId);
        sb.Append('（').Append("第 ").Append(step.Sequence).Append(" 段第 ").Append(step.Step).Append(" 步）");
        sb.Append('\n')
          .Append(map.X.ToString("F1", CultureInfo.InvariantCulture))
          .Append(", ")
          .Append(map.Y.ToString("F1", CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    // ── 查表 ────────────────────────────────────────────────────────────────

    private static readonly Dictionary<uint, uint> MapIdCache = [];

    /// <summary>區域 → 地圖列號；查不到回 0（Mappy 會拒絕 0，那是對的）。</summary>
    private static uint MapIdOf(uint territoryId)
    {
        if (territoryId == 0) return 0;
        if (MapIdCache.TryGetValue(territoryId, out var cached)) return cached;

        uint mapId = 0;
        try
        {
            mapId = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>()
                       .GetRowOrDefault(territoryId)?.Map.RowId ?? 0;
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, $"[{nameof(QuestionableStepOnMappy)}] 查 territory {territoryId} 的地圖失敗");
        }

        MapIdCache[territoryId] = mapId;
        return mapId;
    }

    private static readonly Dictionary<uint, string> ZoneNameCache = [];

    /// <summary>區域名稱；查不到回空字串（<b>不回一個看起來正常的假名字</b>）。</summary>
    private static string ZoneName(uint territoryId)
    {
        if (territoryId == 0) return string.Empty;
        if (ZoneNameCache.TryGetValue(territoryId, out var cached)) return cached;

        var name = string.Empty;
        try
        {
            name = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>()
                      .GetRowOrDefault(territoryId)?.PlaceName.ValueNullable?.Name.ExtractText()
                   ?? string.Empty;
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, $"[{nameof(QuestionableStepOnMappy)}] 查 territory {territoryId} 的名稱失敗");
        }

        ZoneNameCache[territoryId] = name;
        return name;
    }

    /// <summary>
    /// 把 Questionable 的 <c>EInteractionType</c> 英文列舉名翻成中文。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>查不到一律原樣回傳英文，絕不回空字串。</b>對方隨時可能加新的列舉成員，
    /// 而「資訊列上那一格突然變空白」是最難歸因的失敗形式——使用者只會覺得功能壞了。
    /// 原樣顯示至少講得出「它現在在做一件我沒見過的事，叫做 XXX」。
    /// <para>
    /// 📌 這張表是<b>顯示層</b>的方便，不是契約。少一條只是顯示英文，不影響任何行為。
    /// </para>
    /// </remarks>
    private static string InteractionLabel(string interactionType) => interactionType switch
    {
        "None" => "無",
        "Interact" => "互動",
        "WalkTo" => "移動中",
        "AttuneAethernetShard" => "同步都市傳送網",
        "AttuneAetheryte" => "同步乙太之光",
        "RegisterFreeOrFavoredAetheryte" => "設定常用乙太之光",
        "AttuneAetherCurrent" => "同步風脈泉",
        "Combat" => "戰鬥中",
        "UseItem" => "使用道具",
        "EquipItem" => "裝備道具",
        "UnequipItem" => "卸下裝備",
        "PurchaseItem" => "購買道具",
        "EquipRecommended" => "裝備推薦裝",
        "Say" => "說話",
        "Emote" => "情感動作",
        "Action" => "使用技能",
        "StatusOff" => "解除狀態",
        "WaitForObjectAtPosition" => "等待物件出現",
        "WaitForManualProgress" => "等你手動繼續",
        "Duty" => "副本",
        "SinglePlayerDuty" => "單人副本",
        "Jump" => "跳躍",
        "Dive" => "潛水",
        "Craft" => "製作",
        "Gather" => "採集",
        "Snipe" => "狙擊",
        "CreateGearset" => "建立套裝",
        "UpdateGearset" => "更新套裝",
        "SwitchClass" => "切換職業",
        "UnlockTaxiStand" => "解鎖飛行點",
        "Instruction" => "等你手動繼續",
        "AcceptQuest" => "接任務",
        "CompleteQuest" => "交任務",
        "Fish" => "釣魚",
        // 🔴 空字串代表鏡像型別對不上（那個欄位沒被填），要看得出來是「不知道」。
        "" => "？",
        _ => interactionType,
    };

    // ── 模組列上的提示 ──────────────────────────────────────────────────────

    public override ModuleNotice? RowNotice
    {
        get
        {
            if (!IsEnabled) return null;

            // 🔴 衝突排在所有其他提示之前：它是這一列上唯一「不處理會有代價」的東西
            //    （任務跑到一半被登出、而且不會自己接回去）。
            if (HasConflict)
            {
                return new ModuleNotice(
                    ModuleNoticeLevel.Warning,
                    "兩個自動化同時開著，AR 換角會中斷任務",
                    ConflictExplanation
                    + "\n\n（判斷依據：" + arMultiModeDetail + "）");
            }

            // 🔴 「不知道」要在列上看得見。AutoRetainer <b>在</b>、Questionable 也在跑，
            //    卻問不出多角色模式——那就有可能正撞在一起，只是我們說不出來。
            //    ⚠️ 對照組：AutoRetainer 根本沒裝時 arMultiMode 是 NotInstalled，
            //    那不是「不知道」而是「這個問題不存在」，什麼都不顯示。
            if (ConflictUnknown)
            {
                return new ModuleNotice(
                    ModuleNoticeLevel.Unknown,
                    "？ 問不到 AutoRetainer 的多角色模式",
                    "Questionable 正在跑任務，而 AutoRetainer 在——但問不出它的多角色模式開著沒有。\n"
                    + "多角色模式若正開著，它換角色時會把 Questionable 中斷在半路。\n\n"
                    + arMultiModeDetail);
            }

            return state switch
            {
                BridgeState.QuestionableMissing => new ModuleNotice(
                    ModuleNoticeLevel.Unknown,
                    "Questionable 未載入",
                    "找不到 Questionable 的 IPC，所以沒有任務進度可以讀。模組會靜靜地等它出現，不需要重開。"),

                BridgeState.Unknown => new ModuleNotice(
                    ModuleNoticeLevel.Unknown,
                    "尚未讀到狀態",
                    "模組剛啟用，還沒完成第一次查詢。"),

                _ => null,
            };
        }
    }

    // ── 設定 UI ─────────────────────────────────────────────────────────────

    public override void DrawConfig()
    {
        DrawStatus();

        ImGui.Separator();

        var marker = Config.ShowMarker;
        if (ImGui.Checkbox("在 Mappy 地圖上標出目前這一步的位置", ref marker))
        {
            Config.ShowMarker = marker;
            Plugin.Instance.Config.Save();
            lastSignature = string.Empty;
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "需要 Mappy。標記來源是 " + MarkerSource + "，可以在 Mappy 的設定裡單獨關掉。\n"
                + "有些步驟本來就沒有座標（切換職業、等你手動繼續…），那時候地圖上不會有標記。");
        }

        var dtr = Config.ShowDtr;
        if (ImGui.Checkbox("在伺服器資訊列顯示目前的動作", ref dtr))
        {
            Config.ShowDtr = dtr;
            Plugin.Instance.Config.Save();
            if (!dtr && dtrEntry != null) dtrEntry.Shown = false;
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("沒在跑任務的時候整格藏起來，不會一直佔著資訊列的位置。");

        if (!ImGui.CollapsingHeader("圖示###questionableStepIcon")) return;

        using var indent = ImRaii.PushIndent();

        ImGui.TextDisabled("預設值取自遊戲地圖圖例裡「大型任務」用的那顆。覺得不好認就改成別的編號。");

        GameIcons.DrawIconIdSetting("目前步驟", Config.StepIconId, DefaultStepIconId, value =>
        {
            Config.StepIconId = value;
            Plugin.Instance.Config.Save();
            lastSignature = string.Empty;
        });
    }

    /// <summary>
    /// 設定畫面上的狀態列。
    /// </summary>
    /// <remarks>
    /// 🔴 這裡<b>只呼叫 <c>Questionable.IsRunning</c>，絕不呼叫 <c>GetCurrentStepData</c></b>：
    /// 後者包在對方的 <c>IpcFrameworkGate</c> 裡，從繪製路徑呼叫有阻塞主執行緒的風險。
    /// 需要內容的資訊一律用 <see cref="OnUpdate"/> 快取下來的值。
    /// </remarks>
    private void DrawStatus()
    {
        if (!IsEnabled && Throttle.Pass($"TCToolbox.{InternalName}.UiProbe", UiProbeIntervalMs))
        {
            state = QuestionableIpc.TryIsRunning(out _)
                ? BridgeState.Unknown
                : BridgeState.QuestionableMissing;
        }

        // 🔴 畫在 switch <b>之前</b>：下面每一個分支都會 return，而衝突在
        //    BridgeState.Idle（自動模式開著、還沒走到任何一步）時照樣成立。
        DrawConflictLine();

        switch (state)
        {
            case BridgeState.QuestionableMissing:
                ImGui.TextDisabled("Questionable 未載入——沒有任務進度可以讀。");
                return;

            case BridgeState.Idle:
                ImGui.TextDisabled("Questionable 在，但目前沒有進行中的步驟。");
                return;

            case BridgeState.Unknown:
                ImGui.TextDisabled(IsEnabled ? "尚未完成第一次查詢。" : "Questionable 在，啟用模組後開始顯示。");
                return;

            case BridgeState.Running:
            default:
                break;
        }

        var interaction = InteractionLabel(lastInteraction);
        ImGui.TextUnformatted($"目前：{interaction}（任務 {(string.IsNullOrEmpty(lastQuestId) ? "？" : lastQuestId)}，第 {lastSequence} 段第 {lastStep} 步）");

        if (!ImGui.IsItemHovered()) return;

        var sb = new StringBuilder();

        if (!string.Equals(interaction, lastInteraction, StringComparison.Ordinal))
            sb.Append("原始互動類型：").Append(string.IsNullOrEmpty(lastInteraction) ? "？" : lastInteraction).Append('\n');

        var zone = ZoneName(lastTerritory);
        sb.Append("區域：").Append(zone.Length > 0 ? zone : $"？ #{lastTerritory}");

        if (lastPosition is { } pos && MapCoords.TryWorldToMap(MapIdOf(lastTerritory), pos, out var map))
        {
            sb.Append('\n').Append("地圖座標：")
              .Append(map.X.ToString("F1", CultureInfo.InvariantCulture)).Append(", ")
              .Append(map.Y.ToString("F1", CultureInfo.InvariantCulture));
        }
        else
        {
            sb.Append('\n').Append("地圖座標：這一步沒有座標");
        }

        sb.Append('\n').Append("地圖標記：").Append(lastMarkerPlaced ? "已放上" : "未放上");
        sb.Append('\n').Append("標記來源：").Append(MarkerSource).Append("（可在 Mappy 的設定裡單獨關掉）");

        ImGui.SetTooltip(sb.ToString());
    }

    /// <summary>橘字：橘色代表「有事」，與模組列上的 <see cref="ModuleNoticeLevel.Warning"/> 同一個語氣。</summary>
    private static readonly Vector4 ConflictColor = new(1f, 0.68f, 0.26f, 1f);

    /// <summary>
    /// 設定畫面上的衝突提示。沒有衝突可談的時候<b>整行不畫</b>。
    /// </summary>
    /// <remarks>
    /// 🔑 這裡刻意<b>不</b>打 IPC：要顯示的每一個值都是 <see cref="RefreshConflict"/> 在
    /// 框架執行緒上快取好的。從繪製路徑打對方包在 <c>IpcFrameworkGate</c> 裡的端點，
    /// 就是在主執行緒上等一個要靠主執行緒才跑得到的 tick。
    /// </remarks>
    private void DrawConflictLine()
    {
        // 模組關著時上面那些快取是上一次啟用留下來的殘值，判它沒有意義。
        if (!IsEnabled) return;

        if (HasConflict)
        {
            ImGui.TextColored(ConflictColor, "兩個自動化同時開著：AR 換角會中斷任務，而且不會自動接回");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(ConflictExplanation + "\n\n（判斷依據：" + arMultiModeDetail + "）");

            ImGui.Spacing();
            return;
        }

        if (!ConflictUnknown) return;

        ImGui.TextDisabled("？ Questionable 在跑，但問不到 AutoRetainer 的多角色模式");
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "AutoRetainer 在，卻問不出它的多角色模式開著沒有——所以說不出現在有沒有撞在一起。\n"
                + "多角色模式若正開著，它換角色時會把 Questionable 中斷在半路。\n\n"
                + arMultiModeDetail);
        }

        ImGui.Spacing();
    }
}

using System;
using System.Collections.Generic;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 生產／採集職業未滿等、且身上沒有對應的經驗加成狀態時，自動使用「工程學指南」／「生存學指南」。
/// </summary>
/// <remarks>
/// <para>
/// 📌 <b>零 hook、零特徵碼</b>：Framework 輪詢 ＋ <c>ActionManager::UseAction</c>，
/// 做的事情與使用者自己從背包點指南完全相同。
/// </para>
/// <para>
/// 🔑 <b>所有資料都在動工前對台服 7.20 EXD dump 驗過</b>（2026-08-25，
/// <c>exd-tc/7.20/ClassJob.csv</c>／<c>Item.csv</c>／<c>Status.csv</c>）：
/// <list type="bullet">
/// <item><c>ClassJobCategory</c> 32 ＝採集職（採掘師／園藝師／漁師，3 個）；
/// 33 ＝製作職（木工師…烹調師，8 個）。<b>本模組不寫死職業 ID</b>，啟用時從 <c>ClassJob</c> 表現算。</item>
/// <item>採集：<c>26553</c> 改訂版生存學指南 ／ <c>12668</c> 商用生存學指南 ／
/// <c>4635</c> 軍用生存學指南第二卷 ／ <c>4633</c> 軍用生存學指南（<b>高階優先</b>）。</item>
/// <item>製作：<c>26554</c> 改訂版工程學指南 ／ <c>12667</c> 商用工程學指南 ／
/// <c>4634</c> 軍用工程學指南第二卷 ／ <c>4632</c> 軍用工程學指南。</item>
/// <item>狀態 <c>45</c>＝巧手之工（製作）、<c>46</c>＝大地之恩（採集）。</item>
/// </list>
/// </para>
/// <para>
/// ⚠️ <b>與 DailyRoutines <c>AutoUseCrafterGathererManual</c> 的三個刻意差異</b>：
/// <list type="number">
/// <item>DR 用 <c>GetActionStatus(GeneralAction, 2, …)</c> 當「現在能不能動作」的探針，
/// 但台服 <c>GeneralAction#2</c> 是<b>「跳躍」</b>（已對 <c>GeneralAction.csv</c> 查證），
/// 那是一個間接得可疑的代理判斷。這裡改成直接問<b>要用的那件道具本身</b>
/// （<c>GetActionStatus(ActionType.Item, …)</c>），語意直接、不會因為別的原因跳不起來就整個停擺。</item>
/// <item>DR 只在事件（換區／換職／升等／條件結束）時檢查，<b>狀態到期時沒有任何事件</b>
/// ⇒ 指南掉了要等下一次換區才會補。這裡改成低頻輪詢（預設每 10 秒看一次，成本是幾個欄位讀取）。</item>
/// <item>DR 一律用 NQ 道具 ID。這裡先看 NQ 有沒有貨，沒有才用 HQ（<c>+1,000,000</c> 編碼）——
/// 只有 HQ 存貨時 DR 那條會叫遊戲用一件不存在的 NQ 道具，靜默失敗。</item>
/// </list>
/// </para>
/// <para>
/// 🔴 <b>連續失敗會自己退避。</b>使用被遊戲拒絕（狀態沒出現）連續 3 次後停止嘗試 10 分鐘，
/// 並寫一行 <c>Information</c>。沒有這道閘門的話，任何我們沒想到的拒絕原因都會變成
/// 每 30 秒一次的無限重試。
/// </para>
/// </remarks>
public sealed unsafe class AutoCrafterGathererManual : TcModule
{
    public override string InternalName => "AutoCrafterGathererManual";
    public override string DisplayName => "自動使用生產／採集指南";

    public override string Description =>
        "生產職或採集職未滿等、身上又沒有「巧手之工」／「大地之恩」時，自動從背包使用工程學指南／生存學指南。" +
        "背包裡有多種時優先用高階的。戰鬥、騎乘、製作中、採集中、副本、過場、讀取畫面中都不動作；" +
        "背包裡沒有指南時不做任何事。";

    /// <summary>採集／製作是生活內容，歸「部隊 · 生活」。</summary>
    public override ModuleCategory Category => ModuleCategory.Company;

    /// <summary>開著就會自己輪詢並用道具，不是按鈕型。</summary>
    public override bool IsManualTrigger => false;

    public override bool HasConfigUI => true;

    /// <summary>採集職的 <c>ClassJobCategory</c> 列號（採掘師／園藝師／漁師）。</summary>
    private const uint GathererCategory = 32;

    /// <summary>製作職的 <c>ClassJobCategory</c> 列號（木工師…烹調師）。</summary>
    private const uint CrafterCategory = 33;

    /// <summary>「大地之恩」——採集經驗加成狀態。</summary>
    private const uint GathererBonusStatus = 46;

    /// <summary>「巧手之工」——製作經驗加成狀態。</summary>
    private const uint CrafterBonusStatus = 45;

    /// <summary>生存學指南，<b>高階在前</b>：改訂版／商用／軍用第二卷／軍用。</summary>
    private static readonly uint[] GathererManuals = [26553u, 12668u, 4635u, 4633u];

    /// <summary>工程學指南，<b>高階在前</b>：改訂版／商用／軍用第二卷／軍用。</summary>
    private static readonly uint[] CrafterManuals = [26554u, 12667u, 4634u, 4632u];

    /// <summary>HQ 道具在 <c>UseAction</c> 的 actionId 上是原 ID <c>+1,000,000</c>（艦隊慣例）。</summary>
    private const uint HqOffset = 1_000_000u;

    /// <summary>連續幾次「用了但狀態沒出現」之後放棄一段時間。</summary>
    private const int MaxConsecutiveFailures = 3;

    /// <summary>退避時間（毫秒）。</summary>
    private const int BackoffMs = 600_000;

    private AutoCrafterGathererManualConfig Config => Plugin.Instance.Config.CrafterGathererManual;

    /// <summary>採集職的 ClassJob 列號。啟用時由 <c>ClassJob</c> 表現算，之後只讀。</summary>
    private readonly HashSet<uint> gathererJobs = [];

    /// <summary>製作職的 ClassJob 列號。</summary>
    private readonly HashSet<uint> crafterJobs = [];

    /// <summary>上一次真的送出 <c>UseAction</c> 之後，還在等狀態出現的那件道具（0＝沒有在等）。</summary>
    private uint pendingStatusId;

    private int consecutiveFailures;

    protected override void OnEnable()
    {
        BuildJobSets();

        // 🔑 「回 0」比「報錯」常見：資料表讀不到時整個模組會安靜地什麼都不做。
        //    把筆數寫進 Information 級記錄（使用者跑 LogLevel 2），讓「表是空的」看得出來。
        //    台服 7.20 的期望值是 採集 3 ／ 製作 8。
        Svc.Log.Information(
            $"[{InternalName}] 模組啟用：採集職 {gathererJobs.Count} 個、製作職 {crafterJobs.Count} 個" +
            $"（台服 7.20 期望 3／8）；輪詢間隔 {Config.PollSeconds} 秒。");

        pendingStatusId = 0;
        consecutiveFailures = 0;

        Svc.Framework.Update += OnUpdate;
    }

    protected override void OnDisable()
    {
        Svc.Framework.Update -= OnUpdate;
        gathererJobs.Clear();
        crafterJobs.Clear();
        pendingStatusId = 0;
        consecutiveFailures = 0;
    }

    /// <summary>
    /// 從 <c>ClassJob</c> 表現算兩組職業列號。
    /// </summary>
    /// <remarks>
    /// 📌 刻意不寫死 <c>{8..15}</c>／<c>{16,17,18}</c>：那組數字對台服 7.20 是對的，
    /// 但寫死之後新職業上線是靜默失效（模組對新職業什麼都不做，沒有任何徵兆）。
    /// </remarks>
    private void BuildJobSets()
    {
        gathererJobs.Clear();
        crafterJobs.Clear();

        var sheet = Svc.Data.GetExcelSheet<ClassJob>();
        foreach (var row in sheet)
        {
            var category = row.ClassJobCategory.RowId;
            if (category == GathererCategory) gathererJobs.Add(row.RowId);
            else if (category == CrafterCategory) crafterJobs.Add(row.RowId);
        }
    }

    private void OnUpdate(IFramework framework)
    {
        // 輪詢節流：使用者可調，下限 5 秒。這一段以下的成本全是欄位讀取，沒有配置也沒有查表。
        // 🔴 下限要寫在這裡（使用點），不能只靠 SliderInt 的範圍：
        //    slider 沒加 AlwaysClamp 時 Ctrl+點擊可以鍵入範圍外的值，而設定檔手改也會持久生效。
        //    PollSeconds 被設成 0 的話 Pass(key, 0) 每幀放行 ⇒ 每幀跑完整輪詢
        //    （Condition 十七連查、背包掃描、GetActionStatus），而且 Config.PollSeconds * 1_000
        //    在極端值下還有 int 溢位路徑。Clamp 把兩者一併消掉，
        //    也讓上一行註解說的「下限 5 秒」真的成立。
        if (!Throttle.Pass("AutoCrafterGathererManual-Poll", Math.Clamp(Config.PollSeconds, 5, 120) * 1_000)) return;

        var player = Svc.Objects.LocalPlayer;
        if (player == null) return;

        var jobId = player.ClassJob.RowId;
        var isGatherer = gathererJobs.Contains(jobId);
        var isCrafter = crafterJobs.Contains(jobId);
        if (!isGatherer && !isCrafter)
        {
            // 換到非生產採集職就把「等狀態出現」的計數歸零，免得下次切回來時帶著陳舊的失敗數。
            pendingStatusId = 0;
            consecutiveFailures = 0;
            return;
        }

        var statusId = isGatherer ? GathererBonusStatus : CrafterBonusStatus;
        var hasStatus = HasStatus(player, statusId);

        // 上一輪送出過 UseAction，這一輪來結算：狀態出現＝成功，沒出現＝失敗計數 +1。
        if (pendingStatusId != 0)
        {
            if (pendingStatusId == statusId && hasStatus)
            {
                consecutiveFailures = 0;
            }
            else
            {
                RegisterFailure(jobId, statusId, "使用指南後狀態仍未出現");
            }

            pendingStatusId = 0;
        }

        if (hasStatus) return;

        // 🔴 PlayerState 是 [StaticAddress] 且**沒有** isPointer:true ⇒ 取的是物件本身的位址，
        //    永不回 null（判空是死碼）。特徵碼失配時它擲的是受管理的 InvalidOperationException，
        //    不是 AVE——Framework.Update 的例外由 Dalamud 記錄，不會把遊戲弄崩。
        var maxLevel = PlayerState.Instance()->MaxLevel;
        if (maxLevel > 0 && player.Level >= maxLevel) return;

        var blocked = Svc.Condition[ConditionFlag.InCombat] ||
            Svc.Condition[ConditionFlag.Mounted] ||
            Svc.Condition[ConditionFlag.Casting] ||
            Svc.Condition[ConditionFlag.Crafting] ||
            Svc.Condition[ConditionFlag.PreparingToCraft] ||
            Svc.Condition[ConditionFlag.Gathering] ||
            Svc.Condition[ConditionFlag.BetweenAreas] ||
            Svc.Condition[ConditionFlag.BetweenAreas51] ||
            Svc.Condition[ConditionFlag.BoundByDuty] ||
            Svc.Condition[ConditionFlag.Occupied] ||
            Svc.Condition[ConditionFlag.OccupiedInEvent] ||
            Svc.Condition[ConditionFlag.OccupiedInQuestEvent] ||
            Svc.Condition[ConditionFlag.OccupiedSummoningBell] ||
            Svc.Condition[ConditionFlag.OccupiedInCutSceneEvent] ||
            Svc.Condition[ConditionFlag.WatchingCutscene] ||
            Svc.Condition[ConditionFlag.Unconscious] ||
            Svc.Condition[ConditionFlag.LoggingOut];

        if (blocked) return;

        var inventory = InventoryManager.Instance();
        if (inventory == null) return;

        var candidates = isGatherer ? GathererManuals : CrafterManuals;
        if (!TryPickManual(inventory, candidates, out var useId, out var baseId))
        {
            // 沒貨是完全正常的狀態，不該吵人。只在很長的間隔印一次診斷。
            if (Throttle.Pass("AutoCrafterGathererManual-NoItem", 900_000))
            {
                Svc.Log.Information(
                    $"[{InternalName}] 背包裡沒有{(isGatherer ? "生存學" : "工程學")}指南，本次不動作。");
            }

            return;
        }

        // 直接問「這件道具現在能不能用」。回 0 才是可用；非 0 是遊戲自己的拒絕理由碼。
        var status = ActionManager.Instance()->GetActionStatus(ActionType.Item, useId);
        if (status != 0) return;

        // 使用嘗試另設節流：即使輪詢很密，實際送出的動作最多 30 秒一次。
        if (!Throttle.Pass("AutoCrafterGathererManual-Use", 30_000)) return;

        // 🔑 extraParam: 65535 是艦隊「無 UI 用道具」的慣例值（不帶就等於指定第 0 號容器）。
        var used = ActionManager.Instance()->UseAction(ActionType.Item, useId, extraParam: 65535);
        if (used)
        {
            // 下一輪回來結算狀態有沒有真的出現。
            pendingStatusId = statusId;

            if (Config.NotifyOnUse)
                Svc.Chat.Print($"[TC Toolbox] 已自動使用「{ItemNames.Get(baseId)}」。");
        }
        else
        {
            Svc.Log.Information(
                $"[{InternalName}] UseAction 回傳 false（道具 {useId}、職業 {jobId}）：遊戲拒絕使用。");

            RegisterFailure(jobId, statusId, "遭遊戲拒絕使用指南（UseAction 回 false）");
        }
    }

    /// <summary>
    /// 登記一次失敗，達門檻就退避。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>兩條拒絕路徑都必須走這裡。</b>原本只有「送出後狀態沒出現」那條接上門檻檢查，
    /// <c>UseAction</c> 直接回 <see langword="false"/> 那條只做了計數遞增。
    /// 而 <c>GetActionStatus</c> 回 0（可用）但 <c>UseAction</c> 持續回 false 是真實存在的情境
    /// （兩者判準不完全重合），這時 <c>pendingStatusId</c> 永遠不會被設
    /// ⇒ 結算分支永遠不執行 ⇒ 計數無界累加、退避永不觸發，
    /// 變成每個輪詢間隔重試一次並寫一行 log，直到永遠——
    /// 正是類別註解自己點名要防的那個失控形狀。
    /// </remarks>
    private void RegisterFailure(uint jobId, uint statusId, string reason)
    {
        consecutiveFailures++;
        if (consecutiveFailures < MaxConsecutiveFailures) return;

        Svc.Log.Information(
            $"[{InternalName}] 連續 {consecutiveFailures} 次{reason}，" +
            $"暫停 {BackoffMs / 60_000} 分鐘（職業 {jobId}、狀態 {statusId}）。");

        // 🔴 一定要用 Block 不能用 Pass。Pass 在鍵還在冷卻中時直接 return false、
        //    完全不寫時間 —— 而「剛用完道具、30 秒冷卻還沒過」正是我們要設退避的那一刻，
        //    所以 Pass 在真正需要它的時候一律是無操作，而且不報錯。
        Throttle.Block("AutoCrafterGathererManual-Use", BackoffMs);
        consecutiveFailures = 0;
    }

    /// <summary>玩家身上有沒有這個狀態。走 Dalamud 的受管理 <c>StatusList</c>，不碰原生指標。</summary>
    private static bool HasStatus(Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter player, uint statusId)
    {
        foreach (var status in player.StatusList)
        {
            if (status.StatusId == statusId) return true;
        }

        return false;
    }

    /// <summary>
    /// 依高階優先挑一件背包裡真的有的指南。
    /// </summary>
    /// <param name="useId">要傳給 <c>UseAction</c> 的 ID（HQ 是 <c>+1,000,000</c>）。</param>
    /// <param name="baseId">原始 Item 列號（顯示名稱用）。</param>
    /// <remarks>
    /// 🔴 <c>checkEquipped</c>／<c>checkArmory</c> 一律傳 <see langword="false"/>：
    /// 指南不可能在裝備欄或兵裝庫，而預設值 <see langword="true"/> 只會多掃兩組容器。
    /// </remarks>
    private static bool TryPickManual(InventoryManager* inventory, uint[] candidates, out uint useId, out uint baseId)
    {
        foreach (var id in candidates)
        {
            if (inventory->GetInventoryItemCount(id, isHq: false, checkEquipped: false, checkArmory: false) > 0)
            {
                useId = id;
                baseId = id;
                return true;
            }

            if (inventory->GetInventoryItemCount(id, isHq: true, checkEquipped: false, checkArmory: false) > 0)
            {
                useId = id + HqOffset;
                baseId = id;
                return true;
            }
        }

        useId = 0;
        baseId = 0;
        return false;
    }

    public override void DrawConfig()
    {
        ImGui.SetNextItemWidth(160f);
        var poll = Config.PollSeconds;
        if (ImGui.SliderInt("檢查間隔（秒）", ref poll, 5, 120))
        {
            // 寫回前夾擠（slider 可以 Ctrl+點擊鍵入範圍外的值）。
            // ⚙ 這只是第二道：已經落盤的壞值只有使用點的 Math.Clamp 救得到。
            Config.PollSeconds = Math.Clamp(poll, 5, 120);
            Plugin.Instance.Config.Save();
        }

        var notify = Config.NotifyOnUse;
        if (ImGui.Checkbox("使用後顯示聊天訊息", ref notify))
        {
            Config.NotifyOnUse = notify;
            Plugin.Instance.Config.Save();
        }

        ImGui.Spacing();
        ImGui.TextDisabled("採集職用生存學指南（大地之恩），製作職用工程學指南（巧手之工）。");
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "背包裡同時有多種時，依「改訂版 → 商用 → 軍用第二卷 → 軍用」的順序挑，用掉最高階的那一種。\n" +
                "已經滿等的職業不會使用指南。\n" +
                "連續 3 次失敗（狀態沒出現、或遭遊戲拒絕使用）時會自動暫停 10 分鐘，並在記錄裡寫一行原因。");
        }
    }
}

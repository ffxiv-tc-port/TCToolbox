using System;
using System.Collections.Generic;
using System.Reflection;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;

namespace TCToolbox.Core;

/// <summary>一格唯讀狀態的三態。</summary>
/// <remarks>
/// 🔴 <b><see cref="Unknown"/> 必須在列上看得見，不能畫成「沒有」。</b>
/// 「對方沒安裝」與「對方在、而且沒人壓著」是兩件完全不同的事，
/// 兩者都畫成一句「沒有人壓著」會讓使用者對著一扇壞掉的面板做決定。
/// </remarks>
internal enum ControlState
{
    /// <summary>問到了，而且沒有人在插手。</summary>
    Idle = 0,

    /// <summary>
    /// 問到了，<b>有東西正在插手</b>：有外掛在導航／在跑自己的排程，或有外掛壓著別人。
    /// </summary>
    /// <remarks>
    /// 🔑 <b>判準是「有沒有人插手」，不是「這個外掛開了沒」。</b>
    /// 使用者自己把某個外掛開著是常態，畫成黃色的話整面板永遠都是黃的，
    /// 而真正要找的那一行就沒有人看得見了。
    /// </remarks>
    Active = 1,

    /// <summary>問不到（沒安裝／端點不在／對方擲例外）。<b>不是</b> <see cref="Idle"/>。</summary>
    Unknown = 2,
}

/// <summary>「現在是誰在控制」面板上的一列。</summary>
/// <param name="Subject">被問的對象（列的左半，固定字串）。</param>
/// <param name="State">三態。</param>
/// <param name="Text">列上那句話。<see cref="ControlState.Unknown"/> 時是「？」開頭的灰字。</param>
/// <param name="Detail">列上放不下的細節（放 tooltip）：問了哪些端點、失敗訊息是什麼。</param>
internal readonly record struct ControlStatusRow(
    string Subject,
    ControlState State,
    string Text,
    string Detail);

/// <summary>
/// 「現在是誰在控制」：把艦隊裡<b>既有的</b>租約／狀態端點集中問一次，唯讀顯示。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 <b>這個檔完全不寫任何東西。</b>它只呼叫查詢型端點（<c>Is…</c>／<c>Get…</c>），
/// 一個 <c>Set</c>／<c>Acquire</c>／<c>Stop</c> 都沒有。存在的理由是：使用者現在看不到
/// 「是哪個外掛正壓著自動點擊」或「誰正在導航」，而那些資訊在提供端<b>早就有了</b>，
/// 只是零消費（<c>YesAlready.GetSuppressionOwners</c> 是最明顯的一支）。
/// </para>
/// <para>
/// 🔴 <b>「問不到」與「沒有人在控制」要分開。</b>兩者都畫成「沒有人壓著」的話，
/// 面板在對方沒安裝時看起來一切正常——那正是它最沒有價值的時候。
/// 所以三態一路帶到列上（見 <see cref="ControlState"/>）。
/// </para>
/// <para>
/// 🔴 <b>例外處理攔三種，而且不裸接 <see cref="Exception"/>。</b>
/// ①<c>IpcNotReadyError</c>＝沒安裝／端點不在；②<c>IpcTypeMismatchError</c>＝對方改了型別
/// （它<b>不是</b> <c>IpcNotReadyError</c> 的子型別，兩種都要接）；
/// ③<see cref="TargetInvocationException"/>＝<b>提供端自己實作裡擲的例外</b>——
/// CallGate 走 <c>Func.DynamicInvoke</c>，提供端擲的一律被包成這一種，
/// 而全艦隊慣用的 <c>catch (IpcError)</c> 對它<b>一個都攔不到</b>。
/// 這三種都當成「問不到」，因為這裡是每幀會走到的顯示路徑，漏一種就是每幀擲一次例外。
/// </para>
/// <para>
/// ⚠️ <b>結果有快取。</b>面板在 ImGui 的 Draw 路徑上，每幀去打十個 IPC 端點是浪費——
/// 而且對方的實作是跑在<b>我們這條執行緒</b>上的（YesAlready 那支要進它自己的鎖）。
/// 這裡每 <see cref="RefreshIntervalMs"/> 毫秒才重問一次，中間畫上一次的快照。
/// </para>
/// <para>
/// ⚠️ 只在主執行緒呼叫（唯一的呼叫端是 ImGui 的 Draw）。快取沒有上鎖，也不需要。
/// </para>
/// </remarks>
internal static class FleetControlStatus
{
    /// <summary>兩次重問之間至少隔多久（毫秒）。</summary>
    /// <remarks>
    /// 📌 挑 500 的理由：這是「掃一眼」用的面板，半秒的延遲看不出來，
    /// 而 60fps 下這樣把 IPC 呼叫量壓到三十分之一。
    /// </remarks>
    private const int RefreshIntervalMs = 500;

    private static readonly List<ControlStatusRow> Cached = [];

    /// <summary><see cref="Environment.TickCount64"/> 座標系；<c>0</c>＝還沒問過。</summary>
    private static long lastQueriedAt;

    // ── 端點（全部是查詢型；出處逐一寫在旁邊）─────────────────────────────
    // YesAlready/IPC/YesAlreadyIPC.cs：EzIPC.Init(this) ⇒ 前綴＝內部名 "YesAlready"
    //   [EzIPC] public bool IsPluginEnabled()      → 複合值 Active（開關 且 沒人壓著）
    //   [EzIPC] public bool IsUserEnabled()        → 使用者自己的開關 C.Enabled
    //   [EzIPC] public string[] GetSuppressionOwners() → 租約的租用者 ＋ 阻擋清單裡的外掛名
    private static readonly Lazy<ICallGateSubscriber<bool>> YaIsUserEnabled =
        new(() => Svc.PluginInterface.GetIpcSubscriber<bool>("YesAlready.IsUserEnabled"));

    private static readonly Lazy<ICallGateSubscriber<bool>> YaIsPluginEnabled =
        new(() => Svc.PluginInterface.GetIpcSubscriber<bool>("YesAlready.IsPluginEnabled"));

    private static readonly Lazy<ICallGateSubscriber<string[]>> YaSuppressionOwners =
        new(() => Svc.PluginInterface.GetIpcSubscriber<string[]>("YesAlready.GetSuppressionOwners"));

    // AutoRetainer/Modules/IPC.cs:42,50
    //   GetIpcProvider<bool>("AutoRetainer.GetSuppressed") → ManualSuppressed 或 有任何租約
    //   GetIpcProvider<bool>("AutoRetainer.IsBusy")        → 排程開著／多角色模式／任務佇列在跑
    private static readonly Lazy<ICallGateSubscriber<bool>> ArGetSuppressed =
        new(() => Svc.PluginInterface.GetIpcSubscriber<bool>("AutoRetainer.GetSuppressed"));

    private static readonly Lazy<ICallGateSubscriber<bool>> ArIsBusy =
        new(() => Svc.PluginInterface.GetIpcSubscriber<bool>("AutoRetainer.IsBusy"));

    // AutoHook/IPC/AutoHookIPC.cs:33 EzIPC.Init(this, "AutoHook")
    //   [EzIPC] public bool GetPluginState()          → 使用者的開關 Configuration.PluginEnabled
    //   [EzIPC] public bool GetEffectivePluginState() → 疊上租約之後的實際值
    // 🔑 兩支的差異<b>就是</b>「有沒有人正壓著釣魚」——對方刻意分成兩個名字（不是疏漏），
    //    所以這裡也照它的語意讀：兩值不同＝被別人壓著。
    private static readonly Lazy<ICallGateSubscriber<bool>> AhGetPluginState =
        new(() => Svc.PluginInterface.GetIpcSubscriber<bool>("AutoHook.GetPluginState"));

    private static readonly Lazy<ICallGateSubscriber<bool>> AhGetEffectivePluginState =
        new(() => Svc.PluginInterface.GetIpcSubscriber<bool>("AutoHook.GetEffectivePluginState"));

    // vnavmesh/IPCProvider.cs:72 RegisterFunc("Path.GetMovementAllowed", () => followPath.EffectiveMovementAllowed)
    // 🔑 vnavmesh 這支<b>刻意</b>回疊加後的值（與 AutoHook 相反），所以 false 就代表
    //    「使用者自己關了，或有人用租約壓著」。它沒有對應的「是誰」端點，只能顯示有沒有。
    private static readonly Lazy<ICallGateSubscriber<bool>> VnavMovementAllowed =
        new(() => Svc.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.Path.GetMovementAllowed"));

    /// <summary>取得目前的狀態列（有快取，<see cref="RefreshIntervalMs"/> 毫秒內回同一份）。</summary>
    /// <remarks>⚠️ 回的是內部清單本身，呼叫端只讀不改。</remarks>
    public static IReadOnlyList<ControlStatusRow> Snapshot()
    {
        var now = Environment.TickCount64;
        if (lastQueriedAt != 0 && now - lastQueriedAt < RefreshIntervalMs) return Cached;

        lastQueriedAt = now;
        Cached.Clear();
        Cached.Add(DescribeSelf());
        Cached.Add(DescribeMovement());
        Cached.Add(DescribeVnavMovementAllowed());
        Cached.Add(DescribeYesAlready());
        Cached.Add(DescribeAutoRetainer());
        Cached.Add(DescribeAutoHook());
        Cached.Add(DescribeQuestVsRelog());
        return Cached;
    }

    /// <summary>下一次 <see cref="Snapshot"/> 一定重問（面板剛打開時用）。</summary>
    public static void Invalidate() => lastQueriedAt = 0;

    // ── 逐項 ────────────────────────────────────────────────────────────

    /// <summary>本外掛自己：急停冷卻與停止移動看門狗。</summary>
    /// <remarks>
    /// 📌 這一列不走 IPC，所以永遠不會是 <see cref="ControlState.Unknown"/>。
    /// 放在最上面的理由是「使用者最先懷疑的是自己剛剛按的那顆急停」。
    /// </remarks>
    private static ControlStatusRow DescribeSelf()
    {
        if (AutomationGate.TryGetEmergencyStopReason(out var stopReason))
        {
            return new ControlStatusRow(
                "TC Toolbox 自己",
                ControlState.Active,
                stopReason,
                "急停冷卻期間，本外掛所有無人值守流程都不會自己醒過來。\n"
                + "秒數在這個模組的設定裡可以改（0＝不冷卻）。");
        }

        if (NavStop.IsEnforcing)
        {
            return new ControlStatusRow(
                "TC Toolbox 自己",
                ControlState.Active,
                "正在強制停止移動",
                "剛送出停止移動的請求，正在補送以蓋住「路徑還在背景計算」那幾秒。");
        }

        return new ControlStatusRow(
            "TC Toolbox 自己",
            ControlState.Idle,
            "沒有壓著任何東西",
            "沒有急停冷卻，也沒有正在強制停止移動。");
    }

    /// <summary>誰正在把角色帶往某處。</summary>
    private static ControlStatusRow DescribeMovement()
    {
        var movers = ExternalNav.SnapshotMovers();

        var active = new List<string>();
        var unknown = 0;
        var detail = new List<string>(movers.Count);

        foreach (var (name, state) in movers)
        {
            switch (state)
            {
                case ControlState.Active:
                    active.Add(name);
                    detail.Add($"{name}：正在移動");
                    break;
                case ControlState.Unknown:
                    unknown++;
                    detail.Add($"{name}：問不到（多半是沒安裝）");
                    break;
                default:
                    detail.Add($"{name}：閒置");
                    break;
            }
        }

        var detailText = string.Join("\n", detail);

        if (active.Count > 0)
        {
            return new ControlStatusRow(
                "誰在導航",
                ControlState.Active,
                string.Join("、", active),
                detailText);
        }

        // 🔴 全部都問不到時<b>不能</b>說「沒有人在導航」：那是「不知道」。
        if (unknown == movers.Count)
        {
            return new ControlStatusRow(
                "誰在導航",
                ControlState.Unknown,
                "？ 四個導航外掛一個都問不到",
                detailText);
        }

        return new ControlStatusRow("誰在導航", ControlState.Idle, "沒有人在導航", detailText);
    }

    /// <summary>vnavmesh 的移動是不是被壓住了。</summary>
    private static ControlStatusRow DescribeVnavMovementAllowed()
    {
        if (!TryQueryBool(VnavMovementAllowed, out var allowed, out var error))
        {
            return Unavailable("vnavmesh 移動開關", "vnavmesh.Path.GetMovementAllowed", error);
        }

        return allowed
            ? new ControlStatusRow(
                "vnavmesh 移動開關",
                ControlState.Idle,
                "開著",
                "vnavmesh.Path.GetMovementAllowed 回 true：沒有人用租約壓著它的移動。")
            : new ControlStatusRow(
                "vnavmesh 移動開關",
                ControlState.Active,
                "被關著（使用者自己關的，或有人用租約壓著）",
                "vnavmesh.Path.GetMovementAllowed 回的是疊加後的值，\n"
                + "所以分不出是使用者自己關的還是別的外掛壓的——它沒有「是誰」的端點。\n"
                + "這個狀態下 vnavmesh 收到路徑也不會走。");
    }

    /// <summary>YesAlready：現在會不會接手確認框，被誰壓著。</summary>
    private static ControlStatusRow DescribeYesAlready()
    {
        const string subject = "自動點擊確認框（YesAlready）";

        if (!TryQueryBool(YaIsPluginEnabled, out var active, out var error))
            return Unavailable(subject, "YesAlready.IsPluginEnabled", error);

        // 使用者的開關與租約要分開講：「不會接手」的原因是兩種完全不同的東西。
        var userEnabledKnown = TryQueryBool(YaIsUserEnabled, out var userEnabled, out _);
        var owners = QueryOwners();

        var detail = new List<string>
        {
            $"YesAlready.IsPluginEnabled（＝會不會接手）：{active}",
            userEnabledKnown
                ? $"YesAlready.IsUserEnabled（使用者自己的開關）：{userEnabled}"
                : "YesAlready.IsUserEnabled：問不到",
            owners == null
                ? "YesAlready.GetSuppressionOwners：問不到"
                : owners.Length == 0
                    ? "YesAlready.GetSuppressionOwners：沒有人壓著"
                    : $"YesAlready.GetSuppressionOwners：{string.Join("、", owners)}",
        };

        if (active)
            return new ControlStatusRow(subject, ControlState.Idle, "會接手（沒有人壓著）", string.Join("\n", detail));

        if (owners is { Length: > 0 })
        {
            return new ControlStatusRow(
                subject,
                ControlState.Active,
                $"被 {string.Join("、", owners)} 壓著",
                string.Join("\n", detail));
        }

        if (userEnabledKnown && !userEnabled)
        {
            return new ControlStatusRow(
                subject,
                ControlState.Idle,
                "使用者自己關著（不是被別的外掛壓的）",
                string.Join("\n", detail));
        }

        // 會走到這裡：不接手、但問不出是誰／為什麼。那就是「不知道」。
        return new ControlStatusRow(
            subject,
            ControlState.Unknown,
            "？ 不會接手，但問不出原因",
            string.Join("\n", detail));
    }

    /// <summary>AutoRetainer：有沒有被壓著、現在忙不忙。</summary>
    private static ControlStatusRow DescribeAutoRetainer()
    {
        const string subject = "僱員自動化（AutoRetainer）";

        if (!TryQueryBool(ArGetSuppressed, out var suppressed, out var error))
            return Unavailable(subject, "AutoRetainer.GetSuppressed", error);

        var busyKnown = TryQueryBool(ArIsBusy, out var busy, out _);
        var detail =
            $"AutoRetainer.GetSuppressed（手動壓制或有租約）：{suppressed}\n"
            + (busyKnown ? $"AutoRetainer.IsBusy：{busy}" : "AutoRetainer.IsBusy：問不到")
            + "\n它沒有「是誰壓著」的端點，只問得到有沒有被壓著。";

        if (suppressed)
            return new ControlStatusRow(subject, ControlState.Active, "被壓著（有人請它讓開）", detail);

        if (busyKnown && busy)
            return new ControlStatusRow(subject, ControlState.Active, "正在跑自己的排程", detail);

        return new ControlStatusRow(subject, ControlState.Idle, "沒有在跑，也沒有被壓著", detail);
    }

    /// <summary>AutoHook：使用者的開關與實際值的差異就是「被誰壓著」。</summary>
    private static ControlStatusRow DescribeAutoHook()
    {
        const string subject = "釣魚（AutoHook）";

        if (!TryQueryBool(AhGetEffectivePluginState, out var effective, out var error))
            return Unavailable(subject, "AutoHook.GetEffectivePluginState", error);

        var userKnown = TryQueryBool(AhGetPluginState, out var user, out _);
        var detail =
            $"AutoHook.GetEffectivePluginState（疊上租約後的實際值）：{effective}\n"
            + (userKnown ? $"AutoHook.GetPluginState（使用者的開關）：{user}" : "AutoHook.GetPluginState：問不到")
            + "\n兩支刻意是不同語意：使用者的開關不會被租約改寫，這樣壓制結束才還得回去。";

        // 🔴 判準是「使用者開著、實際卻是關的」——那個差額<b>就是</b>別人的租約。
        //    只看實際值分不出「使用者自己關的」與「被壓著」，而那正是這一列存在的理由。
        if (userKnown && user && !effective)
            return new ControlStatusRow(subject, ControlState.Active, "被別的外掛壓著（使用者自己是開著的）", detail);

        // 📌 「開著」不算 Active：這一格問的是「有沒有人插手」，不是「這個外掛開了沒」。
        //    使用者自己的開關狀態放在後半句，讓列上看得出是誰決定的。
        return new ControlStatusRow(
            subject,
            ControlState.Idle,
            effective ? "沒有人壓著（目前開著）" : "沒有人壓著（目前關著）",
            detail);
    }

    /// <summary>
    /// 兩個自動化撞在一起：Questionable 在跑任務，而 AutoRetainer 的多角色模式會把角色登出。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 📌 <b>這一列問的不是「誰在控制」而是「誰等一下會打斷誰」</b>，但使用者會來這扇面板的時刻
    /// 正好一樣：「怎麼跑到一半停了」。判定本身在 <see cref="QuestAutomationConflict"/>，
    /// 與任務進度模組共用同一支——那個模組<b>預設是關的</b>，只做在它上面的話，
    /// 沒開它的人（絕大多數）永遠看不到這個提醒。
    /// </para>
    /// <para>
    /// 🔴 <b>「撞在一起」才算 <see cref="ControlState.Active"/>。</b>
    /// 兩邊各自開著是常態（那是使用者自己開的），畫成黃色的話這一列永遠是黃的。
    /// </para>
    /// <para>
    /// 🔴 <b>三種「沒有衝突」不可以併成同一句</b>：沒在跑任務／沒裝 AutoRetainer／多角色模式關著，
    /// 三者都是綠的，但列上的句子不同——使用者要看得出這個結論是哪一種情況給的。
    /// 問不到的那兩種（問不到 Questionable、問不到多角色模式）照舊是灰字的「？」。
    /// </para>
    /// </remarks>
    private static ControlStatusRow DescribeQuestVsRelog()
    {
        const string subject = "跑任務 × 換角（Questionable × AutoRetainer）";

        var status = QuestAutomationConflict.Evaluate();

        var state = status.Verdict switch
        {
            QuestConflictVerdict.Conflict => ControlState.Active,
            QuestConflictVerdict.Unknown => ControlState.Unknown,
            QuestConflictVerdict.QuestionableUnavailable => ControlState.Unknown,
            _ => ControlState.Idle,
        };

        return new ControlStatusRow(
            subject,
            state,
            QuestAutomationConflict.ShortText(status.Verdict),
            QuestAutomationConflict.Tooltip(status));
    }

    // ── 查詢輔助 ────────────────────────────────────────────────────────

    private static ControlStatusRow Unavailable(string subject, string endpoint, string error) =>
        new(subject,
            ControlState.Unknown,
            "？ 問不到（多半是沒安裝）",
            $"{endpoint} 打不通：{error}\n"
            + "「沒安裝」與「安裝了但沒人壓著」是兩件事，所以這裡不畫成「沒有人壓著」。");

    /// <summary>問一個無參數的 bool 端點。回 <see langword="false"/>＝問不到（不是「值是 false」）。</summary>
    private static bool TryQueryBool(Lazy<ICallGateSubscriber<bool>> gate, out bool value, out string error)
    {
        try
        {
            value = gate.Value.InvokeFunc();
            error = string.Empty;
            return true;
        }
        catch (IpcError ex)
        {
            value = false;
            error = ex.GetType().Name;
            return false;
        }
        catch (TargetInvocationException ex)
        {
            // 🔴 提供端自己實作裡擲的例外走這條（CallGate 用 DynamicInvoke 包起來）。
            //    catch (IpcError) 攔不到它——沒有這一條的話這裡會每幀擲一次。
            value = false;
            error = $"提供端擲出例外：{ex.InnerException?.Message ?? ex.Message}";
            return false;
        }
    }

    /// <summary>問 YesAlready 現在被誰壓著。<see langword="null"/>＝問不到。</summary>
    private static string[]? QueryOwners()
    {
        try
        {
            return YaSuppressionOwners.Value.InvokeFunc() ?? [];
        }
        catch (IpcError)
        {
            return null;
        }
        catch (TargetInvocationException)
        {
            return null;
        }
    }
}

using System;
using System.Collections.Generic;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;

namespace TCToolbox.Core;

/// <summary>一個急停對象的結果。三態，<b>三態都要在列上看得見</b>。</summary>
public enum FleetStopOutcome
{
    /// <summary>對方沒安裝／沒載入。<b>不是失敗</b>，而且是最常見的狀態，不要畫成紅色。</summary>
    NotInstalled = 0,

    /// <summary>停止指令已送達。</summary>
    Ok = 1,

    /// <summary>對方在，但叫它停的時候出事了。</summary>
    Failed = 2,
}

/// <summary>單一對象的急停結果。</summary>
/// <param name="Target">顯示用的外掛名。</param>
/// <param name="Outcome">三態。</param>
/// <param name="Detail">列上放不下的細節（放 tooltip）：做了哪幾個呼叫、失敗訊息是什麼。</param>
public readonly record struct FleetStopResult(string Target, FleetStopOutcome Outcome, string Detail);

/// <summary>
/// 全艦隊急停：把所有「會自己動」的外掛一次叫停。
/// </summary>
/// <remarks>
/// 🔴 <b>絕不透過聊天指令呼叫 <c>/li</c></b>（空參數的 <c>/li</c> 是跨世界傳送）。
/// 🔴 <b>執行順序：先停會讓角色移動的，再停自動化本體。</b>
/// 反過來的話，控制端（AutoDuty／Questionable）在被停掉之前還有機會再送一次導航請求，
/// 而角色會在你以為已經停下來之後繼續走。
/// </remarks>
internal static class FleetStop
{
    // ── 移動層 ───────────────────────────────────────────────────────────────
    // vnavmesh/IPCProvider.cs RegisterAction("Nav.PathfindCancelAll", …)  → <object> / InvokeAction
    // vnavmesh/IPCProvider.cs RegisterAction("Path.Stop", followPath.Stop) → <object> / InvokeAction
    private static readonly Lazy<ICallGateSubscriber<object>> VnavPathfindCancelAll =
        new(() => Svc.PluginInterface.GetIpcSubscriber<object>("vnavmesh.Nav.PathfindCancelAll"));

    private static readonly Lazy<ICallGateSubscriber<object>> VnavPathStop =
        new(() => Svc.PluginInterface.GetIpcSubscriber<object>("vnavmesh.Path.Stop"));

    // Lifestream/IPC/IPCProvider.cs [EzIPC] public void Abort() → 前綴 "Lifestream"，Action 無參數
    private static readonly Lazy<ICallGateSubscriber<object>> LifestreamAbort =
        new(() => Svc.PluginInterface.GetIpcSubscriber<object>("Lifestream.Abort"));

    // AutoDuty/IPC/IPCProvider.cs [EzIPC] public void Stop() → 前綴 "AutoDuty"
    private static readonly Lazy<ICallGateSubscriber<object>> AutoDutyStop =
        new(() => Svc.PluginInterface.GetIpcSubscriber<object>("AutoDuty.Stop"));

    // Questionable/External/QuestionableIpc.cs GetIpcProvider<string, bool>("Questionable.Stop")
    // ⚠️ 這一支是 Func 不是 Action：參數是「誰叫停的」標籤，會寫進它自己的記錄。
    private static readonly Lazy<ICallGateSubscriber<string, bool>> QuestionableStop =
        new(() => Svc.PluginInterface.GetIpcSubscriber<string, bool>("Questionable.Stop"));

    // visland/IPC/vislandIPC.cs GetIpcProvider<object>("visland.StopRoute")
    private static readonly Lazy<ICallGateSubscriber<object>> VislandStopRoute =
        new(() => Svc.PluginInterface.GetIpcSubscriber<object>("visland.StopRoute"));

    // BossmodReborn/BossMod/Framework/IPCProvider.cs Register("AI.SetEnabled", (bool)=>bool)
    // 🔴 前綴是 "BossMod" 不是 "BossmodReborn"，而且它是 Func<bool,bool>（回傳關掉之後的實際狀態）。
    private static readonly Lazy<ICallGateSubscriber<bool, bool>> BossModAiSetEnabled =
        new(() => Svc.PluginInterface.GetIpcSubscriber<bool, bool>("BossMod.AI.SetEnabled"));

    // ── 自動化本體 ───────────────────────────────────────────────────────────
    // AutoRetainer/Modules/EzIPCManagers/IPC_PluginState.cs EzIPC.Init(this, "<內部名>.PluginState")
    private static readonly Lazy<ICallGateSubscriber<object>> ArAbortAllTasks =
        new(() => Svc.PluginInterface.GetIpcSubscriber<object>("AutoRetainer.PluginState.AbortAllTasks"));

    private static readonly Lazy<ICallGateSubscriber<object>> ArDisableAllFunctions =
        new(() => Svc.PluginInterface.GetIpcSubscriber<object>("AutoRetainer.PluginState.DisableAllFunctions"));

    // Artisan/IPC/IPC.cs GetIpcProvider<bool, object>(…).RegisterAction(…)
    private static readonly Lazy<ICallGateSubscriber<bool, object>> ArtisanSetStopRequest =
        new(() => Svc.PluginInterface.GetIpcSubscriber<bool, object>("Artisan.SetStopRequest"));

    private static readonly Lazy<ICallGateSubscriber<bool, object>> ArtisanSetEnduranceStatus =
        new(() => Svc.PluginInterface.GetIpcSubscriber<bool, object>("Artisan.SetEnduranceStatus"));

    private static readonly Lazy<ICallGateSubscriber<bool, object>> ArtisanSetListPause =
        new(() => Svc.PluginInterface.GetIpcSubscriber<bool, object>("Artisan.SetListPause"));

    // GatherBuddyReborn/GatherBuddy/Plugin/GatherBuddyIpc.cs EzIPC.Init(this, GatherBuddy.InternalName)
    // 🔴 那個常數是 "GatherBuddyReborn"（大寫 B），與 feed 上的 InternalName 拼法不同——
    //    這裡要用的是<b>提供端寫死的那個常數</b>，不是 feed 的拼法。
    private static readonly Lazy<ICallGateSubscriber<bool, object>> GbrSetAutoGatherEnabled =
        new(() => Svc.PluginInterface.GetIpcSubscriber<bool, object>("GatherBuddyReborn.SetAutoGatherEnabled"));

    // AutoHook/IPC/AutoHookIPC.cs [EzIPC] public void SetPluginState(bool) → EzIPC.Init(this, "AutoHook")
    // 🔑 刻意用 SetPluginState 而不是 SetPluginStatePersistent：前者走 IpcConfigOverrides，
    //    只改執行期的值、存檔時換回使用者自己的設定 ⇒ 急停關掉釣魚，但不會把使用者的
    //    「啟用 AutoHook」永久改成關。急停要停的是現在，不是下次開遊戲。
    private static readonly Lazy<ICallGateSubscriber<bool, object>> AutoHookSetPluginState =
        new(() => Svc.PluginInterface.GetIpcSubscriber<bool, object>("AutoHook.SetPluginState"));

    // ICE/IPC/IceCosmicExplorationIPC.cs [EzIPC] public void Disable() → EzIPC.Init(this) ⇒ 前綴＝內部名 "ICE"
    // 📌 同源的 ExplorersIcebox 沒有任何 IPC 提供端，所以這一條只會打到 ICE。
    private static readonly Lazy<ICallGateSubscriber<object>> IceDisable =
        new(() => Svc.PluginInterface.GetIpcSubscriber<object>("ICE.Disable"));

    /// <summary>Questionable 端會把這個字串寫進它自己的停止原因記錄裡。</summary>
    private const string StopLabel = "TC Toolbox 全艦隊急停";

    /// <summary>
    /// 把所有會自己動的外掛叫停一輪。<b>永遠跑完整份清單</b>，回傳每一項的三態結果。
    /// </summary>
    public static List<FleetStopResult> StopAll()
    {
        var results = new List<FleetStopResult>(12);

        // ① 會讓角色移動的優先。
        results.Add(StopVnavmesh());
        results.Add(Invoke("Lifestream", () =>
        {
            LifestreamAbort.Value.InvokeAction();
            return "Lifestream.Abort（中止任務佇列與跟隨路徑）";
        }));
        results.Add(Invoke("AutoDuty", () =>
        {
            AutoDutyStop.Value.InvokeAction();
            return "AutoDuty.Stop";
        }));
        results.Add(StopQuestionable());
        results.Add(Invoke("visland", () =>
        {
            VislandStopRoute.Value.InvokeAction();
            return "visland.StopRoute";
        }));
        results.Add(StopBossModAi());

        // ② 自動化本體。
        results.Add(StopWrathCombo());
        results.Add(Invoke("AutoRetainer", () =>
        {
            ArAbortAllTasks.Value.InvokeAction();
            ArDisableAllFunctions.Value.InvokeAction();
            return "AutoRetainer.PluginState.AbortAllTasks ＋ DisableAllFunctions";
        }));
        results.Add(Invoke("Artisan", () =>
        {
            ArtisanSetStopRequest.Value.InvokeAction(true);
            ArtisanSetEnduranceStatus.Value.InvokeAction(false);
            ArtisanSetListPause.Value.InvokeAction(true);
            return "Artisan.SetStopRequest(true) ＋ SetEnduranceStatus(false) ＋ SetListPause(true)";
        }));
        results.Add(Invoke("GatherBuddy Reborn", () =>
        {
            GbrSetAutoGatherEnabled.Value.InvokeAction(false);
            return "GatherBuddyReborn.SetAutoGatherEnabled(false)";
        }));
        results.Add(Invoke("AutoHook", () =>
        {
            AutoHookSetPluginState.Value.InvokeAction(false);
            return "AutoHook.SetPluginState(false)（只改執行期，不會寫進對方的設定檔）";
        }));
        results.Add(Invoke("ICE 宇宙探索", () =>
        {
            IceDisable.Value.InvokeAction();
            return "ICE.Disable";
        }));
        results.Add(StopSomethingNeedDoing());

        return results;
    }

    /// <summary>
    /// vnavmesh：先取消「還在算」的尋路批次，再清掉「已經在走」的路徑點。
    /// </summary>
    /// <remarks>
    /// 🔴 只送 <c>Path.Stop</c> 是攔不住的：<c>SimpleMove</c> 把路徑計算丟到背景工作，
    /// 算完之後才交給 FollowPath 開走，而 <c>Path.Stop</c> 清的是 FollowPath 的路徑點。
    /// 表現成「按了停止、幾秒後角色自己走起來」。
    /// </remarks>
    private static FleetStopResult StopVnavmesh()
    {
        var cancelNote = string.Empty;
        try
        {
            VnavPathfindCancelAll.Value.InvokeAction();
            cancelNote = "Nav.PathfindCancelAll";
        }
        catch (Exception ex)
        {
            cancelNote = $"Nav.PathfindCancelAll 不可用（{ex.GetType().Name}）";
        }

        var result = Invoke("vnavmesh", () =>
        {
            VnavPathStop.Value.InvokeAction();
            return $"{cancelNote} ＋ Path.Stop";
        });

        // 成功送出才去掛補送看門狗：沒安裝 vnavmesh 的人不需要每次急停都多一行警告記錄。
        // ⚠️ RequestStop 內部會再送一次 Path.Stop（冪等的無操作），換來的是接下來三秒持續補送，
        //    蓋住「這一刻路徑還在背景計算、算完才開走」那個窗口。
        if (result.Outcome == FleetStopOutcome.Ok)
            NavStop.RequestStop();

        return result;
    }

    /// <summary>
    /// Questionable：叫停整條任務流程。
    /// </summary>
    /// <remarks>
    /// 🔴 <c>Questionable.Stop</c> <b>是 Func 不是 Action</b>，而且它的回傳值有意義：
    /// 對方那一端是 <c>_gate.Get(IpcStop, () =&gt; Stop(label), false)</c>——正常路徑<b>恆為 true</b>，
    /// <c>false</c> 只可能是它自己的主執行緒閘門逾時（＝<b>沒有</b>確認停下來）。
    /// </remarks>
    private static FleetStopResult StopQuestionable()
    {
        const string target = "Questionable";
        try
        {
            return QuestionableStop.Value.InvokeFunc(StopLabel)
                ? new FleetStopResult(target, FleetStopOutcome.Ok, "Questionable.Stop")
                : new FleetStopResult(target, FleetStopOutcome.Failed,
                    "Questionable.Stop 回傳 false（對方的主執行緒閘門逾時），沒有確認停下來。");
        }
        catch (IpcNotReadyError)
        {
            return new FleetStopResult(target, FleetStopOutcome.NotInstalled,
                "沒有偵測到這個外掛（或它這一版沒有提供對應的 IPC 端點）。");
        }
        catch (Exception ex)
        {
            return new FleetStopResult(target, FleetStopOutcome.Failed, $"{ex.GetType().Name}：{ex.Message}");
        }
    }

    /// <summary>
    /// BossmodReborn 的 AI：優先走 IPC，端點不存在就退回聊天指令。
    /// </summary>
    /// <remarks>
    /// 📌 <c>BossMod.AI.SetEnabled</c> 與 <c>/bmrai off</c> 走的是<b>同一支</b>
    /// <c>AIManager.EnableConfig</c>，所以兩條路的效果逐字相同（含清掉導航目標）。
    /// IPC 這條多一件事：它回傳關閉之後的實際狀態，我們能分辨「叫了」與「真的關掉了」。
    /// </remarks>
    private static FleetStopResult StopBossModAi()
    {
        try
        {
            var stillEnabled = BossModAiSetEnabled.Value.InvokeFunc(false);
            return stillEnabled
                ? new FleetStopResult("BossmodReborn AI", FleetStopOutcome.Failed,
                    "BossMod.AI.SetEnabled(false) 之後對方回報 AI 仍然是開啟狀態"
                    + "（多半是它的主執行緒閘門逾時），請自己再打一次 /bmrai off。")
                : new FleetStopResult("BossmodReborn AI", FleetStopOutcome.Ok,
                    "BossMod.AI.SetEnabled(false)，對方回報已關閉");
        }
        catch (IpcNotReadyError)
        {
            // 端點不存在有兩種可能：沒裝，或裝的是還沒有這支端點的舊版。
            // 用「有沒有註冊 /bmrai 這個指令」把兩者分開。
            return RunCommand("BossmodReborn AI", "/bmrai", "/bmrai off",
                "（舊版沒有 BossMod.AI.SetEnabled 端點，改走聊天指令）");
        }
        catch (Exception ex)
        {
            return new FleetStopResult("BossmodReborn AI", FleetStopOutcome.Failed,
                $"BossMod.AI.SetEnabled 呼叫失敗：{ex.GetType().Name}：{ex.Message}");
        }
    }

    /// <summary>
    /// WrathCombo：關掉自動循環。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>刻意走聊天指令而不是 IPC。</b>它的 <c>WrathCombo.SetAutoRotationState</c> 需要一個
    /// <b>租約</b>（<c>RegisterForLease</c>）——為了按一次急停去承租別人的自動循環控制權，
    /// 之後還得負責歸還、處理回呼與逾時，代價遠大於收益，而且租約沒還乾淨的失敗形式是
    /// 「使用者自己再也開不回來」。<c>/wrath auto off</c> 走的是使用者自己打指令的同一條路。
    /// </remarks>
    private static FleetStopResult StopWrathCombo()
        => RunCommand("WrathCombo", "/wrath", "/wrath auto off", string.Empty);

    /// <summary>
    /// SomethingNeedDoing：停掉所有正在跑的巨集。
    /// </summary>
    /// <remarks>
    /// 📌 SND <b>沒有</b>對外提供任何 IPC 端點（它的 <c>IPC</c> 基底類別全是拿來<b>呼叫別人</b>的），
    /// 所以只能走指令。<c>/snd stop all</c> 打到 <c>_macroScheduler.StopAllMacros()</c>
    /// （<c>Services/CommandService.cs</c> 的 <c>stopCommand.SubCommands</c>）。
    /// </remarks>
    private static FleetStopResult StopSomethingNeedDoing()
        => RunCommand("SomethingNeedDoing", "/snd", "/snd stop all", string.Empty);

    /// <summary>
    /// 包一個 IPC 呼叫並把它歸到三態。
    /// </summary>
    /// <param name="target">顯示用名稱。</param>
    /// <param name="action">實際的呼叫；成功時回傳要放進 tooltip 的細節。</param>
    /// <remarks>
    /// 🔴 <c>IpcNotReadyError</c> 一定要排在 <c>IpcError</c>／<c>Exception</c> 前面接：
    /// 它是 <c>IpcError</c> 的子類別，順序反了就會把「沒安裝」全部歸成「失敗」，
    /// </remarks>
    private static FleetStopResult Invoke(string target, Func<string> action)
    {
        try
        {
            return new FleetStopResult(target, FleetStopOutcome.Ok, action());
        }
        catch (IpcNotReadyError)
        {
            return new FleetStopResult(target, FleetStopOutcome.NotInstalled,
                "沒有偵測到這個外掛（或它這一版沒有提供對應的 IPC 端點）。");
        }
        catch (Exception ex)
        {
            return new FleetStopResult(target, FleetStopOutcome.Failed,
                $"{ex.GetType().Name}：{ex.Message}");
        }
    }

    /// <summary>
    /// 用聊天指令叫停一個外掛。
    /// </summary>
    /// <param name="target">顯示用名稱。</param>
    /// <param name="registeredCommand">用來判斷「有沒有安裝」的指令名（該外掛註冊的那一個）。</param>
    /// <param name="fullCommand">實際要送出的完整指令。</param>
    /// <param name="note">附加說明（可為空字串）。</param>
    /// <remarks>
    /// 🔴 <b>刻意不走 <see cref="ChatSender"/>。</b>那一支在指令沒有註冊時會退回把整行丟進遊戲聊天框，
    /// 於是沒裝該外掛的人每次急停都會在聊天視窗吃到一行「無此指令」。
    /// 這裡先查 <c>ICommandManager.Commands</c>——沒有那個鍵就是沒安裝，什麼都不送。
    /// </remarks>
    private static FleetStopResult RunCommand(
        string target, string registeredCommand, string fullCommand, string note)
    {
        try
        {
            if (!Svc.Commands.Commands.ContainsKey(registeredCommand))
            {
                return new FleetStopResult(target, FleetStopOutcome.NotInstalled,
                    $"沒有偵測到 {registeredCommand} 這個指令，判定為沒有安裝。{note}");
            }

            return Svc.Commands.ProcessCommand(fullCommand)
                ? new FleetStopResult(target, FleetStopOutcome.Ok, $"已送出 {fullCommand}{note}")
                : new FleetStopResult(target, FleetStopOutcome.Failed,
                    $"{fullCommand} 沒有被任何處理常式接下（指令在送出的瞬間被登出了？）。{note}");
        }
        catch (Exception ex)
        {
            return new FleetStopResult(target, FleetStopOutcome.Failed,
                $"送出 {fullCommand} 時發生例外：{ex.GetType().Name}：{ex.Message}{note}");
        }
    }
}

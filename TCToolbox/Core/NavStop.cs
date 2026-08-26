using System;
using Dalamud.Game.Command;
using Dalamud.Plugin.Services;

namespace TCToolbox.Core;

/// <summary>
/// 共用的「停下由本外掛發起的移動」設施：一個 <c>/tcstop</c> 指令 ＋ 補送停止的看門狗。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 <b>為什麼需要補送而不是送一次就好</b>：<c>vnavmesh.Path.Stop</c> 清的是「已經算好的路徑點」，
/// 但 <c>SimpleMove.PathfindAndMoveTo</c> 是把路徑計算丟到背景工作，算完之後才交給
/// FollowPath 開走。所以在「還在算」的那段期間按停止是攔不住的——使用者會看到
/// 「按了停止、幾秒後角色自己走起來」。
/// 解法是開一個補送窗口，持續送停止直到 vnavmesh 兩個狀態都回 false。
/// （完整依據見 <see cref="ExternalNav.IsVnavmeshPathfindInProgress"/> 的說明。）
/// </para>
/// <para>
/// 📌 <b>為什麼做成引用計數的共用設施</b>：會發起移動的模組不只一個
/// （點擊移動、旗標指令…），但 <c>/tcstop</c> 這個指令名只能註冊一次——
/// 兩個模組各自 <c>AddHandler</c> 同一個名字，第二個會失敗，而且失敗是靜默的
/// （使用者只會發現指令有時候有效、有時候沒有）。
/// 這裡由第一個 <see cref="Acquire"/> 註冊、最後一個 <see cref="Release"/> 移除。
/// </para>
/// <para>⚠️ 只在主執行緒使用（模組的啟用／停用與 framework 更新都在主執行緒）。</para>
/// </remarks>
internal static class NavStop
{
    /// <summary>停止移動的指令。</summary>
    public const string Command = "/tcstop";

    /// <summary>
    /// 補送停止指令的窗口長度。
    /// </summary>
    /// <remarks>
    /// 3 秒是為了蓋過「路徑還在背景計算」那段。窗口內每 100ms 補送一次，
    /// 一旦確認既沒在算也沒在走就提早收工，不會空轉滿 3 秒。
    /// </remarks>
    private static readonly TimeSpan EnforceWindow = TimeSpan.FromSeconds(3);

    /// <summary>
    /// 補送停止的<b>絕對</b>上限（自第一次 <see cref="RequestStop"/> 起算）。
    /// </summary>
    /// <remarks>
    /// 🔴 為什麼需要：窗口到期時只要 vnavmesh 還在算路徑就會延展（見
    /// <see cref="OnFrameworkUpdate"/>），若 IPC 端點因故永遠卡在 true，看門狗就會
    /// 永久補送。這條上限保證它一定會收工。
    /// 📌 正常情況遠遠用不到——<c>IsVnavmeshPathfindInProgress</c> 在 IPC 出錯時回 false，
    /// vnavmesh 中途被拆掉也會自然走到「兩個都 false」的提早收工分支。
    /// </remarks>
    private static readonly TimeSpan AbsoluteCap = TimeSpan.FromSeconds(30);

    private static int refCount;
    private static DateTime enforceUntil = DateTime.MinValue;

    /// <summary>本輪補送是從什麼時候開始的（算 <see cref="AbsoluteCap"/> 用）。</summary>
    private static DateTime enforceStartedAt = DateTime.MinValue;

    private static bool watchdogSubscribed;
    private static bool commandRegistered;

    /// <summary>
    /// 最後一個使用者已經離開，但補送窗口還沒關 —— 窗口一關就自己拆掉看門狗。
    /// </summary>
    private static bool pendingTeardown;

    /// <summary>目前是不是還在確保「真的停下來了」。</summary>
    public static bool IsEnforcing => enforceUntil != DateTime.MinValue;

    /// <summary>登記一個使用者（模組啟用時呼叫）。第一個使用者會註冊指令與看門狗。</summary>
    public static void Acquire()
    {
        refCount++;
        if (refCount != 1) return;

        // 前一輪的延後拆卸還沒執行就又有人進來了——撤銷它，繼續共用同一個看門狗。
        pendingTeardown = false;

        if (!commandRegistered)
        {
            Svc.Commands.AddHandler(Command, new CommandInfo(OnStopCommand)
            {
                HelpMessage = "停止 TC Toolbox 發起的自動移動",
            });
            commandRegistered = true;
        }

        if (!watchdogSubscribed)
        {
            Svc.Framework.Update += OnFrameworkUpdate;
            watchdogSubscribed = true;
        }
    }

    /// <summary>登出一個使用者（模組停用時呼叫）。最後一個離開時收掉指令與看門狗。</summary>
    /// <remarks>
    /// 🔴 <b>補送窗口必須活過最後一個 Release</b>：模組的 <c>OnDisable</c> 標準寫法是
    /// 「先 <see cref="RequestStop"/> 再 Release」，若 Release 當場拆掉看門狗，那個
    /// 3 秒補送窗口就只剩下 RequestStop 裡的<b>單獨一發</b> —— 而這個類別存在的唯一
    /// 理由就是「單獨一發攔不住還在背景計算的路徑」。於是使用者關掉模組後，角色
    /// 幾秒後自己走起來，而 <c>/tcstop</c> 這時也已經登出了。
    /// ⇒ 指令照舊立刻登出（模組停用了就不該還留著指令），但看門狗改成延後拆：
    /// 交給 <see cref="OnFrameworkUpdate"/> 在窗口收掉的那一刻自行退訂。
    /// ⚠️ 外掛整個卸載走的是 <see cref="ForceTeardown"/>（不能留訂閱給已卸載的組件）。
    /// </remarks>
    public static void Release()
    {
        refCount--;
        if (refCount > 0) return;

        // 防禦：不管怎麼失衡都不要讓計數變成負的，否則下一次 Acquire 不會註冊指令。
        refCount = 0;

        if (commandRegistered)
        {
            Svc.Commands.RemoveHandler(Command);
            commandRegistered = false;
        }

        if (IsEnforcing)
        {
            pendingTeardown = true;
            return;
        }

        Teardown();
    }

    /// <summary>
    /// 硬拆：無論補送窗口是否還開著，一律退訂看門狗並清空狀態。
    /// </summary>
    /// <remarks>
    /// 🔴 只給外掛整個卸載的路徑用（<c>Plugin.Dispose</c>）。組件都要被卸載了，
    /// 絕對不能留一個指向本組件的 <c>Framework.Update</c> 訂閱。
    /// </remarks>
    public static void ForceTeardown()
    {
        refCount = 0;

        if (commandRegistered)
        {
            Svc.Commands.RemoveHandler(Command);
            commandRegistered = false;
        }

        Teardown();
    }

    private static void Teardown()
    {
        if (watchdogSubscribed)
        {
            Svc.Framework.Update -= OnFrameworkUpdate;
            watchdogSubscribed = false;
        }

        enforceUntil = DateTime.MinValue;
        enforceStartedAt = DateTime.MinValue;
        pendingTeardown = false;
    }

    /// <summary>關掉補送窗口；若拆卸還欠著就順手拆掉。</summary>
    private static void CloseWindow()
    {
        enforceUntil = DateTime.MinValue;
        enforceStartedAt = DateTime.MinValue;

        if (pendingTeardown)
            Teardown();
    }

    /// <summary>
    /// 立刻要求停止，並開啟補送窗口。
    /// </summary>
    /// <remarks>📌 對「本來就沒在移動」是安全的無操作，呼叫端不必先檢查。</remarks>
    public static void RequestStop()
    {
        ExternalNav.TryStopMovement();

        var now = DateTime.UtcNow;

        // 絕對上限自「這一輪的第一次要求停止」起算：連按停止不會把上限一直往後推。
        if (enforceStartedAt == DateTime.MinValue)
            enforceStartedAt = now;

        enforceUntil = now + EnforceWindow;
        Throttle.Reset("NavStop-Enforce");
    }

    private static void OnStopCommand(string command, string arguments)
    {
        RequestStop();
        Svc.Chat.Print("[TC Toolbox] 已要求停止移動。");
        Svc.Log.Information("[NavStop] 使用者以指令要求停止移動。");
    }

    /// <summary>沒開補送窗口時，這裡就只是一行比較後直接返回。</summary>
    private static void OnFrameworkUpdate(IFramework framework)
    {
        if (enforceUntil == DateTime.MinValue)
        {
            // 窗口早就關了、只欠拆卸（Release 已經走過延後路徑）。
            if (pendingTeardown) Teardown();
            return;
        }

        var now = DateTime.UtcNow;

        if (now >= enforceUntil)
        {
            // 🔴 到期不能無條件放棄：路徑計算超過窗口長度（大地圖遠距離目標很常見）時，
            //    vnavmesh 算完照樣把路徑交給 FollowPath 開走 —— 那正是這個類別要修掉的
            //    「按了停止、幾秒後角色自己走起來」原樣復發。
            //    「它存在的唯一理由仍然成立」時要延展窗口，不是到期即棄。
            if (ExternalNav.IsVnavmeshPathfindInProgress() && now - enforceStartedAt < AbsoluteCap)
            {
                enforceUntil = now + EnforceWindow;
                return;
            }

            if (now - enforceStartedAt >= AbsoluteCap)
            {
                // 使用者回報用：走到這裡代表 vnavmesh 的 pathfinding 狀態卡在 true 沒下來。
                Svc.Log.Information(
                    $"[NavStop] 補送停止已達絕對上限 {AbsoluteCap.TotalSeconds:0} 秒（vnavmesh 仍回報正在計算路徑），停止補送。");
            }

            CloseWindow();
            return;
        }

        if (!Throttle.Pass("NavStop-Enforce", 100)) return;

        var pathfinding = ExternalNav.IsVnavmeshPathfindInProgress();
        var running = ExternalNav.IsVnavmeshPathRunning();

        if (running)
            ExternalNav.TryStopMovement();

        // 既沒在算路徑也沒在走＝真的停了，提早收工。
        if (!pathfinding && !running)
            CloseWindow();
    }
}

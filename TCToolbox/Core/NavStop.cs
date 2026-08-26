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

    private static int refCount;
    private static DateTime enforceUntil = DateTime.MinValue;

    /// <summary>目前是不是還在確保「真的停下來了」。</summary>
    public static bool IsEnforcing => enforceUntil != DateTime.MinValue;

    /// <summary>登記一個使用者（模組啟用時呼叫）。第一個使用者會註冊指令與看門狗。</summary>
    public static void Acquire()
    {
        refCount++;
        if (refCount != 1) return;

        Svc.Commands.AddHandler(Command, new CommandInfo(OnStopCommand)
        {
            HelpMessage = "停止 TC Toolbox 發起的自動移動",
        });

        Svc.Framework.Update += OnFrameworkUpdate;
    }

    /// <summary>登出一個使用者（模組停用時呼叫）。最後一個離開時收掉指令與看門狗。</summary>
    public static void Release()
    {
        refCount--;
        if (refCount > 0) return;

        // 防禦：不管怎麼失衡都不要讓計數變成負的，否則下一次 Acquire 不會註冊指令。
        refCount = 0;

        Svc.Framework.Update -= OnFrameworkUpdate;
        Svc.Commands.RemoveHandler(Command);
        enforceUntil = DateTime.MinValue;
    }

    /// <summary>
    /// 立刻要求停止，並開啟補送窗口。
    /// </summary>
    /// <remarks>📌 對「本來就沒在移動」是安全的無操作，呼叫端不必先檢查。</remarks>
    public static void RequestStop()
    {
        ExternalNav.TryStopMovement();
        enforceUntil = DateTime.UtcNow + EnforceWindow;
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
        if (enforceUntil == DateTime.MinValue) return;

        if (DateTime.UtcNow >= enforceUntil)
        {
            enforceUntil = DateTime.MinValue;
            return;
        }

        if (!Throttle.Pass("NavStop-Enforce", 100)) return;

        var pathfinding = ExternalNav.IsVnavmeshPathfindInProgress();
        var running = ExternalNav.IsVnavmeshPathRunning();

        if (running)
            ExternalNav.TryStopMovement();

        // 既沒在算路徑也沒在走＝真的停了，提早收工。
        if (!pathfinding && !running)
            enforceUntil = DateTime.MinValue;
    }
}

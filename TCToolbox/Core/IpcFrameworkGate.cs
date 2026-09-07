using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TCToolbox.Core;

/// <summary>
/// IPC 端點的「遊戲主執行緒閘門」。
/// </summary>
/// <remarks>
/// 🔴🔴 <b>為什麼需要這一層</b>：Dalamud 的 CallGate 是<b>直接方法呼叫</b>，提供端的碼跑在
/// <b>呼叫端的執行緒</b>上。別的外掛從自己的背景工作、<c>Task.Run</c>、或任何非 framework
/// 執行緒打過來時，本外掛這一側就會在那條執行緒上做兩件危險的事：
/// <list type="number">
/// <item><b>讀遊戲的原生記憶體</b>——<c>Svc.Objects</c>／<c>Svc.Targets.Target</c>／
/// <c>HousingManager.Instance()-&gt;</c>／<c>InventoryManager.Instance()-&gt;</c>。
/// 那些物件由遊戲主執行緒每幀重建，讀到一半被換掉就是 AccessViolationException，
/// 而 AVE 在 .NET Core 是 corrupted-state exception ——<b><c>try</c>/<c>catch</c> 攔不到</b>，
/// 整個遊戲直接崩掉。</item>
/// <item><b>寫模組自己的裸集合</b>——<see cref="TaskQueue"/> 的步驟清單是
/// <c>List&lt;Entry&gt;</c>、零同步，而 framework 執行緒每幀在 <c>entries[0]</c>／
/// <c>RemoveAt(0)</c>。並行插入弄壞的<b>不是「慢一拍」而是那個 List 本身</b>；
/// <c>Stop</c> 的 <c>Abort()</c> 更是直接 <c>Clear()</c>，撞上 <c>Tick()</c> 讀隊首就是
/// 索引越界。</item>
/// </list>
/// <para>
/// 🔑 所以<b>每一個</b>對外端點都把整段工作交回主執行緒執行。交回去的是<b>整個方法體</b>，
/// 不是只有第一行檢查 —— 這樣連下游 helper（<c>TryGetGardenPatches</c>、
/// <c>FindInventoryItem</c>、排進佇列之前的前置檢查、聊天輸出）也一起被覆蓋，不必逐一追。
/// </para>
/// <para>
/// 📌 <b>已經在主執行緒上呼叫時行為逐字不變</b>：直接就地執行，不配置 Task、不改變例外型別、
/// 不多花任何一幀。絕大多數消費端（在自己的 <c>Framework.Update</c> 或任務佇列裡呼叫）
/// 走的就是這條路。
/// </para>
/// <para>
/// ⚠️ <b>逾時的處置</b>：等主執行緒最多 <see cref="TimeoutMs"/> 毫秒。逾時就回該端點的
/// 「不可用」值 —— 刻意<b>與模組不存在時的回傳值相同</b>（false／空字串／0／空清單／
/// <c>"unknown"</c>／<c>-1</c>），因為呼叫端本來就要處理那個狀態。
/// 同時用 <see cref="Interlocked"/> 把還沒開始跑的工作標成放棄，避免「呼叫端已經拿到失敗
/// 走人了，五秒後動作才真的排進佇列」這種形狀。
/// </para>
/// <para>
/// 🔴 用 <c>RunOnFrameworkThread</c> 不是 <c>Framework.Run</c>：前者在已經是主執行緒時
/// 就地執行，同步等它不會死結；後者一律 <c>StartNew</c>，同步等會死結。
/// </para>
/// <para>
/// 📌 形狀對齊 <c>Lifestream/Lifestream/IPC/IpcFrameworkGate.cs</c>（2026-09-06 起）。
/// </para>
/// </remarks>
internal static class IpcFrameworkGate
{
    /// <summary>等主執行緒的上限。超過就當作「現在做不到」。</summary>
    internal const int TimeoutMs = 5000;

    private const int StatePending = 0;
    private const int StateRunning = 1;
    private const int StateAbandoned = 2;

    /// <summary>有回傳值的端點。<paramref name="unavailable"/> 是逾時時要回的「不可用」值。</summary>
    internal static T Get<T>(string endpoint, Func<T> body, T unavailable)
    {
        if (Svc.Framework.IsInFrameworkUpdateThread) return body();

        var state = StatePending;
        var task = Svc.Framework.RunOnFrameworkThread(() =>
        {
            // 呼叫端已經逾時走人了就什麼都不做。
            if (Interlocked.CompareExchange(ref state, StateRunning, StatePending) != StatePending) return unavailable;
            return body();
        });

        // 🔴 WaitAny 對已經失敗的工作也回 0（不擲），交給 GetResult 原樣重擲原始例外，
        // 這樣呼叫端看到的例外型別與沒有這層閘門時完全一樣（不會變成 AggregateException）。
        if (Task.WaitAny([task], TimeoutMs) == 0) return task.GetAwaiter().GetResult();

        ReportTimeout(endpoint, Interlocked.CompareExchange(ref state, StateAbandoned, StatePending) == StatePending);
        return unavailable;
    }

    /// <summary>沒有回傳值的端點。</summary>
    internal static void Run(string endpoint, Action body)
    {
        if (Svc.Framework.IsInFrameworkUpdateThread)
        {
            body();
            return;
        }

        var state = StatePending;
        var task = Svc.Framework.RunOnFrameworkThread(() =>
        {
            if (Interlocked.CompareExchange(ref state, StateRunning, StatePending) != StatePending) return;
            body();
        });

        if (Task.WaitAny([task], TimeoutMs) == 0)
        {
            task.GetAwaiter().GetResult();
            return;
        }

        ReportTimeout(endpoint, Interlocked.CompareExchange(ref state, StateAbandoned, StatePending) == StatePending);
    }

    /// <summary>同一個端點的逾時訊息重印間隔。</summary>
    private const long TimeoutLogIntervalMs = 10000;

    /// <summary>節流表上限，避免端點名意外發散時無限成長。</summary>
    private const int MaxTrackedTimeoutKeys = 128;

    private static readonly Dictionary<string, long> TimeoutLogTimes = [];

    /// <summary>
    /// 自帶的節流：首次必放行，之後每 <see cref="TimeoutLogIntervalMs"/> 毫秒放行一次。
    /// </summary>
    /// <remarks>
    /// 🔴 這裡刻意<b>不用</b> <see cref="Throttle"/>：那支的字典是<b>只在主執行緒使用</b>的裸
    /// <c>Dictionary</c>（它自己的註解就這麼寫），而這條路徑跑在呼叫端的執行緒上，
    /// 並行插入弄壞的是整張表 —— 連帶弄壞外掛裡所有模組的節流。所以自帶字典＋自己的鎖。
    /// 🔴 鎖內只碰字典 —— 不寫 log、不做 I/O、不呼叫任何別的外掛。
    /// </remarks>
    private static bool ShouldLogTimeout(string key)
    {
        var now = Environment.TickCount64;
        lock (TimeoutLogTimes)
        {
            if (TimeoutLogTimes.TryGetValue(key, out var last) && now - last < TimeoutLogIntervalMs) return false;
            if (TimeoutLogTimes.Count >= MaxTrackedTimeoutKeys && !TimeoutLogTimes.ContainsKey(key)) TimeoutLogTimes.Clear();
            TimeoutLogTimes[key] = now;
            return true;
        }
    }

    /// <summary>
    /// 要使用者回報的診斷寫 <c>Information</c>（使用者的 LogLevel 收得到，
    /// 且不會被 Debug 的數十萬行淹沒）。
    /// </summary>
    private static void ReportTimeout(string endpoint, bool abandoned)
    {
        if (!ShouldLogTimeout(endpoint)) return;

        var outcome = abandoned
            ? "工作還沒開始就被取消，什麼都沒做"
            : "工作已經開始執行，會照常跑完（呼叫端拿到的回值不代表它沒發生）";
        Svc.Log.Information(
            $"[TC Toolbox IPC 閘門] {endpoint} 等待遊戲主執行緒超過 {TimeoutMs} 毫秒，已回傳「不可用」值。{outcome}。" +
            "通常代表遊戲正在讀取畫面或嚴重掉幀；若持續出現請回報。");
    }
}

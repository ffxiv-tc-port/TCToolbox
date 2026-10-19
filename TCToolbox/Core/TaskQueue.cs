using System;
using System.Collections.Generic;

namespace TCToolbox.Core;

/// <summary>
/// 艦隊慣例的 tick 狀態機：具名步驟、逾時中止、可取消。
/// 由擁有者模組在 Framework.Update 裡呼叫 <see cref="Tick"/>。
/// 步驟回傳值：true=完成進下一步、false=下一 tick 重試、null=中止整條佇列。
/// </summary>
public sealed class TaskQueue
{
    /// <summary>記錄裡的佇列名。沒設就用步驟委派所屬的型別名；共用同一個類別的呼叫端才需要自己設。</summary>
    public string? Owner { get; init; }

    private sealed class Entry
    {
        public required string Name;
        public required Func<bool?> Step;
        public int TimeoutMs;
        public DateTime? StartedAt;
    }

    private readonly List<Entry> entries = [];

    /// <summary>預設單步逾時（毫秒）。</summary>
    public int DefaultTimeoutMs { get; init; } = 10_000;

    public bool IsBusy => entries.Count > 0;

    public string? CurrentStep => entries.Count > 0 ? entries[0].Name : null;

    /// <summary>逾時中止時觸發（參數為步驟名）。</summary>
    public Action<string>? OnTimeout { get; set; }

    /// <summary>步驟擲例外、佇列已清空之後觸發（步驟名、例外）。</summary>
    /// <remarks>沒設就改呼叫 <see cref="OnTimeout"/>（步驟名後註明是例外）：收尾比措辭重要，漏收尾會留下鎖與旗標。</remarks>
    public Action<string, Exception>? OnException { get; set; }

    public void Enqueue(string name, Func<bool?> step, int? timeoutMs = null) =>
        entries.Add(new Entry { Name = name, Step = step, TimeoutMs = timeoutMs ?? DefaultTimeoutMs });

    public void Enqueue(string name, Action action, int? timeoutMs = null) =>
        Enqueue(name, () =>
        {
            action();
            return true;
        }, timeoutMs);

    public void EnqueueWait(string name, Func<bool> condition, int? timeoutMs = null) =>
        Enqueue(name, () => condition() ? true : false, timeoutMs);

    public void EnqueueDelay(int milliseconds, string name = "等待")
    {
        DateTime? until = null;
        Enqueue(name, () =>
        {
            until ??= DateTime.UtcNow.AddMilliseconds(milliseconds);
            return DateTime.UtcNow >= until.Value ? true : false;
        }, milliseconds + 5_000);
    }

    public void Abort() => entries.Clear();

    /// <summary>正常收工：清掉剩下的步驟並回 <see langword="true"/>，給步驟直接 <c>return queue.Finish();</c>。</summary>
    /// <remarks>佇列結果與回 <see langword="null"/> 相同（整條清空），差別只在不寫「回傳 null」那行、語意上不是中止。</remarks>
    public bool Finish()
    {
        Abort();
        return true;
    }

    public void Tick()
    {
        if (entries.Count == 0) return;

        var current = entries[0];
        current.StartedAt ??= DateTime.UtcNow;

        if ((DateTime.UtcNow - current.StartedAt.Value).TotalMilliseconds > current.TimeoutMs)
        {
            Svc.Log.Warning($"[TaskQueue] 步驟逾時，中止佇列：{current.Name}（捨棄後面 {entries.Count - 1} 項）");
            Abort();
            OnTimeout?.Invoke(current.Name);
            return;
        }

        bool? result;
        try
        {
            result = current.Step();
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, $"[TaskQueue] 步驟擲出例外，中止佇列：{current.Name}");
            Abort();
            InvokeFaultCallback(current.Name, ex);
            return;
        }

        switch (result)
        {
            case true:
                // 步驟本身可能已呼叫 Abort()／Enqueue()，僅在原步驟仍在隊首時移除
                if (entries.Count > 0 && ReferenceEquals(entries[0], current))
                    entries.RemoveAt(0);
                break;
            case null:
            {
                // 回 null 可能是正常收尾，也可能是步驟自己判定要中止——兩種都只會在這一輪發生一次。
                var discarded = entries.Count - (entries.Count > 0 && ReferenceEquals(entries[0], current) ? 1 : 0);
                Svc.Log.Information(
                    $"[TaskQueue] {Owner ?? DescribeOwner(current.Step)}：步驟「{current.Name}」回傳 null，"
                    + $"清空佇列（捨棄後面 {discarded} 項）。");
                Abort();
                break;
            }
        }
    }

    private void InvokeFaultCallback(string step, Exception ex)
    {
        try
        {
            if (OnException != null)
                OnException(step, ex);
            else
                OnTimeout?.Invoke($"{step}（實為擲出例外：{ex.GetType().Name}）");
        }
        catch (Exception callbackEx)
        {
            Svc.Log.Error(callbackEx, $"[TaskQueue] 例外後的收尾回呼也擲出例外：{step}");
        }
    }

    /// <summary>步驟委派所屬的型別名；lambda 編進編譯器產生的巢狀型別時，往外找到第一個具名型別。</summary>
    /// <remarks>刻意不用 <c>[CallerFilePath]</c>：那會把建置機器的完整路徑編進出貨的 DLL。</remarks>
    private static string DescribeOwner(Delegate step)
    {
        var type = step.Method.DeclaringType;
        while (type is { IsNested: true } && type.Name.StartsWith('<'))
            type = type.DeclaringType;

        return type?.Name ?? "?";
    }
}

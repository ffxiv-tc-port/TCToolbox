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

    public void Enqueue(string name, Func<bool?> step, int? timeoutMs = null) =>
        entries.Add(new Entry { Name = name, Step = step, TimeoutMs = timeoutMs ?? DefaultTimeoutMs });

    public void Enqueue(string name, Action action, int? timeoutMs = null) =>
        Enqueue(name, () =>
        {
            action();
            return true;
        }, timeoutMs);

    /// <summary>等待條件成立。</summary>
    public void EnqueueWait(string name, Func<bool> condition, int? timeoutMs = null) =>
        Enqueue(name, () => condition() ? true : false, timeoutMs);

    /// <summary>純延遲。</summary>
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

    public void Tick()
    {
        if (entries.Count == 0) return;

        var current = entries[0];
        current.StartedAt ??= DateTime.UtcNow;

        if ((DateTime.UtcNow - current.StartedAt.Value).TotalMilliseconds > current.TimeoutMs)
        {
            Svc.Log.Warning($"[TaskQueue] 步驟逾時，中止佇列：{current.Name}");
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
                Abort();
                break;
        }
    }
}

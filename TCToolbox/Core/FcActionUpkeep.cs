using System;
using System.Collections.Generic;

namespace TCToolbox.Core;

// 本檔只用 System：庫存記帳、低庫存提醒、身上狀態的消失偵測與到期判斷。
// 遊戲端的讀取全部在 FcActionAutoUse 模組；這裡的東西離線就能重播。
// 失敗方向一律是「不續開」：判不出、讀數不像倒數、開始時間對不上，都不算到期。

internal static class FcStockLedger
{
    /// <summary>庫存清單裡每種特效出現幾次。</summary>
    /// <remarks>假設每一份各佔一筆（未在實機校準），所以畫面上一律標「約」。</remarks>
    public static Dictionary<uint, int> CountByAction(IReadOnlyList<uint> actions)
    {
        var counts = new Dictionary<uint, int>();
        foreach (var id in actions)
            counts[id] = counts.TryGetValue(id, out var n) ? n + 1 : 1;
        return counts;
    }

    public static bool SameCounts(IReadOnlyDictionary<uint, int>? a, IReadOnlyDictionary<uint, int>? b)
    {
        if (a == null || b == null) return a == b;
        if (a.Count != b.Count) return false;
        foreach (var (k, v) in a)
        {
            if (!b.TryGetValue(k, out var w) || w != v) return false;
        }

        return true;
    }

    /// <summary>該提醒的特效：勾選的、數量 ≤ <paramref name="threshold"/>、這次登入還沒提醒過。</summary>
    /// <param name="counts"><see langword="null"/>＝這個角色從沒讀過庫存（不知道就不提醒）。</param>
    /// <param name="threshold">0 或負數＝不提醒。</param>
    public static List<uint> DueReminders(IEnumerable<uint> chosen, IReadOnlyDictionary<uint, int>? counts, int threshold,
        IReadOnlySet<uint> alreadyReminded)
    {
        var due = new List<uint>();
        if (counts == null || threshold <= 0) return due;

        foreach (var id in chosen)
        {
            if (alreadyReminded.Contains(id) || due.Contains(id)) continue;
            if (counts.GetValueOrDefault(id) <= threshold) due.Add(id);
        }

        return due;
    }
}

internal enum FcWatchEventKind
{
    None = 0,

    /// <summary>從沒有變成有。</summary>
    Appeared,

    /// <summary>從有變成沒有，而且已經連續沒有 <see cref="FcStatusWatch.DebounceMs"/>（只算角色在、不在讀取區域的時間）。</summary>
    Disappeared,
}

/// <param name="AfterConfirmedAbsence">出現之前確定沒有過（不是剛開始看、也不是換區閃一下）。</param>
/// <param name="LastRemaining">消失前最後一次看到的剩餘秒數（原始值）。</param>
/// <param name="RemainingCounts">那段期間剩餘秒數有照時鐘倒數；<see langword="false"/>＝這個值沒有意義。</param>
/// <param name="SecondsSinceLastSample">最後一次看到它到第一次確定看不到之間隔了幾秒。</param>
internal readonly record struct FcWatchEvent(
    FcWatchEventKind Kind,
    uint StatusId,
    bool AfterConfirmedAbsence,
    float LastRemaining,
    bool RemainingCounts,
    double SecondsSinceLastSample,
    DateTime AbsentSinceUtc)
{
    public static readonly FcWatchEvent None = default;
}

/// <summary>追蹤幾個身上狀態的有／沒有。呼叫端約每秒餵一次，不需要每幀。</summary>
/// <remarks>
/// 契約：只在框架執行緒上呼叫；不配置（固定容量）。只存狀態編號、時間與數字，不存任何原生指標。
/// 「沒有」只在 settled（角色在、不在讀取區域）時累計；中途不 settled 就重新計時。
/// </remarks>
internal sealed class FcStatusWatch
{
    public const int Capacity = 32;
    public const int DebounceMs = 20_000;

    /// <summary>判「剩餘時間有在倒數」要隔多久比一次。</summary>
    public const int RateCheckMs = 10_000;

    private struct Slot
    {
        public uint StatusId;
        public bool Wanted;
        public bool Known;
        public bool Present;
        public bool AbsenceConfirmed;
        public long LastSeenMs;
        public float LastRemaining;
        public long AnchorMs;
        public float AnchorRemaining;
        public bool Counting;
        public long AbsentSinceMs;
        public DateTime AbsentSinceUtc;
    }

    private readonly Slot[] slots = new Slot[Capacity];
    private int count;

    public int Count => count;

    public void Clear() => count = 0;

    /// <summary>標記這一輪要追的狀態；之後呼叫 <see cref="Sweep"/> 丟掉這一輪沒標到的。</summary>
    public void BeginMark()
    {
        for (var i = 0; i < count; i++) slots[i].Wanted = false;
    }

    public void Mark(uint statusId)
    {
        var i = IndexOf(statusId);
        if (i >= 0)
        {
            slots[i].Wanted = true;
            return;
        }

        if (count >= Capacity) return;
        slots[count++] = new Slot { StatusId = statusId, Wanted = true, AbsentSinceMs = -1, AnchorMs = -1 };
    }

    public void Sweep()
    {
        var w = 0;
        for (var r = 0; r < count; r++)
        {
            if (!slots[r].Wanted) continue;
            slots[w++] = slots[r];
        }

        count = w;
    }

    public uint StatusAt(int index) => slots[index].StatusId;

    /// <summary>目前判定為「有」（已過去抖）。不在追蹤裡或還沒看過回 <see langword="null"/>。</summary>
    public bool? IsPresent(uint statusId)
    {
        var i = IndexOf(statusId);
        return i < 0 || !slots[i].Known ? null : slots[i].Present;
    }

    public FcWatchEvent Observe(uint statusId, bool present, float remaining, bool settled, long nowMs, DateTime nowUtc)
    {
        var i = IndexOf(statusId);
        if (i < 0) return FcWatchEvent.None;
        ref var s = ref slots[i];

        if (present)
        {
            var evt = FcWatchEvent.None;
            if (!s.Known)
            {
                s.Known = true;
                s.Present = true;
            }
            else if (!s.Present)
            {
                evt = new FcWatchEvent(FcWatchEventKind.Appeared, statusId, s.AbsenceConfirmed, 0, false, 0, default);
                s.Present = true;
                s.Counting = false;
                s.AnchorMs = -1;
            }

            s.AbsentSinceMs = -1;
            s.AbsenceConfirmed = false;
            TrackRemaining(ref s, remaining, nowMs);
            s.LastSeenMs = nowMs;
            s.LastRemaining = remaining;
            return evt;
        }

        if (!settled)
        {
            s.AbsentSinceMs = -1;
            return FcWatchEvent.None;
        }

        if (!s.Known)
        {
            s.Known = true;
            s.Present = false;
        }

        if (s.AbsentSinceMs < 0)
        {
            s.AbsentSinceMs = nowMs;
            s.AbsentSinceUtc = nowUtc;
        }

        if (nowMs - s.AbsentSinceMs < DebounceMs || s.AbsenceConfirmed) return FcWatchEvent.None;

        s.AbsenceConfirmed = true;
        if (!s.Present) return FcWatchEvent.None;

        s.Present = false;
        var gap = Math.Max(0, s.AbsentSinceMs - s.LastSeenMs) / 1000.0;
        return new FcWatchEvent(FcWatchEventKind.Disappeared, statusId, false, s.LastRemaining, s.Counting, gap, s.AbsentSinceUtc);
    }

    /// <summary>剩餘秒數在一段時間內減少的量和時鐘一致，才算「有在倒數」；常數、0、亂跳都不算。</summary>
    private static void TrackRemaining(ref Slot s, float remaining, long nowMs)
    {
        if (s.AnchorMs < 0)
        {
            s.AnchorMs = nowMs;
            s.AnchorRemaining = remaining;
            return;
        }

        var dtMs = nowMs - s.AnchorMs;
        if (dtMs < RateCheckMs) return;

        var dt = dtMs / 1000.0;
        var drop = s.AnchorRemaining - remaining;
        s.Counting = remaining > 0 && Math.Abs(drop - dt) <= 1.5 + 0.05 * dt;
        s.AnchorMs = nowMs;
        s.AnchorRemaining = remaining;
    }

    private int IndexOf(uint statusId)
    {
        for (var i = 0; i < count; i++)
        {
            if (slots[i].StatusId == statusId) return i;
        }

        return -1;
    }
}

internal enum FcExpiryVerdict
{
    /// <summary>判不出來。零值＝不續開。</summary>
    Unknown = 0,
    NotExpired,
    Expired,
}

internal static class FcExpiryJudge
{
    /// <summary>公會特效持續時間（CompanyAction 說明：24 小時，地球時間）。</summary>
    public static readonly TimeSpan Duration = TimeSpan.FromHours(24);

    /// <summary>用「開始時間＋24 小時」判到期時容許的誤差。</summary>
    public static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(20);

    /// <summary>剩餘時間有在倒數時，推到消失那一刻還剩這麼多秒以內才算到期。</summary>
    public const double RemainingSlackSeconds = 90;

    /// <summary>這段時間內記過開始時間的，再看到它出現時不覆寫（多半是同一份換區回來或被取消又開）。</summary>
    public static readonly TimeSpan KeepStartWithin = TimeSpan.FromHours(23);

    /// <param name="knownStartUtc">已知最近一次開始的時間；<see langword="null"/>＝不知道。</param>
    public static FcExpiryVerdict Judge(in FcWatchEvent e, DateTime? knownStartUtc, out string why)
    {
        if (e.Kind != FcWatchEventKind.Disappeared)
        {
            why = "不是消失事件";
            return FcExpiryVerdict.Unknown;
        }

        if (e.RemainingCounts)
        {
            var projected = e.LastRemaining - e.SecondsSinceLastSample;
            if (projected <= RemainingSlackSeconds)
            {
                why = $"剩餘時間有在倒數，推到消失時約剩 {projected:F0} 秒";
                return FcExpiryVerdict.Expired;
            }

            why = $"剩餘時間有在倒數，消失時還剩約 {projected:F0} 秒（換區、被取消或其他原因）";
            return FcExpiryVerdict.NotExpired;
        }

        if (knownStartUtc is not { } start)
        {
            why = "剩餘時間沒有在倒數，也不知道什麼時候開的";
            return FcExpiryVerdict.Unknown;
        }

        var lasted = e.AbsentSinceUtc - start;
        var off = (lasted - Duration).Duration();
        if (off <= Tolerance)
        {
            why = $"從開始到消失約 {lasted.TotalHours:F2} 小時（持續時間 24 小時）";
            return FcExpiryVerdict.Expired;
        }

        why = $"從開始到消失約 {lasted.TotalHours:F2} 小時，和 24 小時差太多";
        return FcExpiryVerdict.NotExpired;
    }

    /// <summary>看到狀態在確定沒有之後出現時，要不要把「現在」記成它的開始時間。</summary>
    public static bool ShouldAdoptObservedStart(DateTime? knownStartUtc, DateTime nowUtc) =>
        knownStartUtc is not { } start || start > nowUtc || nowUtc - start >= KeepStartWithin;
}

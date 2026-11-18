namespace TCToolbox.Core;

/// <summary><see cref="DailyDutyTripWatch.Observe"/> 這一幀觀察到的轉換。</summary>
internal enum DailyDutyTripEvent
{
    None = 0,

    /// <summary>自己送出的行程第一次被看到在跑。</summary>
    Started,

    /// <summary>看過在跑、現在不跑了（抵達、失敗或被取消）；之後不再追蹤。</summary>
    Ended,

    /// <summary>送出後一直沒看到在跑就過了寬限；之後不再追蹤。</summary>
    NeverStarted,

    /// <summary>使用者手動移動、要取消這一趟（每趟最多回一次）。</summary>
    CancelForManualMove,
}

/// <summary>
/// 追蹤「本插件替使用者按下的那一趟 DailyDuty 帶我去」：分得出那一趟是不是自己送的、何時開始與結束，
/// 以及使用者手動移動時要不要取消。純邏輯、不碰 Dalamud，時間由呼叫端給（毫秒、單調遞增）。
/// </summary>
/// <remarks>
/// 契約：DailyDuty 同時只跑一趟（在跑時不受理新的），所以「送出後第一次看到在跑」就是自己那一趟。
/// 不知道是誰發起的行程（例如在 DailyDuty 自己的介面按的）一律不追蹤，手動移動也不替它取消。
/// </remarks>
internal sealed class DailyDutyTripWatch
{
    /// <summary>送出後多久還沒看到在跑就當作沒出發（DailyDuty 下一幀就出發，原因印在它的聊天訊息）。</summary>
    public const long StartGraceMs = 10_000;

    private long issuedAt;
    private long runningSince;
    private bool seenRunning;
    private bool cancelRequested;

    /// <summary>正在追蹤的那一趟（內部名稱）；空字串＝沒有。</summary>
    public string Name { get; private set; } = string.Empty;

    public bool IsTracking => Name.Length != 0;

    /// <summary>這一趟已經有人要求取消（使用者按的或手動移動）。</summary>
    public bool CancelRequested => cancelRequested;

    /// <summary>從第一次看到在跑起算的毫秒數；還沒看到在跑時是 0。</summary>
    public long RunningMs(long now) => seenRunning ? now - runningSince : 0;

    /// <summary>DailyDuty 回「已受理」的那一刻呼叫。</summary>
    public void NoteIssued(string name, long now)
    {
        Name = name;
        issuedAt = now;
        runningSince = 0;
        seenRunning = false;
        cancelRequested = false;
    }

    /// <summary>使用者自己按了停止：照樣追蹤到結束，只是不再替他重送取消。</summary>
    public void NoteCancelRequested() => cancelRequested = true;

    public void Clear()
    {
        Name = string.Empty;
        seenRunning = false;
        cancelRequested = false;
    }

    /// <param name="running">DailyDuty 此刻回報有行程在跑。</param>
    /// <param name="userSteering">vnavmesh 此刻因使用者手動移動而讓位；只在 <paramref name="running"/> 時有意義。</param>
    /// <param name="cancelOnManualMove">設定：手動移動就取消。</param>
    public DailyDutyTripEvent Observe(bool running, bool userSteering, bool cancelOnManualMove, long now)
    {
        if (!IsTracking) return DailyDutyTripEvent.None;

        if (!running)
        {
            if (seenRunning)
            {
                Clear();
                return DailyDutyTripEvent.Ended;
            }

            if (now - issuedAt <= StartGraceMs) return DailyDutyTripEvent.None;

            Clear();
            return DailyDutyTripEvent.NeverStarted;
        }

        if (!seenRunning)
        {
            seenRunning = true;
            runningSince = now;
            return DailyDutyTripEvent.Started;
        }

        if (!cancelOnManualMove || !userSteering || cancelRequested) return DailyDutyTripEvent.None;

        cancelRequested = true;
        return DailyDutyTripEvent.CancelForManualMove;
    }
}

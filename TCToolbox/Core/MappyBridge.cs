using System.Collections.Generic;

namespace TCToolbox.Core;

/// <summary>
/// 「把一組標記同步到 Mappy」的共用狀態機：開關、Mappy 在不在／版本、從不在變成在要重放、
/// 簽章沒變就不推、定期全量重推。模組只負責組 <see cref="Pending"/> 與簽章。
/// </summary>
/// <remarks>
/// 用法（每個同步週期）：<see cref="TryBeginSync"/> 回 true → 清空並填 <see cref="Pending"/>、算簽章 →
/// <see cref="Commit"/>。節流鍵由模組傳入，與模組設定畫面的 <c>Throttle.Reset</c> 共用同一把。
/// 只在框架執行緒使用。
/// </remarks>
internal sealed class MappyBridge
{
    /// <summary>Mappy 橋接的狀態。<b>「不知道」是零值</b>。</summary>
    public enum State
    {
        /// <summary>還沒探測過。</summary>
        Unknown = 0,

        /// <summary>使用者把「也顯示到 Mappy」關掉了。</summary>
        Disabled,

        /// <summary>Mappy 沒裝或還沒載入。</summary>
        Missing,

        /// <summary>Mappy 回報的 IPC 版本比本模組寫的時候還舊。</summary>
        VersionTooOld,

        /// <summary>一切正常。</summary>
        Ok,
    }

    private readonly MappyMarkerPublisher publisher;

    public MappyBridge(string source) => publisher = new MappyMarkerPublisher(source);

    /// <summary>重複使用的緩衝區，免得每次同步都配一個新的 List。</summary>
    public readonly List<MappyMarkerPublisher.Marker> Pending = [];

    /// <summary>上一次成功推給 Mappy 的內容簽章；空字串＝還沒推過或已被作廢（設成空字串＝下一輪必推）。</summary>
    public string LastSignature = string.Empty;

    /// <summary>Mappy 端的狀態，給設定畫面顯示用；模組停用時設定畫面可以自己探測後寫入。</summary>
    public State Status = State.Unknown;

    /// <summary>最後一次同步的筆數（設定畫面用）。</summary>
    public int LastPlaced;
    public int LastRejected;
    public int LastSkippedNoCoords;

    /// <summary>Mappy 上一次探測時在不在（用來偵測「從不在變成在」）。</summary>
    private bool wasAvailable;

    /// <summary>目前地圖上有沒有我們放的東西（設定被關掉時要收乾淨）。</summary>
    private bool hasMarkers;

    /// <summary>最後一次推送實際呼叫了幾次 IPC（記錄用）。</summary>
    public int LastIpcCalls => publisher.LastIpcCalls;

    /// <summary>模組啟用時呼叫：立刻同步一次，並把手上的 handle 當成失效。</summary>
    public void OnEnable(string syncThrottleKey, string forceResyncThrottleKey)
    {
        Throttle.Reset(syncThrottleKey);
        Throttle.Reset(forceResyncThrottleKey);
        LastSignature = string.Empty;
        wasAvailable = false;
        publisher.Forget();
    }

    /// <summary>模組停用（含插件卸載）時呼叫：一定要把標記收乾淨，否則 Mappy 會一直畫著沒人更新的舊標記。</summary>
    public void OnDisable()
    {
        publisher.Clear();
        hasMarkers = false;
        wasAvailable = false;
        Status = State.Unknown;
        LastSignature = string.Empty;
        LastPlaced = 0;
        LastRejected = 0;
        LastSkippedNoCoords = 0;
    }

    /// <summary>同步的前半段。回 <see langword="true"/>＝這一輪要組標記並呼叫 <see cref="Commit"/>。</summary>
    /// <param name="missingBackoffMs">Mappy 不在時用 <c>Throttle.Block</c> 退避多久（<c>Pass</c> 在冷卻中不寫入，設不了退避）。</param>
    public bool TryBeginSync(string syncThrottleKey, int syncIntervalMs, bool showOnMappy, int missingBackoffMs)
    {
        if (!Throttle.Pass(syncThrottleKey, syncIntervalMs)) return false;

        if (!showOnMappy)
        {
            // 使用者剛把它關掉：收乾淨。之後就什麼都不做。
            if (hasMarkers)
            {
                publisher.Clear();
                hasMarkers = false;
                LastSignature = string.Empty;
                LastPlaced = 0;
            }

            Status = State.Disabled;
            return false;
        }

        // 未登入時讀不到要畫的狀態；已放上去的標記留著不動。
        if (!Svc.ClientState.IsLoggedIn) return false;

        if (!MappyMarkerIpc.TryGetVersion(out var version))
        {
            if (wasAvailable || Status != State.Missing)
            {
                // 🔴 手上的 handle 全部失效了。留著會讓下一次同步以為「已經放上去了」，表現成標記再也不出現。
                publisher.Forget();
                LastSignature = string.Empty;
            }

            wasAvailable = false;
            hasMarkers = false;
            Status = State.Missing;
            Throttle.Block(syncThrottleKey, missingBackoffMs);
            return false;
        }

        if (version < MappyMarkerIpc.SupportedVersion)
        {
            Status = State.VersionTooOld;
            return false;
        }

        // Mappy 從「不在」變成「在」＝它的標記表是空的，一定要重放。
        if (!wasAvailable)
        {
            publisher.Forget();
            LastSignature = string.Empty;
            wasAvailable = true;
        }

        return true;
    }

    /// <summary>同步的後半段：推 <see cref="Pending"/>（用完清空）。回 <see langword="true"/>＝這一輪真的推了。</summary>
    /// <param name="contentChanged">簽章和上一次推的不同（呼叫端只在這時寫記錄，免得每分鐘的保險重推洗版）。</param>
    public bool Commit(string forceResyncThrottleKey, int forceResyncIntervalMs, string newSignature,
                       int skippedNoCoords, out bool contentChanged)
    {
        // 保險：即使簽章沒變也定期整組重推一次（Mappy 可能在我們沒看見的時候被重載過）。
        var forceFull = Throttle.Pass(forceResyncThrottleKey, forceResyncIntervalMs);
        contentChanged = newSignature != LastSignature;

        if (!forceFull && !contentChanged && Status == State.Ok)
        {
            Pending.Clear();
            return false;
        }

        publisher.Publish(Pending, forceFull);
        Pending.Clear();

        LastPlaced = publisher.Placed;
        LastRejected = publisher.LastRejected;
        LastSkippedNoCoords = skippedNoCoords;
        LastSignature = newSignature;
        hasMarkers = publisher.Placed > 0;
        Status = State.Ok;
        return true;
    }
}

using System;

namespace TCToolbox.Core;

/// <summary>
/// 「我要按的視窗 YesAlready 也會自動按」時的讓位憑據：記最近一次請它暫停那一項的結果與時刻。
/// </summary>
/// <remarks>
/// 契約：YesAlready 在視窗出現的當下就按，所以 <see cref="Renew"/> 要放在<b>會叫出視窗的動作之前</b>，
/// 真的要按之前再問 <see cref="IsExclusive"/>。只在主執行緒使用。
/// </remarks>
internal sealed class YesAlreadyBotherLease
{
    /// <summary>每次請它暫停多久（毫秒）；時間到 YesAlready 自己恢復。</summary>
    public const int PauseMs = 10_000;

    /// <summary>上一次的結果還沒滿這麼久就不重問（「確認不了」除外，那種每次都重問）。</summary>
    public const int RenewAfterMs = 3_000;

    /// <summary>結果超過這麼久就不算數。比 <see cref="PauseMs"/> 短，留下暫停到期前的餘裕。</summary>
    public const int FreshMs = 8_000;

    private readonly string feature;
    private readonly Func<string, int, YesAlreadyIpc.BotherPause> pause;
    private readonly Func<long> tickMs;

    /// <summary>上一次問到結果的時刻（<see cref="tickMs"/> 座標）；<c>null</c>＝還沒問過。</summary>
    private long? renewedAt;

    /// <param name="feature">YesAlready 的功能名（例如 <c>MaterializeDialog</c>）。</param>
    /// <param name="pause">測試用替身；預設走 <see cref="YesAlreadyIpc.PauseBother"/>。</param>
    /// <param name="tickMs">測試用時鐘；預設是不受系統改時間影響的 <see cref="Environment.TickCount64"/>。</param>
    public YesAlreadyBotherLease(
        string feature,
        Func<string, int, YesAlreadyIpc.BotherPause>? pause = null,
        Func<long>? tickMs = null)
    {
        this.feature = feature;
        this.pause = pause ?? YesAlreadyIpc.PauseBother;
        this.tickMs = tickMs ?? (() => Environment.TickCount64);
    }

    /// <summary>最近一次的結果；還沒問過是 <see cref="YesAlreadyIpc.BotherPause.Failed"/>。</summary>
    public YesAlreadyIpc.BotherPause Last { get; private set; }

    /// <summary>
    /// 現在按下去不會有 YesAlready 同時按：它已暫停、那一項沒開、或它沒載入，而且這個答案還新。
    /// </summary>
    public bool IsExclusive =>
        Last != YesAlreadyIpc.BotherPause.Failed && renewedAt is { } at && tickMs() - at < FreshMs;

    /// <summary>請 YesAlready 暫停那一項（結果還新就不重問）。回 <see langword="false"/>＝確認不了，不要叫出視窗。</summary>
    public bool Renew()
    {
        var now = tickMs();
        if (Last != YesAlreadyIpc.BotherPause.Failed && renewedAt is { } at && now - at < RenewAfterMs)
            return true;

        Last = pause(feature, PauseMs);
        renewedAt = now;
        return Last != YesAlreadyIpc.BotherPause.Failed;
    }

    /// <summary>忘掉上一次的結果（新的一輪開始／模組停用）。</summary>
    public void Reset()
    {
        Last = YesAlreadyIpc.BotherPause.Failed;
        renewedAt = null;
    }
}

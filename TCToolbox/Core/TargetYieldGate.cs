using System;

namespace TCToolbox.Core;

/// <summary>
/// 「這一拍要不要把選目標讓給別的插件」的純邏輯（不碰 Dalamud），<see cref="Modules.AutoRetarget"/> 與離線探針共用。
/// 查詢回 null＝對方沒載入或沒有這支端點：照舊不讓，記一次原因，之後隔 <see cref="RetryMs"/> 才再問。
/// </summary>
internal sealed class TargetYieldGate
{
    internal const long RetryMs = 30_000;

    private long _retryAtMs;
    private bool _failureLogged;
    private bool _yieldLogged;

    /// <summary>每拍都呼叫（含不在戰鬥中、提早返回的那幾拍）：離開戰鬥後下一段戰鬥的「讓開」再記一次。</summary>
    internal void NoteCombat(bool inCombat)
    {
        if (!inCombat)
            _yieldLogged = false;
    }

    /// <summary>回 true＝這一拍不要自己選目標。<paramref name="log"/> 非 null 時寫一行 Information（失敗只第一次、讓開每段戰鬥一次）。</summary>
    internal bool ShouldYield(long nowMs, Func<bool?> query, out string? log)
    {
        log = null;
        if (nowMs < _retryAtMs)
            return false;
        var owned = query();
        if (owned == null)
        {
            _retryAtMs = nowMs + RetryMs;
            if (!_failureLogged)
            {
                _failureLogged = true;
                log = "AutoDuty 沒載入或版本沒有 AutoDuty.SquadronTargetingOwned：照舊自動選目標";
            }
            return false;
        }
        if (owned != true)
            return false;
        if (!_yieldLogged)
        {
            _yieldLogged = true;
            log = "冒險者小隊小怪路段由 AutoDuty 決定目標（先下號令讓坦克接手）：沒有目標時不自動選，讓給 AutoDuty";
        }
        return true;
    }
}

using System;
using Dalamud.Game.ClientState.Conditions;

namespace TCToolbox.Core;

/// <summary>
/// 「現在可以讓一個<b>無人值守</b>的例行工作動手嗎」的共用閘門。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 <b>這道閘門是給「自己會動」的路徑用的，不是給使用者按下按鈕的路徑用的。</b>
/// 使用者親手按按鈕時，他看得到自己在做什麼；擋下來只會變成「按了沒反應」。
/// 反過來，一個每隔幾十秒自己醒過來一次的迴圈必須先確定沒有插隊到別人頭上，
/// 因為出事的時候使用者根本不知道是誰動的手。
/// </para>
/// <para>
/// 🔑 <b>失敗方向是「不做」。</b>任何一項查不出來（IPC 打不通、還沒登入）一律當成
/// 「現在不是好時機」。漏做一輪的代價是等下一個週期；做錯一輪的代價可能是不可回復的。
/// </para>
/// <para>
/// 📌 <b>「別的外掛正在移動」也算忙。</b>我們自己不走位，但別的外掛正在把角色帶去某處時，
/// 站在原地開一連串的互動選單會把它的流程打斷（互動會鎖住角色、選單會吃掉按鍵）。
/// 判準與呼叫端顯示用的那一份是<b>同一個</b>（<see cref="ExternalNav.TryGetActiveMover"/>），
/// 兩份各寫一次的話遲早會分岔。
/// </para>
/// <para>
/// ⚠️ 只在框架執行緒上呼叫（<c>Svc.Condition</c> 與 IPC 都是）。
/// </para>
/// </remarks>
internal static class AutomationGate
{
    /// <summary>全艦隊急停之後，所有共用這道閘門的無人值守迴圈要靜多久。</summary>
    /// <remarks>
    /// 🔴 <b>光靠 <see cref="NavStop.IsEnforcing"/> 那三秒是不夠的。</b>那三秒只涵蓋
    /// 「確保 vnavmesh 真的停下來」，窗口一關，一個到期的無人值守迴圈下一幀就會自己再開一輪
    /// ——使用者剛按下急停，角色又動了起來，而且看起來像急停沒有生效。
    /// <para>
    /// 📌 60 秒是刻意訂得比園圃重跑的預設間隔（60 秒）不短：急停的語意是「先停下來，我要接手」，
    /// 至少要留給使用者一個週期的時間去做他要做的事。
    /// </para>
    /// </remarks>
    public const int EmergencyStopCooldownSeconds = 60;

    /// <summary>急停冷卻到什麼時候為止（UTC）。<c>MinValue</c>＝沒有冷卻中。</summary>
    private static DateTime emergencyStopUntil = DateTime.MinValue;

    /// <summary>
    /// 通知這道閘門「剛剛執行過一次急停」，開始冷卻。
    /// </summary>
    /// <remarks>⚠️ 只在主執行緒呼叫（急停本身走指令處理常式／熱鍵，都是主執行緒）。</remarks>
    public static void NotifyEmergencyStop()
    {
        emergencyStopUntil = DateTime.UtcNow.AddSeconds(EmergencyStopCooldownSeconds);

        // 🔴 Information 級：「急停之後我的自動流程到底停了多久」出事後只能從記錄回推。
        Svc.Log.Information(
            $"[AutomationGate] 收到全艦隊急停：本外掛的無人值守例行工作暫停 {EmergencyStopCooldownSeconds} 秒。");
    }

    /// <summary>
    /// 現在有沒有理由不要動手。
    /// </summary>
    /// <param name="reason">
    /// 一句話的理由（給記錄與 tooltip 用）。回 <see langword="false"/> 時是空字串。
    /// </param>
    /// <returns><see langword="true"/>＝現在<b>不</b>該動手。</returns>
    public static bool TryGetBusyReason(out string reason)
    {
        reason = string.Empty;

        // 🔴 未登入時 Svc.Condition 的內容是上一次登入留下來的殘值，判它沒有意義。
        if (!Svc.ClientState.IsLoggedIn || Svc.Objects.LocalPlayer == null)
        {
            reason = "還沒登入";
            return true;
        }

        // 🔴 急停冷卻排在所有遊戲狀態之前：使用者剛剛明確要求「全部停下」，
        //    這段期間不管遊戲狀態多空閒都不該有東西自己醒過來。
        if (emergencyStopUntil != DateTime.MinValue)
        {
            var remaining = emergencyStopUntil - DateTime.UtcNow;
            if (remaining > TimeSpan.Zero)
            {
                reason = $"剛執行過全艦隊急停（{remaining.TotalSeconds:F0} 秒後恢復）";
                return true;
            }

            emergencyStopUntil = DateTime.MinValue;
        }

        if (Svc.Condition[ConditionFlag.BetweenAreas] || Svc.Condition[ConditionFlag.BetweenAreas51])
        {
            reason = "正在讀取區域";
            return true;
        }

        if (Svc.Condition[ConditionFlag.LoggingOut])
        {
            reason = "正在登出";
            return true;
        }

        // ⚠️ WatchingCutscene78 與 WatchingCutscene 是兩個不同的旗標，過場的種類不一樣，
        //    只判其中一個會讓另一種過場整個漏掉。
        if (Svc.Condition[ConditionFlag.WatchingCutscene] ||
            Svc.Condition[ConditionFlag.WatchingCutscene78] ||
            Svc.Condition[ConditionFlag.OccupiedInCutSceneEvent])
        {
            reason = "正在播放過場動畫";
            return true;
        }

        if (Svc.Condition[ConditionFlag.InCombat])
        {
            reason = "戰鬥中";
            return true;
        }

        if (Svc.Condition[ConditionFlag.Crafting] || Svc.Condition[ConditionFlag.PreparingToCraft])
        {
            reason = "製作中";
            return true;
        }

        if (Svc.Condition[ConditionFlag.Gathering] || Svc.Condition[ConditionFlag.Fishing])
        {
            reason = "採集中";
            return true;
        }

        if (Svc.Condition[ConditionFlag.BoundByDuty])
        {
            reason = "副本中";
            return true;
        }

        if (Svc.Condition[ConditionFlag.Unconscious])
        {
            reason = "已倒地";
            return true;
        }

        if (Svc.Condition[ConditionFlag.Casting] || Svc.Condition[ConditionFlag.Jumping])
        {
            reason = "正在施放技能或跳躍中";
            return true;
        }

        // 📌 騎乘中大部分的物件互動會被遊戲拒絕（而且是靜默的）。與其排一輪注定整批跳過的
        //    工作、把「跳過 24 格」寫進記錄，不如當成「現在不是好時機」等下一輪。
        if (Svc.Condition[ConditionFlag.Mounted] || Svc.Condition[ConditionFlag.InFlight] ||
            Svc.Condition[ConditionFlag.Diving])
        {
            reason = "騎乘／飛行／潛水中";
            return true;
        }

        if (Svc.Condition[ConditionFlag.Occupied] || Svc.Condition[ConditionFlag.Occupied30] ||
            Svc.Condition[ConditionFlag.Occupied33] || Svc.Condition[ConditionFlag.Occupied38] ||
            Svc.Condition[ConditionFlag.Occupied39] || Svc.Condition[ConditionFlag.OccupiedInEvent] ||
            Svc.Condition[ConditionFlag.OccupiedInQuestEvent] ||
            Svc.Condition[ConditionFlag.OccupiedSummoningBell])
        {
            reason = "正在進行中的事件裡";
            return true;
        }

        // 🔴 本外掛自己發起的移動正在被強制停下來的那幾秒也算忙：那代表有人剛按了停止，
        //    這時候自己開一輪新的工作等於違背使用者剛剛下的指令。
        if (NavStop.IsEnforcing)
        {
            reason = "正在停止移動";
            return true;
        }

        if (ExternalNav.TryGetActiveMover(out var mover))
        {
            reason = $"{mover} 正在移動";
            return true;
        }

        return false;
    }

    /// <summary>現在可以動手嗎。理由不需要時用這支。</summary>
    public static bool IsFree() => !TryGetBusyReason(out _);
}

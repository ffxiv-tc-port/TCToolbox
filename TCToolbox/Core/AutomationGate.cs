using System;
using Dalamud.Game.ClientState.Conditions;

namespace TCToolbox.Core;

/// <summary>
/// 「現在可以讓一個<b>無人值守</b>的例行工作動手嗎」的共用閘門。
/// </summary>
/// <remarks>
/// 🔑 <b>失敗方向是「不做」。</b>任何一項查不出來（IPC 打不通、還沒登入）一律當成
/// 「現在不是好時機」。漏做一輪的代價是等下一個週期；做錯一輪的代價可能是不可回復的。
/// ⚠️ 只在框架執行緒上呼叫（<c>Svc.Condition</c> 與 IPC 都是）。
/// </remarks>
internal static class AutomationGate
{
    /// <summary>冷卻秒數可以設定到的上限。</summary>
    /// <remarks>
    /// ⚠️ 存在的理由不是「有人會想設十分鐘」，而是<b>設定檔是純文字、使用者手改得到</b>：
    /// 沒有這道夾制的話，一個誤植的大數字會讓所有無人值守迴圈看起來像是永遠壞掉了，
    /// 而且唯一的徵兆是一行沒人會去看的記錄。
    /// </remarks>
    public const int MaxCooldownSeconds = 600;

    /// <summary>全艦隊急停之後，所有共用這道閘門的無人值守迴圈要靜多久（秒）。</summary>
    /// <remarks>
    /// 🔴 <b>0＝不冷卻</b>，是合法設定：急停照樣把正在跑的批次停掉，只是下一個到期的迴圈
    /// 可以立刻重開。負數與超過 <see cref="MaxCooldownSeconds"/> 的值在這裡被夾回範圍內。
    /// </remarks>
    public static int EmergencyStopCooldownSeconds
    {
        get
        {
            var seconds = Plugin.Instance.Config.FleetEmergencyStop.EmergencyStopCooldownSeconds;
            return Math.Clamp(seconds, 0, MaxCooldownSeconds);
        }
    }

    /// <summary>急停冷卻到什麼時候為止（UTC）。<c>MinValue</c>＝沒有冷卻中。</summary>
    private static DateTime emergencyStopUntil = DateTime.MinValue;

    /// <summary>
    /// 通知這道閘門「剛剛執行過一次急停」，開始冷卻。
    /// </summary>
    /// <remarks>⚠️ 只在主執行緒呼叫（急停本身走指令處理常式／熱鍵，都是主執行緒）。</remarks>
    public static void NotifyEmergencyStop()
    {
        var seconds = EmergencyStopCooldownSeconds;

        // 🔴 0 秒要走「把冷卻清掉」而不是「設一個立刻到期的時間點」：後者能動，
        //    但會讓 TryGetBusyReason 在下一次判斷時多繞一圈重設狀態，而且訊息會說「暫停 0 秒」。
        emergencyStopUntil = seconds > 0 ? DateTime.UtcNow.AddSeconds(seconds) : DateTime.MinValue;

        // 🔴 Information 級：「急停之後我的自動流程到底停了多久」出事後只能從記錄回推。
        //    🔑 冷卻關掉時也要寫一行——否則「急停之後馬上又自己動起來」會查不到原因。
        Svc.Log.Information(
            seconds > 0
                ? $"[AutomationGate] 收到全艦隊急停：本外掛的無人值守例行工作暫停 {seconds} 秒。"
                : "[AutomationGate] 收到全艦隊急停：冷卻秒數設定為 0，無人值守例行工作不暫停。");
    }

    /// <summary>
    /// <b>只</b>問「全艦隊急停還在冷卻中嗎」，完全不看遊戲狀態。
    /// </summary>
    /// <param name="reason">
    /// 一句話的理由（給記錄用）。回 <see langword="false"/> 時是空字串。
    /// </param>
    /// <returns><see langword="true"/>＝急停冷卻中，現在<b>不</b>該動手。</returns>
    public static bool TryGetEmergencyStopReason(out string reason)
    {
        reason = string.Empty;

        if (emergencyStopUntil == DateTime.MinValue) return false;

        var remaining = emergencyStopUntil - DateTime.UtcNow;
        if (remaining > TimeSpan.Zero)
        {
            reason = $"剛執行過全艦隊急停（{remaining.TotalSeconds:F0} 秒後恢復）";
            return true;
        }

        emergencyStopUntil = DateTime.MinValue;
        return false;
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
        if (TryGetEmergencyStopReason(out var stopReason))
        {
            reason = stopReason;
            return true;
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

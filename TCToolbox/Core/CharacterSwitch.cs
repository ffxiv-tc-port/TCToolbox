using Dalamud.Game.ClientState.Conditions;

namespace TCToolbox.Core;

/// <summary>
/// 請 AutoRetainer 切換到指定角色（＝登出再登入）。每一次都來自使用者的指令或點擊。
/// </summary>
/// <remarks>
/// 🔴🔴 <b>絕不呼叫空參數的 <c>/li</c>。</b>不在原伺服器時就只是不給切，並把原因說出來。
/// ⚠️ 會打 AutoRetainer 的 IPC：只在框架執行緒上呼叫。
/// </remarks>
internal static class CharacterSwitch
{
    /// <summary>請 AutoRetainer 切換到「名稱@伺服器」；不能切時把原因印到聊天欄。</summary>
    /// <param name="charaNameWithWorld">必須與 AutoRetainer 自己的紀錄完全相符。</param>
    /// <param name="requestedBy">寫進記錄的模組名。</param>
    public static void Request(string charaNameWithWorld, string requestedBy)
    {
        if (GetBlockedReason() is { } reason)
        {
            Svc.Chat.PrintError($"[TC Toolbox] 無法切換角色：{reason}");
            return;
        }

        if (!AutoRetainerIpc.TryRelog(charaNameWithWorld, out var accepted))
        {
            Svc.Chat.PrintError("[TC Toolbox] 呼叫 AutoRetainer 切換角色失敗。");
            return;
        }

        if (!accepted)
        {
            // AutoRetainer 拿這個字串比對不到自己的角色清單時就是這裡。
            Svc.Chat.PrintError(
                $"[TC Toolbox] AutoRetainer 沒有接受切換到「{charaNameWithWorld}」" +
                "（名稱不符，或它目前不允許自動登入）。");
            return;
        }

        // 使用者回報用的定錨點：切換角色會登出，出事時這一行是唯一能證明「是他自己要求的」的證據。
        Svc.Log.Information($"[{requestedBy}] 使用者要求切換角色 → {charaNameWithWorld}");
        Svc.Chat.Print($"[TC Toolbox] 正在切換到「{charaNameWithWorld}」…");
    }

    /// <summary>
    /// 現在不能切換角色的原因；<c>null</c>＝可以。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>不在原伺服器時就是不給切</b>，而不是像上游那樣去呼叫 <c>/li</c> 把角色傳回去。
    /// 那個指令空參數是跨世界傳送，不是我們該替使用者做的決定。
    /// </remarks>
    public static string? GetBlockedReason()
    {
        if (Svc.Objects.LocalPlayer == null)
            return "目前不在遊戲中。";

        var player = Svc.Objects.LocalPlayer;
        if (player.HomeWorld.RowId != player.CurrentWorld.RowId)
            return "目前在其他伺服器（旅行中），請先自行回到原伺服器。";

        if (Svc.Condition[ConditionFlag.InCombat])
            return "戰鬥中。";

        if (Svc.Condition[ConditionFlag.BoundByDuty] || Svc.Condition[ConditionFlag.BoundByDuty56] ||
            Svc.Condition[ConditionFlag.BoundByDuty95] || Svc.Condition[ConditionFlag.InDutyQueue])
            return "正在副本中或排隊中。";

        if (Svc.Condition[ConditionFlag.WatchingCutscene] ||
            Svc.Condition[ConditionFlag.OccupiedInCutSceneEvent])
            return "正在播放過場動畫。";

        if (Svc.Condition[ConditionFlag.Occupied] || Svc.Condition[ConditionFlag.Occupied30] ||
            Svc.Condition[ConditionFlag.Occupied33] || Svc.Condition[ConditionFlag.Occupied38] ||
            Svc.Condition[ConditionFlag.Occupied39] || Svc.Condition[ConditionFlag.OccupiedInEvent] ||
            Svc.Condition[ConditionFlag.OccupiedSummoningBell] ||
            Svc.Condition[ConditionFlag.OccupiedInQuestEvent])
            return "正在進行中的事件裡。";

        if (AutoRetainerIpc.IsBusy())
            return "AutoRetainer 正在忙。";

        if (!AutoRetainerIpc.CanAutoLogin())
            return "AutoRetainer 目前不允許自動登入（請確認它的自動登入設定）。";

        return null;
    }
}

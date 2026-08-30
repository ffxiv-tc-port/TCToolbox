using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 野外陸行鳥剩餘時間不足時自動餵食基沙爾野菜（Item 4868）。
/// 機制：Framework 輪詢 Buddy.CompanionInfo.TimeLeft，低於門檻時 UseAction(Item)，零 hook。
/// DR 原版閉源，依描述自寫。
/// </summary>
public sealed unsafe class AutoGysahlGreens : TcModule
{
    public override string InternalName => "AutoGysahlGreens";
    public override string DisplayName => "自動餵食陸行鳥";
    public override string Description => $"野外召喚中的陸行鳥剩餘時間低於門檻（預設 5 分鐘）時，自動使用{itemName}延長時間。戰鬥、騎乘、副本、過場中不動作。";

    public override ModuleCategory Category => ModuleCategory.Company;

    private const uint GysahlGreensItemId = 4868;

    private static string itemName = "基沙爾野菜";

    private AutoGysahlGreensConfig Config => Plugin.Instance.Config.GysahlGreens;

    public override bool HasConfigUI => true;

    protected override void OnEnable()
    {
        // 遊戲字串走 Lumina sheet（台服自帶繁中）
        var row = Svc.Data.GetExcelSheet<Item>().GetRowOrDefault(GysahlGreensItemId);
        if (row != null)
            itemName = row.Value.Name.ExtractText();

        Svc.Framework.Update += OnUpdate;
        Svc.Log.Information($"[AutoGysahlGreens] 模組啟用：門檻 {Config.ThresholdMinutes} 分鐘");
    }

    protected override void OnDisable()
    {
        Svc.Framework.Update -= OnUpdate;
    }

    private void OnUpdate(IFramework framework)
    {
        if (!Throttle.Pass("AutoGysahlGreens-Poll", 2_000)) return;

        if (Svc.Objects.LocalPlayer == null) return;

        var blocked = Svc.Condition[ConditionFlag.InCombat] ||
            Svc.Condition[ConditionFlag.Mounted] ||
            Svc.Condition[ConditionFlag.Casting] ||
            Svc.Condition[ConditionFlag.BetweenAreas] ||
            Svc.Condition[ConditionFlag.BetweenAreas51] ||
            Svc.Condition[ConditionFlag.BoundByDuty] ||
            Svc.Condition[ConditionFlag.OccupiedInQuestEvent] ||
            Svc.Condition[ConditionFlag.OccupiedInCutSceneEvent] ||
            Svc.Condition[ConditionFlag.WatchingCutscene];

        var uiState = UIState.Instance();

        // 診斷：TimeLeft 是這條鏈唯一的原生讀值，讀到 0 或垃圾值都會走靜默返回 ——
        // 印出來，「沒效果」時 log 才有東西可查。實機回報「沒效果」（2026-08-18）
        // 就是因為整條鏈沒有任何可見輸出，無從分辨死在哪段。
        // 🔴 2026-08-30：但這是**純輪詢**，跟有沒有事情發生無關，實機 08-23~30 印了 1,004 次。
        // ⇒ 週期性那筆降 Debug；Information 只留給「真的該餵了卻沒餵成」的現場（見下面那筆），
        //   因為使用者跑 LogLevel 2，會被回報的只有那種時刻。餵食結果本身維持 Information／聊天訊息。
        var timeLeftNow = uiState != null ? uiState->Buddy.CompanionInfo.TimeLeft : 0f;
        var thresholdSeconds = Config.ThresholdMinutes * 60f;

        if (Throttle.Pass("AutoGysahlGreens-Diag", 120_000))
        {
            var tl = uiState != null ? $"{timeLeftNow:F1}s" : "uiState=null";
            Svc.Log.Debug($"[AutoGysahlGreens] 狀態：TimeLeft={tl} 門檻={Config.ThresholdMinutes * 60}s 條件擋下={blocked}");
        }

        // 已經進入該餵的區間、卻被條件擋下 —— 這就是「開了卻沒作用」的可回報現場，維持 Information。
        if (blocked && timeLeftNow > 0 && timeLeftNow <= thresholdSeconds &&
            Throttle.Pass("AutoGysahlGreens-Blocked", 60_000))
        {
            Svc.Log.Information(
                $"[AutoGysahlGreens] 已達門檻但被條件擋下，這次不餵：TimeLeft={timeLeftNow:F1}s 門檻={thresholdSeconds:F0}s");
        }

        if (blocked) return;
        if (uiState == null) return;

        var timeLeft = uiState->Buddy.CompanionInfo.TimeLeft;
        if (timeLeft <= 0 || timeLeft > Config.ThresholdMinutes * 60f) return;

        var inventory = InventoryManager.Instance();
        if (inventory == null) return;

        if (inventory->GetInventoryItemCount(GysahlGreensItemId) <= 0)
        {
            if (Throttle.Pass("AutoGysahlGreens-NoItem", 300_000))
                Svc.Chat.Print($"[TC Toolbox] 陸行鳥剩餘時間不足，但背包裡沒有{itemName}，無法自動餵食。");
            return;
        }

        // 使用嘗試本身另設節流，避免使用失敗時每 2 秒重複嘗試造成刷屏
        if (!Throttle.Pass("AutoGysahlGreens-Use", 10_000)) return;

        if (ActionManager.Instance()->UseAction(ActionType.Item, GysahlGreensItemId, extraParam: 65535))
        {
            if (Config.NotifyOnFeed)
                Svc.Chat.Print($"[TC Toolbox] 陸行鳥剩餘時間不足，已自動餵食{itemName}。");
        }
        else
        {
            Svc.Log.Information($"[AutoGysahlGreens] UseAction 回傳 false：遊戲拒絕使用道具（剩餘 {timeLeft:F0} 秒）。");
        }
    }

    public override void DrawConfig()
    {
        ImGui.SetNextItemWidth(160f);
        var threshold = Config.ThresholdMinutes;
        if (ImGui.SliderInt("剩餘時間門檻（分鐘）", ref threshold, 1, 25))
        {
            Config.ThresholdMinutes = threshold;
            Plugin.Instance.Config.Save();
        }

        var notify = Config.NotifyOnFeed;
        if (ImGui.Checkbox("餵食後顯示聊天訊息", ref notify))
        {
            Config.NotifyOnFeed = notify;
            Plugin.Instance.Config.Save();
        }
    }
}

using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 沒有選中目標時自動選最近的敵人；可選「迷失者／迷失少女優先」（即使已選中其他目標也搶過去）。
/// 機制：純 ObjectTable 枚舉 + ITargetManager 設定目標，零 hook、不寫記憶體。
/// 名稱一律走 Lumina BNpcName 表（比對 NameId row 而非硬編中文字串）。
/// DR 原版閉源，依其設定檔形狀（MarkerTrack / PrioritizeForlorn）與描述自寫。
/// </summary>
public sealed class AutoRetarget : TcModule
{
    public override string InternalName => "AutoRetarget";
    public override string DisplayName => "自動選中目標";

    public override string Description =>
        $"目前沒有選中目標時，自動選中範圍內最近的敵人。另可開啟「{ForlornDisplayName}優先」——" +
        "這兩種 FATE 稀有敵出現時即使已選中其他目標也會自動切過去。";

    public override bool HasConfigUI => true;

    /// <summary>迷失少女 / 迷失者 的 BNpcName row（已對台服 7.20 EXD dump 驗證）。</summary>
    private const uint ForlornMaidenNameId = 6737;
    private const uint ForlornNameId = 6738;

    private static string forlornDisplayName = "迷失者";

    private static string ForlornDisplayName => forlornDisplayName;

    private AutoRetargetConfig Config => Plugin.Instance.Config.Retarget;

    protected override void OnEnable()
    {
        // 遊戲字串走 Lumina sheet（台服自帶繁中）
        var sheet = Svc.Data.GetExcelSheet<BNpcName>();
        var maiden = sheet.GetRowOrDefault(ForlornMaidenNameId)?.Singular.ExtractText();
        var forlorn = sheet.GetRowOrDefault(ForlornNameId)?.Singular.ExtractText();
        if (!string.IsNullOrEmpty(forlorn) && !string.IsNullOrEmpty(maiden))
            forlornDisplayName = $"{forlorn}／{maiden}";

        Svc.Framework.Update += OnUpdate;
    }

    protected override void OnDisable()
    {
        Svc.Framework.Update -= OnUpdate;
    }

    private void OnUpdate(IFramework framework)
    {
        if (!Throttle.Pass("AutoRetarget-Poll", Math.Max(100, Config.PollIntervalMs))) return;

        var localPlayer = Svc.Objects.LocalPlayer;
        if (localPlayer == null || localPlayer.IsDead) return;

        if (Svc.Condition[ConditionFlag.BetweenAreas] ||
            Svc.Condition[ConditionFlag.BetweenAreas51] ||
            Svc.Condition[ConditionFlag.OccupiedInCutSceneEvent] ||
            Svc.Condition[ConditionFlag.WatchingCutscene] ||
            Svc.Condition[ConditionFlag.OccupiedInQuestEvent] ||
            Svc.Condition[ConditionFlag.Occupied33]) return;

        if (Config.OnlyInCombat && !Svc.Condition[ConditionFlag.InCombat]) return;

        var current = Svc.Targets.Target;

        // 迷失者優先：即使已經有目標也搶過去
        if (Config.PrioritizeForlorn)
        {
            var forlorn = FindNearest(localPlayer.Position, onlyForlorn: true);
            if (forlorn != null)
            {
                if (current == null || current.GameObjectId != forlorn.GameObjectId)
                    Svc.Targets.Target = forlorn;
                return;
            }
        }

        if (current != null && current.IsTargetable) return;

        var next = FindNearest(localPlayer.Position, onlyForlorn: false);
        if (next != null)
            Svc.Targets.Target = next;
    }

    private IGameObject? FindNearest(Vector3 origin, bool onlyForlorn)
    {
        IGameObject? best = null;
        var bestDistanceSquared = float.MaxValue;
        var maxDistanceSquared = Config.MaxDistance * Config.MaxDistance;

        foreach (var obj in Svc.Objects)
        {
            if (obj.ObjectKind != ObjectKind.BattleNpc) continue;
            if (obj is not IBattleNpc npc) continue;
            if (npc.BattleNpcKind != BattleNpcSubKind.Enemy) continue;
            if (!npc.IsTargetable || npc.IsDead || npc.CurrentHp == 0) continue;

            var isForlorn = npc.NameId is ForlornMaidenNameId or ForlornNameId;
            if (onlyForlorn && !isForlorn) continue;

            // 只挑敵對的（避免選到其他玩家的寵物或中立生物）
            if (!isForlorn && !npc.StatusFlags.HasFlag(Dalamud.Game.ClientState.Objects.Enums.StatusFlags.Hostile))
                continue;

            var distanceSquared = Vector3.DistanceSquared(origin, npc.Position);
            if (distanceSquared > maxDistanceSquared) continue;
            if (distanceSquared >= bestDistanceSquared) continue;

            bestDistanceSquared = distanceSquared;
            best = npc;
        }

        return best;
    }

    public override void DrawConfig()
    {
        ImGui.SetNextItemWidth(180f);
        var distance = Config.MaxDistance;
        if (ImGui.SliderFloat("搜尋距離（公尺）", ref distance, 5f, 55f, "%.0f"))
        {
            Config.MaxDistance = distance;
            Plugin.Instance.Config.Save();
        }

        ImGui.SetNextItemWidth(180f);
        var interval = Config.PollIntervalMs;
        if (ImGui.SliderInt("檢查間隔（毫秒）", ref interval, 100, 2_000))
        {
            Config.PollIntervalMs = interval;
            Plugin.Instance.Config.Save();
        }

        var forlorn = Config.PrioritizeForlorn;
        if (ImGui.Checkbox($"優先選中{ForlornDisplayName}（即使已選中其他目標）", ref forlorn))
        {
            Config.PrioritizeForlorn = forlorn;
            Plugin.Instance.Config.Save();
        }

        var onlyInCombat = Config.OnlyInCombat;
        if (ImGui.Checkbox("只在戰鬥中生效", ref onlyInCombat))
        {
            Config.OnlyInCombat = onlyInCombat;
            Plugin.Instance.Config.Save();
        }

        ImGui.TextDisabled("DR 原版的「標點光柱追蹤」未移植（原版預設也是關閉）。");
    }
}

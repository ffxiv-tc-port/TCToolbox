using System;
using System.Globalization;
using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 周邊敵人：資訊列顯示半徑內可選取的敵人數量，清單照距離排序（等級／HP／距離／是否以我為目標），
/// 點一列＝選取為目標。只在點選時選取，不自動選取。
/// </summary>
/// <remarks>
/// 判準沿用 <see cref="AutoRetarget"/>：BattleNpc 的 Enemy 子類別、可選取、沒死、HP &gt; 0、敵對旗標。
/// 距離扣掉敵人的碰撞半徑（接近技能射程的算法），半徑篩選也用這個距離。
/// </remarks>
public sealed class NearbyEnemies : NearbyObjectListModule
{
    public override string InternalName => "NearbyEnemies";
    public override string DisplayName => "周邊敵人";

    public override string Description =>
        "在伺服器資訊列顯示附近可選取的敵人數量，滑鼠移上顯示最近幾隻與以你為目標的數量，" +
        "點擊開啟照距離排序的清單視窗（等級／HP／距離／是否以你為目標），點清單上的一列即可選取為目標。" +
        "只在你點選時才選取，不會自動選取。";

    public override ModuleCategory Category => ModuleCategory.Combat;

    private const byte KindBattleNpc = 2;
    private const int StatsDelayMs = 5000;

    private static readonly Vector4 ColorTargetsMe = new(1f, 0.55f, 0.3f, 1f);
    private static readonly string?[] PercentTexts = new string?[101];

    private static readonly NearbyColumn[] ColumnSet =
    [
        new("名稱", 0f),
        new("Lv", 36f),
        new("HP", 48f),
        new("距離", 60f, "到敵人碰撞圈邊緣的距離：角色中心到敵人中心的直線距離，減去敵人的碰撞半徑。\n和技能射程的算法相近；半徑篩選也用這個距離。"),
        new("狀態", 84f, "以我為目標：這隻敵人目前的目標是你。\n戰鬥中：這隻敵人正在戰鬥。"),
    ];

    private int targetingMe;
    private ushort statsTerritory;
    private long statsSince;
    private bool statsLogged = true;

    private NearbyEnemiesConfig Config => Plugin.Instance.Config.NearbyEnemies;

    protected override string DtrTitle => "TC Toolbox 周邊敵人";
    protected override string WindowId => "###TCToolboxNearbyEnemies";
    protected override string PollKey => "NearbyEnemies-Poll";
    protected override int PollIntervalMs => 250;
    protected override string Noun => "敵人";
    protected override string CountUnit => "隻";
    protected override ref float MaxDistanceField => ref Config.MaxDistance;
    protected override ref bool HideInCombatField => ref Config.HideInCombat;
    protected override NearbyColumn[] Columns => ColumnSet;
    protected override int TooltipSummaryKey => targetingMe;

    protected override void OnEnable()
    {
        targetingMe = 0;
        statsTerritory = 0;
        statsLogged = true;
        base.OnEnable();
    }

    protected override bool KindWanted(byte kind) => kind == KindBattleNpc;

    protected override float DistanceTo(IGameObject obj, Vector3 origin) =>
        MathF.Max(0f, Vector3.Distance(origin, obj.Position) - obj.HitboxRadius);

    protected override bool Accept(IGameObject obj, byte kind)
    {
        if (obj is not IBattleNpc npc) return false;

        var sub = npc.BattleNpcKind;
        var isEnemy = sub == BattleNpcSubKind.Enemy;
        if (!isEnemy && !(Config.IncludeParts && sub == BattleNpcSubKind.BattleNpcPart)) return false;
        if (!Config.IncludeNonHostile && (npc.StatusFlags & StatusFlags.Hostile) == 0) return false;
        if (npc.IsDead) return false;

        // 部位的 HP 欄位語意沒驗過，只對本體套用 HP > 0。
        if (isEnemy && npc.CurrentHp == 0) return false;
        return npc.IsTargetable;
    }

    protected override void Fill(NearbyRow row, IGameObject obj, ulong localId)
    {
        if (obj is not IBattleNpc npc) return;

        row.Level = npc.Level;
        row.LevelText = npc.Level == 0 ? "?" : IntText(npc.Level);
        row.HpText = HpText(npc.CurrentHp, npc.MaxHp);
        row.TargetsMe = npc.TargetObjectId == localId;
        row.InCombat = (npc.StatusFlags & StatusFlags.InCombat) != 0;
    }

    /// <summary>無條件進位：還活著就不會顯示 0%。最大 HP 讀不到畫「?」。</summary>
    private static string HpText(uint hp, uint max)
    {
        if (max == 0) return "?";

        var percent = (int)Math.Min(100UL, ((ulong)hp * 100UL + max - 1) / max);
        return PercentTexts[percent] ??= percent.ToString(CultureInfo.InvariantCulture) + "%";
    }

    protected override void AfterPoll()
    {
        // 索引走訪：對介面 foreach 會把列舉器裝箱，每輪多一次配置。
        var rows = Rows;
        var count = 0;
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].TargetsMe) count++;
        }

        targetingMe = count;
        MaybeLogZoneStats();
    }

    /// <summary>
    /// 每換一區寫一行本區戰鬥 NPC 的子類別／敵對旗標計數（Information，使用者回報用）。
    /// 子類別值與敵對旗標在台服的語意只能靠實機確認，清單「靜默變空」時這行能分辨是哪一條篩掉的。
    /// </summary>
    private void MaybeLogZoneStats()
    {
        var territory = Svc.ClientState.TerritoryType;
        var now = Environment.TickCount64;
        if (territory != statsTerritory)
        {
            statsTerritory = territory;
            statsSince = now;
            statsLogged = false;
        }

        if (statsLogged || now - statsSince < StatsDelayMs) return;

        int total = 0, enemies = 0, hostile = 0, targetable = 0, parts = 0, others = 0;
        foreach (var obj in Svc.Objects)
        {
            if ((byte)obj.ObjectKind != KindBattleNpc || obj is not IBattleNpc npc) continue;

            total++;
            var sub = npc.BattleNpcKind;
            if (sub == BattleNpcSubKind.Enemy)
            {
                enemies++;
                if ((npc.StatusFlags & StatusFlags.Hostile) != 0) hostile++;
                if (npc.IsTargetable) targetable++;
            }
            else if (sub == BattleNpcSubKind.BattleNpcPart)
            {
                parts++;
            }
            else
            {
                others++;
            }
        }

        // 一隻戰鬥 NPC 都沒有（主城等）就不寫，過一段時間再看一次。
        if (total == 0)
        {
            statsSince = now;
            return;
        }

        statsLogged = true;
        Svc.Log.Information(
            $"[{InternalName}] 地圖 {territory}：戰鬥 NPC {total}（敵人子類別 {enemies}，其中敵對 {hostile}、可選取 {targetable}；" +
            $"部位 {parts}；其他 {others}），清單列出 {Rows.Count}（半徑 {MaxDistance:0} 米" +
            $"{(Config.IncludeParts ? "，含部位" : string.Empty)}{(Config.IncludeNonHostile ? "，含非敵對" : string.Empty)}）。");
    }

    protected override SeString BuildDtrText(string countText) =>
        new(new IconPayload(BitmapFontIcon.DamagePhysical), new TextPayload(countText));

    protected override int TooltipKey(NearbyRow row) => row.Level * 2 + (row.TargetsMe ? 1 : 0);

    protected override void AppendTooltipLine(StringBuilder sb, NearbyRow row)
    {
        sb.Append(row.Name).Append(" Lv").Append(row.LevelText);
        if (row.TargetsMe) sb.Append("（以我為目標）");
    }

    protected override void AppendTooltipSummary(StringBuilder sb)
    {
        if (targetingMe > 0)
            sb.Append("以我為目標：").Append(targetingMe).Append(" 隻\n");
    }

    protected override string FirstCellText(NearbyRow row) => row.Name;

    protected override void DrawOtherCells(NearbyRow row)
    {
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(row.LevelText);
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(row.HpText);
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(row.DistanceText);
        ImGui.TableNextColumn();
        if (row.TargetsMe)
            ImGui.TextColored(ColorTargetsMe, "以我為目標");
        else if (row.InCombat)
            ImGui.TextUnformatted("戰鬥中");
    }

    protected override void DrawExtraConfig()
    {
        var c = Config;
        if (ImGui.Checkbox("也列出首領的部位", ref c.IncludeParts))
        {
            Plugin.Instance.Config.Save();
            RequestRepoll();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("有些首領身上有可以個別選取的部位（例如手、頭），勾選後也會列出來。");

        if (ImGui.Checkbox("也列出非敵對的戰鬥 NPC", ref c.IncludeNonHostile))
        {
            Plugin.Instance.Config.Save();
            RequestRepoll();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("不看敵對旗標：衛兵、友方的戰鬥 NPC 也會列出來。\n清單一直是空的、但附近明明有敵人時，可以勾這個試試。");

        ImGui.TextDisabled("距離＝到敵人碰撞圈邊緣（中心距離減去敵人的碰撞半徑）。");
    }
}

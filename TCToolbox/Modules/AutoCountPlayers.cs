using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Plugin.Services;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 周邊玩家數量統計：伺服器資訊列（DTR）顯示人數，點擊開啟清單懸浮窗。
/// 機制：Framework 輪詢 ObjectTable 枚舉，零 hook（不抄 DR 的 InfoProxy hook 部分）。
/// </summary>
public sealed class AutoCountPlayers : TcModule
{
    public override string InternalName => "AutoCountPlayers";
    public override string DisplayName => "周邊玩家統計";
    public override string Description => "在伺服器資訊列顯示周邊玩家數量，滑鼠移上顯示名單，點擊開啟可搜尋的清單視窗（點名單可選取目標）。";

    public override bool HasConfigUI => true;

    private sealed record PlayerInfo(uint EntityId, string Name, string World, string Job, float Distance);

    private readonly List<PlayerInfo> players = [];
    private IDtrBarEntry? dtrEntry;
    private bool windowOpen;
    private string search = string.Empty;

    private AutoCountPlayersConfig Config => Plugin.Instance.Config.CountPlayers;

    protected override void OnEnable()
    {
        dtrEntry = Svc.DtrBar.Get("TC Toolbox 周邊玩家");
        dtrEntry.Shown = true;
        dtrEntry.Text = "周邊玩家: 0";
        dtrEntry.Tooltip = "點擊開啟周邊玩家清單";
        dtrEntry.OnClick = _ => windowOpen = !windowOpen;

        Svc.Framework.Update += OnUpdate;
        Svc.PluginInterface.UiBuilder.Draw += DrawWindow;
    }

    protected override void OnDisable()
    {
        Svc.Framework.Update -= OnUpdate;
        Svc.PluginInterface.UiBuilder.Draw -= DrawWindow;

        dtrEntry?.Remove();
        dtrEntry = null;
        players.Clear();
        windowOpen = false;
    }

    private void OnUpdate(IFramework framework)
    {
        if (dtrEntry == null) return;
        if (!Throttle.Pass("AutoCountPlayers-Poll", 500)) return;

        var localPlayer = Svc.Objects.LocalPlayer;
        if (localPlayer == null)
        {
            players.Clear();
            dtrEntry.Shown = false;
            return;
        }

        if (Config.HideInCombat && Svc.Condition[ConditionFlag.InCombat])
        {
            dtrEntry.Shown = false;
            return;
        }

        dtrEntry.Shown = true;

        players.Clear();
        foreach (var obj in Svc.Objects)
        {
            if (obj is not IPlayerCharacter pc) continue;
            if (pc.EntityId == localPlayer.EntityId) continue;

            players.Add(new PlayerInfo(
                pc.EntityId,
                pc.Name.TextValue,
                pc.HomeWorld.ValueNullable?.Name.ExtractText() ?? string.Empty,
                pc.ClassJob.ValueNullable?.Abbreviation.ExtractText() ?? "?",
                Vector3.Distance(localPlayer.Position, pc.Position)));
        }

        players.Sort((a, b) => a.Distance.CompareTo(b.Distance));

        dtrEntry.Text = $"周邊玩家: {players.Count}";

        if (players.Count == 0)
        {
            dtrEntry.Tooltip = "附近沒有其他玩家";
        }
        else
        {
            var sb = new StringBuilder();
            foreach (var p in players.Take(30))
                sb.AppendLine($"[{p.Job}] {p.Name}{(string.IsNullOrEmpty(p.World) ? "" : $" @ {p.World}")}");
            if (players.Count > 30)
                sb.AppendLine($"…等共 {players.Count} 人");
            sb.Append("點擊開啟周邊玩家清單");
            dtrEntry.Tooltip = sb.ToString();
        }
    }

    private void DrawWindow()
    {
        if (!windowOpen) return;

        ImGui.SetNextWindowSize(new Vector2(420, 340), ImGuiCond.FirstUseEver);
        if (ImGui.Begin($"周邊玩家 ({players.Count})###TCToolboxCountPlayers", ref windowOpen))
        {
            ImGui.SetNextItemWidth(-1f);
            ImGui.InputTextWithHint("##search", "搜尋玩家名稱…", ref search, 64);

            if (ImGui.BeginTable("##playerTable", 4,
                    ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.ScrollY))
            {
                ImGui.TableSetupScrollFreeze(0, 1);
                ImGui.TableSetupColumn("職業", ImGuiTableColumnFlags.WidthFixed, 48f);
                ImGui.TableSetupColumn("名稱", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("伺服器", ImGuiTableColumnFlags.WidthFixed, 80f);
                ImGui.TableSetupColumn("距離", ImGuiTableColumnFlags.WidthFixed, 56f);
                ImGui.TableHeadersRow();

                foreach (var p in players.ToArray())
                {
                    if (!string.IsNullOrWhiteSpace(search) &&
                        !p.Name.Contains(search, System.StringComparison.OrdinalIgnoreCase)) continue;

                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(p.Job);

                    ImGui.TableNextColumn();
                    if (ImGui.Selectable($"{p.Name}##{p.EntityId}", false, ImGuiSelectableFlags.SpanAllColumns))
                    {
                        var obj = Svc.Objects.SearchByEntityId(p.EntityId);
                        if (obj != null)
                            Svc.Targets.Target = obj;
                    }
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("點擊選取為目標");

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(p.World);

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted($"{p.Distance:0.0}m");
                }

                ImGui.EndTable();
            }
        }

        ImGui.End();
    }

    public override void DrawConfig()
    {
        var hide = Config.HideInCombat;
        if (ImGui.Checkbox("戰鬥中隱藏資訊列項目", ref hide))
        {
            Config.HideInCombat = hide;
            Plugin.Instance.Config.Save();
        }
    }
}

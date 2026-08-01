using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 老主顧交易總覽：唯讀顯示各老主顧的滿意度等級、當前等級進度與本週交易次數。
/// 機制：靜態資料讀 Lumina SatisfactionNpc／ENpcResident／Quest 表（台服自帶繁中），
/// 動態狀態讀 ClientStructs SatisfactionSupplyManager 的唯讀欄位——零 hook、零寫入、
/// 零封包，連原生函式都不呼叫（總用量自己加總、解鎖判定用等級欄位，不呼叫
/// GetUsedAllowances／IsQuestComplete）。
/// ⚠️ DR 的 FastCustomDeliveriesInfo 描述寫「顯示本週報酬增加的老主顧」但根本沒實作，
/// 本模組不承接那個承諾——範圍就是誠實的狀態總覽。
/// </summary>
public sealed class CustomDeliveriesOverview : TcModule
{
    public override string InternalName => "CustomDeliveriesOverview";
    public override string DisplayName => "老主顧交易總覽";
    public override string Description => "唯讀總覽視窗：列出各老主顧的滿意度等級、當前等級進度、本週已交易次數與全體共用的週上限。資料只讀不寫，零 hook。";

    public override bool HasConfigUI => true;

    /// <summary>
    /// 每週全體老主顧共用的交易上限。這是 ClientStructs
    /// <c>SatisfactionSupplyManager.GetRemainingAllowances()</c> 原始碼裡的常數（12 - 已用），
    /// 直接取常數自己加總，避免呼叫原生函式。
    /// </summary>
    private const int WeeklyTotalCap = 12;

    /// <summary>滿意度等級上限（遊戲 UI 的五顆心）。</summary>
    private const int MaxRank = 5;

    /// <summary>從 Lumina 表快取下來的靜態資料（動態狀態每幀重讀，不快取）。</summary>
    private sealed record NpcStaticInfo(
        int Index,              // SatisfactionSupplyManager 各陣列的索引（= SatisfactionNpc RowId - 1）
        string Name,            // ENpcResident.Singular（台服繁中）
        byte LevelUnlock,
        byte DeliveriesPerWeek,
        string QuestName,       // 解鎖任務名（可能為空字串）
        ushort[] RankThresholds // SatisfactionNpcParams[rank].SatisfactionRequired，索引＝滿意度等級
    );

    private readonly List<NpcStaticInfo> npcs = [];
    private bool windowOpen;

    protected override void OnEnable()
    {
        npcs.Clear();
        foreach (var row in Svc.Data.GetExcelSheet<SatisfactionNpc>())
        {
            if (row.RowId == 0) continue; // 佔位列

            // ⚠️ 台服 EXD 對未開放內容「有列但名稱是空字串」——名稱取不到就整列跳過，
            // 不顯示空白列。
            var name = row.Npc.ValueNullable?.Singular.ExtractText();
            if (string.IsNullOrWhiteSpace(name)) continue;

            var thresholds = new ushort[row.SatisfactionNpcParams.Count];
            for (var i = 0; i < thresholds.Length; i++)
                thresholds[i] = row.SatisfactionNpcParams[i].SatisfactionRequired;

            npcs.Add(new NpcStaticInfo(
                (int)row.RowId - 1,
                name,
                row.LevelUnlock,
                row.DeliveriesPerWeek,
                row.QuestRequired.ValueNullable?.Name.ExtractText() ?? string.Empty,
                thresholds));
        }

        Svc.PluginInterface.UiBuilder.Draw += DrawWindow;
    }

    protected override void OnDisable()
    {
        Svc.PluginInterface.UiBuilder.Draw -= DrawWindow;
        npcs.Clear();
        windowOpen = false;
    }

    public override void DrawConfig()
    {
        if (ImGui.Button(windowOpen ? "關閉總覽視窗" : "開啟總覽視窗"))
            windowOpen = !windowOpen;
        ImGui.TextDisabled("純唯讀顯示；額度與滿意度由伺服器每週重置，本模組不做任何操作。");
    }

    private void DrawWindow()
    {
        if (!windowOpen) return;

        ImGui.SetNextWindowSize(new Vector2(560, 380), ImGuiCond.FirstUseEver);
        if (ImGui.Begin("老主顧交易總覽###TCToolboxCustomDeliveries", ref windowOpen))
            DrawContent();
        ImGui.End();
    }

    private unsafe void DrawContent()
    {
        if (!Svc.ClientState.IsLoggedIn)
        {
            ImGui.TextDisabled("尚未登入，無法讀取老主顧狀態。");
            return;
        }

        var mgr = SatisfactionSupplyManager.Instance();
        if (mgr == null)
        {
            // 讀不到就顯示占位，絕不解參考可疑指標。
            ImGui.TextDisabled("目前讀不到老主顧狀態（遊戲尚未初始化）。");
            return;
        }

        // 三個固定陣列都是唯讀欄位；span 只在本幀使用、不跨幀保存。
        var satisfaction = mgr->Satisfaction;
        var ranks = mgr->SatisfactionRanks;
        var used = mgr->UsedAllowances;

        var totalUsed = 0;
        foreach (var u in used)
            totalUsed += u;
        var remaining = WeeklyTotalCap - totalUsed;
        if (remaining < 0) remaining = 0;

        ImGui.TextUnformatted($"本週共用額度：已用 {totalUsed}／{WeeklyTotalCap}");
        ImGui.SameLine();
        if (remaining == 0)
            ImGui.TextColored(new Vector4(1f, 0.5f, 0.4f, 1f), "（已用完）");
        else
            ImGui.TextDisabled($"（剩餘 {remaining} 次）");
        ImGui.TextDisabled("額度每週重置；各老主顧另有單獨的每週上限。");
        ImGui.Separator();

        if (!ImGui.BeginTable("##customDeliveries", 4,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.ScrollY))
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("老主顧", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("滿意度", ImGuiTableColumnFlags.WidthFixed, 64f);
        ImGui.TableSetupColumn("等級進度", ImGuiTableColumnFlags.WidthFixed, 130f);
        ImGui.TableSetupColumn("本週交易", ImGuiTableColumnFlags.WidthFixed, 72f);
        ImGui.TableHeadersRow();

        foreach (var npc in npcs)
        {
            ImGui.TableNextRow();

            // 索引超出遊戲端陣列（例如未來改版陣列縮短）就整列顯示占位，不解參考。
            var idx = npc.Index;
            var inBounds = idx >= 0 && idx < ranks.Length && idx < satisfaction.Length && idx < used.Length;
            var rank = inBounds ? ranks[idx] : (byte)0;
            var unlocked = inBounds && rank > 0;

            ImGui.TableNextColumn();
            if (unlocked)
                ImGui.TextUnformatted(npc.Name);
            else
                ImGui.TextDisabled(npc.Name);
            if (ImGui.IsItemHovered())
            {
                var quest = string.IsNullOrEmpty(npc.QuestName) ? string.Empty : $"，完成任務「{npc.QuestName}」";
                ImGui.SetTooltip(unlocked
                                     ? $"解鎖條件：等級 {npc.LevelUnlock}{quest}"
                                     : $"未解鎖。解鎖條件：等級 {npc.LevelUnlock}{quest}");
            }

            ImGui.TableNextColumn();
            if (unlocked)
                ImGui.TextUnformatted($"{rank}／{MaxRank}");
            else
                ImGui.TextDisabled("－");

            ImGui.TableNextColumn();
            if (!unlocked)
            {
                ImGui.TextDisabled("－");
            }
            else
            {
                // 當前等級的進度門檻：SatisfactionNpcParams[等級].SatisfactionRequired。
                // 已滿級（或表裡沒有下一級門檻）就顯示 MAX。
                var threshold = rank < npc.RankThresholds.Length ? npc.RankThresholds[rank] : (ushort)0;
                if (rank >= MaxRank || threshold == 0)
                {
                    ImGui.TextDisabled("已達最高等級");
                }
                else
                {
                    var cur = satisfaction[idx];
                    var frac = cur >= threshold ? 1f : (float)cur / threshold;
                    ImGui.ProgressBar(frac, new Vector2(-1f, 0f), $"{cur}／{threshold}");
                }
            }

            ImGui.TableNextColumn();
            if (!unlocked)
            {
                ImGui.TextDisabled("－");
            }
            else
            {
                var usedN = used[idx];
                if (npc.DeliveriesPerWeek > 0 && usedN >= npc.DeliveriesPerWeek)
                    ImGui.TextColored(new Vector4(1f, 0.5f, 0.4f, 1f), $"{usedN}／{npc.DeliveriesPerWeek}");
                else
                    ImGui.TextUnformatted($"{usedN}／{npc.DeliveriesPerWeek}");
            }
        }

        ImGui.EndTable();
    }
}

using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 老主顧交易總覽：唯讀顯示各老主顧的滿意度等級、當前等級進度與本週交易次數，
/// 並支援右鍵「導航過去」——傳送到該區乙太之光（Lifestream IPC）後接 vnavmesh IPC
/// 直接走到 NPC 座標；兩者缺一律優雅退化（見 <see cref="DrawNavigationContextMenu"/>）。
/// 機制：靜態資料讀 Lumina SatisfactionNpc／ENpcResident／Quest／Level／Aetheryte 表
/// （台服自帶繁中），動態狀態讀 ClientStructs SatisfactionSupplyManager 的唯讀欄位——
/// 主體零 hook、零寫入、零封包，連原生查詢函式都不呼叫（總用量自己加總、解鎖判定用
/// 等級欄位，不呼叫 GetUsedAllowances／IsQuestComplete）。唯一的外部作用只在使用者
/// 右鍵點選導航選單那一刻發生（IPC 呼叫／原生 SetFlagMapMarker），且一律走 IPC、
/// 絕不透過聊天指令（`/li` 空參數＝跨世界傳送，是明確紅線）。
/// ⚠️ DR 的 FastCustomDeliveriesInfo 描述寫「顯示本週報酬增加的老主顧」但根本沒實作，
/// 本模組不承接那個承諾——範圍就是誠實的狀態總覽（外加手動觸發的導航捷徑）。
/// </summary>
public sealed class CustomDeliveriesOverview : TcModule
{
    public override string InternalName => "CustomDeliveriesOverview";
    public override string DisplayName => "老主顧交易總覽";
    public override string Description => "唯讀總覽視窗：列出各老主顧的滿意度等級、當前等級進度、本週已交易次數與全體共用的週上限。資料只讀不寫，零 hook。";

    public override ModuleCategory Category => ModuleCategory.Company;

    public override bool HasConfigUI => true;

    /// <summary>
    /// 每週全體老主顧共用的交易上限。這是 ClientStructs
    /// <c>SatisfactionSupplyManager.GetRemainingAllowances()</c> 原始碼裡的常數（12 - 已用），
    /// 直接取常數自己加總，避免呼叫原生函式。
    /// </summary>
    private const int WeeklyTotalCap = 12;

    /// <summary>滿意度等級上限（遊戲 UI 的五顆心）。</summary>
    private const int MaxRank = 5;

    /// <summary>
    /// NPC 在世界中的落點：座標＋所在區域／地圖直接讀自 Lumina <c>Level</c> 表
    /// （零轉換，SetFlagMapMarker／vnavmesh 都吃世界座標），<c>AetheryteId</c> 是同一
    /// 個區域裡離 NPC 最近的「主乙太之光」（<c>IsAetheryte==true</c>，能用 Teleport
    /// 動作長途傳送的那種，不是只能就近走網路的乙太之光網路點）；0＝該區域找不到。
    /// </summary>
    private readonly record struct NpcLocation(ushort TerritoryType, uint Map, Vector3 WorldPosition, uint AetheryteId);

    /// <summary>從 Lumina 表快取下來的靜態資料（動態狀態每幀重讀，不快取）。</summary>
    private sealed record NpcStaticInfo(
        int Index,              // SatisfactionSupplyManager 各陣列的索引（= SatisfactionNpc RowId - 1）
        string Name,            // ENpcResident.Singular（台服繁中）
        byte LevelUnlock,
        byte DeliveriesPerWeek,
        string QuestName,       // 解鎖任務名（可能為空字串）
        ushort[] RankThresholds, // SatisfactionNpcParams[rank].SatisfactionRequired，索引＝滿意度等級
        NpcLocation? Location    // 查不到座標（Level 表沒有對應列）就是 null
    );

    /// <summary>Level 表單一列的落點資訊（世界座標＋所屬區域／地圖）。</summary>
    private readonly record struct LevelPlacement(Vector3 Position, ushort Territory, uint Map);

    /// <summary>使用者在等待中的導航被判定為手動移動而取消的位移門檻（碼）。</summary>
    private const float ManualMoveCancelDistance = 2f;

    private readonly List<NpcStaticInfo> npcs = [];
    private readonly TaskQueue navQueue = new();
    private bool windowOpen;

    protected override void OnEnable()
    {
        npcs.Clear();

        BuildLevelPlacements(out var npcPlacements, out var aetherytePlacements);
        var mainAetherytesByTerritory = BuildMainAetherytesByTerritory(aetherytePlacements);

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

            NpcLocation? location = null;
            if (npcPlacements.TryGetValue(row.Npc.RowId, out var placement))
            {
                var aetheryteId = 0u;
                if (mainAetherytesByTerritory.TryGetValue(placement.Territory, out var candidates))
                    aetheryteId = PickNearestAetheryte(candidates, placement.Position);
                location = new NpcLocation(placement.Territory, placement.Map, placement.Position, aetheryteId);
            }

            npcs.Add(new NpcStaticInfo(
                (int)row.RowId - 1,
                name,
                row.LevelUnlock,
                row.DeliveriesPerWeek,
                row.QuestRequired.ValueNullable?.Name.ExtractText() ?? string.Empty,
                thresholds,
                location));
        }

        navQueue.OnTimeout = step => Svc.Chat.Print($"[TC Toolbox] 導航逾時，已取消：{step}");

        Svc.PluginInterface.UiBuilder.Draw += DrawWindow;
        Svc.Framework.Update += OnFrameworkUpdate;
    }

    protected override void OnDisable()
    {
        Svc.Framework.Update -= OnFrameworkUpdate;
        Svc.PluginInterface.UiBuilder.Draw -= DrawWindow;
        navQueue.Abort();
        npcs.Clear();
        windowOpen = false;
    }

    private void OnFrameworkUpdate(IFramework framework) => navQueue.Tick();

    /// <summary>
    /// 掃一次 Level 表，把 NPC（Type 8，Object 連到 ENpcBase）與乙太之光（Type 12，
    /// Object 連到 Aetheryte）的世界座標各自建成查找表。只在模組啟用時跑一次。
    /// </summary>
    private static void BuildLevelPlacements(
        out Dictionary<uint, LevelPlacement> npcPlacements,
        out Dictionary<uint, LevelPlacement> aetherytePlacements)
    {
        npcPlacements = [];
        aetherytePlacements = [];

        foreach (var level in Svc.Data.GetExcelSheet<Level>())
        {
            Dictionary<uint, LevelPlacement>? target = level.Type switch
            {
                8 => npcPlacements,        // ENpcBase
                12 => aetherytePlacements, // Aetheryte
                _ => null,
            };
            if (target == null) continue;

            // 同一 NPC／乙太之光可能有多筆 Level 列（重複佈景），只取第一筆命中的。
            target.TryAdd(level.Object.RowId,
                new LevelPlacement(new Vector3(level.X, level.Y, level.Z), (ushort)level.Territory.RowId, level.Map.RowId));
        }
    }

    /// <summary>
    /// 只挑 <c>IsAetheryte==true</c> 的主水晶（能用長途 Teleport 動作傳送的那種，
    /// 不含只能就近走的乙太之光網路點），依區域分組，供「最近的乙太之光」比對用。
    /// </summary>
    private static Dictionary<ushort, List<(uint Id, Vector3? Pos)>> BuildMainAetherytesByTerritory(
        Dictionary<uint, LevelPlacement> aetherytePlacements)
    {
        Dictionary<ushort, List<(uint Id, Vector3? Pos)>> result = [];

        foreach (var ae in Svc.Data.GetExcelSheet<Aetheryte>())
        {
            if (!ae.IsAetheryte) continue;
            if (!ae.Territory.IsValid) continue;

            var territory = (ushort)ae.Territory.RowId;
            var pos = aetherytePlacements.TryGetValue(ae.RowId, out var placement) ? placement.Position : (Vector3?)null;

            if (!result.TryGetValue(territory, out var list))
                result[territory] = list = [];
            list.Add((ae.RowId, pos));
        }

        return result;
    }

    /// <summary>同一區域可能不只一顆主水晶（少數大型開放區域），依 X/Z 平面距離挑最近的。</summary>
    private static uint PickNearestAetheryte(List<(uint Id, Vector3? Pos)> candidates, Vector3 npcPos)
    {
        if (candidates.Count == 0) return 0;

        var best = candidates[0].Id;
        var bestDistSq = float.MaxValue;
        foreach (var (id, pos) in candidates)
        {
            var distSq = pos is { } p
                ? Vector2.DistanceSquared(new Vector2(p.X, p.Z), new Vector2(npcPos.X, npcPos.Z))
                : float.MaxValue; // 座標查不到的候選排最後，仍給個保底值
            if (distSq < bestDistSq)
            {
                bestDistSq = distSq;
                best = id;
            }
        }

        return best;
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
            DrawNavigationContextMenu(npc);

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

    /// <summary>
    /// 右鍵導航選單（<c>BeginPopupContextItem</c> 慣例，掛在 NPC 名稱那格上）。
    /// 主選項「導航過去」依 Lifestream／vnavmesh 的即時可用狀態自動決定實際行為與
    /// 顯示文字；查不到座標的 NPC 兩個選項都灰化顯示「位置未知」。
    /// </summary>
    private void DrawNavigationContextMenu(NpcStaticInfo npc)
    {
        if (!ImGui.BeginPopupContextItem($"##navctx{npc.Index}"))
            return;

        if (npc.Location is not { } loc)
        {
            ImGui.BeginDisabled();
            ImGui.MenuItem("位置未知");
            ImGui.EndDisabled();
            ImGui.EndPopup();
            return;
        }

        var sameZone = Svc.ClientState.TerritoryType == loc.TerritoryType;
        var vnavReady = ExternalNav.IsVnavmeshReady();

        string label;
        bool enabled;

        if (sameZone)
        {
            // 已在同一張圖：不需要傳送，vnavmesh 可用就走過去，不可用就退化成標旗。
            label = vnavReady ? "導航過去" : "導航過去（未偵測到 vnavmesh，改為地圖標旗）";
            enabled = true;
        }
        else if (!ExternalNav.IsLifestreamAvailable())
        {
            label = "導航過去（未偵測到 Lifestream，無法傳送）";
            enabled = false;
        }
        else if (loc.AetheryteId == 0)
        {
            label = "導航過去（該區找不到可傳送的乙太之光）";
            enabled = false;
        }
        else if (!IsAetheryteUnlocked(loc.AetheryteId, out _))
        {
            label = "導航過去（尚未解鎖該區乙太之光）";
            enabled = false;
        }
        else
        {
            label = vnavReady ? "導航過去" : "傳送並標旗（未偵測到 vnavmesh，不會自動走過去）";
            enabled = true;
        }

        if (enabled)
        {
            if (ImGui.MenuItem(label))
                StartNavigation(npc, loc, sameZone);
        }
        else
        {
            ImGui.BeginDisabled();
            ImGui.MenuItem(label);
            ImGui.EndDisabled();
        }

        if (ImGui.MenuItem("僅地圖標旗（不傳送、不自動走路）"))
            SetMapFlagAndOpenMap(npc, loc);

        ImGui.EndPopup();
    }

    /// <summary>
    /// 啟動門到門導航：跨區就先傳送再等進圖，同圖就直接下達 vnavmesh 指令；
    /// 三態（等待傳送完成／等待進入區域／下達導航指令）走 <see cref="TaskQueue"/>。
    /// 呼叫本身即取消前一趟還在跑的導航（涵蓋「再右鍵別人」的取消需求）。
    /// </summary>
    private void StartNavigation(NpcStaticInfo npc, NpcLocation loc, bool sameZone)
    {
        navQueue.Abort();

        if (sameZone)
        {
            navQueue.Enqueue("下達導航指令", () => IssueWalkOrFallback(npc, loc));
            return;
        }

        if (!IsAetheryteUnlocked(loc.AetheryteId, out var subIndex))
        {
            Svc.Chat.Print("[TC Toolbox] 尚未解鎖該區的乙太之光，無法傳送。");
            return;
        }

        var navStartPos = Svc.Objects.LocalPlayer?.Position ?? Vector3.Zero;
        var targetTerritory = loc.TerritoryType;
        var aetheryteId = loc.AetheryteId;
        var teleportIssued = false;

        navQueue.Enqueue("等待傳送完成", () =>
        {
            if (!teleportIssued)
            {
                teleportIssued = true;
                if (!ExternalNav.TryTeleport(aetheryteId, subIndex, out var accepted) || !accepted)
                {
                    Svc.Chat.Print("[TC Toolbox] Lifestream 傳送請求失敗（可能忙碌中或傳送動作被鎖定），已取消導航。");
                    return (bool?)null;
                }
            }

            if (HasCancelledByManualMove(navStartPos))
                return (bool?)null;

            // 「傳送完成」＝畫面開始載入，或（極端情況）已經人在目的地。
            return Svc.Condition[ConditionFlag.BetweenAreas]
                   || Svc.Condition[ConditionFlag.BetweenAreas51]
                   || Svc.ClientState.TerritoryType == targetTerritory;
        }, timeoutMs: 15_000);

        navQueue.Enqueue("等待進入區域", () =>
        {
            if (Svc.ClientState.TerritoryType == targetTerritory
                && !Svc.Condition[ConditionFlag.BetweenAreas]
                && !Svc.Condition[ConditionFlag.BetweenAreas51])
                return true;

            if (HasCancelledByManualMove(navStartPos))
                return (bool?)null;

            return false;
        }, timeoutMs: 20_000);

        navQueue.Enqueue("下達導航指令", () => IssueWalkOrFallback(npc, loc));
    }

    /// <summary>下達 vnavmesh 導航指令；失敗（未安裝／網格未就緒）就退化成標旗＋開地圖。</summary>
    private static void IssueWalkOrFallback(NpcStaticInfo npc, NpcLocation loc)
    {
        if (ExternalNav.TryMoveTo(loc.WorldPosition, false, out var started) && started)
            return;

        Svc.Chat.Print($"[TC Toolbox] 無法透過 vnavmesh 自動走到「{npc.Name}」，已改為地圖標旗，請自行前往。");
        SetMapFlagAndOpenMap(npc, loc);
    }

    /// <summary>原生 SetFlagMapMarker／OpenMap；不依賴任何外掛，永遠可用。</summary>
    private static unsafe void SetMapFlagAndOpenMap(NpcStaticInfo npc, NpcLocation loc)
    {
        var agent = AgentMap.Instance();
        if (agent == null) return;
        agent->SetFlagMapMarker(loc.TerritoryType, loc.Map, loc.WorldPosition);
        agent->OpenMap(loc.Map, loc.TerritoryType, npc.Name);
    }

    /// <summary>
    /// 等待期間玩家是否手動移動離開了原地——視為使用者自行取消。載入畫面期間
    /// （BetweenAreas）座標本來就不可靠，不判斷，一律視為未取消。
    /// </summary>
    private static bool HasCancelledByManualMove(Vector3 navStartPos)
    {
        if (Svc.Condition[ConditionFlag.BetweenAreas] || Svc.Condition[ConditionFlag.BetweenAreas51])
            return false;

        var current = Svc.Objects.LocalPlayer?.Position;
        return current is { } pos && Vector3.Distance(pos, navStartPos) > ManualMoveCancelDistance;
    }

    /// <summary>該乙太之光是否已解鎖（在玩家的傳送清單裡），順便回傳其 SubIndex。</summary>
    private static bool IsAetheryteUnlocked(uint aetheryteId, out byte subIndex)
    {
        foreach (var entry in Svc.AetheryteList)
        {
            if (entry.AetheryteId == aetheryteId)
            {
                subIndex = entry.SubIndex;
                return true;
            }
        }

        subIndex = 0;
        return false;
    }
}

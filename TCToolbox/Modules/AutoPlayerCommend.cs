using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.DutyState;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using Lumina.Excel.Sheets;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 副本結束時自動把「最優隊員推薦」給對位（同職業／同職能）的隊友。
/// 機制：DutyState.DutyCompleted → 由 _Notification 開啟推薦視窗 → 解析 VoteMvp 的
/// AtkValues 取得候選名單與可選狀態 → AgentId.ContentsMvp 合成事件送出，零 hook。
/// 參考 DailyRoutines AutoPlayerCommend 的對位優先序重寫（API13、無 OmenTools 相依）。
/// </summary>
public sealed unsafe class AutoPlayerCommend : TcModule
{
    public override string InternalName => "AutoPlayerCommend";
    public override string DisplayName => "自動最優隊員推薦";

    public override string Description =>
        "副本完成時自動給予最優隊員推薦，優先挑同職業、其次同職能的隊友。" +
        "可在副本中右鍵隊員選「指定為最優隊員」手動覆蓋本局對象（指定自己＝本局不推薦）。";

    public override ModuleCategory Category => ModuleCategory.Combat;

    public override bool HasConfigUI => true;

    private const string MipDisplayConfigKey = "MipDispType";

    private readonly TaskQueue queue = new() { DefaultTimeoutMs = 15_000 };

    private readonly MenuItem assignMenuItem;

    /// <summary>本局手動指定的推薦對象（0＝未指定；等於自己＝本局不推薦）。</summary>
    private ulong assignedContentId;

    private uint? savedMipDisplayType;

    private AutoPlayerCommendConfig Config => Plugin.Instance.Config.PlayerCommend;

    public AutoPlayerCommend()
    {
        assignMenuItem = new MenuItem
        {
            Name = "指定為最優隊員",
            PrefixColor = 539,
            UseDefaultPrefix = true,
            OnClicked = OnAssignClicked,
        };
    }

    protected override void OnEnable()
    {
        queue.OnTimeout = step => Svc.Log.Warning($"[{InternalName}] 推薦流程逾時，已停止：{step}");

        Svc.DutyState.DutyCompleted += OnDutyCompleted;
        Svc.ClientState.TerritoryChanged += OnTerritoryChanged;
        Svc.ContextMenu.OnMenuOpened += OnMenuOpened;
        Svc.Framework.Update += OnUpdate;
    }

    protected override void OnDisable()
    {
        Svc.DutyState.DutyCompleted -= OnDutyCompleted;
        Svc.ClientState.TerritoryChanged -= OnTerritoryChanged;
        Svc.ContextMenu.OnMenuOpened -= OnMenuOpened;
        Svc.Framework.Update -= OnUpdate;

        queue.Abort();
        RestoreMipDisplayType();
        assignedContentId = 0;
    }

    private void OnUpdate(IFramework framework) => queue.Tick();

    private void OnTerritoryChanged(ushort territory)
    {
        assignedContentId = 0;
        queue.Abort();
        RestoreMipDisplayType();
    }

    #region 右鍵指定

    private void OnMenuOpened(IMenuOpenedArgs args)
    {
        if (args.MenuType != ContextMenuType.Default) return;
        if (!Svc.Condition[ConditionFlag.BoundByDuty]) return;
        if (args.Target is not MenuTargetDefault target) return;
        if (target.TargetCharacter == null && target.TargetContentId == 0) return;

        args.AddMenuItem(assignMenuItem);
    }

    private void OnAssignClicked(IMenuItemClickedArgs args)
    {
        if (args.Target is not MenuTargetDefault target) return;

        var contentId = target.TargetCharacter?.ContentId ?? target.TargetContentId;
        if (contentId == 0) return;

        assignedContentId = contentId;

        if (contentId == Svc.PlayerState.ContentId)
        {
            Svc.Chat.Print("[TC Toolbox] 已設定本局不自動給予最優隊員推薦。");
            return;
        }

        var name = target.TargetCharacter?.Name.ToString() ?? target.TargetName;
        Svc.Chat.Print($"[TC Toolbox] 已指定「{name}」為本局最優隊員。");
    }

    #endregion

    private void OnDutyCompleted(object? sender, ushort territory)
    {
        if (queue.IsBusy) return;
        if (Svc.Party.Length <= 1) return;
        if (assignedContentId != 0 && assignedContentId == Svc.PlayerState.ContentId) return;

        // 推薦視窗預設可能被設定為「不顯示清單」；暫時關掉自動顯示，送出後還原
        SuppressMipDisplayType();

        queue.Enqueue("開啟最優隊員推薦視窗", OpenCommendWindow, 10_000);
        queue.Enqueue("送出最優隊員推薦", GiveCommendation, 20_000);
        queue.Enqueue("還原推薦清單顯示設定", RestoreMipDisplayType);
    }

    private void SuppressMipDisplayType()
    {
        try
        {
            if (!Svc.GameConfig.UiConfig.TryGet(MipDisplayConfigKey, out uint current)) return;
            savedMipDisplayType = current;
            Svc.GameConfig.UiConfig.Set(MipDisplayConfigKey, 0u);
        }
        catch (Exception ex)
        {
            Svc.Log.Debug(ex, $"[{InternalName}] 無法讀寫 {MipDisplayConfigKey}，略過");
            savedMipDisplayType = null;
        }
    }

    private void RestoreMipDisplayType()
    {
        if (savedMipDisplayType is not { } value) return;
        savedMipDisplayType = null;

        try
        {
            Svc.GameConfig.UiConfig.Set(MipDisplayConfigKey, value);
        }
        catch (Exception ex)
        {
            Svc.Log.Debug(ex, $"[{InternalName}] 還原 {MipDisplayConfigKey} 失敗");
        }
    }

    private bool? OpenCommendWindow()
    {
        if (UiHelper.IsAddonReady("VoteMvp")) return true;

        var notification = UiHelper.GetAddon("_Notification");
        if (notification == null) return false;

        if (Throttle.Pass("AutoPlayerCommend-Open", 1_000))
            UiHelper.FireCallback(notification, true, 0, 11);

        return false;
    }

    private bool? GiveCommendation()
    {
        var voteMvp = UiHelper.GetAddon("VoteMvp");
        if (!UiHelper.IsReady(voteMvp)) return false;

        var agentModule = AgentModule.Instance();
        if (agentModule == null) return null;

        var agent = agentModule->GetAgentByInternalId(AgentId.ContentsMvp);
        if (agent == null || !agent->IsAgentActive()) return false;

        var candidates = BuildCandidateOrder();
        if (candidates.Count == 0) return true;

        foreach (var candidate in candidates)
        {
            if (!TryFindVoteIndex(voteMvp, candidate.Name, candidate.ClassJobId, out var index)) continue;

            UiHelper.SendAgentEvent(AgentId.ContentsMvp, 0, 0, index);

            if (Config.NotifyOnCommend)
            {
                var jobName = Svc.Data.GetExcelSheet<ClassJob>().GetRowOrDefault(candidate.ClassJobId)?.Name.ExtractText()
                              ?? string.Empty;
                Svc.Chat.Print($"[TC Toolbox] 已給予「{candidate.Name}」（{jobName}）最優隊員推薦。");
            }

            return true;
        }

        Svc.Log.Warning($"[{InternalName}] 候選名單與推薦視窗內容對不上，未送出推薦");
        return true;
    }

    #region 對位優先序

    private enum PlayerRole
    {
        None,
        Tank,
        MeleeDps,
        RangedDps,
        Healer,
    }

    private static PlayerRole ToRole(byte rawRole) => rawRole switch
    {
        1 => PlayerRole.Tank,
        2 => PlayerRole.MeleeDps,
        3 => PlayerRole.RangedDps,
        4 => PlayerRole.Healer,
        _ => PlayerRole.None,
    };

    private static bool IsDps(PlayerRole role) => role is PlayerRole.MeleeDps or PlayerRole.RangedDps;

    private sealed record Candidate(
        string Name,
        uint ClassJobId,
        uint ClassJobCategoryId,
        byte RawRole,
        PlayerRole Role,
        ulong ContentId);

    private List<Candidate> BuildCandidateOrder()
    {
        var localPlayer = Svc.Objects.LocalPlayer;
        if (localPlayer == null) return [];

        var localContentId = Svc.PlayerState.ContentId;
        var jobSheet = Svc.Data.GetExcelSheet<ClassJob>();

        var members = new List<Candidate>();
        foreach (var member in Svc.Party)
        {
            var contentId = (ulong)member.ContentId;
            if (contentId == localContentId) continue;

            var jobRow = jobSheet.GetRowOrDefault(member.ClassJob.RowId);
            if (jobRow == null) continue;

            var rawRole = jobRow.Value.Role;
            members.Add(new Candidate(
                member.Name.TextValue,
                member.ClassJob.RowId,
                jobRow.Value.ClassJobCategory.RowId,
                rawRole,
                ToRole(rawRole),
                contentId));
        }

        if (members.Count == 0) return [];

        if (Config.IgnoreBlacklistedPlayers)
        {
            var blacklist = InfoProxyBlacklist.Instance();
            if (blacklist != null)
            {
                members.RemoveAll(x =>
                    blacklist->GetBlockResultType(0, x.ContentId) != InfoProxyBlacklist.BlockResultType.NotBlocked);
            }
        }

        if (members.Count == 0) return [];

        var selfJobRow = jobSheet.GetRowOrDefault(localPlayer.ClassJob.RowId);
        var selfRawRole = selfJobRow?.Role ?? 0;
        var selfRole = ToRole(selfRawRole);
        var selfJobId = localPlayer.ClassJob.RowId;
        var selfCategoryId = selfJobRow?.ClassJobCategory.RowId ?? 0;

        // 隊伍中同職業的人數（自身以外）
        var jobCounts = members.GroupBy(x => x.ClassJobId).ToDictionary(g => g.Key, g => g.Count());

        var assigned = assignedContentId;

        return members
               // 1. 手動指定 > 同職業 > 同職能細分
               .OrderByDescending(x =>
               {
                   if (assigned != 0 && x.ContentId == assigned) return 3;
                   if (selfJobId == x.ClassJobId) return 2;

                   if (IsDps(selfRole) && IsDps(x.Role))
                       return selfRole == x.Role && selfCategoryId == x.ClassJobCategoryId ? 1 : 0;

                   return selfRawRole == x.RawRole ? 1 : 0;
               })
               // 2. 自身是 DPS 時，隊伍裡「有兩個以上的同一種其他 DPS 職業」降權
               .ThenByDescending(x =>
                   IsDps(selfRole) && IsDps(x.Role) && selfJobId != x.ClassJobId &&
                   jobCounts.TryGetValue(x.ClassJobId, out var count) && count >= 2
                       ? 0
                       : 1)
               // 3. 職能親和度
               .ThenByDescending(x => selfRole switch
               {
                   PlayerRole.Tank or PlayerRole.Healer => x.Role is PlayerRole.Tank or PlayerRole.Healer ? 1 : 0,
                   PlayerRole.MeleeDps => x.Role switch
                   {
                       PlayerRole.MeleeDps => 3,
                       PlayerRole.RangedDps => 2,
                       PlayerRole.Healer => 1,
                       _ => 0,
                   },
                   PlayerRole.RangedDps => x.Role switch
                   {
                       PlayerRole.RangedDps => 3,
                       PlayerRole.MeleeDps => 2,
                       PlayerRole.Healer => 1,
                       _ => 0,
                   },
                   _ => 0,
               })
               .ToList();
    }

    /// <summary>
    /// 在 VoteMvp 的 AtkValues 裡找出對應玩家的候選索引。
    /// 版面：[1]＝候選人數、[2+i]＝職業圖示 ID（62100 + 職業 ID）、[9+i]＝名稱、[16+i]＝是否可選。
    /// </summary>
    private static bool TryFindVoteIndex(
        FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase* voteMvp,
        string playerName,
        uint classJobId,
        out int index)
    {
        index = -1;

        // 🔴 光是判 voteMvp 與長度還不夠。AtkValuesSpan 的實作是
        // new Span<AtkValue>(AtkValues, AtkValuesCount)，它自己不判 AtkValues 這個欄位，
        // 而 Span 的建構子也不驗指標。addon 拆解時 AtkValues 會先被釋放成 null、
        // AtkValuesCount 卻可能還留著殘值，這個組合會合法建構出一個長度非零的 Span，
        // 連 Span 自己的邊界檢查都會放行，一直到真的索引下去才對位址 0 解參考 ＝
        // AccessViolationException（corrupted-state exception，try/catch 攔不到）。
        // ⇒ 讀不到就回 false ＝ 這名候選人跳過，由呼叫端試下一位。
        if (voteMvp == null || voteMvp->AtkValues == null) return false;

        var values = voteMvp->AtkValuesSpan;
        if (values.Length < 24) return false;

        var count = (int)values[1].UInt;
        if (count is <= 0 or > 7) return false;

        for (var i = 0; i < count; i++)
        {
            if (values[16 + i].UInt != 1) continue;

            var nameValue = values[9 + i];
            if (nameValue.Type != FFXIVClientStructs.FFXIV.Component.GUI.ValueType.String ||
                nameValue.String.Value == null) continue;

            var name = Dalamud.Memory.MemoryHelper
                               .ReadSeStringNullTerminated((nint)nameValue.String.Value).TextValue;
            if (!string.Equals(name, playerName, StringComparison.Ordinal)) continue;

            var iconId = values[2 + i].UInt;
            if (iconId < 62100) continue;
            if (iconId - 62100 != classJobId) continue;

            index = i;
            return true;
        }

        return false;
    }

    #endregion

    public override void DrawConfig()
    {
        var ignore = Config.IgnoreBlacklistedPlayers;
        if (ImGui.Checkbox("跳過遊戲黑名單內的玩家", ref ignore))
        {
            Config.IgnoreBlacklistedPlayers = ignore;
            Plugin.Instance.Config.Save();
        }

        var notify = Config.NotifyOnCommend;
        if (ImGui.Checkbox("推薦後顯示聊天訊息", ref notify))
        {
            Config.NotifyOnCommend = notify;
            Plugin.Instance.Config.Save();
        }

        ImGui.TextDisabled("DR 原版的「黑名單副本」清單未移植；不想推薦時請在該局副本內右鍵自己\n選「指定為最優隊員」，即為本局不推薦。");
    }
}

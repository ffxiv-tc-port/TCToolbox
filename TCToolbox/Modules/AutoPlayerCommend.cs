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

    /// <summary>常駐 HUD 的通知容器；對它送「點了第 N 種通知」的合成事件。</summary>
    private const string NoticeAddonName = "_Notification";

    /// <summary>
    /// 最優隊員推薦通知的<b>實體視窗</b>：它在 ⇔ 這一局真的掛著一則「可以給推薦」的通知。
    /// </summary>
    /// <remarks>
    /// 🔑 <b>離線機械證明</b>（台服 7.20 <c>ffxiv_dx11.exe</c>，image base <c>0x140000000</c>）：
    /// <list type="number">
    /// <item>通知種類的名字指標表在 <c>0x142123DA0</c>，共 34 筆（筆數來自關閉函式
    /// <c>0x14146CC20</c> 開頭的界限檢查 <c>cmp edi, 0x22</c>），
    /// <b>索引 11 就是 <c>_NotificationIcMvp</c></b>——本模組送的第二個參數正是 11。</item>
    /// <item>整顆執行檔<b>只有一處</b>引用那張表（<c>0x14146E5EA</c> 的
    /// <c>lea r12,[rip+…]</c> ＋ <c>mov r12,[r12+rbp*8]</c>），而那段程式碼做的事就是
    /// 「把 <c>表[種類]</c> 這個名字的視窗開起來」⇒「這扇視窗在」與「這種通知正掛著」是同一件事。</item>
    /// <item><c>_Notification</c> 自己的 <c>ReceiveEvent</c>（vtable <c>0x142123900</c> 第 2 格、
    /// 實作 <c>0x14146CAA0</c>）在點擊事件上組出的參數正是
    /// <c>{Int 0, Int 種類索引, …}</c> 再對自己 <c>FireCallback</c>——
    /// 也就是說本模組送的 <c>(0, 11)</c> <b>逐欄與玩家真的用滑鼠點下去完全相同</b>。</item>
    /// </list>
    /// ⚠️ 目前<b>只拿來寫診斷、刻意不當閘門</b>：「查不到它就直接放棄」還沒有實機證據，
    /// 猜錯的話失敗形式是「推薦從此永遠不送出」，而且一樣安靜。
    /// </remarks>
    private const string MvpNoticeAddonName = "_NotificationIcMvp";

    /// <summary>推薦清單本體。</summary>
    private const string VoteAddonName = "VoteMvp";

    private readonly TaskQueue queue = new() { DefaultTimeoutMs = 15_000 };

    private readonly MenuItem assignMenuItem;

    /// <summary>本局手動指定的推薦對象（0＝未指定；等於自己＝本局不推薦）。</summary>
    private ulong assignedContentId;

    private uint? savedMipDisplayType;

    /// <summary>本輪流程中是否曾經觀察到 <see cref="MvpNoticeAddonName"/>（純診斷，不影響流程）。</summary>
    private bool mvpNoticeSeen;

    /// <summary>本輪真的對 <see cref="NoticeAddonName"/> 送出了幾次合成點擊（純診斷）。</summary>
    private int noticeClicks;

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
        queue.OnTimeout = OnQueueTimeout;

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

        mvpNoticeSeen = false;
        noticeClicks = 0;

        // 🔴 開場診斷寫 Information：這條路每場副本只走一次，而且是「沒反應」時唯一的現場。
        Svc.Log.Information(
            $"[{InternalName}] 副本完成（區域 {territory}），開始推薦流程：隊伍 {Svc.Party.Length} 人、" +
            $"手動指定={(assignedContentId == 0 ? "無" : assignedContentId.ToString())}、" +
            $"{MvpNoticeAddonName}={UiHelper.DescribeAddonInstances(MvpNoticeAddonName)}、" +
            $"{VoteAddonName}={UiHelper.DescribeAddonInstances(VoteAddonName)}");

        // 推薦視窗預設可能被設定為「不顯示清單」；暫時關掉自動顯示，送出後還原
        SuppressMipDisplayType();

        queue.Enqueue("開啟最優隊員推薦視窗", OpenCommendWindow, 10_000);
        queue.Enqueue("送出最優隊員推薦", GiveCommendation, 20_000);
        queue.Enqueue("還原推薦清單顯示設定", RestoreMipDisplayType);
    }

    /// <summary>
    /// 步驟逾時的收尾：<b>先把 <see cref="MipDisplayConfigKey"/> 還原</b>，再寫一行分得出成因的診斷。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>還原一定要放在這裡</b>：<see cref="TaskQueue.Tick"/> 逾時時是<b>先 <c>Abort()</c>
    /// 再叫 <see cref="TaskQueue.OnTimeout"/></b>，佇列尾巴那個「還原推薦清單顯示設定」步驟
    /// <b>已經連同整條佇列被丟掉了</b>。在補上這一支之前，逾時之後要一路等到換區
    /// （<see cref="OnTerritoryChanged"/>）或停用模組才會還原——玩家留在原地不走的話，
    /// 遊戲的「最優隊員推薦顯示方式」就一直被我們壓成 0。
    /// </remarks>
    private void OnQueueTimeout(string step)
    {
        RestoreMipDisplayType();

        var voteState = UiHelper.DescribeAddonInstances(VoteAddonName);
        var context =
            $"[{InternalName}] 推薦流程逾時，已停止：{step}。" +
            $"送出 {noticeClicks} 次通知點擊、全程有沒有看到「{MvpNoticeAddonName}」={mvpNoticeSeen}、" +
            $"{VoteAddonName}={voteState}。";

        // ⚠️ 兩種成因的等級刻意不同：沒有可推薦對象是預期行為（Information），
        //    看得到通知卻打不開清單才是真的異常（Warning，維持原本的等級）。
        if (mvpNoticeSeen)
        {
            Svc.Log.Warning(
                context +
                $"看得到通知卻始終打不開 {VoteAddonName} ⇒ 合成點擊送得出去但沒有生效，請把這一行回報。");
            return;
        }

        Svc.Log.Information(
            context +
            $"全程都沒有出現「{MvpNoticeAddonName}」通知 ⇒ 這一局本來就沒有可以推薦的對象" +
            "（整隊都是自己人的固定隊、隊友是 NPC 支援者等等都會這樣），逾時是正確行為、不是故障。");
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

    /// <summary>
    /// 對 <see cref="NoticeAddonName"/> 送「點了第 11 種通知（最優隊員推薦）」，直到
    /// <see cref="VoteAddonName"/> 開起來為止。
    /// </summary>
    /// <remarks>
    /// 📌 <b>按壓行為刻意與先前完全相同</b>（同樣的參數、同樣的 1 秒節流、同樣不因為
    /// <see cref="MvpNoticeAddonName"/> 不在就提早放棄）——這一版只多記了兩個診斷欄位。
    /// 「通知不在就別按」看起來很合理，但它會把「推薦不送出」這個失敗形式變成靜默的，
    /// 而目前還沒有實機證據能排除「通知在、只是 addon 查不到」。
    /// </remarks>
    private bool? OpenCommendWindow()
    {
        if (UiHelper.IsAddonReady(VoteAddonName)) return true;

        // 只判 null、不解參考，也不跨幀保存：拿到就用完丟掉。
        if (UiHelper.GetAddon(MvpNoticeAddonName) != null) mvpNoticeSeen = true;

        var notification = UiHelper.GetAddon(NoticeAddonName);
        if (notification == null) return false;

        if (Throttle.Pass("AutoPlayerCommend-Open", 1_000) &&
            UiHelper.TryFireCallback(notification, true, 0, 11))
            noticeClicks++;

        return false;
    }

    private bool? GiveCommendation()
    {
        var voteMvp = UiHelper.GetAddon(VoteAddonName);
        if (!UiHelper.IsReady(voteMvp)) return false;

        var agentModule = AgentModule.Instance();
        if (agentModule == null) return null;

        var agent = agentModule->GetAgentByInternalId(AgentId.ContentsMvp);
        if (agent == null || !agent->IsAgentActive()) return false;

        var candidates = BuildCandidateOrder();
        if (candidates.Count == 0)
        {
            Svc.Log.Information($"[{InternalName}] 推薦清單已開啟，但沒有任何可推薦的隊友（可能全在黑名單裡），本局不推薦。");
            return true;
        }

        foreach (var candidate in candidates)
        {
            if (!TryFindVoteIndex(voteMvp, candidate.Name, candidate.ClassJobId, out var index)) continue;

            UiHelper.SendAgentEvent(AgentId.ContentsMvp, 0, 0, index);

            // 🔴 這一行不受「推薦後顯示聊天訊息」開關影響：關掉聊天提示的人也要能在 log 裡看到結果。
            Svc.Log.Information(
                $"[{InternalName}] 已送出最優隊員推薦：「{candidate.Name}」（職業 {candidate.ClassJobId}、" +
                $"清單索引 {index}），本輪共送出 {noticeClicks} 次通知點擊。");

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

using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Aetherytes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 戰鬥中排隊傳送：戰鬥中從本模組的目的地清單挑一個乙太之光，記下來，
/// <b>等戰鬥結束</b>再透過 Lifestream 傳送過去。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 <b>零記憶體 patch、零封包、零 hook。</b>DR 原版是把客戶端的「戰鬥中不能傳送」判斷
/// （<c>CanUseTeleport</c>）用記憶體 patch 打穿，再攔 <c>ExecuteCommand</c> 把傳送指令收下來排隊。
/// 本模組<b>不碰記憶體</b>：既然那道客戶端閘門會擋掉戰鬥中的傳送，攔 <c>ExecuteCommand</c>
/// 這條路在不 patch 的前提下根本不會觸發（指令在送到 <c>ExecuteCommand</c> 之前就被閘門擋下）。
/// ⇒ 唯一的觸發路徑改成<b>本模組自己的目的地視窗</b>：使用者在戰鬥中從
/// <see cref="IAetheryteList"/>（自己已解鎖的乙太之光）點一個目的地，我們存下
/// <c>(AetheryteId, SubIndex)</c>，等 <see cref="ConditionFlag.InCombat"/> 轉為 false 時，
/// 走 Lifestream 的 <c>Teleport</c> IPC 執行。<b>不透過聊天指令 <c>/li</c></b>
/// （空參數的 <c>/li</c> 是跨世界傳送，紅線）。
/// </para>
/// <para>
/// 🔴 <b>只記 id，不跨幀保存原生指標。</b>排隊的是 <c>(uint AetheryteId, byte SubIndex)</c>
/// 兩個純值加一段已複製的名字字串，<see cref="IAetheryteList"/> 每次繪製重新枚舉。
/// </para>
/// <para>
/// 📌 開著但不去用它，遊戲行為完全不變（清單是唯讀的、只有點下某個目的地才會排隊），
/// 所以標記為 <see cref="IsManualTrigger"/>。
/// </para>
/// </remarks>
public sealed class QueueCombatTeleport : TcModule
{
    public override string InternalName => "QueueCombatTeleport";
    public override string DisplayName => "戰鬥中排隊傳送";

    public override string Description =>
        "戰鬥中從清單挑一個乙太之光排隊，戰鬥結束後自動傳送過去（透過 Lifestream，不碰封包、不碰記憶體）。"
        + "不在戰鬥中就直接傳送。";

    public override ModuleCategory Category => ModuleCategory.Combat;

    /// <summary>開著不去點目的地，什麼都不會發生。</summary>
    public override bool IsManualTrigger => true;

    public override bool HasConfigUI => true;

    private static readonly Vector4 WarnColor = new(1f, 0.65f, 0.25f, 1f);
    private static readonly Vector4 NeutralColor = new(0.68f, 0.68f, 0.68f, 1f);

    private QueueCombatTeleportConfig Config => Plugin.Instance.Config.QueueCombatTeleport;

    private readonly TaskQueue queue = new() { DefaultTimeoutMs = 15_000 };

    /// <summary>
    /// 已排隊的目的地；<c>null</c>＝目前沒有排隊。
    /// </summary>
    /// <remarks>🔴 純值：乙太之光 RowId、子索引、已複製的名字。不含任何原生指標。</remarks>
    private (uint AetheryteId, byte SubIndex, string Name)? queued;

    private bool windowOpen;

    private string searchFilter = string.Empty;

    /// <summary>本幀重建的目的地快照（每幀清空重填，不跨幀保存）。</summary>
    private readonly List<(uint AetheryteId, byte SubIndex, string Name, uint GilCost)> destinations = [];

    protected override void OnEnable()
    {
        queued = null;
        windowOpen = false;
        searchFilter = string.Empty;

        queue.OnTimeout = step =>
            Svc.Log.Information($"[{InternalName}] 排隊步驟逾時：{step}（放棄本次傳送）");

        Svc.Framework.Update += OnFrameworkUpdate;
        Svc.Condition.ConditionChange += OnConditionChanged;
        Svc.PluginInterface.UiBuilder.Draw += DrawWindow;

        Svc.Log.Information($"[{InternalName}] 模組啟用。");
    }

    protected override void OnDisable()
    {
        Svc.PluginInterface.UiBuilder.Draw -= DrawWindow;
        Svc.Condition.ConditionChange -= OnConditionChanged;
        Svc.Framework.Update -= OnFrameworkUpdate;

        queue.Abort();
        queued = null;
        windowOpen = false;
    }

    private void OnFrameworkUpdate(IFramework framework) => queue.Tick();

    // ── 排隊 · 觸發 ───────────────────────────────────────────────────────────

    private void OnConditionChanged(ConditionFlag flag, bool value)
    {
        // 只關心「脫離戰鬥」這一刻，而且要有排隊中的目的地。
        if (flag != ConditionFlag.InCombat || value) return;
        if (queued == null) return;

        ScheduleTeleport();
    }

    /// <summary>把排隊中的目的地排進佇列執行（延遲 → 傳送）。</summary>
    private void ScheduleTeleport()
    {
        if (queued is not { } target) return;

        // 使用者已經按下新的目的地或取消時，舊的那趟就此作廢。
        queue.Abort();

        var delay = Math.Max(0, Config.DelayMs);
        if (delay > 0)
            queue.EnqueueDelay(delay, $"等待 {delay}ms 後傳送");

        queue.Enqueue($"傳送到「{target.Name}」", () =>
        {
            // 執行前再確認一次：延遲期間可能又進了戰鬥（連續遭遇），這時不要硬送。
            if (Svc.Condition[ConditionFlag.InCombat])
            {
                Svc.Log.Information($"[{InternalName}] 傳送前又進入戰鬥，維持排隊等下次脫戰。");
                return null;
            }

            if (!ExternalNav.IsLifestreamAvailable())
            {
                Svc.Log.Information($"[{InternalName}] Lifestream 未安裝／未載入，無法執行排隊的傳送：「{target.Name}」。");
                Svc.Chat.PrintError($"[TC Toolbox] 需要 Lifestream 才能執行排隊的傳送（目的地：{target.Name}）。");
                queued = null;
                return null;
            }

            if (!ExternalNav.TryTeleport(target.AetheryteId, target.SubIndex, out var accepted))
            {
                Svc.Log.Information($"[{InternalName}] 呼叫 Lifestream.Teleport 失敗：「{target.Name}」。");
                queued = null;
                return null;
            }

            Svc.Log.Information(
                $"[{InternalName}] 已請求傳送 → 乙太之光 {target.AetheryteId}(子{target.SubIndex})「{target.Name}」，Lifestream 接受={accepted}");

            if (Config.AnnounceInChat)
                Svc.Chat.Print($"[TC Toolbox] 戰鬥結束，傳送到「{target.Name}」。");

            queued = null;
            return true;
        });
    }

    /// <summary>使用者從清單選了一個目的地。</summary>
    private void QueueDestination(uint aetheryteId, byte subIndex, string name)
    {
        queued = (aetheryteId, subIndex, name);
        queue.Abort();

        var inCombat = Svc.Condition[ConditionFlag.InCombat];

        Svc.Log.Information(
            $"[{InternalName}] 使用者選定目的地：乙太之光 {aetheryteId}(子{subIndex})「{name}」，"
            + (inCombat ? "戰鬥中——已排隊，脫戰後執行。" : "非戰鬥中——立即傳送。"));

        if (inCombat)
        {
            if (Config.AnnounceInChat)
                Svc.Chat.Print($"[TC Toolbox] 已排隊，戰鬥結束後傳送到「{name}」。");
        }
        else
        {
            // 不在戰鬥中：沒有「脫戰」事件可等，直接排進佇列執行。
            ScheduleTeleport();
        }
    }

    private void CancelQueued()
    {
        if (queued is { } t)
            Svc.Log.Information($"[{InternalName}] 使用者取消排隊的傳送：「{t.Name}」。");

        queued = null;
        queue.Abort();
    }

    // ── 列上提示 ───────────────────────────────────────────────────────────────

    public override ModuleNotice? RowNotice
    {
        get
        {
            if (queued is not { } t) return null;

            // 排了隊卻沒有 Lifestream＝這趟永遠執行不了，值得在列上警示。
            if (!ExternalNav.IsLifestreamAvailable())
            {
                return new ModuleNotice(
                    ModuleNoticeLevel.Warning,
                    "! 缺 Lifestream",
                    $"已排隊傳送到「{t.Name}」，但偵測不到 Lifestream，脫離戰鬥時將無法執行。\n"
                    + "安裝並啟用 Lifestream，或按模組視窗裡的「取消」清除排隊。");
            }

            return new ModuleNotice(
                ModuleNoticeLevel.Unknown,
                $"已排隊：{t.Name}",
                "戰鬥結束後會傳送到這個目的地。在模組視窗裡可以改選或取消。");
        }
    }

    // ── UI ────────────────────────────────────────────────────────────────────

    public override void DrawConfig()
    {
        if (ImGui.Button(windowOpen ? "關閉目的地清單" : "開啟目的地清單"))
            windowOpen = !windowOpen;

        ImGui.TextDisabled("戰鬥中從清單挑目的地會排隊，戰鬥結束後自動傳送；不在戰鬥中則立即傳送。");

        ImGui.SetNextItemWidth(160f);
        var delay = Config.DelayMs;
        if (ImGui.SliderInt("脫戰後延遲（毫秒）", ref delay, 0, 5000))
        {
            Config.DelayMs = delay;
            Plugin.Instance.Config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("戰鬥剛結束時常常還在收尾（撿取、對話），留一點延遲比較不會打斷。");

        var announce = Config.AnnounceInChat;
        if (ImGui.Checkbox("在聊天欄提示排隊／傳送", ref announce))
        {
            Config.AnnounceInChat = announce;
            Plugin.Instance.Config.Save();
        }
    }

    private void DrawWindow()
    {
        if (!windowOpen) return;

        ImGui.SetNextWindowSize(new Vector2(420, 460), ImGuiCond.FirstUseEver);
        if (ImGui.Begin($"{DisplayName}###TCToolboxQueueCombatTeleport", ref windowOpen))
            DrawContent();
        ImGui.End();
    }

    private void DrawContent()
    {
        if (!Svc.ClientState.IsLoggedIn)
        {
            ImGui.TextDisabled("尚未登入。");
            return;
        }

        var lifestream = ExternalNav.IsLifestreamAvailable();
        if (!lifestream)
        {
            ImGui.TextColored(WarnColor, "偵測不到 Lifestream，無法執行傳送。");
            ImGui.TextDisabled("本模組透過 Lifestream 的傳送 IPC 執行，請先安裝並啟用 Lifestream。");
            ImGui.Separator();
        }

        DrawQueuedLine();
        ImGui.Separator();

        ImGui.SetNextItemWidth(-1f);
        var filter = searchFilter;
        if (ImGui.InputTextWithHint("##teleportSearch", "搜尋目的地名稱…", ref filter, 64))
            searchFilter = filter;

        DrawDestinationList(lifestream);
    }

    private void DrawQueuedLine()
    {
        if (queued is { } t)
        {
            var inCombat = Svc.Condition[ConditionFlag.InCombat];
            ImGui.TextColored(NeutralColor,
                inCombat ? $"已排隊：{t.Name}（戰鬥結束後傳送）" : $"已排隊：{t.Name}（即將傳送）");
            ImGui.SameLine();
            if (ImGui.Button("取消##cancelQueued"))
                CancelQueued();
        }
        else
        {
            ImGui.TextDisabled(Svc.Condition[ConditionFlag.InCombat]
                ? "戰鬥中——點下面任一目的地即可排隊。"
                : "不在戰鬥中——點下面任一目的地會立即傳送。");
        }
    }

    private void DrawDestinationList(bool lifestreamAvailable)
    {
        RebuildDestinations();

        if (destinations.Count == 0)
        {
            ImGui.TextDisabled(searchFilter.Length > 0
                ? "沒有符合的目的地。"
                : "目前沒有可用的乙太之光（需要先解鎖／傳承）。");
            return;
        }

        using var child = ImRaii.Child("##teleportList", new Vector2(-1f, -1f), true);
        if (!child) return;

        foreach (var (aetheryteId, subIndex, name, gilCost) in destinations)
        {
            using var id = ImRaii.PushId((int)((aetheryteId << 4) | subIndex));

            using (ImRaii.Disabled(!lifestreamAvailable))
            {
                if (ImGui.Button("傳送"))
                    QueueDestination(aetheryteId, subIndex, name);
            }

            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(name);

            if (gilCost > 0)
            {
                ImGui.SameLine();
                ImGui.TextDisabled($"（{gilCost} 金幣）");
            }
        }
    }

    /// <summary>
    /// 重新枚舉 <see cref="IAetheryteList"/> 抄成純值快照。
    /// </summary>
    /// <remarks>
    /// 🔴 <c>IAetheryteEntry</c> 是 Dalamud 受管理的包裝，這裡把要用到的欄位全部抄成值型別，
    /// 繪製時不再回頭碰它。名字取自 <c>AetheryteData</c> 的 Lumina 列（唯讀查表，安全）。
    /// </remarks>
    private void RebuildDestinations()
    {
        destinations.Clear();

        try
        {
            foreach (var entry in Svc.AetheryteList)
            {
                if (entry == null) continue;

                var name = ResolveName(entry);
                if (string.IsNullOrWhiteSpace(name)) continue;

                if (searchFilter.Length > 0
                    && name.IndexOf(searchFilter, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                destinations.Add((entry.AetheryteId, entry.SubIndex, name, entry.GilCost));
            }
        }
        catch (Exception ex)
        {
            if (Throttle.Pass("QueueCombatTeleport-ListError", 30_000))
                Svc.Log.Information(ex, $"[{InternalName}] 枚舉乙太之光清單時發生例外，本幀跳過。");
            destinations.Clear();
        }
    }

    private static string ResolveName(IAetheryteEntry entry)
    {
        var aetheryte = entry.AetheryteData.ValueNullable;
        if (aetheryte is not { } row) return string.Empty;

        var placeName = row.PlaceName.ValueNullable;
        var baseName = placeName?.Name.ExtractText() ?? string.Empty;

        // 房屋／公寓等共用同一 RowId、以 SubIndex 區分的目的地，補一個序號才分得出來。
        if (entry.SubIndex != 0)
            baseName = string.IsNullOrWhiteSpace(baseName)
                ? $"（子索引 {entry.SubIndex}）"
                : $"{baseName}（{entry.SubIndex}）";

        return baseName;
    }
}

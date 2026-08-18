using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 副本開始／結束時自動送出一句自訂訊息。
/// </summary>
/// <remarks>
/// 參考 ComplexTweaks <c>EnhancedDutyStartEnd</c> 的「播報」那一半重寫。
/// <para>
/// 🔴 <b>上游的「指定玩家不在場就自動退本」整段刻意不移植。</b>
/// 那一段會在副本開始的瞬間自己呼叫 <c>LeaveCurrentContent</c>，等於把「有沒有某個人在隊上」
/// 接成一條自動退本的觸發鏈——是艦隊的自動化紅線。本模組只送訊息，
/// <b>不會離開副本、不會報名、不會對隊伍做任何操作</b>。
/// </para>
/// <para>
/// 🔴 <b>頻道指令一律從遊戲的 <c>TextCommand</c> 表查，不寫死。</b>台服每個頻道同時有
/// 中文別名與英文別名，寫死任何一種都是在賭那一欄在那一列有值，賭輸的表現是
/// 「訊息被當成一般發言送到目前頻道」——那比沒送出去糟得多。
/// </para>
/// <para>
/// 📌 兩個頻道的預設值都是<b>默語</b>（<c>/echo</c>）：只有自己看得到。
/// 加上訊息預設為空字串，所以開了模組但沒設定的人不會對任何人送出任何東西。
/// </para>
/// </remarks>
public sealed class DutyAnnounce : TcModule
{
    public override string InternalName => "DutyAnnounce";
    public override string DisplayName => "副本開始／結束播報";

    public override string Description =>
        "副本正式開始與通關時，各送出一句你自己寫的訊息（可選頻道，預設是只有自己看得到的默語）。" +
        "訊息支援 {duty}／{time}／{elapsed} 三個代入欄位。" +
        "只送訊息——不會退本、不會報名、不會對隊伍做任何操作。";

    public override ModuleCategory Category => ModuleCategory.Combat;

    public override bool HasConfigUI => true;

    private DutyAnnounceConfig Config => Plugin.Instance.Config.DutyAnnounce;

    private readonly TaskQueue queue = new();

    /// <summary>本場副本開始的時刻（<see cref="Environment.TickCount64"/>）；<c>0</c>＝不知道。</summary>
    /// <remarks>
    /// ⚠️ <c>0</c> 是<b>真的「不知道」</b>而不是「剛開始」：中途進本、斷線重連、或是在副本裡才啟用
    /// 模組，都拿不到開始時刻。那種情況 <c>{elapsed}</c> 會被代入「?」而不是「00:00」——
    /// 把不知道畫成一個看起來很正常的數字會直接誤導人。
    /// </remarks>
    private long dutyStartTick;

    /// <summary>副本名的快取（TerritoryType → 名稱）。資料表不會在執行期變動。</summary>
    private static readonly Dictionary<ushort, string> DutyNameCache = [];

    protected override void OnEnable()
    {
        Svc.DutyState.DutyStarted += OnDutyStarted;
        Svc.DutyState.DutyCompleted += OnDutyCompleted;
        Svc.ClientState.TerritoryChanged += OnTerritoryChanged;
        Svc.Framework.Update += OnUpdate;
    }

    protected override void OnDisable()
    {
        Svc.DutyState.DutyStarted -= OnDutyStarted;
        Svc.DutyState.DutyCompleted -= OnDutyCompleted;
        Svc.ClientState.TerritoryChanged -= OnTerritoryChanged;
        Svc.Framework.Update -= OnUpdate;

        dutyStartTick = 0;
        queue.Abort();
    }

    /// <summary>
    /// 模組列上的提示：訊息會不會被其他玩家看到。
    /// </summary>
    /// <remarks>
    /// 🔑 這正是「隨時掃視」該放列上的資訊——公開頻道的播報一旦設錯，代價是每一場副本
    /// 都對整隊人洗一次版，而使用者不會主動打開設定去確認。
    /// 整段包在 try 裡：這是 Draw 路徑。
    /// </remarks>
    public override ModuleNotice? RowNotice
    {
        get
        {
            try
            {
                var start = HasMessage(Config.StartMessage) ? Config.StartChannelRow : (uint?)null;
                var end = HasMessage(Config.EndMessage) ? Config.EndChannelRow : (uint?)null;

                if (start == null && end == null) return null;

                var publicChannels = new List<string>(2);
                if (start != null && start != TextCommands.ChatChannelRows.Echo)
                    AddChannelName(publicChannels, start.Value);
                if (end != null && end != TextCommands.ChatChannelRows.Echo && end != start)
                    AddChannelName(publicChannels, end.Value);

                if (publicChannels.Count == 0)
                    return new ModuleNotice(ModuleNoticeLevel.Unknown, "只送默語（只有自己看得到）", string.Empty);

                return new ModuleNotice(
                    ModuleNoticeLevel.Warning,
                    $"會送到 {string.Join("、", publicChannels)}",
                    "這些頻道的訊息其他玩家看得到。每一場副本都會送一次，設錯的話等於固定洗版。");
            }
            catch (Exception ex)
            {
                Svc.Log.Warning(ex, $"[{InternalName}] 產生列上提示失敗");
                return null;
            }
        }
    }

    private static void AddChannelName(List<string> into, uint rowId)
    {
        var name = TextCommands.DisplayName(rowId);
        into.Add(string.IsNullOrEmpty(name) ? $"頻道 #{rowId}" : name);
    }

    private void OnUpdate(IFramework framework) => queue.Tick();

    private void OnDutyStarted(object? sender, ushort territoryType)
    {
        dutyStartTick = Environment.TickCount64;
        Announce("開始", Config.StartMessage, Config.StartChannelRow, territoryType);
    }

    private void OnDutyCompleted(object? sender, ushort territoryType)
    {
        Announce("結束", Config.EndMessage, Config.EndChannelRow, territoryType);
    }

    /// <summary>
    /// 換區時把還沒送出的播報倒掉。
    /// </summary>
    /// <remarks>
    /// 🔴 沒有這一段的話，「通關訊息還在等延遲時就先傳送出本」會讓那句話送到副本外面
    /// （多半是某個公開頻道）。上游對它的自動退本佇列也做了同一件事。
    /// </remarks>
    private void OnTerritoryChanged(ushort territoryType)
    {
        if (queue.IsBusy)
        {
            Svc.Log.Information($"[{InternalName}] 已離開副本區域，取消尚未送出的播報。");
            queue.Abort();
        }

        dutyStartTick = 0;
    }

    private void Announce(string phase, string template, uint channelRow, ushort territoryType)
    {
        if (!HasMessage(template)) return;

        var channel = TextCommands.Resolve(channelRow);
        if (channel == null)
        {
            // fail closed：查不到頻道指令就什麼都不送，不要拿猜的字面值去賭。
            Svc.Log.Information(
                $"[{InternalName}] 副本{phase}播報取消：TextCommand 第 {channelRow} 列查不到可用的頻道指令。");
            return;
        }

        string line;
        try
        {
            line = BuildLine(template, channel, territoryType);
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, $"[{InternalName}] 組出副本{phase}播報內容失敗");
            return;
        }

        Svc.Log.Information($"[{InternalName}] 副本{phase}，{Config.DelayMs}ms 後送出：{line}");

        queue.EnqueueDelay(Config.DelayMs, $"等待 {Config.DelayMs}ms");
        queue.Enqueue($"送出{phase}播報", () => { ChatSender.ExecuteCommand(line); });
    }

    /// <summary>把樣板組成真的要送出去的一行。</summary>
    /// <remarks>
    /// 📌 樣板本身以 <c>/</c> 開頭時<b>直接當成一條完整指令送</b>，不再套頻道——
    /// 這讓「我要用 /mk 或自己的巨集」這種需求不必再開一個模組。
    /// 上游也是同樣的判斷。
    /// </remarks>
    private string BuildLine(string template, string channelCommand, ushort territoryType)
    {
        var text = ApplyPlaceholders(template.Trim(), territoryType);

        // ChatSender 會拒絕含換行的指令；設定 UI 是單行輸入框，但貼上時仍可能帶進換行。
        text = text.Replace('\r', ' ').Replace('\n', ' ').Trim();

        return text.StartsWith('/') ? text : $"{channelCommand} {text}";
    }

    private string ApplyPlaceholders(string template, ushort territoryType) => template
        .Replace("{duty}", ResolveDutyName(territoryType), StringComparison.OrdinalIgnoreCase)
        .Replace("{time}", DateTime.Now.ToString("HH:mm"), StringComparison.OrdinalIgnoreCase)
        .Replace("{elapsed}", DescribeElapsed(), StringComparison.OrdinalIgnoreCase);

    /// <summary>本場副本已經過的時間；不知道開始時刻時回「?」。</summary>
    private string DescribeElapsed()
    {
        if (dutyStartTick == 0) return "?";

        var elapsed = TimeSpan.FromMilliseconds(Environment.TickCount64 - dutyStartTick);
        if (elapsed < TimeSpan.Zero) return "?";

        return elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
            : $"{elapsed.Minutes:00}:{elapsed.Seconds:00}";
    }

    /// <summary>由區域編號查副本名；查不到時回「?」而不是空字串。</summary>
    /// <remarks>
    /// ⚠️ 回空字串的話訊息會變成「已通關　（用了 12:34）」這種看起來只是排版怪的東西，
    /// 使用者不會意識到是查表失敗。回「?」才看得出來。
    /// </remarks>
    private static string ResolveDutyName(ushort territoryType)
    {
        if (DutyNameCache.TryGetValue(territoryType, out var cached)) return cached;

        var name = "?";
        try
        {
            foreach (var row in Svc.Data.GetExcelSheet<ContentFinderCondition>())
            {
                if (row.TerritoryType.RowId != territoryType) continue;

                var text = row.Name.ExtractText().Trim();
                if (text.Length == 0) continue;

                name = text;
                break;
            }
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, $"[DutyAnnounce] 由區域 {territoryType} 查副本名失敗");
        }

        DutyNameCache[territoryType] = name;
        return name;
    }

    private static bool HasMessage(string? template) => !string.IsNullOrWhiteSpace(template);

    public override void DrawConfig()
    {
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
        ImGui.TextDisabled(
            "訊息留空＝該時機不送任何東西。以 / 開頭的訊息會整行當成指令送出（不再套頻道）。\n" +
            "代入欄位：{duty}＝副本名、{time}＝現在時刻、{elapsed}＝本場已經過的時間（不知道時代入「?」）。");
        ImGui.PopTextWrapPos();

        ImGui.Spacing();
        DrawPhase("副本開始", "start",
                  () => Config.StartMessage, v => Config.StartMessage = v,
                  () => Config.StartChannelRow, v => Config.StartChannelRow = v,
                  "副本正式開始（開場結界消失）時送出。");

        ImGui.Spacing();
        DrawPhase("副本結束", "end",
                  () => Config.EndMessage, v => Config.EndMessage = v,
                  () => Config.EndChannelRow, v => Config.EndChannelRow = v,
                  "副本通關時送出。⚠️ 只有通關會觸發，中途退本或全滅都不會。");

        ImGui.Spacing();
        ImGui.Separator();

        ImGui.SetNextItemWidth(200f);
        var delay = Config.DelayMs;
        if (ImGui.SliderInt("觸發後先等（毫秒）", ref delay, 0, 10_000))
            Config.DelayMs = Math.Clamp(delay, 0, 60_000);
        if (ImGui.IsItemDeactivatedAfterEdit())
            Plugin.Instance.Config.Save();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("通關的瞬間畫面還在演出，太早送出的訊息容易被其他系統訊息淹掉。\n" +
                             "⚠️ 等待期間如果離開了副本，這次播報會被取消（免得送到副本外面去）。");

        if (queue.IsBusy)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"等待中：{queue.CurrentStep}");
        }
    }

    private void DrawPhase(
        string title, string id,
        Func<string> getMessage, Action<string> setMessage,
        Func<uint> getChannel, Action<uint> setChannel,
        string tooltip)
    {
        ImGui.TextUnformatted(title);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(tooltip);

        ImGui.SetNextItemWidth(-1f);
        var message = getMessage();
        if (ImGui.InputTextWithHint($"##{id}Msg", "留空＝不送　例：{duty} 開打（{time}）", ref message, 200))
            setMessage(message);
        if (ImGui.IsItemDeactivatedAfterEdit())
            Plugin.Instance.Config.Save();

        var channelRow = getChannel();
        var currentName = TextCommands.DisplayName(channelRow) ?? $"頻道 #{channelRow}";

        ImGui.SetNextItemWidth(200f);
        using (var combo = ImRaii.Combo($"頻道##{id}Channel", currentName))
        {
            if (combo)
            {
                foreach (var candidate in TextCommands.ChatChannelRows.AnnounceChoices)
                {
                    var name = TextCommands.DisplayName(candidate);
                    var command = TextCommands.Resolve(candidate);

                    // 查不到指令的頻道直接不列出來——列出來也只會選了之後什麼都不發生。
                    if (name == null || command == null) continue;

                    var label = candidate == TextCommands.ChatChannelRows.Echo
                        ? $"{name}（{command}，只有自己看得到）"
                        : $"{name}（{command}）";

                    if (!ImGui.Selectable(label, candidate == channelRow)) continue;

                    setChannel(candidate);
                    Plugin.Instance.Config.Save();
                }
            }
        }

        if (HasMessage(message) && channelRow != TextCommands.ChatChannelRows.Echo)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(1f, 0.75f, 0.4f, 1f), "其他玩家看得到");
        }

        // 預覽：把樣板套上目前所在區域，讓人看得到真的會送出去的那一行。
        var preview = BuildPreview(message, channelRow);
        if (preview.Length > 0)
        {
            ImGui.TextDisabled($"送出內容：{preview}");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("以「目前所在區域」代入 {duty} 的預覽。\n真正送出時代入的是那一場副本的名字。");
        }
    }

    /// <summary>設定畫面的預覽行；任何失敗都回空字串（Draw 路徑不得擲例外）。</summary>
    private string BuildPreview(string template, uint channelRow)
    {
        if (!HasMessage(template)) return string.Empty;

        try
        {
            var channel = TextCommands.Resolve(channelRow);
            if (channel == null) return "（查不到這個頻道的指令，不會送出）";

            return BuildLine(template, channel, Svc.ClientState.TerritoryType);
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, $"[{InternalName}] 產生播報預覽失敗");
            return string.Empty;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface.Utility.Raii;
using Lumina.Excel.Sheets;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 聊天座標自動開地圖：聊天訊息裡出現地圖座標連結時，自動把地圖開到那個座標並插上旗標。
/// </summary>
/// <remarks>
/// <para>
/// 📌 <b>只做「開地圖」這一件事。</b>移植自 PandorasBox <c>AutoOpenCoords</c>，但刻意<b>不</b>移植：
/// <list type="bullet">
/// <item>同一份上游功能組裡的 <c>AutoTPCoords</c>（自動傳送到座標）—— 已裁決不做。</item>
/// <item>上游的 Sonar 過濾（比對發話者是不是字串 <c>"sonar"</c>）—— 那是英文外掛的訊息來源，台服沒有意義。</item>
/// <item>上游的「忽略 &lt;pos&gt; 旗標」（判斷訊息裡有沒有 <c>"Z:"</c>）—— 靠英文訊息格式，台服會靜默失效。</item>
/// </list>
/// </para>
/// <para>
/// 🔑 開地圖走 Dalamud 的 <c>IGameGui.OpenMapWithMapLink</c>（內部是
/// <c>RaptureAtkModule::OpenMapWithMapLink</c>，與點擊聊天視窗裡的座標連結同一條路徑），
/// <b>不碰任何原生指標、不寫記憶體</b>。它本身就會插旗標，所以不需要另外呼叫 <c>SetFlagMapMarker</c>。
/// </para>
/// <para>
/// 📌 頻道名稱<b>從遊戲的 <c>LogFilter</c> 表現讀</b>，不寫死中文。
/// ⚠️ <c>LogFilter.LogKind</c> 對頻道是 <b>N:1</b>（同一個 LogKind 可能有多列），
/// 所以<b>只在唯一命中時</b>才用遊戲的名字，其餘用內建字串——這是為了避免拿到別的列的名字。
/// （2026-08-19 離線核對台服 7.20 <c>LogFilter.csv</c>：本模組列出的頻道裡，
/// 只有「悄悄話（收到）」的 LogKind 13 在表裡沒有對應列，其餘全部唯一。）
/// </para>
/// </remarks>
public sealed class ChatCoordsOpenMap : TcModule
{
    public override string InternalName => "ChatCoordsOpenMap";
    public override string DisplayName => "聊天座標自動開地圖";

    public override string Description =>
        "聊天訊息裡出現地圖座標連結時自動開啟地圖並插上旗標（等同幫你點一下那個連結）。" +
        "可以逐頻道關閉；短時間內重複的同一個座標只會開一次。不做自動傳送。";

    public override ModuleCategory Category => ModuleCategory.Misc;

    public override bool HasConfigUI => true;

    /// <summary>
    /// 設定畫面上列出來的頻道（顯示順序即此順序）。
    /// </summary>
    /// <remarks>
    /// 📌 刻意<b>不</b>用 <c>Enum.GetValues&lt;XivChatType&gt;()</c> 全列：那會列出幾十個
    /// 系統／戰鬥記錄類型，而那些訊息裡不會有玩家貼的座標，只是把設定畫面淹掉。
    /// </remarks>
    private static readonly (XivChatType Type, string Fallback)[] Channels =
    [
        (XivChatType.Say, "說話"),
        (XivChatType.Shout, "喊話"),
        (XivChatType.Yell, "呼喊"),
        (XivChatType.TellIncoming, "悄悄話（收到）"),
        (XivChatType.TellOutgoing, "悄悄話（送出）"),
        (XivChatType.Party, "小隊"),
        (XivChatType.CrossParty, "跨界小隊"),
        (XivChatType.Alliance, "團隊"),
        (XivChatType.FreeCompany, "公會"),
        (XivChatType.NoviceNetwork, "新人頻道"),
        (XivChatType.PvPTeam, "戰隊"),
        (XivChatType.Ls1, "通訊貝1"),
        (XivChatType.Ls2, "通訊貝2"),
        (XivChatType.Ls3, "通訊貝3"),
        (XivChatType.Ls4, "通訊貝4"),
        (XivChatType.Ls5, "通訊貝5"),
        (XivChatType.Ls6, "通訊貝6"),
        (XivChatType.Ls7, "通訊貝7"),
        (XivChatType.Ls8, "通訊貝8"),
        (XivChatType.CrossLinkShell1, "跨界通訊貝1"),
        (XivChatType.CrossLinkShell2, "跨界通訊貝2"),
        (XivChatType.CrossLinkShell3, "跨界通訊貝3"),
        (XivChatType.CrossLinkShell4, "跨界通訊貝4"),
        (XivChatType.CrossLinkShell5, "跨界通訊貝5"),
        (XivChatType.CrossLinkShell6, "跨界通訊貝6"),
        (XivChatType.CrossLinkShell7, "跨界通訊貝7"),
        (XivChatType.CrossLinkShell8, "跨界通訊貝8"),
        (XivChatType.Echo, "默語"),
    ];

    private readonly record struct RecentLink(uint TerritoryId, int RawX, int RawY, DateTime At);

    private readonly List<RecentLink> recent = [];

    /// <summary>頻道名稱快取（只在唯一命中時才是遊戲的名字，見類別註解）。</summary>
    private readonly Dictionary<XivChatType, string> channelNames = [];

    private ChatCoordsOpenMapConfig Config => Plugin.Instance.Config.ChatCoordsOpenMap;

    protected override void OnEnable()
    {
        Svc.Chat.ChatMessage += OnChatMessage;
        Svc.Log.Information(
            $"[{InternalName}] 模組啟用：忽略 {Config.IgnoredChannels.Count} 個頻道、"
            + $"重複判定 {Config.DedupeSeconds} 秒、副本中略過＝{Config.SkipWhileBoundByDuty}");
    }

    protected override void OnDisable()
    {
        Svc.Chat.ChatMessage -= OnChatMessage;
        recent.Clear();
    }

    private void OnChatMessage(
        XivChatType type,
        int timestamp,
        ref SeString sender,
        ref SeString message,
        ref bool isHandled)
    {
        // 🔴 這是遊戲主執行緒上的回呼，擲例外會一路往上；整段包起來，最壞只寫一行記錄。
        try
        {
            HandleMessage(type, message);
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, $"[{InternalName}] 處理聊天訊息時發生例外（已忽略這一則）");
        }
    }

    private void HandleMessage(XivChatType type, SeString message)
    {
        if (Config.IgnoredChannels.Contains((ushort)type)) return;

        MapLinkPayload? link = null;
        foreach (var payload in message.Payloads)
        {
            if (payload is not MapLinkPayload mapLink) continue;
            link = mapLink;
            break;
        }

        if (link == null) return;

        if (Config.SkipWhileBoundByDuty && Svc.Condition[ConditionFlag.BoundByDuty])
        {
            Svc.Log.Information($"[{InternalName}] 副本中，略過一個座標連結（{link.PlaceName}）。");
            return;
        }

        var territoryId = link.TerritoryType.RowId;
        if (IsDuplicate(territoryId, link.RawX, link.RawY)) return;

        if (!Svc.GameGui.OpenMapWithMapLink(link))
        {
            Svc.Log.Information($"[{InternalName}] 遊戲拒絕開啟地圖（{link.PlaceName} {link.CoordinateString}）。");
            return;
        }

        Svc.Log.Information(
            $"[{InternalName}] 已開啟地圖：{ChannelName(type)}／{link.PlaceName} {link.CoordinateString}");

        if (Config.AnnounceInChat)
            Svc.Chat.Print($"[TC Toolbox] 已開啟地圖：{link.PlaceName} {link.CoordinateString}");
    }

    /// <summary>
    /// 同一個座標在冷卻時間內只處理一次。
    /// </summary>
    /// <remarks>
    /// 📌 判準用<b>原始整數座標</b>（<c>RawX</c>／<c>RawY</c>）而不是換算後的浮點數，
    /// 也不是訊息文字——同一則座標被不同人轉貼時文字不同、座標一樣，那正是要擋掉的情況。
    /// </remarks>
    private bool IsDuplicate(uint territoryId, int rawX, int rawY)
    {
        var now = DateTime.UtcNow;
        var window = TimeSpan.FromSeconds(Math.Max(0, Config.DedupeSeconds));

        for (var i = recent.Count - 1; i >= 0; i--)
        {
            if (now - recent[i].At > window)
            {
                recent.RemoveAt(i);
                continue;
            }

            if (recent[i].TerritoryId == territoryId && recent[i].RawX == rawX && recent[i].RawY == rawY)
                return true;
        }

        recent.Add(new RecentLink(territoryId, rawX, rawY, now));
        return false;
    }

    /// <summary>
    /// 頻道的顯示名稱。
    /// </summary>
    /// <remarks>
    /// 🔴 <c>LogFilter</c> 的 <c>LogKind</c> 對頻道是 N:1，<b>只有唯一命中時才採用遊戲的名字</b>；
    /// 命中 0 個或 2 個以上一律退回內建字串。多命中時採第一列的話，會拿到某個子分類的名字
    /// （例如「自己的…／他人的…」），而那是一個看起來很正常的錯答案。
    /// </remarks>
    private string ChannelName(XivChatType type)
    {
        if (channelNames.TryGetValue(type, out var cached)) return cached;

        var fallback = type.ToString();
        foreach (var (channelType, text) in Channels)
        {
            if (channelType != type) continue;
            fallback = text;
            break;
        }

        var result = fallback;
        try
        {
            var sheet = Svc.Data.GetExcelSheet<LogFilter>();
            var hits = 0;
            var name = string.Empty;

            foreach (var row in sheet)
            {
                if (row.LogKind != (byte)type) continue;

                var text = row.Name.ExtractText();
                if (string.IsNullOrWhiteSpace(text)) continue;

                hits++;
                name = text;
                if (hits > 1) break;
            }

            if (hits == 1) result = name;
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, $"[{InternalName}] 讀取 LogFilter 失敗，頻道名改用內建字串");
        }

        channelNames[type] = result;
        return result;
    }

    public override void DrawConfig()
    {
        ImGui.TextDisabled("只做「開啟地圖並插旗標」，不做自動傳送。");

        var announce = Config.AnnounceInChat;
        if (ImGui.Checkbox("開啟地圖後在聊天視窗留一行", ref announce))
        {
            Config.AnnounceInChat = announce;
            Plugin.Instance.Config.Save();
        }

        var skipDuty = Config.SkipWhileBoundByDuty;
        if (ImGui.Checkbox("副本中不自動開地圖", ref skipDuty))
        {
            Config.SkipWhileBoundByDuty = skipDuty;
            Plugin.Instance.Config.Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("戰鬥中被彈出地圖很干擾；關掉這格的話副本裡也會照開。");

        ImGui.SetNextItemWidth(160f);
        var dedupe = Config.DedupeSeconds;
        if (ImGui.SliderInt("重複座標的冷卻（秒）", ref dedupe, 0, 60))
        {
            Config.DedupeSeconds = dedupe;
            Plugin.Instance.Config.Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("同一個座標在這段時間內再出現就不再開一次（多人轉貼同一個點時很常見）。");

        if (!ImGui.CollapsingHeader("頻道（取消勾選＝該頻道不處理）")) return;

        using var indent = ImRaii.PushIndent();
        using var child = ImRaii.Child("TCToolboxCoordsChannels",
                                       new Vector2(ImGui.GetContentRegionAvail().X, 220f), true);
        if (!child) return;

        foreach (var (type, _) in Channels)
        {
            using var id = ImRaii.PushId((int)type);

            var enabled = !Config.IgnoredChannels.Contains((ushort)type);
            if (!ImGui.Checkbox(ChannelName(type), ref enabled)) continue;

            if (enabled)
                Config.IgnoredChannels.Remove((ushort)type);
            else
                Config.IgnoredChannels.Add((ushort)type);

            Plugin.Instance.Config.Save();
        }
    }
}

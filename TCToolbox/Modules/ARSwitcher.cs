using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// AutoRetainer 角色切換：伺服器資訊列顯示目前是第幾個角色，並提供切換指令。
/// </summary>
/// <remarks>
/// 🔴🔴 <b>絕不呼叫空參數的 <c>/li</c>。</b>上游 CBT 的 <c>ARSwitcher</c> 在「目前不在原伺服器」時
/// 會直接 <c>ProcessCommand("/li")</c>——那是 Lifestream 的跨世界傳送，空參數等於把角色
/// 送到別的伺服器去。這裡完全不做「回原伺服器」這件事：不在原伺服器時就<b>只是不給切</b>，
/// 並把原因說出來。
/// </remarks>
public sealed class ARSwitcher : TcModule
{
    public override string InternalName => "ARSwitcher";

    public override string DisplayName => "AutoRetainer 角色切換";

    public override string Description =>
        "在伺服器資訊列顯示目前是 AutoRetainer 清單裡的第幾個角色，並提供 /tcnext、/tcprev、" +
        "/tcswitch 三個切換指令。純手動：不掛任何自動接手事件，也不會自己換角色。" +
        "未安裝 AutoRetainer 時資訊列不顯示。";

    public override ModuleCategory Category => ModuleCategory.Company;

    /// <inheritdoc/>
    /// <remarks>開著但不下指令（且沒開啟點擊切換）＝遊戲行為完全不變。</remarks>
    public override bool IsManualTrigger => true;

    public override bool HasConfigUI => true;

    private const string NextCommand = "/tcnext";
    private const string PrevCommand = "/tcprev";
    private const string SwitchCommand = "/tcswitch";

    private const BitmapFontIcon DtrIcon = BitmapFontIcon.Returner;

    private ARSwitcherConfig Config => Plugin.Instance.Config.ArSwitcher;

    private IDtrBarEntry? dtrEntry;

    /// <summary>一位 AutoRetainer 已登記角色。</summary>
    private readonly record struct CharacterEntry(ulong Cid, string Name, string World)
    {
        public string FullName => $"{Name}@{World}";
    }

    /// <summary>目前的角色清單（每次輪詢重建；只存數值與字串，不存任何指標）。</summary>
    private readonly List<CharacterEntry> characters = [];

    /// <summary>目前角色在 <see cref="characters"/> 裡的索引；-1＝不在清單上。</summary>
    private int currentIndex = -1;

    private bool autoRetainerAvailable;

    /// <summary>名稱反射有沒有成功——決定前／後切換能不能用。</summary>
    private bool namesResolved;

    private const string VentureThrottleKey = "ARSwitcher-Venture";

    /// <summary>探險時間只在派遣／收回時變：30 秒問一次，兩次之間用本機時鐘往下扣。</summary>
    private const int VenturePollMs = 30_000;

    /// <summary>資訊列說明裡最多列出幾位角色的探險時間。</summary>
    private const int VenturePreviewCount = 15;

    /// <summary>
    /// 各角色「最早回來的那位僱員」在取樣當下還剩幾秒；值 null＝AutoRetainer 答沒有進行中的探險。
    /// </summary>
    /// <remarks>Update 整份換掉，繪製只讀參考。字典裡沒有這個 CID＝還沒問到（畫「?」）。</remarks>
    private IReadOnlyDictionary<ulong, long?> ventureSeconds = new Dictionary<ulong, long?>();

    /// <summary>上一輪取樣的 <see cref="Environment.TickCount64"/>。</summary>
    private long ventureSampledAt;

    /// <summary>探險端點打不打得通；null＝還沒問過。</summary>
    private bool? ventureReachable;

    /// <summary>設定畫面按下「切換」後，留給下一次 Update 執行的目標（IPC 不在繪製時打）。</summary>
    private string? pendingSwitch;

    /// <summary>探險表的一列（排序與顯示共用）。</summary>
    private readonly record struct VentureRow(CharacterEntry Entry, int Index, bool Asked, long? Remaining);

    protected override void OnEnable()
    {
        characters.Clear();
        currentIndex = -1;
        autoRetainerAvailable = false;
        namesResolved = false;
        ResetVentures();
        pendingSwitch = null;

        dtrEntry = Svc.DtrBar.Get("TC Toolbox 角色切換");
        dtrEntry.Shown = false;
        dtrEntry.Text = new SeString(new IconPayload(DtrIcon), new TextPayload("?"));
        dtrEntry.Tooltip = "TC Toolbox — AutoRetainer 角色切換";
        dtrEntry.OnClick = OnDtrClick;

        Svc.Commands.AddHandler(NextCommand, new CommandInfo(OnNextCommand)
        {
            HelpMessage = "切換到 AutoRetainer 清單裡的下一個角色",
        });

        Svc.Commands.AddHandler(PrevCommand, new CommandInfo(OnPrevCommand)
        {
            HelpMessage = "切換到 AutoRetainer 清單裡的上一個角色",
        });

        Svc.Commands.AddHandler(SwitchCommand, new CommandInfo(OnSwitchCommand)
        {
            HelpMessage = "切換到指定角色：/tcswitch 名稱@伺服器",
        });

        Svc.Framework.Update += OnUpdate;
    }

    protected override void OnDisable()
    {
        Svc.Framework.Update -= OnUpdate;

        Svc.Commands.RemoveHandler(NextCommand);
        Svc.Commands.RemoveHandler(PrevCommand);
        Svc.Commands.RemoveHandler(SwitchCommand);

        dtrEntry?.Remove();
        dtrEntry = null;

        characters.Clear();
        currentIndex = -1;
        autoRetainerAvailable = false;
        namesResolved = false;
        ResetVentures();
        pendingSwitch = null;
    }

    private void OnUpdate(IFramework framework)
    {
        if (dtrEntry == null) return;

        if (pendingSwitch is { } target)
        {
            pendingSwitch = null;
            DoSwitch(target);
        }

        // 角色清單幾乎不變，兩秒一次遠遠夠用；而且每次都要跨外掛 IPC，不該每幀做。
        if (!Throttle.Pass("ARSwitcher-Poll", 2_000)) return;

        Rescan();
        if (Config.ShowVentureCountdown) PollVentures();
        UpdateDtr();
    }

    private void ResetVentures()
    {
        ventureSeconds = new Dictionary<ulong, long?>();
        ventureSampledAt = 0;
        ventureReachable = null;
        Throttle.Reset(VentureThrottleKey);
    }

    /// <summary>向 AutoRetainer 問每個角色最早回來的僱員，整份換掉 <see cref="ventureSeconds"/>。</summary>
    private void PollVentures()
    {
        if (!autoRetainerAvailable || !Throttle.Pass(VentureThrottleKey, VenturePollMs)) return;

        var map = new Dictionary<ulong, long?>();
        foreach (var c in characters)
        {
            if (!AutoRetainerIpc.TryGetClosestVentureSeconds(c.Cid, out var seconds))
            {
                ventureReachable = false;
                ventureSeconds = new Dictionary<ulong, long?>();
                return;
            }

            map[c.Cid] = seconds;
        }

        ventureSeconds = map;
        ventureSampledAt = Environment.TickCount64;
        ventureReachable = true;
    }

    /// <summary>取樣值扣掉取樣後經過的時間。負數＝已經回來了。</summary>
    private long RemainingNow(long sampledSeconds) =>
        sampledSeconds - (Environment.TickCount64 - ventureSampledAt) / 1000;

    /// <summary>目前角色以外、最早有僱員回來的角色（讀不到名稱的不算：組不出切換要的字串）。</summary>
    private bool TryGetEarliestOther(out CharacterEntry entry, out long remaining)
    {
        entry = default;
        remaining = long.MaxValue;
        var found = false;
        var snapshot = ventureSeconds;

        for (var i = 0; i < characters.Count; i++)
        {
            var c = characters[i];
            if (i == currentIndex || c.Name.Length == 0) continue;
            if (!snapshot.TryGetValue(c.Cid, out var seconds) || seconds is not { } sampled) continue;

            var r = RemainingNow(sampled);
            if (r >= remaining) continue;

            remaining = r;
            entry = c;
            found = true;
        }

        return found;
    }

    /// <summary>最早的排前面；「沒有進行中的探險」其次；還沒問到的（畫「?」）最後。</summary>
    private List<VentureRow> BuildVentureRows()
    {
        var snapshot = ventureSeconds;
        var rows = new List<VentureRow>(characters.Count);
        for (var i = 0; i < characters.Count; i++)
        {
            var c = characters[i];
            var asked = snapshot.TryGetValue(c.Cid, out var seconds);
            rows.Add(new VentureRow(c, i, asked, seconds is { } sampled ? RemainingNow(sampled) : (long?)null));
        }

        return rows
            .OrderBy(r => !r.Asked ? 2 : r.Remaining == null ? 1 : 0)
            .ThenBy(r => r.Remaining ?? long.MaxValue)
            .ToList();
    }

    /// <summary>「探險歸來」或「X小時Y分」；不到一分鐘算一分鐘。</summary>
    private static string DescribeVenture(long remaining)
    {
        if (remaining <= 0) return "探險歸來";

        var minutes = (remaining + 59) / 60;
        return minutes >= 60 ? $"{minutes / 60}小時{minutes % 60}分" : $"{minutes}分";
    }

    /// <summary>資訊列上接在「第幾個／共幾個」後面的那一小段。「不知道」畫「?」。</summary>
    private string VentureBarText()
    {
        if (ventureReachable != true) return "?";
        return TryGetEarliestOther(out _, out var remaining) ? DescribeVenture(remaining) : "—";
    }

    /// <summary>資訊列說明裡「各角色最早回來的僱員」那一段；選項關著時是空字串。</summary>
    private string BuildVentureTooltip()
    {
        if (!Config.ShowVentureCountdown) return string.Empty;

        var sb = new StringBuilder("\n\n各角色最早回來的僱員（每 30 秒向 AutoRetainer 詢問一次）：");
        if (ventureReachable == null) return sb.Append("\n還沒問過，幾秒內會更新。").ToString();
        if (ventureReachable == false) return sb.Append("\n讀不到（AutoRetainer 版本太舊或未載入）。").ToString();

        var hasEarliest = TryGetEarliestOther(out var earliest, out _);
        var rows = BuildVentureRows();
        var shown = Math.Min(rows.Count, VenturePreviewCount);
        for (var i = 0; i < shown; i++)
        {
            var row = rows[i];
            sb.Append('\n').Append(Describe(row.Entry));
            if (row.Index == currentIndex) sb.Append("（目前）");

            sb.Append('：').Append(!row.Asked ? "?"
                : row.Remaining is { } rem ? DescribeVenture(rem)
                : "沒有進行中的探險");

            if (hasEarliest && row.Entry.Cid == earliest.Cid) sb.Append("　※最早");
        }

        if (rows.Count > shown)
            sb.Append($"\n…等共 {rows.Count} 位");

        if (hasEarliest)
            sb.Append("\n在 TC Toolbox 設定裡可以一鍵切換到最早回來的角色。");

        return sb.ToString();
    }

    private void Rescan()
    {
        characters.Clear();
        currentIndex = -1;
        namesResolved = false;

        autoRetainerAvailable = AutoRetainerIpc.IsAvailable();
        if (!autoRetainerAvailable) return;

        var cids = AutoRetainerIpc.GetRegisteredCharacters();
        if (cids.Count == 0) return;

        var localCid = Svc.PlayerState.ContentId;
        var resolvedAll = true;

        foreach (var cid in cids)
        {
            if (AutoRetainerIpc.TryGetCharacterName(cid, out var name, out var world))
            {
                characters.Add(new CharacterEntry(cid, name, world));
            }
            else
            {
                resolvedAll = false;

                // 名字讀不到也要保留這一位，否則「第幾個／共幾個」會算錯。
                characters.Add(new CharacterEntry(cid, string.Empty, string.Empty));
            }

            if (cid == localCid)
                currentIndex = characters.Count - 1;
        }

        namesResolved = resolvedAll;
    }

    private void UpdateDtr()
    {
        if (dtrEntry == null) return;

        // 🔴 未安裝 AutoRetainer 就完全不顯示（不是「不知道」，是這個功能根本不適用）。
        if (!autoRetainerAvailable || characters.Count == 0)
        {
            dtrEntry.Shown = false;
            return;
        }

        dtrEntry.Shown = true;

        // ⚠️「不知道」要看得見：目前角色不在 AutoRetainer 清單上時顯示 ?/N，不要顯示 0/N。
        var label = currentIndex >= 0
            ? $"{currentIndex + 1}/{characters.Count}"
            : $"?/{characters.Count}";

        if (Config.ShowVentureCountdown)
            label += "　" + VentureBarText();

        dtrEntry.Text = new SeString(new IconPayload(DtrIcon), new TextPayload(label));

        var sb = new StringBuilder();
        sb.Append("TC Toolbox — AutoRetainer 角色切換\n");

        if (currentIndex < 0)
        {
            sb.Append("\n目前角色不在 AutoRetainer 的登記清單裡。");
        }
        else
        {
            var current = characters[currentIndex];
            sb.Append($"\n目前：{Describe(current)}");

            if (TryGetNeighbour(1, out var next))
                sb.Append($"\n下一個：{Describe(next)}");

            if (TryGetNeighbour(-1, out var prev))
                sb.Append($"\n上一個：{Describe(prev)}");
        }

        if (!namesResolved)
            sb.Append("\n\n⚠ 讀不到部分角色名稱，前／後切換無法使用。");

        sb.Append(BuildVentureTooltip());

        sb.Append(Config.SwitchOnDtrClick
            ? "\n\n左鍵：下一個角色\n右鍵：上一個角色"
            : "\n\n左鍵／右鍵：開啟／關閉 TC Toolbox 設定");

        dtrEntry.Tooltip = sb.ToString();
    }

    private static string Describe(in CharacterEntry entry)
        => entry.Name.Length > 0 ? entry.FullName : $"（讀不到名稱，CID {entry.Cid:X}）";

    /// <summary>取得相對目前角色偏移 <paramref name="direction"/> 的角色（環狀）。</summary>
    /// <remarks>📌 名稱讀不到的角色不能當目標——沒有名字就組不出 <c>Relog</c> 要的字串。</remarks>
    private bool TryGetNeighbour(int direction, out CharacterEntry entry)
    {
        entry = default;

        if (currentIndex < 0 || characters.Count < 2) return false;

        // 最多繞一圈，跳過讀不到名字的與自己。
        for (var step = 1; step < characters.Count; step++)
        {
            var index = ((currentIndex + direction * step) % characters.Count + characters.Count)
                        % characters.Count;

            if (index == currentIndex) continue;

            var candidate = characters[index];
            if (candidate.Name.Length == 0) continue;

            entry = candidate;
            return true;
        }

        return false;
    }

    private void OnDtrClick(DtrInteractionEvent ev)
    {
        // 📌 預設不讓點擊直接切角色：切換＝登出，手滑的代價太高。
        if (!Config.SwitchOnDtrClick)
        {
            Plugin.Instance.ToggleMainWindow();
            return;
        }

        SwitchRelative(ev.ClickType == MouseClickType.Right ? -1 : 1);
    }

    private void OnNextCommand(string command, string arguments) => SwitchRelative(1);

    private void OnPrevCommand(string command, string arguments) => SwitchRelative(-1);

    private void OnSwitchCommand(string command, string arguments)
    {
        var target = arguments.Trim();

        if (target.Length == 0)
        {
            Svc.Chat.Print($"[TC Toolbox] 用法：{SwitchCommand} 名稱@伺服器");
            return;
        }

        if (!target.Contains('@'))
        {
            Svc.Chat.PrintError($"[TC Toolbox] 請用「名稱@伺服器」的格式，例如：{SwitchCommand} 光之戰士@紅玉海");
            return;
        }

        DoSwitch(target);
    }

    private void SwitchRelative(int direction)
    {
        // 每次切換前重新抓一次，不要用最多可能兩秒前的快取去決定要登出到哪裡。
        Rescan();

        if (!autoRetainerAvailable)
        {
            Svc.Chat.PrintError("[TC Toolbox] 未偵測到 AutoRetainer，無法切換角色。");
            return;
        }

        if (currentIndex < 0)
        {
            Svc.Chat.PrintError("[TC Toolbox] 目前角色不在 AutoRetainer 的登記清單裡。");
            return;
        }

        if (!TryGetNeighbour(direction, out var target))
        {
            Svc.Chat.PrintError(namesResolved
                ? "[TC Toolbox] 找不到可以切換過去的角色。"
                : "[TC Toolbox] 讀不到 AutoRetainer 的角色名稱，前／後切換無法使用（可用 /tcswitch 名稱@伺服器）。");
            return;
        }

        DoSwitch(target.FullName);
    }

    private void DoSwitch(string charaNameWithWorld) => CharacterSwitch.Request(charaNameWithWorld, InternalName);

    public override void DrawConfig()
    {
        if (!autoRetainerAvailable)
        {
            ImGui.TextColored(new Vector4(1f, 0.5f, 0.4f, 1f), "未偵測到 AutoRetainer —— 這個模組不會有任何作用。");
            ImGui.TextDisabled("資訊列項目在未安裝 AutoRetainer 時不會顯示。");
            return;
        }

        ImGui.TextDisabled($"已登記角色 {characters.Count} 位；目前是第 " +
                           (currentIndex >= 0 ? $"{currentIndex + 1} 位。" : "? 位（不在清單上）。"));

        if (!namesResolved)
        {
            ImGui.TextColored(new Vector4(1f, 0.85f, 0.35f, 1f),
                "⚠ 讀不到部分角色名稱，前／後切換無法使用。");
            ImGui.TextDisabled($"仍然可以用 {SwitchCommand} 名稱@伺服器 指定切換。");
        }

        ImGui.Separator();

        ImGui.TextUnformatted($"{NextCommand}：下一個角色");
        ImGui.TextUnformatted($"{PrevCommand}：上一個角色");
        ImGui.TextUnformatted($"{SwitchCommand} 名稱@伺服器：指定角色");

        ImGui.Separator();

        var switchOnClick = Config.SwitchOnDtrClick;
        if (ImGui.Checkbox("點擊資訊列項目就切換角色##arSwitcherClick", ref switchOnClick))
        {
            Config.SwitchOnDtrClick = switchOnClick;
            Plugin.Instance.Config.Save();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "開啟後：左鍵＝下一個角色、右鍵＝上一個角色。\n" +
                "⚠ 切換角色等於登出再登入，而這顆圖示就在時鐘旁邊——\n" +
                "預設關閉是為了避免手滑點到就被登出。\n" +
                "關閉時點擊只會開啟／關閉 TC Toolbox 設定視窗。");
        }

        ImGui.TextDisabled("戰鬥中、副本中、事件中或不在原伺服器時都不會切換。");
        ImGui.TextDisabled("不會自己換角色：沒有掛任何自動接手事件。");

        ImGui.Separator();

        var showVenture = Config.ShowVentureCountdown;
        if (ImGui.Checkbox("顯示各角色的僱員還要多久回來##arSwitcherVenture", ref showVenture))
        {
            Config.ShowVentureCountdown = showVenture;
            Plugin.Instance.Config.Save();
            ResetVentures();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "每 30 秒向 AutoRetainer 詢問一次：每個角色「最早回來的那位僱員」還要多久。\n" +
                "只算有勾選給 AutoRetainer 管理的僱員。\n" +
                "資訊列會多顯示其他角色裡最早的那一位，滑鼠移上列出每個角色；下面可以一鍵切換過去。\n" +
                "只顯示，不會自己切換角色。");
        }

        if (Config.ShowVentureCountdown)
            DrawVentures();
    }

    /// <summary>設定畫面上的各角色探險時間。只讀快照，不打 IPC（切換留給下一次 Update）。</summary>
    private void DrawVentures()
    {
        var reachable = ventureReachable;
        if (reachable == null)
        {
            ImGui.TextDisabled("還沒向 AutoRetainer 詢問過（幾秒內會更新）。");
            return;
        }

        if (reachable == false)
        {
            ImGui.TextColored(new Vector4(1f, 0.85f, 0.35f, 1f),
                "讀不到各角色的探險時間（AutoRetainer 版本太舊或未載入）。");
            return;
        }

        if (TryGetEarliestOther(out var earliest, out var remaining))
        {
            if (ImGui.Button(
                    $"切換到最早回來的角色：{earliest.FullName}（{DescribeVenture(remaining)}）##arSwitcherVentureSwitch"))
                pendingSwitch = earliest.FullName;

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    "請 AutoRetainer 切換過去（會登出目前角色）。\n" +
                    "戰鬥中、副本中、事件中、不在原伺服器，或 AutoRetainer 正在忙時不會切換。");
            }
        }
        else
        {
            ImGui.TextDisabled("其他角色都沒有進行中的探險。");
        }

        if (!ImGui.BeginTable("##arSwitcherVentures", 2,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.SizingStretchProp))
            return;

        ImGui.TableSetupColumn("角色", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("最早回來的僱員", ImGuiTableColumnFlags.WidthFixed, 140f);
        ImGui.TableHeadersRow();

        foreach (var row in BuildVentureRows())
        {
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.Index == currentIndex ? $"{Describe(row.Entry)}（目前）" : Describe(row.Entry));

            ImGui.TableNextColumn();
            if (!row.Asked)
            {
                ImGui.TextDisabled("?");
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("這個角色還沒問到，下一輪就會有。");
            }
            else if (row.Remaining is not { } rem)
            {
                ImGui.TextDisabled("—");
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip(
                        "AutoRetainer 說這個角色沒有進行中的探險\n" +
                        "（沒有派遣中的僱員，或僱員沒有勾選給 AutoRetainer 管理）。");
                }
            }
            else if (rem <= 0)
            {
                ImGui.TextColored(new Vector4(0.5f, 1f, 0.5f, 1f), DescribeVenture(rem));
            }
            else
            {
                ImGui.TextUnformatted(DescribeVenture(rem));
            }
        }

        ImGui.EndTable();
    }
}

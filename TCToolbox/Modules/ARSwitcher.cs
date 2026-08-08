using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.Game.ClientState.Conditions;
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
/// <para>
/// 🔴🔴 <b>絕不呼叫空參數的 <c>/li</c>。</b>上游 CBT 的 <c>ARSwitcher</c> 在「目前不在原伺服器」時
/// 會直接 <c>ProcessCommand("/li")</c>——那是 Lifestream 的跨世界傳送，空參數等於把角色
/// 送到別的伺服器去。這裡完全不做「回原伺服器」這件事：不在原伺服器時就<b>只是不給切</b>，
/// 並把原因說出來。
/// </para>
/// <para>
/// 🔴 <b>零自動化。</b>不註冊 AutoRetainer 的任何 post-process 事件——那會把本外掛接進
/// 「雇員作業做完 → 自動換下一個角色」的自動接手鏈，是艦隊紅線。
/// 每一次切換都必須來自使用者的指令或點擊。
/// </para>
/// <para>
/// ⚠️ <b>切換角色＝登出再登入。</b>所以預設<b>不</b>讓資訊列的點擊直接切換
/// （<see cref="ARSwitcherConfig.SwitchOnDtrClick"/> 預設 <c>false</c>）：
/// 那顆圖示就在時鐘旁邊，手滑點到的代價是整個角色被登出。
/// 想要上游那種一點就換的行為可以自己打開。
/// </para>
/// <para>
/// 📌 角色名稱透過反射從 AutoRetainer 的資料裡讀（本外掛零相依，不編進對方的型別）。
/// 讀不到時前／後切換會停用並在提示裡說明，但<b>指定名稱</b>的切換照樣可用——
/// 見 <see cref="AutoRetainerIpc"/>。
/// </para>
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

    /// <summary>資訊列圖示。</summary>
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

    protected override void OnEnable()
    {
        characters.Clear();
        currentIndex = -1;
        autoRetainerAvailable = false;
        namesResolved = false;

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
    }

    private void OnUpdate(IFramework framework)
    {
        if (dtrEntry == null) return;

        // 角色清單幾乎不變，兩秒一次遠遠夠用；而且每次都要跨外掛 IPC，不該每幀做。
        if (!Throttle.Pass("ARSwitcher-Poll", 2_000)) return;

        Rescan();
        UpdateDtr();
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

        sb.Append(Config.SwitchOnDtrClick
            ? "\n\n左鍵：下一個角色　右鍵：上一個角色"
            : "\n\n點擊：開啟 TC Toolbox 設定");

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

    private void DoSwitch(string charaNameWithWorld)
    {
        if (GetBlockedReason() is { } reason)
        {
            Svc.Chat.PrintError($"[TC Toolbox] 無法切換角色：{reason}");
            return;
        }

        if (!AutoRetainerIpc.TryRelog(charaNameWithWorld, out var accepted))
        {
            Svc.Chat.PrintError("[TC Toolbox] 呼叫 AutoRetainer 切換角色失敗。");
            return;
        }

        if (!accepted)
        {
            // AutoRetainer 拿這個字串比對不到自己的角色清單時就是這裡。
            Svc.Chat.PrintError(
                $"[TC Toolbox] AutoRetainer 沒有接受切換到「{charaNameWithWorld}」" +
                "（名稱不符，或它目前不允許自動登入）。");
            return;
        }

        // 使用者回報用的定錨點：切換角色會登出，出事時這一行是唯一能證明「是他自己要求的」的證據。
        Svc.Log.Information($"[{InternalName}] 使用者要求切換角色 → {charaNameWithWorld}");
        Svc.Chat.Print($"[TC Toolbox] 正在切換到「{charaNameWithWorld}」…");
    }

    /// <summary>
    /// 現在不能切換角色的原因；<c>null</c>＝可以。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>不在原伺服器時就是不給切</b>，而不是像上游那樣去呼叫 <c>/li</c> 把角色傳回去。
    /// 那個指令空參數是跨世界傳送，不是我們該替使用者做的決定。
    /// </remarks>
    private static string? GetBlockedReason()
    {
        if (Svc.Objects.LocalPlayer == null)
            return "目前不在遊戲中。";

        var player = Svc.Objects.LocalPlayer;
        if (player.HomeWorld.RowId != player.CurrentWorld.RowId)
            return "目前在其他伺服器（旅行中），請先自行回到原伺服器。";

        if (Svc.Condition[ConditionFlag.InCombat])
            return "戰鬥中。";

        if (Svc.Condition[ConditionFlag.BoundByDuty] || Svc.Condition[ConditionFlag.BoundByDuty56] ||
            Svc.Condition[ConditionFlag.BoundByDuty95] || Svc.Condition[ConditionFlag.InDutyQueue])
            return "正在副本中或排隊中。";

        if (Svc.Condition[ConditionFlag.WatchingCutscene] ||
            Svc.Condition[ConditionFlag.OccupiedInCutSceneEvent])
            return "正在播放過場動畫。";

        if (Svc.Condition[ConditionFlag.Occupied] || Svc.Condition[ConditionFlag.Occupied30] ||
            Svc.Condition[ConditionFlag.Occupied33] || Svc.Condition[ConditionFlag.Occupied38] ||
            Svc.Condition[ConditionFlag.Occupied39] || Svc.Condition[ConditionFlag.OccupiedInEvent] ||
            Svc.Condition[ConditionFlag.OccupiedSummoningBell] ||
            Svc.Condition[ConditionFlag.OccupiedInQuestEvent])
            return "正在進行中的事件裡。";

        if (AutoRetainerIpc.IsBusy())
            return "AutoRetainer 正在忙。";

        if (!AutoRetainerIpc.CanAutoLogin())
            return "AutoRetainer 目前不允許自動登入（請確認它的自動登入設定）。";

        return null;
    }

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
                "關閉時點擊只會開啟 TC Toolbox 設定視窗。");
        }

        ImGui.TextDisabled("戰鬥中、副本中、事件中或不在原伺服器時都不會切換。");
        ImGui.TextDisabled("不會自己換角色：沒有掛任何自動接手事件。");
    }
}

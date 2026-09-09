using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using TCToolbox.Core;
using TCToolbox.Windows;

namespace TCToolbox.Modules;

/// <summary>
/// 全艦隊急停：一個指令／一顆熱鍵／一顆大按鈕，把所有會自己動的外掛一次叫停。
/// </summary>
/// <remarks>
/// 🔴 <b>只停不啟。</b>這個模組沒有、也不要有「一鍵恢復」——急停的價值就在於它永遠只往
/// 安全的方向走。要恢復請各自到那些外掛裡開回來（那也順便強迫使用者確認自己真的想繼續）。
/// <para>
/// 📌 對象清單與端點出處全部寫在 <see cref="FleetStop"/>。這個檔只管觸發、顯示與存檔。
/// </para>
/// <para>
/// ⚠️ 這是<b>手動觸發</b>模組（<see cref="IsManualTrigger"/> 恆為 true）：開著但不去按它，
/// 遊戲行為完全不變。它唯一掛的東西是熱鍵輪詢，而熱鍵預設未綁定。
/// </para>
/// </remarks>
public sealed class FleetEmergencyStop : TcModule
{
    public const string Command = "/tcstopall";

    /// <summary>短別名。緊急的時候少打幾個字是有意義的。</summary>
    public const string CommandAlias = "/tcpanic";

    public override string InternalName => "FleetEmergencyStop";

    public override string DisplayName => "全艦隊急停";

    public override string Description =>
        "一個動作把所有會自己動的外掛同時叫停：先停會讓角色移動的（vnavmesh／Lifestream／AutoDuty／" +
        "Questionable／visland／BossmodReborn AI），再停自動化本體（WrathCombo／AutoRetainer／Artisan／" +
        "GatherBuddy Reborn／AutoHook／ICE／SomethingNeedDoing）。" +
        $"指令 {Command}（別名 {CommandAlias}），也可以綁一顆熱鍵。每個對象的結果分成成功／失敗／未安裝三態。";

    public override ModuleCategory Category => ModuleCategory.Misc;

    public override bool HasConfigUI => true;

    /// <summary>開著也不會自己動——它只在你按下去的那一刻做一次事。</summary>
    public override bool IsManualTrigger => true;

    private static FleetEmergencyStopConfig Config => Plugin.Instance.Config.FleetEmergencyStop;

    /// <summary>最後一次急停的結果（沒跑過就是空的）。只在主執行緒讀寫。</summary>
    private static readonly List<FleetStopResult> LastResults = [];

    /// <summary>最後一次急停的時間（UTC）；<see cref="DateTime.MinValue"/>＝這次還沒跑過。</summary>
    private static DateTime lastRunAt = DateTime.MinValue;

    private static readonly Vector4 OkColor = new(0.42f, 0.85f, 0.45f, 1f);
    private static readonly Vector4 FailColor = new(1f, 0.42f, 0.42f, 1f);
    private static readonly Vector4 MissingColor = new(0.62f, 0.62f, 0.62f, 1f);
    private static readonly Vector4 BigButtonColor = new(0.62f, 0.16f, 0.16f, 1f);
    private static readonly Vector4 BigButtonHoverColor = new(0.75f, 0.22f, 0.22f, 1f);

    private readonly HotkeyWatcher hotkey = new();

    private FleetStopWindow? window;

    /// <summary>
    /// 列上提示：跑過一次之後就把「上一次的結果」留在列上。
    /// </summary>
    /// <remarks>
    /// 🔑 沒跑過時<b>不顯示</b>而不是顯示 0——「還沒用過」與「用了但零成功」是兩件事，
    /// 畫成 0 會讓人以為急停失效了。
    /// </remarks>
    public override ModuleNotice? RowNotice
    {
        get
        {
            if (lastRunAt == DateTime.MinValue) return null;

            Count(out var ok, out var failed, out var missing);

            return new ModuleNotice(
                failed > 0 ? ModuleNoticeLevel.Warning : ModuleNoticeLevel.Unknown,
                failed > 0 ? $"上次急停：成功 {ok}、失敗 {failed}" : $"上次急停：成功 {ok}",
                $"{lastRunAt.ToLocalTime():HH:mm:ss} 執行。\n"
                + $"成功 {ok}、失敗 {failed}、未安裝 {missing}。\n"
                + "逐項結果在下方「設定」裡，或按「開啟結果視窗」。");
        }
    }

    protected override void OnEnable()
    {
        Svc.Commands.AddHandler(Command, new CommandInfo(OnCommand)
        {
            HelpMessage = "全艦隊急停：把所有會自己動的外掛一次叫停",
        });
        Svc.Commands.AddHandler(CommandAlias, new CommandInfo(OnCommand)
        {
            HelpMessage = $"{Command} 的別名",
        });

        // 🔴 借用共用的停止移動看門狗：它負責在按下急停之後的三秒內持續補送 vnavmesh 的停止，
        //    蓋住「路徑還在背景計算、算完才開走」那個窗口。沒有 Acquire 的話，
        //    FleetStop 裡的 NavStop.RequestStop 只會送出單獨一發 —— 而那正是這個看門狗存在的理由。
        NavStop.Acquire();

        window = new FleetStopWindow(this);
        Plugin.Instance.WindowSystem.AddWindow(window);

        hotkey.Reset();
        Svc.Framework.Update += OnUpdate;
    }

    protected override void OnDisable()
    {
        Svc.Framework.Update -= OnUpdate;
        hotkey.Reset();

        if (window != null)
        {
            // 🔴 WindowSystem.RemoveWindow 對「不在清單裡」的視窗會擲 ArgumentException，
            //    而外掛整個卸載時 Plugin.Dispose 是先 RemoveAllWindows() 再 module.Disable()
            //    —— 沒有這道檢查，每次卸載都會在記錄裡留下一行 Error。
            if (Plugin.Instance.WindowSystem.Windows.Contains(window))
                Plugin.Instance.WindowSystem.RemoveWindow(window);

            window.IsOpen = false;
            window = null;
        }

        NavStop.Release();

        Svc.Commands.RemoveHandler(CommandAlias);
        Svc.Commands.RemoveHandler(Command);
    }

    private void OnUpdate(IFramework framework)
    {
        if (!hotkey.Poll(Config.Hotkey)) return;
        Execute("熱鍵");
    }

    private void OnCommand(string command, string arguments) => Execute($"指令 {command}");

    /// <summary>跑一輪急停，把結果留下來給 UI 與記錄用。</summary>
    /// <param name="source">誰觸發的（寫進記錄；出事時這是唯一分得出來的線索）。</param>
    internal void Execute(string source)
    {
        // 🔴 <b>先停自己再停別人。</b>本外掛自己也有會發起移動的無人值守流程
        //    （園圃自動整理的走位）。順序顛倒的話，FleetStop 已經把 vnavmesh 停下來了，
        //    而我們自己的佇列還活著、下一格照樣送一次新的導航請求——
        //    表現是「按了急停，角色停一下又走了」，而且看起來像 vnavmesh 沒停。
        var selfResult = StopOwnAutomation();

        var results = FleetStop.StopAll();
        results.Insert(0, selfResult);

        LastResults.Clear();
        LastResults.AddRange(results);
        lastRunAt = DateTime.UtcNow;

        Count(out var ok, out var failed, out var missing);

        // 🔴 Information 級：使用者跑 LogLevel 1，而「急停到底有沒有生效」出事之後只能從記錄回推。
        Svc.Log.Information(
            $"[FleetEmergencyStop] 全艦隊急停（{source}）：成功 {ok}、失敗 {failed}、未安裝 {missing}，共 {results.Count} 項。");

        foreach (var r in results)
        {
            // 未安裝不逐項寫（那是常態，會把記錄洗掉）；成功與失敗都寫，失敗才有對照組。
            if (r.Outcome == FleetStopOutcome.NotInstalled) continue;
            Svc.Log.Information($"[FleetEmergencyStop] {r.Target}：{DescribeOutcome(r.Outcome)}　{r.Detail}");
        }

        if (Config.AnnounceInChat)
        {
            Svc.Chat.Print($"[TC Toolbox] 全艦隊急停：成功 {ok}、失敗 {failed}、未安裝 {missing}。");
            foreach (var r in results)
            {
                if (r.Outcome != FleetStopOutcome.Failed) continue;
                Svc.Chat.Print($"[TC Toolbox] 急停失敗：{r.Target} —— {r.Detail}");
            }
        }

        if (Config.OpenResultWindow && window != null)
            window.IsOpen = true;

        AnnounceToTataru(failed);
    }

    /// <summary>
    /// 把<b>本外掛自己</b>會自己動的流程叫停，並讓共用閘門進入急停冷卻。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 🔴 <b>「別的外掛都停了」不等於「全部停了」。</b>急停對象清單列的都是別人，
    /// 而本外掛自己也有會發起移動與互動的無人值守流程。漏掉自己的失敗形式最難歸因：
    /// 使用者看到角色停了一下又動起來，而急停結果視窗上每一項都是綠的。
    /// </para>
    /// <para>
    /// 🔴 <b>光是中止佇列不夠，還要壓住「下一個週期」。</b>園圃的無人值守重跑是按時間排程的，
    /// 佇列清掉之後它下一次到期就會自己再開一輪。所以這裡另外通知
    /// <see cref="AutomationGate"/> 進入冷卻——<c>NavStop</c> 那三秒補送窗口只涵蓋
    /// 「確保 vnavmesh 真的停下來」，涵蓋不到排程。
    /// </para>
    /// <para>
    /// 📌 <b>三態一律回 <see cref="FleetStopOutcome.Ok"/>。</b>「沒有東西正在跑」在這裡不是
    /// 「未安裝」——模組就在這裡，冷卻也真的設下去了，畫成灰色會讓使用者以為自己這一項沒生效。
    /// </para>
    /// </remarks>
    private static FleetStopResult StopOwnAutomation()
    {
        var stopped = new List<string>();

        foreach (var module in Plugin.Instance.Modules)
        {
            // ⚠️ 只挑真的會發起移動／長流程的模組，逐個具名。
            //    用「全部模組一律 Disable」那種寫法會把使用者的開關關掉，而急停不該改設定。
            if (module is not AutoGardensWork garden) continue;
            if (!garden.IsBusy) continue;

            var step = garden.CurrentStepName;
            garden.StopBatch();
            stopped.Add($"園圃自動作業（中止於「{step}」）");
        }

        AutomationGate.NotifyEmergencyStop();

        // 🔑 冷卻可以被設成 0，那時候說「已暫停 0 秒」是騙人的——分成兩句話講。
        var seconds = AutomationGate.EmergencyStopCooldownSeconds;
        var cooldownNote = seconds > 0
            ? $"並暫停無人值守重跑 {seconds} 秒"
            : "冷卻秒數設為 0，無人值守重跑不暫停";

        var detail = stopped.Count > 0
            ? $"已中止：{string.Join("、", stopped)}；{cooldownNote}"
            : $"沒有正在跑的批次；{cooldownNote}";

        return new FleetStopResult("TC Toolbox 自己", FleetStopOutcome.Ok, detail);
    }

    /// <summary>整輪急停跑完之後，請塔塔露出一聲。</summary>
    /// <param name="failed">這一輪有幾個對象是 <see cref="FleetStopOutcome.Failed"/>。</param>
    /// <remarks>
    /// <para>
    /// 🔴🔴 <b>判準只看 <see cref="FleetStopOutcome.Failed"/>，<c>NotInstalled</c> 不算失敗。</b>
    /// 「未安裝」是清單上最常見的狀態（沒有人裝滿十二個外掛），把它算進去的話
    /// <b>每一次</b>急停都會聽到「有幾個停不下來」——那句話會立刻失去意義，
    /// 而真正有東西沒停下來的那一次也就沒有人會當一回事了。
    /// </para>
    /// <para>
    /// 📌 <b>整輪跑完才叫一次</b>，不是逐項叫。急停清單有十幾項，逐項出聲等於連續轟炸。
    /// </para>
    /// <para>
    /// 📌 TataruPraise 沒安裝、總開關關著、或這個情境被使用者關掉時，這一整段是安靜的
    /// no-op（閘門在 <see cref="TataruPraiseIpc.TryPraise"/> 裡）——急停本身的行為完全不受影響。
    /// </para>
    /// <para>
    /// ⚠️ 在主執行緒上呼叫：<c>Execute</c> 的兩個入口（熱鍵的 <c>Framework.Update</c>、
    /// 指令處理常式）都在主執行緒，而 IPC 的實作是跑在<b>呼叫端的執行緒</b>上的。
    /// </para>
    /// </remarks>
    private static void AnnounceToTataru(int failed)
    {
        if (failed > 0)
        {
            TataruPraiseIpc.TryPraise(
                TataruPraiseIpc.CategoryFleetStopFailed,
                $"全艦隊急停有 {failed} 項失敗");
            return;
        }

        TataruPraiseIpc.TryPraise(TataruPraiseIpc.CategoryFleetStop, "全艦隊急停全部停妥");
    }

    private static void Count(out int ok, out int failed, out int missing)
    {
        ok = 0;
        failed = 0;
        missing = 0;
        foreach (var r in LastResults)
        {
            switch (r.Outcome)
            {
                case FleetStopOutcome.Ok: ok++; break;
                case FleetStopOutcome.Failed: failed++; break;
                default: missing++; break;
            }
        }
    }

    private static string DescribeOutcome(FleetStopOutcome outcome) => outcome switch
    {
        FleetStopOutcome.Ok => "成功",
        FleetStopOutcome.Failed => "失敗",
        _ => "未安裝",
    };

    public override void DrawConfig()
    {
        DrawBigButton();

        ImGui.Spacing();

        if (HotkeyUi.Draw("FleetEmergencyStop", Config.Hotkey))
            Plugin.Instance.Config.Save();

        var announce = Config.AnnounceInChat;
        if (ImGui.Checkbox("在聊天視窗顯示結果摘要", ref announce))
        {
            Config.AnnounceInChat = announce;
            Plugin.Instance.Config.Save();
        }

        var openWindow = Config.OpenResultWindow;
        if (ImGui.Checkbox("急停後自動開啟結果視窗", ref openWindow))
        {
            Config.OpenResultWindow = openWindow;
            Plugin.Instance.Config.Save();
        }

        ImGui.SameLine();
        if (ImGui.Button("開啟結果視窗##FleetEmergencyStopOpen") && window != null)
            window.IsOpen = true;

        DrawCooldownSetting();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // 唯讀：這一格什麼都不改，只是把艦隊裡本來就有、卻沒有人去讀的租約／狀態端點顯示出來。
        ControlStatusPanel.Draw();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawResults();

        ImGui.Spacing();
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
        ImGui.TextDisabled(
            "只停不啟：這裡沒有「一鍵恢復」，要繼續請各自回那些外掛裡打開。\n" +
            "WrathCombo 與 SomethingNeedDoing 走它們自己的聊天指令（/wrath auto off、/snd stop all），" +
            "其餘走 IPC；沒安裝的顯示成「未安裝」而不是失敗。");
        ImGui.PopTextWrapPos();
    }

    /// <summary>急停之後無人值守流程要靜多久。</summary>
    /// <remarks>
    /// <para>
    /// 🔑 <b>這個數字原本是寫死在程式裡的政策值</b>，改成可調並不改變預設行為（仍然是 60 秒）。
    /// 會想改它的情境是真實的：常跑無人值守的人希望急停之後有更長的接手時間，
    /// 而只把急停當「立刻剎車」用的人會希望按完就能馬上重開。
    /// </para>
    /// <para>
    /// 🔴 <b>0 是合法值，而且要讓使用者看得出它代表什麼。</b>滑桿拉到 0 時，下方會多出
    /// 一行說明——只留一個孤零零的 0，讀起來像是設定壞掉。
    /// </para>
    /// <para>
    /// ⚠️ 這只影響<b>本外掛自己</b>的無人值守迴圈（目前是園圃自動整理）。
    /// 別的外掛被急停之後會不會自己重開，是它們自己的事，這個滑桿管不到。
    /// </para>
    /// </remarks>
    private static void DrawCooldownSetting()
    {
        var seconds = Config.EmergencyStopCooldownSeconds;
        ImGui.SetNextItemWidth(200f);
        if (ImGui.SliderInt(
                "急停後暫停無人值守流程（秒）##FleetEmergencyStopCooldown",
                ref seconds,
                0,
                AutomationGate.MaxCooldownSeconds))
        {
            Config.EmergencyStopCooldownSeconds = Math.Clamp(seconds, 0, AutomationGate.MaxCooldownSeconds);
            Plugin.Instance.Config.Save();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "急停會先把正在跑的批次停掉，再讓本外掛的無人值守流程靜這麼久。\n" +
                "沒有這段冷卻的話，一個到期的迴圈下一幀就會自己再開一輪——\n" +
                "表現成「剛按下急停，角色又動了起來」，看起來像急停失效。\n" +
                "設成 0 就是不暫停：停下正在跑的那一輪之後，下一輪照常開始。\n" +
                "這個設定只管本外掛自己的流程，管不到別的外掛。");
        }

        // 列上看得見「0 代表什麼」：孤零零一個 0 讀起來像是設定壞掉。
        if (seconds <= 0)
        {
            ImGui.TextDisabled("目前為 0：急停不會暫停無人值守流程（正在跑的那一輪還是會被停下來）。");
        }
    }
    /// <summary>那顆大按鈕。設定面板與結果視窗共用同一份。</summary>
    internal void DrawBigButton()
    {
        bool clicked;
        using (ImRaii.PushColor(ImGuiCol.Button, BigButtonColor))
        using (ImRaii.PushColor(ImGuiCol.ButtonHovered, BigButtonHoverColor))
        {
            clicked = ImGui.Button("全部停止##FleetEmergencyStopRun",
                new Vector2(ImGui.GetContentRegionAvail().X, 40f));
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("立刻叫停所有會自己動的外掛。這個動作只停不啟，按錯了不會弄壞任何東西。");

        if (clicked) Execute("按鈕");
    }

    /// <summary>最後一次急停的逐項結果。設定面板與結果視窗共用同一份。</summary>
    internal static void DrawResults()
    {
        if (lastRunAt == DateTime.MinValue)
        {
            ImGui.TextDisabled("這次遊戲期間還沒有執行過急停。");
            return;
        }

        ImGui.TextDisabled($"上次執行：{lastRunAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}");

        foreach (var result in LastResults)
        {
            var color = result.Outcome switch
            {
                FleetStopOutcome.Ok => OkColor,
                FleetStopOutcome.Failed => FailColor,
                _ => MissingColor,
            };

            ImGui.TextColored(color, DescribeOutcome(result.Outcome));
            ImGui.SameLine();
            ImGui.TextUnformatted(result.Target);

            // 細節（做了哪幾個呼叫、錯誤訊息是什麼）放 tooltip：那是「起疑才查」的東西。
            // 三態本身留在列上——包含「未安裝」，因為「這個外掛根本沒被叫到」也是要看得見的資訊。
            if (!string.IsNullOrEmpty(result.Detail) && ImGui.IsItemHovered())
                ImGui.SetTooltip(result.Detail);
        }
    }
}

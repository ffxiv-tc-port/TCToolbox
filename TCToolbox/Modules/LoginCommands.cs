using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 登入後自動執行使用者設定的斜線指令。
/// </summary>
/// <remarks>
/// 參考 ComplexTweaks <c>EnhancedLoginLogout</c> 的「跑指令」那一半重寫；
/// 上游的角色分組指令表與登出鉤子<b>刻意不移植</b>。
/// <para>
/// 🔴 <b>觸發時機只有「登入」這一種事件，而且一次登入只跑一輪。</b>
/// 不掛區域切換、不掛副本、不掛任何外掛的完成事件——那會把這個模組變成事件驅動的自動化鏈起點，
/// 是艦隊紅線。想要別的時機請自己按下面那顆「立刻執行一次」。
/// </para>
/// <para>
/// 📌 <b>指令內容不過濾</b>：那是使用者自己的巨集，我們沒有立場替他決定哪一條能跑。
/// 唯一的限制來自 <see cref="ChatSender"/>（必須以 <c>/</c> 開頭、不得含換行、不得超過 500 位元組），
/// 那些是「送出去也不會成立」的形式限制，不是內容審查。
/// 設定畫面會把遊戲與其他外掛都不認得的指令標成灰字，但<b>只是提示，照樣會送</b>——
/// 外掛可能比本模組晚載入，當下不認得不代表登入時不認得。
/// </para>
/// </remarks>
public sealed class LoginCommands : TcModule
{
    public override string InternalName => "LoginCommands";
    public override string DisplayName => "登入後執行指令";

    public override string Description =>
        "登入完成之後，自動依序執行你自己設定的斜線指令（每行一條）。" +
        "只在「登入」這一個時機觸發，一次登入跑一輪；指令內容完全由你決定，本模組不過濾也不改寫。";

    public override ModuleCategory Category => ModuleCategory.Misc;

    public override bool HasConfigUI => true;

    private LoginCommandsConfig Config => Plugin.Instance.Config.LoginCommands;

    /// <summary>已經收到登入事件、但還沒開始跑指令。</summary>
    private bool armed;

    /// <summary>角色資料就緒的時刻（<see cref="Environment.TickCount64"/>）；<c>0</c>＝還沒就緒。</summary>
    private long readySinceTick;

    private readonly TaskQueue queue = new();

    /// <summary>上一輪的結果，只給設定畫面顯示用。</summary>
    private string lastRunSummary = string.Empty;

    protected override void OnEnable()
    {
        Svc.ClientState.Login += OnLogin;
        Svc.ClientState.Logout += OnLogout;
        Svc.Framework.Update += OnUpdate;

        // 📌 刻意不在這裡自己補跑一次。啟用模組（或外掛熱載入）時人已經在遊戲裡了，
        //    那不是「登入」——把它當成登入會讓每次改設定都莫名其妙送出一輪指令。
    }

    protected override void OnDisable()
    {
        Svc.ClientState.Login -= OnLogin;
        Svc.ClientState.Logout -= OnLogout;
        Svc.Framework.Update -= OnUpdate;

        armed = false;
        readySinceTick = 0;
        queue.Abort();
    }

    private void OnLogin()
    {
        armed = true;
        readySinceTick = 0;
        queue.Abort();

        Svc.Log.Information($"[{InternalName}] 收到登入事件，等待角色資料就緒後執行登入指令。");
    }

    private void OnLogout(int type, int code)
    {
        // 🔴 登出時一定要把還沒跑完的佇列倒掉：留著的話下一次登入會先把上一個角色的殘留指令送出去。
        if (armed || queue.IsBusy)
            Svc.Log.Information($"[{InternalName}] 登出，取消尚未執行完的登入指令。");

        armed = false;
        readySinceTick = 0;
        queue.Abort();
    }

    private void OnUpdate(IFramework framework)
    {
        queue.Tick();

        if (!armed) return;

        // 角色資料還沒到位就一直等——這裡不設上限，因為「一直沒登入成功」本來就不該跑指令。
        if (!Svc.ClientState.IsLoggedIn) return;
        if (Svc.Objects.LocalPlayer == null) return;
        if (Svc.Condition[ConditionFlag.BetweenAreas]) return;
        if (Svc.Condition[ConditionFlag.BetweenAreas51]) return;

        var now = Environment.TickCount64;
        if (readySinceTick == 0)
        {
            readySinceTick = now;
            return;
        }

        if (now - readySinceTick < Config.InitialDelayMs) return;

        armed = false;
        readySinceTick = 0;

        RunCommands("登入");
    }

    /// <summary>把設定裡的指令逐行排進佇列。<paramref name="reason"/> 只進記錄。</summary>
    private void RunCommands(string reason)
    {
        var lines = ParseCommands(Config.Commands);
        if (lines.Count == 0)
        {
            lastRunSummary = $"（{reason}）沒有可執行的指令";
            Svc.Log.Information($"[{InternalName}] {reason}觸發，但設定裡沒有任何可執行的指令。");
            return;
        }

        if (Config.SkipWhenAutoRetainerBusy && AutoRetainerIpc.IsAvailable() && AutoRetainerIpc.IsBusy())
        {
            lastRunSummary = $"（{reason}）AutoRetainer 忙碌中，本次略過";
            Svc.Log.Information(
                $"[{InternalName}] {reason}觸發，但 AutoRetainer 正在作業中，本次略過 {lines.Count} 條指令。");
            return;
        }

        queue.Abort();

        foreach (var line in lines)
        {
            var command = line;
            queue.EnqueueDelay(Config.IntervalMs, $"等待 {Config.IntervalMs}ms");

            // ⚠️ 這裡刻意用「有大括號、不回傳值」的 lambda：
            //    `() => ChatSender.ExecuteCommand(command)` 同時符合 Enqueue 的 Action 與
            //    Func<bool?> 兩個多載（bool 隱含轉得到 bool?），是編譯期歧義。
            queue.Enqueue($"執行 {command}", () => { ChatSender.ExecuteCommand(command); });
        }

        lastRunSummary = $"（{reason}）已排入 {lines.Count} 條指令";
        Svc.Log.Information($"[{InternalName}] {reason}觸發，已排入 {lines.Count} 條指令，間隔 {Config.IntervalMs}ms。");

        if (Config.NotifyInChat)
            Svc.Chat.Print($"[TC Toolbox] {reason}後執行 {lines.Count} 條自訂指令。");
    }

    /// <summary>把多行文字切成可執行的指令清單（去空行、去空白、只留斜線開頭的）。</summary>
    private static List<string> ParseCommands(string raw)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(raw)) return result;

        foreach (var rawLine in raw.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            // ⚠️ 不以 / 開頭的行直接丟掉而不是自動補上斜線：
            //    上游會自動補，但那會讓一行純文字的註解變成一條真的指令送出去。
            if (!line.StartsWith('/')) continue;

            result.Add(line);
        }

        return result;
    }

    public override void DrawConfig()
    {
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
        ImGui.TextDisabled("每行一條指令，必須以 / 開頭。不以 / 開頭的行會被忽略（可以拿來當註解）。");
        ImGui.PopTextWrapPos();

        var commands = Config.Commands;
        if (ImGui.InputTextMultiline("##loginCommands", ref commands, 4096,
                new Vector2(-1f, ImGui.GetTextLineHeight() * 6f)))
            Config.Commands = commands;
        if (ImGui.IsItemDeactivatedAfterEdit())
            Plugin.Instance.Config.Save();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("例：\n/echo 早安\n/ac 蹲下\n/snd run 每日巡邏\n\n" +
                             "指令內容不會被過濾或改寫，送出去的就是你寫的這一行。");

        DrawCommandHints();

        ImGui.Spacing();

        ImGui.SetNextItemWidth(200f);
        var initialDelay = Config.InitialDelayMs;
        if (ImGui.SliderInt("登入後先等（毫秒）", ref initialDelay, 0, 30_000))
            Config.InitialDelayMs = Math.Clamp(initialDelay, 0, 120_000);
        if (ImGui.IsItemDeactivatedAfterEdit())
            Plugin.Instance.Config.Save();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("從「角色已經站在地圖上」那一刻起再等這麼久才開始跑第一條。\n" +
                             "太短的話有些外掛還沒載入完，它們的指令會被當成不存在而失敗。");

        ImGui.SetNextItemWidth(200f);
        var interval = Config.IntervalMs;
        if (ImGui.SliderInt("每條之間隔（毫秒）", ref interval, 100, 5_000))
            Config.IntervalMs = Math.Clamp(interval, 0, 60_000);
        if (ImGui.IsItemDeactivatedAfterEdit())
            Plugin.Instance.Config.Save();

        var skipWhenBusy = Config.SkipWhenAutoRetainerBusy;
        if (ImGui.Checkbox("AutoRetainer 作業中時整輪略過", ref skipWhenBusy))
        {
            Config.SkipWhenAutoRetainerBusy = skipWhenBusy;
            Plugin.Instance.Config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("AutoRetainer 的多角色模式會自己反覆登入登出。\n" +
                             "那種登入通常不是你本人要開始玩，此時插進去的指令只會打斷它的作業。\n" +
                             "AutoRetainer 沒安裝時這一格沒有作用。");

        if (!AutoRetainerIpc.IsAvailable())
        {
            ImGui.SameLine();
            ImGui.TextDisabled("（未偵測到 AutoRetainer）");
        }

        var notify = Config.NotifyInChat;
        if (ImGui.Checkbox("執行時在聊天欄顯示一行", ref notify))
        {
            Config.NotifyInChat = notify;
            Plugin.Instance.Config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("這是你唯一看得到「指令是我自己設定的、不是遊戲在亂動」的證據。\n記錄一律會寫，不受這格影響。");

        ImGui.Spacing();
        ImGui.Separator();

        var busy = queue.IsBusy;
        using (Dalamud.Interface.Utility.Raii.ImRaii.Disabled(busy))
        {
            if (ImGui.Button("立刻執行一次"))
                RunCommands("手動");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("不必重新登入就試跑一輪，用來確認指令寫對了沒有。\n這是手動觸發，與登入事件無關。");

        if (busy)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"執行中：{queue.CurrentStep}");
        }
        else if (lastRunSummary.Length > 0)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(lastRunSummary);
        }
    }

    /// <summary>
    /// 逐行標出「這個指令有沒有人認得」。
    /// </summary>
    /// <remarks>
    /// 📌 <b>只是提示，不擋執行。</b>外掛的指令是在它自己載入時才註冊的，
    /// 所以現在標成灰字的指令，登入時很可能是好的。
    /// 這一段整個包在 try 裡——它在 Draw 路徑上，擲一次例外整個介面就到重開遊戲為止。
    /// </remarks>
    private void DrawCommandHints()
    {
        List<string> lines;
        try
        {
            lines = ParseCommands(Config.Commands);
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, $"[{InternalName}] 解析指令清單失敗");
            return;
        }

        if (lines.Count == 0)
        {
            ImGui.TextDisabled("目前沒有可執行的指令。");
            return;
        }

        var unknown = new List<string>();
        try
        {
            foreach (var line in lines)
            {
                var token = TextCommands.FirstToken(line);
                if (token.Length == 0) continue;
                if (TextCommands.IsKnownGameCommand(token)) continue;
                if (TextCommands.IsRegisteredPluginCommand(token)) continue;
                unknown.Add(token);
            }
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, $"[{InternalName}] 指令辨識失敗");
            return;
        }

        if (unknown.Count == 0)
        {
            ImGui.TextDisabled($"共 {lines.Count} 條，全部都認得。");
            return;
        }

        ImGui.TextDisabled($"共 {lines.Count} 條，其中 {unknown.Count} 條目前沒人認得：");
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.8f, 1f), string.Join("、", unknown));
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("遊戲的 TextCommand 表裡沒有、目前也沒有任何外掛註冊過這些指令。\n" +
                             "⚠️ 這不代表它們是錯的——外掛可能比本模組晚載入。\n" +
                             "這些指令照樣會在登入時送出。");
    }
}

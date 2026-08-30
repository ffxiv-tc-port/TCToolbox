using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Keys;
using FFXIVClientStructs.FFXIV.Component.GUI;
using TCToolbox.Core;
using CSFramework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;

namespace TCToolbox.Modules;

/// <summary>
/// 點擊移動：在世界上按住修飾鍵點一下地面，角色就走過去。
/// </summary>
/// <remarks>
/// <para>
/// 🔴🔴 <b>移動一律走 vnavmesh 的 IPC，絕不自己接管角色移動。</b>
/// 上游 CBT 的 <c>ClickToMove</c> 有兩種模式，其中一種是它自帶的 <c>OverrideMovement</c>
/// ——那是每幀改寫角色的移動輸入，等於自己實作一套走路，會穿牆、會卡地形、
/// 也繞過遊戲自己的移動限制。這裡<b>只</b>保留尋路模式，全部經由
/// <see cref="ExternalNav"/> 呼叫 vnavmesh 的 <c>PathfindAndMoveTo</c>。
/// vnavmesh 沒裝就是不能用，並且會明確說出來，不會退化成自己走。
/// </para>
/// <para>
/// 🔴 <b>預設要按修飾鍵，不是裸左鍵。</b>上游是裸左鍵放開就觸發，這在 FFXIV 裡是壞的：
/// 左鍵拖曳是<b>旋轉鏡頭</b>，左鍵點擊是<b>選取目標</b>。裸左鍵的話，每一次轉鏡頭
/// 都會在放開的瞬間對著鏡頭停下來的地方發一次尋路。
/// 所以預設是「Shift ＋ 左鍵」，而且<b>另外</b>擋掉拖曳
/// （按下與放開的螢幕距離超過 <see cref="DragTolerancePixels"/> 就當成轉鏡頭，見
/// <see cref="OnDraw"/>）——修飾鍵設成「無」時這道防線仍然在。
/// </para>
/// <para>
/// 📌 停止手段有三個，因為這個功能可以在完全沒有視窗開著的情況下讓角色跑起來：
/// <c>/tcstop</c> 指令、設定畫面上的「停止移動」按鈕、以及再點一次新的目標
/// （新的一次點擊會取代前一趟）。
/// </para>
/// </remarks>
public sealed unsafe class ClickToMove : TcModule
{
    public override string InternalName => "ClickToMove";

    public override string DisplayName => "點擊移動";

    public override string Description =>
        "按住修飾鍵在地面上點一下，角色就自動走過去（需要 vnavmesh 外掛）。" +
        "只下一次導航指令，走到就停——不會自動戰鬥、不會自動接續下一個目標。" +
        "隨時可用 /tcstop 停下來。";

    public override ModuleCategory Category => ModuleCategory.Misc;

    /// <inheritdoc/>
    /// <remarks>
    /// 開著但不去點＝遊戲行為完全不變。它不掛 hook、不自己接手、也不定時做事，
    /// 每一次移動都對應一個明確的使用者手勢，所以歸在手動觸發。
    /// </remarks>
    public override bool IsManualTrigger => true;

    public override bool HasConfigUI => true;

    /// <summary>可選的修飾鍵（0＝不需要修飾鍵）。</summary>
    private static readonly (int Code, string Label)[] SelectableKeys =
    [
        (0, "無（裸左鍵）"),
        ((int)VirtualKey.SHIFT, "SHIFT"),
        ((int)VirtualKey.CONTROL, "CTRL"),
        ((int)VirtualKey.MENU, "ALT"),
    ];

    /// <summary>
    /// 按下與放開之間允許的螢幕位移（像素）。超過就當成「在轉鏡頭」而不是「點地面」。
    /// </summary>
    /// <remarks>
    /// 🔴 這道防線<b>與修飾鍵無關，永遠生效</b>。上游沒有這一段，所以它在
    /// 「按住左鍵轉一圈鏡頭再放開」時會直接跑起來。
    /// 6 像素是手抖的容忍度：真的要點地面的人不會移動超過這個距離。
    /// </remarks>
    private const float DragTolerancePixels = 6f;

    /// <summary>vnavmesh 狀態探測的快取間隔（毫秒）。</summary>
    /// <remarks>
    /// ⚠️ 快取是為了成本不是為了正確性：vnavmesh 沒安裝時每次 IPC 都會擲例外，
    /// 而這是在每幀的繪製路徑上。失效模式是良性的——狀態最多晚 500ms，
    /// 而真的下指令時失敗照樣會說出來。
    /// </remarks>
    private const int ProbeIntervalMs = 500;

    private ClickToMoveConfig Config => Plugin.Instance.Config.ClickToMove;

    // ── vnavmesh 探測的快取 ──
    private bool vnavInstalled;
    private bool vnavReady;
    private bool vnavPathRunning;
    private bool vnavPathfinding;

    /// <summary>這一次左鍵是不是「在遊戲世界上按下的」（而不是按在介面上）。</summary>
    private bool pressActive;

    /// <summary>左鍵按下時的螢幕座標，用來算拖曳距離。</summary>
    private Vector2 pressScreenPos;

    /// <summary>最近一次成功下達導航的目的地，只給設定畫面顯示。</summary>
    private Vector3 lastDestination;

    private bool hasLastDestination;

    protected override void OnEnable()
    {
        pressActive = false;
        hasLastDestination = false;

        NavStop.Acquire();
        Svc.PluginInterface.UiBuilder.Draw += OnDraw;
    }

    protected override void OnDisable()
    {
        Svc.PluginInterface.UiBuilder.Draw -= OnDraw;

        // 使用者在我們發起的移動還在跑的時候關掉模組——是我們讓他跑起來的，就由我們收掉。
        // 📌 沒發起過就不呼叫：不要在拆卸路徑上對別的外掛做沒必要的 IPC。
        // 📌 這個順序（先停再放）是對的，而且 NavStop.Release 會讓補送窗口活過最後一次
        //    Release（延後拆看門狗）——否則這裡等於只送出單獨一發，攔不住還在計算中的路徑。
        if (hasLastDestination)
            NavStop.RequestStop();

        NavStop.Release();

        pressActive = false;
        hasLastDestination = false;
    }

    /// <summary>
    /// 點擊偵測放在繪製回呼裡，因為只有這裡的 ImGui 輸入狀態是有效的。
    /// </summary>
    /// <remarks>
    /// 📌 在繪製回呼裡呼叫 IPC 是這個 repo 既有的做法（F.A.T.E. 總覽的導航按鈕就是），
    /// 兩者都在主執行緒上，沒有跨執行緒問題。
    /// </remarks>
    private void OnDraw()
    {
        RefreshVnavState();

        var io = ImGui.GetIO();

        // ① 滑鼠正在操作某個 Dalamud 視窗（外掛的視窗）→ 這一下不是點世界。
        if (io.WantCaptureMouse)
        {
            pressActive = false;
            return;
        }

        // ② 遊戲視窗沒有焦點（alt-tab 出去了）→ 不處理。
        var csFramework = CSFramework.Instance();
        if (csFramework == null || csFramework->WindowInactive)
        {
            pressActive = false;
            return;
        }

        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            // 按下的這一刻就決定這次算不算數：修飾鍵要對、而且不能按在遊戲自己的介面上。
            pressActive = IsModifierHeld() && !IsCursorOverGameUi();
            pressScreenPos = io.MousePos;
            return;
        }

        if (!ImGui.IsMouseReleased(ImGuiMouseButton.Left)) return;

        if (!pressActive) return;
        pressActive = false;

        // 🔴 拖曳＝轉鏡頭，不是點地面。這道防線與修飾鍵無關，永遠生效。
        if (Vector2.Distance(io.MousePos, pressScreenPos) > DragTolerancePixels) return;

        // 放開的當下再確認一次修飾鍵還按著——按下之後放掉修飾鍵通常代表反悔了。
        if (!IsModifierHeld()) return;

        TryMoveToCursor(io.MousePos);
    }

    private bool IsModifierHeld()
    {
        var code = Config.ModifierKeyCode;
        if (code == 0) return true;
        return Svc.Keys[(VirtualKey)code];
    }

    /// <summary>
    /// 滑鼠是不是正壓在<b>遊戲自己的</b>介面元件上（技能列、選單、地圖…）。
    /// </summary>
    /// <remarks>
    /// 🔴 每一層解參考都要判空。<c>AtkStage</c>／<c>AtkCollisionManager</c> 在讀取畫面、
    /// 登入前等時機是空的，少判一層就是自找的存取違規，而 AVE 是 <c>try/catch</c> 攔不到的。
    /// <para>
    /// 📌 讀不到的時候<b>回 true</b>（＝當成壓在介面上，不觸發移動）。
    /// 這個方向的錯誤是「少走一次」，反方向是「在讀取畫面時亂發尋路」。
    /// </para>
    /// </remarks>
    private static bool IsCursorOverGameUi()
    {
        var stage = AtkStage.Instance();
        if (stage == null) return true;

        var collision = stage->AtkCollisionManager;
        if (collision == null) return true;

        return collision->IntersectingCollisionNode != null;
    }

    private void RefreshVnavState()
    {
        if (!Throttle.Pass("ClickToMove-VnavProbe", ProbeIntervalMs)) return;

        vnavReady = ExternalNav.IsVnavmeshReady();

        // 網格就緒必然代表外掛在，可以省一次 IPC。
        vnavInstalled = vnavReady || ExternalNav.IsVnavmeshInstalled();

        vnavPathRunning = vnavInstalled && ExternalNav.IsVnavmeshPathRunning();
        vnavPathfinding = vnavInstalled && ExternalNav.IsVnavmeshPathfindInProgress();
    }

    /// <summary>把螢幕座標換算成世界座標並下一次導航指令。</summary>
    private void TryMoveToCursor(Vector2 screenPos)
    {
        if (GetBlockedReason() is { } reason)
        {
            if (Throttle.Pass("ClickToMove-Blocked", 3_000))
                Svc.Chat.Print($"[TC Toolbox] 點擊移動：{reason}");
            return;
        }

        // ⚠️ 回傳 false＝這條射線沒有打到任何地形（點到天空或視野外）。
        // 一定要看回傳值：失敗時 worldPos 的內容沒有意義，拿去尋路等於送一個亂數座標。
        if (!Svc.GameGui.ScreenToWorld(screenPos, out var worldPos))
        {
            if (Throttle.Pass("ClickToMove-NoHit", 2_000))
                Svc.Chat.Print("[TC Toolbox] 點擊移動：那個方向沒有可以走的地面。");
            return;
        }

        // 前一趟就此作廢：使用者點了新的地方，意思就是不要舊的了。
        // vnavmesh 對「還在算路徑時又收到新請求」是直接拒絕的，所以要先停。
        if (vnavPathRunning || vnavPathfinding)
            NavStop.RequestStop();

        if (!ExternalNav.TryMoveTo(worldPos, Config.AllowFly, out var started, DisplayName))
        {
            Throttle.Reset("ClickToMove-VnavProbe");
            Svc.Chat.Print("[TC Toolbox] 點擊移動：無法呼叫 vnavmesh，沒有開始移動。");
            return;
        }

        if (!started)
        {
            Throttle.Reset("ClickToMove-VnavProbe");
            if (Throttle.Pass("ClickToMove-Refused", 2_000))
                Svc.Chat.Print("[TC Toolbox] 點擊移動：vnavmesh 拒絕了這次導航（多半是上一個路徑還在計算中），請稍候再試。");
            return;
        }

        lastDestination = worldPos;
        hasLastDestination = true;

        // 讓狀態顯示立刻反映「開始動了」，不要等快取到期。
        Throttle.Reset("ClickToMove-VnavProbe");

        // 使用者回報用的定錨點：出事時這一行是唯一能證明「移動是他自己點的、目標在哪」的證據。
        Svc.Log.Information(
            $"[{InternalName}] 使用者點擊移動 → {worldPos:F1} 飛行={Config.AllowFly}");

        if (Config.NotifyOnMove)
            Svc.Chat.Print($"[TC Toolbox] 前往 {worldPos.X:F1}, {worldPos.Y:F1}, {worldPos.Z:F1}（/tcstop 可停下）。");
    }

    /// <summary>現在不能發起移動的原因；<c>null</c>＝可以。</summary>
    private string? GetBlockedReason()
    {
        if (Svc.Objects.LocalPlayer == null)
            return "目前不在遊戲中。";

        if (!vnavInstalled)
            return "未偵測到 vnavmesh 外掛，無法自動移動。";

        if (!vnavReady)
            return "vnavmesh 的導航網格尚未就緒（可能正在背景載入，或這個區域沒有網格）。";

        if (Svc.Condition[ConditionFlag.BetweenAreas] || Svc.Condition[ConditionFlag.BetweenAreas51])
            return "正在讀取地圖。";

        if (Svc.Condition[ConditionFlag.WatchingCutscene] ||
            Svc.Condition[ConditionFlag.OccupiedInCutSceneEvent])
            return "正在播放過場動畫。";

        if (Svc.Condition[ConditionFlag.Occupied33] || Svc.Condition[ConditionFlag.OccupiedInQuestEvent])
            return "正在進行中的事件裡。";

        return null;
    }

    public override void DrawConfig()
    {
        DrawVnavStatus();

        ImGui.Separator();

        // ── 停止移動 ──
        // vnavmesh 不在、或根本沒有移動在跑時停用這顆按鈕，並用 tooltip 說明原因。
        var stopReason = GetStopBlockedReason();
        var stopBlocked = stopReason != null;

        if (stopBlocked) ImGui.BeginDisabled();
        var stopClicked = ImGui.Button("停止移動");
        if (stopBlocked) ImGui.EndDisabled();

        // ⚠️ 停用中的項目預設不回報 hover，一定要 AllowWhenDisabled 才問得到——
        //    否則「按鈕灰掉又沒有說明」就是純粹的靜默失敗。
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(stopReason ?? $"立刻停止移動。\n也可以直接輸入 {NavStop.Command}。");

        if (stopClicked && !stopBlocked)
        {
            NavStop.RequestStop();
            Svc.Chat.Print("[TC Toolbox] 已要求停止移動。");
        }

        ImGui.SameLine();
        ImGui.TextDisabled($"（指令：{NavStop.Command}）");

        ImGui.Separator();

        // ── 修飾鍵 ──
        var currentIndex = 0;
        for (var i = 0; i < SelectableKeys.Length; i++)
        {
            if (SelectableKeys[i].Code == Config.ModifierKeyCode)
            {
                currentIndex = i;
                break;
            }
        }

        if (ImGui.BeginCombo("觸發用修飾鍵##clickToMoveModifier", SelectableKeys[currentIndex].Label))
        {
            for (var i = 0; i < SelectableKeys.Length; i++)
            {
                if (ImGui.Selectable(SelectableKeys[i].Label, i == currentIndex))
                {
                    Config.ModifierKeyCode = SelectableKeys[i].Code;
                    Plugin.Instance.Config.Save();
                }
            }

            ImGui.EndCombo();
        }

        if (Config.ModifierKeyCode == 0)
        {
            ImGui.TextColored(new Vector4(1f, 0.85f, 0.35f, 1f),
                "⚠ 裸左鍵：選取目標與點地面會共用同一個動作。");
            ImGui.TextDisabled("（按住左鍵轉鏡頭仍然不會觸發移動——拖曳一律排除。）");
        }

        var allowFly = Config.AllowFly;
        if (ImGui.Checkbox("允許飛行路徑##clickToMoveFly", ref allowFly))
        {
            Config.AllowFly = allowFly;
            Plugin.Instance.Config.Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("在還沒解鎖飛行的區域開這個，vnavmesh 會算不出路徑，\n表現是「點了沒反應」。");

        var notify = Config.NotifyOnMove;
        if (ImGui.Checkbox("每次開始移動時在聊天欄顯示##clickToMoveNotify", ref notify))
        {
            Config.NotifyOnMove = notify;
            Plugin.Instance.Config.Save();
        }

        if (hasLastDestination)
            ImGui.TextDisabled($"上次目的地：{lastDestination.X:F1}, {lastDestination.Y:F1}, {lastDestination.Z:F1}");

        ImGui.TextDisabled("走到就停：不會自動戰鬥、不會自動接續下一個目標。");
    }

    private void DrawVnavStatus()
    {
        if (!vnavInstalled)
        {
            ImGui.TextColored(new Vector4(1f, 0.5f, 0.4f, 1f), "未偵測到 vnavmesh —— 這個模組不會有任何作用。");
            ImGui.TextDisabled("點擊移動完全依賴 vnavmesh 尋路；本模組不會自己接管角色移動。");
            return;
        }

        if (!vnavReady)
        {
            ImGui.TextColored(new Vector4(1f, 0.85f, 0.35f, 1f), "vnavmesh 已安裝，但導航網格尚未就緒。");
            return;
        }

        if (vnavPathfinding)
        {
            ImGui.TextColored(new Vector4(1f, 0.85f, 0.35f, 1f), "正在計算路徑…");
            return;
        }

        if (vnavPathRunning)
        {
            ImGui.TextColored(new Vector4(1f, 0.85f, 0.35f, 1f), "移動中。");
            return;
        }

        if (NavStop.IsEnforcing)
        {
            ImGui.TextColored(new Vector4(1f, 0.85f, 0.35f, 1f), "正在停止移動…");
            return;
        }

        ImGui.TextDisabled("vnavmesh 就緒。");
    }

    /// <summary>「停止移動」按鈕不能按的原因；<c>null</c>＝可以按。</summary>
    private string? GetStopBlockedReason()
    {
        if (!vnavInstalled)
            return "未偵測到 vnavmesh，沒有可以停止的移動。";

        if (!vnavPathRunning && !vnavPathfinding && !NavStop.IsEnforcing)
            return "目前沒有移動在進行。";

        return null;
    }
}

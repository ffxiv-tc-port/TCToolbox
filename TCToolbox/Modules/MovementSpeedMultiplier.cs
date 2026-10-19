using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.Command;
using Dalamud.Hooking;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using Lumina.Excel.Sheets;
using TCToolbox.Core;
using GroupManager = FFXIVClientStructs.FFXIV.Client.Game.Group.GroupManager;

namespace TCToolbox.Modules;

/// <summary>
/// 移動速度倍率：只在使用者列進白名單的地圖（TerritoryType id）裡，把角色移動速度乘上一個倍率。
/// </summary>
/// <remarks>
/// 🔴🔴 <b>detour 契約：不做任何「沒有被原函式證明過」的解參考。</b>
/// <c>a1</c> 原封不動轉給原函式；唯一一處解參考是 <c>a1+8</c>，而且<b>只在原函式成功返回之後</b>才做
/// ——原函式自己進門就無條件解參考同一個位址，所以它順利返回就是那個位址可讀的證明
/// （詳見 <see cref="IsLocalPlayer"/>）。除此之外只有 float 運算。
/// AccessViolationException <c>try/catch</c> 攔不到，所以防護不能靠例外隔離，只能靠<b>不做沒有被證明過的解參考</b>。
/// 🔴 <b>使用者裁決：預設關、白名單預設空。</b>開了模組但一張地圖都沒加＝完全不動作。
/// </remarks>
public sealed unsafe class MovementSpeedMultiplier : TcModule
{
    public override string InternalName => "MovementSpeedMultiplier";

    public override string DisplayName => "移動速度倍率（限白名單地圖）";

    public override string Description =>
        "在你指定的地圖裡把移動速度乘上一個倍率（副本內外都算）。預設關、白名單預設是空的——" +
        "沒加地圖就完全不會動作。白名單以外的地圖與 PvP 區域一律原速。";

    public override ModuleCategory Category => ModuleCategory.Combat;

    public override bool HasConfigUI => true;

    /// <summary>查看／設定倍率的指令。</summary>
    public const string Command = "/tcspeed";

    /// <summary>
    /// 移動速度倍率計算函式的呼叫點特徵碼（開頭是 E8，ScanText 解到的是被呼叫的那支函式）。
    /// </summary>
    /// <remarks>
    /// 📌 這條與 BossModReborn <c>WorldStateGameSync</c> 用的是同一條，兩邊各自獨立掃到同一支函式。
    /// </remarks>
    private const string SpeedMultiplierSignature = "E8 ?? ?? ?? ?? 44 0F 28 D8 45 0F 57 D2";

    /// <summary>倍率下限＝原速。<b>不提供減速</b>：那沒有使用情境，卻多一整類「怎麼變慢了」的問題。</summary>
    public const float MinMultiplier = 1.0f;

    /// <summary>
    /// 倍率上限。
    /// </summary>
    /// <remarks>
    /// 🔴 3.0 是使用者要求的上限，不是量到的安全值。
    /// 遊戲原生的疾跑約是 1.3~1.4 倍——超過那個範圍越多，離「客戶端本來就會出現的速度」越遠。
    /// <b>伺服器端對位移速度的容忍門檻無法離線證明</b>，倍率開多高等於拿帳號試多少，由使用者自己拿捏。
    /// </remarks>
    public const float MaxMultiplier = 3.0f;

    /// <summary>滑桿每一級的大小。</summary>
    private const float MultiplierStep = 0.05f;

    /// <summary>浮點比較用的容差（倍率只有兩位小數，這個值遠小於一級）。</summary>
    private const float Epsilon = 0.0001f;

    private delegate float CalculateMovementSpeedMultiplierDelegate(nint a1);

    private Hook<CalculateMovementSpeedMultiplierDelegate>? hook;

    /// <summary>
    /// 這一幀實際要套用的倍率。<b>framework 執行緒寫、detour 讀。</b>
    /// </summary>
    /// <remarks>
    /// 🔴 <c>volatile</c> 是必要的：寫入端是 framework 更新、讀取端是遊戲自己的執行緒呼進來的 detour。
    /// 沒有 volatile 的話 JIT 可以把這個讀取提升到迴圈外，表現是「離開白名單地圖後速度沒有立刻恢復」。
    /// <para>📌 型別刻意是 <c>float</c> 不是 <c>double</c>——C# 不允許 <c>volatile double</c>。</para>
    /// </remarks>
    private volatile float activeMultiplier = 1f;

    /// <summary>特徵碼有沒有解到。解不到＝這個模組整個是 no-op，UI 與指令都要說出來。</summary>
    private bool sigResolved;

    /// <summary>Control 全域單例的位址（不是遊戲物件）。啟用時解析一次、掛 hook 之前寫入，detour 只讀；0＝沒解到。</summary>
    private nint controlAddress;

    /// <summary>上一次寫進記錄的倍率，用來只在<b>變化時</b>記一行，而不是每幀洗記錄。</summary>
    private float lastLoggedMultiplier = 1f;

    /// <summary>detour 曾經真的對<b>本地玩家</b>套用過放大（只由 detour 寫入，framework 執行緒讀）。</summary>
    private volatile bool sawLocalPlayerApply;

    /// <summary>detour 曾經在「本來要放大」時擋下<b>非本地玩家</b>的呼叫。</summary>
    /// <remarks>
    /// 🔑 這兩個旗標存在的唯一目的是把「這支函式到底會不會為別的角色跑」這個
    /// <b>離線證明不了</b>的問題，變成使用者記錄裡看得到的事實。
    /// 🔴 型別是 <c>volatile bool</c> 而不是計數器：detour 裡只准做「存一個常數」這種
    /// 不配置記憶體、不會擲例外的動作。記錄要寫在 framework 執行緒上，<b>不准寫在 detour 裡</b>
    /// ——那是每幀都會走的路徑，而且 <c>Svc.Log</c> 會配置字串。
    /// </remarks>
    private volatile bool sawOtherCharacter;

    private bool reportedLocalPlayerApply;
    private bool reportedOtherCharacter;

    /// <summary>角色管理器佔用的物件表格數：伺服器生成的角色都在 0～199，之後是過場／集體動作的本機複製體。</summary>
    private const int CharacterSlots = 200;

    /// <summary>這一幀倍率為什麼是這個值。framework 執行緒寫，設定畫面與指令讀。</summary>
    private volatile SpeedState speedState;

    // 附近玩家偵測：以下全部只在 framework 執行緒寫；存的只有計數、距離與時間，不存任何物件包裝或指標。
    private volatile int guardOthersCount;

    /// <summary>最近一位「算數」的其他玩家距離（不看半徑）；NaN＝物件表裡沒有。</summary>
    private volatile float guardNearestMeters = float.NaN;

    private volatile int guardResumeInMs;
    private long guardLastSeenMs = NearbyPlayerGuard.NeverSeen;
    private bool guardObserving;
    private bool guardEvaluatedThisFrame;
    private bool guardPauseLogged;
    private uint guardObservedTerritory;
    private float guardFarthestMeters;
    private int guardMaxOthers;

    private MovementSpeedMultiplierConfig Config => Plugin.Instance.Config.MovementSpeedMultiplier;

    // ── UI 狀態（只在繪製執行緒讀寫；資料表在執行期不會變，索引建一次就好）──
    private string mapSearch = string.Empty;
    private List<MapPickerEntry>? mapIndex;
    private Dictionary<uint, string>? dutyNamesByTerritory;

    private sealed record MapPickerEntry(uint Id, string Place, string Duties);

    /// <inheritdoc/>
    /// <remarks>
    /// 🔴 「不知道／沒作用」本身要在列上看得見，不能只藏在 tooltip 裡——
    /// 特徵碼解不到時這個模組看起來是開著的，但它什麼都不會做。
    /// </remarks>
    public override ModuleNotice? RowNotice
    {
        get
        {
            if (IsEnabled && !sigResolved)
                return new ModuleNotice(
                    ModuleNoticeLevel.Warning,
                    "特徵碼未命中，沒有作用",
                    "找不到遊戲的移動速度倍率計算函式或本地玩家位址（多半是遊戲更新過了）。\n" +
                    "模組不會掛任何 hook，遊戲行為與關閉時完全相同。");

            if (Config.TerritoryWhitelist.Count == 0)
                return new ModuleNotice(
                    ModuleNoticeLevel.Unknown,
                    "白名單是空的，不會在任何地圖生效",
                    "在設定裡加入地圖之後才會作用。這是刻意的預設：不加就完全不動作。");

            return null;
        }
    }

    protected override void OnEnable()
    {
        activeMultiplier = 1f;
        lastLoggedMultiplier = 1f;
        ResetIdentityGateDiagnostics();
        ResetNearbyGuard();

        // 🔴 解不到特徵碼就整個不掛 hook。這裡刻意<b>不</b>擲例外、也不記 Error：
        //    這不是故障而是「這一版遊戲我認不得」，正確的行為是安靜地什麼都不做。
        if (!Svc.SigScanner.TryScanText(SpeedMultiplierSignature, out var address) || address == nint.Zero)
        {
            sigResolved = false;
            RegisterCommand();
            Svc.Log.Information(
                $"[{InternalName}] 特徵碼未命中，本模組停用（不會掛 hook，遊戲行為與關閉時相同）。");
            return;
        }

        controlAddress = ResolveControlAddress();
        if (controlAddress == nint.Zero)
        {
            sigResolved = false;
            RegisterCommand();
            Svc.Log.Information(
                $"[{InternalName}] 找不到本地玩家所在的遊戲位址（Control），" +
                "本模組停用（不會掛 hook，遊戲行為與關閉時相同）。");
            return;
        }

        sigResolved = true;
        hook = Svc.Hooks.HookFromAddress<CalculateMovementSpeedMultiplierDelegate>(address, Detour);
        hook.Enable();

        Svc.Framework.Update += OnFrameworkUpdate;

        // 🔴 指令一定要<b>最後</b>才註冊。<c>TcModule.Enable</c> 會把 <c>OnEnable</c> 擲出的例外
        //    吃掉並讓 <c>IsEnabled</c> 留在 false，於是 <c>OnDisable</c> 永遠不會被呼叫——
        //    先註冊的話，掛 hook 失敗就會留下一個永遠拆不掉的指令處理器，
        //    而對同一個名字重複 AddHandler 是<b>靜默失敗</b>的。
        RegisterCommand();

        Svc.Log.Information(
            $"[{InternalName}] 已掛上移動速度倍率 hook @ 0x{address:X}，" +
            $"白名單 {Config.TerritoryWhitelist.Count} 張地圖，設定倍率 {ClampedConfigMultiplier():0.00}。");
    }

    private void RegisterCommand() =>
        Svc.Commands.AddHandler(Command, new CommandInfo(OnCommand)
        {
            HelpMessage = $"查看或設定移動速度倍率（用法：{Command} 1.25；不帶參數＝只顯示目前值，不改）",
        });

    protected override void OnDisable()
    {
        Svc.Framework.Update -= OnFrameworkUpdate;
        Svc.Commands.RemoveHandler(Command);

        // 🔴 順序很重要：先把倍率歸 1，再拆 hook。
        //    拆卸與「還在飛的 detour 呼叫」之間有一個很短的窗口，
        //    這樣那些呼叫拿到的是原值，而不是還在放大的值。
        activeMultiplier = 1f;

        hook?.Dispose();
        hook = null;

        sigResolved = false;
        lastLoggedMultiplier = 1f;
        ResetIdentityGateDiagnostics();
        ResetNearbyGuard();
    }

    private void ResetIdentityGateDiagnostics()
    {
        sawLocalPlayerApply = false;
        sawOtherCharacter = false;
        reportedLocalPlayerApply = false;
        reportedOtherCharacter = false;
    }

    /// <summary>
    /// 速度倍率計算的 detour。
    /// </summary>
    /// <remarks>
    /// 🔴🔴 <b>這個方法裡不准出現任何指標解參，也不准出現任何會擲例外的呼叫。</b>
    /// <c>a1</c> 原封不動轉給原函式；唯一的解參考在 <see cref="IsLocalPlayer"/>，而且只在原函式返回之後。
    /// </remarks>
    private float Detour(nint a1)
    {
        var self = hook;
        if (self is null) return 1f;

        var original = self.OriginalDisposeSafe(a1);

        var multiplier = activeMultiplier;
        if (multiplier <= 1f + Epsilon) return original;

        // ⚠️ 原函式理論上不會回 NaN／無限大，但這裡是每幀都會走的路徑，
        //    而一個 NaN 乘出去就是角色再也不會動——成本一個比較，不值得省。
        if (!float.IsFinite(original)) return original;

        if (!IsLocalPlayer(a1))
        {
            sawOtherCharacter = true;
            return original;
        }

        sawLocalPlayerApply = true;
        return original * multiplier;
    }

    /// <summary>
    /// 這一次呼叫的對象是不是<b>本地玩家</b>。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>不可以把這個呼叫搬到 <c>original</c> 之前。</b>搬了就變成我們自己先賭一把，
    /// 而 AccessViolationException 在 .NET Core 是 corrupted-state exception，<c>try/catch</c> 根本攔不到。
    /// 🔑 讀不到就回 <c>false</c>（＝不放大）。這個方向的錯誤是「該加速時沒加速」，
    /// 反方向是「對不該碰的角色改了速度」。
    /// </remarks>
    private bool IsLocalPlayer(nint a1)
    {
        if (a1 == nint.Zero) return false;

        // 刻意不在這裡呼叫 Control.Instance()：位址解不開時它擲例外，而這裡是 detour。
        var control = (Control*)controlAddress;
        if (control == null) return false;

        var localPlayer = (nint)control->LocalPlayer;
        if (localPlayer == nint.Zero) return false;

        return *(nint*)(a1 + 8) == localPlayer;
    }

    /// <summary>遊戲的 Control 全域單例位址；解不出來回 0（CS 對解不出的位址是擲例外，不是回 null）。</summary>
    private static nint ResolveControlAddress()
    {
        try
        {
            return (nint)Control.Instance();
        }
        catch (Exception)
        {
            return nint.Zero;
        }
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        guardEvaluatedThisFrame = false;
        var desired = ComputeMultiplier(out var state);
        activeMultiplier = desired;

        // 這一幀沒走到附近玩家偵測（離開白名單地圖、關掉選項、倍率改回 1…）＝這段觀察結束。
        if (guardObserving && !guardEvaluatedThisFrame) EndGuardObservation();
        speedState = state;

        LogIdentityGateOnce();

        if (Math.Abs(desired - lastLoggedMultiplier) <= Epsilon) return;

        // 寫 Debug：每次換地圖、附近有人時暫停／恢復都會印，量大。
        //    這一行是出事時唯一能證明「倍率什麼時候被改成多少、在哪張地圖」的證據，要查就開 Debug。
        Svc.Log.Debug(
            $"[{InternalName}] 速度倍率 {lastLoggedMultiplier:0.00}→{desired:0.00}（{DescribeCurrentMap()}）");

        lastLoggedMultiplier = desired;
    }

    /// <summary>
    /// 把「這支函式到底只為本地玩家跑、還是也為別的角色跑」寫進記錄，<b>各只寫一次</b>。
    /// </summary>
    /// <remarks>
    /// <para>📌 一律 <c>Information</c>：要使用者回報的診斷。</para>
    /// </remarks>
    private void LogIdentityGateOnce()
    {
        if (sawLocalPlayerApply && !reportedLocalPlayerApply)
        {
            reportedLocalPlayerApply = true;
            Svc.Log.Information($"[{InternalName}] 本地玩家身分閘門：已對本地玩家套用放大（閘門運作正常）。");
        }

        if (sawOtherCharacter && !reportedOtherCharacter)
        {
            reportedOtherCharacter = true;
            Svc.Log.Information(
                $"[{InternalName}] 本地玩家身分閘門：擋下了一次非本地玩家的呼叫" +
                "（證實這支函式也會為其他角色執行，閘門有實際作用）。");
        }
    }

    /// <summary>
    /// 算出這一幀該套用的倍率。<b>全部在 framework 執行緒上做完</b>，detour 只讀結果。
    /// </summary>
    /// <remarks>
    /// 🔑 每一個「不確定」都回 1（＝原速）。這個方向的錯誤是「該加速時沒加速」，
    /// 反方向是「在不該生效的地方偷偷改了移動速度」——後者才是會出事的那個。
    /// </remarks>
    private float ComputeMultiplier(out SpeedState state)
    {
        var configured = ClampedConfigMultiplier();
        var aboveOne = configured > MinMultiplier + Epsilon;

        // 短路的讀取順序：倍率是 1 就不讀物件表，沒登入（讀取畫面、登入前）就不呼叫 PvP 判斷。
        var local = aboveOne ? Svc.Objects.LocalPlayer : null;
        var inPvP = local != null && GameMain.IsInPvPArea();
        var territory = local != null ? CurrentTerritoryId() : 0u;
        var whitelisted = SpeedWhitelist.Contains(Config.TerritoryWhitelist, territory);

        state = SpeedGate.BeforeGuard(aboveOne, local != null, inPvP, whitelisted);
        if (state != SpeedState.Active) return 1f;

        // 放在戰鬥判斷之前：戰鬥中也要持續觀察，否則「戰鬥結束那一刻」拿到的是過期的判斷。
        var guard = Config.PauseWhenOthersNearby ? EvaluateNearbyGuard(local!, territory) : SpeedState.Active;
        state = SpeedGate.AfterGuard(guard, Config.OnlyOutOfCombat, Svc.Condition[ConditionFlag.InCombat]);
        return state == SpeedState.Active ? configured : 1f;
    }

    // ── 附近玩家偵測 ─────────────────────────────────────

    /// <summary>
    /// 附近玩家偵測的一幀：掃物件表、更新暫停狀態。<b>只在 framework 執行緒呼叫</b>；detour 不碰這裡的任何東西，
    /// 只讀結果折進去的 <see cref="activeMultiplier"/>。
    /// </summary>
    /// <returns><see cref="SpeedState.Active"/>＝放行；其餘＝這一幀暫停的原因。</returns>
    private SpeedState EvaluateNearbyGuard(IPlayerCharacter local, uint territory)
    {
        guardEvaluatedThisFrame = true;
        var now = Environment.TickCount64;

        // 直接換到另一張白名單地圖＝上一段觀察結束、新的一段從頭確認。
        if (guardObserving && guardObservedTerritory != territory) EndGuardObservation();

        if (!guardObserving)
        {
            // 剛進白名單地圖或剛打開選項時物件表可能還在補人：當成剛看到人，觀察滿一段時間才放行。
            guardObserving = true;
            guardLastSeenMs = now;
            guardObservedTerritory = territory;
            guardFarthestMeters = 0f;
            guardMaxOthers = 0;
        }

        // 切換區域（含深層迷宮換層）時物件表會清空重建，這段期間的「沒人」不可信。
        if (Svc.Condition[ConditionFlag.BetweenAreas] || Svc.Condition[ConditionFlag.BetweenAreas51])
        {
            guardLastSeenMs = now;
            guardOthersCount = 0;
            guardNearestMeters = float.NaN;
            guardResumeInMs = NearbyPlayerGuard.ResumeDelayMs;
            return SpeedState.Loading;
        }

        var radius = NearbyPlayerGuard.EffectiveRadius(Config.OthersNearbyRadius);
        ScanOtherPlayers(local, radius, out var counted, out var nearest, out var allOthers, out var farthest);

        guardMaxOthers = Math.Max(guardMaxOthers, allOthers);
        guardFarthestMeters = MathF.Max(guardFarthestMeters, farthest);

        var paused = NearbyPlayerGuard.ShouldPause(
            true, counted, now, guardLastSeenMs, NearbyPlayerGuard.ResumeDelayMs, out guardLastSeenMs);

        guardOthersCount = counted;
        guardNearestMeters = nearest;
        guardResumeInMs = paused && counted == 0
            ? (int)Math.Clamp(NearbyPlayerGuard.ResumeDelayMs - (now - guardLastSeenMs), 0, NearbyPlayerGuard.ResumeDelayMs)
            : 0;

        var result = !paused ? SpeedState.Active : counted > 0 ? SpeedState.OthersNearby : SpeedState.Confirming;
        LogGuardTransition(result, counted, nearest, radius);
        return result;
    }

    /// <summary>
    /// 物件表角色區裡的其他玩家。<paramref name="counted"/>＝半徑內、算數的人數；<paramref name="nearest"/>＝算數的人裡最近的
    /// （不看半徑，NaN＝沒有）；<paramref name="allOthers"/>／<paramref name="farthest"/>＝不分隊友、不看半徑，只供診斷。
    /// </summary>
    private void ScanOtherPlayers(
        IPlayerCharacter local, float radius, out int counted, out float nearest, out int allOthers, out float farthest)
    {
        var ignoreParty = Config.IgnorePartyMembers;
        Span<uint> party = stackalloc uint[8];
        var partyCount = ignoreParty ? ReadPartyEntityIds(party) : 0;
        var partyIds = party[..partyCount];

        // 自己的值當場抄走：迴圈裡的索引子會就地改寫共用包裝。
        var selfId = local.EntityId;
        var selfPosition = local.Position;

        counted = 0;
        nearest = float.NaN;
        allOthers = 0;
        farthest = 0f;

        var slots = Math.Min(CharacterSlots, Svc.Objects.Length);
        for (var i = 0; i < slots; i++)
        {
            if (Svc.Objects[i] is not IPlayerCharacter player) continue;

            var id = player.EntityId;
            var isSelf = id == selfId;
            var distance = Vector3.Distance(selfPosition, player.Position);
            if (!isSelf)
            {
                allOthers++;
                if (distance > farthest) farthest = distance;
            }

            if (!NearbyPlayerGuard.IsOtherPlayer(true, isSelf, partyIds.Contains(id), ignoreParty)) continue;

            if (float.IsNaN(nearest) || distance < nearest) nearest = distance;
            if (NearbyPlayerGuard.WithinRadius(distance, radius)) counted++;
        }
    }

    /// <summary>自己小隊（不含團隊裡的其他小隊）成員的 EntityId 抄進 <paramref name="buffer"/>，回傳個數。</summary>
    private static int ReadPartyEntityIds(Span<uint> buffer)
    {
        var groupManager = GroupManager.Instance();
        if (groupManager == null) return 0;

        var members = groupManager->MainGroup.PartyMembers;
        var n = Math.Min(Math.Min((int)groupManager->MainGroup.MemberCount, members.Length), buffer.Length);
        for (var i = 0; i < n; i++)
            buffer[i] = members[i].EntityId;
        return n;
    }

    /// <summary>
    /// 只記「開始因為有人而暫停」與「解除暫停」兩種轉換，各一行。
    /// </summary>
    /// <remarks>有人站在半徑邊界上時會在「有人／確認中」之間來回，那段不記——解除要等延遲走完，所以一輪最多兩行。</remarks>
    private void LogGuardTransition(SpeedState result, int counted, float nearest, float radius)
    {
        if (result == SpeedState.OthersNearby && !guardPauseLogged)
        {
            guardPauseLogged = true;
            Svc.Log.Information(
                $"[{InternalName}] 附近有其他玩家時強制停用：{RadiusText(radius)}內有 {counted} 位其他玩家" +
                $"（最近 {nearest:0.0} 米），暫停加速（{DescribeCurrentMap()}）。");
        }
        else if (result == SpeedState.Active && guardPauseLogged)
        {
            guardPauseLogged = false;
            Svc.Log.Information(
                $"[{InternalName}] 附近有其他玩家時強制停用：連續 {NearbyPlayerGuard.ResumeDelayMs / 1000} 秒" +
                $"附近沒有其他玩家，解除暫停（{DescribeCurrentMap()}）。");
        }
    }

    /// <summary>一段觀察結束。看過其他玩家時記一行物件表實際涵蓋的距離，給調整半徑當依據。</summary>
    private void EndGuardObservation()
    {
        if (guardMaxOthers > 0)
            Svc.Log.Information(
                $"[{InternalName}] 附近玩家偵測結束（{DescribeMap(guardObservedTerritory)}）：這段期間物件表裡的其他玩家" +
                $"最遠 {guardFarthestMeters:0.0} 米、同時最多 {guardMaxOthers} 位。");

        ResetNearbyGuard();
    }

    private void ResetNearbyGuard()
    {
        guardObserving = false;
        guardPauseLogged = false;
        guardLastSeenMs = NearbyPlayerGuard.NeverSeen;
        guardObservedTerritory = 0;
        guardFarthestMeters = 0f;
        guardMaxOthers = 0;
        guardOthersCount = 0;
        guardNearestMeters = float.NaN;
        guardResumeInMs = 0;
        speedState = SpeedState.Idle;
    }

    private static string RadiusText(float effectiveRadius) =>
        effectiveRadius <= 0f ? "物件表" : $"{effectiveRadius:0} 米";

    /// <summary>設定值一律夾在允許範圍內再用。</summary>
    /// <remarks>
    /// 🔴 夾限放在<b>讀取端</b>而不是只放在 UI：設定檔是純文字，使用者（或舊版本、或手改）
    /// 可以在裡面放任何數字。UI 擋得住滑桿，擋不住檔案。
    /// </remarks>
    private float ClampedConfigMultiplier() => Math.Clamp(Config.Multiplier, MinMultiplier, MaxMultiplier);

    /// <summary>目前所在地圖的 TerritoryType id；0＝不知道（未登入）。</summary>
    /// <remarks>那個值是 Dalamud 在進區時記下的，登出後不會歸零，所以先看有沒有登入。</remarks>
    private static uint CurrentTerritoryId() =>
        Svc.ClientState.IsLoggedIn ? Svc.ClientState.TerritoryType : 0u;

    private static string DescribeCurrentMap() => DescribeMap(CurrentTerritoryId());

    private static string DescribeMap(uint id)
    {
        if (id == 0) return "地圖未知";

        var name = MapName(id);
        return string.IsNullOrEmpty(name) ? $"地圖 {id}" : $"{name}（{id}）";
    }

    /// <summary>地圖的台服地名（TerritoryType → PlaceName）；查不到回空字串。</summary>
    private static string MapName(uint id) =>
        Svc.Data.GetExcelSheet<TerritoryType>().GetRowOrDefault(id)?.PlaceName.ValueNullable?.Name.ExtractText()
        ?? string.Empty;

    // ── 指令 ──────────────────────────────────────────────

    /// <summary><c>/tcspeed [倍率]</c>。不帶參數＝只顯示目前值，<b>不改任何東西</b>。</summary>
    private void OnCommand(string command, string arguments)
    {
        var args = arguments.Trim();

        if (args.Length == 0)
        {
            Svc.Chat.Print(
                $"[TC Toolbox] 移動速度倍率：設定值 {ClampedConfigMultiplier():0.00}，" +
                $"此刻實際生效 {activeMultiplier:0.00}（{DescribeCurrentMap()}）。");

            if (Config.PauseWhenOthersNearby)
                Svc.Chat.Print($"[TC Toolbox] 附近有其他玩家時強制停用：開（{GuardSettingsText()}）。" +
                               (GuardStatusText(speedState) ?? string.Empty));

            if (!sigResolved)
                Svc.Chat.Print("[TC Toolbox] ⚠ 特徵碼未命中，這個模組目前不會有任何作用。");
            else if (Config.TerritoryWhitelist.Count == 0)
                Svc.Chat.Print("[TC Toolbox] ⚠ 白名單是空的，任何地圖都不會生效。");

            Svc.Chat.Print($"[TC Toolbox] 要修改請用 {Command} <倍率>，範圍 {MinMultiplier:0.00}～{MaxMultiplier:0.00}。");
            return;
        }

        // ⚠️ 一律用 InvariantCulture 解析：使用者的地區設定可能把小數點當成逗號，
        //    那樣「1.25」會被解析成 125 再被夾到 1.5——是靜默的錯誤結果，不是解析失敗。
        if (!float.TryParse(args, NumberStyles.Float, CultureInfo.InvariantCulture, out var requested))
        {
            Svc.Chat.Print(
                $"[TC Toolbox] 看不懂「{args}」。用法：{Command} 1.25" +
                $"（範圍 {MinMultiplier:0.00}～{MaxMultiplier:0.00}；不帶參數＝只顯示目前值）。");
            return;
        }

        if (!float.IsFinite(requested))
        {
            Svc.Chat.Print($"[TC Toolbox] 「{args}」不是一個有效的倍率。");
            return;
        }

        var clamped = Math.Clamp(requested, MinMultiplier, MaxMultiplier);
        Config.Multiplier = clamped;
        Plugin.Instance.Config.Save();

        if (Math.Abs(clamped - requested) > Epsilon)
            Svc.Chat.Print($"[TC Toolbox] {requested:0.00} 超出允許範圍，已夾到 {clamped:0.00}。");
        else
            Svc.Chat.Print($"[TC Toolbox] 移動速度倍率已設為 {clamped:0.00}（只在白名單地圖內生效）。");
    }

    // ── 設定畫面 ──────────────────────────────────────────

    public override void DrawConfig()
    {
        if (!sigResolved && IsEnabled)
        {
            ImGui.TextColored(new Vector4(1f, 0.5f, 0.4f, 1f), "找不到移動速度倍率計算函式或本地玩家位址的特徵碼。");
            ImGui.TextDisabled("這個模組目前不會有任何作用（沒有掛任何 hook）。多半是遊戲更新過了。");
            ImGui.Separator();
        }

        // 🔴 這段紅字是使用者裁決的一部分，不要改成比較婉轉的說法。
        ImGui.TextColored(new Vector4(1f, 0.35f, 0.35f, 1f),
            "⚠ 超過遊戲原生速度有被伺服器判定異常的風險，後果自負。");
        ImGui.TextDisabled("上限 3.00（應要求自 1.50 提高）。伺服器端真正的容忍門檻無法從客戶端證明，");
        ImGui.TextDisabled("倍率開多高等於拿帳號試多少——原生疾跑約 1.3~1.4 倍，超過越多越顯眼。");

        ImGui.Separator();

        var multiplier = ClampedConfigMultiplier();
        ImGui.SetNextItemWidth(240f);
        if (ImGui.SliderFloat($"倍率（{MinMultiplier:0.00}～{MaxMultiplier:0.00}）##speedMultiplier",
                ref multiplier, MinMultiplier, MaxMultiplier, "%.2f"))
        {
            // 滑桿本身是連續的，這裡吸附到 0.05 一級，讓值永遠是兩位小數的整數級。
            var snapped = MathF.Round(multiplier / MultiplierStep) * MultiplierStep;
            Config.Multiplier = Math.Clamp(snapped, MinMultiplier, MaxMultiplier);
            Plugin.Instance.Config.Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"1.00 ＝ 原速（等於關閉）。每一級 {MultiplierStep:0.00}。\n" +
                             $"也可以用指令：{Command} 1.25");

        var outOfCombatOnly = Config.OnlyOutOfCombat;
        if (ImGui.Checkbox("只在非戰鬥中生效##speedOutOfCombat", ref outOfCombatOnly))
        {
            Config.OnlyOutOfCombat = outOfCombatOnly;
            Plugin.Instance.Config.Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("預設開。戰鬥中的位移最容易被伺服器端的檢查注意到，\n" +
                             "而副本裡真正想跑快的通常是打完之後趕路的那一段。");

        var guardOn = Config.PauseWhenOthersNearby;
        if (ImGui.Checkbox("附近有其他玩家時強制停用##speedNearbyGuard", ref guardOn))
        {
            Config.PauseWhenOthersNearby = guardOn;
            Plugin.Instance.Config.Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("預設開。附近出現其他玩家時，倍率立刻回到原速。\n" +
                             "只算其他玩家角色：自己、NPC、召喚物、劇情輔助器／親信戰友都不算。\n" +
                             $"其他玩家離開後，要連續 {NearbyPlayerGuard.ResumeDelayMs / 1000} 秒附近都沒人才恢復，" +
                             "避免有人站在邊界上時忽快忽慢；\n剛進白名單地圖或切換區域時也會先確認這麼久。");

        if (Config.PauseWhenOthersNearby)
        {
            using var indent = ImRaii.PushIndent();
            DrawNearbyGuardOptions();
        }

        ImGui.Separator();

        DrawWhitelist();

        ImGui.Separator();
        DrawStatus();
    }

    private void DrawNearbyGuardOptions()
    {
        var radius = NearbyPlayerGuard.EffectiveRadius(Config.OthersNearbyRadius);
        ImGui.SetNextItemWidth(240f);
        if (ImGui.SliderFloat("半徑##speedNearbyRadius", ref radius, 0f, NearbyPlayerGuard.MaxRadius,
                radius <= 0f ? "%.0f（不限距離）" : "%.0f 米"))
        {
            Config.OthersNearbyRadius = NearbyPlayerGuard.EffectiveRadius(MathF.Round(radius));
            Plugin.Instance.Config.Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("0＝物件表裡只要有其他玩家就停（遊戲只載入一定範圍內的玩家，範圍由伺服器決定）。\n" +
                             "預設 50 米。下方狀態會顯示目前最近的其他玩家有多遠，可以拿來調整。");

        var ignoreParty = Config.IgnorePartyMembers;
        if (ImGui.Checkbox("隊伍成員不算##speedNearbyIgnoreParty", ref ignoreParty))
        {
            Config.IgnorePartyMembers = ignoreParty;
            Plugin.Instance.Config.Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("預設關：隊友也算其他玩家——多人副本裡隊友就是最常看到你的人。\n" +
                             "打開後只看自己小隊以外的玩家（團隊任務裡其他小隊的人仍然算）。");
    }

    private void DrawStatus()
    {
        var active = activeMultiplier;
        if (active > 1f + Epsilon)
            ImGui.TextColored(new Vector4(0.45f, 0.9f, 0.5f, 1f),
                $"目前生效中：{active:0.00} 倍（{DescribeCurrentMap()}）");
        else
            ImGui.TextDisabled($"目前未生效（{DescribeCurrentMap()}）——原速。");

        var state = speedState;
        if (GuardStatusText(state) is { } guardText)
            ImGui.TextColored(new Vector4(1f, 0.85f, 0.35f, 1f), guardText);
        else if (ReasonText(state) is { } reason)
            ImGui.TextDisabled(reason);

        if (Config.PauseWhenOthersNearby && state is SpeedState.Active or SpeedState.InCombat)
            ImGui.TextDisabled(NearbySummaryText());

        ImGui.TextDisabled($"指令：{Command}（不帶參數＝只顯示，不改）");
    }

    /// <summary>附近玩家偵測造成的暫停；不是這三種狀態回 null。</summary>
    private string? GuardStatusText(SpeedState state) => state switch
    {
        SpeedState.OthersNearby =>
            $"因附近有 {guardOthersCount} 位其他玩家暫停中（最近 {guardNearestMeters:0.0} 米）。",
        SpeedState.Confirming =>
            $"確認附近沒有其他玩家中，{guardResumeInMs / 1000f:0.0} 秒後恢復。",
        SpeedState.Loading => "切換區域中：載入完成後會先確認附近沒有其他玩家。",
        _ => null,
    };

    private static string? ReasonText(SpeedState state) => state switch
    {
        SpeedState.MultiplierIsOne => "原因：倍率是 1.00（等於關閉）。",
        SpeedState.NotLoggedIn => "原因：不在遊戲中。",
        SpeedState.PvP => "原因：PvP 區域一律原速。",
        SpeedState.NotWhitelisted => "原因：目前地圖不在白名單裡。",
        SpeedState.InCombat => "原因：戰鬥中（已設定只在非戰鬥中生效）。",
        _ => null,
    };

    private string NearbySummaryText()
    {
        var nearest = guardNearestMeters;
        if (float.IsNaN(nearest))
            return $"附近玩家偵測：物件表裡沒有其他玩家{(Config.IgnorePartyMembers ? "（隊友不算）" : string.Empty)}。";

        var radius = NearbyPlayerGuard.EffectiveRadius(Config.OthersNearbyRadius);
        return $"附近玩家偵測：{RadiusText(radius)}內沒有其他玩家（最近的在 {nearest:0.0} 米）。";
    }

    private string GuardSettingsText()
    {
        var radius = NearbyPlayerGuard.EffectiveRadius(Config.OthersNearbyRadius);
        var range = radius <= 0f ? "不限距離" : $"半徑 {radius:0} 米";
        return $"{range}，{(Config.IgnorePartyMembers ? "隊友不算" : "隊友也算")}";
    }

    private void DrawWhitelist()
    {
        var whitelist = Config.TerritoryWhitelist;
        ImGui.Text($"白名單地圖（{whitelist.Count}）");

        if (whitelist.Count == 0)
            ImGui.TextColored(new Vector4(1f, 0.85f, 0.35f, 1f), "空的——目前不會在任何地圖生效。");

        // 「加入目前地圖」：最常用的一條路，而且不必打字就不會加錯。
        var currentId = CurrentTerritoryId();
        var alreadyListed = currentId != 0 && whitelist.Contains(currentId);
        var canAddCurrent = currentId != 0 && !alreadyListed;
        var addLabel = currentId == 0 ? "加入目前地圖" : $"加入目前地圖：{DescribeMap(currentId)}";

        if (!canAddCurrent) ImGui.BeginDisabled();
        var addCurrentClicked = ImGui.Button($"{addLabel}###speedAddCurrentMap");
        if (!canAddCurrent) ImGui.EndDisabled();

        // ⚠️ 停用中的項目預設不回報 hover，一定要 AllowWhenDisabled 才問得到，
        //    否則「按鈕灰掉又沒有說明」就是純粹的靜默失敗。
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(currentId == 0
                ? "讀不到目前所在的地圖（不在遊戲中）。"
                : alreadyListed
                    ? $"目前所在的 {DescribeMap(currentId)} 已經在白名單裡了。"
                    : $"把目前所在的 {DescribeMap(currentId)} 加進白名單（副本內外都算）。");

        if (addCurrentClicked && canAddCurrent)
        {
            whitelist.Add(currentId);
            Plugin.Instance.Config.Save();
        }

        DrawMapPicker();

        // 已選清單：一列一個，按一下就移除。
        foreach (var id in whitelist.OrderBy(x => x).ToList())
        {
            var name = MapName(id);
            var label = string.IsNullOrEmpty(name) ? $"（未具名地圖 {id}）" : $"{name}（{id}）";
            if (id == currentId) label += "　← 目前所在";

            var clicked = ImGui.Selectable($"{label}###speedWhitelist{id}", false);
            if (ImGui.IsItemHovered())
            {
                var duties = DutiesIn(id);
                ImGui.SetTooltip(duties.Length == 0 ? "點一下從白名單移除。" : $"對應副本：{duties}\n點一下從白名單移除。");
            }

            if (!clicked) continue;

            whitelist.Remove(id);
            Plugin.Instance.Config.Save();
        }

        if (whitelist.Count > 0)
            ImGui.TextDisabled("（點一列即可從白名單移除）");

        DrawLegacyDutyWhitelistNote();
    }

    /// <summary>舊版的副本白名單（升級時已換成地圖）；換不到地圖的那幾筆要在列上看得見。</summary>
    private void DrawLegacyDutyWhitelistNote()
    {
        var legacy = Config.DutyWhitelist;
        if (legacy.Count == 0) return;

        List<string> lines = new(legacy.Count);
        var unresolved = 0;
        foreach (var cfc in legacy.OrderBy(x => x))
        {
            var duty = DutySupportData.GetDutyName(cfc);
            var dutyText = string.IsNullOrEmpty(duty) ? $"副本 {cfc}" : $"{duty}（{cfc}）";
            var territory = DutySupportData.GetTerritoryType(cfc);
            if (territory == 0)
            {
                unresolved++;
                lines.Add($"{dutyText} → 找不到對應的地圖");
                continue;
            }

            var stillListed = Config.TerritoryWhitelist.Contains(territory);
            lines.Add($"{dutyText} → {DescribeMap(territory)}{(stillListed ? string.Empty : "（已不在白名單）")}");
        }

        if (unresolved > 0)
            ImGui.TextColored(new Vector4(1f, 0.85f, 0.35f, 1f),
                $"舊版副本白名單有 {unresolved} 筆找不到對應的地圖，沒有換過來（滑過看是哪幾筆）。");
        else
            ImGui.TextDisabled($"舊版副本白名單 {legacy.Count} 筆已在升級時換成地圖（原清單留在設定檔備查，不再用於判斷）。");

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(string.Join("\n", lines) +
                             (unresolved > 0 ? "\n\n找不到的那幾筆：進到那個副本後按「加入目前地圖」即可。" : string.Empty));
    }

    private void DrawMapPicker()
    {
        ImGui.SetNextItemWidth(240f);
        using var combo = ImRaii.Combo("搜尋加入##speedMapPicker", "搜尋地名或副本名…");
        if (!combo) return;

        var search = mapSearch;
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.InputTextWithHint("##speedMapSearch", "搜尋地名或副本名…", ref search, 64))
            mapSearch = search;
        ImGui.Separator();

        if (mapSearch.Length == 0)
        {
            ImGui.TextDisabled("輸入地名或副本名開始搜尋（地圖很多，不預先全列）。");
            return;
        }

        var whitelist = Config.TerritoryWhitelist;
        var shown = 0;
        foreach (var entry in mapIndex ??= BuildMapIndex())
        {
            if (!entry.Place.Contains(mapSearch, StringComparison.OrdinalIgnoreCase) &&
                !entry.Duties.Contains(mapSearch, StringComparison.OrdinalIgnoreCase)) continue;

            var label = entry.Duties.Length == 0
                ? $"{entry.Place}（{entry.Id}）"
                : $"{entry.Place}（{entry.Id}）· {entry.Duties}";
            if (ImGui.Selectable($"{label}###speedMapPick{entry.Id}", whitelist.Contains(entry.Id),
                    ImGuiSelectableFlags.DontClosePopups))
            {
                if (!whitelist.Remove(entry.Id))
                    whitelist.Add(entry.Id);
                Plugin.Instance.Config.Save();
            }

            if (++shown < 100) continue;

            ImGui.TextDisabled("…只顯示前 100 筆，請縮小搜尋範圍。");
            break;
        }

        if (shown == 0)
            ImGui.TextDisabled("沒有符合的地圖。");
    }

    /// <summary>有台服地名的地圖，附上以它為場地的副本名，給搜尋用。</summary>
    private List<MapPickerEntry> BuildMapIndex()
    {
        List<MapPickerEntry> list = [];
        foreach (var row in Svc.Data.GetExcelSheet<TerritoryType>())
        {
            var place = row.PlaceName.ValueNullable?.Name.ExtractText();
            if (string.IsNullOrWhiteSpace(place)) continue;
            list.Add(new MapPickerEntry(row.RowId, place, DutiesIn(row.RowId)));
        }

        return list;
    }

    /// <summary>以這張地圖為場地的具名副本（超過三個只列前三個）；沒有回空字串。</summary>
    private string DutiesIn(uint territory)
    {
        dutyNamesByTerritory ??= BuildDutyNamesByTerritory();
        return dutyNamesByTerritory.GetValueOrDefault(territory, string.Empty);
    }

    private static Dictionary<uint, string> BuildDutyNamesByTerritory()
    {
        var names = new Dictionary<uint, List<string>>();
        foreach (var row in Svc.Data.GetExcelSheet<ContentFinderCondition>())
        {
            var territory = row.TerritoryType.RowId;
            var name = row.Name.ExtractText();

            // ContentFinderCondition 有大量名稱為空的佔位列（未開放／非任務），列出來也沒有意義。
            if (territory == 0 || string.IsNullOrWhiteSpace(name)) continue;

            if (!names.TryGetValue(territory, out var list)) names[territory] = list = [];
            if (!list.Contains(name)) list.Add(name);
        }

        return names.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.Count <= 3
                ? string.Join("、", kv.Value)
                : $"{string.Join("、", kv.Value.Take(3))} 等 {kv.Value.Count} 個");
    }
}

/// <summary>這一幀倍率為什麼是這個值。</summary>
internal enum SpeedState
{
    Idle = 0,
    MultiplierIsOne,
    NotLoggedIn,
    PvP,
    NotWhitelisted,
    Loading,
    OthersNearby,
    Confirming,
    InCombat,
    Active,
}

/// <summary>
/// 倍率閘門的判定順序。純函式：不碰遊戲、不碰設定，離線可測。
/// </summary>
/// <remarks>
/// 順序就是契約：倍率 → 登入 → PvP → 白名單地圖 → 附近玩家偵測 → 戰鬥。
/// 刻意沒有「在不在副本」這一道：白名單比對的是地圖 id，在白名單地圖裡副本內外一樣生效。
/// </remarks>
internal static class SpeedGate
{
    /// <summary>附近玩家偵測之前的幾道；<see cref="SpeedState.Active"/>＝放行到附近玩家偵測。</summary>
    public static SpeedState BeforeGuard(bool multiplierAboveOne, bool loggedIn, bool inPvP, bool whitelisted)
    {
        if (!multiplierAboveOne) return SpeedState.MultiplierIsOne;
        if (!loggedIn) return SpeedState.NotLoggedIn;

        // 🔴 PvP 一律排除，不看白名單。移動速度在 PvP 裡是對其他玩家的直接優勢。
        if (inPvP) return SpeedState.PvP;

        return whitelisted ? SpeedState.Active : SpeedState.NotWhitelisted;
    }

    /// <summary>附近玩家偵測之後；<paramref name="guard"/>＝偵測結果（選項關著時傳 <see cref="SpeedState.Active"/>）。</summary>
    public static SpeedState AfterGuard(SpeedState guard, bool onlyOutOfCombat, bool inCombat)
    {
        if (guard != SpeedState.Active) return guard;
        return onlyOutOfCombat && inCombat ? SpeedState.InCombat : SpeedState.Active;
    }
}

/// <summary>白名單比對與舊版副本白名單的轉換。純函式：不碰遊戲、不碰設定物件，離線可測。</summary>
internal static class SpeedWhitelist
{
    /// <summary>目前地圖在不在白名單；<paramref name="territoryId"/> 為 0（不知道）一律不在。</summary>
    public static bool Contains(IReadOnlySet<uint> whitelist, uint territoryId) =>
        territoryId != 0 && whitelist.Contains(territoryId);

    /// <summary>
    /// 把舊版副本白名單（ContentFinderCondition 列號）換成地圖 id 併進 <paramref name="territories"/>。
    /// 只加不刪；<paramref name="legacyDuties"/> 本身不動。
    /// </summary>
    /// <param name="dutyToTerritory">副本列號 → 地圖 id；0＝查不到。</param>
    /// <returns>每一筆舊值與換到的地圖 id，依舊值排序；地圖 id 0＝換不到、沒有加進去。</returns>
    public static List<(uint Duty, uint Territory)> ConvertLegacyDuties(
        IEnumerable<uint> legacyDuties, Func<uint, uint> dutyToTerritory, ISet<uint> territories)
    {
        List<(uint Duty, uint Territory)> result = [];
        foreach (var duty in legacyDuties.OrderBy(x => x))
        {
            var territory = duty == 0 ? 0u : dutyToTerritory(duty);
            if (territory != 0) territories.Add(territory);
            result.Add((duty, territory));
        }

        return result;
    }
}

/// <summary>
/// 「附近有其他玩家就暫停加速」的判定。純函式：不碰遊戲物件、不碰設定，離線可測。
/// </summary>
internal static class NearbyPlayerGuard
{
    /// <summary>「還沒看過任何人」的哨兵值；不可拿來做減法。</summary>
    public const long NeverSeen = long.MinValue;

    /// <summary>其他玩家離開後，要連續這麼久都沒人才恢復加速（毫秒）。</summary>
    /// <remarks>
    /// 擋的是有人站在半徑邊界、或物件表在邊界上生成／移除時每幀進出造成的忽快忽慢；
    /// 同時是剛進白名單地圖、換層時物件表還在補人的觀察期。遠大於幀間隔，又短到不會讓人覺得「怎麼還沒恢復」。
    /// </remarks>
    public const int ResumeDelayMs = 3000;

    /// <summary>半徑上限（米）。</summary>
    public const float MaxRadius = 200f;

    /// <summary>設定的半徑換成實際用的值；0＝不限距離。NaN、負值一律當 0（最保守）。</summary>
    public static float EffectiveRadius(float configured) =>
        float.IsFinite(configured) && configured > 0f ? MathF.Min(configured, MaxRadius) : 0f;

    /// <summary>物件表角色區裡的一個物件算不算「其他玩家」（還沒看距離）。</summary>
    public static bool IsOtherPlayer(bool isPlayer, bool isSelf, bool isPartyMember, bool ignorePartyMembers) =>
        isPlayer && !isSelf && !(ignorePartyMembers && isPartyMember);

    /// <summary>距離在不在半徑內；<paramref name="effectiveRadius"/> 為 0＝不看距離。距離是 NaN 時算在內（寧可多停一次）。</summary>
    public static bool WithinRadius(float distance, float effectiveRadius) =>
        effectiveRadius <= 0f || !(distance > effectiveRadius);

    /// <summary>這一幀要不要暫停加速。</summary>
    /// <param name="lastSeenMs">上一次看到其他玩家的時間；<see cref="NeverSeen"/>＝從沒看過。</param>
    /// <param name="newLastSeenMs">下一幀要傳回來的 <paramref name="lastSeenMs"/>。</param>
    public static bool ShouldPause(
        bool enabled, int othersNearby, long nowMs, long lastSeenMs, int resumeDelayMs, out long newLastSeenMs)
    {
        if (!enabled)
        {
            newLastSeenMs = NeverSeen;
            return false;
        }

        if (othersNearby > 0)
        {
            newLastSeenMs = nowMs;
            return true;
        }

        newLastSeenMs = lastSeenMs;
        return lastSeenMs != NeverSeen && nowMs - lastSeenMs < resumeDelayMs;
    }
}

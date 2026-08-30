using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Hooking;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using Lumina.Excel.Sheets;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 移動速度倍率：只在使用者列進白名單的副本裡，把角色移動速度乘上一個倍率。
/// </summary>
/// <remarks>
/// <para>
/// 🔑 <b>hook 的是哪一支、為什麼是那一支（2026-08-30 對台服 7.20 主程式離線鑑識，全部可重驗）</b>
/// </para>
/// <list type="bullet">
/// <item>
/// 特徵碼 <c>E8 ?? ?? ?? ?? 44 0F 28 D8 45 0F 57 D2</c> 在 <c>.text</c> <b>唯一命中</b>
/// （命中位址 <c>0x14085A956</c>，是一條 <c>call</c>）。Dalamud 的 <c>ScanText</c> 會自動跟隨
/// 開頭的 E8 rel32，所以解出來的是<b>被呼叫的那支函式</b>＝<c>0x1408903B0</c>。
/// </item>
/// <item>
/// <c>0x1408903B0</c> 有 <b>8 個 call xref</b>（0x14085A956、0x14085B476、0x14085F3E1、
/// 0x14085F8B2、0x141712AF0、0x141715139、0x14171B43E、0x14171B476），
/// <b>不是內聯後的死碼</b>——這條是必驗的，艦隊有「特徵碼唯一命中但函式零引用、hook 靜默永不觸發」的前例。
/// </item>
/// <item>
/// <b>回傳值語意＝倍率，不是絕對速度</b>（已讀反組譯逐條確認）：函式尾端是
/// <c>cvtdq2ps</c> 之後 <c>divss xmm0, [0x141FDB208]</c>，而該常數實測 <b>= 100.0f</b>；
/// 另一條「取不到目標就早退」的路徑回傳 <c>[0x141FDB168]</c> 實測 <b>= 1.0f</b>。
/// 也就是 <c>回傳 = max(速度百分比, 0) / 100</c>，<b>1.0 ＝ 原速</b>。
/// </item>
/// <item>
/// <b>遊戲自己就是這樣用它的</b>：<c>0x141712A75</c> 那支函式先依移動方式選出基礎速度到 xmm6
/// （站在地上走是 <c>[走路控制器 + 0x58]</c>，坐騎／飛行／游泳走 <c>[[走路控制器 + 0x50] + 0xC/0x14/0x18]</c>），
/// 然後 <c>call 0x1408903B0</c> 取得倍率，最後 <c>mulss xmm0, xmm6</c>。
/// 也就是<b>最終速度 ＝ 基礎速度 × 本函式回傳值</b>——我們乘在回傳值上，等價於乘在最終速度上，
/// 而且<b>坐騎／飛行／游泳／走路全部共用同一支</b>，不必分別處理。
/// </item>
/// <item>
/// 參數 <c>a1</c> 是一個容器結構：函式一進去就 <c>mov rcx,[rcx+8]</c> 取出角色指標再打它的虛表
/// （<c>[vtbl+0x270]</c>／<c>[vtbl+0x278]</c>）。這一點被用來做<b>本地玩家身分閘門</b>，見 <see cref="IsLocalPlayer"/>。
/// </item>
/// <item>
/// 這支函式<b>有 8 個呼叫點，而且無法離線證明它只為本地玩家執行</b>
/// （其中幾個落在 4557／6148 位元組的巨大鏈結函式裡）。所以 detour <b>不假設</b>它只跑本地玩家，
/// 而是每一次呼叫都拿 <c>a1+8</c> 的角色指標與 <c>Control.Instance()-&gt;LocalPlayer</c> 比對，
/// 不是本人就原值放行。<b>這把「假設」換成了「每次都檢查」。</b>
/// </item>
/// </list>
/// <para>
/// 🔴🔴 <b>detour 契約：不做任何「沒有被原函式證明過」的解參考。</b>
/// <c>a1</c> 原封不動轉給原函式；唯一一處解參考是 <c>a1+8</c>，而且<b>只在原函式成功返回之後</b>才做
/// ——原函式自己進門就無條件解參考同一個位址，所以它順利返回就是那個位址可讀的證明
/// （詳見 <see cref="IsLocalPlayer"/>）。除此之外只有 float 運算。
/// AccessViolationException 在 .NET Core 是 corrupted-state exception，<c>try/catch</c> 攔不到，
/// 所以防護不能靠例外隔離，只能靠<b>不做沒有被證明過的解參考</b>。
/// 要不要生效的判斷（在不在副本、在不在白名單、有沒有在戰鬥）<b>全部在 framework 執行緒上每幀算完</b>，
/// 結果寫進 <see cref="activeMultiplier"/> 這個 <c>volatile float</c>，detour 只讀它。
/// </para>
/// <para>
/// 🔴 <b>所有失效形式都是 no-op（回傳原值），不是崩潰</b>：特徵碼解不到＝不掛 hook 也不記錯誤級記錄；
/// 倍率是 1＝直接回原值；不在白名單副本＝倍率被算成 1；原值不是有限數＝回原值；
/// 停用時先把倍率歸 1 再拆 hook。
/// </para>
/// <para>
/// ⚠️ <b>與 BossModReborn 的關係</b>：BMR 用<b>同一條特徵碼</b>每幀呼叫這支函式來估玩家速度
/// （<c>WorldStateGameSync</c>）。它自己組的 <c>CharacterContainer</c> 是
/// <c>[FieldOffset(0x8)] Character*</c> 且填本地玩家，<b>通得過</b>我們的身分閘門，
/// 所以它讀到的是<b>放大後</b>的倍率——對它的尋路是正確的方向（它會知道角色跑比較快）。
/// </para>
/// <para>
/// 🔴 <b>使用者裁決：預設關、白名單預設空。</b>開了模組但一個副本都沒加＝完全不動作。
/// 倍率上限刻意壓在 1.5：伺服器對位移速度的容忍度<b>無法離線證明</b>，見設定畫面上的紅字。
/// </para>
/// </remarks>
public sealed unsafe class MovementSpeedMultiplier : TcModule
{
    public override string InternalName => "MovementSpeedMultiplier";

    public override string DisplayName => "移動速度倍率（限白名單副本）";

    public override string Description =>
        "在你指定的副本裡把移動速度乘上一個倍率。預設關、白名單預設是空的——" +
        "沒加副本就完全不會動作。只在副本內生效，外面的世界一律原速。";

    public override ModuleCategory Category => ModuleCategory.Combat;

    public override bool HasConfigUI => true;

    /// <summary>查看／設定倍率的指令。</summary>
    public const string Command = "/tcspeed";

    /// <summary>
    /// 移動速度倍率計算函式的呼叫點特徵碼（台服 7.20 唯一命中，跟隨 E8 後＝<c>0x1408903B0</c>）。
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
    /// 🔴 1.5 是<b>刻意保守</b>的數字，不是量到的安全值。遊戲原生的疾跑約是 1.3~1.4 倍，
    /// 所以 1.5 大致落在「客戶端本來就會出現的速度」的邊緣。
    /// <b>伺服器端對位移速度的容忍門檻無法離線證明</b>，調高這個常數等於拿帳號去試。
    /// </remarks>
    public const float MaxMultiplier = 1.5f;

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
    /// 沒有 volatile 的話 JIT 可以把這個讀取提升到迴圈外，表現是「切出白名單副本後速度沒有立刻恢復」。
    /// <para>📌 型別刻意是 <c>float</c> 不是 <c>double</c>——C# 不允許 <c>volatile double</c>。</para>
    /// </remarks>
    private volatile float activeMultiplier = 1f;

    /// <summary>特徵碼有沒有解到。解不到＝這個模組整個是 no-op，UI 與指令都要說出來。</summary>
    private bool sigResolved;

    /// <summary>上一次寫進記錄的倍率，用來只在<b>變化時</b>記一行，而不是每幀洗記錄。</summary>
    private float lastLoggedMultiplier = 1f;

    /// <summary>detour 曾經真的對<b>本地玩家</b>套用過放大（只由 detour 寫入，framework 執行緒讀）。</summary>
    private volatile bool sawLocalPlayerApply;

    /// <summary>detour 曾經在「本來要放大」時擋下<b>非本地玩家</b>的呼叫。</summary>
    /// <remarks>
    /// 🔑 這兩個旗標存在的唯一目的是把「這支函式到底會不會為別的角色跑」這個
    /// <b>離線證明不了</b>的問題，變成使用者記錄裡看得到的事實。
    /// <para>
    /// 🔴 型別是 <c>volatile bool</c> 而不是計數器：detour 裡只准做「存一個常數」這種
    /// 不配置記憶體、不會擲例外的動作。記錄要寫在 framework 執行緒上，<b>不准寫在 detour 裡</b>
    /// ——那是每幀都會走的路徑，而且 <c>Svc.Log</c> 會配置字串。
    /// </para>
    /// </remarks>
    private volatile bool sawOtherCharacter;

    private bool reportedLocalPlayerApply;
    private bool reportedOtherCharacter;

    private MovementSpeedMultiplierConfig Config => Plugin.Instance.Config.MovementSpeedMultiplier;

    // ── UI 狀態 ──
    private string dutySearch = string.Empty;

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
                    "找不到遊戲的移動速度倍率計算函式（多半是遊戲更新過了）。\n" +
                    "模組不會掛任何 hook，遊戲行為與關閉時完全相同。");

            if (Config.DutyWhitelist.Count == 0)
                return new ModuleNotice(
                    ModuleNoticeLevel.Unknown,
                    "白名單是空的，不會在任何副本生效",
                    "在設定裡加入副本之後才會作用。這是刻意的預設：不加就完全不動作。");

            return null;
        }
    }

    protected override void OnEnable()
    {
        activeMultiplier = 1f;
        lastLoggedMultiplier = 1f;
        ResetIdentityGateDiagnostics();

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
            $"白名單 {Config.DutyWhitelist.Count} 個副本，設定倍率 {ClampedConfigMultiplier():0.00}。");
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
    /// <c>a1</c> 只是原封不動轉給原函式；我們碰的只有它的回傳值與一個 <c>volatile float</c>。
    /// <para>
    /// 📌 <c>hook</c> 為 <c>null</c> 時回傳 <b>1.0</b> 而不是 0：1.0 正好是遊戲自己那條
    /// 「取不到目標就早退」路徑的回傳值（已離線讀出常數 <c>0x141FDB168</c> ＝ 1.0f），
    /// 也就是「原速」。回 0 會讓角色完全不能動。
    /// 這條路只有在停用與 detour 撞在一起的極短窗口才走得到。
    /// </para>
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
    /// 🔴 <b>為什麼這裡的解參考是安全的（而且只有這個順序安全）</b>：
    /// 這個方法只在 <c>OriginalDisposeSafe</c> <b>成功返回之後</b>才被呼叫。
    /// 原函式（<c>0x1408903B0</c>）進去的前三條指令就是
    /// <c>mov rdi,rcx</c> ／ <c>mov rcx,[rcx+8]</c> ／ <c>mov rax,[rcx]</c>——
    /// 它<b>無條件地</b>解參考 <c>a1+8</c>，而且還接著解參考那個角色指標去打它的虛表。
    /// 所以「原函式已經順利返回」本身就是「<c>a1+8</c> 這一刻讀得到」的<b>證明</b>，
    /// 同一條呼叫、同一個執行緒、中間沒有讓出。我們沒有引進任何新的解參考風險。
    /// <para>
    /// 🔴 <b>不可以把這個呼叫搬到 <c>original</c> 之前。</b>搬了就變成我們自己先賭一把，
    /// 而 AccessViolationException 在 .NET Core 是 corrupted-state exception，<c>try/catch</c> 根本攔不到。
    /// </para>
    /// <para>
    /// 📌 比對的是<b>位址</b>：CS 的 <c>BattleChara</c> 帶 <c>[Inherits&lt;Character&gt;]</c>，
    /// 基底型別就在偏移 0，所以同一個物件的 <c>BattleChara*</c> 與 <c>Character*</c> 是同一個位址。
    /// </para>
    /// <para>
    /// 📌 BossModReborn 自己組的 <c>CharacterContainer</c> 也是 <c>[FieldOffset(0x8)] Character*</c>
    /// 且填的是本地玩家，所以它每幀那一發<b>仍然</b>拿得到放大後的倍率，不受這道閘門影響。
    /// </para>
    /// <para>
    /// 🔑 讀不到就回 <c>false</c>（＝不放大）。這個方向的錯誤是「該加速時沒加速」，
    /// 反方向是「對不該碰的角色改了速度」。
    /// </para>
    /// </remarks>
    private static bool IsLocalPlayer(nint a1)
    {
        if (a1 == nint.Zero) return false;

        // ⚠️ 這個判空不是多餘的：Control.Instance() 是 lea 型的 [StaticAddress]，
        //    正常情況下永遠不是 null——但特徵碼解不開時 CS 會讓它回 0。
        var control = Control.Instance();
        if (control == null) return false;

        var localPlayer = (nint)control->LocalPlayer;
        if (localPlayer == nint.Zero) return false;

        return *(nint*)(a1 + 8) == localPlayer;
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        var desired = ComputeMultiplier();
        activeMultiplier = desired;

        LogIdentityGateOnce();

        if (Math.Abs(desired - lastLoggedMultiplier) <= Epsilon) return;

        // 🔴 一律 Information：使用者的記錄等級會濾掉 Debug／Verbose。
        //    這一行是出事時唯一能證明「倍率什麼時候被改成多少、在哪個副本」的證據。
        Svc.Log.Information(
            $"[{InternalName}] 速度倍率 {lastLoggedMultiplier:0.00}→{desired:0.00}（{DescribeCurrentDuty()}）");

        lastLoggedMultiplier = desired;
    }

    /// <summary>
    /// 算出這一幀該套用的倍率。<b>全部在 framework 執行緒上做完</b>，detour 只讀結果。
    /// </summary>
    /// <remarks>
    /// 🔑 每一個「不確定」都回 1（＝原速）。這個方向的錯誤是「該加速時沒加速」，
    /// 反方向是「在不該生效的地方偷偷改了移動速度」——後者才是會出事的那個。
    /// </remarks>
    /// <summary>
    /// 把「這支函式到底只為本地玩家跑、還是也為別的角色跑」寫進記錄，<b>各只寫一次</b>。
    /// </summary>
    /// <remarks>
    /// 🔑 這件事<b>離線證明不了</b>（那支函式有 8 個呼叫點，其中幾個落在數千位元組的巨大鏈結函式裡）。
    /// 與其寫一句「假設只有本地玩家會走到」，不如讓使用者的記錄直接回答它。
    /// <para>📌 一律 <c>Information</c>：使用者的記錄等級會濾掉 Debug／Verbose。</para>
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

    private float ComputeMultiplier()
    {
        var configured = ClampedConfigMultiplier();
        if (configured <= MinMultiplier + Epsilon) return 1f;

        // 不在遊戲裡（讀取畫面、登入前）＝不生效。
        if (Svc.Objects.LocalPlayer == null) return 1f;

        // 🔴 PvP 一律排除，不看白名單。移動速度在 PvP 裡是對其他玩家的直接優勢。
        if (GameMain.IsInPvPArea()) return 1f;

        if (!IsBoundByDuty()) return 1f;

        var cfc = CurrentContentFinderConditionId();
        if (cfc == 0 || !Config.DutyWhitelist.Contains(cfc)) return 1f;

        if (Config.OnlyOutOfCombat && Svc.Condition[ConditionFlag.InCombat]) return 1f;

        return configured;
    }

    /// <summary>設定值一律夾在允許範圍內再用。</summary>
    /// <remarks>
    /// 🔴 夾限放在<b>讀取端</b>而不是只放在 UI：設定檔是純文字，使用者（或舊版本、或手改）
    /// 可以在裡面放任何數字。UI 擋得住滑桿，擋不住檔案。
    /// </remarks>
    private float ClampedConfigMultiplier() => Math.Clamp(Config.Multiplier, MinMultiplier, MaxMultiplier);

    private static bool IsBoundByDuty() =>
        Svc.Condition[ConditionFlag.BoundByDuty] ||
        Svc.Condition[ConditionFlag.BoundByDuty56] ||
        Svc.Condition[ConditionFlag.BoundByDuty95];

    /// <summary>目前所在副本的 <c>ContentFinderCondition</c> 列號；0＝不在副本裡／讀不到。</summary>
    /// <remarks>
    /// 📌 這裡是這個模組唯一一處解參考，而且只在 framework 執行緒上、當幀取用、判空之後才讀，
    /// <b>不跨幀保存</b>。detour 裡沒有任何解參考。
    /// </remarks>
    private static uint CurrentContentFinderConditionId()
    {
        var gameMain = GameMain.Instance();
        return gameMain == null ? 0u : gameMain->CurrentContentFinderConditionId;
    }

    private static string DescribeCurrentDuty()
    {
        var id = CurrentContentFinderConditionId();
        if (id == 0) return "不在副本內";

        var name = DutyName(id);
        return string.IsNullOrEmpty(name) ? $"副本 {id}" : $"{name}（{id}）";
    }

    private static string DutyName(uint id)
    {
        var row = Svc.Data.GetExcelSheet<ContentFinderCondition>().GetRowOrDefault(id);
        return row == null ? string.Empty : row.Value.Name.ExtractText();
    }

    // ── 指令 ──────────────────────────────────────────────

    /// <summary><c>/tcspeed [倍率]</c>。不帶參數＝只顯示目前值，<b>不改任何東西</b>。</summary>
    private void OnCommand(string command, string arguments)
    {
        var args = arguments.Trim();

        if (args.Length == 0)
        {
            Svc.Chat.Print(
                $"[TC Toolbox] 移動速度倍率：設定值 {ClampedConfigMultiplier():0.00}，" +
                $"此刻實際生效 {activeMultiplier:0.00}（{DescribeCurrentDuty()}）。");

            if (!sigResolved)
                Svc.Chat.Print("[TC Toolbox] ⚠ 特徵碼未命中，這個模組目前不會有任何作用。");
            else if (Config.DutyWhitelist.Count == 0)
                Svc.Chat.Print("[TC Toolbox] ⚠ 白名單是空的，任何副本都不會生效。");

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
            Svc.Chat.Print($"[TC Toolbox] 移動速度倍率已設為 {clamped:0.00}（只在白名單副本內生效）。");
    }

    // ── 設定畫面 ──────────────────────────────────────────

    public override void DrawConfig()
    {
        if (!sigResolved && IsEnabled)
        {
            ImGui.TextColored(new Vector4(1f, 0.5f, 0.4f, 1f), "找不到移動速度倍率計算函式的特徵碼。");
            ImGui.TextDisabled("這個模組目前不會有任何作用（沒有掛任何 hook）。多半是遊戲更新過了。");
            ImGui.Separator();
        }

        // 🔴 這段紅字是使用者裁決的一部分，不要改成比較婉轉的說法。
        ImGui.TextColored(new Vector4(1f, 0.35f, 0.35f, 1f),
            "⚠ 超過遊戲原生速度有被伺服器判定異常的風險，後果自負。");
        ImGui.TextDisabled("上限刻意壓在 1.50。伺服器端真正的容忍門檻無法從客戶端證明，");
        ImGui.TextDisabled("所以這個上限是保守猜測，不是量到的安全值。");

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

        ImGui.Separator();

        DrawWhitelist();

        ImGui.Separator();
        DrawStatus();
    }

    private void DrawStatus()
    {
        var active = activeMultiplier;
        if (active > 1f + Epsilon)
            ImGui.TextColored(new Vector4(0.45f, 0.9f, 0.5f, 1f),
                $"目前生效中：{active:0.00} 倍（{DescribeCurrentDuty()}）");
        else
            ImGui.TextDisabled($"目前未生效（{DescribeCurrentDuty()}）——原速。");

        ImGui.TextDisabled($"指令：{Command}（不帶參數＝只顯示，不改）");
    }

    private void DrawWhitelist()
    {
        ImGui.Text($"白名單副本（{Config.DutyWhitelist.Count}）");

        if (Config.DutyWhitelist.Count == 0)
            ImGui.TextColored(new Vector4(1f, 0.85f, 0.35f, 1f), "空的——目前不會在任何副本生效。");

        // 「加入目前所在副本」：最常用的一條路，而且不必打字就不會加錯。
        var currentId = CurrentContentFinderConditionId();
        var canAddCurrent = currentId != 0 && !Config.DutyWhitelist.Contains(currentId);

        if (!canAddCurrent) ImGui.BeginDisabled();
        var addCurrentClicked = ImGui.Button("加入目前所在副本");
        if (!canAddCurrent) ImGui.EndDisabled();

        // ⚠️ 停用中的項目預設不回報 hover，一定要 AllowWhenDisabled 才問得到，
        //    否則「按鈕灰掉又沒有說明」就是純粹的靜默失敗。
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(currentId == 0
                ? "現在不在副本裡。"
                : Config.DutyWhitelist.Contains(currentId)
                    ? $"{DescribeCurrentDuty()} 已經在白名單裡了。"
                    : $"把 {DescribeCurrentDuty()} 加進白名單。");

        if (addCurrentClicked && canAddCurrent)
        {
            Config.DutyWhitelist.Add(currentId);
            Plugin.Instance.Config.Save();
        }

        ImGui.SameLine();
        DrawDutyMultiSelect();

        // 已選清單：一列一個，按一下就移除。
        foreach (var id in Config.DutyWhitelist.OrderBy(x => x).ToList())
        {
            var name = DutyName(id);
            var label = string.IsNullOrEmpty(name) ? $"（未具名副本 {id}）" : $"{name}（{id}）";

            if (!ImGui.Selectable($"{label}##whitelist{id}", false)) continue;

            Config.DutyWhitelist.Remove(id);
            Plugin.Instance.Config.Save();
        }

        if (Config.DutyWhitelist.Count > 0)
            ImGui.TextDisabled("（點一列即可從白名單移除）");
    }

    private void DrawDutyMultiSelect()
    {
        ImGui.SetNextItemWidth(240f);
        using var combo = ImRaii.Combo("加入副本##speedDutyPicker", "搜尋副本名…");
        if (!combo) return;

        var search = dutySearch;
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.InputTextWithHint("##speedDutySearch", "搜尋副本名…", ref search, 64))
            dutySearch = search;
        ImGui.Separator();

        if (dutySearch.Length == 0)
        {
            ImGui.TextDisabled("輸入副本名開始搜尋（副本很多，不預先全列）。");
            return;
        }

        var shown = 0;
        foreach (var row in Svc.Data.GetExcelSheet<ContentFinderCondition>())
        {
            var name = row.Name.ExtractText();

            // ContentFinderCondition 有大量名稱為空的佔位列（未開放／非任務），列出來也沒有意義。
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (!name.Contains(dutySearch, StringComparison.OrdinalIgnoreCase)) continue;

            var chosen = Config.DutyWhitelist.Contains(row.RowId);
            if (ImGui.Selectable($"{name}（{row.RowId}）", chosen, ImGuiSelectableFlags.DontClosePopups))
            {
                if (!Config.DutyWhitelist.Remove(row.RowId))
                    Config.DutyWhitelist.Add(row.RowId);
                Plugin.Instance.Config.Save();
            }

            if (++shown < 100) continue;

            ImGui.TextDisabled("…只顯示前 100 筆，請縮小搜尋範圍。");
            break;
        }

        if (shown == 0)
            ImGui.TextDisabled("沒有符合的副本。");
    }
}

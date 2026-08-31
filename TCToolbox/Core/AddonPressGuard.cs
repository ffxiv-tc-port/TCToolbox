using System;
using System.Collections.Generic;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace TCToolbox.Core;

/// <summary>
/// 「同一扇視窗按過就不要再按，直到它真的收掉」的共用閘門。
/// </summary>
/// <remarks>
/// 🔴🔴 <b>存在的唯一理由是 2026-08-31 的實機崩潰</b>（<c>crash-20260831205734</c>，
/// <c>C0000005</c>，堆疊 <c>ConfirmRequest→FireCallback→ffxiv_dx11+5BE756</c>）：
/// <c>SelectYesno</c> 按下「是」之後有<b>「正在關閉中」的幾幀</b>，這段期間
/// <c>GetAddonByName</c> 仍然回得到實例、<c>IsVisible</c> 與
/// <c>UldManager.LoadedState == Loaded</c> 也<b>三關全過</b>——
/// 也就是說 <see cref="UiHelper.IsReady"/>／<see cref="UiHelper.IsAddonReady"/>
/// <b>擋不住這個窗口</b>。此時再對它 <c>FireCallback</c> 就是原生
/// AccessViolationException（.NET Core 的 corrupted-state exception，
/// <c>try/catch</c> 與任何 SafeWrapper 都攔不到，遊戲當場關閉）。
/// <para>
/// 🔑 <b>做法</b>：按下之前先登記「這個名字底下的哪一個實例位址被按過」，
/// 在觀察到那扇窗<b>真的走完生命週期</b>之前不准再按同一個位址。
/// 解除封鎖的觀察點是 <c>AddonLifecycle</c> 的兩個事件（<b>不是輪詢</b>，
/// 因為 <c>PostDraw</c> 型的呼叫端在窗消失的那一幀根本不會被叫到）：
/// <list type="bullet">
/// <item><see cref="AddonEvent.PreFinalize"/>＝這一扇正在被銷毀 ⇒ 按過的那扇已經到終點。</item>
/// <item><see cref="AddonEvent.PostSetup"/>＝有新的一扇被建立起來 ⇒ 我們按過的那扇已經不是它了。</item>
/// </list>
/// ⚠️ 刻意<b>不</b>把 <c>PostRefresh</c> 也當解除點：它有可能在「關閉中」那幾幀觸發，
/// 那會把封鎖提早解除，正好把這道防線變成沒有。
/// </para>
/// <para>
/// 📌 <b>位址不同就放行</b>：同一個名字底下現在掛的是另一個實例時，我們按過的那扇
/// 已經不可能再被 <c>GetAddonByName</c> 取到，對新的那扇送 callback 與舊的無關。
/// （位址被新視窗重用的情形也不成問題：重用之前一定先經過
/// <see cref="AddonEvent.PreFinalize"/>／<see cref="AddonEvent.PostSetup"/>，封鎖已被清掉。）
/// </para>
/// <para>
/// 🔴 <b>逾時放行是刻意的</b>（<see cref="ReleaseTimeoutMs"/>）：萬一某扇窗既不 finalize
/// 也不重新 setup（例如上一次的 callback 根本沒生效、視窗就是還開著），
/// 沒有逾時的話呼叫端會<b>永遠</b>按不下去，等於把崩潰換成靜默失效。
/// 逾時值遠大於「關閉中」那幾幀（60fps 下數十毫秒、卡頓時也就數百毫秒），
/// 撐到這個長度還在的視窗依定義是「還開著」而不是「正在關閉」。
/// </para>
/// <para>⚠️ 只在主執行緒使用（與 <see cref="Throttle"/> 同一個前提）。</para>
/// </remarks>
internal static unsafe class AddonPressGuard
{
    /// <summary>按下之後最久封鎖多久（毫秒）。到期＝判定「上一次沒生效」而不是「正在關閉」。</summary>
    private const int ReleaseTimeoutMs = 2_000;

    private readonly record struct PressRecord(nint Address, DateTime At);

    private static readonly Dictionary<string, PressRecord> PressedByAddon = new(StringComparer.Ordinal);

    private static readonly Dictionary<string, IAddonLifecycle.AddonEventDelegate> Watchers =
        new(StringComparer.Ordinal);

    /// <summary>
    /// 登記「即將對這扇視窗送出 callback」。<b>回 <see langword="false"/> ＝這一幀絕對不能送。</b>
    /// </summary>
    /// <remarks>
    /// 呼叫點要放在<b>緊接著送出動作之前</b>——這支一回 <see langword="true"/> 就已經把
    /// 「按過了」記下去，登記完卻不按的話會白白封鎖到逾時為止。
    /// </remarks>
    public static bool TryBeginPress(string addonName, AtkUnitBase* addon)
    {
        if (addon == null || string.IsNullOrEmpty(addonName)) return false;

        EnsureWatching(addonName);

        var address = (nint)addon;
        if (PressedByAddon.TryGetValue(addonName, out var pressed) && pressed.Address == address)
        {
            var waitedMs = (DateTime.UtcNow - pressed.At).TotalMilliseconds;
            if (waitedMs < ReleaseTimeoutMs)
            {
                // 🔴 這就是崩潰的那一幀。診斷寫 Information（使用者跑 LogLevel 2），並節流免得洗版。
                if (Throttle.Pass($"AddonPressGuard-Hold-{addonName}", 1_000))
                    Svc.Log.Information(
                        $"[AddonPressGuard] 「{addonName}」（實例 0x{address:X}）按過之後還沒觀察到它收掉，" +
                        "這一幀不再送 callback——對關閉中的視窗送 callback 是攔不到的存取違規。");

                return false;
            }

            if (Throttle.Pass($"AddonPressGuard-Release-{addonName}", 10_000))
                Svc.Log.Information(
                    $"[AddonPressGuard] 「{addonName}」（實例 0x{address:X}）按下後 {waitedMs:F0} 毫秒" +
                    "既沒有被銷毀也沒有重新建立，判定為「上一次按下沒生效」而不是「正在關閉」，解除封鎖讓呼叫端重試。");
        }

        PressedByAddon[addonName] = new PressRecord(address, DateTime.UtcNow);
        return true;
    }

    /// <summary>外掛卸載時硬拆所有監聽器（不留指向本組件的委派）。</summary>
    public static void ForceTeardown()
    {
        foreach (var (addonName, handler) in Watchers)
        {
            Svc.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, addonName, handler);
            Svc.AddonLifecycle.UnregisterListener(AddonEvent.PreFinalize, addonName, handler);
        }

        Watchers.Clear();
        PressedByAddon.Clear();
    }

    /// <summary>
    /// 第一次守護某個 addon 名稱時掛上解除封鎖用的監聽器。
    /// </summary>
    /// <remarks>
    /// 掛上去之後就不再拆（只在 <see cref="ForceTeardown"/> 拆）：這兩條監聽器只做
    /// 一次字典移除，成本可忽略，而動態掛／拆比較容易留下懸空的監聽器。
    /// </remarks>
    private static void EnsureWatching(string addonName)
    {
        if (Watchers.ContainsKey(addonName)) return;

        IAddonLifecycle.AddonEventDelegate handler = (_, _) => PressedByAddon.Remove(addonName);

        Watchers[addonName] = handler;
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, addonName, handler);
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, addonName, handler);
    }
}

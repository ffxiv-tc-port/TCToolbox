using System;
using System.Collections.Generic;
using System.Reflection;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;

namespace TCToolbox.Core;

/// <summary>
/// AutoDuty 的 IPC 呼叫端包裝：問狀態、查路徑、暫時覆寫設定、跑一輪。
/// </summary>
/// <remarks>
/// 📌 端點與型別照提供端原樣宣告（AutoDuty 的 <c>IPC/IPCProvider.cs</c>，EzIPC 前綴＝內部名 <c>AutoDuty</c>；
/// 沒有回傳值的端點最後一個型別是 <c>object</c>）。
/// 🔴 只在框架執行緒上呼叫：AutoDuty 在框架執行緒上就地執行 <c>Run</c>，
/// 「回來時還停著＝沒有開始跑」只有在這條執行緒上判得準。
/// 🔴 刻意不包 <c>SetConfig</c>：那支會把值寫進使用者的 AutoDuty 存檔。
/// </remarks>
internal static class AutoDutyIpc
{
    /// <summary>暫時覆寫的結果。零值是「端點不在」：失敗方向是不跑。</summary>
    public enum OverrideResult
    {
        /// <summary>端點不在（沒裝、沒載入、版本太舊）或呼叫失敗。</summary>
        Unavailable = 0,

        /// <summary>端點在，但回 <see langword="false"/>：有一項驗不過，整批都沒套用。</summary>
        Rejected = 1,

        Accepted = 2,
    }

    private const int ErrorLogIntervalMs = 60_000;

    private static readonly Lazy<ICallGateSubscriber<bool>> IsStoppedGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<bool>("AutoDuty.IsStopped"));

    private static readonly Lazy<ICallGateSubscriber<uint, bool>> ContentHasPathGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<uint, bool>("AutoDuty.ContentHasPath"));

    private static readonly Lazy<ICallGateSubscriber<object, bool>> PushConfigOverridesGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<object, bool>("AutoDuty.PushConfigOverrides"));

    private static readonly Lazy<ICallGateSubscriber<bool>> PopConfigOverridesGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<bool>("AutoDuty.PopConfigOverrides"));

    private static readonly Lazy<ICallGateSubscriber<uint, int, bool, object>> RunGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<uint, int, bool, object>("AutoDuty.Run"));

    /// <summary>AutoDuty 是不是停著。回 <see langword="false"/>＝問不到（<paramref name="stopped"/> 沒有意義）。</summary>
    public static bool TryIsStopped(out bool stopped) =>
        TryCall("AutoDuty.IsStopped", static () => IsStoppedGate.Value.InvokeFunc(), out stopped);

    /// <summary>AutoDuty 有沒有這個區域（TerritoryType RowId）的路徑檔。回 <see langword="false"/>＝問不到。</summary>
    public static bool TryContentHasPath(uint territoryType, out bool hasPath) =>
        TryCall("AutoDuty.ContentHasPath", () => ContentHasPathGate.Value.InvokeFunc(territoryType), out hasPath);

    /// <summary>暫時覆寫一組設定：不寫進存檔，AutoDuty 停止時自己還原。</summary>
    /// <remarks>CallGate 會把字典 JSON 來回成 <c>JObject</c> 才交給對方，對方兩種都收。</remarks>
    public static OverrideResult PushOverrides(Dictionary<string, string> overrides)
    {
        if (!TryCall("AutoDuty.PushConfigOverrides", () => PushConfigOverridesGate.Value.InvokeFunc(overrides), out var accepted))
            return OverrideResult.Unavailable;

        return accepted ? OverrideResult.Accepted : OverrideResult.Rejected;
    }

    /// <summary>還原所有暫時覆寫。⚠️ 是全域的：別的外掛推的也會一起還原。</summary>
    public static void PopOverrides() =>
        TryCall("AutoDuty.PopConfigOverrides", static () => PopConfigOverridesGate.Value.InvokeFunc(), out _);

    /// <summary>請 AutoDuty 開始跑。回 <see langword="false"/> 時 <paramref name="error"/> 說明原因。</summary>
    public static bool TryRun(uint territoryType, int loops, bool bareMode, out string error)
    {
        try
        {
            RunGate.Value.InvokeAction(territoryType, loops, bareMode);
            error = string.Empty;
            return true;
        }
        catch (IpcError ex)
        {
            error = $"端點打不通（{ex.GetType().Name}）";
            return false;
        }
        catch (TargetInvocationException ex)
        {
            var inner = ex.InnerException ?? ex;
            error = $"AutoDuty 擲出 {inner.GetType().Name}：{inner.Message}";
            return false;
        }
        catch (Exception ex)
        {
            LogUnexpected("AutoDuty.Run", ex);
            error = ex.Message;
            return false;
        }
    }

    private static bool TryCall<T>(string endpoint, Func<T> call, out T value)
    {
        try
        {
            value = call();
            return true;
        }
        catch (IpcError)
        {
            // 沒裝／還沒載入／版本太舊都是正常狀態，不寫記錄。
            value = default!;
            return false;
        }
        catch (TargetInvocationException ex)
        {
            // 🔴 提供端實作裡擲的例外被 CallGate 的 DynamicInvoke 包成這一種，catch (IpcError) 攔不到。
            LogUnexpected(endpoint, ex.InnerException ?? ex);
            value = default!;
            return false;
        }
        catch (Exception ex)
        {
            LogUnexpected(endpoint, ex);
            value = default!;
            return false;
        }
    }

    private static void LogUnexpected(string endpoint, Exception ex)
    {
        if (!Throttle.Pass($"TCToolbox.AutoDutyIpc.Error.{endpoint}", ErrorLogIntervalMs)) return;

        // 🔴 Information 級：要使用者回報的診斷。
        Svc.Log.Information(
            $"[AutoDutyIpc] 呼叫 {endpoint} 時發生非預期例外（{ErrorLogIntervalMs / 1000} 秒內只報一次）：{ex}");
    }
}

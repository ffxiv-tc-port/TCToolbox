using System;
using System.Collections.Generic;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;

namespace TCToolbox.Core;

/// <summary>
/// DailyDuty 狀態 IPC 的呼叫端包裝：還沒做完的任務、名稱、類型，以及「帶我去」與取消。
/// </summary>
/// <remarks>
/// 🔴 端點名與型別逐字對應 DailyDuty 的 <c>Classes/StatusIpc.cs</c>（第 3 版）。
/// 對方所有端點都不阻塞；問不到一律回空清單／空字串／0。
/// 第 3 版才有的端點只在對方版本 <c>&gt;= UnknownListVersion</c> 時呼叫。
/// 🔴 本檔只准在遊戲主執行緒呼叫（Framework.Update、ImGui Draw、指令處理常式）：
/// 錯誤記錄的節流用的是主執行緒專用的 <see cref="Throttle"/>。
/// 🔴 <see cref="TryTravelToModule"/> 只准接在使用者按下的按鈕上：不輪詢、不重試、不自動接續。
/// </remarks>
internal static class DailyDutyIpc
{
    /// <summary>名稱、類型與「帶我去」從第 2 版才有；判準是 <c>對方版本 &gt;= RequiredVersion</c>。</summary>
    public const int RequiredVersion = 2;

    /// <summary>「DailyDuty 不知道狀態」的清單從第 3 版才有。</summary>
    public const int UnknownListVersion = 3;

    /// <summary>類型碼（0＝問不到）。</summary>
    public const int TypeDaily = 1;

    public const int TypeWeekly = 2;

    public const int TypeSpecial = 3;

    /// <summary><see cref="TryTravelToModule"/> 的回傳碼。0 是「不可用」，不是「已受理」。</summary>
    public const int TravelUnavailable = 0;

    public const int TravelRequested = 1;

    public const int TravelNoDestination = 2;

    public const int TravelBusy = 3;

    private const int ErrorLogIntervalMs = 60_000;

    private static readonly Lazy<ICallGateSubscriber<int>> ApiVersionGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<int>("DailyDuty.StatusApiVersion"));

    private static readonly Lazy<ICallGateSubscriber<List<string>>> IncompleteModulesGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<List<string>>("DailyDuty.GetIncompleteModules"));

    private static readonly Lazy<ICallGateSubscriber<List<string>>> UnknownModulesGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<List<string>>("DailyDuty.GetUnknownModules"));

    private static readonly Lazy<ICallGateSubscriber<string, string>> DisplayNameGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<string, string>("DailyDuty.GetModuleDisplayName"));

    private static readonly Lazy<ICallGateSubscriber<string, int>> ModuleTypeGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<string, int>("DailyDuty.GetModuleType"));

    private static readonly Lazy<ICallGateSubscriber<List<string>>> TravelModulesGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<List<string>>("DailyDuty.GetTravelModules"));

    private static readonly Lazy<ICallGateSubscriber<string, int>> TravelToModuleGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<string, int>("DailyDuty.TravelToModule"));

    private static readonly Lazy<ICallGateSubscriber<bool>> IsTravelRunningGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<bool>("DailyDuty.IsTravelRunning"));

    private static readonly Lazy<ICallGateSubscriber<bool>> CancelTravelGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<bool>("DailyDuty.CancelTravel"));

    /// <summary>DailyDuty 在不在（順便拿版本）。<see langword="false"/>＝沒裝、沒載入或還沒註冊。</summary>
    public static bool TryGetVersion(out int version)
    {
        version = 0;

        try
        {
            version = ApiVersionGate.Value.InvokeFunc();
            return true;
        }
        catch (IpcError)
        {
            // 沒裝／還沒載入是正常狀態，不寫記錄。
            return false;
        }
        catch (Exception ex)
        {
            LogUnexpected("DailyDuty.StatusApiVersion", ex);
            return false;
        }
    }

    /// <summary>DailyDuty 裡有啟用、狀態是「未完成」的任務內部名稱。<see langword="false"/>＝問不到。</summary>
    public static bool TryGetIncompleteModules(out List<string> names) =>
        TryGetNames(IncompleteModulesGate, "DailyDuty.GetIncompleteModules", out names);

    /// <summary>
    /// DailyDuty 裡有啟用、但它不知道狀態的任務內部名稱（例：通緝令一張都沒勾），暫時關閉的不算。
    /// 只在對方版本 <c>&gt;= <see cref="UnknownListVersion"/></c> 時呼叫；<see langword="false"/>＝問不到。
    /// </summary>
    public static bool TryGetUnknownModules(out List<string> names) =>
        TryGetNames(UnknownModulesGate, "DailyDuty.GetUnknownModules", out names);

    /// <summary>此刻畫得出「帶我去」的任務內部名稱。<see langword="false"/>＝問不到。</summary>
    public static bool TryGetTravelModules(out List<string> names) =>
        TryGetNames(TravelModulesGate, "DailyDuty.GetTravelModules", out names);

    /// <summary>任務在 DailyDuty 介面語言下的名稱；問不到回空字串。</summary>
    public static string GetModuleDisplayName(string moduleName)
    {
        try
        {
            return DisplayNameGate.Value.InvokeFunc(moduleName) ?? string.Empty;
        }
        catch (IpcError)
        {
            return string.Empty;
        }
        catch (Exception ex)
        {
            LogUnexpected("DailyDuty.GetModuleDisplayName", ex);
            return string.Empty;
        }
    }

    /// <summary>任務類型碼（1 每日／2 每週／3 特殊）；問不到回 0。</summary>
    public static int GetModuleType(string moduleName)
    {
        try
        {
            return ModuleTypeGate.Value.InvokeFunc(moduleName);
        }
        catch (IpcError)
        {
            return 0;
        }
        catch (Exception ex)
        {
            LogUnexpected("DailyDuty.GetModuleType", ex);
            return 0;
        }
    }

    /// <summary>DailyDuty 此刻有沒有行程在跑（不論誰發起）；<see langword="null"/>＝問不到。</summary>
    public static bool? IsTravelRunning()
    {
        try
        {
            return IsTravelRunningGate.Value.InvokeFunc();
        }
        catch (IpcError)
        {
            return null;
        }
        catch (Exception ex)
        {
            LogUnexpected("DailyDuty.IsTravelRunning", ex);
            return null;
        }
    }

    /// <summary>
    /// 替使用者按下某個任務的「帶我去」。<see langword="false"/>＝呼叫本身失敗，<paramref name="code"/> 沒有意義。
    /// </summary>
    public static bool TryTravelToModule(string moduleName, out int code)
    {
        code = TravelUnavailable;

        try
        {
            code = TravelToModuleGate.Value.InvokeFunc(moduleName);
            return true;
        }
        catch (IpcError)
        {
            return false;
        }
        catch (Exception ex)
        {
            LogUnexpected("DailyDuty.TravelToModule", ex);
            return false;
        }
    }

    /// <summary>
    /// 取消 DailyDuty 目前的行程與還沒執行的出發請求。<see langword="false"/>＝呼叫本身失敗。
    /// </summary>
    /// <param name="hadSomething">呼叫當下有沒有東西可以取消。</param>
    /// <param name="error">呼叫出事時的說明；端點不存在（沒載入、版本不足）時是空字串。</param>
    /// <remarks>
    /// 只有 <see cref="IpcNotReadyError"/> 算「端點不存在」；其他例外（含型別不合）都是出事，
    /// 與全艦隊急停對其他對象的三態分法一致。
    /// </remarks>
    public static bool TryCancelTravel(out bool hadSomething, out string error)
    {
        hadSomething = false;
        error = string.Empty;

        try
        {
            hadSomething = CancelTravelGate.Value.InvokeFunc();
            return true;
        }
        catch (IpcNotReadyError)
        {
            return false;
        }
        catch (Exception ex)
        {
            LogUnexpected("DailyDuty.CancelTravel", ex);
            error = $"{ex.GetType().Name}：{ex.Message}";
            return false;
        }
    }

    /// <summary><see cref="TryTravelToModule"/> 回傳碼的中文說明（記錄與畫面共用）。</summary>
    public static string DescribeTravelCode(int code) => code switch
    {
        TravelUnavailable => "不可用：未登入、DailyDuty 還沒整理好第一份狀態，或正在卸載",
        TravelRequested => "已受理，下一幀出發",
        TravelNoDestination => "此刻沒有目的地",
        TravelBusy => "忙碌中：已經有行程，或上一個請求還沒執行",
        _ => $"不認得的回傳值 {code}",
    };

    private static bool TryGetNames(Lazy<ICallGateSubscriber<List<string>>> gate, string endpoint, out List<string> names)
    {
        names = [];

        try
        {
            if (gate.Value.InvokeFunc() is not { } result) return false;

            names = result;
            return true;
        }
        catch (IpcError)
        {
            return false;
        }
        catch (Exception ex)
        {
            LogUnexpected(endpoint, ex);
            return false;
        }
    }

    private static void LogUnexpected(string endpoint, Exception ex)
    {
        if (!Throttle.Pass($"TCToolbox.DailyDutyIpc.Error.{endpoint}", ErrorLogIntervalMs)) return;

        Svc.Log.Information(
            $"[DailyDutyIpc] 呼叫 {endpoint} 時發生非預期例外（{ErrorLogIntervalMs / 1000} 秒內只報一次）：{ex}");
    }
}

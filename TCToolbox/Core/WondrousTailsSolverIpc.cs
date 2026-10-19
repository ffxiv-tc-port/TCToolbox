using System;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;

namespace TCToolbox.Core;

/// <summary>
/// WondrousTailsSolver 的<b>唯讀</b> IPC 呼叫端包裝：查任一天書盤面的連線機率。
/// </summary>
/// <remarks>
/// 🔴 <b>回傳的是 0~1 的比例，不是百分比</b>——顯示成 % 要自己乘 100。
/// 提供端回傳前做 <c>Math.Round(..., 4)</c>，所以最小刻度是 0.01%。
/// 📌 端點是純函式（只讀對方建構時凍結的表），跑在<b>呼叫端的執行緒</b>上：
/// 不需要節流，也不需要框架閘門。
/// </remarks>
internal static class WondrousTailsSolverIpc
{
    /// <summary>本外掛寫的時候對照的版本。判準是<c>對方版本 &gt;= SupportedVersion</c>。</summary>
    public const int SupportedVersion = 1;

    /// <summary>機率陣列的長度：1／2／3 條線各一格。</summary>
    public const int ChanceCount = 3;

    private const int ErrorLogIntervalMs = 60_000;

    private static readonly Lazy<ICallGateSubscriber<int>> ApiVersionGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<int>("WondrousTailsSolver.ApiVersion"));

    private static readonly Lazy<ICallGateSubscriber<int, double[]>> LineChancesGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<int, double[]>("WondrousTailsSolver.GetLineChances"));

    /// <summary>WondrousTailsSolver 在不在（順便拿版本）。回 <see langword="false"/>＝沒裝或還沒註冊 IPC。</summary>
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
            // 沒裝／還沒載入完成都是正常狀態，不寫記錄。
            return false;
        }
        catch (Exception ex)
        {
            LogUnexpected("WondrousTailsSolver.ApiVersion", ex);
            return false;
        }
    }

    /// <summary>
    /// 查一個 16 bit 盤面（bit i＝第 i 格已貼印花）的 1／2／3 條線機率（<b>0~1 的比例</b>）。
    /// </summary>
    /// <remarks>
    /// 🔴 對方對「不可能存在的盤面」（超過 9 個印花等）回整組 <c>-1</c>＝<b>不知道</b>，
    /// 不是「機率 0」。呼叫端要把它畫成「？」而不是 0%。
    /// </remarks>
    public static bool TryGetLineChances(int mask, out double[] chances)
    {
        chances = [];

        try
        {
            // 對方契約上一律 new 一個長度 3 的陣列，但呼叫端不該賭別人的實作。
            if (LineChancesGate.Value.InvokeFunc(mask) is not { Length: ChanceCount } result) return false;

            chances = result;
            return true;
        }
        catch (IpcError)
        {
            return false;
        }
        catch (Exception ex)
        {
            LogUnexpected("WondrousTailsSolver.GetLineChances", ex);
            return false;
        }
    }

    private static void LogUnexpected(string endpoint, Exception ex)
    {
        if (!Throttle.Pass($"TCToolbox.WondrousTailsSolverIpc.Error.{endpoint}", ErrorLogIntervalMs)) return;

        // 🔴 Information 級：要使用者回報的診斷。
        Svc.Log.Information(
            $"[WondrousTailsSolverIpc] 呼叫 {endpoint} 時發生非預期例外（{ErrorLogIntervalMs / 1000} 秒內只報一次）：{ex}");
    }
}

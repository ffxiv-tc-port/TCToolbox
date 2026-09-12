using System;
using System.Numerics;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;

namespace TCToolbox.Core;

/// <summary>
/// Mappy 通用標記 IPC 的呼叫端包裝（只放／清標記，不讀也不改 Mappy 的任何設定）。
/// </summary>
/// <remarks>
/// 🔴 <c>mapCoords</c> 是<b>地圖座標</b>——遊戲介面上顯示的那個 X/Y（例如 12.3, 34.5），
/// 不是世界座標也不是貼圖座標。傳錯的失敗形式是<b>標記靜靜地落在地圖上不相干的位置</b>，
/// 不會有任何錯誤訊息。
/// </remarks>
internal static class MappyMarkerIpc
{
    /// <summary>本外掛寫的時候對照的 Mappy 標記 IPC 版本。</summary>
    /// <remarks>
    /// 📌 判準是 <c>對方版本 &gt;= SupportedVersion</c>：Mappy 端的註解把這四個端點列為對外契約
    /// （改名＝破壞相容性），所以後續版本視為只增不減。對方版本<b>比較小</b>才是真的不能用。
    /// </remarks>
    public const int SupportedVersion = 1;

    /// <summary>非預期例外的記錄節流間隔（毫秒）。</summary>
    private const int ErrorLogIntervalMs = 60_000;

    // 建立 subscriber 只是本地物件，零成本；真正的探測發生在 InvokeFunc()。
    private static readonly Lazy<ICallGateSubscriber<int>> GetVersionGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<int>("Mappy.GetVersion"));

    private static readonly Lazy<ICallGateSubscriber<string, uint, Vector2, uint, string, uint>> AddMarkerGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<string, uint, Vector2, uint, string, uint>("Mappy.AddMarker"));

    private static readonly Lazy<ICallGateSubscriber<string, uint, bool>> RemoveMarkerGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<string, uint, bool>("Mappy.RemoveMarker"));

    private static readonly Lazy<ICallGateSubscriber<string, bool>> ClearSourceGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<string, bool>("Mappy.ClearSource"));

    /// <summary>Mappy 在不在（順便拿版本）。回 <see langword="false"/>＝沒裝或還沒註冊 IPC。</summary>
    public static bool TryGetVersion(out int version)
    {
        version = 0;

        try
        {
            version = GetVersionGate.Value.InvokeFunc();
            return true;
        }
        catch (IpcError)
        {
            // 沒裝／還沒載入完成，這是正常狀態，不寫記錄。
            return false;
        }
        catch (Exception ex)
        {
            LogUnexpected("Mappy.GetVersion", ex);
            return false;
        }
    }

    /// <summary>放一個標記。回 0＝被 Mappy 拒絕或 IPC 打不通（兩者都不是例外狀況）。</summary>
    public static uint AddMarker(string source, uint mapId, Vector2 mapCoordinates, uint iconId, string tooltip)
    {
        try
        {
            return AddMarkerGate.Value.InvokeFunc(source, mapId, mapCoordinates, iconId, tooltip);
        }
        catch (IpcError)
        {
            return 0;
        }
        catch (Exception ex)
        {
            LogUnexpected("Mappy.AddMarker", ex);
            return 0;
        }
    }

    /// <summary>
    /// 移除單一標記。找不到（含 Mappy 中途重載過）時回 <see langword="false"/>，那也是正常的。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>週期性重推一定要走這支，不要走 <see cref="ClearSource"/>。</b>
    /// </remarks>
    public static bool RemoveMarker(string source, uint handle)
    {
        try
        {
            return RemoveMarkerGate.Value.InvokeFunc(source, handle);
        }
        catch (IpcError)
        {
            return false;
        }
        catch (Exception ex)
        {
            LogUnexpected("Mappy.RemoveMarker", ex);
            return false;
        }
    }

    /// <summary>清掉某個來源的全部標記。來源不存在時 Mappy 回 <see langword="false"/>，那也是正常的。</summary>
    /// <remarks>⚠️ 只在模組停用／卸載時用，理由見 <see cref="RemoveMarker"/>。</remarks>
    public static bool ClearSource(string source)
    {
        try
        {
            return ClearSourceGate.Value.InvokeFunc(source);
        }
        catch (IpcError)
        {
            return false;
        }
        catch (Exception ex)
        {
            LogUnexpected("Mappy.ClearSource", ex);
            return false;
        }
    }

    private static void LogUnexpected(string endpoint, Exception ex)
    {
        if (!Throttle.Pass($"TCToolbox.MappyMarkerIpc.Error.{endpoint}", ErrorLogIntervalMs)) return;

        // 🔴 Information 級：使用者跑 LogLevel 1，盲區只有 Verbose,Debug 收得到但單檔數十萬行會淹沒。
        Svc.Log.Information($"[MappyMarkerIpc] 呼叫 {endpoint} 時發生非預期例外（{ErrorLogIntervalMs / 1000} 秒內只報一次）：{ex}");
    }
}

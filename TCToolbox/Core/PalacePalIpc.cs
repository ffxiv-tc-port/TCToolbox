using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;

namespace TCToolbox.Core;

/// <summary>
/// Palace Pal 的<b>唯讀</b> IPC 呼叫端包裝（讀它已經標示出來的陷阱／埋藏寶藏座標）。
/// </summary>
/// <remarks>
/// 📌 <b>座標是<see langword="世界"/>座標</b>（<c>Vector3</c>），不是地圖座標——
/// 要畫到 Mappy 上必須先過 <see cref="MapCoords.TryWorldToMap"/>。
/// 傳錯的失敗形式是標記靜靜地落在地圖上不相干的位置，沒有任何錯誤訊息。
/// 🔴 <b>型別刻意用 <c>List&lt;Vector3&gt;</c> 逐字照抄。</b>對方的註解寫著「跨
/// AssemblyLoadContext 只能傳共用執行期型別……絕對不要改成自訂 class/record/tuple」。
/// </remarks>
internal static class PalacePalIpc
{
    /// <summary>本外掛寫的時候對照的 Palace Pal IPC 版本。</summary>
    /// <remarks>判準是<c>對方版本 &gt;= SupportedVersion</c>：對方比較舊才是真的不能用。</remarks>
    public const int SupportedVersion = 1;

    /// <summary>非預期例外的記錄節流間隔（毫秒）。</summary>
    private const int ErrorLogIntervalMs = 60_000;

    private static readonly Lazy<ICallGateSubscriber<int>> ApiVersionGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<int>("PalacePal.ApiVersion"));

    private static readonly Lazy<ICallGateSubscriber<ushort, List<Vector3>>> TrapGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<ushort, List<Vector3>>("PalacePal.GetTrapLocations"));

    private static readonly Lazy<ICallGateSubscriber<ushort, List<Vector3>>> HoardGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<ushort, List<Vector3>>("PalacePal.GetHoardLocations"));

    /// <summary>Palace Pal 在不在（順便拿版本）。回 <see langword="false"/>＝沒裝或還沒註冊 IPC。</summary>
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
            // 沒裝／還沒載入完成，這是正常狀態，不寫記錄。
            return false;
        }
        catch (Exception ex)
        {
            LogUnexpected("PalacePal.ApiVersion", ex);
            return false;
        }
    }

    /// <summary>取這個區域的陷阱座標（<b>世界座標</b>）。</summary>
    public static bool TryGetTraps(ushort territoryType, out List<Vector3> locations) =>
        Query(TrapGate, "PalacePal.GetTrapLocations", territoryType, out locations);

    /// <summary>取這個區域的埋藏寶藏座標（<b>世界座標</b>）。</summary>
    public static bool TryGetHoards(ushort territoryType, out List<Vector3> locations) =>
        Query(HoardGate, "PalacePal.GetHoardLocations", territoryType, out locations);

    private static bool Query(
        Lazy<ICallGateSubscriber<ushort, List<Vector3>>> gate,
        string endpoint,
        ushort territoryType,
        out List<Vector3> locations)
    {
        try
        {
            // 🔴 對方回 null 在契約上不會發生（它一律 new 一個清單），但呼叫端不該賭別人的實作。
            locations = gate.Value.InvokeFunc(territoryType) ?? [];
            return true;
        }
        catch (IpcError)
        {
            locations = [];
            return false;
        }
        catch (Exception ex)
        {
            LogUnexpected(endpoint, ex);
            locations = [];
            return false;
        }
    }

    private static void LogUnexpected(string endpoint, Exception ex)
    {
        if (!Throttle.Pass($"TCToolbox.PalacePalIpc.Error.{endpoint}", ErrorLogIntervalMs)) return;

        // 🔴 Information 級：使用者跑 LogLevel 1，盲區只有 Verbose；Debug 收得到但單檔數十萬行會淹沒。
        Svc.Log.Information(
            $"[PalacePalIpc] 呼叫 {endpoint} 時發生非預期例外（{ErrorLogIntervalMs / 1000} 秒內只報一次）：{ex}");
    }
}

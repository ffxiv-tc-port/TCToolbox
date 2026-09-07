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
/// <para>
/// 對方的契約（2026-09-08 逐字對照 <c>PalacePal/Pal.Client/DependencyInjection/IpcProvider.cs</c>）：
/// <list type="bullet">
/// <item><c>PalacePal.ApiVersion() -&gt; int</c>（目前 1）</item>
/// <item><c>PalacePal.GetTrapLocations(ushort territoryType) -&gt; List&lt;Vector3&gt;</c></item>
/// <item><c>PalacePal.GetHoardLocations(ushort territoryType) -&gt; List&lt;Vector3&gt;</c></item>
/// </list>
/// 三支都是純讀取它記憶體裡既有的清單：沒有查資料庫、沒有連線、沒有寫入。
/// </para>
/// <para>
/// 📌 <b>座標是<see langword="世界"/>座標</b>（<c>Vector3</c>），不是地圖座標——
/// 要畫到 Mappy 上必須先過 <see cref="MapCoords.TryWorldToMap"/>。
/// 傳錯的失敗形式是標記靜靜地落在地圖上不相干的位置，沒有任何錯誤訊息。
/// </para>
/// <para>
/// 📌 <b>回空清單是正常狀態不是錯誤</b>：對方的 <c>GetTerritoryIfReady</c> 對「不是深層迷宮的
/// territory」與「還沒載入完」都回 null ⇒ 空清單。呼叫端不該把它當失敗，也不該因此寫記錄。
/// </para>
/// <para>
/// 🔴 <b>型別刻意用 <c>List&lt;Vector3&gt;</c> 逐字照抄。</b>對方的註解寫著「跨
/// AssemblyLoadContext 只能傳共用執行期型別……絕對不要改成自訂 class/record/tuple」。
/// </para>
/// <para>
/// ⚠️ <b><c>territoryType</c> 是 <c>ushort</c> 不是 <c>uint</c>。</b>宣告成 <c>uint</c> 會走進
/// <c>CallGateChannel</c> 的型別轉換路徑；那條路在數值型別上多半會成功，
/// 但沒有理由去賭它——照抄對方的簽章。
/// </para>
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

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

    /// <summary>「已確認／未確認」兩態端點那條版本線。</summary>
    /// <remarks>
    /// 🔴 與 <see cref="SupportedVersion"/><b>各自獨立遞增</b>：對方是分開宣告的三條線，
    /// 拿其中一條的版本去推斷另一條有沒有，會在對方只實作了一半時靜默判錯。
    /// </remarks>
    public const int LocationStateSupportedVersion = 1;

    /// <summary>「現在真的看得到什麼」端點那條版本線（同樣獨立）。</summary>
    public const int VisibleLocationSupportedVersion = 1;

    /// <summary>非預期例外的記錄節流間隔（毫秒）。</summary>
    private const int ErrorLogIntervalMs = 60_000;

    private static readonly Lazy<ICallGateSubscriber<int>> ApiVersionGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<int>("PalacePal.ApiVersion"));

    private static readonly Lazy<ICallGateSubscriber<ushort, List<Vector3>>> TrapGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<ushort, List<Vector3>>("PalacePal.GetTrapLocations"));

    private static readonly Lazy<ICallGateSubscriber<ushort, List<Vector3>>> HoardGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<ushort, List<Vector3>>("PalacePal.GetHoardLocations"));

    private static readonly Lazy<ICallGateSubscriber<int>> LocationStateVersionGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<int>("PalacePal.LocationStateApiVersion"));

    private static readonly Lazy<ICallGateSubscriber<ushort, List<Vector3>>> ConfirmedTrapGate =
        new(() => Svc.PluginInterface
                     .GetIpcSubscriber<ushort, List<Vector3>>("PalacePal.GetConfirmedTrapLocations"));

    private static readonly Lazy<ICallGateSubscriber<ushort, List<Vector3>>> UnconfirmedTrapGate =
        new(() => Svc.PluginInterface
                     .GetIpcSubscriber<ushort, List<Vector3>>("PalacePal.GetUnconfirmedTrapLocations"));

    private static readonly Lazy<ICallGateSubscriber<ushort, List<Vector3>>> ConfirmedHoardGate =
        new(() => Svc.PluginInterface
                     .GetIpcSubscriber<ushort, List<Vector3>>("PalacePal.GetConfirmedHoardLocations"));

    private static readonly Lazy<ICallGateSubscriber<ushort, List<Vector3>>> UnconfirmedHoardGate =
        new(() => Svc.PluginInterface
                     .GetIpcSubscriber<ushort, List<Vector3>>("PalacePal.GetUnconfirmedHoardLocations"));

    private static readonly Lazy<ICallGateSubscriber<int>> VisibleLocationVersionGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<int>("PalacePal.VisibleLocationApiVersion"));

    private static readonly Lazy<ICallGateSubscriber<ushort, List<Vector3>>> VisibleTrapGate =
        new(() => Svc.PluginInterface
                     .GetIpcSubscriber<ushort, List<Vector3>>("PalacePal.GetVisibleTrapLocations"));

    private static readonly Lazy<ICallGateSubscriber<ushort, List<Vector3>>> VisibleHoardGate =
        new(() => Svc.PluginInterface
                     .GetIpcSubscriber<ushort, List<Vector3>>("PalacePal.GetVisibleHoardLocations"));

    private static readonly Lazy<ICallGateSubscriber<ushort, List<Vector3>>> VisibleSilverCofferGate =
        new(() => Svc.PluginInterface
                     .GetIpcSubscriber<ushort, List<Vector3>>("PalacePal.GetVisibleSilverCofferLocations"));

    private static readonly Lazy<ICallGateSubscriber<ushort, List<Vector3>>> VisibleGoldCofferGate =
        new(() => Svc.PluginInterface
                     .GetIpcSubscriber<ushort, List<Vector3>>("PalacePal.GetVisibleGoldCofferLocations"));

    /// <summary>可見快照的年齡／樓層。</summary>
    /// <remarks>
    /// 🔴 提供端回的是<b>非可空 <c>int</c></b>，這裡也必須是 <c>int</c>。宣告成 <c>int?</c> 時，
    /// CallGate 只在型別不同才轉換，回 null 的那一次擲的是看起來與 IPC 無關的 NRE。
    /// </remarks>
    private static readonly Lazy<ICallGateSubscriber<ushort, int>> VisibleAgeGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<ushort, int>("PalacePal.GetVisibleLocationsAgeMillis"));

    private static readonly Lazy<ICallGateSubscriber<ushort, int>> VisibleFloorGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<ushort, int>("PalacePal.GetVisibleLocationsFloor"));

    /// <summary>Palace Pal 在不在（順便拿版本）。回 <see langword="false"/>＝沒裝或還沒註冊 IPC。</summary>
    public static bool TryGetVersion(out int version) =>
        QueryVersion(ApiVersionGate, "PalacePal.ApiVersion", out version);

    /// <summary>對方有沒有兩態端點（順便拿版本）。</summary>
    public static bool TryGetLocationStateVersion(out int version) =>
        QueryVersion(LocationStateVersionGate, "PalacePal.LocationStateApiVersion", out version);

    /// <summary>對方有沒有「現在看得到什麼」端點（順便拿版本）。</summary>
    public static bool TryGetVisibleLocationVersion(out int version) =>
        QueryVersion(VisibleLocationVersionGate, "PalacePal.VisibleLocationApiVersion", out version);

    /// <summary>取這個區域的陷阱座標（<b>世界座標</b>）。</summary>
    public static bool TryGetTraps(ushort territoryType, out List<Vector3> locations) =>
        Query(TrapGate, "PalacePal.GetTrapLocations", territoryType, out locations);

    /// <summary>取這個區域的埋藏寶藏座標（<b>世界座標</b>）。</summary>
    public static bool TryGetHoards(ushort territoryType, out List<Vector3> locations) =>
        Query(HoardGate, "PalacePal.GetHoardLocations", territoryType, out locations);

    /// <summary>本機親眼確認過的陷阱座標。</summary>
    /// <remarks>
    /// ⚠️ 「已確認」講的是<b>資料可信度</b>，不是「這一層現在有」——一個區域涵蓋十層，
    /// 這份清單是十層累積下來的候選生成點。給使用者看的文字要照這個意思寫。
    /// </remarks>
    public static bool TryGetConfirmedTraps(ushort territoryType, out List<Vector3> locations) =>
        Query(ConfirmedTrapGate, "PalacePal.GetConfirmedTrapLocations", territoryType, out locations);

    /// <summary>只來自共享資料、本機還沒親眼確認過的陷阱座標。</summary>
    public static bool TryGetUnconfirmedTraps(ushort territoryType, out List<Vector3> locations) =>
        Query(UnconfirmedTrapGate, "PalacePal.GetUnconfirmedTrapLocations", territoryType, out locations);

    /// <summary>本機親眼確認過的埋藏寶藏座標。</summary>
    public static bool TryGetConfirmedHoards(ushort territoryType, out List<Vector3> locations) =>
        Query(ConfirmedHoardGate, "PalacePal.GetConfirmedHoardLocations", territoryType, out locations);

    /// <summary>只來自共享資料、本機還沒親眼確認過的埋藏寶藏座標。</summary>
    public static bool TryGetUnconfirmedHoards(ushort territoryType, out List<Vector3> locations) =>
        Query(UnconfirmedHoardGate, "PalacePal.GetUnconfirmedHoardLocations", territoryType, out locations);

    /// <summary>這一趟這一層<b>現在真的擺在那裡</b>的陷阱實體（要已現形才有實體）。</summary>
    /// <remarks>
    /// 🔴 這是上一幀的觀測快照不是資料庫：範圍由<b>遊戲自己的物件串流距離</b>決定，走遠到遊戲
    /// 把物件收掉就從清單消失。用它之前先問 <see cref="TryGetVisibleAgeMillis"/>，太舊當不知道。
    /// </remarks>
    public static bool TryGetVisibleTraps(ushort territoryType, out List<Vector3> locations) =>
        Query(VisibleTrapGate, "PalacePal.GetVisibleTrapLocations", territoryType, out locations);

    /// <summary>現在真的看得到的埋藏寶藏實體（要用過感知寶藏，或已被挖出）。</summary>
    public static bool TryGetVisibleHoards(ushort territoryType, out List<Vector3> locations) =>
        Query(VisibleHoardGate, "PalacePal.GetVisibleHoardLocations", territoryType, out locations);

    /// <summary>現在真的看得到的銀寶箱座標。</summary>
    public static bool TryGetVisibleSilverCoffers(ushort territoryType, out List<Vector3> locations) =>
        Query(VisibleSilverCofferGate, "PalacePal.GetVisibleSilverCofferLocations", territoryType, out locations);

    /// <summary>現在真的看得到的金寶箱座標。</summary>
    public static bool TryGetVisibleGoldCoffers(ushort territoryType, out List<Vector3> locations) =>
        Query(VisibleGoldCofferGate, "PalacePal.GetVisibleGoldCofferLocations", territoryType, out locations);

    /// <summary>可見快照拍下來多久了（毫秒）。</summary>
    /// <remarks>
    /// 🔴 <b>-1＝不知道</b>（沒快照／不是這個區域／IPC 打不通），<b>不是「0 毫秒」</b>。
    /// 介面上要畫成「？」；畫成 0 會讓人以為這一層是乾淨的。
    /// </remarks>
    public static bool TryGetVisibleAgeMillis(ushort territoryType, out int ageMillis) =>
        QueryInt(VisibleAgeGate, "PalacePal.GetVisibleLocationsAgeMillis", territoryType, out ageMillis);

    /// <summary>可見快照是哪一層拍的。</summary>
    /// <remarks>🔴 <c>-1</c>＝不知道；<c>0</c>＝有快照但樓層還沒讀到，<b>不是「第 0 層」</b>。</remarks>
    public static bool TryGetVisibleFloor(ushort territoryType, out int floor) =>
        QueryInt(VisibleFloorGate, "PalacePal.GetVisibleLocationsFloor", territoryType, out floor);

    private static bool QueryVersion(Lazy<ICallGateSubscriber<int>> gate, string endpoint, out int version)
    {
        version = 0;

        try
        {
            version = gate.Value.InvokeFunc();
            return true;
        }
        catch (IpcError)
        {
            // 沒裝／還沒載入完成／對方根本沒開這條版本線，都是正常狀態，不寫記錄。
            return false;
        }
        catch (Exception ex)
        {
            LogUnexpected(endpoint, ex);
            return false;
        }
    }

    private static bool QueryInt(
        Lazy<ICallGateSubscriber<ushort, int>> gate,
        string endpoint,
        ushort territoryType,
        out int value)
    {
        // 🔴 打不通時給 -1（＝不知道），不要給 0：0 在對方的契約裡是有意義的值。
        value = -1;

        try
        {
            value = gate.Value.InvokeFunc(territoryType);
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

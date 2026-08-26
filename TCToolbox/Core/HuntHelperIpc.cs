using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;

namespace TCToolbox.Core;

/// <summary>
/// Hunt Helper 狩獵列車 IPC 的<b>唯讀</b>呼叫端包裝。
/// </summary>
/// <remarks>
/// <para>
/// 對方的契約（2026-08-26 逐字對照 <c>HuntHelper/Managers/IpcSystem.cs</c>）：
/// <list type="bullet">
/// <item><c>HH.GetVersion() -&gt; uint</c>（目前 1）</item>
/// <item><c>HH.GetTrainList() -&gt; List&lt;MobRecord&gt;</c></item>
/// <item><c>HH.ImportTrainList(List&lt;MobRecord&gt;)</c>——<b>會改寫使用者的列車清單，本外掛永遠不呼叫</b></item>
/// <item><c>HH.channel.MarkSeen</c>——標記某隻怪已看過時廣播一筆 <c>MobRecord</c></item>
/// </list>
/// </para>
///
/// <para>
/// 🔴🔴 <b>為什麼要自己做一個鏡像型別</b>：Hunt Helper 的 <c>MobRecord</c> 是
/// <c>IpcSystem</c> 裡的 <b><c>private record struct</c></b>——外部組件<b>無法具名</b>，
/// 所以「照抄對方的簽章」這條路在編譯期就走不通（這大概也是艦隊裡這組 IPC 至今零消費者的原因）。
/// </para>
/// <para>
/// 走得通的理由在 Dalamud 本體：<c>CallGateChannel.InvokeFunc&lt;TRet&gt;</c> 在
/// <c>typeof(TRet) != methodInfo.ReturnType</c> 時<b>會用 Newtonsoft 把結果 JSON 來回轉一次</b>
/// （<c>Dalamud/Plugin/Ipc/Internal/CallGateChannel.cs</c> 的 <c>ConvertObject</c>）。
/// 也就是說只要<b>成員名字對得上</b>，用自己宣告的型別接就成立，不必碰對方的型別。
/// </para>
/// <para>
/// 🔴 <b>對不上的失敗形式是靜默的</b>：JSON 裡沒有對應鍵的成員會留在預設值
/// （名字變成空字串、<c>MapID</c> 變成 0、座標變成 0,0），<b>不會擲例外也不會有記錄</b>。
/// 改這個型別的成員名等於改 IPC 契約——<see cref="TrainMob"/> 的每個名字都必須逐字等於
/// 對方 <c>MobRecord</c> 的成員名。
/// </para>
///
/// <para>
/// 🔴🔴 <b><see cref="TryGetTrainList"/> 只能在框架執行緒上呼叫。</b>
/// 對方的實作是 <c>_framework.RunOnFrameworkThread(…).Result</c>——
/// 在框架執行緒上時 Dalamud 走的是 <c>Task.FromResult(func())</c>（<b>原地執行</b>，安全）；
/// 不在框架執行緒上時會排進下一個 tick 再 <c>.Result</c> 等它。
/// 所以從<b>繪製路徑</b>或任何會擋住主執行緒的地方呼叫它，就是在等一個永遠不會到來的 tick。
/// ⇒ 本外掛只從 <c>Framework.Update</c> 呼叫這一支；設定畫面上只呼叫沒有這個問題的
/// <see cref="TryGetVersion"/>。
/// </para>
/// </remarks>
internal static class HuntHelperIpc
{
    /// <summary>本外掛寫的時候對照的 Hunt Helper IPC 版本。</summary>
    public const uint SupportedVersion = 1;

    /// <summary>非預期例外的記錄節流間隔（毫秒）。</summary>
    private const int ErrorLogIntervalMs = 60_000;

    /// <summary>
    /// Hunt Helper <c>MobRecord</c> 的鏡像型別，<b>只靠 JSON 成員名對應</b>（見類別註解）。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>這些名字不是命名風格問題，是對外契約。</b><c>MobID</c>／<c>TerritoryID</c>／
    /// <c>MapID</c>／<c>LastSeenUTC</c> 的大小寫全部照抄對方，不要「順手」改成 <c>MobId</c> 之類——
    /// 改了不會編譯失敗，只會讓那個欄位永遠是 0。
    /// <para>
    /// 📌 刻意用<b>可變的 class</b> 而不是 <c>record struct</c>：Newtonsoft 對「無參數建構子＋可寫屬性」
    /// 的反序列化路徑最單純，不必去賭它認不認得位置參數建構子的參數名。
    /// </para>
    /// <para>
    /// 📌 <c>Position</c> 是 <b>地圖座標</b>（介面上顯示的 X/Y），不是世界座標——
    /// Hunt Helper 在 <c>HuntTrainMobExtensions.ToHuntTrainMob</c> 就已經用
    /// <c>MapHelpers.ConvertToMapCoordinate</c> 換算過了，而且同一個值被拿去餵
    /// <c>SeString.CreateMapLink</c>（那支吃的就是地圖座標）。
    /// ⇒ <b>可以直接交給 Mappy 的 <c>AddMarker</c>，兩邊的單位是同一個，不需要再換算。</b>
    /// </para>
    /// </remarks>
    public sealed class TrainMob
    {
        public string Name { get; set; } = string.Empty;

        public uint MobID { get; set; }

        public uint TerritoryID { get; set; }

        public uint MapID { get; set; }

        public uint Instance { get; set; }

        public Vector2 Position { get; set; }

        public bool Dead { get; set; }

        public DateTime LastSeenUTC { get; set; }
    }

    private static readonly Lazy<ICallGateSubscriber<uint>> GetVersionGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<uint>("HH.GetVersion"));

    private static readonly Lazy<ICallGateSubscriber<List<TrainMob>>> GetTrainListGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<List<TrainMob>>("HH.GetTrainList"));

    /// <summary>
    /// 「有怪被標記為已看過」的廣播。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>刻意宣告成 <c>object</c> 而不是 <see cref="TrainMob"/>。</b>
    /// 訂閱端的委派是由<b>對方</b>的 <c>SendMessage</c> 呼叫的，我們這裡擲出去的例外會一路傳回
    /// Hunt Helper 的事件裡——用嚴格型別接的話，對方哪天改了 <c>MobRecord</c> 的形狀，
    /// 我們的轉型失敗就會變成<b>別人家的例外</b>。
    /// 收成 <c>object</c> 時 Dalamud 只是把它轉成 <c>JObject</c>，實務上不會失敗。
    /// <para>
    /// 📌 反正我們也不需要這筆內容：收到通知只是把「該重新拉一次清單」的旗標打開，
    /// 真正的資料一律回頭問 <see cref="TryGetTrainList"/>（那才是完整且當下的清單）。
    /// </para>
    /// </remarks>
    private static readonly Lazy<ICallGateSubscriber<object, bool>> MarkSeenChannel =
        new(() => Svc.PluginInterface.GetIpcSubscriber<object, bool>("HH.channel.MarkSeen"));

    /// <summary>Hunt Helper 在不在（順便拿版本）。回 <see langword="false"/>＝沒裝或還沒註冊 IPC。</summary>
    public static bool TryGetVersion(out uint version)
    {
        version = 0;

        try
        {
            version = GetVersionGate.Value.InvokeFunc();
            return true;
        }
        catch (IpcError)
        {
            return false;
        }
        catch (Exception ex)
        {
            LogUnexpected("HH.GetVersion", ex);
            return false;
        }
    }

    /// <summary>
    /// 取目前的狩獵列車清單。<b>只能在框架執行緒上呼叫</b>（理由見類別註解）。
    /// </summary>
    /// <returns>IPC 打得通且轉換成功才回 <see langword="true"/>；否則 <paramref name="list"/> 為空清單。</returns>
    public static bool TryGetTrainList(out List<TrainMob> list)
    {
        try
        {
            list = GetTrainListGate.Value.InvokeFunc() ?? [];
            return true;
        }
        catch (IpcError ex)
        {
            // ⚠️ 這裡把 IpcError 分成兩種意思：沒註冊（正常）與型別轉換爆掉（值得知道）。
            //    後者代表對方的 MobRecord 形狀變了，而那正是本檔最容易靜默壞掉的地方。
            if (ex is not IpcNotReadyError) LogUnexpected("HH.GetTrainList（鏡像型別可能已對不上）", ex);

            list = [];
            return false;
        }
        catch (Exception ex)
        {
            LogUnexpected("HH.GetTrainList", ex);
            list = [];
            return false;
        }
    }

    /// <summary>訂閱「有怪被標記為已看過」。訂閱失敗不是致命的，只是會退回純輪詢。</summary>
    public static bool TrySubscribeMarkSeen(Action<object> handler)
    {
        try
        {
            MarkSeenChannel.Value.Subscribe(handler);
            return true;
        }
        catch (Exception ex)
        {
            LogUnexpected("HH.channel.MarkSeen（訂閱）", ex);
            return false;
        }
    }

    /// <summary>取消訂閱。必須傳入與訂閱時<b>同一個</b>委派實例。</summary>
    public static void TryUnsubscribeMarkSeen(Action<object> handler)
    {
        try
        {
            MarkSeenChannel.Value.Unsubscribe(handler);
        }
        catch (Exception ex)
        {
            LogUnexpected("HH.channel.MarkSeen（取消訂閱）", ex);
        }
    }

    private static void LogUnexpected(string endpoint, Exception ex)
    {
        if (!Throttle.Pass($"TCToolbox.HuntHelperIpc.Error.{endpoint}", ErrorLogIntervalMs)) return;

        // 🔴 Information 級：使用者跑 LogLevel 2，Debug／Verbose 收不到。
        Svc.Log.Information($"[HuntHelperIpc] 呼叫 {endpoint} 時發生非預期例外（{ErrorLogIntervalMs / 1000} 秒內只報一次）：{ex}");
    }
}

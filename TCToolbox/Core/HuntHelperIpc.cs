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
/// 🔴 <b>對不上的失敗形式是靜默的</b>：JSON 裡沒有對應鍵的成員會留在預設值
/// （名字變成空字串、<c>MapID</c> 變成 0、座標變成 0,0），<b>不會擲例外也不會有記錄</b>。
/// 改這個型別的成員名等於改 IPC 契約——<see cref="TrainMob"/> 的每個名字都必須逐字等於
/// 對方 <c>MobRecord</c> 的成員名。
/// 🔴🔴 <b><see cref="TryGetTrainList"/> 只能在框架執行緒上呼叫。</b>
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

        // 🔴 Information 級：使用者跑 LogLevel 1，盲區只有 Verbose,Debug 收得到但單檔數十萬行會淹沒。
        Svc.Log.Information($"[HuntHelperIpc] 呼叫 {endpoint} 時發生非預期例外（{ErrorLogIntervalMs / 1000} 秒內只報一次）：{ex}");
    }
}

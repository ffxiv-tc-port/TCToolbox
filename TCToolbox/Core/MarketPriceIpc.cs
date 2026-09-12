using System;
using System.Reflection;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;

namespace TCToolbox.Core;

/// <summary>
/// Marketbuddy 快取裡某件道具最近一次看到的掛單摘要（<b>鏡像型別</b>）。
/// </summary>
/// <remarks>
/// 🔴🔴 <b>成員名是跨外掛契約。</b>對方的型別是它自己組件裡的
/// <c>Marketbuddy.MarketDataCache.PublicSnapshot</c>（<c>internal</c>，我們接不到），
/// 所以宣告一個成員名<b>逐字相同</b>的鏡像型別讓 <c>CallGateChannel.ConvertObject</c>
/// 走 JSON 來回轉換。名字打錯＝那一欄靜默變 0，而 0 在價格欄剛好有意義（「沒有掛單」），
/// 所以錯了完全看不出來。
/// <para>
/// ⚠️ 屬性要 <c>public</c> get/set（對方那邊是 <c>init</c>，但那不影響我們的反序列化）。
/// </para>
/// </remarks>
public sealed class MarketSnapshotEntry
{
    /// <summary>道具編號。</summary>
    public uint ItemId { get; set; }

    /// <summary>這份資料屬於哪一個<b>世界</b>（對方整份快取隨世界切換清掉）。</summary>
    public uint WorldId { get; set; }

    /// <summary>看到這筆資料的時間（Unix 毫秒，UTC）。</summary>
    public long ObservedAtUnixMs { get; set; }

    /// <summary>第一頁裡的普通品掛單筆數。</summary>
    public int ListingCountNq { get; set; }

    /// <summary>第一頁裡的高品質掛單筆數。</summary>
    public int ListingCountHq { get; set; }

    /// <summary>普通品最低單價；<b>0 代表沒有普通品掛單</b>（不是「免費」）。</summary>
    public uint LowestPriceNq { get; set; }

    /// <summary>高品質最低單價；<b>0 代表沒有高品質掛單</b>。</summary>
    public uint LowestPriceHq { get; set; }

    /// <summary>
    /// true 代表對方<b>走完流程確認過「沒人在賣」</b>，而不是「還沒查過」。
    /// </summary>
    public bool ConfirmedEmpty { get; set; }
}

/// <summary>
/// 對 Marketbuddy 市場快取的<b>唯讀</b>查價包裝。
/// </summary>
/// <remarks>
/// <para>
/// 🔴🔴 <b>紅線：只顯示，不採購、不掛單、不送任何市場查詢。</b>對方那兩支端點的實作
/// 逐字是「純讀取，零副作用……對外開放的是<b>已經看到的東西</b>，不是幫你去查一次」。
/// 我們這一側也不做任何「去查一下」的動作。
/// </para>
/// <para>
/// 🔴 <b>所以查不到是常態，不是故障。</b>那份快取是被動累積的——使用者自己在市場佈告板上
/// 看過的道具才會有資料。畫面上一律畫灰色的「?」，<b>絕不畫 0</b>：0 在價格欄的意思是
/// 「沒有人在賣」，與「我不知道」是完全不同的一句話。
/// </para>
/// <para>
/// 📌 <b>為什麼不接 PriceInsight。</b>2026-09-12 逐字查過它的原始碼：
/// 它<b>完全沒有註冊任何 IPC 端點</b>（零個 <c>GetIpcProvider</c>），所以沒有可接的東西。
/// 艦隊裡目前唯一對外開放查價的是 Marketbuddy。
/// </para>
/// <para>
/// 🔑 版本號用 <c>&gt;=</c> 比對，不要用 <c>==</c>——對方合法地遞增版本時，
/// 嚴格相等會讓我們這邊<b>靜默失效</b>。
/// </para>
/// </remarks>
internal static class MarketPriceIpc
{
    /// <summary>本模組需要的最低契約版本（對方 <c>MarketCacheApiVersion</c>）。</summary>
    private const int RequiredApiVersion = 1;

    // Marketbuddy/IPCManager.cs:153 GetIpcProvider<int>("Marketbuddy.MarketCache.Version")
    private static readonly Lazy<ICallGateSubscriber<int>> VersionGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<int>("Marketbuddy.MarketCache.Version"));

    // Marketbuddy/IPCManager.cs:155
    //   GetIpcProvider<uint, MarketDataCache.PublicSnapshot?>("Marketbuddy.MarketCache.Get")
    private static readonly Lazy<ICallGateSubscriber<uint, MarketSnapshotEntry?>> GetGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<uint, MarketSnapshotEntry?>("Marketbuddy.MarketCache.Get"));

    private static bool IsIpcFailure(Exception ex) =>
        ex is IpcError or TargetInvocationException or InvalidCastException;

    /// <summary>
    /// Marketbuddy 的查價快取現在可以用嗎。
    /// </summary>
    /// <param name="reason">回 <see langword="false"/> 時可以直接顯示的原因。</param>
    /// <remarks>
    /// ⚠️ 這一支會真的做一次 IPC 呼叫，<b>不要每幀問</b>。呼叫端自己節流。
    /// </remarks>
    public static bool IsAvailable(out string reason)
    {
        try
        {
            var version = VersionGate.Value.InvokeFunc();
            if (version >= RequiredApiVersion)
            {
                reason = string.Empty;
                return true;
            }

            reason = $"Marketbuddy 的查價端點版本是 {version}，本模組需要 {RequiredApiVersion} 以上。";
            return false;
        }
        catch (IpcNotReadyError)
        {
            reason = "沒有偵測到 Marketbuddy（未安裝、未載入，或這一版沒有查價端點）。";
            return false;
        }
        catch (Exception ex) when (IsIpcFailure(ex))
        {
            reason = $"Marketbuddy 沒有回答版本查詢（{ex.GetType().Name}）。";
            return false;
        }
    }

    /// <summary>
    /// 這件道具在<b>本世界</b>最近一次看到的掛單摘要；<c>null</c>＝快取裡沒有這一筆。
    /// </summary>
    /// <remarks>
    /// 🔴 <c>null</c> 有兩種成因而且這裡分不出來：Marketbuddy 沒裝，或它裝著但沒看過這件東西。
    /// 呼叫端要先用 <see cref="IsAvailable"/> 把前者攔掉，剩下的 <c>null</c> 才是「還沒看過」。
    /// </remarks>
    public static MarketSnapshotEntry? TryGet(uint itemId)
    {
        if (itemId == 0) return null;

        try
        {
            return GetGate.Value.InvokeFunc(itemId);
        }
        catch (Exception ex) when (IsIpcFailure(ex))
        {
            // 節流：這條路徑一次重新整理會被呼叫幾十次，失敗時不要寫幾十行記錄。
            if (Throttle.Pass("MarketPriceIpc-Get-Failed", 60_000))
            {
                Svc.Log.Information(
                    $"[MarketPriceIpc] Marketbuddy.MarketCache.Get 呼叫失敗（{ex.GetType().Name}）：{ex.Message}");
            }

            return null;
        }
    }
}

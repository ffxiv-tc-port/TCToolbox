using System;
using System.Collections.Generic;
using System.Reflection;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;

namespace TCToolbox.Core;

/// <summary>
/// <c>ItemVendorLocation.GetItemVendorsWorld</c> 回傳的一筆商人資料（<b>鏡像型別</b>）。
/// </summary>
/// <remarks>
/// <para>
/// 🔴🔴 <b>成員名是跨外掛契約，一個字都不能改。</b>對方的型別是它自己組件裡的
/// <c>ItemVendorLocation.IPC.VendorLocationInfo</c>；我們宣告的型別不同 ⇒ Dalamud 的
/// <c>CallGateChannel.ConvertObject</c> 會走 <b>Newtonsoft JSON 來回轉換</b>
/// （<c>SerializeObject</c> → <c>DeserializeObject(json, 我們的型別)</c>）。
/// 名字打錯的失敗形式是<b>那個欄位靜默變成預設值</b>，不是例外——
/// 座標欄打錯就會得到一組 <c>(0, 0)</c>，而那正好長得像「在原點」。
/// </para>
/// <para>
/// 📌 對方少給幾個欄位是安全的（維持預設值）；對方多給欄位也安全（Newtonsoft 忽略）。
/// 所以這裡<b>刻意抄全</b>，即使目前畫面上用不到 <c>ShopSheetName</c> 之類的欄位——
/// 抄全的成本是幾行，漏抄的代價是將來要用時忘記它其實拿得到。
/// </para>
/// <para>
/// ⚠️ 屬性必須是 <c>public</c> 的 get/set：Newtonsoft 的預設 contract resolver
/// 只認公開可寫的成員，改成唯讀或 <c>internal</c> 都會讓整份資料變成預設值而不報錯。
/// </para>
/// </remarks>
public sealed class VendorLocationEntry
{
    /// <summary>問的是哪一個道具（原樣回拋）。</summary>
    public uint ItemId { get; set; }

    /// <summary>商人的 <c>ENpcResident</c> 列號。</summary>
    public uint NpcId { get; set; }

    /// <summary>商人名稱（客戶端語言，台服＝繁中）。</summary>
    public string NpcName { get; set; } = string.Empty;

    /// <summary>商店名稱；沒有就是空字串。</summary>
    public string ShopName { get; set; } = string.Empty;

    /// <summary>商店資料的列號（<c>0</c>＝對方建表時拿不到）。</summary>
    public uint ShopId { get; set; }

    /// <summary><see cref="ShopId"/> 是哪一張 Excel 表的列號；拿不到時是空字串。</summary>
    public string ShopSheetName { get; set; } = string.Empty;

    /// <summary>
    /// 取得管道：<c>GilShop</c>／<c>SpecialShop</c>／<c>GcShop</c>／<c>Achievement</c>／
    /// <c>FcShop</c>／<c>QuestReward</c>／<c>CollectableExchange</c>。
    /// </summary>
    public string SourceType { get; set; } = string.Empty;

    /// <summary>
    /// 這個商人有沒有已知的所在位置。
    /// </summary>
    /// <remarks>
    /// 🔴 <see langword="false"/> 時底下所有座標欄位都是 0，<b>那個 0 沒有意義</b>。
    /// 一律依這個旗標判斷，不要把 0 當成「在原點」——把角色送到 (0, 0) 的失敗是靜默的。
    /// </remarks>
    public bool HasLocation { get; set; }

    /// <summary>所在區域的 <c>TerritoryType</c> 列號。</summary>
    public uint TerritoryTypeId { get; set; }

    /// <summary>所在地圖的 <c>Map</c> 列號。</summary>
    public uint MapId { get; set; }

    /// <summary>世界座標 X。</summary>
    public float WorldX { get; set; }

    /// <summary>
    /// 世界座標 <b>Z</b>（不是地圖上的 Y）。<c>Lifestream.GoToMapPoint</c> 的第三個參數要的就是這個。
    /// </summary>
    public float WorldZ { get; set; }

    /// <summary>地圖座標 X（遊戲內地圖上顯示的那組數字）。</summary>
    public float MapX { get; set; }

    /// <summary>地圖座標 Y。</summary>
    public float MapY { get; set; }

    /// <summary>
    /// <see cref="MapX"/>／<see cref="MapY"/> 算得出來嗎。
    /// </summary>
    /// <remarks>
    /// 🔑 <b>這一欄的存在讓我們完全不必自己做地圖座標換算。</b>提供端已經解過
    /// <c>Map</c> 表的 <c>SizeFactor</c>／<c>Offset</c>，解不開時它把這一欄留成
    /// <see langword="false"/>、兩個座標留 0。我們照這一欄畫「?」就好，
    /// 不要再寫第四份座標公式（艦隊裡已經有三份彼此不相容的，見 <see cref="MapCoords"/>）。
    /// </remarks>
    public bool MapCoordinatesKnown { get; set; }

    /// <summary>要付的代價；可能是空清單（例如任務獎勵）。</summary>
    public List<VendorCostEntry> Costs { get; set; } = [];
}

/// <summary>一筆代價（<b>鏡像型別</b>，成員名不可改）。</summary>
public sealed class VendorCostEntry
{
    /// <summary>數量。</summary>
    public uint Amount { get; set; }

    /// <summary>貨幣／材料名稱（客戶端語言）。</summary>
    public string CurrencyName { get; set; } = string.Empty;
}

/// <summary>一次商人查詢的結果狀態。</summary>
public enum VendorLookupStatus
{
    /// <summary>
    /// 問不到：Item Vendor Location 沒裝、沒載入，或裝的版本沒有這支端點。
    /// </summary>
    /// <remarks>
    /// 🔴 零值刻意是「不知道」。把它併進 <see cref="NoVendor"/> 的話，
    /// 沒裝 IVL 的人會看到一整排「沒有商人賣」——那是一個有自信的錯誤答案。
    /// </remarks>
    Unavailable = 0,

    /// <summary>IVL 在，但它完全不認得這個道具（回 <see langword="null"/>）。</summary>
    NoVendor = 1,

    /// <summary>查到了（清單可能是空的：認得這個道具但沒有可回報的商人）。</summary>
    Ok = 2,
}

/// <summary>
/// 對 Item Vendor Location 的<b>唯讀</b> IPC 包裝：某個道具哪裡買得到。
/// </summary>
/// <remarks>
/// <para>
/// 📌 <b>用的是新端點 <c>ItemVendorLocation.GetItemVendorsWorld</c></b>（2026-09-12 加的），
/// 不是舊的 <c>GetItemVendors</c>。理由：舊那支只給<b>地圖座標</b>而且用巢狀 tuple，
/// 而 <c>Lifestream.GoToMapPoint</c> 要的是<b>世界座標</b>——拿地圖座標餵進去不會有錯誤訊息，
/// 角色只是被帶到那張圖上不相干的地方。新端點直接給世界座標。
/// </para>
/// <para>
/// 🔴 <b>「沒裝」與「版本太舊」都是 <c>IpcNotReadyError</c>，分不出來。</b>
/// 所以這裡多做一件事：端點不在時去查 Dalamud 的 <c>InstalledPlugins</c>，
/// 讓訊息說得出「IVL 裝著但這一版沒有這支端點，請更新」。
/// 不做這一步的話使用者會去外掛清單找一個他明明已經裝好的東西。
/// </para>
/// <para>
/// ⚠️ <b>三類例外都要攔，理由各不相同</b>（與 <see cref="AllaganToolsIpc"/> 同一份契約）：
/// <c>IpcError</c>（沒註冊／型別不合，兩者都是它的子類）、
/// <c>TargetInvocationException</c>（<b>提供端自己實作擲出來的</b>——CallGate 用
/// <c>DynamicInvoke</c> 呼叫同步提供端，所以會被包成這一個型別，<c>IpcError</c> 攔不到）、
/// <c>InvalidCastException</c>（對方換了回傳值的形狀）。
/// 一律不裸 <c>catch (Exception)</c>。
/// </para>
/// <para>
/// 🔑 回傳型別是<b>參考型別</b> <c>List&lt;T&gt;</c>，所以對方回 <see langword="null"/> 是安全的
/// （<c>CallGateChannel</c> 的 <c>(TRet)result</c> 只對<b>可空值型別</b>擲那個看起來與 IPC
/// 毫無關係的 <c>NullReferenceException</c>）。
/// </para>
/// </remarks>
internal static class VendorLookupIpc
{
    /// <summary>Item Vendor Location 的外掛內部名（feed 上的 <c>InternalName</c>）。</summary>
    public const string PluginInternalName = "ItemVendorLocation";

    // ItemVendorLocation/IPC/ItemVendorLocation.cs:30
    //   GetIpcProvider<uint, List<VendorLocationInfo>?>("ItemVendorLocation.GetItemVendorsWorld")
    private static readonly Lazy<ICallGateSubscriber<uint, List<VendorLocationEntry>?>> ItemVendorsWorldGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<uint, List<VendorLocationEntry>?>(
            "ItemVendorLocation.GetItemVendorsWorld"));

    // ItemVendorLocation/IPC/ItemVendorLocation.cs:28
    //   GetIpcProvider<uint, object?>("ItemVendorLocation.OpenVendorResults")
    // 📌 這一支是 Func<uint, object?> 不是 Action：它回 null，所以要用 InvokeFunc。
    private static readonly Lazy<ICallGateSubscriber<uint, object?>> OpenVendorResultsGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<uint, object?>("ItemVendorLocation.OpenVendorResults"));

    /// <summary>這一類例外全部代表「這條 IPC 現在不能用」，而不是我們自己算錯。</summary>
    private static bool IsIpcFailure(Exception ex) =>
        ex is IpcError or TargetInvocationException or InvalidCastException;

    /// <summary>
    /// 這個道具哪裡買得到。
    /// </summary>
    /// <param name="itemId">道具編號。</param>
    /// <param name="vendors">查到的商人（<b>永不為 null</b>，查不到時是空清單）。</param>
    /// <param name="reason">
    /// 回 <see cref="VendorLookupStatus.Unavailable"/> 時可以直接顯示的原因。
    /// </param>
    public static VendorLookupStatus TryGetVendors(
        uint itemId, out List<VendorLocationEntry> vendors, out string reason)
    {
        vendors = [];
        reason = string.Empty;

        if (itemId == 0)
        {
            reason = "道具編號是 0。";
            return VendorLookupStatus.Unavailable;
        }

        try
        {
            var result = ItemVendorsWorldGate.Value.InvokeFunc(itemId);

            // 🔴 null 與空清單是<b>不同的答案</b>：提供端的註解逐字寫著
            //    「null 代表完全不認得這個道具，空清單代表認得但沒有可回報的商人」。
            //    併成一種的話，「這東西只能自己做／只有市場有」與「IVL 沒有這筆資料」
            //    在畫面上會長得一樣。
            if (result == null) return VendorLookupStatus.NoVendor;

            vendors = result;
            return VendorLookupStatus.Ok;
        }
        catch (IpcNotReadyError)
        {
            reason = IsIvlLoaded()
                ? "Item Vendor Location 裝著，但這一版沒有 GetItemVendorsWorld 端點，請更新它。"
                : "沒有偵測到 Item Vendor Location（未安裝或未載入）。";
            return VendorLookupStatus.Unavailable;
        }
        catch (Exception ex) when (IsIpcFailure(ex))
        {
            Svc.Log.Information(
                $"[VendorLookupIpc] ItemVendorLocation.GetItemVendorsWorld({itemId}) 呼叫失敗"
                + $"（{ex.GetType().Name}）：{ex.Message}");
            reason = $"Item Vendor Location 沒有回答商人查詢（{ex.GetType().Name}）。";
            return VendorLookupStatus.Unavailable;
        }
    }

    /// <summary>
    /// 叫 Item Vendor Location 自己開它的結果視窗（完整清單、篩選、右鍵選單都在那裡）。
    /// </summary>
    /// <remarks>
    /// 📌 <b>刻意不在我們這邊重做那扇視窗。</b>IVL 的結果視窗已經處理了「同一件東西好幾家店賣」
    /// 「代價是別的道具」這些情況，重做一份只會多一份要跟著它改的碼。
    /// 我們的表負責「缺什麼」，細節交給它。
    /// </remarks>
    /// <returns><see langword="false"/>＝IVL 未安裝／未載入（什麼都沒發生）。</returns>
    public static bool TryOpenVendorWindow(uint itemId)
    {
        if (itemId == 0) return false;

        try
        {
            OpenVendorResultsGate.Value.InvokeFunc(itemId);
            return true;
        }
        catch (Exception ex) when (IsIpcFailure(ex))
        {
            Svc.Log.Information(
                $"[VendorLookupIpc] ItemVendorLocation.OpenVendorResults({itemId}) 呼叫失敗"
                + $"（{ex.GetType().Name}）：{ex.Message}");
            return false;
        }
    }

    /// <summary>Item Vendor Location 有沒有載入。</summary>
    /// <remarks>
    /// 🔴 只用來<b>把錯誤訊息講清楚</b>，不當成功能閘門：真正的判準永遠是「端點答不答得出來」。
    /// 拿「有沒有載入」當閘門的話，對方載入中／端點還沒註冊的那一小段就會被判成可用。
    /// </remarks>
    private static bool IsIvlLoaded()
    {
        try
        {
            foreach (var plugin in Svc.PluginInterface.InstalledPlugins)
            {
                if (!plugin.IsLoaded) continue;
                if (string.Equals(plugin.InternalName, PluginInternalName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch (Exception ex)
        {
            // 問不到就當成沒裝（訊息退回比較保守的那一句），但把原因留在記錄裡。
            Svc.Log.Information($"[VendorLookupIpc] 列舉已安裝外掛失敗：{ex.Message}");
        }

        return false;
    }
}

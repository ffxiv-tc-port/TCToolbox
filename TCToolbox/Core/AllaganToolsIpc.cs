using System;
using System.Collections.Generic;
using System.Reflection;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;

namespace TCToolbox.Core;

/// <summary>
/// 對 AllaganTools（InventoryTools）的<b>唯讀</b> IPC 包裝：只問「有哪些搜尋清單」與
/// 「某個清單現在包含哪些道具」，不改對方任何狀態。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 <b>刻意不用 <c>AllaganTools.GetRetrievalItems</c>。</b>對方那支的第一個判斷就是
/// 「目前有沒有作用中的<b>製作清單</b>」（<c>GetActiveCraftList()</c> ＋
/// <c>FilterType == CraftFilter</c>）——沒有作用中的製作清單時它回的是空字典，
/// 而那與「清單是空的」完全分不出來。本模組要的是「使用者挑一個清單」，不是製作清單。
/// </para>
/// <para>
/// ⚠️ <b><see cref="TryGetSearchFilters"/> 只列得出 <c>SearchFilter</c> 一種。</b>
/// 對方的 <c>GetSearchFilters</c> 實作是
/// <c>Lists.Where(c =&gt; c.FilterType == FilterType.SearchFilter)</c>——
/// 排序清單（SortingFilter）與遊戲道具清單（GameItemFilter）<b>不會出現在下拉選單裡</b>，
/// 即使 <see cref="TryGetFilterItems"/> 對那兩種是能用的。
/// 這就是模組面板上還留一個「直接輸入清單名稱」欄位的原因：
/// 對方的 <c>GetFilterItems</c> 走的是 <c>GetListByKeyOrName</c>，<b>鍵或名稱都收</b>。
/// </para>
/// <para>
/// 🔴 <b><see cref="TryGetFilterItems"/> 是重的，不要每幀呼叫。</b>對方在裡面對
/// SearchFilter／SortingFilter／GameItemFilter 都會走一次 <c>RefreshList(filter)</c>
/// ——那是整份清單重算。呼叫端要自己快取（本外掛只在「選了清單」「按重新整理」
/// 「開始取回」這三個時刻各叫一次）。
/// </para>
/// <para>
/// ⚠️ <b>三類例外都要攔，理由各不相同</b>：
/// <list type="bullet">
/// <item><see cref="IpcError"/>：對方沒裝／端點還沒註冊（<c>IpcNotReadyError</c>），
/// 或宣告型別對不上（<c>IpcTypeMismatchError</c>）。兩者都是 <see cref="IpcError"/> 的子類。</item>
/// <item><see cref="TargetInvocationException"/>：<b>對方自己的實作擲出來的例外</b>。
/// Dalamud 的 CallGate 是用 <c>Func.DynamicInvoke</c> 呼叫提供端的
/// （<c>CallGateChannel.InvokeFunc</c>），所以提供端擲的東西一律被包成這一個型別，
/// <b><see cref="IpcError"/> 攔不到</b>。AllaganTools 這兩支端點都是同步的，
/// 所以是這個形狀而不是 faulted Task。</item>
/// <item><see cref="InvalidCastException"/>：對方換了回傳值的形狀。</item>
/// </list>
/// 一律不裸 <c>catch (Exception)</c>——那會連我們自己的程式錯誤一起吞掉。
/// </para>
/// </remarks>
internal static class AllaganToolsIpc
{
    /// <summary>建立 subscriber 是零成本的純本地物件；真正的探測發生在 InvokeFunc()。</summary>
    private static readonly Lazy<ICallGateSubscriber<bool>> IsInitializedGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<bool>("AllaganTools.IsInitialized"));

    private static readonly Lazy<ICallGateSubscriber<Dictionary<string, string>>> SearchFiltersGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<Dictionary<string, string>>("AllaganTools.GetSearchFilters"));

    private static readonly Lazy<ICallGateSubscriber<string, Dictionary<uint, uint>>> FilterItemsGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<string, Dictionary<uint, uint>>("AllaganTools.GetFilterItems"));

    /// <summary>這一類例外全部代表「這條 IPC 現在不能用」，而不是我們自己算錯。</summary>
    private static bool IsIpcFailure(Exception ex) =>
        ex is IpcError or TargetInvocationException or InvalidCastException;

    /// <summary>AllaganTools 有沒有裝、而且已經初始化完畢。</summary>
    /// <param name="reason">回 <see langword="false"/> 時，畫面上可以直接顯示的原因。</param>
    public static bool IsReady(out string reason)
    {
        try
        {
            if (IsInitializedGate.Value.InvokeFunc())
            {
                reason = string.Empty;
                return true;
            }

            reason = "AllaganTools 已載入但還沒初始化完畢。";
            return false;
        }
        catch (Exception ex) when (IsIpcFailure(ex))
        {
            reason = "沒有偵測到 AllaganTools（未安裝或未載入）。";
            return false;
        }
    }

    /// <summary>
    /// 使用者的<b>搜尋清單</b>：鍵是清單 key，值是顯示名稱。
    /// </summary>
    /// <remarks>
    /// ⚠️ 回 <see langword="true"/> 而字典是空的，是<b>合法且常見</b>的結果
    /// （使用者一個搜尋清單都沒建）。呼叫端必須把「空」與「問不到」分開處理——
    /// 兩者在畫面上要顯示不同的話。
    /// </remarks>
    public static bool TryGetSearchFilters(out Dictionary<string, string> filters, out string reason)
    {
        filters = [];

        if (!IsReady(out reason)) return false;

        try
        {
            filters = SearchFiltersGate.Value.InvokeFunc() ?? [];
            reason = string.Empty;
            return true;
        }
        catch (Exception ex) when (IsIpcFailure(ex))
        {
            Svc.Log.Information(
                $"[AllaganToolsIpc] AllaganTools.GetSearchFilters 呼叫失敗（{ex.GetType().Name}）：{ex.Message}");
            reason = $"AllaganTools 沒有回答清單查詢（{ex.GetType().Name}）。";
            return false;
        }
    }

    /// <summary>
    /// 某個清單目前包含哪些道具編號。<paramref name="keyOrName"/> 可以是清單 key，也可以是清單名稱。
    /// </summary>
    /// <remarks>
    /// 📌 對方回的是「道具編號 → 數量」，但這裡<b>只取編號</b>：那個數量的語意隨清單型別而不同
    /// （製作清單是「需要幾個」、搜尋／排序清單是「所有來源加總的持有量」、
    /// 遊戲道具清單一律是 0），拿來當「僱員身上有幾個」用一定會錯。
    /// 本模組只需要它當「哪些道具算數」的白名單，真正的數量一律自己讀僱員容器。
    /// </remarks>
    public static bool TryGetFilterItems(string keyOrName, out HashSet<uint> itemIds, out string reason)
    {
        itemIds = [];

        if (string.IsNullOrWhiteSpace(keyOrName))
        {
            reason = "沒有指定清單。";
            return false;
        }

        if (!IsReady(out reason)) return false;

        try
        {
            var items = FilterItemsGate.Value.InvokeFunc(keyOrName);
            if (items != null)
            {
                foreach (var id in items.Keys)
                {
                    if (id != 0) itemIds.Add(id);
                }
            }

            reason = string.Empty;
            return true;
        }
        catch (Exception ex) when (IsIpcFailure(ex))
        {
            Svc.Log.Information(
                $"[AllaganToolsIpc] AllaganTools.GetFilterItems(\"{keyOrName}\") 呼叫失敗（{ex.GetType().Name}）：{ex.Message}");
            reason = $"AllaganTools 沒有回答清單內容（{ex.GetType().Name}）。";
            return false;
        }
    }
}

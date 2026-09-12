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
/// 一律不裸 <c>catch (Exception)</c>——那會連我們自己的程式錯誤一起吞掉。
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

    // InventoryTools/IPC/IPCService.cs:522 GetIpcProvider<Dictionary<string,string>>("AllaganTools.GetCraftLists")
    private static readonly Lazy<ICallGateSubscriber<Dictionary<string, string>>> CraftListsGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<Dictionary<string, string>>("AllaganTools.GetCraftLists"));

    // InventoryTools/IPC/IPCService.cs:503
    //   GetIpcProvider<string, Dictionary<uint, uint>>("AllaganTools.GetCraftItems")
    private static readonly Lazy<ICallGateSubscriber<string, Dictionary<uint, uint>>> CraftItemsGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<string, Dictionary<uint, uint>>("AllaganTools.GetCraftItems"));

    // InventoryTools/IPC/IPCService.cs:512
    //   GetIpcProvider<bool, HashSet<ulong>>("AllaganTools.GetCharactersOwnedByActive")
    private static readonly Lazy<ICallGateSubscriber<bool, HashSet<ulong>>> OwnedCharactersGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<bool, HashSet<ulong>>(
            "AllaganTools.GetCharactersOwnedByActive"));

    // InventoryTools/IPC/IPCService.cs:458 GetIpcProvider<uint, ulong, int, uint>("AllaganTools.ItemCount")
    private static readonly Lazy<ICallGateSubscriber<uint, ulong, int, uint>> ItemCountGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<uint, ulong, int, uint>("AllaganTools.ItemCount"));

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

    /// <summary>
    /// 使用者的<b>製作清單</b>：鍵是清單 key，值是顯示名稱。
    /// </summary>
    /// <remarks>
    /// 🔴 「空」與「問不到」必須分開處理：前者是「你還沒建製作清單」，後者是
    /// 「AllaganTools 不在」。畫成同一句話的話，使用者會去建一張他根本不需要的清單。
    /// </remarks>
    public static bool TryGetCraftLists(out Dictionary<string, string> lists, out string reason)
    {
        lists = [];

        if (!IsReady(out reason)) return false;

        try
        {
            lists = CraftListsGate.Value.InvokeFunc() ?? [];
            reason = string.Empty;
            return true;
        }
        catch (Exception ex) when (IsIpcFailure(ex))
        {
            Svc.Log.Information(
                $"[AllaganToolsIpc] AllaganTools.GetCraftLists 呼叫失敗（{ex.GetType().Name}）：{ex.Message}");
            reason = $"AllaganTools 沒有回答製作清單查詢（{ex.GetType().Name}）。";
            return false;
        }
    }

    /// <summary>
    /// 某個製作清單要做的<b>成品</b>：道具編號 → 要做幾個。
    /// </summary>
    /// <param name="keyOrName">清單 key 或清單名稱（對方走 <c>GetListByKeyOrName</c>，兩者都收）。</param>
    public static bool TryGetCraftOutputs(
        string keyOrName, out Dictionary<uint, uint> outputs, out string reason)
    {
        outputs = [];

        if (string.IsNullOrWhiteSpace(keyOrName))
        {
            reason = "沒有指定製作清單。";
            return false;
        }

        if (!IsReady(out reason)) return false;

        try
        {
            outputs = CraftItemsGate.Value.InvokeFunc(keyOrName) ?? [];
            reason = string.Empty;
            return true;
        }
        catch (Exception ex) when (IsIpcFailure(ex))
        {
            Svc.Log.Information(
                $"[AllaganToolsIpc] AllaganTools.GetCraftItems(\"{keyOrName}\") 呼叫失敗"
                + $"（{ex.GetType().Name}）：{ex.Message}");
            reason = $"AllaganTools 沒有回答製作清單內容（{ex.GetType().Name}）。";
            return false;
        }
    }

    /// <summary>
    /// 目前角色名下所有「有道具欄的東西」的擁有者編號：角色本人、他的僱員、部隊、宅邸。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>它是照 AllaganTools 的記錄回答的，不是照「現在讀得到的」。</b>
    /// 沒有被 AllaganTools 掃過的僱員不會在裡面 —— 那是「持有量算少了」的方向，
    /// 所以呼叫端要把這個數字顯示出來，讓使用者自己判斷合不合理。
    /// </remarks>
    public static bool TryGetOwnerIds(out List<ulong> ownerIds, out string reason)
    {
        ownerIds = [];

        if (!IsReady(out reason)) return false;

        try
        {
            var ids = OwnedCharactersGate.Value.InvokeFunc(true);
            if (ids != null)
            {
                foreach (var id in ids)
                {
                    if (id != 0) ownerIds.Add(id);
                }
            }

            reason = string.Empty;
            return true;
        }
        catch (Exception ex) when (IsIpcFailure(ex))
        {
            Svc.Log.Information(
                $"[AllaganToolsIpc] AllaganTools.GetCharactersOwnedByActive 呼叫失敗"
                + $"（{ex.GetType().Name}）：{ex.Message}");
            reason = $"AllaganTools 沒有回答角色／僱員清單（{ex.GetType().Name}）。";
            return false;
        }
    }

    /// <summary>
    /// 某個擁有者身上這件道具的<b>總數（HQ 與 NQ 合計）</b>。
    /// </summary>
    /// <remarks>
    /// ⚠️ 第三個參數傳 <c>-1</c>＝<b>不限道具欄類型</b>（對方的實作是
    /// <c>inventoryType == -1 || ...</c>）。傳 0 的話會變成「只算第 0 號道具欄」，
    /// 而 0 是一個有效的道具欄編號 —— 失敗形式是靜默地只算到背包第一頁。
    /// </remarks>
    /// <returns><see langword="false"/>＝這條 IPC 現在不能用（<paramref name="count"/> 不可信）。</returns>
    public static bool TryGetItemCount(uint itemId, ulong ownerId, out uint count)
    {
        count = 0;

        if (itemId == 0) return false;

        try
        {
            count = ItemCountGate.Value.InvokeFunc(itemId, ownerId, -1);
            return true;
        }
        catch (Exception ex) when (IsIpcFailure(ex))
        {
            // 節流：一次重新整理會呼叫幾百次，失敗時不要寫幾百行記錄。
            if (Throttle.Pass("AllaganToolsIpc-ItemCount-Failed", 60_000))
            {
                Svc.Log.Information(
                    $"[AllaganToolsIpc] AllaganTools.ItemCount 呼叫失敗（{ex.GetType().Name}）：{ex.Message}");
            }

            return false;
        }
    }
}

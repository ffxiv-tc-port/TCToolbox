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

    /// <summary>
    /// 使用者的<b>製作清單</b>：鍵是清單 key，值是顯示名稱。
    /// </summary>
    /// <remarks>
    /// ⚠️ 對方的實作是 <c>Lists.Where(c =&gt; c.FilterType == FilterType.CraftFilter
    /// &amp;&amp; !c.CraftListDefault)</c>——<b>那個「預設製作清單」被刻意排除了</b>。
    /// 使用者只用過那張預設清單的話，這裡會回一個空字典（而且是<b>合法</b>結果），
    /// 所以呼叫端要留一個「直接輸入清單名稱／key」的欄位當後路：
    /// <see cref="TryGetCraftOutputs"/> 走的是 <c>GetListByKeyOrName</c>，<b>鍵或名稱都收</b>。
    /// <para>
    /// 🔴 「空」與「問不到」必須分開處理：前者是「你還沒建製作清單」，後者是
    /// 「AllaganTools 不在」。畫成同一句話的話，使用者會去建一張他根本不需要的清單。
    /// </para>
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
    /// <remarks>
    /// 🔴🔴 <b>這支回的是成品，不是材料。</b>對方的 <c>GetCraftItems</c> 實作只收
    /// <c>craftItem.IsOutputItem</c> 為真的那幾筆（<c>InventoryTools/IPC/IPCService.cs:161</c>），
    /// 也就是「清單最上層那幾個要做的東西」。<b>材料要自己從 <c>Recipe</c> 表展開</b>
    /// （見 <see cref="RecipeMaterials"/>）——沒有任何 IPC 端點會給你材料清單，
    /// Artisan 也沒有「清單裡有什麼」的端點。
    /// <para>
    /// 🔴 <b>刻意不用 <c>AllaganTools.GetRetrievalItems</c>。</b>那支的第一件事是
    /// <c>GetActiveCraftList()</c>——它回的是「目前<b>作用中</b>的製作清單要從僱員取回什麼」，
    /// 沒有作用中的清單時回空字典，與「清單是空的」分不出來；而且它算的是<b>取回量</b>
    /// 不是需求量。我們要的是「使用者挑的那一張清單」。
    /// </para>
    /// <para>
    /// ⚠️ 清單型別不是製作清單時對方回<b>空字典</b>（不是例外）。這裡照樣回
    /// <see langword="true"/>＋空字典——呼叫端顯示「這張清單沒有要做的東西」，
    /// 而不是報錯。
    /// </para>
    /// </remarks>
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
    /// 📌 對方的實作是「列舉它記得的所有道具欄擁有者，留下
    /// <c>BelongsToActiveCharacter</c> 為真的」（<c>includeOwner: true</c> 時含角色自己）。
    /// 所以這份清單的長度＝角色本人 ＋ 僱員數 ＋ 部隊／宅邸（若有記錄）。
    /// <para>
    /// ⚠️ <b>它是照 AllaganTools 的記錄回答的，不是照「現在讀得到的」。</b>
    /// 沒有被 AllaganTools 掃過的僱員不會在裡面 —— 那是「持有量算少了」的方向，
    /// 所以呼叫端要把這個數字顯示出來，讓使用者自己判斷合不合理。
    /// </para>
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
    /// <para>
    /// 🔴 <b>這支回的是 HQ＋NQ 合計。</b>對方的 <c>ItemCount</c> 完全不看
    /// <c>InventoryItem.ItemFlags</c>（只有 <c>ItemCountHQ</c> 才篩 <c>HighQuality</c>），
    /// 所以它等於「不分品質的持有量」。製作材料的缺口就是要這個——HQ 材料一樣做得出東西。
    /// </para>
    /// <para>
    /// 📌 <paramref name="ownerId"/> 對應對方 <c>InventoryItem.RetainerId</c> 那一欄，
    /// 而那一欄存的是<b>這個道具欄的擁有者</b>（角色本人也算），不是「僱員」而已。
    /// 所以角色自己的 id 傳進去就會拿到他背包＋武具庫＋鞍袋等等的合計。
    /// </para>
    /// <para>
    /// 🔴 <b>這支是重的，不要在 Draw 路徑上或每幀呼叫。</b>對方的實作是對
    /// <c>AllItems</c>（所有已知道具欄的每一格）做一次 LINQ 全掃，而這個模組一次重新整理
    /// 要問「材料數 × 擁有者數」次。呼叫端必須把它切開分幾幀做。
    /// </para>
    /// <para>
    /// ⚠️ 第三個參數傳 <c>-1</c>＝<b>不限道具欄類型</b>（對方的實作是
    /// <c>inventoryType == -1 || ...</c>）。傳 0 的話會變成「只算第 0 號道具欄」，
    /// 而 0 是一個有效的道具欄編號 —— 失敗形式是靜默地只算到背包第一頁。
    /// </para>
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

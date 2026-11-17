using FFXIVClientStructs.FFXIV.Client.Game;

namespace TCToolbox.Core;

/// <summary>取得背包類容器並判斷「能不能走訪」的唯一入口；本插件不在別處直接呼叫 <c>GetInventoryContainer</c>。</summary>
/// <remarks>
/// 🔴 判的是 <c>Items</c> 不是 <c>GetInventorySlot</c> 的回傳值：<c>Items</c> 為 null 而 <c>Size &gt; 0</c> 時，
/// <c>GetInventorySlot(i)</c> 回的是「null＋偏移」的非 null 假指標，判空一定通過，解參考就是攔不到的 AVE。
/// 🔴 契約：out 出來的指標只在這一次呼叫、這一幀內有效；不可存進欄位、不可跨 await 或延後工作。
/// <c>Size</c> 刻意不在這裡擋：走訪一律以 <c>Size</c> 為上界、單格存取各自判格號範圍；
/// 在這裡擋會把「空容器」改判成「讀不到」，回傳「不知道」的呼叫端語意會跟著變。
/// </remarks>
internal static unsafe class InventoryScan
{
    /// <summary>容器存在、已載入、格位陣列已配置才回 <see langword="true"/>。</summary>
    public static bool TryGet(InventoryManager* manager, InventoryType type, out InventoryContainer* container)
        => TryGetCore(manager, type, requireLoaded: true, out container);

    /// <summary>同 <see cref="TryGet"/> 但不看 <c>IsLoaded</c>：只給刻意要跟別的插件或既有判斷看同一份資料的呼叫端。</summary>
    public static bool TryGetIgnoringLoaded(InventoryManager* manager, InventoryType type, out InventoryContainer* container)
        => TryGetCore(manager, type, requireLoaded: false, out container);

    /// <summary>純判斷，不呼叫任何原生函式；<paramref name="container"/> 為 null 時不解參考。</summary>
    internal static bool IsWalkable(InventoryContainer* container, bool requireLoaded)
    {
        if (container == null) return false;
        if (requireLoaded && !container->IsLoaded) return false;
        return container->Items != null;
    }

    private static bool TryGetCore(InventoryManager* manager, InventoryType type, bool requireLoaded, out InventoryContainer* container)
    {
        container = null;
        if (manager == null) return false;

        var found = manager->GetInventoryContainer(type);
        if (!IsWalkable(found, requireLoaded)) return false;

        container = found;
        return true;
    }
}

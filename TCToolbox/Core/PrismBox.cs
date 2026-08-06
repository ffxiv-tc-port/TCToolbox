using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace TCToolbox.Core;

/// <summary>
/// 投影台（Glamour Dresser／<c>MirageManager</c> 的 PrismBox）共用存取層。
///
/// 🔴 <b>不跨幀保存任何原生指標。</b><see cref="Snapshot"/> 回傳的是**受管理的複本**，
/// 每一次要動作時都重新取 <c>MirageManager.Instance()</c>。
///
/// 🔴 <b>CS 的 <c>MirageManager.Instance()</c> 在特徵碼解析失敗時是「擲例外」而不是「回 null」</b>
/// （InteropGenerator 產生的碼會呼叫 <c>ThrowHelper.ThrowNullAddress</c>），
/// <c>RestorePrismBoxItem</c> 也一樣。所以這裡一律先看 <c>Addresses.*.Value</c> 是不是 0，
/// 而不是直接呼叫再期待它回 null —— 見 <see cref="IsAvailable"/>。
///
/// 📌 兩條特徵碼都對台服 7.20 <c>ffxiv_dx11.exe</c> 做過離線唯一性驗證
/// （工具 <c>~/.claude/tools/sigscan/verify_cs_sigs.py</c>，校準閘門全過）：
///   <c>MirageManager.Instance</c>        StaticAddress 命中 1 次 → 靜態指標 0x14292B520（.data）
///   <c>RestorePrismBoxItem</c>           MemberFunction 命中 1 次 → 函式 0x14084FE10
/// ⚠️ 這只證明「這一版解析得到」，不證明語意；語意仍以 CS 的宣告為準。
/// </summary>
public static unsafe class PrismBox
{
    /// <summary>投影台裡的一格（已排除空格）。全部是值複本，不含指標。</summary>
    /// <param name="Index">在 <c>PrismBoxItemIds</c> 裡的索引，也是 <c>RestorePrismBoxItem</c> 的參數。</param>
    /// <param name="FullItemId">原始值，HQ 是 <c>+1000000</c> 編碼。</param>
    /// <param name="BaseItemId">去掉 HQ 編碼後的 <c>Item</c> 列號。</param>
    public readonly record struct Entry(int Index, uint FullItemId, uint BaseItemId, byte Stain0, byte Stain1)
    {
        /// <summary>有沒有被染色（兩個染色槽任一非 0）。</summary>
        public bool IsDyed => Stain0 != 0 || Stain1 != 0;
    }

    /// <summary>兩條特徵碼都解析得到才算可用。解析不到時**絕不呼叫**，否則會擲例外。</summary>
    public static bool IsAvailable =>
        MirageManager.Addresses.Instance.Value != 0 &&
        MirageManager.Addresses.RestorePrismBoxItem.Value != 0;

    private static MirageManager* Manager() => IsAvailable ? MirageManager.Instance() : null;

    /// <summary>
    /// 這一刻能不能安全地對投影台動作。**這是 fail-closed 的閘門，不是建議**。
    ///
    /// 🔑 少了它最典型的失敗形式是「一致的回 0」：投影台資料在切換區域時會被清空
    /// （CS 對 <c>MirageManager</c> 的註解就這麼寫），沒載入時整個 800 格都是 0，
    /// 掃出來一定是「沒有重複」「沒有可收進收藏櫃的」—— 看起來像功能正常執行完，
    /// 其實是根本沒有資料。所以資料沒載入時要**明講**，不能安靜地回報「沒有東西要處理」。
    /// </summary>
    public static bool TryReady(out string reason)
    {
        reason = string.Empty;

        if (!IsAvailable)
        {
            reason = "找不到投影台的遊戲函式（特徵碼失效），本功能已停用。";
            return false;
        }

        if (!Svc.ClientState.IsLoggedIn || Svc.Objects.LocalPlayer == null || Svc.Condition[ConditionFlag.BetweenAreas])
        {
            reason = "正在切換區域，請稍後再試。";
            return false;
        }

        var manager = Manager();
        if (manager == null)
        {
            reason = "取不到投影台資料。";
            return false;
        }

        if (!manager->PrismBoxRequested || !manager->PrismBoxLoaded)
        {
            reason = "投影台資料尚未載入，請先在投影台前開啟「投影台」視窗。";
            return false;
        }

        // 🔴 要求視窗開著是刻意的 fail-closed：取出動作是送給伺服器的請求，
        // 沒開投影台時伺服器多半直接忽略，而那種失敗**完全靜默**。
        // 寧可在這裡講清楚，也不要讓使用者按了按鈕以為有跑。
        if (!UiHelper.IsAddonReady("MiragePrismPrismBox"))
        {
            reason = "請先開啟「投影台」視窗再執行。";
            return false;
        }

        return true;
    }

    /// <summary>目前投影台內容的受管理複本（不含空格）。呼叫端拿到後可以跨幀持有。</summary>
    public static List<Entry> Snapshot()
    {
        var result = new List<Entry>();
        var manager = Manager();
        if (manager == null) return result;

        var ids = manager->PrismBoxItemIds;
        var stain0 = manager->PrismBoxStain0Ids;
        var stain1 = manager->PrismBoxStain1Ids;

        for (var i = 0; i < ids.Length; i++)
        {
            var full = ids[i];
            if (full == 0) continue;

            result.Add(new Entry(
                i, full, full % 1_000_000,
                i < stain0.Length ? stain0[i] : (byte)0,
                i < stain1.Length ? stain1[i] : (byte)0));
        }

        return result;
    }

    /// <summary>目前非空格數。用來判斷伺服器有沒有真的把那一件取走。</summary>
    public static int LiveCount()
    {
        var manager = Manager();
        if (manager == null) return -1;

        var ids = manager->PrismBoxItemIds;
        var count = 0;
        for (var i = 0; i < ids.Length; i++)
        {
            if (ids[i] != 0) count++;
        }

        return count;
    }

    /// <summary>某一格現在是不是仍然是這件道具。用來確認快照沒有過期。</summary>
    public static bool IsEntryAt(int index, uint fullItemId)
    {
        var manager = Manager();
        if (manager == null) return false;

        var ids = manager->PrismBoxItemIds;
        return index >= 0 && index < ids.Length && ids[index] == fullItemId;
    }

    /// <summary>
    /// 把某一格取回背包。
    /// <para>回傳 <c>false</c> 代表遊戲當場拒絕（CS 註解：已持有的獨特道具、或背包空間不足），
    /// **不是**「送出失敗」；回傳 <c>true</c> 只代表請求已送出，實際結果要看投影台內容有沒有變。</para>
    /// </summary>
    public static bool Restore(int index)
    {
        var manager = Manager();
        if (manager == null) return false;
        if (index < 0 || index >= manager->PrismBoxItemIds.Length) return false;

        return manager->RestorePrismBoxItem((uint)index);
    }

    /// <summary>
    /// 背包（Inventory1–4）剩餘空格。
    /// ⚠️ 刻意不用 <c>GetInventoryItemCount</c> 那一族 —— 它在 <c>BetweenAreas</c> 會回 0，
    /// 而「回 0」在這裡會被解讀成「背包滿了」。<see cref="TryReady"/> 已經先擋掉切換區域。
    /// </summary>
    public static int EmptyBagSlots()
    {
        var manager = InventoryManager.Instance();
        return manager == null ? 0 : (int)manager->GetEmptySlotsInBag();
    }
}

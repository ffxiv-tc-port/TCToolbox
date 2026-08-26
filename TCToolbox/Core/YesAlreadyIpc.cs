using System;
using System.Collections.Generic;
using Dalamud.Plugin.Ipc;

namespace TCToolbox.Core;

/// <summary>
/// 對 YesAlready 的最小介接：查它現在會不會接手對話框，以及在我們的序列期間請它讓開。
/// </summary>
/// <remarks>
/// 🔴 存在的唯一理由：自動改名要驅動一連串 SelectYesno（其中「要儲存目前的形象嗎？」<b>必須按「否」</b>），
/// YesAlready 若在旁邊自動點確認框，會破壞這個確定性的順序 —— 而按錯的後果是
/// 僱員的形象範本被不可逆覆寫。所以改名序列期間請它讓開、序列一結束就放開。
/// <para>
/// 🔴 <b>做法是加入 YesAlready 自己的阻擋清單，不是把它整個關掉。</b>
/// 這是 ECommons 系外掛的既有標準做法（樣板＝AutoRetainer <c>NewYesAlreadyManager</c>）：
/// 共享資料 <c>"YesAlready.StopRequests"</c> 是一個 <c>HashSet&lt;string&gt;</c>，每個外掛放自己的名字；
/// YesAlready 端 <c>Active =&gt; C.Enabled &amp;&amp; !BlockListHandler.Locked</c>、
/// 而 <c>Locked =&gt; BlockList.Count != 0</c>，且每一個事件都重新計算 <c>Active</c>
/// （<c>Watcher.cs</c>／<c>BaseFeatures/AddonFeature.cs</c>），所以加進去即時生效。
/// </para>
/// <para>
/// 🔴 <b>為什麼不能沿用舊的「問它開著沒 → IPC 關掉 → 事後開回來」</b>：
/// <c>YesAlready.IsPluginEnabled</c> 回的是上面那個<b>複合值</b> <c>Active</c>，不是
/// 使用者的開關 <c>C.Enabled</c>。只要當下有<b>別的</b>外掛（AutoRetainer／AutoDuty…）
/// 在它自己的序列期間掛著 stop request，<c>Locked</c> 就成立、探針讀到 <c>false</c>，
/// 於是我們誤判成「本來就關著」而直接跳過 —— 不壓制、也不記帳。
/// 對方序列一結束把鎖放掉，YesAlready 就<b>在我們的改名序列中途醒過來</b>。
/// 那正是這個類別宣稱要消滅的 race，而探針量錯了命題。
/// 阻擋清單沒有這個問題：它天然可以和別的外掛的鎖並存（各放各的名字），
/// 完全不碰 <c>C.Enabled</c>，也就不需要任何「原始狀態」探針。
/// </para>
/// <para>⚠️ YesAlready 沒裝／共享資料拿不到＝沒有 race，一律當沒事跳過。</para>
/// <para>
/// ⚠️ <b>已知殘餘窗口</b>：YesAlready 的 <c>BlockListHandler</c> 建構時會 <c>Clear()</c> 整個清單，
/// 所以在我們掛著鎖的期間<b>重新載入 YesAlready</b> 會把我們的名字洗掉。
/// 這是這個機制本身的性質（AutoRetainer 也一樣）；<see cref="Suppress"/> 做成冪等，
/// 重複呼叫會把名字重新放回去。
/// </para>
/// </remarks>
internal static class YesAlreadyIpc
{
    /// <summary>YesAlready 的阻擋清單共享資料鍵（逐字對應 <c>BlockListHandler.BlockListNamespace</c>）。</summary>
    private const string BlockListNamespace = "YesAlready.StopRequests";

    // 建 subscriber 本身零成本；真正的探測發生在 Invoke 時。
    private static readonly Lazy<ICallGateSubscriber<bool>> IsEnabledGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<bool>("YesAlready.IsPluginEnabled"));

    /// <summary>true＝我們的名字目前掛在阻擋清單裡，收尾時要拿掉。</summary>
    private static bool suppressed;

    /// <summary>目前是否處於「已請 YesAlready 讓開」狀態。</summary>
    public static bool IsSuppressed => suppressed;

    /// <summary>放進阻擋清單的名字＝本外掛的 InternalName（與別的外掛各佔一格，互不干擾）。</summary>
    private static string BlockListKey => Svc.PluginInterface.InternalName;

    /// <summary>
    /// 問 YesAlready 現在會不會接手對話框。<c>null</c>＝沒裝／IPC 不在／問不到。
    /// </summary>
    /// <remarks>
    /// 📌 純查詢，<b>不會去改它的狀態</b>——給「要提醒使用者 YesAlready 可能會接手某個對話框」用。
    /// ⚠️ 回的是複合值 <c>Active</c>（使用者開關 <b>且</b> 沒有任何外掛掛著 stop request），
    /// 這對「它現在會不會接手」這個提醒來說正是要問的東西；
    /// 但它<b>不能</b>拿來推斷「使用者本來有沒有開」——那正是 <see cref="Suppress"/> 以前踩的坑。
    /// ⚠️ 另外問不到它個別功能的開關（那些只存在它自己的設定檔裡，沒有 IPC）。
    /// </remarks>
    public static bool? QueryActive()
    {
        try
        {
            return IsEnabledGate.Value.InvokeFunc();
        }
        catch (Exception)
        {
            // 沒裝／版本不合／gate 不存在：回「不知道」，呼叫端只是少顯示一句提醒。
            return null;
        }
    }

    /// <summary>請 YesAlready 在我們的序列期間讓開。沒裝／拿不到共享資料＝no-op。冪等。</summary>
    public static void Suppress()
    {
        if (!TryGetBlockList(out var blockList)) return;

        blockList.Add(BlockListKey);

        if (suppressed) return;

        suppressed = true;
        Svc.Log.Information("[RetainerBatchRename] 已請 YesAlready 讓開（改名序列期間），結束後會放開。");
    }

    /// <summary>放開 YesAlready。任何收尾／中止路徑都要呼叫，冪等。</summary>
    public static void Restore()
    {
        if (!suppressed) return;

        try
        {
            if (TryGetBlockList(out var blockList))
                blockList.Remove(BlockListKey);

            Svc.Log.Information("[RetainerBatchRename] 已放開 YesAlready。");
        }
        finally
        {
            suppressed = false;
        }
    }

    /// <summary>取得共享的阻擋清單；拿不到（沒裝／Dalamud 拒絕）就回 false，呼叫端一律當 no-op。</summary>
    private static bool TryGetBlockList(out HashSet<string> blockList)
    {
        try
        {
            blockList = Svc.PluginInterface.GetOrCreateData<HashSet<string>>(BlockListNamespace, () => []);
            return blockList != null;
        }
        catch (Exception ex)
        {
            blockList = null!;

            if (Throttle.Pass("YesAlreadyIpc-BlockList", 60_000))
                Svc.Log.Information($"[RetainerBatchRename] 取不到 YesAlready 的阻擋清單（不影響改名）：{ex.Message}");

            return false;
        }
    }
}

using System;
using System.Collections.Generic;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;

namespace TCToolbox.Core;

/// <summary>
/// 對 YesAlready 的最小介接：查它現在會不會接手對話框，以及在我們的序列期間請它讓開。
/// </summary>
/// <remarks>
/// 🔴 阻擋清單存在的理由：自動改名要驅動一連串 SelectYesno（其中「要儲存目前的形象嗎？」<b>必須按「否」</b>），
/// YesAlready 若在旁邊自動點確認框，會破壞這個確定性的順序 —— 而按錯的後果是
/// 僱員的形象範本被不可逆覆寫。所以改名序列期間請它讓開、序列一結束就放開。
/// 🔴 <b>做法是加入 YesAlready 自己的阻擋清單，不是把它整個關掉。</b>
/// <para>⚠️ YesAlready 沒裝／共享資料拿不到＝沒有 race，一律當沒事跳過。</para>
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

    public static bool IsSuppressed => suppressed;

    /// <summary>放進阻擋清單的名字＝本外掛的 InternalName（與別的外掛各佔一格，互不干擾）。</summary>
    private static string BlockListKey => Svc.PluginInterface.InternalName;

    /// <summary>
    /// 問 YesAlready 現在會不會接手對話框。<c>null</c>＝沒裝／IPC 不在／問不到。
    /// </summary>
    /// <remarks>
    /// ⚠️ 回的是複合值 <c>Active</c>（使用者開關 <b>且</b> 沒有任何外掛掛著 stop request），
    /// 這對「它現在會不會接手」這個提醒來說正是要問的東西；
    /// 但它<b>不能</b>拿來推斷「使用者本來有沒有開」。
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
        Svc.Log.Information("[RetainerBatchRename] 已請 YesAlready 讓開（批次期間），結束後會放開。");
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

    /// <summary><see cref="PauseBother"/> 的結果。</summary>
    public enum BotherPause
    {
        /// <summary>確認不了（YesAlready 載入了但 IPC 出錯，或問不到它有沒有載入）。零值刻意給這個。</summary>
        Failed = 0,

        /// <summary>已暫停，時間到 YesAlready 自己恢復。</summary>
        Paused,

        /// <summary>那個功能本來就沒開（YesAlready 回 false），不會自動按。</summary>
        NotActive,

        /// <summary>YesAlready 沒有載入。</summary>
        NotLoaded,
    }

    // 簽章逐字對應 YesAlready 的 PauseBother(string name, int milliseconds) -> bool。
    private static readonly Lazy<ICallGateSubscriber<string, int, bool>> PauseBotherGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<string, int, bool>("YesAlready.PauseBother"));

    /// <summary>請 YesAlready 把單一個自動按窗功能暫停一段時間；時間到它自己恢復，重疊的暫停取最長的那個。</summary>
    /// <param name="feature">YesAlready 的功能名（例如 <c>SelectString</c>）。</param>
    /// <param name="milliseconds">暫停多久。</param>
    public static BotherPause PauseBother(string feature, int milliseconds)
    {
        try
        {
            return PauseBotherGate.Value.InvokeFunc(feature, milliseconds) ? BotherPause.Paused : BotherPause.NotActive;
        }
        catch (IpcNotReadyError)
        {
            // 端點不在：YesAlready 沒載入才算沒事；載入了卻沒有這個端點就當成確認不了。
            return PluginPresence.IsLoaded("YesAlready") == false ? BotherPause.NotLoaded : BotherPause.Failed;
        }
        catch (Exception ex)
        {
            if (Throttle.Pass("YesAlreadyIpc-PauseBother", 60_000))
                Svc.Log.Information($"[YesAlreadyIpc] PauseBother（{feature}）失敗（{ex.GetType().Name}）：{ex.Message}");

            return BotherPause.Failed;
        }
    }

    /// <summary>取到之後快取：Dalamud 每次成功取用都多登記一個使用者、不去重，而呼叫端每幀都會來。</summary>
    private static HashSet<string>? cachedBlockList;

    /// <summary>取得共享的阻擋清單；拿不到（沒裝／Dalamud 拒絕）就回 false，呼叫端一律當 no-op。</summary>
    private static bool TryGetBlockList(out HashSet<string> blockList)
    {
        if (cachedBlockList != null)
        {
            blockList = cachedBlockList;
            return true;
        }

        try
        {
            blockList = Svc.PluginInterface.GetOrCreateData<HashSet<string>>(BlockListNamespace, () => []);
            cachedBlockList = blockList;
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

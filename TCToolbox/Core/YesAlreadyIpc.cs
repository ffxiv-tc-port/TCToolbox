using System;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;

namespace TCToolbox.Core;

/// <summary>
/// 對 YesAlready 的最小 IPC 包裝：查它開著沒、暫時關掉、事後還原。
/// </summary>
/// <remarks>
/// 🔴 存在的唯一理由：自動改名要驅動一連串 SelectYesno（其中「要儲存目前的形象嗎？」必須按「否」），
/// YesAlready 若在旁邊自動點確認框，會破壞這個確定性的順序。所以改名序列期間把它暫停、序列一結束就還原。
/// <para>🔴 只在「原本是開的」時才關、才還原（<see cref="suppressed"/> 記著這件事），
/// 絕不把使用者本來就關著的 YesAlready 打開。</para>
/// <para>⚠️ YesAlready 沒裝／IPC 不在＝沒有 race，一律當沒事跳過（吞 <see cref="IpcError"/>）。
/// EzIPC 用 InternalName「YesAlready」當前綴，所以 gate 名是「YesAlready.*」
/// （對照 AutoRetainer ECommons.IPC 的 <c>YesAlreadyIPC</c>：<c>SetPluginEnabled</c>／<c>IsPluginEnabled</c>）。</para>
/// </remarks>
internal static class YesAlreadyIpc
{
    // 建 subscriber 本身零成本；真正的探測發生在 Invoke 時。
    private static readonly Lazy<ICallGateSubscriber<bool>> IsEnabledGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<bool>("YesAlready.IsPluginEnabled"));

    // SetPluginEnabled 是 Action<bool>；消費端用 <bool, object> 再走 InvokeAction。
    private static readonly Lazy<ICallGateSubscriber<bool, object>> SetEnabledGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<bool, object>("YesAlready.SetPluginEnabled"));

    /// <summary>true＝是我們把它關掉的，收尾時要還原成開。</summary>
    private static bool suppressed;

    /// <summary>目前是否處於「被我們暫停」狀態。</summary>
    public static bool IsSuppressed => suppressed;

    /// <summary>
    /// 問 YesAlready 現在開著沒。<c>null</c>＝沒裝／IPC 不在／問不到。
    /// </summary>
    /// <remarks>
    /// 📌 純查詢，<b>不會去改它的狀態</b>——給「要提醒使用者 YesAlready 可能會接手某個對話框」用。
    /// ⚠️ 問得到的只有「這個外掛整體開著沒」，<b>問不到它個別功能的開關</b>
    /// （那些只存在它自己的設定檔裡，沒有 IPC）。
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

    /// <summary>把 YesAlready 暫停（只在它原本開著時）。沒裝／IPC 不在＝no-op。</summary>
    public static void Suppress()
    {
        if (suppressed) return;
        try
        {
            if (!IsEnabledGate.Value.InvokeFunc()) return; // 本來就關著：不動它，也不記成「我們關的」
            SetEnabledGate.Value.InvokeAction(false);
            suppressed = true;
            Svc.Log.Information("[RetainerBatchRename] 已暫停 YesAlready（改名序列期間），結束後會還原。");
        }
        catch (IpcError)
        {
            // 沒裝／版本不合／gate 不存在：沒有 race，跳過。
        }
        catch (Exception ex)
        {
            Svc.Log.Information($"[RetainerBatchRename] 暫停 YesAlready 失敗（不影響改名）：{ex.Message}");
        }
    }

    /// <summary>還原 YesAlready（只在剛剛是我們關的時候）。任何收尾／中止路徑都要呼叫，冪等。</summary>
    public static void Restore()
    {
        if (!suppressed) return;
        try
        {
            SetEnabledGate.Value.InvokeAction(true);
            Svc.Log.Information("[RetainerBatchRename] 已還原 YesAlready。");
        }
        catch (IpcError)
        {
            // 對方在序列途中被關掉了：無從還原，但也不再有 race。
        }
        catch (Exception ex)
        {
            Svc.Log.Information($"[RetainerBatchRename] 還原 YesAlready 失敗：{ex.Message}");
        }
        finally
        {
            suppressed = false;
        }
    }
}

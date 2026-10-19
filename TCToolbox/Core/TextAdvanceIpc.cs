using System;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;

namespace TCToolbox.Core;

/// <summary>TextAdvance 的唯讀查詢：它現在會不會自己處理交納視窗（<c>Request</c>）。只問，不改它任何狀態。</summary>
/// <remarks>
/// 契約：端點名與型別逐字對應 TextAdvance 的 <c>IPCProvider</c>（三支都是無參數、回 <c>bool</c>）。只在主執行緒呼叫。
/// </remarks>
internal static class TextAdvanceIpc
{
    /// <summary><see cref="QueryRequestHandling"/>／<see cref="QueryQuestAcceptHandling"/> 的結果。</summary>
    public enum RequestHandling
    {
        /// <summary>確認不了（它載入了但問不到，或問不到它有沒有載入）。零值刻意給這個。</summary>
        Unknown = 0,

        /// <summary>TextAdvance 沒有載入。</summary>
        NotLoaded,

        /// <summary>載入了，但現在不會碰交納視窗：沒啟用，或自動填格與自動交出都關著。</summary>
        Idle,

        /// <summary>啟用中，而且自動填格或自動交出至少開著一項。</summary>
        Active,
    }

    private static readonly Lazy<ICallGateSubscriber<bool>> IsEnabledGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<bool>("TextAdvance.IsEnabled"));

    private static readonly Lazy<ICallGateSubscriber<bool>> RequestHandinGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<bool>("TextAdvance.GetEnableRequestHandin"));

    private static readonly Lazy<ICallGateSubscriber<bool>> RequestFillGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<bool>("TextAdvance.GetEnableRequestFill"));

    private static readonly Lazy<ICallGateSubscriber<bool>> QuestAcceptGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<bool>("TextAdvance.GetEnableQuestAccept"));

    /// <summary>問 TextAdvance 現在會不會自己按任務受理視窗（<c>JournalAccept</c>）的「接受」。</summary>
    /// <remarks>與 <see cref="QueryRequestHandling"/> 同一套判準：被別的插件暫停照樣算 <see cref="RequestHandling.Active"/>。</remarks>
    public static RequestHandling QueryQuestAcceptHandling(out string detail)
    {
        try
        {
            if (!IsEnabledGate.Value.InvokeFunc())
            {
                detail = "IsEnabled=False";
                return RequestHandling.Idle;
            }

            var accept = QuestAcceptGate.Value.InvokeFunc();
            detail = $"IsEnabled=True、GetEnableQuestAccept={accept}";
            return accept ? RequestHandling.Active : RequestHandling.Idle;
        }
        catch (IpcNotReadyError)
        {
            return NotReady(out detail);
        }
        catch (Exception ex)
        {
            detail = $"查詢失敗（{ex.GetType().Name}）：{ex.Message}";
            return RequestHandling.Unknown;
        }
    }

    /// <summary>端點不在：TextAdvance 沒載入才算沒事；載入了卻沒有端點就當成確認不了。</summary>
    private static RequestHandling NotReady(out string detail)
    {
        if (PluginPresence.IsLoaded("TextAdvance") == false)
        {
            detail = "TextAdvance 沒有載入";
            return RequestHandling.NotLoaded;
        }

        detail = "TextAdvance 已載入（或問不到它有沒有載入），但查詢端點不在";
        return RequestHandling.Unknown;
    }

    /// <summary>問 TextAdvance 現在會不會自己填交納視窗或按「交出」。</summary>
    /// <remarks>
    /// 刻意不看 <c>TextAdvance.IsPaused</c>：那是別的插件暫時鎖住它，隨時會放開，放開的那一刻它就會動手，
    /// 所以「啟用但被鎖住」照樣算 <see cref="RequestHandling.Active"/>。
    /// </remarks>
    /// <param name="detail">這個答案是怎麼來的（寫記錄用），永不為 null。</param>
    public static RequestHandling QueryRequestHandling(out string detail)
    {
        try
        {
            if (!IsEnabledGate.Value.InvokeFunc())
            {
                detail = "IsEnabled=False";
                return RequestHandling.Idle;
            }

            var handin = RequestHandinGate.Value.InvokeFunc();
            var fill = RequestFillGate.Value.InvokeFunc();
            detail = $"IsEnabled=True、GetEnableRequestHandin={handin}、GetEnableRequestFill={fill}";
            return handin || fill ? RequestHandling.Active : RequestHandling.Idle;
        }
        catch (IpcNotReadyError)
        {
            return NotReady(out detail);
        }
        catch (Exception ex)
        {
            detail = $"查詢失敗（{ex.GetType().Name}）：{ex.Message}";
            return RequestHandling.Unknown;
        }
    }
}

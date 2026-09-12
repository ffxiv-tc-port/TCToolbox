using System;
using System.Numerics;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;

namespace TCToolbox.Core;

/// <summary>
/// Questionable 的<b>唯讀</b> IPC 呼叫端包裝（只問「現在跑到哪一步」，不下任何指令）。
/// </summary>
/// <remarks>
/// 🔴 <b>本外掛只呼叫這兩支。</b>同一個 provider 還註冊了 <c>StartQuest</c>／<c>Stop</c>／
/// <c>ImportQuestPriority</c> 等等會<b>改變對方行為</b>的端點，一支都不碰。
/// 🔴🔴 <b><see cref="TryGetCurrentStep"/> 只能在框架執行緒上呼叫。</b>
/// </remarks>
internal static class QuestionableIpc
{
    /// <summary>非預期例外的記錄節流間隔（毫秒）。</summary>
    private const int ErrorLogIntervalMs = 60_000;

    /// <summary>
    /// Questionable <c>QuestionableIpc.StepData</c> 的鏡像型別，<b>只靠 JSON 成員名對應</b>。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>這些名字是對外契約，不是命名風格。</b><c>QuestId</c>／<c>Sequence</c>／<c>Step</c>／
    /// <c>InteractionType</c>／<c>Position</c>／<c>TerritoryId</c> 全部照抄對方，
    /// 改了不會編譯失敗，只會讓那個欄位永遠是預設值。
    /// </remarks>
    public sealed class StepData
    {
        /// <summary>任務 id 的字串形式（例如 <c>"1234"</c>）。</summary>
        public string QuestId { get; set; } = string.Empty;

        public byte Sequence { get; set; }

        public int Step { get; set; }

        /// <summary>對方 <c>EInteractionType</c> 的 <c>ToString()</c>（英文列舉名，例如 <c>Interact</c>）。</summary>
        public string InteractionType { get; set; } = string.Empty;

        /// <summary>這一步的目標<b>世界座標</b>；<c>null</c>＝這一步沒有座標（例如換職業、等待）。</summary>
        public Vector3? Position { get; set; }

        /// <summary>這一步所在區域的 <c>TerritoryType</c> 列號。</summary>
        public uint TerritoryId { get; set; }
    }

    private static readonly Lazy<ICallGateSubscriber<bool>> IsRunningGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<bool>("Questionable.IsRunning"));

    private static readonly Lazy<ICallGateSubscriber<StepData?>> GetCurrentStepGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<StepData?>("Questionable.GetCurrentStepData"));

    /// <summary>Questionable 在不在，以及它正不正在跑。</summary>
    /// <returns><see langword="false"/>＝沒裝或還沒註冊 IPC（<paramref name="running"/> 為 false）。</returns>
    public static bool TryIsRunning(out bool running)
    {
        running = false;

        try
        {
            running = IsRunningGate.Value.InvokeFunc();
            return true;
        }
        catch (IpcError)
        {
            return false;
        }
        catch (Exception ex)
        {
            LogUnexpected("Questionable.IsRunning", ex);
            return false;
        }
    }

    /// <summary>
    /// 取目前這一步。<b>只能在框架執行緒上呼叫</b>（理由見類別註解）。
    /// </summary>
    /// <param name="step">
    /// 目前這一步；<c>null</c>＝<b>Questionable 在，但現在沒有任何進行中的步驟</b>。
    /// </param>
    /// <returns>
    /// IPC 打得通才回 <see langword="true"/>。
    /// 🔑 <b>「打不通」與「打得通但沒有步驟」是兩件事</b>，呼叫端要分開處置：
    /// 前者該顯示「未偵測到 Questionable」，後者該顯示「目前沒有在跑任務」。
    /// 合併成同一個 false 的話，使用者永遠分不出是外掛沒裝還是自己沒開始跑。
    /// </returns>
    public static bool TryGetCurrentStep(out StepData? step)
    {
        step = null;

        try
        {
            step = GetCurrentStepGate.Value.InvokeFunc();
            return true;
        }
        catch (IpcError ex)
        {
            // ⚠️ 分兩種意思：沒註冊（正常）與型別轉換爆掉（值得知道）。
            //    後者代表對方的 StepData 形狀變了，而那正是本檔最容易靜默壞掉的地方。
            if (ex is not IpcNotReadyError)
                LogUnexpected("Questionable.GetCurrentStepData（鏡像型別可能已對不上）", ex);

            return false;
        }
        catch (Exception ex)
        {
            LogUnexpected("Questionable.GetCurrentStepData", ex);
            return false;
        }
    }

    private static void LogUnexpected(string endpoint, Exception ex)
    {
        if (!Throttle.Pass($"TCToolbox.QuestionableIpc.Error.{endpoint}", ErrorLogIntervalMs)) return;

        // 🔴 Information 級：使用者跑 LogLevel 1，盲區只有 Verbose；Debug 收得到但單檔數十萬行會淹沒。
        Svc.Log.Information(
            $"[QuestionableIpc] 呼叫 {endpoint} 時發生非預期例外（{ErrorLogIntervalMs / 1000} 秒內只報一次）：{ex}");
    }
}

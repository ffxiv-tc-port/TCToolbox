using System;
using System.Numerics;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;

namespace TCToolbox.Core;

/// <summary>
/// Questionable 的<b>唯讀</b> IPC 呼叫端包裝（只問「現在跑到哪一步」，不下任何指令）。
/// </summary>
/// <remarks>
/// <para>
/// 對方的契約（2026-09-08 逐字對照 <c>Questionable/External/QuestionableIpc.cs</c>）：
/// <list type="bullet">
/// <item><c>Questionable.IsRunning() -&gt; bool</c></item>
/// <item><c>Questionable.GetCurrentStepData() -&gt; StepData?</c></item>
/// </list>
/// 🔴 <b>本外掛只呼叫這兩支。</b>同一個 provider 還註冊了 <c>StartQuest</c>／<c>Stop</c>／
/// <c>ImportQuestPriority</c> 等等會<b>改變對方行為</b>的端點，一支都不碰。
/// </para>
///
/// <para>
/// 🔴🔴 <b>為什麼要自己做一個鏡像型別</b>：<c>StepData</c> 雖然是 <c>public</c>，
/// 但它住在 Questionable 的組件裡，跨 AssemblyLoadContext 具名不到。
/// 走得通的理由在 Dalamud 本體：<c>CallGateChannel.InvokeFunc&lt;TRet&gt;</c> 在
/// <c>typeof(TRet) != methodInfo.ReturnType</c> 時會用 Newtonsoft 把結果 JSON 來回轉一次
/// ⇒ 只要<b>成員名字對得上</b>，用自己宣告的型別接就成立。
/// </para>
/// <para>
/// 🔴 <b>對不上的失敗形式是靜默的</b>：JSON 裡沒有對應鍵的成員留在預設值
/// （<c>InteractionType</c> 變空字串、<c>TerritoryId</c> 變 0、<c>Position</c> 變 null），
/// <b>不擲例外也不寫記錄</b>。<see cref="StepData"/> 的每一個名字都必須逐字等於對方的成員名。
/// </para>
///
/// <para>
/// 📌 <b>這裡沒有踩到「<c>T?</c> 宣告成 <c>T</c>」那個坑</b>：<c>StepData</c> 是<b>參考型別</b>，
/// 對方回 null 時 <c>ConvertObject</c> 立刻回 null、最後的 <c>(TRet)result</c> 轉的是 null 參考，
/// 不會像可空<b>值</b>型別那樣擲一個看起來與 IPC 毫無關係的 <c>NullReferenceException</c>。
/// ⚠️ 但 <see cref="StepData.Position"/> 本身是 <c>Vector3?</c>，那是 JSON 內部的事，
/// 由 Newtonsoft 處理，與上面那條無關——<b>不要「順手」把它改成非可空的 <c>Vector3</c></b>，
/// 那會讓「這一步沒有座標」變成一個看起來很正常的原點。
/// </para>
///
/// <para>
/// 🔴🔴 <b><see cref="TryGetCurrentStep"/> 只能在框架執行緒上呼叫。</b>
/// 對方是用自己的 <c>IpcFrameworkGate</c> 包起來的（<c>_gate.Get(…)</c>），
/// 那條路徑在非框架執行緒上會排進下一個 tick 再等它。從<b>繪製路徑</b>呼叫就是在主執行緒上
/// 等一個要靠主執行緒才跑得到的 tick，表現成「整個遊戲卡住幾秒」而不是任何錯誤訊息。
/// </para>
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
    /// <para>
    /// 📌 對方那邊是 <c>required … { get; init; }</c>，這裡刻意用<b>可寫屬性 ＋ 無參數建構子</b>：
    /// Newtonsoft 對這個形狀的反序列化路徑最單純，不必去賭它認不認得 init-only 與 required。
    /// </para>
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

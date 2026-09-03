using System;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;

namespace TCToolbox.Core;

/// <summary>
/// 單向橋接到「塔塔露誇獎」(TataruPraise)：發生值得出聲的事件時請它念一句。
/// </summary>
/// <remarks>
/// 🔴 <b>零組件相依。</b>只用 Dalamud 原生 CallGate 的字串契約，對方沒安裝時本檔的每一條路徑都是安靜的 no-op。
/// <para>
/// 🔴 契約名與情境名逐字取自 TataruPraise 的 <c>IpcContract.cs</c> 與 <c>Core/PraiseCategory.cs</c>。
/// CallGate 是純字串比對，名字打錯不會有任何錯誤訊息，只會永遠得到「這個頻道沒有人註冊」——<b>靜默斷線</b>；
/// 情境名打錯則是對方那邊「未知情境」永遠回 <c>false</c>。所以字串一律常數化，不散在呼叫點上。
/// </para>
/// <para>
/// 🔴 <b>只能從主執行緒（Framework tick／Draw）呼叫。</b>IPC 的實作是在<b>呼叫端的執行緒</b>上跑的，
/// 從背景 Task 叫過去等於把對方的程式碼拉到背景執行緒。
/// </para>
/// <para>
/// ⚠️ 這是<b>單向通知</b>：回傳值只拿來寫記錄，不影響 TC Toolbox 的任何流程，
/// 也不因為對方回 <c>false</c> 而重試。呼叫端自己的冷卻／去重就是唯一的節流。
/// </para>
/// </remarks>
internal static class TataruPraiseIpc
{
    /// <summary><c>Func&lt;bool&gt;</c>：總開關開著而且真的有可播的內容。</summary>
    private const string TagIsAvailable = "TataruPraise.IsAvailable";

    /// <summary><c>Func&lt;string, bool&gt;</c>：從指定情境的誇獎池挑一句念。</summary>
    private const string TagPraise = "TataruPraise.Praise";

    /// <summary>
    /// 情境「玩家警示」——附近出現了要注意的玩家。
    /// </summary>
    /// <remarks>
    /// ⚠️ TataruPraise 拿這個字串當 <c>pool.json</c> 的鍵（它的 <c>PraiseCategory.PlayerAlert</c>），
    /// <b>對不上就靜默不出聲</b>（它端會寫一行 Information 說收到未知情境）。
    /// </remarks>
    internal const string CategoryPlayerAlert = "玩家警示";

    // 建 subscriber 本身零成本；真正的探測發生在 InvokeFunc()：對方沒註冊同名端點就丟 IpcNotReadyError。
    private static readonly Lazy<ICallGateSubscriber<bool>> IsAvailableGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<bool>(TagIsAvailable));

    private static readonly Lazy<ICallGateSubscriber<string, bool>> PraiseGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<string, bool>(TagPraise));

    /// <summary>
    /// 請塔塔露就指定情境念一句。對方沒裝、關著、或池裡沒已合成的句子，這裡都是安靜的 no-op。
    /// </summary>
    /// <param name="category">情境名，必須逐字對上 TataruPraise 的池鍵（用本類別的常數）。</param>
    /// <param name="reason">寫進記錄用的來源描述，讓 log 分得出是哪個模組哪一條規則叫的。</param>
    internal static void TryPraise(string category, string reason)
    {
        try
        {
            // 先問一次：對方的總開關關著、或池裡一句已合成的都沒有，就不要浪費它的冷卻。
            if (!IsAvailableGate.Value.InvokeFunc()) return;

            var accepted = PraiseGate.Value.InvokeFunc(category);
            // Information 級：這是「使用者說沒出聲」時唯一問得出真相的一行（使用者跑 LogLevel 1）。
            Svc.Log.Information($"[TataruPraise] {reason}：Praise(「{category}」) 回傳 {accepted}。");
        }
        catch (IpcNotReadyError)
        {
            // 對方沒安裝／沒載入。這是完全正常的狀態，刻意不寫 log——沒裝的人每次命中都會走到這裡。
        }
        catch (Exception ex)
        {
            Svc.Log.Information($"[TataruPraise] 呼叫失敗（{reason}）：{ex.Message}");
        }
    }
}

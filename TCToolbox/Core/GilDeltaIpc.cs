using System;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;

namespace TCToolbox.Core;

/// <summary>
/// 單向橋接到 GilDelta：本外掛做了一件會動到錢包的事情時，先跟它說一聲。
/// </summary>
/// <remarks>
/// 🔴 <b>零組件相依。</b>只用 Dalamud 原生 CallGate 的字串契約；對方沒安裝時本檔的每一條路徑
/// 都是安靜的 no-op（<c>IpcNotReadyError</c> 直接吞掉、不寫記錄——沒裝的人每次按都會走到那裡）。
/// <para>
/// 📌 <b>這是「提示」不是「記帳」。</b>GilDelta 自己會從錢包變動去推分類（<c>NpcShopBuyRule</c>
/// 看的是「Self 錢包減少 ＋ Shop／InclusionShop 開著」），推得出來的它本來就推得出來。
/// 這裡多給的是<b>歸屬</b>：Dalamud 不會告訴對方是誰呼叫的，<c>note</c> 是唯一能留下
/// 「這筆是 TC Toolbox 弄出來的」的地方（GilDelta 的 <c>GilDeltaIpc</c> 註解逐字這麼寫）。
/// </para>
/// <para>
/// 🔴 <c>ttlMs</c> 是提示的有效期，對方會夾到它自己的 <c>GilHintStore.MaxTtlMs</c>。
/// 送出提示與錢包真的變動之間隔著<b>使用者按確認</b>那段時間，所以不能給太短。
/// </para>
/// <para>
/// ⚠️ 對方的 <c>Submit</c> 只碰 concurrent 集合，自稱任何執行緒都能叫；
/// 即使如此本檔的呼叫點仍然都在主執行緒（ImGui 繪製回呼／framework tick）上。
/// </para>
/// </remarks>
internal static class GilDeltaIpc
{
    /// <summary><c>Func&lt;string, string, int, bool&gt;</c>：category／note／ttlMs → 有沒有被收下。</summary>
    private const string TagHint = "GilDelta.Hint";

    /// <summary>
    /// 分類名：向 NPC 商店買東西。
    /// </summary>
    /// <remarks>
    /// 🔴 逐字取自 GilDelta 的 <c>GilEventCategory</c> 列舉成員名（它用 <c>Enum.TryParse</c> 解，
    /// 大小寫不拘但拼字要對）。打錯的話對方會回 <c>false</c> 並在它那邊寫一行「未知的分類提示」，
    /// <b>不會擲例外</b>——所以拼錯的失敗形式是安靜地什麼都沒發生。
    /// <para>
    /// ⚠️ 這個分類在對方那邊被限定成「<b>Self 錢包</b>而且金額<b>減少</b>」
    /// （<c>HintScope</c> 的 <c>DirectionMatches</c>）：拿它去標僱員錢包或收入是對不上的。
    /// </para>
    /// </remarks>
    internal const string CategoryNpcShopBuy = "NpcShopBuy";

    /// <summary>本外掛送出的 note 一律用這個前綴，方便對方的記錄一眼看出來源。</summary>
    private const string NotePrefix = "TCToolbox:";

    // 建 subscriber 本身零成本；真正的探測發生在 InvokeFunc()：對方沒註冊同名端點就丟 IpcNotReadyError。
    private static readonly Lazy<ICallGateSubscriber<string, string, int, bool>> HintGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<string, string, int, bool>(TagHint));

    /// <summary>
    /// 告訴 GilDelta「接下來那筆錢包變動是我造成的」。對方沒裝就是安靜的無操作。
    /// </summary>
    /// <param name="category">分類名，用本類別的常數。</param>
    /// <param name="detail">要寫進 note 的細節（會自動加上 <c>TCToolbox:</c> 前綴）。</param>
    /// <param name="ttlMs">提示有效期（毫秒）。要蓋過「使用者按確認」那段時間。</param>
    internal static void TryHint(string category, string detail, int ttlMs)
    {
        try
        {
            var accepted = HintGate.Value.InvokeFunc(category, NotePrefix + detail, ttlMs);

            // Information 級、每次呼叫一行：這些呼叫點都是使用者按一次才發生一次的離散動作
            // （不是每幀），所以不會洗版；而「歸因為什麼沒生效」事後只能從這一行回推。
            Svc.Log.Information(
                $"[GilDelta] 已送出提示 {category}／{detail}（ttl {ttlMs}ms），對方回傳 {accepted}。");
        }
        catch (IpcNotReadyError)
        {
            // 沒安裝 GilDelta。完全正常的狀態，刻意不寫記錄——沒裝的人每次按都會走到這裡。
        }
        catch (Exception ex)
        {
            Svc.Log.Information($"[GilDelta] 送出提示失敗（{category}／{detail}）：{ex.Message}");
        }
    }
}

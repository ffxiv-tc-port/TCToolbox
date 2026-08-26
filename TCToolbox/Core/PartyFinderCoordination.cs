using System;

namespace TCToolbox.Core;

/// <summary>
/// 招募板相關模組之間的最小協調點。
/// </summary>
/// <remarks>
/// <para>
/// 存在的唯一理由是一個<b>批次互動</b>：<see cref="Modules.NoAutoClosePartyFinder"/> 在隊員變動時會把
/// 招募詳細視窗<b>關掉再用新資料重開</b>（<c>OpenListing</c>），而那次重開會觸發
/// <c>LookingForGroupDetail</c> 的 <c>PostSetup</c>——正是 <see cref="Modules.AutoJoinPartyFinder"/>
/// 用來判斷「使用者剛點開一則招募」的事件。兩個模組同時開著時，<b>別人加入／離開造成的刷新</b>會被
/// AutoJoin 誤當成使用者主動開詳細而<b>自動加入</b>。
/// </para>
/// <para>
/// 作法：NoAutoClose 在它<b>程式重開</b>之前呼叫 <see cref="MarkProgrammaticReopen"/>；AutoJoin 在
/// <c>PostSetup</c> 時呼叫 <see cref="ConsumeProgrammaticReopen"/>，若這次開啟是程式重開就跳過自動加入。
/// </para>
/// <para>
/// 🔑 <b>用「一次性消費＋短效期」而不是時間窗過濾</b>：只讓「緊接在重開之後的那一次」開啟被跳過，
/// 不會連帶把使用者在幾秒內自己開的<b>別</b>則招募也擋掉。短效期（<see cref="WindowMs"/>）只是保險絲，
/// 防止旗標在 <c>PostSetup</c> 因故沒到時無限期殘留（殘留會讓下一次使用者主動開詳細被錯誤跳過）。
/// </para>
/// <para>📌 只在主執行緒（框架更新／addon 事件）使用，不需要同步。兩個模組任一沒開都無害。</para>
/// </remarks>
public static class PartyFinderCoordination
{
    /// <summary>旗標的最長有效期（毫秒）。超過就當作沒有程式重開在等待。</summary>
    private const int WindowMs = 2000;

    private static long programmaticReopenTick;

    /// <summary>標記「接下來這一次招募詳細視窗的開啟是程式重開，不是使用者主動點的」。</summary>
    public static void MarkProgrammaticReopen() => programmaticReopenTick = Environment.TickCount64;

    /// <summary>
    /// 消費旗標：若最近確實有一次程式重開在等待，回 <see langword="true"/> 並清掉（一次性）。
    /// </summary>
    /// <remarks>不論結果如何都會清掉旗標，確保一次標記最多只跳過一次開啟。</remarks>
    public static bool ConsumeProgrammaticReopen()
    {
        if (programmaticReopenTick == 0) return false;

        var recent = Environment.TickCount64 - programmaticReopenTick < WindowMs;
        programmaticReopenTick = 0;
        return recent;
    }
}

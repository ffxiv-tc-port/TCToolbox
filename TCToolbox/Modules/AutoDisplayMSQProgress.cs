using System;
using System.Linq;
using System.Text;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 顯示主線任務進度：在冒險筆記的劇情任務清單（<c>ScenarioTree</c>）標題列，補上「目前這個資料片的
/// 主線還剩幾個、已完成百分之幾」。<b>唯讀顯示</b>，不改動任何任務資料、不解鎖、不接受任務。
/// 機制：訂閱 <c>ScenarioTree</c> 的 PostRefresh，重新計算後把文字寫進該 addon 的標題欄再請它自我
/// 重繪；不 hook、不寫遊戲記憶體、不做 patch。
/// 參考 DailyRoutines AutoDisplayMSQProgress 設計重寫（API13、無 OmenTools 相依）。
/// </summary>
/// <remarks>
/// 🔴 <b>台服未離線證明的假設</b>：注入的欄位索引（<c>AtkValues[7]</c>）、按鈕節點 id（13）、
/// 其內文字節點 id（6）沿用 DR 對國服客端的觀測值。台服若不同，最壞情況只是「標題顯示錯位或沒變化」，
/// 不會崩潰——全部經界限與 null 檢查，且只有真的算出進度時才寫。下一次遊戲自己重繪就會蓋回正常內容。
/// <para>
/// 🔴 呼叫 <c>OnRefresh</c> 會再觸發一次 PostRefresh（Dalamud 掛在該 vfunc 上），
/// 用 <see cref="inRefresh"/> 這道再入旗標擋掉，避免無窮遞迴。
/// </para>
/// </remarks>
public sealed unsafe class AutoDisplayMSQProgress : TcModule
{
    public override string InternalName => "AutoDisplayMSQProgress";
    public override string DisplayName => "顯示主線任務進度";

    public override string Description =>
        "在冒險筆記（劇情任務清單）的標題列顯示目前資料片主線的剩餘任務數與完成百分比。" +
        "唯讀顯示，不改動任務資料。";

    public override ModuleCategory Category => ModuleCategory.Misc;

    private const string AddonName = "ScenarioTree";

    /// <summary>主線劇情任務的日誌類別圖示（台服 7.20 實查 <c>JournalGenre</c> 14 列皆為此值）。</summary>
    private const uint MsqGenreIcon = 61412;

    /// <summary>我們自己觸發 <c>OnRefresh</c> 時設 true，擋掉遞迴的 PostRefresh 回呼。</summary>
    private bool inRefresh;

    /// <summary>節流器的鍵。⚠️ Throttle 的鍵是全域持久的，停用→重啟用要自己清掉殘留冷卻。</summary>
    private const string ComputeThrottleKey = "AutoDisplayMSQProgress-Compute";

    /// <summary>上一次算出來的進度字串；<c>null</c>＝還沒算出過／上次算不出來。</summary>
    private string? cachedText;

    protected override void OnEnable()
    {
        // ⚠️ 停用→（1 秒內）重啟用時，上一輪殘留的冷卻會把下面這次補打整個吃掉，
        //    表現成「重新啟用之後標題沒有變化」。鍵是全域持久的，所以要自己清。
        Throttle.Reset(ComputeThrottleKey);
        cachedText = null;

        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostRefresh, AddonName, OnAddonRefresh);
        if (UiHelper.IsAddonReady(AddonName))
            OnAddonRefresh(AddonEvent.PostRefresh, null!);
    }

    protected override void OnDisable()
    {
        Svc.AddonLifecycle.UnregisterListener(OnAddonRefresh);
        cachedText = null;
    }

    /// <summary>
    /// ScenarioTree 每次重繪都要把進度重新注入回去。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>節流只能包住「計算」，不能包住「注入」。</b>這裡的注入是覆蓋遊戲自己的標題文字，
    /// 而<b>下一次遊戲自己重繪就會蓋回正常內容</b>（見類別註解）——也就是說「最後一次 PostRefresh」
    /// 才是有效狀態。若把整個處理常式節流掉，1 秒內連續 refresh（開冒險筆記、任務進度變動時很常見）
    /// 的第二次就會把我們的文字蓋掉，而那次事件被節流吃掉、又沒有輪詢或重試路徑
    /// ⇒ 進度顯示消失／過期，直到下一次撐過節流的 refresh 為止。
    /// ⇒ 每次 PostRefresh 都無條件重新注入（注入本身很便宜），只有昂貴的
    /// <see cref="TryComputeProgress"/>（掃整張 Quest 表）走節流＋快取。
    /// </remarks>
    private void OnAddonRefresh(AddonEvent type, AddonArgs args)
    {
        if (inRefresh) return;

        try
        {
            var addon = UiHelper.GetAddon(AddonName);
            if (addon == null || !UiHelper.IsReady(addon)) return;

            // 快取還沒建立時一定要算（Throttle.Pass 首次必放行，之後每秒最多一次）。
            if (cachedText == null || Throttle.Pass(ComputeThrottleKey, 1000))
                cachedText = ComputeText();

            if (cachedText == null) return;

            InjectText(addon, cachedText);
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, $"[{InternalName}] 計算或注入主線進度失敗");
        }
    }

    /// <summary>算出要顯示的字串；算不出來（資料還沒就緒／不在主線）回 <c>null</c>。</summary>
    private static string? ComputeText()
    {
        if (!TryComputeProgress(out var remaining, out var percentComplete, out var firstIncompleteQuest))
            return null;
        if (remaining <= 0 || percentComplete <= 0f) return null;

        var quest = Svc.Data.GetExcelSheet<Quest>().GetRowOrDefault(firstIncompleteQuest);
        if (quest == null) return null;

        var questName = quest.Value.Name.ExtractText();
        if (string.IsNullOrEmpty(questName)) return null;

        return $"{questName} ({remaining} / {percentComplete:F1}%)";
    }

    /// <summary>把文字寫進 ScenarioTree 標題欄，並請它自我重繪。全程界限與 null 檢查。</summary>
    private void InjectText(AtkUnitBase* addon, string text)
    {
        // SetManagedString 需要 null 結尾的 UTF-8；它會自己複製一份，所以區域緩衝離開後仍安全。
        var utf8 = Encoding.UTF8.GetBytes(text);
        var buffer = new byte[utf8.Length + 1];
        Array.Copy(utf8, buffer, utf8.Length);

        inRefresh = true;
        try
        {
            if (addon->AtkValues != null && addon->AtkValuesCount > 7)
            {
                fixed (byte* p = buffer)
                    addon->AtkValues[7].SetManagedString(p);
                addon->OnRefresh(addon->AtkValuesCount, addon->AtkValues);
            }

            var button = addon->GetComponentButtonById(13);
            if (button != null)
            {
                var node = button->UldManager.SearchNodeById(6);
                if (node != null && node->Type == NodeType.Text)
                    ((AtkTextNode*)node)->SetText(text);
            }
        }
        finally
        {
            inRefresh = false;
        }
    }

    /// <summary>算出目前資料片主線的「剩餘數／完成百分比／目前劇情任務列號」。</summary>
    private static bool TryComputeProgress(out int remaining, out float percentComplete, out uint firstIncompleteQuest)
    {
        remaining = 0;
        percentComplete = 0f;
        firstIncompleteQuest = 0;

        var uiState = UIState.Instance();
        var agent = AgentScenarioTree.Instance();
        if (uiState == null || agent == null || agent->Data == null) return false;

        var questSheet = Svc.Data.GetExcelSheet<Quest>();

        // 主線任務＝日誌類別圖示為 MSQ 那張、且有名字的任務。
        var msqQuests = questSheet
            .Where(q => q.JournalGenre.ValueNullable?.Icon == MsqGenreIcon &&
                        !string.IsNullOrEmpty(q.Name.ExtractText()))
            .ToList();
        if (msqQuests.Count == 0) return false;

        // 玩家目前進行到的資料片＝已完成的主線任務裡，所屬資料片 RowId 最大的那一個。
        var currentExpansionRowId = 0u;
        foreach (var q in msqQuests)
        {
            var expansionId = q.Expansion.RowId;
            if (expansionId <= currentExpansionRowId) continue;
            if (IsCompleted(uiState, q))
                currentExpansionRowId = expansionId;
        }

        var expansionQuests = msqQuests
            .Where(q => q.Expansion.RowId == currentExpansionRowId)
            .OrderBy(q => q.RowId)
            .ToList();
        if (expansionQuests.Count == 0) return false;

        var completed = expansionQuests.Count(q => IsCompleted(uiState, q));
        var total = currentExpansionRowId == 0 ? AdjustArrTotalCount(expansionQuests.Count) : expansionQuests.Count;
        if (total <= 0) return false;

        remaining = total - completed;
        percentComplete = completed * 100f / total;
        firstIncompleteQuest = agent->Data->CurrentScenarioQuest + 0x10000u;
        return true;
    }

    private static bool IsCompleted(UIState* uiState, Quest quest)
    {
        byte maxSeq = 0;
        foreach (var todo in quest.TodoParams)
            if (todo.ToDoCompleteSeq > maxSeq)
                maxSeq = todo.ToDoCompleteSeq;
        return uiState->IsUnlockLinkUnlockedOrQuestCompleted(quest.RowId, maxSeq);
    }

    /// <summary>
    /// ARR（資料片 0）的三個起始都市有互斥的初期任務：只有你選的那個都市那條算數，
    /// 另外兩條要扣掉。此為遊戲結構性數字，與伺服器地區無關。
    /// </summary>
    private static int AdjustArrTotalCount(int baseCount)
    {
        var playerState = PlayerState.Instance();
        var count = baseCount;
        if (playerState != null)
        {
            if (playerState->StartTown != 1) count -= 23; // 森都格里達尼亞
            if (playerState->StartTown != 2) count -= 23; // 砂都烏爾達哈
            if (playerState->StartTown != 3) count -= 24; // 海都利姆薩·羅敏薩
        }
        return count - 8;
    }
}

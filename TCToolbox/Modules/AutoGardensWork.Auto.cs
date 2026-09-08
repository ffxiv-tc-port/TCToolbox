using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Memory;
using FFXIVClientStructs.FFXIV.Client.UI;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 自動園圃作業的<b>決策層</b>：讀出每一格的狀態，依使用者的策略決定要做什麼。
/// </summary>
/// <remarks>
/// 🔴 <b>狀態讀不到記憶體裡。</b>本 pin 的 <c>FFXIVClientStructs</c> 沒有任何園圃作物欄位
/// （<c>HousingFurniture</c> 只有 Id／Stain／Position／Rotation／Index），
/// 全艦隊唯一專門追蹤作物的 Accountant 也是監看 UI 與聊天、自己存一份資料庫。
/// ⇒ <b>唯一的狀態來源是互動時遊戲顯示的那句 Talk</b>
/// （<c>custom/001/CmnDefHousingGardeningPlant_00151</c> 的第 0／7～10 列），
/// 次要來源是選單上出現了哪些選項。兩者都必須真的互動一次才拿得到。
/// <para>
/// 📌 這一層取代的是使用者原本寫在 SomethingNeedDoing 巨集裡的 Lua 決策
/// （那份腳本的分工註解寫著「TCToolbox 只做互動與選單操作，本腳本負責決策」）。
/// 逐格的<b>互動</b>仍然走既有的狀態機，一行都沒有改。
/// </para>
/// <para>
/// 🔴 <b>兩個來源必須互相同意才動手</b>：Talk 判出來要做的事，如果選單上根本沒有那個選項，
/// 一律略過。單靠其中一個的失敗形式是「按到別的選項」，而園圃的動作有一半是不可回復的。
/// </para>
/// </remarks>
public sealed unsafe partial class AutoGardensWork
{
    /// <summary>這一輪處理完之後要重種的地壟（只存 GameObjectId，不存指標）。</summary>
    private readonly List<ulong> replantQueue = [];

    /// <summary>這一輪每一格的決定，給聊天回報與 UI 用。</summary>
    private readonly List<string> autoDecisions = [];

    /// <summary>SelectYesno 沒出現時最多等多久就當作不需要確認。</summary>
    /// <remarks>
    /// 🔴 不能只靠步驟逾時兜底：<see cref="TaskQueue"/> 的逾時是<b>中止整條佇列</b>，
    /// 於是「這一格剛好不跳確認框」會把後面所有地壟一起取消。這條軟上限讓它安靜地往下走。
    /// </remarks>
    private static readonly TimeSpan DisposeConfirmWait = TimeSpan.FromSeconds(3);

    /// <summary>「自動重跑」兩輪之間的間隔下限／上限（秒）。</summary>
    /// <remarks>
    /// 🔴 下限不是美觀問題。一輪要對每一格各開一次選單，24 格跑完本來就要幾十秒；
    /// 允許把間隔設成 1 秒等於「永遠有一輪在跑」，使用者會發現自己的角色再也不能做別的事。
    /// ⚠️ 夾緊發生在<b>讀取</b>那一刻，不是寫入那一刻——舊設定檔或手改過的 JSON 帶進來的怪值
    /// （0、負數）也一樣被擋住。只夾寫入端的話，那些值會靜靜地變成「每幀跑一輪」。
    /// </remarks>
    private const int AutoLoopMinSeconds = 10;

    private const int AutoLoopMaxSeconds = 3600;

    /// <summary>模組啟用（或重新登入）之後的緩衝，避免使用者還在看設定畫面就自己動起來。</summary>
    private static readonly TimeSpan AutoLoopStartGrace = TimeSpan.FromSeconds(10);

    /// <summary>閘門評估的最短間隔（毫秒）。</summary>
    /// <remarks>
    /// 📌 為什麼需要：閘門沒過的時候<b>不會</b>把下一輪的時間往後推（否則「戰鬥了一下」就會
    /// 白白延後一整個週期），所以沒有這道節流的話，只要條件一直不成立就是每幀掃一次物件表。
    /// </remarks>
    private const int AutoLoopGateIntervalMs = 2_000;

    private string AutoLoopGateKey => $"{InternalName}-AutoLoopGate";

    /// <summary>下一輪最早可以在什麼時候開始（UTC）。<c>MinValue</c>＝還沒排過（會先吃緩衝）。</summary>
    private DateTime nextAutoLoopUtc = DateTime.MinValue;

    /// <summary>上一幀佇列是不是忙著。用來偵測「busy → idle」那個轉換。</summary>
    /// <remarks>
    /// 🔑 間隔要從<b>跑完</b>那一刻起算，而且不管上一輪是誰發起的——使用者剛手動按完
    /// 一輪收穫，自動重跑不該在下一幀就接著開一輪。這個旗標是唯一能分辨那個轉換的東西。
    /// </remarks>
    private bool loopSawQueueBusy;

    /// <summary>這一輪是自動重跑發起的（＝逐格記錄降級、不進聊天）。</summary>
    private bool loopRoundQuiet;

    /// <summary>上一次閘門擋下來的理由，只給設定畫面看（空字串＝沒被擋）。</summary>
    private string lastLoopBlockReason = string.Empty;

    // ── 狀態擷取 ────────────────────────────────────────────────────────────

    /// <summary>
    /// 從 Talk 視窗把這一格的狀態與作物讀下來。<b>必須在點掉 Talk 之前呼叫。</b>
    /// </summary>
    /// <remarks>
    /// 📌 只採信第一次讀到的那一句：Talk 是可以翻頁的，後面幾頁與作物狀態無關。
    /// 讀不出已知狀態時<b>不</b>設 <c>StateCaptured</c>，下一 tick 會再試一次
    /// （那多半代表這一幀讀到的是別的對話頁，或字串正在被改寫）。
    /// </remarks>
    private void CaptureTalkState(PatchJob job)
    {
        if (job.StateCaptured) return;

        var addon = UiHelper.GetAddon(UiHelper.TalkAddonName);
        if (!UiHelper.IsReady(addon)) return;

        var node = ((AddonTalk*)addon)->AtkTextNode228;

        // 🔴 兩層判空：節點本身，以及它的字串緩衝區。AtkTextNode 建好但 NodeText 還沒填
        //    （或正在被換掉）時 StringPtr 是空的，直接讀就是攔不到的存取違規。
        if (node == null || !node->NodeText.StringPtr.HasValue) return;

        var seString = MemoryHelper.ReadSeString(&node->NodeText);
        var text = seString.TextValue;
        if (string.IsNullOrWhiteSpace(text)) return;

        var state = ClassifyTalk(text, out var cropName);
        if (state == PatchState.Unknown) return;

        job.State = state;
        job.StateCaptured = true;

        // 🔑 作物身分優先走 item 連結：那是一個 id，完全不碰文字。
        foreach (var payload in seString.Payloads)
        {
            if (payload is not ItemPayload item || item.ItemId == 0) continue;
            job.CropItemId = item.ItemId;
            break;
        }

        // 退路：那句話沒有帶連結時，用作物名去查 id。名字本身來自遊戲的 Item 表
        // （程式碼裡沒有寫死中文），而且查出來之後仍然是拿 id 去比對。
        if (job.CropItemId == 0 && cropName.Length > 0)
            job.CropItemId = GardenCropData.CropIdByName(cropName);

        LogPerPatch(
            $"[{InternalName}] 地壟 {job.GameObjectId:X} 狀態＝{state}"
            + $"，作物 id＝{job.CropItemId}"
            + (job.CropItemId == 0 && cropName.Length > 0 ? $"（名稱「{cropName}」查不到對應道具）" : string.Empty));
    }

    /// <summary>
    /// 把 Talk 那句話分類。<paramref name="cropName"/> 是句首那段作物名（空地壟時為空字串）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 表裡存的只有「句尾那段固定文字」，句首的作物名是一個執行期才展開的 item 巨集
    /// ⇒ 這裡一律比對<b>句尾</b>，作物名取它前面那一段。
    /// 四句話彼此沒有子字串關係（2026-09-08 直讀台服 sqpack 確認），所以判斷順序不影響結果；
    /// 仍然把「枯萎」放第一個，因為那是唯一不可回復的分支。
    /// </remarks>
    private PatchState ClassifyTalk(string text, out string cropName)
    {
        cropName = string.Empty;

        if (text.Contains(textPlotEmpty, StringComparison.Ordinal))
            return PatchState.Empty;

        if (TrySplit(text, textStatusDead, out cropName)) return PatchState.Withered;
        if (TrySplit(text, textStatusRipe, out cropName)) return PatchState.Ripe;
        if (TrySplit(text, textStatusDepressed, out cropName)) return PatchState.NeedsCare;
        if (TrySplit(text, textStatusVigorous, out cropName)) return PatchState.Growing;

        return PatchState.Unknown;
    }

    private static bool TrySplit(string text, string suffix, out string head)
    {
        head = string.Empty;
        if (string.IsNullOrEmpty(suffix)) return false;

        var index = text.IndexOf(suffix, StringComparison.Ordinal);
        if (index < 0) return false;

        head = text[..index].Trim();
        return true;
    }

    // ── 決策 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 依策略決定這一格要做什麼；回 <see langword="false"/>＝什麼都不做。
    /// </summary>
    /// <param name="job">這一格的工作狀態（狀態與作物已由 <see cref="CaptureTalkState"/> 填好）。</param>
    /// <param name="entries">選單上目前的選項（不含取消）。</param>
    private bool ResolveAutoAction(PatchJob job, List<string> entries)
    {
        var targetCrop = GardenCropData.CropOfSeed(Config.SeedItemId);

        var canPlant = Config.SeedItemId != 0 && Config.SoilItemId != 0
                       && FindInventoryItem(Config.SeedItemId) != null
                       && FindInventoryItem(Config.SoilItemId) != null;
        var canFertilize = Config.FertilizerItemId != 0 && FindInventoryItem(Config.FertilizerItemId) != null;

        var decision = GardenDecisionMaker.Decide(
            job.State, job.CropItemId, targetCrop,
            Config.TargetMaturePolicy, Config.OtherMaturePolicy,
            Config.FertilizeMode, Config.WitheredMode,
            Config.TendWhenNeeded, Config.PlantWhenEmpty,
            canPlant, canFertilize);

        job.Reason = decision.Reason;

        if (decision.Action is not { } action)
        {
            RecordDecision(job, null);
            return false;
        }

        var chosen = action switch
        {
            AutoGardenAction.Harvest => GardenAction.Harvest,
            AutoGardenAction.Tend => GardenAction.Tend,
            AutoGardenAction.Fertilize => GardenAction.Fertilize,
            AutoGardenAction.Plant => GardenAction.Plant,
            _ => GardenAction.Dispose,
        };

        // 🔴 兩個來源要互相同意：Talk 說可以做的事，選單上必須真的有那個選項。
        //    對不起來就不動——那代表我對這一格的判讀是錯的，而不是遊戲少給了一個選項。
        var optionText = ActionText(chosen);
        if (!entries.Any(x => x.Contains(optionText, StringComparison.Ordinal)))
        {
            job.Reason = $"{decision.Reason}；但選單上沒有「{optionText}」，判讀可能有誤，本次不動";
            RecordDecision(job, null);
            return false;
        }

        job.Chosen = chosen;
        job.Replant = decision.Replant;
        RecordDecision(job, optionText);
        return true;
    }

    private void RecordDecision(PatchJob job, string? optionText)
    {
        var line = optionText == null
            ? $"略過：{job.Reason}"
            : $"{optionText}：{job.Reason}" + (job.Replant ? "（稍後重種）" : string.Empty);

        autoDecisions.Add(line);
        LogPerPatch($"[{InternalName}] 地壟 {job.GameObjectId:X} → {line}");

        if (Config.AnnounceEachDecision)
            Svc.Chat.Print($"[TC Toolbox] 園圃：{line}");
    }

    // ── 「處理」（清除枯萎作物）的後續步驟 ──────────────────────────────────

    /// <summary>
    /// 「處理」選下去之後會跳一個確認框（表的第 11 列「確定要處理掉作物嗎？」），按是。
    /// </summary>
    /// <remarks>
    /// 🔴 這一步<b>永遠不會讓整批停下來</b>：確認框沒出現時等一小段時間就往下走，
    /// 而不是讓 <see cref="TaskQueue"/> 的步驟逾時把整條佇列中止（那會把後面所有地壟一起取消）。
    /// </remarks>
    private void EnqueueDisposeSteps(PatchJob job)
    {
        var confirmed = false;
        DateTime? waitUntil = null;

        queue.Enqueue("確認處理", () =>
        {
            if (job.Skipped || job.Chosen != GardenAction.Dispose) return true;

            waitUntil ??= DateTime.UtcNow + DisposeConfirmWait;

            if (UiHelper.IsAddonReady(UiHelper.SelectYesnoAddonName))
            {
                confirmed = true;
                if (Throttle.Pass("AutoGardensWork-DisposeYes", 300))
                    UiHelper.ClickSelectYesnoYes();
                return false;
            }

            // 已經按過而且框收起來了＝完成；一直沒出現就等到軟上限再放行。
            return confirmed || DateTime.UtcNow >= waitUntil.Value ? true : false;
        }, 10_000);
    }

    // ── 批次入口 ────────────────────────────────────────────────────────────

    /// <summary>
    /// 「自動整理」：對附近每一格讀狀態、依策略動作，最後把該重種的種回去。
    /// </summary>
    /// <param name="startedByLoop">
    /// 這一輪是「自動重跑」發起的（<see langword="false"/>＝使用者自己按的按鈕）。
    /// 🔴 它只影響<b>吵不吵</b>，不影響任何一個決策：決策與佇列兩條路徑完全共用，
    /// 分岔的話遲早會變成「手動按跟自動跑做出來的事不一樣」，而那種差異查起來極痛苦。
    /// </param>
    private void StartAutoBatch(bool startedByLoop = false)
    {
        if (queue.IsBusy) return;

        loopRoundQuiet = startedByLoop;

        if (!TryGetGardenPatches(out var patches, out var error))
        {
            // 🔴 自動重跑的失敗不進聊天：閘門剛剛才確認過互動距離內有花盆，走到這裡
            //    多半只是狀態在這一瞬間變了（被傳送出去、權限判定翻轉）。
            //    每個週期印一次紅字，比沒有這個功能還糟。
            if (startedByLoop) Svc.Log.Debug($"[{InternalName}] 自動重跑取消：{error}");
            else Svc.Chat.PrintError($"[TC Toolbox] {error}");
            return;
        }

        doneCount = 0;
        skippedCount = 0;
        replantQueue.Clear();
        autoDecisions.Clear();
        ResetWalkRound();

        var targetCrop = GardenCropData.CropOfSeed(Config.SeedItemId);
        LogPerPatch(
            $"[{InternalName}] 自動整理開始：{patches.Count} 格；目標種子 {Config.SeedItemId}"
            + $" → 目標作物 {targetCrop}；策略 目標成熟={Config.TargetMaturePolicy}"
            + $" 非目標成熟={Config.OtherMaturePolicy} 施肥={Config.FertilizeMode}"
            + $" 枯萎={Config.WitheredMode} 護理={Config.TendWhenNeeded} 空地播種={Config.PlantWhenEmpty}"
            + $" 走位={WalkEnabled}");

        foreach (var (patchId, _) in patches)
            EnqueuePatch(
                patchId, GardenAction.Auto, Config.FertilizerItemId, Config.SeedItemId, Config.SoilItemId,
                allowWalk: true);

        queue.Enqueue("排入重種", () =>
        {
            // 🔴 在步驟裡對同一個佇列 Enqueue 是 TaskQueue 明文支援的
            //    （它的 Tick 只在「原步驟仍在隊首」時才移除），新步驟接在尾端。
            //    這正是重種必須放在這裡而不是 StartAutoBatch 的理由：哪幾格要重種，
            //    要等前面每一格都真的做完才知道。
            foreach (var id in replantQueue)
                EnqueuePatch(id, GardenAction.Plant, 0, Config.SeedItemId, Config.SoilItemId, allowWalk: true);

            var replantCount = replantQueue.Count;
            queue.Enqueue("彙總結果", () =>
            {
                lastSummary = $"園圃「自動整理」完成：處理 {doneCount} 格、跳過 {skippedCount} 格"
                              + (replantCount > 0 ? $"，其中重種 {replantCount} 格" : string.Empty)
                              + (walkedCount > 0 ? $"；走位 {walkedCount} 次" : string.Empty);
                AnnounceRoundResult();
                return true;
            });

            return true;
        });
    }

    // ── 無人值守重跑 ────────────────────────────────────────────────────────

    /// <summary>夾緊之後的重跑間隔（秒）。</summary>
    private int AutoLoopSeconds =>
        Math.Clamp(Config.AutoLoopIntervalSeconds, AutoLoopMinSeconds, AutoLoopMaxSeconds);

    /// <summary>把下一輪推到「現在 ＋ 一個間隔」。</summary>
    private void PostponeAutoLoop() => nextAutoLoopUtc = DateTime.UtcNow.AddSeconds(AutoLoopSeconds);

    /// <summary>模組停用時把排程清掉，下次啟用重新吃一次緩衝。</summary>
    private void ResetAutoLoop()
    {
        nextAutoLoopUtc = DateTime.MinValue;
        loopSawQueueBusy = false;
        loopRoundQuiet = false;
        lastLoopBlockReason = string.Empty;
    }

    /// <summary>
    /// 每一幀問一次：現在該不該自己開一輪「自動整理」。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 🔴 <b>走位預設關著。</b>關著的時候條件是「站在有花盆的地方」而不是「走去有花盆的地方」，
    /// 行為與加走位之前完全一樣。使用者自己打開
    /// <see cref="AutoGardensWorkConfig.WalkBetweenPatches"/> 之後，條件才放寬成
    /// 「<see cref="SearchRange"/> 內有花盆」，由 <c>EnqueueWalkToPatch</c> 逐格帶過去。
    /// </para>
    /// <para>
    /// 🔑 <b>決策與佇列與手動按鈕完全共用</b>：這支唯一做的事是決定「要不要呼叫
    /// <see cref="StartAutoBatch"/>」，一格園圃該做什麼、怎麼做，一行都沒有另外寫。
    /// </para>
    /// <para>
    /// 📌 <b>擋下來的時候一律靜默。</b>「附近沒花盆」「正在戰鬥」是常態不是錯誤，
    /// 每個週期報一次的話這個功能會變成噪音來源。理由留在設定畫面上給想知道的人看。
    /// </para>
    /// </remarks>
    private void TickAutoLoop()
    {
        // 🔴 忙碌追蹤要在「開關關著」之前做：關著的時候照樣要記住佇列跑完了，
        //    否則使用者一打開開關，那個殘留的 true 會讓第一輪被無謂地延後一個週期。
        if (queue.IsBusy)
        {
            loopSawQueueBusy = true;
            return;
        }

        if (loopSawQueueBusy)
        {
            loopSawQueueBusy = false;
            loopRoundQuiet = false;
            PostponeAutoLoop();
            return;
        }

        if (!Config.AutoLoopEnabled)
        {
            lastLoopBlockReason = string.Empty;
            return;
        }

        // 第一次（含剛啟用、剛登入）先給一段緩衝，不要在使用者還盯著設定畫面時就動起來。
        if (nextAutoLoopUtc == DateTime.MinValue)
        {
            nextAutoLoopUtc = DateTime.UtcNow + AutoLoopStartGrace;
            return;
        }

        if (DateTime.UtcNow < nextAutoLoopUtc) return;

        // 到期之後才開始評估閘門，而且評估本身也節流（理由見 AutoLoopGateIntervalMs）。
        if (!Throttle.Pass(AutoLoopGateKey, AutoLoopGateIntervalMs)) return;

        if (AutomationGate.TryGetBusyReason(out var busy))
        {
            lastLoopBlockReason = busy;
            return;
        }

        // 🔴 閘門的半徑必須跟著走位開關走。走位打開時仍然只看互動距離的話，
        //    使用者得先自己站到某一格旁邊迴圈才會醒過來——那正好是走位要解掉的那件事，
        //    而失敗形式是「勾了走位但它從來不動」，完全靜默。
        var gateRange = WalkEnabled ? SearchRange : InteractRange;
        if (!AnyPatchWithinRange(gateRange))
        {
            lastLoopBlockReason = WalkEnabled
                ? $"{gateRange:F0} 碼內沒有園圃地壟或花盆"
                : "互動距離內沒有園圃地壟或花盆";
            return;
        }

        lastLoopBlockReason = string.Empty;

        // 先排程再開跑：這一輪若在中途被逾時中止（佇列被清空、彙總那一步永遠不會跑到），
        // 這個值就是唯一擋住「立刻再開一輪」的東西。
        PostponeAutoLoop();
        StartAutoBatch(startedByLoop: true);
    }

    // ── 記錄 ────────────────────────────────────────────────────────────────

    /// <summary>逐格的診斷記錄：手動按的時候是 <c>Information</c>，自動重跑時降成 <c>Debug</c>。</summary>
    /// <remarks>
    /// 🔴 <b>降級只發生在自動重跑這條新路徑上，手動那條一個字都沒改。</b>
    /// 一輪 24 格會寫約 48 行；每分鐘一輪就是每小時近三千行，那足以把使用者記錄檔裡
    /// <b>別的</b>東西淹掉——包含事後要拿來查這個功能自己出了什麼事的那些行。
    /// <para>
    /// 📌 使用者的 <c>LogLevel</c> 是 1，<c>Debug</c> 收得到（真正的盲區只有 Verbose），
    /// 所以降級不等於丟掉，需要時仍然查得到；而每一輪的<b>結果</b>照樣是 Information。
    /// </para>
    /// </remarks>
    private void LogPerPatch(string message)
    {
        if (loopRoundQuiet) Svc.Log.Debug(message);
        else Svc.Log.Information(message);
    }

    /// <summary>一輪跑完的結果。</summary>
    /// <remarks>
    /// 📌 自動重跑<b>什麼都沒做的時候完全靜默</b>（那是絕大多數的輪次）；
    /// 真的動了手才寫一行 <c>Information</c>，而且不進聊天視窗。
    /// 手動按的那條路徑維持原本的行為：聊天一行、記錄一行，做了幾格都照報。
    /// </remarks>
    private void AnnounceRoundResult()
    {
        if (!loopRoundQuiet)
        {
            Svc.Chat.Print($"[TC Toolbox] {lastSummary}");
            Svc.Log.Information($"[{InternalName}] {lastSummary}");
            return;
        }

        if (doneCount == 0) return;

        Svc.Log.Information($"[{InternalName}] 自動重跑：{lastSummary}");
    }

    // ── 設定 UI ─────────────────────────────────────────────────────────────

    private static readonly string[] MatureLabels = ["跳過不動", "收穫（不重種）", "收穫並重種目標作物"];
    private static readonly string[] FertilizeLabels = ["不施肥", "只對目標作物施肥", "對所有生長中的作物施肥"];
    private static readonly string[] WitheredLabels = ["不動它", "處理掉", "處理掉並重種目標作物"];

    /// <summary>「自動整理」那一區的 UI。由 <c>DrawConfig</c> 呼叫。</summary>
    private void DrawAutoSection()
    {
        ImGui.TextUnformatted("自動整理（讀出每一格的狀態，再依下面的策略決定要做什麼）：");

        if (ImGui.Button("開始自動整理##garden"))
            StartAutoBatch();

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "對附近每一格園圃各互動一次，從遊戲自己顯示的那句話讀出狀態，\n" +
                "再依策略決定收穫／護理／施肥／處理／播種，最後把該重種的種回去。\n" +
                "策略預設是保守的：非目標作物跳過、不施肥、枯萎不動。");
        }

        ImGui.Spacing();

        // 🔑 走位放在「自動重跑」之前：它同時影響手動按的批次與無人值守重跑，
        //    放進重跑那一區的話會被讀成「只有自動重跑才會走」。
        DrawWalkSection();

        ImGui.Spacing();
        DrawAutoLoopSection();
        ImGui.Spacing();

        using (ImRaii.PushIndent())
        {
            var targetCrop = GardenCropData.CropOfSeed(Config.SeedItemId);
            if (Config.SeedItemId == 0)
            {
                ImGui.TextDisabled("? 還沒選種子，所以沒有「目標作物」——成熟的作物一律走「非目標」那條策略。");
            }
            else if (targetCrop == 0)
            {
                ImGui.TextDisabled($"? 選的種子（{Config.SeedItemId}）推不出收穫作物，成熟判定會一律走「非目標」。");
            }
            else
            {
                var name = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Item>()
                    .GetRowOrDefault(targetCrop)?.Name.ExtractText() ?? string.Empty;
                ImGui.TextDisabled($"目標作物：{name}（#{targetCrop}，由上面選的種子推得）");
            }

            DrawEnumCombo("目標作物成熟時", MatureLabels, (int)Config.TargetMaturePolicy, v =>
            {
                Config.TargetMaturePolicy = (MatureCropPolicy)v;
                Plugin.Instance.Config.Save();
            });

            DrawEnumCombo("其他作物成熟時", MatureLabels, (int)Config.OtherMaturePolicy, v =>
            {
                Config.OtherMaturePolicy = (MatureCropPolicy)v;
                Plugin.Instance.Config.Save();
            }, "辨識不出是什麼作物的那幾格也走這一條——「不知道」與「確定不是目標」一律當成同一件事。");

            DrawEnumCombo("施肥", FertilizeLabels, (int)Config.FertilizeMode, v =>
            {
                Config.FertilizeMode = (FertilizePolicy)v;
                Plugin.Instance.Config.Save();
            });

            DrawEnumCombo("枯萎的作物", WitheredLabels, (int)Config.WitheredMode, v =>
            {
                Config.WitheredMode = (WitheredPolicy)v;
                Plugin.Instance.Config.Save();
            }, "「處理」是不可回復的，所以預設不動。");

            var tend = Config.TendWhenNeeded;
            if (ImGui.Checkbox("狀態不好時護理", ref tend))
            {
                Config.TendWhenNeeded = tend;
                Plugin.Instance.Config.Save();
            }

            var plantEmpty = Config.PlantWhenEmpty;
            if (ImGui.Checkbox("空地壟播種目標作物", ref plantEmpty))
            {
                Config.PlantWhenEmpty = plantEmpty;
                Plugin.Instance.Config.Save();
            }

            var announce = Config.AnnounceEachDecision;
            if (ImGui.Checkbox("在聊天視窗逐格說明決定", ref announce))
            {
                Config.AnnounceEachDecision = announce;
                Plugin.Instance.Config.Save();
            }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("一座 3×8 的庭院會刷 24 行；記錄一律會寫，不受這格影響。");
        }

        if (autoDecisions.Count > 0)
        {
            ImGui.Spacing();
            if (ImGui.TreeNodeEx($"上次自動整理的逐格決定（{autoDecisions.Count}）###gardenAutoLog"))
            {
                foreach (var line in autoDecisions)
                    ImGui.TextDisabled(line);
                ImGui.TreePop();
            }
        }
    }

    /// <summary>「自動重跑」那一小區的 UI。</summary>
    /// <remarks>
    /// 🔑 狀態那一行刻意放在<b>列上</b>而不是 tooltip：使用者要能一眼看出「它現在到底會不會動」。
    /// tooltip 藏的是「為什麼」，不是「有沒有問題」。
    /// </remarks>
    private void DrawAutoLoopSection()
    {
        var loop = Config.AutoLoopEnabled;
        if (ImGui.Checkbox("自動重跑（站在園圃旁就每隔一段時間跑一輪）", ref loop))
        {
            Config.AutoLoopEnabled = loop;
            Plugin.Instance.Config.Save();
            ResetAutoLoop();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "打開之後，只要你人站在自家（或有權限的）園圃／花盆旁邊，模組就會自己\n" +
                "每隔一段時間跑一輪上面那個「自動整理」，用的是同一套策略。\n" +
                "\n" +
                "預設不會走位、不會傳送、不會替你上坐騎——只處理站得到的那幾格。\n" +
                "要它自己在園圃之間移動的話，另外勾下面那格「自動走位」。\n" +
                "戰鬥中、製作中、過場中、別的外掛正在移動角色時一律讓開。\n" +
                "\n" +
                "⚠️ 你在上面調的策略從此會自己跑。不想讓它自己動的話關掉這格，\n" +
                "四顆手動按鈕與「開始自動整理」按鈕完全不受影響。");
        }

        using var indent = ImRaii.PushIndent();

        ImGui.SetNextItemWidth(220f);
        var seconds = Config.AutoLoopIntervalSeconds;
        if (ImGui.SliderInt("重跑間隔（秒）", ref seconds, AutoLoopMinSeconds, 600))
        {
            Config.AutoLoopIntervalSeconds = seconds;
            Plugin.Instance.Config.Save();
            ResetAutoLoop();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "從「上一輪跑完」那一刻起算，不是從開始起算。\n" +
                $"下限 {AutoLoopMinSeconds} 秒：一輪本身就要幾十秒，設得比它短等於永遠有一輪在跑。\n" +
                "作物的成熟是以小時計的，這個值不必調小。");
        }

        DrawAutoLoopStatus();
    }

    /// <summary>「自動走位」那一小區的 UI。</summary>
    /// <remarks>
    /// 🔑 vnavmesh 在不在、上一次為什麼放棄，都畫在<b>列上</b>而不是 tooltip：
    /// 這兩件事屬於「它現在到底會不會動」，使用者要能一眼掃到。
    /// tooltip 藏的是「為什麼這樣設計」，不是「有沒有問題」。
    /// </remarks>
    private void DrawWalkSection()
    {
        var walk = Config.WalkBetweenPatches;
        if (ImGui.Checkbox("自動走位（自己走到下一格園圃旁，需要 vnavmesh）", ref walk))
        {
            Config.WalkBetweenPatches = walk;
            Plugin.Instance.Config.Save();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "打開之後，批次處理時會自己走到下一格園圃旁邊，不必你一格一格走過去。\n" +
                "\n" +
                "只在目前這張圖裡走：不傳送、不跨區、不上坐騎（一律地面路線）。\n" +
                "別的外掛（vnavmesh／Lifestream／AutoDuty／BossMod AI）正在移動角色時不搶，\n" +
                "整輪讓開等下一次。\n" +
                "\n" +
                $"走不到、逾時（{WalkHopTimeout.TotalSeconds:F0} 秒）、卡住、或這裡根本沒有導航網格時，\n" +
                "這一輪就退回原本的行為（只處理站得到的那幾格），不會重試、不會亂走。\n" +
                $"隨時可用 {NavStop.Command} 或全艦隊急停停下來。\n" +
                "\n" +
                "⚠️ 房屋內部與庭園有沒有可用的導航網格要看 vnavmesh 在那張圖建不建得出來，\n" +
                "而且玩家擺的家具與柵欄不會出現在導航網格上——路徑可能穿過它們然後卡住。");
        }

        if (!Config.WalkBetweenPatches) return;

        using var indent = ImRaii.PushIndent();

        // 外掛在不在，直接畫在列上（做法同 /gotoflag 那個模組）。
        var vnavReady = ExternalNav.IsVnavmeshReady();
        if (vnavReady)
        {
            ImGui.TextDisabled($"vnavmesh：導航網格就緒（走到 {WalkArriveDistance:F1} 碼內就開始互動）。");
        }
        else if (ExternalNav.IsVnavmeshInstalled())
        {
            ImGui.TextColored(new Vector4(1f, 0.85f, 0.35f, 1f), "vnavmesh：導航網格尚未就緒，走位暫時不能用。");
        }
        else
        {
            ImGui.TextColored(new Vector4(1f, 0.5f, 0.4f, 1f), "vnavmesh：未偵測到，走位不能用（會退回只處理站得到的那幾格）。");
        }

        if (lastWalkGiveUpReason.Length > 0)
            ImGui.TextDisabled($"上次放棄走位：{lastWalkGiveUpReason}");
    }

    /// <summary>自動重跑現在到底會不會動——一句話講完。</summary>
    private void DrawAutoLoopStatus()
    {
        if (!Config.AutoLoopEnabled)
        {
            ImGui.TextDisabled("目前只會在你按下按鈕時才動。");
            return;
        }

        if (!IsEnabled)
        {
            ImGui.TextDisabled("? 模組還沒啟用，所以自動重跑也還沒開始。");
            return;
        }

        if (queue.IsBusy)
        {
            ImGui.TextDisabled($"正在跑：{CurrentStepName}");
            return;
        }

        if (lastLoopBlockReason.Length > 0)
        {
            ImGui.TextDisabled($"目前讓開中：{lastLoopBlockReason}");
            return;
        }

        // 🔴 「不知道」要看得見：還沒排過班就顯示問號，不要畫一個看起來很具體的 0 秒。
        if (nextAutoLoopUtc == DateTime.MinValue)
        {
            ImGui.TextDisabled("? 還沒排定下一輪（剛啟用，等一下就會開始）。");
            return;
        }

        var remaining = nextAutoLoopUtc - DateTime.UtcNow;
        ImGui.TextDisabled(remaining > TimeSpan.Zero
            ? $"下一輪約 {remaining.TotalSeconds:F0} 秒後（條件符合才會真的跑）。"
            : "隨時可以開始，正在等條件符合。");
    }
    /// <summary>一個策略下拉。</summary>
    /// <remarks>
    /// 🔴 tooltip 必須在 <c>BeginCombo</c> 之後、還沒畫任何選項之前問 <c>IsItemHovered</c>：
    /// 展開之後每個 <c>Selectable</c> 都會把「目前這個項目」換掉，
    /// <c>EndCombo</c> 之後再問就不是這顆下拉了（tooltip 會掛錯或乾脆不出現）。
    /// </remarks>
    private static void DrawEnumCombo(
        string label, string[] labels, int current, Action<int> onSelect, string? tooltip = null)
    {
        if (current < 0 || current >= labels.Length) current = 0;

        ImGui.SetNextItemWidth(280f);
        var open = ImGui.BeginCombo($"{label}##gardenPolicy", labels[current]);

        if (tooltip != null && ImGui.IsItemHovered()) ImGui.SetTooltip(tooltip);
        if (!open) return;

        for (var i = 0; i < labels.Length; i++)
        {
            if (ImGui.Selectable(labels[i], i == current))
                onSelect(i);
        }

        ImGui.EndCombo();
    }
}

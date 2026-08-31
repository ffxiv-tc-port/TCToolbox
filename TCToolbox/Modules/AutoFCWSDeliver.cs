using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using TCToolbox.Core;
using ValueType = FFXIVClientStructs.FFXIV.Component.GUI.ValueType;

namespace TCToolbox.Modules;

/// <summary>
/// 部隊合建（工房專案／潛水艇零件）素材一鍵全交。
/// 機制：解析 SubmarinePartsMenu 的 AtkValues 取得可交納素材，逐項對
/// AgentId.CompanyCraftMaterial 送出合成事件，再自動填入並送出 Request 交納視窗，零 hook。
/// 參考 DailyRoutines AutoFCWSDeliver 設計重寫（API13、自帶 Request 填交，不依賴其他模組）。
/// </summary>
public sealed unsafe class AutoFCWSDeliver : TcModule
{
    public override string InternalName => "AutoFCWSDeliver";
    public override string DisplayName => "合建素材一鍵全交";
    public override string Description => "開啟部隊工房「合建」素材交納視窗時，顯示一鍵全交按鈕：自動逐項交納所有數量足夠的素材（含自動填入交納視窗與確認），交完或按停止為止。";

    public override ModuleCategory Category => ModuleCategory.Company;

    /// <summary>合建視窗上按了「開始」才跑；開著不按，一件素材都不會交出去。</summary>
    public override bool IsManualTrigger => true;

    private readonly TaskQueue queue = new();

    private int fillSlotCursor;
    private int deliveredCount;

    /// <summary>合建視窗最後一次 Finalize 的時刻；遊戲交納後會重建它，短暫消失不代表使用者關了視窗。</summary>
    private DateTime? menuGoneSince;

    /// <summary>視窗重建的容忍時間。超過就當作真的被關掉。</summary>
    private const double MenuRebuildGraceSeconds = 3;

    /// <summary>
    /// 這一項的 <c>Request</c> 交納視窗有沒有真的出現過。
    /// </summary>
    /// <remarks>
    /// 🔴 沒有這個旗標的話 <see cref="ConfirmRequest"/> 會把「視窗從頭到尾沒出現」也算成交納成功
    /// ——overlay 上「已交 N 項」持續攀升而實際上一項都沒交出去。
    /// </remarks>
    private bool requestSeen;

    /// <summary>這一項送出交納後，交納確認框有沒有被按過（診斷用，不影響流程）。</summary>
    private bool yesnoAnswered;

    /// <summary>
    /// 🔴 已對某扇確認框按過「是」、但還沒觀察到它消失過。
    /// 2026-08-31 崩潰修正（crash-20260831205734）：SelectYesno 按下後「正在關閉」的那幾幀，
    /// GetAddonByName 仍回實例且 IsVisible＋Loaded 全過——此時再 FireCallback＝原生 AVE
    /// （堆疊：ConfirmRequest→FireCallback→ffxiv_dx11+5BE756，C0000005）。
    /// 這面旗立起時一律不再按任何確認框；觀察到「窗不在」就自動清（無需專門重設點，殘留無害）。
    /// </summary>
    private bool yesnoWaitingToClose;

    /// <summary>「等待交納視窗」步驟開始的時刻（每項重設）。</summary>
    private DateTime? requestWaitStartedAt;

    /// <summary>交納視窗與確認框「兩扇都不在」開始的時刻，用來做交出後的沉澱等待。</summary>
    private DateTime? requestGoneSince;

    /// <summary>「等待清單更新」步驟開始的時刻（每項重設）。</summary>
    private DateTime? listWaitStartedAt;

    /// <summary>送出這一項之前的清單指紋，交完之後拿來確認清單真的變了。</summary>
    private (uint Index, int Count, uint ItemId, uint Owned)? preDeliverFingerprint;

    /// <summary>
    /// 送出合成事件之後，等交納視窗（或交納確認框）出現的上限。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>舊碼這裡是一個寫死的 <c>EnqueueDelay(500)</c>，那不是等待、是賭。</b>
    /// 實機 log（2026-08-29 17:53／18:07）顯示交納視窗常常要 600 毫秒以上才開得起來：
    /// 500 毫秒一到 <see cref="FillRequest"/> 就因為「視窗還沒 ready」直接回 true，
    /// <see cref="ConfirmRequest"/> 接著在同一幀走進「兩扇視窗都不在」那條路回 true，
    /// 於是解析步驟立刻跑下一輪、對<b>同一項</b>再送一次合成事件
    /// ——log 裡同一項的「確定要為合建設備提供…」確認框在一秒內出現兩次就是這個。
    /// 清單當然沒變，三輪之後零進展保險絲就把整條流程判成「交納沒有生效」。
    /// ⚠️ 這個值只有在事件<b>真的</b>沒生效時才會被等滿，調大只是讓保險絲晚一點觸發，不會漏交。
    /// </remarks>
    private const int RequestAppearWaitMs = 3_000;

    /// <summary>
    /// 交納視窗與確認框都消失之後，還要再確認多久沒有東西冒出來才算交完。
    /// </summary>
    /// <remarks>
    /// 🔴 按下「交出」到確認框冒出來之間有好幾幀是「兩扇視窗都不在」的空窗期，
    /// 舊碼在那一幀就判定這一項完成並往下跑，等於在自己的確認框還沒按掉時
    /// 就去送下一項的合成事件。
    /// </remarks>
    private const int ConfirmSettleMs = 600; // 2026-08-31 自 1000 下調:實測按「交出」後確認框 10~150ms 內出現,600 仍寬

    /// <summary>
    /// 交完一項之後，等合建視窗的素材清單反映出去的上限。
    /// </summary>
    /// <remarks>
    /// 📌 實機 log 顯示 <c>SubmarinePartsMenu</c> 在單項交納後<b>並不會</b>被 Finalize 重建
    /// （整趟只有結束時那一次 Finalize），它是原地更新 AtkValues 的
    /// ——所以「等重建」等不到東西，真正該等的是<b>清單指紋變了</b>。
    /// 等不到就照樣往下走，讓零進展保險絲去判，不會因此卡死。
    /// </remarks>
    private const int ListUpdateWaitMs = 3_000;

    /// <summary>交納確認框的辨識錨（台服 7.20：「確定要為合建設備提供○○×N嗎？」）。</summary>
    private const string FcwsPromptAnchor = "合建設備";

    /// <summary>
    /// 上一輪解析結果的指紋（首項 Index ＋ 清單長度），用來偵測「零進展」。
    /// </summary>
    private (uint Index, int Count, uint ItemId, uint Owned)? lastParseFingerprint;

    /// <summary>連續幾輪解析結果完全相同了。</summary>
    private int noProgressRounds;

    /// <summary>這一趟排進佇列的項數（<see cref="MaxItemsPerRun"/> 的計數）。</summary>
    private int enqueuedItems;

    /// <summary>
    /// 連續幾輪解析結果完全相同就判定「交納沒有生效」並中止。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>這整條批次迴圈原本沒有任何無進展保險絲。</b>解析步驟會無條件遞迴重排下一輪，
    /// 而送出 agent 事件被台服靜默拒絕（台服的拒絕常常完全沒有徵兆）或 <c>Request</c> 視窗
    /// 因任何原因沒開時，<see cref="FillRequest"/> 把「視窗沒出現」當成「已完成」回 true、
    /// <see cref="ConfirmRequest"/> 接著把它計成已交納，下一輪又解析到同一批素材
    /// ⇒ 每約 0.7 秒重送一次 agent 事件，永不自停。
    /// 逾時保底管不到：每一步都很快就「完成」了。
    /// 📌 同類模組（AutoMerge／OpenAllCoffers／TradeAllCollectables）本來就都有這兩道保險絲。
    /// </remarks>
    private const int MaxNoProgressRounds = 3;

    /// <summary>單趟交納的項數上限（合建素材實際上限約數十項，這個數綽綽有餘）。</summary>
    private const int MaxItemsPerRun = 100;

    private sealed record DeliverItem(uint Index, uint ItemId, uint Count, uint Owned);

    /// <summary>
    /// 取得工房領地，兩種失效都擋掉。
    /// <c>HousingManager.Instance()</c> 是 <c>[StaticAddress(..., isPointer: true)]</c>：
    /// 特徵碼失效時擲 <see cref="InvalidOperationException"/>，單例尚未建立時回 null——
    /// 後者直接解參考就是 AccessViolation，try/catch 攔不到，所以兩道都要。
    /// </summary>
    private static WorkshopTerritory* GetWorkshopTerritory()
    {
        try
        {
            var housing = HousingManager.Instance();
            return housing == null ? null : housing->WorkshopTerritory;
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, "[AutoFCWSDeliver] 取得工房領地失敗（多半是特徵碼失效）");
            return null;
        }
    }

    protected override void OnEnable()
    {
        queue.OnTimeout = step => Svc.Chat.PrintError($"[TC Toolbox] 合建交納步驟逾時，已停止：{step}");

        Svc.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "SubmarinePartsMenu", OnMenuFinalize);
        Svc.Framework.Update += OnUpdate;
        Svc.PluginInterface.UiBuilder.Draw += DrawOverlay;
    }

    protected override void OnDisable()
    {
        Svc.AddonLifecycle.UnregisterListener(OnMenuFinalize);
        Svc.Framework.Update -= OnUpdate;
        Svc.PluginInterface.UiBuilder.Draw -= DrawOverlay;
        queue.Abort();
    }

    private void OnUpdate(IFramework framework) => queue.Tick();

    private void OnMenuFinalize(AddonEvent type, AddonArgs args)
    {
        // 舊碼在這裡直接 Abort，於是每次 Finalize 都在跟「是不是要重建」賽跑——實機 log 顯示
        // 同一批素材交到第 2、5、8、11、33 項都可能停，數字沒有規律，正是這種競態的樣子。
        // 這裡只記錄時刻，真正的判斷交給下面的等待邏輯：回得來就繼續，回不來才算關閉。
        // ⚠️ 2026-08-29 實機修正：單項交納後這個 addon 其實「不會」被重建（整趟只在最後
        //    Finalize 一次，它是原地更新 AtkValues），所以「等清單更新」用的是指紋比對
        //    而不是等重建，見 WaitListRefreshed。這條寬限只剩「使用者關掉視窗」那條路在用。
        if (!queue.IsBusy) return;

        menuGoneSince ??= DateTime.UtcNow;
        Svc.Log.Information(
            $"[AutoFCWSDeliver] 合建視窗 Finalize，目前步驟「{queue.CurrentStep}」，本輪已交 {deliveredCount} 項，等待重建中");
    }

    private void DrawOverlay()
    {
        var addon = UiHelper.GetAddon("SubmarinePartsMenu");
        if (!UiHelper.IsReady(addon)) return;

        const ImGuiWindowFlags flags = ImGuiWindowFlags.AlwaysAutoResize |
                                       ImGuiWindowFlags.NoTitleBar |
                                       ImGuiWindowFlags.NoCollapse |
                                       ImGuiWindowFlags.NoSavedSettings;

        if (ImGui.Begin("###TCToolboxFCWSDeliver", flags))
        {
            ImGui.SetWindowPos(new Vector2(addon->GetX() + 6, addon->GetY() - ImGui.GetWindowSize().Y - 4));

            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(new Vector4(1f, 0.85f, 0.35f, 1f), DisplayName);

            ImGui.SameLine();
            using (Dalamud.Interface.Utility.Raii.ImRaii.Disabled(queue.IsBusy))
            {
                if (ImGui.Button("開始##fcws"))
                    Start();
            }

            ImGui.SameLine();
            if (ImGui.Button("停止##fcws"))
            {
                queue.Abort();
                Svc.Chat.Print($"[TC Toolbox] 已手動停止交納（本輪已交 {deliveredCount} 項）。");
            }

            if (queue.IsBusy)
            {
                ImGui.SameLine();
                ImGui.TextDisabled(queue.CurrentStep ?? string.Empty);
            }
        }

        ImGui.End();
    }

    private void Start()
    {
        if (queue.IsBusy) return;

        if (GetWorkshopTerritory() == null)
        {
            Svc.Chat.PrintError("[TC Toolbox] 必須在部隊工房內才能使用合建交納。");
            return;
        }

        deliveredCount = 0;
        menuGoneSince = null;
        requestSeen = false;
        yesnoAnswered = false;
        requestWaitStartedAt = null;
        requestGoneSince = null;
        listWaitStartedAt = null;
        preDeliverFingerprint = null;
        lastParseFingerprint = null;
        noProgressRounds = 0;
        enqueuedItems = 0;
        Svc.Log.Information("[AutoFCWSDeliver] 開始批次交納。");
        EnqueueNextItem();
    }

    private void EnqueueNextItem()
    {
        queue.Enqueue("解析可交納素材", () =>
        {
            var addon = UiHelper.GetAddon("SubmarinePartsMenu");
            if (!UiHelper.IsReady(addon))
            {
                // 視窗不在有兩種可能：遊戲正在重建它（交完一項後必然發生），或使用者真的關了。
                // 前者只需要等幾幀，所以先給一段寬限；逾時保底仍在（本步 10 秒）。
                menuGoneSince ??= DateTime.UtcNow;
                if ((DateTime.UtcNow - menuGoneSince.Value).TotalSeconds < MenuRebuildGraceSeconds)
                    return false;

                Svc.Chat.Print($"[TC Toolbox] 合建視窗已關閉，停止交納（本輪已交 {deliveredCount} 項）。");
                Svc.Log.Information(
                    $"[AutoFCWSDeliver] 視窗消失超過 {MenuRebuildGraceSeconds} 秒未重建，判定為關閉");
                return null;
            }

            menuGoneSince = null; // 回來了，重新計時

            // 🔴 上一項的交納視窗還開著就送下一項的合成事件，兩項會互相蓋掉
            //    （而且第二次的確認框會被算到第一項頭上）。等它關掉再說；
            //    真的關不掉的話由本步驟的 10 秒逾時收口，訊息看得見。
            if (UiHelper.IsAddonReady("Request"))
            {
                if (Throttle.Pass("AutoFCWSDeliver-WaitPrevRequest", 1_000))
                    Svc.Log.Information("[AutoFCWSDeliver] 上一扇交納視窗還開著，先等它關閉再送下一項。");
                return false;
            }

            // 🔴 2026-08-31 崩潰的另一半：上一項的確認框遲到彈出時，這裡照樣送下一項的合成事件，
            //    兩條窗鏈同幀重疊——之後對「正在關閉」的確認框重按就 AVE。確認框還在就不送。
            if (UiHelper.IsAddonReady("SelectYesno"))
            {
                if (Throttle.Pass("AutoFCWSDeliver-WaitPrevYesno", 1_000))
                    Svc.Log.Information("[AutoFCWSDeliver] 上一項的確認框還開著，先等它收掉再送下一項。");
                return false;
            }

            var items = ParseDeliverables(addon);
            if (items.Count == 0)
            {
                Svc.Chat.Print($"[TC Toolbox] 合建素材交納完成：本輪共交納 {deliveredCount} 項（其餘素材數量不足或已交滿）。");
                return true;
            }

            var item = items[0];

            // ── 保險絲一：零進展偵測 ───────────────────────────────────────────
            // 解析結果與上一輪完全相同 ＝ 上一輪那一項其實沒交出去。
            // 這是「agent 事件被靜默拒絕」唯一看得出來的地方（台服的拒絕沒有任何回饋）。
            // 🔴 2026-08-31 使用者回報「繳交太慢」的根因：指紋原本只有 (Index, Count)——
            //    同一項要連續交多輪（總需求大於單次量）時兩者都不變，「等清單更新」每輪
            //    白等滿 ListUpdateWaitMs，零進展保險絲還會誤數。納入 ItemId 與持有數 Owned：
            //    同項交一輪素材必定被扣、Owned 必變，清單變化在下一次解析就看得到。
            var fingerprint = (item.Index, items.Count, item.ItemId, item.Owned);
            if (lastParseFingerprint == fingerprint)
            {
                noProgressRounds++;
                if (noProgressRounds >= MaxNoProgressRounds)
                {
                    Svc.Chat.PrintError(
                        $"[TC Toolbox] 交納沒有生效（連續 {noProgressRounds} 輪清單完全沒變），已停止。" +
                        $"本輪實際交出 {deliveredCount} 項。");
                    Svc.Log.Information(
                        $"[AutoFCWSDeliver] 零進展保險絲觸發：連續 {noProgressRounds} 輪解析到同一批素材" +
                        $"（首項 Index={item.Index}、清單長度={items.Count}），判定交納事件沒有生效。");
                    return null;
                }
            }
            else
            {
                lastParseFingerprint = fingerprint;
                noProgressRounds = 0;
            }

            // ── 保險絲二：單趟項數上限 ─────────────────────────────────────────
            if (enqueuedItems >= MaxItemsPerRun)
            {
                Svc.Chat.PrintError($"[TC Toolbox] 已達單趟交納上限 {MaxItemsPerRun} 項，先停下來（本輪已交 {deliveredCount} 項）。");
                Svc.Log.Information($"[AutoFCWSDeliver] 觸及 MaxItemsPerRun={MaxItemsPerRun}，中止本趟。");
                return null;
            }

            enqueuedItems++;

            // 隱藏值：Type 帶 ItemId（去 HQ 位）、Int64 高 32 位帶數量——與遊戲原生點擊列時送出的事件相同
            var hidden = new AtkValue
            {
                Type = (ValueType)(item.ItemId % 500_000),
                Int64 = (long)item.Count << 32,
            };
            UiHelper.SendAgentEvent(AgentId.CompanyCraftMaterial, 0, 0, item.Index, item.Count, hidden);

            Svc.Log.Information(
                $"[AutoFCWSDeliver] 送出合成事件：Index={item.Index}、道具 {item.ItemId}×{item.Count}" +
                $"（清單剩 {items.Count} 項，本輪已交 {deliveredCount} 項）。");

            fillSlotCursor = 1;
            requestSeen = false;
            yesnoAnswered = false;
            requestWaitStartedAt = null;
            requestGoneSince = null;
            listWaitStartedAt = null;
            preDeliverFingerprint = fingerprint;

            // 每一步都是真的等待，不是固定延遲：等交納視窗 → 填 → 交出並等確認框處理完 → 等清單更新。
            // 步驟逾時給的是「等待上限 ＋ 一段緩衝」，讓步驟自己印出診斷後乾淨收尾，
            // 而不是讓 TaskQueue 的逾時把整條佇列砍掉。
            queue.Enqueue("等待交納視窗", WaitForRequest, RequestAppearWaitMs + 9_000);
            queue.Enqueue("填入交納視窗", FillRequest, 10_000);
            queue.Enqueue("送出交納並等待完成", ConfirmRequest, 15_000);
            queue.Enqueue("等待合建清單更新", WaitListRefreshed, ListUpdateWaitMs + 9_000);
            EnqueueNextItem();
            return true;
        }, 10_000);
    }

    /// <summary>
    /// 送出合成事件之後，真的等到交納視窗出現為止。
    /// </summary>
    /// <remarks>
    /// 📌 台服 7.20 實機順序（2026-08-29 log 直證）是
    /// <b>送事件 → <c>Request</c> 交納視窗直接開啟 → 填完按「交出」之後才出現
    /// 「確定要為合建設備提供○○×N嗎？」確認框</b>——確認框在<b>後面</b>，不在前面。
    /// 但確認框先出現的順序這裡照樣擋得住：看到就按「是」，按完<b>繼續等</b>交納視窗，
    /// 不會像舊碼那樣把「確認框按掉了」誤當成「這一項交完了」。
    /// <para>
    /// ⚠️ 只認帶「合建設備」字樣的確認框。這裡是交納視窗<b>還沒開</b>的時段，
    /// 無差別按「是」等於幫任何路過的確認框做決定。
    /// </para>
    /// </remarks>
    private bool? WaitForRequest()
    {
        requestWaitStartedAt ??= DateTime.UtcNow;

        // 🔴 每一步都重新取 addon：原生指標一律不跨幀保存。
        if (UiHelper.IsAddonReady("Request"))
        {
            requestSeen = true;
            var waited = (int)(DateTime.UtcNow - requestWaitStartedAt.Value).TotalMilliseconds;
            Svc.Log.Information($"[AutoFCWSDeliver] 交納視窗已出現（等了 {waited} 毫秒），開始填入。");
            return true;
        }

        var yesno = UiHelper.GetAddon("SelectYesno");
        if (!UiHelper.IsReady(yesno))
        {
            yesnoWaitingToClose = false; // 窗不在＝上一按已收效，解除「等消失」
        }
        else if (yesnoWaitingToClose)
        {
            return false; // 🔴 按過的那扇還在關閉途中，這幾幀絕不再碰它（AVE 路徑）
        }
        else
        {
            var prompt = UiHelper.GetSelectYesnoText();
            // 🔴 讀出替換字元＝窗的記憶體正在變動（崩潰前實機 log 的亂碼 prompt 就是這徵兆），這幀不碰。
            if (prompt.Contains('�'))
                return false;
            if (prompt.Contains(FcwsPromptAnchor, StringComparison.Ordinal))
            {
                if (Throttle.Pass("AutoFCWSDeliver-PreYesno", 300))
                {
                    UiHelper.FireCallback(yesno, true, 0);
                    yesnoWaitingToClose = true;
                    yesnoAnswered = true;
                    Svc.Log.Information(
                        $"[AutoFCWSDeliver] 交納視窗之前先按下確認框「是」：「{prompt}」，繼續等待交納視窗。");
                }

                return false;
            }
        }

        if ((DateTime.UtcNow - requestWaitStartedAt.Value).TotalMilliseconds < RequestAppearWaitMs)
            return false;

        // requestSeen 維持 false ⇒ 後面兩步會直接跳過，這一項不計入已交納，
        // 連續數輪都這樣才由零進展保險絲中止。
        Svc.Log.Information(
            $"[AutoFCWSDeliver] 等了 {RequestAppearWaitMs} 毫秒仍沒有交納視窗、也沒有合建確認框，" +
            "判定這一項的合成事件沒有生效。");
        return true;
    }

    /// <summary>把 Request 交納視窗的每個欄位填入對應道具（TextAdvance ExecRequestFill 同款事件形狀）。</summary>
    private bool? FillRequest()
    {
        // 等待步驟已經判定事件沒生效（交納視窗從頭到尾沒開）——這一項整段跳過。
        if (!requestSeen) return true;

        var request = UiHelper.GetAddon("Request");
        if (!UiHelper.IsReady(request))
        {
            // 出現過又不見了 ＝ 已經交出去（或使用者自己關了），交給 ConfirmRequest 收尾。
            Svc.Log.Information("[AutoFCWSDeliver] 填入途中交納視窗已關閉，直接進入送出／收尾判定。");
            return true;
        }

        var contextIcon = UiHelper.GetAddon("ContextIconMenu");
        if (UiHelper.IsReady(contextIcon))
        {
            // 道具選單開著 → 選第一個（即對應素材）
            UiHelper.FireCallback(contextIcon, false, 0, 0, 1021003, 0, 0);
            fillSlotCursor++;
            return false;
        }

        var entryCount = ((AddonRequest*)request)->EntryCount;
        if (fillSlotCursor > entryCount)
        {
            Svc.Log.Information($"[AutoFCWSDeliver] 交納視窗 {entryCount} 格已填完，準備按下「交出」。");
            return true;
        }

        if (Throttle.Pass("AutoFCWSDeliver-FillSlot", 150))
            UiHelper.FireCallback(request, false, 2, fillSlotCursor - 1, 0, 0);
        return false;
    }

    /// <summary>按下交納並等待視窗（含 HQ 確認）全部關閉。</summary>
    private bool? ConfirmRequest()
    {
        if (GetWorkshopTerritory() == null) return null;

        var yesno = UiHelper.GetAddon("SelectYesno");
        if (!UiHelper.IsReady(yesno))
        {
            yesnoWaitingToClose = false; // 窗不在＝上一按已收效，解除「等消失」
        }
        else if (yesnoWaitingToClose)
        {
            requestGoneSince = null; // 框還在（關閉途中），沉澱計時照樣重來
            return false;            // 🔴 但絕不重按——對關閉中的窗 FireCallback 就是這次的崩潰
        }
        else
        {
            // 交納確認。台服按下「交出」之後最多會連著出現兩扇：
            // 「確定要交易優質道具嗎？」（含 HQ 時）與「確定要為合建設備提供○○×N嗎？」。
            // ⚠️ 這裡刻意不做文字過濾（維持既有行為）：這個時點交納視窗是我們自己開的，
            //    冒出來的確認框就是這條流程的，漏按任何一扇都會讓這一項卡住。
            requestGoneSince = null; // 還有確認框要處理，沉澱計時重來
            var prompt = UiHelper.GetSelectYesnoText();
            // 🔴 讀出替換字元＝窗的記憶體正在變動，這幀不碰（下一幀重讀）。
            if (prompt.Contains('�'))
                return false;
            if (Throttle.Pass("AutoFCWSDeliver-Yesno", 300))
            {
                UiHelper.FireCallback(yesno, true, 0);
                yesnoWaitingToClose = true;
                yesnoAnswered = true;
                if (Throttle.Pass("AutoFCWSDeliver-YesnoLog", 1_000))
                    Svc.Log.Information($"[AutoFCWSDeliver] 交納確認框按下「是」：「{prompt}」。");
            }

            return false;
        }

        var request = UiHelper.GetAddon("Request");
        if (UiHelper.IsReady(request))
        {
            requestGoneSince = null;
            if (Throttle.Pass("AutoFCWSDeliver-HandOver", 500))
                UiHelper.ClickButton(request, ((AddonRequest*)request)->HandOverButton);
            return false;
        }

        // 🔴 只有「交納視窗真的出現過」才算交出去了。視窗從頭到尾沒開＝這一項根本沒交，
        //    盲目遞增會讓 overlay 的「已交 N 項」在零交納的情況下持續攀升，
        //    把使用者的唯一回饋管道變成假訊號。真正的收口交給解析步驟的零進展保險絲。
        if (!requestSeen)
        {
            if (Throttle.Pass("AutoFCWSDeliver-NoRequest", 10_000))
            {
                Svc.Log.Information(
                    "[AutoFCWSDeliver] 交納視窗未出現，本項不計入已交納" +
                    "（多半是 agent 事件被拒絕；連續數輪沒進展會由保險絲中止）。");
            }

            return true;
        }

        // 🔴 兩扇視窗都不在，還不能立刻算完成。
        //    按下「交出」到確認框冒出來之間有好幾幀是「兩扇都不在」的空窗，
        //    舊碼在那一幀就回 true ⇒ 解析步驟馬上跑下一輪、對同一項再送一次合成事件
        //    （實機 log 裡同一項確認框一秒內出現兩次就是這個），清單當然沒變 ⇒ 保險絲誤觸。
        //    ⇒ 要求「連續 ConfirmSettleMs 都沒有任何一扇冒出來」才收。
        requestGoneSince ??= DateTime.UtcNow;
        if ((DateTime.UtcNow - requestGoneSince.Value).TotalMilliseconds < ConfirmSettleMs)
            return false;

        deliveredCount++;
        Svc.Log.Information(
            $"[AutoFCWSDeliver] 第 {deliveredCount} 項交納完成（確認框{(yesnoAnswered ? "已" : "未")}出現）。");
        return true;
    }

    /// <summary>
    /// 交完一項之後，等合建視窗的素材清單真的更新了再去解析下一項。
    /// </summary>
    /// <remarks>
    /// 🔴 沒有這一步的話，下一輪解析讀到的是<b>交納前</b>的 AtkValues：指紋沒變
    /// ⇒ 零進展保險絲把「還沒更新」誤判成「交納沒有生效」，而且會對同一項再送一次合成事件。
    /// 📌 <c>SubmarinePartsMenu</c> 單項交納後不會被重建（整趟只有結束時一次 Finalize，
    /// 2026-08-29 實機 log 直證），所以判準是<b>指紋變了</b>而不是「重建完成」。
    /// </remarks>
    private bool? WaitListRefreshed()
    {
        // 這一項根本沒交出去，不必等清單——讓保險絲盡快算完它的三輪。
        if (!requestSeen) return true;

        listWaitStartedAt ??= DateTime.UtcNow;

        var addon = UiHelper.GetAddon("SubmarinePartsMenu");
        if (!UiHelper.IsReady(addon))
        {
            // 視窗不在：可能真的被重建、也可能被使用者關掉。寬限之內就等，
            // 超過寬限就往下走，由解析步驟既有的「視窗已關閉」判定收口（訊息也在那裡）。
            menuGoneSince ??= DateTime.UtcNow;
            return (DateTime.UtcNow - menuGoneSince.Value).TotalSeconds < MenuRebuildGraceSeconds ? false : true;
        }

        menuGoneSince = null; // 視窗在，重新計時（與解析步驟同一條規則）
        var items = ParseDeliverables(addon);
        var fingerprint = items.Count == 0 ? ((uint)0, 0, (uint)0, (uint)0) : (items[0].Index, items.Count, items[0].ItemId, items[0].Owned);
        if (preDeliverFingerprint != fingerprint)
        {
            Svc.Log.Information(
                $"[AutoFCWSDeliver] 合建清單已更新（首項 Index={preDeliverFingerprint?.Index}、長度 " +
                $"{preDeliverFingerprint?.Count} → 首項 Index={fingerprint.Item1}、長度 {fingerprint.Item2}），" +
                "進入下一項。");
            return true;
        }

        if ((DateTime.UtcNow - listWaitStartedAt.Value).TotalMilliseconds < ListUpdateWaitMs)
            return false;

        Svc.Log.Information(
            $"[AutoFCWSDeliver] 等了 {ListUpdateWaitMs} 毫秒合建清單仍沒有變化，" +
            "交給零進展保險絲判斷是不是真的沒交出去。");
        return true;
    }

    /// <summary>解析合建視窗目前可交納（數量足夠且未交滿）的素材清單。</summary>
    private static List<DeliverItem> ParseDeliverables(AtkUnitBase* addon)
    {
        var result = new List<DeliverItem>();

        // 🔴 光是判 addon 與長度還不夠。AtkValuesSpan 的實作是
        // new Span<AtkValue>(AtkValues, AtkValuesCount)，它自己不判 AtkValues 這個欄位，
        // 而 Span 的建構子也不驗指標。addon 拆解時 AtkValues 會先被釋放成 null、
        // AtkValuesCount 卻可能還留著殘值，這個組合會合法建構出一個長度非零的 Span，
        // 連 Span 自己的邊界檢查都會放行，一直到真的索引下去才對位址 0 解參考 ＝
        // AccessViolationException（corrupted-state exception，try/catch 攔不到）。
        // ⇒ 必須另外自判 AtkValues 欄位；讀不到就回空清單（與下面既有的長度不足同一條路）。
        if (addon == null || addon->AtkValues == null) return result;

        var agent = AgentCompanyCraftMaterial.Instance();
        if (agent == null) return result;

        var supplyCount = 0;
        foreach (var supply in agent->SupplyItems)
        {
            if (supply != 0)
                supplyCount++;
        }

        if (supplyCount == 0) return result;

        var values = addon->AtkValuesSpan;
        if (values.Length <= 132 + supplyCount) return result;

        for (var i = 0; i < supplyCount; i++)
        {
            var itemId = values[12 + i].UInt;
            if (itemId == 0) continue; // 此欄無道具

            var completed = values[132 + i].UInt;
            if (completed == 1) continue; // 已交滿

            var required = values[60 + i].UInt;
            var owned = values[72 + i].UInt;
            if (owned < required) continue; // 持有數不足

            result.Add(new DeliverItem((uint)i, itemId, required, owned));
        }

        return result;
    }
}

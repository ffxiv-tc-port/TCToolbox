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
using Lumina.Excel.Sheets;
using TCToolbox.Core;
using ValueType = FFXIVClientStructs.FFXIV.Component.GUI.ValueType;

namespace TCToolbox.Modules;

/// <summary>
/// 公會合建（工坊專案／潛水艇零件）素材一鍵全交。
/// 機制：解析 SubmarinePartsMenu 的 AtkValues 取得可交納素材，逐項對
/// AgentId.CompanyCraftMaterial 送出合成事件，再自動填入並送出 Request 交納視窗，零 hook。
/// 參考 DailyRoutines AutoFCWSDeliver 設計重寫（API13、自帶 Request 填交，不依賴其他模組）。
/// </summary>
public sealed unsafe partial class AutoFCWSDeliver : TcModule
{
    public override string InternalName => "AutoFCWSDeliver";
    public override string DisplayName => "合建素材一鍵全交";
    public override string Description =>
        "開啟公會工坊「合建」素材交納視窗時，顯示一鍵全交按鈕：自動逐項交納所有數量足夠的素材"
        + "（含自動填入交納視窗與確認），交完或按停止為止。"
        + "另有一顆「缺料加進製作清單」：把這個階段還缺、身上不夠的素材逐列列出來，"
        + $"勾選後一次加進 AllaganTools 的製作清單「{ShortfallCraftListName}」（只加清單，不製作、不買東西）。"
        + "勾選「連續推進」的話，交完一個階段會自動推進工程進展再交下一個階段，直到所有階段交完"
        + "；再勾「最後完成並回收成品」的話，最後會自動按「完成」並回收成品（預設關，最後一步留給你手動）。";

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

    /// <summary>這一項送出交納後，<b>本模組自己</b>按掉了幾扇交納確認框（診斷用，不影響流程）。</summary>
    /// <remarks>
    /// 🔴 台服按下「交出」之後最多會連著跳<b>兩扇</b>（含 HQ 時的「確定要交易優質道具嗎？」
    /// ＋「確定要為合建設備提供○○×N嗎？」），所以這裡是計數不是布林——
    /// 只按到一扇就往下走，第二扇會留在畫面上把整條流程卡死。
    /// </remarks>
    private int yesnoPressedThisItem;

    /// <summary>交納途中的確認框（合建交納框、優質道具框）讓位憑據：叫出它們的動作之前續約，按之前問 IsExclusive。</summary>
    private readonly YesAlreadyBotherLease deliverConfirmLease = new("SelectYesno");
    private YesAlreadyIpc.BotherPause? loggedDeliverPause;

    /// <summary>這一項送出交納後，本模組<b>看到過</b>幾扇確認框（看到不一定按得到）。</summary>
    /// <remarks>
    /// 和 <see cref="yesnoPressedThisItem"/> 分開記，是為了讓 log 能分辨三種情況：
    /// 沒看到（別人搶先按掉了，或是我們根本看不到那扇窗）、看到且按掉、看到卻按不下去。
    /// 「不知道」本身要看得見，不能把三種都印成同一句話。
    /// </remarks>
    private int yesnoSeenThisItem;

    /// <summary>上一幀看到的那扇確認框實例位址，用來把「同一扇看了很多幀」數成一扇。</summary>
    /// <remarks>
    /// 🔴 位址<b>只做等值比較，永不解參考</b>，也絕不跨幀拿來當指標用——每一幀都重新查。
    /// ⚠️ 位址是會被新視窗重用的，所以這個計數只是診斷用的近似值，不拿來做任何流程判斷。
    /// </remarks>
    private nint lastYesnoSeenAddress;

    /// <summary>
    /// 「交納視窗還開著，但一扇確認框都找不到」這個狀態是從什麼時候開始的（<c>null</c>＝現在不是這個狀態）。
    /// </summary>
    /// <remarks>
    /// 超過 <see cref="ConfirmStallReportMs"/> 就把 <c>SelectYesno</c> 每一格的狀態印出來。
    /// </remarks>
    private DateTime? confirmStallSince;

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
    /// ⚠️ 這個值只有在事件<b>真的</b>沒生效時才會被等滿，調大只是讓保險絲晚一點觸發，不會漏交。
    /// </remarks>
    private const int RequestAppearWaitMs = 3_000;

    /// <summary>
    /// 交納視窗與確認框都消失之後，還要再確認多久沒有東西冒出來才算交完。
    /// </summary>
    /// <remarks>
    /// 🔴 按下「交出」到確認框冒出來之間有好幾幀是「兩扇視窗都不在」的空窗期，
    /// 在那一幀就判定這一項完成的話，等於在自己的確認框還沒按掉時
    /// 就去送下一項的合成事件。
    /// </remarks>
    private const int ConfirmSettleMs = 600; // 實測按「交出」後確認框 10~150ms 內出現，600 仍寬

    /// <summary>
    /// 「交納視窗還開著、卻一扇確認框都找不到」持續這麼久就寫一次診斷（毫秒）。
    /// </summary>
    /// <remarks>
    /// 正常節奏下這個狀態只有幾幀（按下「交出」到確認框冒出來之間），所以 1.5 秒不會誤報；
    /// 真的卡住時每 <see cref="ConfirmStallReportMs"/> 印一行 <c>Information</c>，使用者的 log 就足以定案。
    /// </remarks>
    private const int ConfirmStallReportMs = 1_500;

    /// <summary>
    /// 交完一項之後，等合建視窗的素材清單反映出去的上限。
    /// </summary>
    /// <remarks>
    /// 等不到就照樣往下走，讓零進展保險絲去判，不會因此卡死。
    /// </remarks>
    private const int ListUpdateWaitMs = 3_000;

    /// <summary>交納確認框的辨識錨（台服：「確定要為合建設備提供○○×N嗎？」）。</summary>
    private const string FcwsPromptAnchor = "合建設備";

    /// <summary>
    /// 交納流程<b>第二種</b>會冒出來的確認框：含 HQ 素材時的「確定要交易優質道具嗎？」。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>本模組必須自己認得它</b>：<c>AutoRequestItemSubmit</c> 在合建視窗開著時會整支讓路
    /// （見那支的 <c>ShouldYieldToFcwsDeliver</c>），所以這一扇沒有別人會按。
    /// </remarks>
    private static readonly uint[] HighQualityPromptRows = [5450, 11514, 102434];

    /// <summary>解析好的優質道具確認框字串（<see cref="OnEnable"/> 建一次；台服沒有的列不收）。</summary>
    private readonly HashSet<string> highQualityPrompts = new(StringComparer.Ordinal);

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
    /// 沒有這道保險絲的話，事件被靜默拒絕時會每約 0.7 秒重送一次 agent 事件，永不自停；
    /// 逾時保底管不到：每一步都很快就「完成」了。
    /// </remarks>
    private const int MaxNoProgressRounds = 3;

    /// <summary>單趟交納的項數上限（合建素材實際上限約數十項，這個數綽綽有餘）。</summary>
    private const int MaxItemsPerRun = 100;

    private sealed record DeliverItem(uint Index, uint ItemId, uint Count, uint Owned);

    /// <summary>合建視窗上的一列素材（未過濾）。</summary>
    private sealed record MaterialRow(uint Index, uint ItemId, uint Required, uint Owned, bool Completed);

    /// <summary>一列「還缺」的素材。<c>Missing</c> 恆大於 0。</summary>
    private sealed record ShortfallItem(uint ItemId, uint Required, uint Owned, uint Missing);

    /// <summary>缺料要送進哪一張 AllaganTools 製作清單（固定名稱，不存在就自動建）。</summary>
    private const string ShortfallCraftListName = "來自合建交納";

    /// <summary>缺料面板開著沒。以下欄位只在遊戲主執行緒讀寫。</summary>
    private bool shortfallPanelOpen;

    /// <summary>面板上被取消勾選的道具編號；不勾的不會被加進製作清單。</summary>
    private readonly HashSet<uint> shortfallExcluded = [];

    /// <summary>面板最後一次重算出來的缺料。</summary>
    private readonly List<ShortfallItem> shortfallRows = [];

    /// <summary>上一次加入的結果（空字串＝還沒按過）。</summary>
    private string shortfallResult = string.Empty;

    /// <summary>AllaganTools 現在可不可用（跟著面板的重算一起更新，不每幀問）。</summary>
    private bool allaganReady;

    private string allaganReason = string.Empty;

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

        highQualityPrompts.Clear();
        foreach (var row in HighQualityPromptRows)
        {
            var text = Svc.Data.GetExcelSheet<Addon>().GetRowOrDefault(row)?.Text.ExtractText().Trim();
            if (!string.IsNullOrWhiteSpace(text)) highQualityPrompts.Add(text);
        }

        // 要使用者回報的診斷：log 裡有這一行才知道實際用的判準是什麼。
        Svc.Log.Information(
            $"[AutoFCWSDeliver] 會自動按下的確認框判準：含「{FcwsPromptAnchor}」字樣，或優質道具 " +
            $"{highQualityPrompts.Count}/{HighQualityPromptRows.Length} 條：{string.Join(" | ", highQualityPrompts)}");

        ResolveLoopAnchors();

        Svc.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "SubmarinePartsMenu", OnMenuFinalize);
        Svc.Framework.Update += OnUpdate;
        Svc.PluginInterface.UiBuilder.Draw += DrawOverlay;
    }

    public override string? StopForFleetEmergency()
    {
        if (!IsBusy) return null;

        var step = CurrentStepName;
        StopForEmergency();
        return $"合建素材交納（中止於「{step}」）";
    }

    protected override void OnDisable()
    {
        Svc.AddonLifecycle.UnregisterListener(OnMenuFinalize);
        Svc.Framework.Update -= OnUpdate;
        Svc.PluginInterface.UiBuilder.Draw -= DrawOverlay;
        queue.Abort();
        highQualityPrompts.Clear();
        ResetLoopState();
        deliverConfirmLease.Reset();
        loggedDeliverPause = null;
        floatingOverlayPos = null;
    }

    private void OnUpdate(IFramework framework)
    {
        KeepSelectStringExclusive();
        queue.Tick();
    }

    private void OnMenuFinalize(AddonEvent type, AddonArgs args)
    {
        // 這裡只記錄時刻，不直接 Abort：真正的判斷交給下面的等待邏輯，回得來就繼續，回不來才算關閉。
        // ⚠️ 單項交納後這個 addon 其實「不會」被重建（整趟只在最後 Finalize 一次，它是原地更新 AtkValues），
        //    所以「等清單更新」用的是指紋比對而不是等重建，見 WaitListRefreshed。這條寬限只剩「使用者關掉視窗」那條路在用。
        if (!queue.IsBusy) return;

        menuGoneSince ??= DateTime.UtcNow;
        Svc.Log.Information(
            $"[AutoFCWSDeliver] 合建視窗 Finalize，目前步驟「{queue.CurrentStep}」，本輪已交 {deliveredCount} 項，等待重建中");
    }

    private void DrawOverlay()
    {
        var addon = UiHelper.GetAddon("SubmarinePartsMenu");
        var anchored = UiHelper.IsReady(addon);

        // 連續推進換階段時合建視窗是關著的：停止鈕照樣要按得到，停在最後貼著合建視窗的位置。
        if (!anchored && !(queue.IsBusy && Config.ContinuousPhases && floatingOverlayPos.HasValue)) return;

        const ImGuiWindowFlags flags = ImGuiWindowFlags.AlwaysAutoResize |
                                       ImGuiWindowFlags.NoTitleBar |
                                       ImGuiWindowFlags.NoCollapse |
                                       ImGuiWindowFlags.NoSavedSettings;

        if (ImGui.Begin("###TCToolboxFCWSDeliver", flags))
        {
            if (anchored)
            {
                var pos = new Vector2(addon->GetX() + 6, addon->GetY() - ImGui.GetWindowSize().Y - 4);
                ImGui.SetWindowPos(pos);
                floatingOverlayPos = pos;
            }
            else
            {
                ImGui.SetWindowPos(floatingOverlayPos!.Value);
            }

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
                if (queue.IsBusy)
                    Svc.Log.Information($"[AutoFCWSDeliver] 使用者按下停止，本輪已交 {deliveredCount} 項。");

                queue.Abort();
                Svc.Chat.Print($"[TC Toolbox] 已手動停止交納（本輪已交 {deliveredCount} 項）。");
            }

            ImGui.SameLine();
            var loop = Config.ContinuousPhases;
            if (ImGui.Checkbox("連續推進##fcwsloop", ref loop))
            {
                Config.ContinuousPhases = loop;
                Plugin.Instance.Config.Save();
            }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(LoopTooltip);

            ImGui.SameLine();
            DrawFinishCheckbox("完成並回收成品##fcwsfinish");

            if (queue.IsBusy)
            {
                ImGui.SameLine();
                ImGui.TextDisabled(queue.CurrentStep ?? string.Empty);
            }

            if (anchored)
            {
                ImGui.SameLine();
                if (ImGui.Button(shortfallPanelOpen ? "收起缺料##fcwsgap" : "缺料加進製作清單##fcwsgap"))
                {
                    shortfallPanelOpen = !shortfallPanelOpen;
                    if (shortfallPanelOpen) RefreshShortfalls(addon);
                }

                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip(
                        $"列出這個階段還缺、而且身上不夠的素材，勾選後一次加進 AllaganTools 的製作清單「{ShortfallCraftListName}」。"
                        + "只加清單：不會開始製作、不會去市場買東西，也不會換掉你目前選中的製作清單。");
                }

                if (shortfallPanelOpen) DrawShortfallPanel(addon);
            }
        }

        ImGui.End();
    }

    /// <summary>重算缺料清單與 AllaganTools 的可用性。</summary>
    private void RefreshShortfalls(AtkUnitBase* addon)
    {
        shortfallRows.Clear();
        shortfallRows.AddRange(ParseShortfalls(addon));
        allaganReady = AllaganToolsIpc.IsReady(out allaganReason);

        // 換了階段之後，之前取消勾選的道具不該繼續被記得。
        shortfallExcluded.RemoveWhere(id =>
        {
            foreach (var row in shortfallRows)
            {
                if (row.ItemId == id) return false;
            }

            return true;
        });
    }

    /// <summary>缺料面板：逐列勾選，一顆按鈕加進製作清單。</summary>
    private void DrawShortfallPanel(AtkUnitBase* addon)
    {
        ImGui.Separator();

        if (Throttle.Pass("AutoFCWSDeliver-ShortfallRefresh", 1_000))
            RefreshShortfalls(addon);

        if (shortfallRows.Count == 0)
        {
            ImGui.TextDisabled("這個階段沒有「數量不足」的素材。");
            return;
        }

        var selected = 0;
        foreach (var row in shortfallRows)
        {
            var include = !shortfallExcluded.Contains(row.ItemId);
            var label = $"{ItemNames.Get(row.ItemId)}　還缺 {row.Missing}（需要 {row.Required}、身上 {row.Owned}）##fcwsgap{row.ItemId}";

            if (ImGui.Checkbox(label, ref include))
            {
                if (include) shortfallExcluded.Remove(row.ItemId);
                else shortfallExcluded.Add(row.ItemId);
            }

            if (include) selected++;
        }

        using (Dalamud.Interface.Utility.Raii.ImRaii.Disabled(!allaganReady || selected == 0))
        {
            if (ImGui.Button($"把勾選的 {selected} 種加進「{ShortfallCraftListName}」##fcwsgapadd"))
                PushShortfallToCraftList();
        }

        // 「不知道」本身要在列上看得見：按鈕灰掉而不說原因的話，使用者只會以為按鈕壞了。
        if (!allaganReady)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(allaganReason);
        }

        if (shortfallResult.Length != 0)
            ImGui.TextDisabled(shortfallResult);
    }

    /// <summary>把勾選的缺料送進固定名稱的製作清單。</summary>
    private void PushShortfallToCraftList()
    {
        var wanted = new Dictionary<uint, uint>();
        var detail = string.Empty;

        foreach (var row in shortfallRows)
        {
            if (shortfallExcluded.Contains(row.ItemId)) continue;

            wanted[row.ItemId] = row.Missing;
            detail += detail.Length == 0 ? string.Empty : "、";
            detail += $"{ItemNames.Get(row.ItemId)}×{row.Missing}";
        }

        if (!AllaganToolsIpc.TryAddToNamedCraftList(ShortfallCraftListName, wanted, out var sent, out var reason))
        {
            shortfallResult = reason;
            Svc.Chat.PrintError($"[TC Toolbox] 缺料沒有加進製作清單：{reason}");
            Svc.Log.Information($"[AutoFCWSDeliver] 缺料加入製作清單失敗：{reason}");
            return;
        }

        shortfallResult = $"已送出 {sent} 種到製作清單「{ShortfallCraftListName}」。";
        Svc.Chat.Print(
            $"[TC Toolbox] 已把 {sent} 種缺料加進 AllaganTools 製作清單「{ShortfallCraftListName}」。");
        Svc.Log.Information(
            $"[AutoFCWSDeliver] 缺料加入製作清單「{ShortfallCraftListName}」：{sent} 種，{detail}");
    }

    private void Start()
    {
        if (queue.IsBusy) return;

        if (GetWorkshopTerritory() == null)
        {
            Svc.Chat.PrintError("[TC Toolbox] 必須在公會工坊內才能使用合建交納。");
            return;
        }

        if (!ConfirmTextAdvanceIdle(starting: true)) return;

        deliveredCount = 0;
        menuGoneSince = null;
        requestSeen = false;
        yesnoPressedThisItem = 0;
        yesnoSeenThisItem = 0;
        lastYesnoSeenAddress = nint.Zero;
        confirmStallSince = null;
        requestWaitStartedAt = null;
        requestGoneSince = null;
        listWaitStartedAt = null;
        preDeliverFingerprint = null;
        lastParseFingerprint = null;
        noProgressRounds = 0;
        enqueuedItems = 0;
        ResetLoopState();
        Svc.Log.Information("[AutoFCWSDeliver] 開始批次交納。");
        if (Config.ContinuousPhases) LogLoopStart();
        EnqueueNextItem();
    }

    /// <summary>
    /// TextAdvance 啟用時自己也會填交納視窗、按「交出」；兩邊對同一扇各做一次，後做的那次會打在關閉中的視窗上。
    /// </summary>
    /// <remarks>
    /// 契約：送出會叫出交納視窗的事件之前、以及每次要碰交納視窗之前都問一次；它會動手或確認不了就說明並回
    /// <see langword="false"/>，呼叫端整趟停下。只管交納視窗：確認框與合建設備選單 TextAdvance 不碰。
    /// </remarks>
    private bool ConfirmTextAdvanceIdle(bool starting)
    {
        var handling = TextAdvanceIpc.QueryRequestHandling(out var detail);
        if (handling is TextAdvanceIpc.RequestHandling.NotLoaded or TextAdvanceIpc.RequestHandling.Idle) return true;

        var head = starting
            ? "合建交納沒有開始"
            : $"合建交納已停止（本輪已交 {deliveredCount} 項；交納視窗若還開著，留給你或 TextAdvance 處理）";
        Svc.Chat.PrintError(handling == TextAdvanceIpc.RequestHandling.Active
            ? $"[TC Toolbox] {head}：TextAdvance 啟用中，它的「自動確認交付請求 (RH)」或「自動填充請求 (RF)」開著，會和這裡同時操作交納視窗。" +
              "請先用 /at n 關掉 TextAdvance（或在它的設定關掉那兩項）再按開始。"
            : $"[TC Toolbox] {head}：確認不了 TextAdvance 會不會同時操作交納視窗（問不到它的狀態），為了不兩邊同時按，這裡不動手。");
        Svc.Log.Information(
            $"[AutoFCWSDeliver] {(starting ? "不開始" : $"中止於「{queue.CurrentStep}」")}：TextAdvance → {handling}（{detail}）。");
        return false;
    }

    /// <summary>請 YesAlready 暫停 SelectYesno 自動回答；回 <see langword="false"/>＝確認不了，不要做會叫出確認框的事。</summary>
    private bool KeepDeliverConfirmExclusive()
    {
        var ok = deliverConfirmLease.Renew();
        if (loggedDeliverPause == deliverConfirmLease.Last) return ok;

        loggedDeliverPause = deliverConfirmLease.Last;
        Svc.Log.Information(
            $"[AutoFCWSDeliver] 交納：請 YesAlready 暫停「SelectYesno」自動回答 → {deliverConfirmLease.Last}" +
            $"（每 {YesAlreadyBotherLease.RenewAfterMs / 1000} 秒續 {YesAlreadyBotherLease.PauseMs / 1000} 秒，結束後它自己恢復）。");
        return ok;
    }

    /// <summary>確認不了 YesAlready 已讓開確認框：整趟停下、確認框留給使用者。回 <c>null</c> 給佇列。</summary>
    private bool? StopForDeliverConfirm(string prompt)
    {
        Svc.Chat.PrintError(
            $"[TC Toolbox] 合建交納已停止（本輪已交 {deliveredCount} 項）：確認不了 YesAlready 會不會同時按確認框，" +
            "為了不兩邊各按一次，這裡不再動手。畫面上的確認框請自己按。");
        Svc.Log.Information(
            $"[AutoFCWSDeliver] 中止於「{queue.CurrentStep}」：YesAlready 讓位結果 {deliverConfirmLease.Last}（或已過期）" +
            (prompt.Length > 0 ? $"；確認框「{prompt}」" : string.Empty) + "。");
        return null;
    }

    private void EnqueueNextItem()
    {
        queue.Enqueue("解析可交納素材", () =>
        {
            var addon = UiHelper.GetAddon("SubmarinePartsMenu");
            if (!UiHelper.IsReady(addon))
            {
                // 連續推進：階段交完時遊戲會自己收起合建視窗、打開合建設備選單。
                if (LoopEnabled && IsStationMenuOpen())
                {
                    EnqueuePhaseEnd();
                    return true;
                }

                // 視窗不在有兩種可能：遊戲正在重建它，或使用者真的關了。
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
            // ⚠️ 用掃全部實例的版本：只看第 1 格的話，「還開著」會被答成「已經關了」。
            if (UiHelper.IsAnyAddonReady("Request"))
            {
                if (Throttle.Pass("AutoFCWSDeliver-WaitPrevRequest", 1_000))
                    Svc.Log.Information("[AutoFCWSDeliver] 上一扇交納視窗還開著，先等它關閉再送下一項。");
                return false;
            }

            // 🔴 上一項的確認框遲到彈出時若照樣送下一項的合成事件，
            //    兩條窗鏈同幀重疊——之後對「正在關閉」的確認框重按就 AVE。確認框還在就不送。
            // ⚠️ 同上，掃全部實例：兩扇確認框連著跳時第 1 格可能已經是關掉的那一扇。
            if (UiHelper.IsAnyAddonReady("SelectYesno"))
            {
                if (Throttle.Pass("AutoFCWSDeliver-WaitPrevYesno", 1_000))
                    Svc.Log.Information("[AutoFCWSDeliver] 上一項的確認框還開著，先等它收掉再送下一項。");
                return false;
            }

            var items = ParseDeliverables(addon);
            if (items.Count == 0)
            {
                if (LoopEnabled) return OnPhaseListEmpty(addon);

                Svc.Chat.Print($"[TC Toolbox] 合建素材交納完成：本輪共交納 {deliveredCount} 項（其餘素材數量不足或已交滿）。");
                Svc.Log.Information($"[AutoFCWSDeliver] 批次交納結束：本輪共交納 {deliveredCount} 項，清單上已沒有可交的素材。");
                return true;
            }

            var item = items[0];

            // ── 保險絲一：零進展偵測 ───────────────────────────────────────────
            // 解析結果與上一輪完全相同 ＝ 上一輪那一項其實沒交出去。
            // 這是「agent 事件被靜默拒絕」唯一看得出來的地方（台服的拒絕沒有任何回饋）。
            // 🔴 指紋要納入 ItemId 與持有數 Owned：同一項要連續交多輪（總需求大於單次量）時 Index 與 Count 都不變，
            //    而同項交一輪素材必定被扣、Owned 必變，清單變化在下一次解析就看得到。
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

            // 下面這個事件會叫出交納視窗：TextAdvance 會動手（或確認不了）就不送。
            if (!ConfirmTextAdvanceIdle(starting: false)) return null;

            // 也可能先叫出合建確認框：YesAlready 讓開確認不了就不送。
            if (!KeepDeliverConfirmExclusive()) return StopForDeliverConfirm(string.Empty);

            enqueuedItems++;

            // 隱藏值：Type 帶 ItemId（去 HQ 位）、Int64 高 32 位帶數量——與遊戲原生點擊列時送出的事件相同
            var hidden = new AtkValue
            {
                Type = (ValueType)(item.ItemId % 500_000),
                Int64 = (long)item.Count << 32,
            };
            UiHelper.SendAgentEvent(AgentId.CompanyCraftMaterial, 0, 0, item.Index, item.Count, hidden);

            Svc.Log.Debug(
                $"[AutoFCWSDeliver] 送出合成事件：Index={item.Index}、道具 {item.ItemId}×{item.Count}" +
                $"（清單剩 {items.Count} 項，本輪已交 {deliveredCount} 項）。");

            fillSlotCursor = 1;
            requestSeen = false;
            yesnoPressedThisItem = 0;
            yesnoSeenThisItem = 0;
            lastYesnoSeenAddress = nint.Zero;
            confirmStallSince = null;
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
    /// ⚠️ 只認帶「合建設備」字樣的確認框。這裡是交納視窗<b>還沒開</b>的時段，
    /// 無差別按「是」等於幫任何路過的確認框做決定。
    /// </remarks>
    private bool? WaitForRequest()
    {
        requestWaitStartedAt ??= DateTime.UtcNow;

        // 🔴 每一步都重新取 addon：原生指標一律不跨幀保存。
        // ⚠️ 掃全部實例，與解析步驟的「上一扇交納視窗還開著嗎」用同一把尺——
        //    一邊掃全部、一邊只看第 1 格的話，兩邊會對同一個畫面得出相反的答案。
        if (UiHelper.IsAnyAddonReady("Request"))
        {
            requestSeen = true;
            var waited = (int)(DateTime.UtcNow - requestWaitStartedAt.Value).TotalMilliseconds;
            Svc.Log.Debug($"[AutoFCWSDeliver] 交納視窗已出現（等了 {waited} 毫秒），開始填入。");
            return true;
        }

        // 🔴 掃全部實例，不是只看第 1 格：確認框可能有兩扇，第 1 格拿到的常是已經關掉的那一扇。
        var yesno = UiHelper.FindReadyAddon(UiHelper.SelectYesnoAddonName);
        if (yesno != null)
        {
            var prompt = ReadPrompt(yesno);
            // 🔴 讀出替換字元＝窗的記憶體正在變動，這幀不碰。
            if (LooksMidUpdateNoisy(prompt))
                return false;
            if (prompt.Contains(FcwsPromptAnchor, StringComparison.Ordinal))
            {
                NoteYesnoSeen(yesno);
                if (!deliverConfirmLease.IsExclusive) return StopForDeliverConfirm(prompt);

                // 🔴 守衛在 TryFireCallback 裡（按位址、SelectYesno 是「回答一次即終結」的併鍵窗）：
                //    同一扇實例只送得出去一次，回 false＝「這一輪沒按到，下一輪再來」。
                if (UiHelper.TryFireCallback(yesno, true, 0))
                {
                    yesnoPressedThisItem++;
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

        // ⚠️ 與其他步驟同樣掃全部實例：只看第 1 格會把「還開著的第 2 格」誤判成「視窗已關閉」，
        //    於是這一格根本沒填就跳去 ConfirmRequest。
        var request = UiHelper.FindReadyAddon("Request");
        if (request == null)
        {
            // 出現過又不見了 ＝ 已經交出去（或使用者自己關了），交給 ConfirmRequest 收尾。
            Svc.Log.Information("[AutoFCWSDeliver] 填入途中交納視窗已關閉，直接進入送出／收尾判定。");
            return true;
        }

        // 流程進行中 TextAdvance 才被打開的情況：每次要碰交納視窗（含它的道具選單）之前再問一次。
        if (!ConfirmTextAdvanceIdle(starting: false)) return null;

        var contextIcon = UiHelper.GetAddon("ContextIconMenu");
        if (UiHelper.IsReady(contextIcon))
        {
            // 道具選單開著 → 選第一個（即對應素材）
            // 🔴 選了就關：關閉中那幾幀 IsReady 照過，這裡又是每 tick 回來、沒有節流——
            //    守衛（ContextIconMenu 是併鍵的單答窗）擋住對同一實例的第二次，
            //    而且只有真的送出去才推進游標，否則游標會多跳、漏填格。
            if (UiHelper.TryFireCallback(contextIcon, false, 0, 0, 1021003, 0, 0))
                fillSlotCursor++;
            return false;
        }

        var entryCount = ((AddonRequest*)request)->EntryCount;
        if (fillSlotCursor > entryCount)
        {
            Svc.Log.Debug($"[AutoFCWSDeliver] 交納視窗 {entryCount} 格已填完，準備按下「交出」。");
            return true;
        }

        if (Throttle.Pass("AutoFCWSDeliver-FillSlot", 150))
            UiHelper.FireCallback(request, false, 2, fillSlotCursor - 1, 0, 0);
        return false;
    }

    /// <summary>
    /// 讀這一扇確認框的提示文字，<b>payload 已經剝掉，只剩純文字</b>。
    /// </summary>
    /// <remarks>
    /// 讀 <see cref="AddonPrompt.ReadSelectYesnoText"/>（<c>MemoryHelper.ReadSeString().TextValue</c>），
    /// 與本 repo 其他確認框判定（<c>AutoRequestItemSubmit</c>／<c>OptimizedFreeShop</c>／
    /// <c>AutoJoinPartyFinder</c>）同一個基準。剝掉 payload 之後只剩純文字，
    /// U+FFFD 才是它原本的語意：<b>真的</b>讀到半個字元＝窗的記憶體正在變動。
    /// </remarks>
    private static string ReadPrompt(AtkUnitBase* yesno)
    {
        // 🔴 兩道空指標閘門逐字沿用 UiHelper.ReadSelectYesnoText：AddonPrompt 那一支只判 addon 與
        //    PromptText，沒有判 NodeText.StringPtr。StringPtr 為 null 而 Length 還留著殘值時，
        //    Utf8String.AsSpan() 會建出一個長度非零、指向位址 0 的 Span——列舉下去就是
        //    AccessViolationException（corrupted-state exception，try/catch 攔不到）。
        //    這條路每幀都會走（確認框開著時），所以閘門要放在這裡而不是靠呼叫端。
        if (!UiHelper.IsReady(yesno)) return string.Empty;

        var node = ((AddonSelectYesno*)yesno)->PromptText;
        if (node == null || !node->NodeText.StringPtr.HasValue) return string.Empty;

        return AddonPrompt.ReadSelectYesnoText(yesno).Trim();
    }

    /// <summary>
    /// <see cref="AddonPrompt.LooksMidUpdate"/>，但命中時<b>寫一行 log</b>。
    /// </summary>
    /// <remarks>
    /// 🔴 這條路上靜默 <c>return false</c> 的話，「模組看到確認框卻一扇都按不下去」在實機 log 裡不會留下任何痕跡。
    /// 節流 1 秒，卡住時每秒一行就夠定案。
    /// </remarks>
    private static bool LooksMidUpdateNoisy(string prompt)
    {
        if (!AddonPrompt.LooksMidUpdate(prompt)) return false;

        if (Throttle.Pass("AutoFCWSDeliver-YesnoMidUpdate", 1_000))
            Svc.Log.Information(
                $"[AutoFCWSDeliver] 確認框的提示文字含替換字元（視窗記憶體變動中），這一幀不按：「{prompt}」");

        return true;
    }

    /// <summary>這扇確認框是不是「這條交納流程自己叫出來的」——只有這兩種才准自動按「是」。</summary>
    /// <remarks>
    /// 🔴 優質道具那一組刻意用<b>包含</b>而不是<b>逐字相等</b>。
    /// 包含比對比它寬鬆，不可能比它更難命中，而誤中的風險被前置條件壓得很低：
    /// 這一段只有「本模組剛按下交出鈕、還沒收尾」的數百毫秒會執行。
    /// </remarks>
    private bool IsFcwsFlowPrompt(string prompt)
    {
        if (prompt.Contains(FcwsPromptAnchor, StringComparison.Ordinal)) return true;

        foreach (var needle in highQualityPrompts)
        {
            if (prompt.Contains(needle, StringComparison.Ordinal)) return true;
        }

        return false;
    }

    /// <summary>
    /// 記下「這一幀看到一扇確認框」，同一扇連看好幾幀只算一扇。
    /// </summary>
    /// <remarks>
    /// 純診斷用（<see cref="yesnoSeenThisItem"/>），不參與任何流程判斷——
    /// 所以位址被重用而少數一扇也不會影響交納結果。
    /// </remarks>
    private void NoteYesnoSeen(AtkUnitBase* yesno)
    {
        var address = (nint)yesno;
        if (address == lastYesnoSeenAddress) return;

        lastYesnoSeenAddress = address;
        yesnoSeenThisItem++;
    }

    /// <summary>按下交納並等待視窗（含 HQ 確認）全部關閉。</summary>
    private bool? ConfirmRequest()
    {
        if (GetWorkshopTerritory() == null)
        {
            Svc.Log.Information($"[AutoFCWSDeliver] 交納途中讀不到公會工坊（多半是已經離開工坊），停止本趟（本輪已交 {deliveredCount} 項）。");
            return null;
        }

        // 交納確認。台服按下「交出」之後最多會連著出現兩扇：
        // 「確定要交易優質道具嗎？」（含 HQ 時）與「確定要為合建設備提供○○×N嗎？」。
        var yesno = UiHelper.FindReadyAddon(UiHelper.SelectYesnoAddonName);
        if (yesno != null)
        {
            requestGoneSince = null;  // 還有確認框要處理，沉澱計時重來
            confirmStallSince = null; // 看得到窗就不算卡住
            NoteYesnoSeen(yesno);

            var prompt = ReadPrompt(yesno);
            // 🔴 讀出替換字元＝窗的記憶體正在變動，這幀不碰（下一幀重讀）。
            if (LooksMidUpdateNoisy(prompt))
                return false;

            // 🔴🔴 只按白名單裡的兩種框（合建交納框與含 HQ 素材時的優質道具框），理由：
            //    ① 這一段是「交出鈕按下之後」的數百毫秒，別的來源照樣可以在這期間彈確認框（組隊邀請、交易邀請…），
            //       無差別按「是」等於幫使用者做決定。
            //    ② 判準本身就是<b>診斷</b>：認不得的框會寫一行 Information 指名它，「看到卻沒按」不會整段靜默。
            if (prompt.Length == 0)
            {
                // 讀不出字＝判不出這是誰的框（窗剛建好還沒填字，或文字節點不在）。判不出來就不按。
                if (Throttle.Pass("AutoFCWSDeliver-YesnoNoText", 1_000))
                    Svc.Log.Information("[AutoFCWSDeliver] 確認框讀不到提示文字，這一幀不按（下一幀重讀）。");

                return false;
            }

            if (!IsFcwsFlowPrompt(prompt))
            {
                if (Throttle.Pass("AutoFCWSDeliver-YesnoUnknown", 3_000))
                    Svc.Log.Information(
                        $"[AutoFCWSDeliver] 交納途中出現不認得的確認框，本模組不按（留給使用者或 YesAlready 決定）：" +
                        $"「{prompt}」。判準：含「{FcwsPromptAnchor}」字樣，或 {string.Join(" | ", highQualityPrompts)}");

                return false;
            }

            if (!deliverConfirmLease.IsExclusive) return StopForDeliverConfirm(prompt);

            // 回 false＝守衛擋下（這一扇剛按過、還沒觀察到它收掉），下一輪再來，不是錯誤。
            if (UiHelper.TryFireCallback(yesno, true, 0))
            {
                yesnoPressedThisItem++;
                Svc.Log.Debug(
                    $"[AutoFCWSDeliver] 交納確認框按下「是」（本項第 {yesnoPressedThisItem} 扇）：「{prompt}」。");
            }

            return false;
        }

        var request = UiHelper.FindReadyAddon("Request");
        if (request != null)
        {
            requestGoneSince = null;

            // 🔴 「交納視窗還開著、卻一扇確認框都找不到」正常只有幾幀。卡住時這行是唯一能離線定案的線索
            //    （分不出模組死了、看不到窗、還是在等別的），寫 Information。
            confirmStallSince ??= DateTime.UtcNow;
            if ((DateTime.UtcNow - confirmStallSince.Value).TotalMilliseconds >= ConfirmStallReportMs &&
                Throttle.Pass("AutoFCWSDeliver-ConfirmStall", ConfirmStallReportMs))
            {
                Svc.Log.Information(
                    $"[AutoFCWSDeliver] 交納視窗仍開著但找不到任何確認框已 " +
                    $"{(int)(DateTime.UtcNow - confirmStallSince.Value).TotalMilliseconds} 毫秒" +
                    $"（本項已看到 {yesnoSeenThisItem} 扇、按下 {yesnoPressedThisItem} 扇）。" +
                    $"SelectYesno 實例：{UiHelper.DescribeAddonInstances(UiHelper.SelectYesnoAddonName)}");
            }

            if (Throttle.Pass("AutoFCWSDeliver-HandOver", 500))
            {
                if (!ConfirmTextAdvanceIdle(starting: false)) return null;
                if (!KeepDeliverConfirmExclusive()) return StopForDeliverConfirm(string.Empty);
                UiHelper.ClickButton(request, ((AddonRequest*)request)->HandOverButton);
            }

            return false;
        }

        confirmStallSince = null;

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
        //    在那一幀就回 true 的話，解析步驟馬上跑下一輪、對同一項再送一次合成事件，
        //    清單當然沒變 ⇒ 保險絲誤觸。
        //    ⇒ 要求「連續 ConfirmSettleMs 都沒有任何一扇冒出來」才收。
        requestGoneSince ??= DateTime.UtcNow;
        if ((DateTime.UtcNow - requestGoneSince.Value).TotalMilliseconds < ConfirmSettleMs)
            return false;

        deliveredCount++;

        // 🔴 這一行是使用者唯一看得到的歸因：分辨「這一扇是本模組按的」還是「別人（YesAlready
        //    的強制熱鍵、或使用者自己點）按掉的」。兩種都會讓交納成功，但只有前者代表模組正常運作
        //    ——「本模組按下 0 扇」正是靠這行才看得出來。
        // 🔴 第三種情況只能說「本模組沒看到」，不能說「沒出現」：輪詢每幀一次，別人在同一幀就按掉的框我們本來就看不到。
        var attribution = yesnoPressedThisItem > 0
            ? $"確認框已出現，本模組按下 {yesnoPressedThisItem} 扇"
            : yesnoSeenThisItem > 0
                ? $"確認框已出現但本模組一扇都沒按到（看到 {yesnoSeenThisItem} 扇；由 YesAlready 或手動處理）"
                : "本模組全程沒看到確認框（多半是 YesAlready 或使用者在同一幀就按掉了，也可能這一項本來就不跳確認框——不代表確認框沒出現）";

        // 本模組按下至少一扇是正常節奏，逐件只記 Debug；一扇都沒按到才升 Information，歸因照樣看得見。
        var doneLine = $"[AutoFCWSDeliver] 第 {deliveredCount} 項交納完成（{attribution}）。";
        if (yesnoPressedThisItem > 0) Svc.Log.Debug(doneLine);
        else Svc.Log.Information(doneLine);
        return true;
    }

    /// <summary>
    /// 交完一項之後，等合建視窗的素材清單真的更新了再去解析下一項。
    /// </summary>
    /// <remarks>
    /// 🔴 沒有這一步的話，下一輪解析讀到的是<b>交納前</b>的 AtkValues：指紋沒變
    /// ⇒ 零進展保險絲把「還沒更新」誤判成「交納沒有生效」，而且會對同一項再送一次合成事件。
    /// </remarks>
    private bool? WaitListRefreshed()
    {
        // 這一項根本沒交出去，不必等清單——讓保險絲盡快算完它的三輪。
        if (!requestSeen) return true;

        listWaitStartedAt ??= DateTime.UtcNow;

        var addon = UiHelper.GetAddon("SubmarinePartsMenu");
        if (!UiHelper.IsReady(addon))
        {
            // 連續推進：合建設備選單已經出現＝這個階段交完了，不必等滿寬限。
            if (LoopEnabled && IsStationMenuOpen()) return true;

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
            Svc.Log.Debug(
                $"[AutoFCWSDeliver] 合建清單已更新（首項 Index={preDeliverFingerprint?.Index}、長度 " +
                $"{preDeliverFingerprint?.Count} → 首項 Index={fingerprint.Item1}、長度 {fingerprint.Item2}），" +
                $"進入下一項。指紋 {DescribeFingerprint(preDeliverFingerprint)} → {DescribeFingerprint(fingerprint)}。");
            return true;
        }

        if ((DateTime.UtcNow - listWaitStartedAt.Value).TotalMilliseconds < ListUpdateWaitMs)
            return false;

        Svc.Log.Information(
            $"[AutoFCWSDeliver] 等了 {ListUpdateWaitMs} 毫秒合建清單仍沒有變化，" +
            "交給零進展保險絲判斷是不是真的沒交出去。" +
            $"指紋 {DescribeFingerprint(preDeliverFingerprint)} → {DescribeFingerprint(fingerprint)}。");
        return true;
    }

    /// <summary>把清單指紋壓成一行（首項 Index／清單長度／道具 ID／持有數），純診斷用。</summary>
    private static string DescribeFingerprint((uint Index, int Count, uint ItemId, uint Owned)? fingerprint) =>
        fingerprint is null
            ? "（無）"
            : $"{fingerprint.Value.Index}/{fingerprint.Value.Count}/{fingerprint.Value.ItemId}/{fingerprint.Value.Owned}";

    /// <summary>解析合建視窗目前可交納（數量足夠且未交滿）的素材清單。</summary>
    private static List<DeliverItem> ParseDeliverables(AtkUnitBase* addon)
    {
        var result = new List<DeliverItem>();

        foreach (var row in ParseRows(addon))
        {
            if (row.Completed) continue;
            if (row.Owned < row.Required) continue; // 持有數不足

            result.Add(new DeliverItem(row.Index, row.ItemId, row.Required, row.Owned));
        }

        return result;
    }

    /// <summary>解析「還沒交滿、而且身上不夠」的素材（與可交納清單互斥）。</summary>
    private static List<ShortfallItem> ParseShortfalls(AtkUnitBase* addon)
    {
        var result = new List<ShortfallItem>();

        foreach (var row in ParseRows(addon))
        {
            if (row.Completed) continue;
            if (row.Owned >= row.Required) continue;

            result.Add(new ShortfallItem(row.ItemId, row.Required, row.Owned, row.Required - row.Owned));
        }

        return result;
    }

    /// <summary>解析合建視窗上的素材列（不過濾）。空清單＝視窗還沒準備好。</summary>
    private static List<MaterialRow> ParseRows(AtkUnitBase* addon)
    {
        var result = new List<MaterialRow>();

        // 🔴 光是判 addon 與長度還不夠。AtkValuesSpan 是 new Span<AtkValue>(AtkValues, AtkValuesCount)，它自己不判 AtkValues 這個欄位；
        // addon 拆解時 AtkValues 會先被釋放成 null、AtkValuesCount 卻可能還留著殘值，索引下去才對位址 0 解參考 ＝
        // AccessViolationException（corrupted-state exception，try/catch 攔不到）。
        // ⇒ 必須另外自判 AtkValues 欄位；讀不到就回空清單。
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

            result.Add(new MaterialRow(
                (uint)i, itemId, values[60 + i].UInt, values[72 + i].UInt, values[132 + i].UInt == 1));
        }

        return result;
    }
}

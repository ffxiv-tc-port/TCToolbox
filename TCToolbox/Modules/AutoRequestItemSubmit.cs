using System;
using System.Collections.Generic;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Memory;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 交易視窗（<c>Request</c>）自動繳交道具：自動把要求的道具填進格子，填滿後自動按下「交出」。
/// 機制：<c>AddonLifecycle</c> ＋ 對 <c>AgentNpcTrade</c> 送合成事件，
/// 交出鈕走既有的 <see cref="UiHelper.ClickButton"/>。零 hook、零特徵碼、不寫記憶體。
/// </summary>
/// <remarks>
/// 📌 <b>取代 DailyRoutines 的 <c>AutoRequestItemSubmit</c></b>。使用者裁決不走 TextAdvance 的
/// 主開關——那會連帶啟用對話跳過／過場 ESC／自動接任務，行為擴散不可接受。
/// <para>
/// 🔴 與 DR 的形狀差異（DR 是每幀無節流）：這裡照本 repo 既有樣板
/// （<see cref="AutoCustomDeliveryResult"/>）走 PostSetup ＋ PostDraw ＋
/// <see cref="Throttle"/>，每 <c>DelayMs</c> 才動一步。填格子本來就是一步一等的流程
/// （送出事件 → 遊戲開 <c>ContextIconMenu</c> → 選道具 → 下一格），無節流地灌事件只會
/// 讓同一格重送十幾次。
/// </para>
/// <para>
/// 🔴 <b>HQ 確認框用資料驅動判定，不寫死字串。</b>三個判準都是 <c>Addon</c> 表的列，
/// 台服 7.20 EXD 已核對（5450「繳交優質道具」、11514「遞交優質道具」、
/// 102434「確定要交易優質道具嗎？」）。⚠️ 而且**只有 <c>Request</c> 視窗開著時**才會去看
/// <c>SelectYesno</c>——沒有這個前置條件的話，任何長得像的確認框都會被按掉。
/// </para>
/// <para>
/// ⚠️ 這個模組會對<b>所有</b>交易視窗生效（任務繳交、部隊合建、雜項 NPC 交易），
/// 不分辨要交的是什麼。預設關閉。
/// </para>
/// </remarks>
public sealed unsafe class AutoRequestItemSubmit : TcModule
{
    public override string InternalName => "AutoRequestItemSubmit";
    public override string DisplayName => "自動繳交道具";

    public override string Description =>
        "NPC 交易／繳交視窗開啟時，自動把要求的道具填進格子並按下交出（含優質道具的確認框）。" +
        "⚠️ 對所有交易視窗一律生效，不分辨要交的是什麼道具，請自行確認要交的東西再開啟。";

    public override bool HasConfigUI => true;

    private const string AddonName = "Request";

    /// <summary>
    /// 判定「這個 SelectYesno 是不是優質道具確認框」用的 <c>Addon</c> 列。
    /// 🔑 用列號查客戶端自己的字串，所以跟語言無關，也不會因為台服用全形「？」而失效
    /// （台服 102434 實際是「確定要交易優質道具嗎<b>？</b>」，全形問號——這正是不該寫死字串的理由）。
    /// </summary>
    private static readonly uint[] HighQualityPromptRows = [5450, 11514, 102434];

    /// <summary>
    /// 品質偏好下拉選單的標籤。⚠️ 順序必須對齊 <see cref="HandInQualityPreference"/> 的列舉值
    /// （索引直接當列舉值用）。
    /// </summary>
    private static readonly string[] QualityPreferenceLabels =
        ["無偏好（交遊戲列的第一個）", "偏好優質品（HQ）", "偏好普通品（NQ）"];

    /// <summary>解析好的確認框字串。<see cref="OnEnable"/> 時建一次，空字串（台服未開放的列）不收。</summary>
    private readonly HashSet<string> highQualityPrompts = new(StringComparer.Ordinal);

    private AutoRequestItemSubmitConfig Config => Plugin.Instance.Config.RequestItemSubmit;

    protected override void OnEnable()
    {
        highQualityPrompts.Clear();
        foreach (var row in HighQualityPromptRows)
        {
            var text = Svc.Data.GetExcelSheet<Addon>().GetRowOrDefault(row)?.Text.ExtractText().Trim();
            if (!string.IsNullOrWhiteSpace(text)) highQualityPrompts.Add(text);
        }

        Svc.Log.Information(
            $"[{InternalName}] 優質道具確認框判準 {highQualityPrompts.Count}/{HighQualityPromptRows.Length} 條：" +
            $"{string.Join(" | ", highQualityPrompts)}");

        // PostSetup 有可能在視窗還沒真的可以互動時就送出，PostDraw 當重試——
        // 兩者共用同一個節流器，所以最多每 DelayMs 動一步，不會變成每幀灌事件。
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, AddonName, OnRequest);
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostDraw, AddonName, OnRequest);

        // ⚠️ 這兩條刻意常駐註冊（DR 是在 Request 開關時動態註冊／解除）。
        // 常駐比較不會留下懸空的監聽器，安全性改由處理常式裡的「Request 必須開著」前置條件保證，
        // 那個條件比 DR 的註冊時機更嚴格（DR 只保證「Request 曾經開過」）。
        // PostDraw 是必要的重試：PostSetup 那一刻確認框不一定已經可以互動，
        // 而只掛 PostSetup 的話錯過就沒有第二次機會。
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "SelectYesno", OnSelectYesno);
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostDraw, "SelectYesno", OnSelectYesno);
    }

    protected override void OnDisable()
    {
        Svc.AddonLifecycle.UnregisterListener(OnRequest);
        Svc.AddonLifecycle.UnregisterListener(OnSelectYesno);
        highQualityPrompts.Clear();
    }

    /// <summary>
    /// 優質道具確認框。三個條件全部成立才按「是」：
    /// 模組設定允許、<c>Request</c> 視窗正開著、提示文字命中 <see cref="HighQualityPromptRows"/>。
    /// 任何一條不成立就完全不動作（fail-closed）。
    /// </summary>
    private void OnSelectYesno(AddonEvent type, AddonArgs args)
    {
        if (!Config.ConfirmHighQuality) return;

        // PostDraw 每幀都會進來，所以節流放最前面——後面每一步都要取 addon、讀字串。
        if (!Throttle.Pass("AutoRequestItemSubmit-Yesno", Math.Max(200, Config.DelayMs))) return;

        // 🔴 沒有這個前置條件的話，任何長得像的確認框都會被按掉。
        if (!UiHelper.IsAddonReady(AddonName)) return;

        var addon = (AddonSelectYesno*)args.Addon.Address;
        if (!UiHelper.IsReady((AtkUnitBase*)addon)) return;
        if (addon->PromptText == null) return;

        // 用 MemoryHelper 解 SeString 再取 TextValue：兩邊（這裡與 Addon 表的 ExtractText）
        // 都是「拿掉 payload 之後的純文字」，比對基準才一致。
        var prompt = MemoryHelper.ReadSeString(&addon->PromptText->NodeText).TextValue.Trim();
        if (string.IsNullOrEmpty(prompt)) return;

        if (!highQualityPrompts.Contains(prompt))
        {
            // ⚠️ 這行是「完全比對」這個假設失效時唯一的徵兆（否則只會表現成「確認框不會自己按」）。
            if (Throttle.Pass("AutoRequestItemSubmit-PromptMiss", 10_000))
                Svc.Log.Information(
                    $"[{InternalName}] 交易中出現未認得的確認框，不動作：「{prompt}」" +
                    $"（目前判準：{string.Join(" | ", highQualityPrompts)}）");
            return;
        }

        UiHelper.FireCallback((AtkUnitBase*)addon, true, 0);
        Svc.Log.Information($"[{InternalName}] 已確認優質道具交易：「{prompt}」");
    }

    private void OnRequest(AddonEvent type, AddonArgs args)
    {
        try
        {
            if (!Throttle.Pass("AutoRequestItemSubmit-Step", Math.Max(200, Config.DelayMs))) return;
            if (ShouldYieldToFcwsDeliver()) return;

            var addon = (AddonRequest*)args.Addon.Address;
            if (!UiHelper.IsReady((AtkUnitBase*)addon)) return;

            // 交出鈕能按就按——遊戲只在所有欄位都填滿時才啟用它，所以「按得動」同時就是「填完了」的判準。
            //
            // 🔴 判斷「能不能按」一律交給 UiHelper.ClickButton，**不要自己先讀 IsEnabled**：
            // CS 的 AtkComponentButton.IsEnabled 是
            // `AtkComponentBase.OwnerNode->AtkResNode.NodeFlags.HasFlag(...)`，
            // 它對 OwnerNode **沒有任何 null 檢查**，先讀它等於自己開一個存取違規的口子
            // （而 AVE 是 .NET Core 的 corrupted-state exception，外面那圈 try/catch 攔不到）。
            // ClickButton 內部依序驗 addon／button／OwnerNode／IsEnabled／可見性／事件非 null，
            // 全部通過才送事件，回 false 就代表「現在按不動」——正好是我們要的分支條件。
            if (UiHelper.ClickButton((AtkUnitBase*)addon, addon->HandOverButton))
            {
                Svc.Log.Information($"[{InternalName}] 已按下交出鈕（欄位數 {addon->EntryCount}）。");
                return;
            }

            FillNextSlot();
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, $"[{InternalName}] 處理交易視窗時發生例外");
        }
    }

    /// <summary>
    /// 把還沒填的第一格填上。一次只處理一格：送出事件之後遊戲要開
    /// <c>ContextIconMenu</c> 讓人選同款道具的哪一份（NQ／HQ），選完才輪得到下一格。
    /// </summary>
    private void FillNextSlot()
    {
        var agent = AgentNpcTrade.Instance();
        if (agent == null) return;

        var manager = InventoryManager.Instance();
        if (manager == null) return;

        // HandIn 是交易視窗自己的暫存容器：已經填進格子的道具會出現在這裡。
        var container = manager->GetInventoryContainer(InventoryType.HandIn);
        if (container == null) return;

        var uiState = UIState.Instance();
        if (uiState == null) return;

        var requests = uiState->NpcTrade.Requests;

        // 🔴 Count 是遊戲填的 byte，不能直接拿來當迴圈上界：Items 是固定 5 格的內嵌陣列，
        // Count 若大於 5 就會讀到 ItemRequests 結構後面的記憶體。兩軸都收斂。
        var count = Math.Min((int)requests.Count, requests.Items.Length);
        if (count <= 0) return;

        for (var i = 0; i < count; i++)
        {
            // 容器大小同樣是遊戲說了算，不能假設它一定 ≥ count。
            if (i >= container->Size) break;

            var slot = container->GetInventorySlot(i);
            var wanted = requests.Items[i];

            // 這一格已經填好了 → 換下一格
            if (slot != null && slot->ItemId == wanted.ItemId) continue;

            // 道具選單還沒開 → 請遊戲替第 i 格開出可選道具清單
            if (!UiHelper.IsAddonReady("ContextIconMenu"))
            {
                UiHelper.SendAgentEvent(AgentId.NpcTrade, 0, 2, i, 0, 0);
                return;
            }

            // 選單開著 → 從候選裡挑一份（遊戲已經照要求篩過，含 HQ／收藏品條件）
            var itemId = PickTurnInItemId(agent);
            if (itemId == 0) return;

            UiHelper.SendAgentEvent(AgentId.NpcTrade, 1, 0, 0, itemId, 0u, 0);
            return;
        }
    }

    /// <summary>
    /// 候選道具可能待在哪些自有容器裡。<see cref="IsLiveInventorySlot"/> 用它來確認一個候選指標
    /// 真的指向現在活著的道具格。
    /// </summary>
    /// <remarks>
    /// ⚠️ 這份清單<b>寧可多列不要少列</b>：漏列一個容器只會讓品質偏好對那種道具不生效
    /// （退回既有行為，而且 log 會留下一行），多列一個容器則完全沒有壞處。
    /// </remarks>
    private static readonly InventoryType[] CandidateSourceContainers =
    [
        InventoryType.Inventory1, InventoryType.Inventory2,
        InventoryType.Inventory3, InventoryType.Inventory4,
        InventoryType.Crystals, InventoryType.KeyItems,
        InventoryType.EquippedItems,
        InventoryType.ArmoryMainHand, InventoryType.ArmoryOffHand,
        InventoryType.ArmoryHead, InventoryType.ArmoryBody, InventoryType.ArmoryHands,
        InventoryType.ArmoryWaist, InventoryType.ArmoryLegs, InventoryType.ArmoryFeets,
        InventoryType.ArmoryEar, InventoryType.ArmoryNeck, InventoryType.ArmoryWrist,
        InventoryType.ArmoryRings, InventoryType.ArmorySoulCrystal,
    ];

    /// <summary>
    /// 這個候選指標是不是「現在活著的自有道具格」。
    /// <b>只比對指標值，完全不解參考 <paramref name="candidate"/>。</b>
    /// </summary>
    /// <remarks>
    /// 🔴 這是 <see cref="PickTurnInItemId"/> 能安全走訪候選清單的<b>唯一理由</b>。
    /// 候選陣列有幾格有效是遊戲填的欄位說了算，而那個欄位在台服<b>沒有被離線驗證過</b>
    /// （<c>AgentNpcTrade</c> 在 FFXIVClientStructs 裡完全沒有特徵碼／虛擬表可以當定位錨點，
    /// 離線探測工具的校準閘門因此無從滿足）。如果它其實不是「候選數」，
    /// 多出來的格子可能留著上一次交易的舊指標，而解到失效指標是 AccessViolation——
    /// .NET Core 的 corrupted-state exception，<c>try/catch</c> 攔不到，遊戲當場關閉。
    /// <para>
    /// 🔑 所以這裡<b>反過來問</b>：不去相信清單長度，而是拿候選指標比對
    /// 「遊戲現在自己承認的每一個道具格位址」。比對只用指標值、不碰它指到的記憶體，
    /// 所以指標再爛都不會出事；比不中就當那一格不存在。
    /// </para>
    /// <para>
    /// 🔑 而且這裡<b>沒有引入任何新的原生層假設</b>：<c>InventoryManager.Instance()</c>、
    /// <c>GetInventoryContainer</c>、<c>Size</c>、<c>GetInventorySlot</c>
    /// 全都是 <see cref="FillNextSlot"/> 出貨前就在用的東西。
    /// </para>
    /// <para>
    /// 📌 成本：最壞情況約一千次指標比對，而呼叫端有 <see cref="Throttle"/> 擋著
    /// （最快每 <c>DelayMs</c> ≥200ms 一次），不是每幀。
    /// </para>
    /// </remarks>
    private static bool IsLiveInventorySlot(InventoryItem* candidate)
    {
        if (candidate == null) return false;

        var manager = InventoryManager.Instance();
        if (manager == null) return false;

        foreach (var type in CandidateSourceContainers)
        {
            var container = manager->GetInventoryContainer(type);
            if (container == null) continue;

            // 容器大小同樣是遊戲說了算；上界另外夾一次，免得壞掉的 Size 讓迴圈跑到天荒地老。
            var size = container->Size;
            if (size <= 0 || size > 1_000) continue;

            for (var i = 0; i < size; i++)
                if (container->GetInventorySlot(i) == candidate) return true;
        }

        return false;
    }

    /// <summary>
    /// 從遊戲給的候選清單裡挑一份要交出去的道具，回傳可直接送進事件的「帶旗標道具 ID」（0＝挑不到）。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>這是偏好不是限定</b>：偏好的那一種挑不到時退回第一個候選，不會卡住不交。
    /// <para>
    /// 🔴 <b>第 0 格以外的候選一律先過 <see cref="IsLiveInventorySlot"/> 才解參考。</b>
    /// 第 0 格維持本設定加入之前的做法（直接解參考），所以既有行為與既有風險原封不動；
    /// 新增的走訪範圍則完全不倚賴「<c>SelectedTurnInSlotItemOptions</c> 真的是候選數」
    /// 這個未經台服驗證的假設——它現在只是個便宜的上界，就算它是錯的，
    /// 最壞結果也只是<b>挑不到偏好的品質而退回既有行為</b>，不會是存取違規。
    /// </para>
    /// <para>
    /// ⚠️ 候選上界 ≤ 1 或偏好為「無」（預設）時完全不進走訪迴圈，走的是<b>與本設定加入之前
    /// 逐字相同</b>的路徑。
    /// </para>
    /// </remarks>
    private uint PickTurnInItemId(AgentNpcTrade* agent)
    {
        var options = agent->SelectedTurnInSlotItemOptionValues;
        var preference = Config.QualityPreference;

        // 🔑 品質判定與要送出去的值同源：GetItemId() 回的是「帶旗標」的 ID，
        // HQ ＝本體 ID + 1000000（Dalamud 的 ItemUtil.IsHighQuality 就是這個判準），
        // 所以不必另外去讀 InventoryItem 的旗標欄位，也不會兩邊對不起來。
        static uint IdOf(InventoryItem* item) =>
            item == null || item->ItemId == 0 ? 0u : item->GetItemId();

        // 第 0 格：與本設定加入之前逐字相同的取法。
        var first = IdOf(options[0].Value);

        // ⚠️ 這個欄位只當上界用；它是不是真的「候選數」不影響安全性（見 IsLiveInventorySlot）。
        var count = Math.Clamp((int)agent->SelectedTurnInSlotItemOptions, 0, options.Length);
        if (count <= 1 || preference == HandInQualityPreference.None) return first;

        var wantHighQuality = preference == HandInQualityPreference.PreferHighQuality;
        if (first != 0 && ItemUtil.IsHighQuality(first) == wantHighQuality) return first;

        var rejected = 0;
        for (var i = 1; i < count; i++)
        {
            var candidate = options[i].Value;

            // 🔴 沒通過驗證就完全不碰它——連它的 ItemId 都不讀。
            if (!IsLiveInventorySlot(candidate)) { rejected++; continue; }

            var rawId = IdOf(candidate);
            if (rawId == 0) continue;

            if (ItemUtil.IsHighQuality(rawId) == wantHighQuality)
            {
                if (Throttle.Pass("AutoRequestItemSubmit-Quality", 10_000))
                    Svc.Log.Information(
                        $"[{InternalName}] 候選上界 {count}，偏好{(wantHighQuality ? "優質" : "普通")}品；" +
                        $"挑中第 {i} 個候選（道具 ID {rawId}）。");
                return rawId;
            }
        }

        // ⚠️ rejected 是「這個欄位到底是不是候選數」在實機上唯一的證據來源：
        // 它若長期是 0，代表上界與實際候選一致；若總是很大，代表那個欄位不是候選數
        // （而且因為有 IsLiveInventorySlot 擋著，這件事只會表現成偏好失效，不會是崩潰）。
        if (Throttle.Pass("AutoRequestItemSubmit-Quality", 10_000))
            Svc.Log.Information(
                $"[{InternalName}] 候選上界 {count}（其中 {rejected} 個不是現存的道具格，已略過），" +
                $"沒有偏好的{(wantHighQuality ? "優質" : "普通")}品；" +
                $"退回第一個候選（道具 ID {first}）照樣交出。");

        return first;
    }

    /// <summary>
    /// 部隊合建的交納視窗也是 <c>Request</c>，而 <see cref="AutoFCWSDeliver"/> 有自己的一整套
    /// 填格＋交出流程。兩個模組同時驅動同一扇視窗沒有好處，所以合建視窗開著、
    /// 而且那個模組是啟用狀態時，這裡讓路。
    /// <para>⚠️ 只在「那個模組真的開著」時讓路——否則合建視窗就變成完全沒人處理。</para>
    /// </summary>
    private bool ShouldYieldToFcwsDeliver()
    {
        if (!UiHelper.IsAddonReady("SubmarinePartsMenu")) return false;

        foreach (var module in Plugin.Instance.Modules)
        {
            if (module is not AutoFCWSDeliver) continue;
            if (!module.IsEnabled) return false;

            if (Throttle.Pass("AutoRequestItemSubmit-YieldFcws", 10_000))
                Svc.Log.Information($"[{InternalName}] 合建交納視窗開著，交由「合建素材一鍵全交」處理，本模組不動作。");
            return true;
        }

        return false;
    }

    public override void DrawConfig()
    {
        ImGui.SetNextItemWidth(180f);
        var delay = Config.DelayMs;
        if (ImGui.SliderInt("動作間隔（毫秒）", ref delay, 200, 3_000))
        {
            Config.DelayMs = delay;
            Plugin.Instance.Config.Save();
        }

        using (ImRaii.PushIndent())
            ImGui.TextDisabled("每填一格／按一次鈕之間的最短間隔，同時也是最短反應時間。");

        ImGui.SetNextItemWidth(240f);
        var preferenceIndex = (int)Config.QualityPreference;
        if (ImGui.Combo("品質偏好", ref preferenceIndex, QualityPreferenceLabels, QualityPreferenceLabels.Length))
        {
            Config.QualityPreference = (HandInQualityPreference)preferenceIndex;
            Plugin.Instance.Config.Save();
        }

        using (ImRaii.PushIndent())
        {
            ImGui.TextDisabled(Config.QualityPreference switch
            {
                HandInQualityPreference.PreferHighQuality =>
                    "同一款道具有普通品與優質品都能交時，優先交出優質品。",
                HandInQualityPreference.PreferNormalQuality =>
                    "同一款道具有普通品與優質品都能交時，優先交出普通品（把優質品留著）。",
                _ => "不挑，交出遊戲列在最前面的那一份——可能是普通品也可能是優質品。",
            });
            ImGui.TextDisabled("這是偏好不是限定：偏好的那一種沒有時會退回另一種照樣交出，不會卡住。");
            ImGui.TextDisabled("繳交本身就要求優質品時，遊戲只會列出優質品，此設定無從發揮。");
        }

        var confirmHq = Config.ConfirmHighQuality;
        if (ImGui.Checkbox("自動確認優質道具的交易確認框", ref confirmHq))
        {
            Config.ConfirmHighQuality = confirmHq;
            Plugin.Instance.Config.Save();
        }

        using (ImRaii.PushIndent())
        {
            ImGui.TextDisabled("關閉時交易照樣自動填格與交出，只有「確定要交易優質道具嗎？」");
            ImGui.TextDisabled("這扇確認框留給你自己按。判準取自遊戲介面用語，不是寫死的字串。");
            ImGui.TextDisabled(highQualityPrompts.Count == 0
                ? "⚠️ 目前一條判準都沒解析到，確認框不會自動按。"
                : $"目前判準（{highQualityPrompts.Count} 條）：{string.Join(" / ", highQualityPrompts)}");
        }
    }
}

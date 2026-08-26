using System;
using System.Collections.Generic;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Memory;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
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
/// ⚠️ 預設關閉。開啟後也只在<b>任務交付</b>時接手 —— 早期版本是對所有交易視窗一律生效，
/// 那個行為已經在 2026-08-07 因為誤觸理符繳交與以物易物而收掉（見下）。
/// </para>
/// <para>
/// 🔴 <b>只在「確定是任務交付」時才動作</b>（見 <see cref="IsQuestTurnIn"/>）。
/// <c>Request</c> 這扇視窗被遊戲重複使用在任務交付、理符繳交、部隊合建交納、
/// 以物易物……等好幾種情境上，而視窗本身看不出自己是誰開的。
/// 2026-08-07 實機 log 直證兩件事：①理符繳交視窗一開，本模組在 43 毫秒內就按下了交出鈕
/// （<c>ClickButton</c> 回 true，代表那顆鈕在<b>一格都還沒填</b>時就是「可按」的），
/// 空手交出被遊戲當成取消，NPC 隨即回「你怎麼了？做好的東西忘記拿了？」，十次皆然；
/// ②以物易物視窗開著時，本模組按下了三次【交換】——<b>那是不可逆的資產損失</b>。
/// 因此判準改成白名單：認得出是任務交付才接手，認不出就完全不動作。
/// </para>
/// </remarks>
public sealed unsafe class AutoRequestItemSubmit : TcModule
{
    public override string InternalName => "AutoRequestItemSubmit";
    public override string DisplayName => "自動繳交道具";

    public override string Description =>
        "任務交付視窗開啟時，自動把要求的道具填進格子並按下交出（含優質道具的確認框）。" +
        "🔴 只在確認得出是「任務交付」時才動作：理符繳交、以物易物、代幣／收藏品兌換、" +
        "部隊合建交納一律不碰（那些按下去可能換掉不該換的東西）。判斷不出來時也不動作。";

    public override ModuleCategory Category => ModuleCategory.Company;

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

        // 🔴 確認框也吃同一張白名單：不是任務交付就完全不碰。
        // 只做一半（不填格卻幫忙按確認）會把別人的流程按到錯的地方，比完全不動作更糟。
        if (!IsQuestTurnIn()) return;

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

            // 🔴 白名單必須排在按鈕之前 —— 這扇視窗的交出鈕在一格都沒填時就是可按的，
            // 慢一步判定等於已經把別人的交易按下去了。
            if (!IsQuestTurnIn()) return;

            var addon = (AddonRequest*)args.Addon.Address;
            if (!UiHelper.IsReady((AtkUnitBase*)addon)) return;

            // 🔴 先確認「真的填滿了」再按。
            //
            // 「按得動 ⇒ 填完了」這個假設是錯的：2026-08-07 實機直證，理符繳交視窗開啟後
            // 43 毫秒（約 2 幀）ClickButton 就回 true，而當時一格都還沒填，空手交出被遊戲
            // 當成取消。因為這裡是「先試按、按不動才填」，那一按就 return，
            // FillNextSlot() 從頭到尾沒被呼叫過 —— 十次繳交十次失敗。
            // 上面的白名單只是把這個錯誤假設關進較小的籠子裡，並沒有修正它本身。
            if (!AreAllRequestedItemsHandedIn()) { FillNextSlot(); return; }

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
    /// 交易視窗要求的每一項，是不是都已經真的躺在 <c>HandIn</c> 暫存容器裡（含數量）。
    /// 這是按下交出鈕的<b>前置條件</b>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 🔴 <b>為什麼需要這道守衛</b>：本模組原本拿「交出鈕按得動」當「格子填滿了」的判準。
    /// 那個假設是錯的 —— 2026-08-07 實機 log 直證，理符繳交視窗開啟後 43 毫秒
    /// <see cref="UiHelper.ClickButton"/> 就回 true（代表該鈕的 <c>NodeFlags.Enabled</c>
    /// 真的是設起來的），而當時一格都還沒填；空手交出被遊戲判成取消，
    /// NPC 隨即回「你怎麼了？做好的東西忘記拿了？」。
    /// </para>
    /// <para>
    /// 🔑 <b>這道守衛對「現在會成功的路徑」是 no-op</b>：交出成功的那一刻，
    /// 依定義每一格都已經填好了，所以這裡必然全部通過、必然照按。
    /// 它唯一擋掉的是「還沒填完就按」——也就是現在唯一會失敗的那條路徑。
    /// </para>
    /// <para>
    /// 🔴 <b>讀不到就別按</b>：容器／要求清單任何一項取不到、或數量對不上，一律回
    /// <c>false</c>，交給 <see cref="FillNextSlot"/> 去補，下一輪再試。
    /// 失敗形式是「多等一輪」，不是「空手交出」。
    /// </para>
    /// <para>
    /// ⚠️ 這裡<b>沒有引入任何新的原生層假設</b>：<c>InventoryManager.Instance()</c>、
    /// <c>GetInventoryContainer(HandIn)</c>、<c>Size</c>、<c>GetInventorySlot</c>、
    /// <c>UIState.NpcTrade.Requests</c> 全都是 <see cref="FillNextSlot"/> 出貨前就在用的東西，
    /// 邊界收斂也照抄它的形狀。
    /// </para>
    /// </remarks>
    private bool AreAllRequestedItemsHandedIn()
    {
        var manager = InventoryManager.Instance();
        if (manager == null) return false;

        // HandIn 是交易視窗自己的暫存容器：已經填進格子的道具會出現在這裡。
        var container = manager->GetInventoryContainer(InventoryType.HandIn);
        if (container == null) return false;

        var uiState = UIState.Instance();
        if (uiState == null) return false;

        var requests = uiState->NpcTrade.Requests;

        // 🔴 與 FillNextSlot 同一組收斂：Count 是遊戲填的 byte，Items 是固定 5 格的內嵌陣列，
        // Count 若大於 5 就會讀到 ItemRequests 結構後面的記憶體。兩軸都收斂。
        var count = Math.Min((int)requests.Count, requests.Items.Length);

        // 要求清單還沒讀到 ⇒ 無從確認填滿了沒 ⇒ 不按。
        if (count <= 0)
        {
            if (Throttle.Pass("AutoRequestItemSubmit-NotFilled", 10_000))
                Svc.Log.Information(
                    $"[{InternalName}] 交易要求清單還讀不到（Count={requests.Count}），先不按交出鈕。");
            return false;
        }

        for (var i = 0; i < count; i++)
        {
            var wanted = requests.Items[i];

            // 容器大小同樣是遊戲說了算，不能假設它一定 ≥ count。
            if (i >= container->Size)
            {
                if (Throttle.Pass("AutoRequestItemSubmit-NotFilled", 10_000))
                    Svc.Log.Information(
                        $"[{InternalName}] 第 {i + 1} 項（道具 {wanted.ItemId}）超出暫存容器大小 " +
                        $"{container->Size}，先不按交出鈕。");
                return false;
            }

            var slot = container->GetInventorySlot(i);
            if (slot == null || slot->ItemId != wanted.ItemId)
            {
                if (Throttle.Pass("AutoRequestItemSubmit-NotFilled", 10_000))
                    Svc.Log.Information(
                        $"[{InternalName}] 第 {i + 1} 項還沒填：要的是道具 {wanted.ItemId}，" +
                        $"格子裡是 {(slot == null ? "空的" : slot->ItemId.ToString())}，先不按交出鈕。");
                return false;
            }

            if (slot->Quantity < wanted.RequiredQuantity)
            {
                if (Throttle.Pass("AutoRequestItemSubmit-NotFilled", 10_000))
                    Svc.Log.Information(
                        $"[{InternalName}] 第 {i + 1} 項（道具 {wanted.ItemId}）數量不足：" +
                        $"已放 {slot->Quantity}／需要 {wanted.RequiredQuantity}，" +
                        $"還差 {wanted.RequiredQuantity - slot->Quantity}，先不按交出鈕。");
                return false;
            }
        }

        return true;
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

    /// <summary>
    /// 已知會共用 <c>Request</c> 視窗、而且「按下去就回不來」的視窗。開著就一律不動作。
    /// </summary>
    /// <remarks>
    /// ⚠️ 這只是<b>保險</b>，不是主判準 —— 主判準是 <see cref="IsQuestTurnIn"/> 的白名單。
    /// 黑名單永遠列不完（理符、以物易物，之後還會有別的），而每漏一個的代價都是使用者的道具，
    /// 所以真正擋住風險的是「非任務交付就不動作」，這張表只負責在白名單萬一誤判時再擋一層。
    /// </remarks>
    private static readonly string[] NeverActWhileOpen =
    [
        "ShopExchangeItem",   // 以物易物（潛水艇零件等）：按下的是【交換】，換錯不可逆
        "ShopExchangeCoin",   // 代幣兌換
        "InclusionShop",      // 道具交易（羅薇娜商會等）
        "CollectablesShop",   // 收藏品交易
    ];

    /// <summary>
    /// 🔴 <b>白名單：只有「確定是任務（Quest）交付」時才接手，其餘一律不動作。</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// 🔴 <b>為什麼是白名單而不是逐個排除</b>：<c>Request</c> 這扇視窗被遊戲重複使用在
    /// 任務交付、理符繳交、部隊合建交納、以物易物……等好幾種情境上，而
    /// <see cref="AddonRequest"/> 本身完全看不出自己是誰開的。逐個排除的黑名單永遠列不完，
    /// 而漏掉一個的代價是<b>不可逆的資產損失</b>（2026-08-07 實機：以物易物視窗被按了三次
    /// 【交換】）。所以改成「認得出是任務交付才動作」，認不出就讓使用者自己按。
    /// </para>
    /// <para>
    /// 🔴 <b>失敗形式一律是「不動作」</b>：任何一個環節取不到資料、或事件對不上已接的任務，
    /// 都回 <c>false</c>。少做一次自動化的代價是多按一下；做錯一次的代價是道具沒了。
    /// </para>
    /// <para>
    /// 🔑 <b>判準本身是自我驗證的</b>：不只要求「目前的事件是任務事件」
    /// （<see cref="EventHandlerContent.Quest"/>），還要求那個事件的
    /// <c>EntryId</c> 真的等於玩家<b>現在接著</b>的某個任務編號。
    /// 任務事件的 ID 就是 <c>0x10000 | 任務編號</c>——這條規則在
    /// <c>EventFramework.GetEventHandlerById(ushort)</c> 的多載裡寫得很明白
    /// （<c>id | 0x10000</c>），而 <see cref="EventHandlerContent.Quest"/> 正好是 <c>0x0001</c>。
    /// 兩段都吻合才算數，所以就算 <c>EventState</c> 的語意跟預期不同，
    /// 也幾乎不可能湊巧命中——<b>對不上就是不動作</b>，倒向安全的那一邊。
    /// </para>
    /// <para>
    /// ⚠️ <b>已知的縮減</b>：理符繳交、部隊合建交納、以物易物、代幣／收藏品兌換，
    /// 從此一律不再自動處理（那些本來就不該由本模組接手）。
    /// </para>
    /// </remarks>
    private bool IsQuestTurnIn()
    {
        // 保險：已知不可逆的視窗開著就直接放棄，連白名單都不必看。
        foreach (var addonName in NeverActWhileOpen)
        {
            if (!UiHelper.IsAddonReady(addonName)) continue;

            if (Throttle.Pass("AutoRequestItemSubmit-NeverAct", 10_000))
                Svc.Log.Information(
                    $"[{InternalName}] 偵測到「{addonName}」開著（按下去不可逆），本模組不動作。");
            return false;
        }

        var questManager = QuestManager.Instance();
        var framework = EventFramework.Instance();
        if (questManager == null || framework == null)
        {
            if (Throttle.Pass("AutoRequestItemSubmit-NotQuest", 30_000))
                Svc.Log.Information($"[{InternalName}] 取不到任務／事件狀態，無法確認是任務交付，本模組不動作。");
            return false;
        }

        var event1 = framework->EventState1.EventId;
        var event2 = framework->EventState2.EventId;

        if (TryMatchAcceptedQuest(questManager, event1, out var questId) ||
            TryMatchAcceptedQuest(questManager, event2, out questId))
        {
            if (Throttle.Pass("AutoRequestItemSubmit-Quest", 30_000))
                Svc.Log.Information(
                    $"[{InternalName}] 判定為任務交付（任務編號 {questId}；" +
                    $"事件 1={Describe(event1)}、事件 2={Describe(event2)}），接手處理。");
            return true;
        }

        // 📌 這行是「白名單到底認不認得出任務交付」在實機上唯一的證據來源。
        // 它若在真的交任務時還是印出來，代表 EventState 的語意跟這裡的假設不同，
        // 到時候照著印出來的事件值改判準即可（而在那之前，模組是「不動作」而不是「亂動作」）。
        if (Throttle.Pass("AutoRequestItemSubmit-NotQuest", 30_000))
            Svc.Log.Information(
                $"[{InternalName}] 交易視窗不是任務交付，本模組不動作。" +
                $"（事件 1={Describe(event1)}、事件 2={Describe(event2)}；" +
                $"目前接著 {questManager->NumAcceptedQuests} 個任務）");

        return false;
    }

    /// <summary>
    /// 這個事件是不是「玩家現在接著的某個任務」。<c>ContentId</c> 與
    /// <c>EntryId</c> 兩段都要吻合才算。
    /// </summary>
    private static bool TryMatchAcceptedQuest(QuestManager* questManager, EventId eventId, out ushort questId)
    {
        questId = 0;
        if (eventId.ContentId != EventHandlerContent.Quest) return false;

        var entryId = eventId.EntryId;
        if (entryId == 0) return false;

        // 🔴 逐格比對 NormalQuests，不呼叫 QuestManager.IsQuestAccepted ——
        // 後者是特徵碼定位的遊戲函式，台服的特徵碼一律先假設會失效，而且失效是靜默的。
        // 這裡只讀欄位，沒有任何特徵碼相依。
        var quests = questManager->NormalQuests;
        for (var i = 0; i < quests.Length; i++)
        {
            if (quests[i].QuestId != entryId) continue;
            questId = entryId;
            return true;
        }

        return false;
    }

    /// <summary>把事件 ID 印成人看得懂的樣子（診斷用）。</summary>
    private static string Describe(EventId eventId) =>
        $"{eventId.ContentId}#{eventId.EntryId}(0x{eventId.Id:X})";

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

        ImGui.Separator();
        ImGui.TextDisabled("只在確認得出是「任務交付」時才動作，判斷不出來就完全不動作。");
        using (ImRaii.PushIndent())
        {
            ImGui.TextDisabled("同一扇交易視窗，遊戲也拿來做理符繳交、以物易物、代幣兌換與");
            ImGui.TextDisabled("部隊合建交納，而視窗本身看不出自己是誰開的。那些情境按錯的代價");
            ImGui.TextDisabled("是換掉不該換的道具且無法還原，所以認不出來時一律讓你自己按。");
            ImGui.TextDisabled("每次沒有接手時都會在記錄裡寫明原因（Information 等級）。");
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Memory;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using TCToolbox.Core;
using GameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;
using ValueType = FFXIVClientStructs.FFXIV.Component.GUI.ValueType;

namespace TCToolbox.Modules;

/// <summary>
/// 自動園圃作業：收穫／護理／施肥批次，與播種（選定種子＋土壤）批次。
/// 機制：ObjectTable 找出周圍園圃地壟（EObj 2003757），以
/// TargetSystem->InteractWithObject 逐格互動（不使用封包偽造），
/// SelectString 選項以文字比對（文字來源 custom/001/CmnDefHousingGardeningPlant_00151 表，
/// 走 PopupMenu 條目、避開台服首行標題偏移陷阱），tick 狀態機逐步推進。
/// 參考 DailyRoutines AutoGardensWork 設計重寫；DR 的 EventStart 封包互動已改為標準物件互動。
/// </summary>
public sealed unsafe partial class AutoGardensWork : TcModule
{
    public override string InternalName => "AutoGardensWork";
    public override string DisplayName => "自動園圃作業";
    public override string Description =>
        "站在自家（或部隊）庭院的園圃、或房屋內的花盆旁，一鍵批次收穫／護理／施肥附近所有地壟與花盆；" +
        "亦可選定種子與土壤後批次播種。距離太遠或狀態不符的會自動跳過。" +
        "另有「自動整理」：先讀出每一格種了什麼、成熟了沒、枯萎了沒，再依你設定的策略" +
        "（非目標作物怎麼辦、要不要施肥、枯萎的要不要清掉）逐格決定動作，最後把該重種的種回去。"
        + "另有「自動重跑」（預設開）：只要你人就在園圃旁，每隔一段時間自己跑一輪。預設不走位、不傳送，戰鬥製作過場或別的外掛正在移動時一律讓開。"
        + "想讓它自己在園圃之間移動的話，另有「自動走位」（預設關，需要 vnavmesh）：只在目前這張圖裡走地面路線，不傳送、不跨區、不上坐騎，走不到就安靜放棄。"
        + "指令：/tcgarden＝跑一輪自動整理，/tcgarden ui／status／stop／harvest／tend／fertilize／plant。";

    public override ModuleCategory Category => ModuleCategory.Company;

    /// <summary>
    /// 「手動觸發」分頁要不要收這個模組。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>這裡必須跟著「自動重跑」那個開關走，不能寫死成 <c>true</c>。</b>
    /// <see cref="TcModule.IsManualTrigger"/> 的判準只有一條：<b>開著但不去按它，遊戲行為完全不變</b>。
    /// </remarks>
    public override bool IsManualTrigger => !Config.AutoLoopEnabled;

    public override bool HasConfigUI => true;

    /// <summary>庭院園圃地壟 EObj（EObjName 2003757「園圃」，CustomTalk 721047）。</summary>
    private const uint GardenPatchDataId = 2003757;

    /// <summary>
    /// 室內園藝花盆的 CustomTalk（721227）。與庭院地壟的 721047 是不同的 CustomTalk，
    /// 但兩者的 Script 指令完全相同（PLANT_TITLE→Addon 6420、FC_AUTHORITY_SEEDING），
    /// 也就是同一套選單／播種流程，所以可以共用同一個狀態機。
    /// </summary>
    private const uint PotCustomTalkId = 721227;

    /// <summary>HousingFurniture.UsageType 的「園藝花盆」值；與 <see cref="PotCustomTalkId"/> 完全對應。</summary>
    private const byte PotUsageType = 11;

    /// <summary>互動距離上限（碼）；超出的跳過。</summary>
    private const float InteractRange = 6f;

    /// <summary>搜尋半徑（碼）：室外庭院與室內房間都用同一個值。</summary>
    private const float SearchRange = 30f;

    private const string GardeningTextSheet = "custom/001/CmnDefHousingGardeningPlant_00151";

    /// <summary>園圃動作；<see cref="Scan"/> 只互動讀取可用選項後取消，不改變任何狀態。</summary>
    public enum GardenAction
    {
        Harvest,
        Tend,
        Fertilize,
        Plant,

        /// <summary>處理掉（清除）作物。<b>不可回復</b>；只有「自動整理」在使用者明確選了那個策略時才會排入。</summary>
        Dispose,

        /// <summary>自動整理：先讀這一格的狀態，再依使用者的策略決定要做什麼。</summary>
        Auto,

        Scan,
    }

    /// <summary>可種植物件的種類：庭院地壟或室內花盆。</summary>
    public enum PatchKind
    {
        /// <summary>庭院園圃地壟（室外）。</summary>
        Plot,

        /// <summary>園藝花盆（室內家具）。</summary>
        Pot,
    }

    private sealed class PatchJob
    {
        public bool Skipped;

        /// <summary>是否已對此地壟發出互動（決定收尾時要不要等互動狀態結束）。</summary>
        public bool Interacted;

        /// <summary>這一格的 GameObjectId（重種佇列要用；只存 id，不存指標）。</summary>
        public ulong GameObjectId;

        /// <summary>Talk 讀到的狀態。<c>Unknown</c>＝沒讀到（一律不動它）。</summary>
        public PatchState State;

        /// <summary>Talk 讀到的作物 item id；0＝不明。</summary>
        public uint CropItemId;

        /// <summary>Talk 已經讀過了（多頁對話時只採信第一次讀到的那一句）。</summary>
        public bool StateCaptured;

        /// <summary>這一格實際要做的動作。非 Auto 的批次在排隊時就等於 action。</summary>
        public GardenAction Chosen;

        /// <summary>做完之後要不要把目標作物種回去（Auto 專用，排在同一輪的最後）。</summary>
        public bool Replant;

        /// <summary>決策理由（記錄與聊天回報用）。</summary>
        public string Reason = string.Empty;
    }

    private readonly TaskQueue queue = new();

    // 選單文字（啟用時自 Lumina 表載入；讀取失敗時保留台服 7.20 實測值）
    private string textCancel = "取消";
    private string textPlant = "播種";
    private string textFertilize = "施肥";
    private string textTend = "護理";
    private string textHarvest = "收穫";

    // ── 狀態台詞（同一張表的第 0／7～10 列）。這是作物狀態的唯一來源。──────────
    // 🔴 這幾行不是裝飾：本 pin 的 FFXIVClientStructs 沒有任何園圃作物欄位，
    //    互動時遊戲顯示的這句話就是「種了什麼、成熟了沒、枯萎了沒」的全部證據。
    //    fallback 是 2026-09-08 直讀台服 sqpack 取得的實際值（不是手打的）。
    private string textPlotEmpty = "地壟裡沒有種任何東西。";
    private string textStatusDead = "已經枯萎了……";
    private string textStatusVigorous = "正茁壯成長。";
    private string textStatusDepressed = "的狀態不太好……";
    private string textStatusRipe = "已經成熟了。";
    private string textDispose = "處理";

    private int doneCount;
    private int skippedCount;

    /// <summary>上一批（或上一次單格操作）的結果彙總，供 UI 與 IPC 查詢。</summary>
    private string lastSummary = string.Empty;

    /// <summary>各地壟最近一次 Scan 讀到的可用選項（不含「取消」）。狀態無法離線讀取，只能靠互動取得。</summary>
    private readonly Dictionary<ulong, List<string>> scannedActions = [];

    private List<(uint Id, string Name)>? seedItems;
    private List<(uint Id, string Name)>? soilItems;
    private List<(uint Id, string Name)>? fertilizerItems;
    private string seedSearch = string.Empty;
    private string soilSearch = string.Empty;
    private string fertilizerSearch = string.Empty;

    private AutoGardensWorkConfig Config => Plugin.Instance.Config.GardensWork;

    protected override void OnEnable()
    {
        LoadSheetTexts();
        queue.OnTimeout = step =>
        {
            lastSummary = $"步驟逾時已中止：{step}（完成 {doneCount} 格、跳過 {skippedCount} 格）。";
            Svc.Chat.PrintError($"[TC Toolbox] 園圃步驟逾時，批次已停止：{step}（已完成 {doneCount} 格）");
        };

        // 走位會發起 vnavmesh 的移動 ⇒ 借用共用的停止設施：註冊 /tcstop、並讓補送看門狗
        // 蓋住「按了停止、路徑還在背景計算、算完才開走」那幾秒。
        // 📌 引用計數的，走位沒打開也照樣 Acquire——它只決定指令與看門狗掛不掛得起來，
        //    本身不會讓任何東西動起來。
        NavStop.Acquire();
        walkStartedNav = false;
        lastWalkGiveUpReason = string.Empty;
        ResetWalkRound();

        Svc.Framework.Update += OnUpdate;
        Svc.ClientState.TerritoryChanged += OnTerritoryChanged;
    }

    protected override void OnDisable()
    {
        Svc.ClientState.TerritoryChanged -= OnTerritoryChanged;
        Svc.Framework.Update -= OnUpdate;
        queue.Abort();
        scannedActions.Clear();
        ResetAutoLoop();
        ResetMaterialShortage();

        // 使用者在我們發起的移動還在跑的時候關掉模組——是我們讓他跑起來的，就由我們收掉。
        // 🔴 順序不能顛倒：Release 會把 /tcstop 登出，先停再放才不會留下「還在走、但停不了」。
        //    （補送窗口本身會活過最後一次 Release，見 NavStop.Release。）
        StopWalkIfMoving();
        NavStop.Release();
        walkStartedNav = false;
    }

    /// <summary>換區後地壟 ObjectId 會重來，掃描結果一律作廢，避免拿到別座庭院的舊狀態。</summary>
    private void OnTerritoryChanged(ushort territoryType) => scannedActions.Clear();

    private void OnUpdate(IFramework framework)
    {
        queue.Tick();

        // 🔴 先 Tick 再評估迴圈：兩者的順序決定了「剛跑完的那一幀」算不算閒。
        //    反過來的話，上一輪的最後一步還在佇列裡，而迴圈已經看到 IsBusy 是 false。
        TickAutoLoop();

        // 缺料評估放在框架執行緒上（自己節流成每秒一次）：模組列與設定畫面只讀快取。
        // 🔴 那兩處都在 ImGui 的繪製路徑上，不可以在那裡掃背包與物件表。
        UpdateMaterialShortage();
    }

    /// <summary>遊戲字串一律走 Lumina sheet；此表無 EXDSchema 定義，用 RawRow 直讀。</summary>
    private void LoadSheetTexts()
    {
        try
        {
            var sheet = Svc.Data.GetExcelSheet<RawRow>(null, GardeningTextSheet);
            string Read(uint rowId, string fallback)
            {
                var text = sheet.GetRowOrDefault(rowId)?.ReadStringColumn(1).ExtractText();
                return string.IsNullOrWhiteSpace(text) ? fallback : text;
            }

            textCancel = Read(1, textCancel);
            textPlant = Read(2, textPlant);
            textFertilize = Read(3, textFertilize);
            textTend = Read(4, textTend);
            textHarvest = Read(6, textHarvest);
            textDispose = Read(5, textDispose);

            // 第 0／7~10 列是狀態台詞。表裡存的只有「句尾那段固定文字」，
            // 句首會變動的作物名是一個 item 巨集，執行期才會被展開成作物名。
            textPlotEmpty = Read(0, textPlotEmpty);
            textStatusDead = Read(7, textStatusDead);
            textStatusVigorous = Read(8, textStatusVigorous);
            textStatusDepressed = Read(9, textStatusDepressed);
            textStatusRipe = Read(10, textStatusRipe);
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, $"[{InternalName}] 讀取園圃選單文字表失敗，使用台服 7.20 預設值");
        }
    }

    private string ActionText(GardenAction action) => action switch
    {
        GardenAction.Harvest => textHarvest,
        GardenAction.Tend => textTend,
        GardenAction.Fertilize => textFertilize,
        GardenAction.Plant => textPlant,
        GardenAction.Dispose => textDispose,
        _ => textCancel,
    };

    #region 批次流程

    private void StartBatch(GardenAction action)
    {
        if (queue.IsBusy) return;

        if (!TryGetGardenPatches(out var patches, out var error))
        {
            Svc.Chat.PrintError($"[TC Toolbox] {error}");
            return;
        }

        if (action == GardenAction.Fertilize &&
            (Config.FertilizerItemId == 0 || FindInventoryItem(Config.FertilizerItemId) == null))
        {
            Svc.Chat.PrintError("[TC Toolbox] 請先在設定選擇肥料，且背包內須有存貨。");
            return;
        }

        if (action == GardenAction.Plant)
        {
            if (Config.SeedItemId == 0 || Config.SoilItemId == 0)
            {
                Svc.Chat.PrintError("[TC Toolbox] 請先在設定選擇種子與土壤。");
                return;
            }

            if (FindInventoryItem(Config.SeedItemId) == null || FindInventoryItem(Config.SoilItemId) == null)
            {
                Svc.Chat.PrintError("[TC Toolbox] 背包內沒有所選的種子或土壤。");
                return;
            }
        }

        doneCount = 0;
        skippedCount = 0;
        ResetWalkRound();

        foreach (var (patchId, _) in patches)
            EnqueuePatch(patchId, action, Config.FertilizerItemId, Config.SeedItemId, Config.SoilItemId, allowWalk: true);

        var plotCount = patches.Count(x => x.Kind == PatchKind.Plot);
        var potCount = patches.Count - plotCount;

        queue.Enqueue("彙總結果", () =>
        {
            lastSummary = $"園圃「{ActionText(action)}」批次完成：處理 {doneCount} 格、跳過 {skippedCount} 格"
                        + $"（地壟 {plotCount}、花盆 {potCount}）。";
            Svc.Chat.Print($"[TC Toolbox] {lastSummary}");
            return true;
        });
    }

    /// <param name="fertilizerItemId">施肥用；批次走設定值，IPC 走呼叫端指定值。</param>
    /// <param name="seedItemId">播種用種子。</param>
    /// <param name="soilItemId">播種用土壤。</param>
    /// <param name="allowWalk">
    /// 這一格允許先走過去嗎（還要看使用者有沒有打開走位設定）。
    /// 🔴 <b>預設 <see langword="false"/>，而且 IPC 那條路徑刻意不傳 <see langword="true"/>。</b>
    /// <c>TCToolbox.Gardening.*</c> 的契約是「一次一格、由呼叫端決策與推進」，
    /// 腳本作者呼叫 <c>Harvest(id)</c> 時角色突然自己走起來是他沒有要求的行為改變。
    /// （那條路徑本來就會在距離超過 <see cref="InteractRange"/> 時直接回一句失敗原因。）
    /// </param>
    private void EnqueuePatch(
        ulong gameObjectId, GardenAction action, uint fertilizerItemId, uint seedItemId, uint soilItemId,
        bool allowWalk = false)
    {
        // 🔴 只存 GameObjectId，不存 IGameObject 也不存位址：每一步都自己重查。
        //    本 pin 的 ObjectTable 包裝是每格預配、就地改寫 Address 的，跨幀持有會靜默換人。
        var job = new PatchJob { GameObjectId = gameObjectId, Chosen = action };

        // 🔴 關著的時候<b>連步驟都不排</b>，不是排一個立刻回 true 的空步驟——
        //    後者每一格都要多花一幀，而且會在「執行中：走到地壟旁」閃一下，
        //    讓沒開走位的人以為模組要走路了。
        if (allowWalk && WalkEnabled)
            EnqueueWalkToPatch(job);

        queue.Enqueue("互動地壟", () =>
        {
            var localPlayer = Svc.Objects.LocalPlayer;
            var obj = Svc.Objects.SearchById(gameObjectId);
            if (localPlayer == null || obj == null ||
                Vector3.Distance(localPlayer.Position, obj.Position) > InteractRange)
            {
                job.Skipped = true;
                skippedCount++;
                return true;
            }

            // 紅線替代：不用 EventStart 封包，走標準物件互動（AutoRetainer／Lifestream 台服生產同法）
            TargetSystem.Instance()->InteractWithObject((GameObject*)obj.Address, false);
            job.Interacted = true;
            return true;
        });

        queue.Enqueue("等待選單開啟", () =>
        {
            if (job.Skipped) return true;

            // 🔑 作物狀態的唯一來源就是這句話，而且點掉 Talk 之後就沒了 ——
            //    所以一定要在 ClickTalkIfOpen 之前讀。非 Auto 的動作也照讀（純唯讀、零成本），
            //    這樣事後看記錄時永遠知道當時那一格是什麼狀態。
            CaptureTalkState(job);

            if (UiHelper.ClickTalkIfOpen()) return false;
            return UiHelper.IsAddonReady("SelectString") ? true : false;
        }, 8_000);

        queue.Enqueue("選擇動作", () =>
        {
            if (job.Skipped) return true;

            var addon = UiHelper.GetAddon("SelectString");
            if (!UiHelper.IsReady(addon)) return false;

            var entries = UiHelper.GetSelectStringEntries(addon);
            // 🔴 選項文字讀出 U+FFFD ＝選單記憶體正在變動（建到一半／關閉中）：這一幀不判也不按，下一 tick 重讀。
            if (UiHelper.LooksMidUpdate(entries)) return false;
            var cancelIndex = entries.FindIndex(x => x.Contains(textCancel, StringComparison.Ordinal));

            // Scan：只記錄目前可用的選項後取消，不改變地壟狀態。
            // 地壟的作物狀態無法從記憶體離線讀取（ClientStructs 無生長階段欄位），
            // 「目前有哪些選項」就是唯一可靠的狀態訊號。
            if (action == GardenAction.Scan)
            {
                scannedActions[gameObjectId] =
                [
                    .. entries.Where(x => !string.IsNullOrWhiteSpace(x) &&
                                          !x.Contains(textCancel, StringComparison.Ordinal)),
                ];
                UiHelper.SelectStringEntry(addon, cancelIndex >= 0 ? cancelIndex : -1);
                return true;
            }

            // Auto：狀態已經在上一步從 Talk 讀好了，這裡才把它變成一個具體動作。
            if (action == GardenAction.Auto && !ResolveAutoAction(job, entries))
            {
                // 決定是「這一格什麼都不做」——取消離開，算成跳過。
                job.Skipped = true;
                skippedCount++;
                UiHelper.SelectStringEntry(addon, cancelIndex >= 0 ? cancelIndex : -1);
                return true;
            }

            var target = ActionText(job.Chosen);
            var index = entries.FindIndex(x => x.Contains(target, StringComparison.Ordinal));
            if (index < 0)
            {
                // 此地壟沒有這個動作（例如空地壟不能收穫、已成熟不能施肥）→ 取消並跳過
                job.Skipped = true;
                skippedCount++;
                UiHelper.SelectStringEntry(addon, cancelIndex >= 0 ? cancelIndex : -1);
                return true;
            }

            UiHelper.SelectStringEntry(addon, index);
            return true;
        }, 5_000);

        // 🔴 後續步驟必須在「排隊的那一刻」就決定排不排，而 Auto 要等真的互動、讀到 Talk
        //    之後才知道要做什麼 ⇒ Auto 一律把三組後續步驟全部排進來，每一組自己看
        //    job.Chosen 決定要不要動。沒被選中的那幾組是零成本的直接返回。
        if (action is GardenAction.Fertilize or GardenAction.Auto)
            EnqueueFertilizeSteps(job, fertilizerItemId);
        if (action is GardenAction.Plant or GardenAction.Auto)
            EnqueuePlantSteps(job, seedItemId, soilItemId);
        if (action == GardenAction.Auto)
            EnqueueDisposeSteps(job);

        queue.Enqueue("等待互動結束", () =>
        {
            // 因距離跳過（從未互動）的地壟直接放行；取消跳過的仍要等選單收起、互動狀態結束
            if (job.Skipped && !job.Interacted) return true;

            UiHelper.ClickTalkIfOpen();

            if (UiHelper.IsAddonReady("SelectString")) return false;
            if (Svc.Condition[ConditionFlag.OccupiedInQuestEvent]) return false;

            if (!job.Skipped)
            {
                doneCount++;
                // 🔴 重種必須等這一格真的做完才登記：中途逾時中止時佇列會被清掉，
                //    這裡沒跑到就不會有人去種——比「先登記後失敗」安全。
                if (job.Replant) replantQueue.Add(job.GameObjectId);
            }

            return true;
        }, 15_000);
    }

    private void EnqueueFertilizeSteps(PatchJob job, uint fertilizerItemId)
    {
        queue.Enqueue("開啟肥料選單", () =>
        {
            if (job.Skipped || job.Chosen != GardenAction.Fertilize) return true;
            if (UiHelper.IsAddonReady("SelectString")) return false; // 等選單收起

            var fertilizer = FindInventoryItem(fertilizerItemId);
            if (fertilizer == null)
            {
                Svc.Chat.PrintError("[TC Toolbox] 肥料已用完，批次停止。");
                return null;
            }

            // AgentModule.Instance() 走 UIModule，UI 尚未建立時回 null（CS 手寫實作）；
            // AgentInventoryContext.Instance() 與 GetAgentByInternalId 也都可能回 null。
            // 任何一個取不到就回 false ＝ 下個 tick 重試（真的取不到就由這一步的 8 秒逾時收），
            // 不 return null——那會中止整條批次佇列。
            var agentModule = AgentModule.Instance();
            if (agentModule == null) return false;

            var inventoryAgent = agentModule->GetAgentByInternalId(AgentId.Inventory);
            var inventoryContext = AgentInventoryContext.Instance();
            if (inventoryAgent == null || inventoryContext == null) return false;

            inventoryContext->OpenForItemSlot(
                fertilizer->Container,
                fertilizer->Slot,
                0,
                inventoryAgent->AddonId);
            return true;
        }, 8_000);

        queue.Enqueue("點選施肥", () =>
        {
            if (job.Skipped || job.Chosen != GardenAction.Fertilize) return true;

            var context = UiHelper.GetAddon("ContextMenu");
            if (!UiHelper.IsReady(context)) return false;

            // ContextMenu 版面：[0]＝項目筆數、[7+i]＝第 i 筆的文字。
            const int firstEntryIndex = 7;

            // 🔴 AtkValuesSpan 的實作是 new Span<AtkValue>(AtkValues, AtkValuesCount)：
            // 它自己不判 AtkValues 這個欄位，而 Span 的建構子也不驗指標。addon 拆解時
            // AtkValues 會先被釋放成 null、AtkValuesCount 卻可能還留著殘值，這個組合會
            // 合法建構出一個長度非零的 Span，連 Span 自己的邊界檢查都會放行，一直到真的
            // 索引下去才對位址 0 解參考 ＝ AccessViolationException（corrupted-state
            // exception，try/catch 攔不到，整個遊戲行程直接死）。
            // ⇒ IsReady() 與 Length 都擋不住這條，必須另外自判 AtkValues 欄位。
            // 取不到就回 false ＝ 下個 tick 重試，真的一直取不到由這一步的 8 秒逾時收。
            if (context->AtkValues == null) return false;

            var values = context->AtkValuesSpan;
            if (values.Length < firstEntryIndex) return false;

            var entryCount = (int)values[0].UInt;
            // 聲稱的筆數放不進實際陣列＝殘值或正在拆解（用減法寫，避免 firstEntryIndex + entryCount 溢位）。
            // entryCount 為 0 時照舊往下走，讓「沒有施肥選項」那條路徑跳過此格。
            if (entryCount < 0 || values.Length - firstEntryIndex < entryCount) return false;

            for (var i = 0; i < entryCount; i++)
            {
                var value = values[firstEntryIndex + i];
                if (value.Type is not (ValueType.String or ValueType.ManagedString) || value.String.Value == null)
                    continue;

                var text = MemoryHelper.ReadSeStringNullTerminated((nint)value.String.Value).TextValue;
                if (!text.Contains(textFertilize, StringComparison.Ordinal)) continue;

                UiHelper.FireCallback(context, true, 0, i, 0);
                return true;
            }

            // 沒有施肥選項 → 關閉選單並跳過此格
            job.Skipped = true;
            skippedCount++;
            context->Close(true);
            return true;
        }, 8_000);
    }

    private void EnqueuePlantSteps(PatchJob job, uint seedItemId, uint soilItemId)
    {
        queue.Enqueue("填入種子與土壤", () =>
        {
            if (job.Skipped || job.Chosen != GardenAction.Plant) return true;

            var addon = UiHelper.GetAddon("HousingGardening");
            if (!UiHelper.IsReady(addon)) return false;

            var soil = FindInventoryItem(soilItemId);
            var seed = FindInventoryItem(seedItemId);
            if (soil == null || seed == null)
            {
                Svc.Chat.PrintError("[TC Toolbox] 種子或土壤已用完，批次停止。");
                return null;
            }

            var agent = AgentHousingPlant.Instance();
            if (agent == null) return null;

            agent->SelectedItems[0] = new AgentHousingPlant.SelectedItem
            {
                ItemId = soil->ItemId,
                InventoryType = soil->Container,
                InventorySlot = (ushort)soil->Slot,
            };
            agent->SelectedItems[1] = new AgentHousingPlant.SelectedItem
            {
                ItemId = seed->ItemId,
                InventoryType = seed->Container,
                InventorySlot = (ushort)seed->Slot,
            };

            agent->ConfirmSeedAndSoilSelection();
            return true;
        }, 8_000);

        queue.Enqueue("確認播種", () =>
        {
            if (job.Skipped || job.Chosen != GardenAction.Plant) return true;

            if (UiHelper.IsAddonReady("SelectYesno"))
            {
                if (Throttle.Pass("AutoGardensWork-PlantYes", 300))
                    UiHelper.ClickSelectYesnoYes();
                return false;
            }

            return UiHelper.IsAddonReady("HousingGardening") ? false : true;
        }, 8_000);
    }

    #endregion

    #region 環境與物品

    /// <summary>
    /// 園藝花盆的顯示名稱集合，由 HousingFurniture 表推導（UsageType 11／CustomTalk 721227），
    /// 台服 7.20 為海濱／林間／綠洲花盆三種。刻意不寫死字串：名稱由 Lumina 以 client 語言回傳，
    /// 而且這樣會**排除**南瓜花盆之類純裝飾的花盆（它們 UsageType=0、不能種東西）。
    /// </summary>
    private static HashSet<string>? potNames;

    private static HashSet<string> GetPotNames()
    {
        if (potNames != null) return potNames;

        var names = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var furniture in Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.HousingFurniture>())
            {
                if (furniture.UsageType != PotUsageType && furniture.CustomTalk.RowId != PotCustomTalkId)
                    continue;

                var name = furniture.Item.ValueNullable?.Name.ExtractText();
                if (!string.IsNullOrWhiteSpace(name))
                    names.Add(name);
            }
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, "[AutoGardensWork] 讀取園藝花盆清單失敗，本次只處理庭院地壟");
        }

        potNames = names;
        return potNames;
    }

    /// <summary>
    /// 判斷物件是不是可種植的容器。庭院地壟以 EObj DataId 判定（精準）；
    /// 花盆是玩家擺放的家具，物件名稱＝家具道具名，所以比對由表推導出的名稱集合。
    /// </summary>
    private static bool TryClassify(Dalamud.Game.ClientState.Objects.Types.IGameObject obj, out PatchKind kind)
    {
        if (obj.ObjectKind == ObjectKind.EventObj && obj.BaseId == GardenPatchDataId)
        {
            kind = PatchKind.Plot;
            return true;
        }

        // 花盆可能以 EventObj 或 Housing 兩種 ObjectKind 出現（未能離線確認實際值），
        // 兩者都接受；真正的把關是名稱必須在園藝花盆集合內。
        if (obj.ObjectKind is ObjectKind.EventObj or ObjectKind.Housing &&
            GetPotNames().Contains(obj.Name.TextValue))
        {
            kind = PatchKind.Pot;
            return true;
        }

        kind = default;
        return false;
    }

    private static bool TryGetGardenPatches(out List<(ulong Id, PatchKind Kind)> patches, out string error)
    {
        patches = [];
        error = string.Empty;

        var localPlayer = Svc.Objects.LocalPlayer;
        if (localPlayer == null)
        {
            error = "無法取得玩家狀態。";
            return false;
        }

        var housing = HousingManager.Instance();
        if (housing == null || (housing->OutdoorTerritory == null && housing->IndoorTerritory == null))
        {
            error = "必須站在住宅區的庭院或房屋內才能使用園圃／花盆批次。";
            return false;
        }

        if (!HasGardenPermission(housing))
        {
            error = "這裡不是你擁有（或有權限）的房屋。";
            return false;
        }

        var found = new List<(ulong Id, PatchKind Kind, float Distance)>();
        foreach (var obj in Svc.Objects)
        {
            var distance = Vector3.Distance(localPlayer.Position, obj.Position);
            if (distance > SearchRange) continue;
            if (!TryClassify(obj, out var kind)) continue;
            found.Add((obj.GameObjectId, kind, distance));
        }

        patches = [.. found.OrderBy(x => x.Distance).Select(x => (x.Id, x.Kind))];

        if (patches.Count == 0)
        {
            error = "附近沒有園圃地壟或園藝花盆（請站到園圃／花盆旁）。";
            return false;
        }

        return true;
    }

    /// <summary>
    /// 指定半徑之內有沒有至少一格可種植的容器。
    /// </summary>
    /// <param name="range">
    /// 判定半徑（碼）。無人值守迴圈傳的是 <see cref="InteractRange"/>（走位關著＝只算站得到的），
    /// 走位打開時傳 <see cref="SearchRange"/>（走得過去的都算）。
    /// </param>
    /// <remarks>
    /// <para>📌 只問「有沒有」，找到第一個就收工；住宅區以外一律回 <see langword="false"/>。</para>
    /// </remarks>
    private static bool AnyPatchWithinRange(float range)
    {
        var localPlayer = Svc.Objects.LocalPlayer;
        if (localPlayer == null) return false;

        var housing = HousingManager.Instance();
        if (housing == null || (housing->OutdoorTerritory == null && housing->IndoorTerritory == null))
            return false;

        if (!HasGardenPermission(housing)) return false;

        foreach (var obj in Svc.Objects)
        {
            if (Vector3.Distance(localPlayer.Position, obj.Position) > range) continue;
            if (TryClassify(obj, out _)) return true;
        }

        return false;
    }

    /// <summary>
    /// 是否有權在此處園藝。主要依遊戲自己的權限判定 <c>HasHousePermissions</c>（室內外皆適用），
    /// 並保留原本的自宅 HouseId 比對作為後援，避免改動既有的室外行為。
    /// </summary>
    private static bool HasGardenPermission(HousingManager* housing)
    {
        if (housing->HasHousePermissions()) return true;

        var outdoorId = housing->GetCurrentHouseId().Id;
        if (outdoorId != 0 && IsOwnedHouse(outdoorId)) return true;

        var indoorId = housing->GetCurrentIndoorHouseId().Id;
        return indoorId != 0 && IsOwnedHouse(indoorId);
    }

    private static bool IsOwnedHouse(ulong houseId)
    {
        foreach (var estateType in Enum.GetValues<EstateType>())
        {
            if (estateType == EstateType.SharedEstate)
            {
                for (var i = 0; i < 2; i++)
                {
                    if (HousingManager.GetOwnedHouseId(estateType, i).Id == houseId)
                        return true;
                }
            }
            else if (HousingManager.GetOwnedHouseId(estateType).Id == houseId)
            {
                return true;
            }
        }

        return false;
    }

    private static InventoryItem* FindInventoryItem(uint itemId)
    {
        var manager = InventoryManager.Instance();
        if (manager == null || itemId == 0) return null;

        ReadOnlySpan<InventoryType> containers =
        [
            InventoryType.Inventory1, InventoryType.Inventory2,
            InventoryType.Inventory3, InventoryType.Inventory4,
        ];

        foreach (var type in containers)
        {
            var container = manager->GetInventoryContainer(type);
            // 🔴 判的是 Items 不是 GetInventorySlot 的回傳值：Items 為 null 而 Size > 0 時，
            //    GetInventorySlot 回的是「null + 偏移」這種非 null 的假指標，下面的判空一定通過，
            //    解參考就是攔不到的 AVE（corrupted-state exception，try/catch 無效）。
            //    樣板同 DiscardList.ScanMatches／TriadCardRecycle 的背包掃描。
            if (container == null || !container->IsLoaded || container->Items == null) continue;

            for (var i = 0; i < container->Size; i++)
            {
                var item = container->GetInventorySlot(i);
                if (item != null && item->ItemId == itemId && item->Quantity > 0)
                    return item;
            }
        }

        return null;
    }

    #endregion

    #region IPC 對外介面

    /// <summary>
    /// 給本機腳本（SND 等）用的細項操作層：一次一格，不提供「一鍵全自動」入口。
    /// 呼叫端負責決策與逐格推進；批次入口只保留在本模組的 UI 上。
    /// </summary>
    public bool IsBusy => queue.IsBusy;

    public string CurrentStepName => queue.CurrentStep ?? string.Empty;

    public int DoneCount => doneCount;

    public int SkippedCount => skippedCount;

    public string LastSummary => lastSummary;

    /// <summary>目前的策略要用、但拿不到的材料（空字串＝都拿得到）。給指令與 UI 用。</summary>
    /// <remarks>⚠️ 讀的是每秒更新一次的快取，模組停用時恆為空字串。</remarks>
    public string MaterialShortage => materialShortage;

    /// <summary>
    /// 聊天指令的批次入口。回傳空字串＝已經排進佇列，非空＝<b>沒有開始</b>的 zh-TW 理由。
    /// </summary>
    /// <param name="action">
    /// <see cref="GardenAction.Auto"/>＝一輪「自動整理」；其餘走原本的單一動作批次。
    /// </param>
    /// <remarks>
    /// 🔴 <b>這裡套了 <see cref="AutomationGate"/>，而設定畫面上的按鈕沒有套。</b>
    /// 兩者刻意不同：按按鈕的人正看著這個面板，而快捷列上的一顆巨集是可以在任何情況下被按到的
    /// （副本裡、別的外掛正在帶著角色走、剛按完全艦隊急停）。在那些時候開一連串互動選單
    /// 會把別人的流程打斷，而使用者根本不知道是誰動的手。
    /// </remarks>
    public string RunBatchFromCommand(GardenAction action)
    {
        if (!IsEnabled) return "自動園圃作業模組未啟用（請在 TC Toolbox 設定視窗開啟）。";

        if (queue.IsBusy)
            return $"園圃批次正在跑：{CurrentStepName}（完成 {doneCount} 格、跳過 {skippedCount} 格）。";

        if (AutomationGate.TryGetBusyReason(out var busy)) return $"現在不動手：{busy}。";

        if (action == GardenAction.Auto) StartAutoBatch();
        else StartBatch(action);

        return string.Empty;
    }

    /// <summary>環境是否可執行園圃操作；回傳空字串代表可用，否則為 zh-TW 失敗原因。</summary>
    public string GetUnavailableReason()
    {
        if (!IsEnabled) return "自動園圃作業模組未啟用（請在 TC Toolbox 設定視窗開啟）。";
        return TryGetGardenPatches(out _, out var error) ? string.Empty : error;
    }

    /// <summary>附近（30 碼內）可種植物件的 GameObjectId，依距離排序；環境不符時回空清單。含地壟與花盆。</summary>
    public List<ulong> GetNearbyPatchIds() =>
        TryGetGardenPatches(out var patches, out _) ? [.. patches.Select(x => x.Id)] : [];

    /// <summary>
    /// 只取指定種類的可種植物件；<paramref name="kind"/> 傳 "plot"（庭院地壟）或 "pot"（園藝花盆），
    /// 其他值視為全部。
    /// </summary>
    public List<ulong> GetNearbyPatchIdsOfKind(string kind)
    {
        if (!TryGetGardenPatches(out var patches, out _)) return [];

        return kind switch
        {
            "plot" => [.. patches.Where(x => x.Kind == PatchKind.Plot).Select(x => x.Id)],
            "pot" => [.. patches.Where(x => x.Kind == PatchKind.Pot).Select(x => x.Id)],
            _ => [.. patches.Select(x => x.Id)],
        };
    }

    /// <summary>該物件的種類："plot"（庭院地壟）／"pot"（園藝花盆）／"unknown"（不是可種植物件）。</summary>
    public string GetPatchKind(ulong gameObjectId)
    {
        var obj = Svc.Objects.SearchById(gameObjectId);
        if (obj == null || !TryClassify(obj, out var kind)) return "unknown";
        return kind == PatchKind.Plot ? "plot" : "pot";
    }

    /// <summary>物件與玩家的距離（碼）；找不到時回 -1。</summary>
    public float GetPatchDistance(ulong gameObjectId)
    {
        var localPlayer = Svc.Objects.LocalPlayer;
        var obj = Svc.Objects.SearchById(gameObjectId);
        return localPlayer == null || obj == null
            ? -1f
            : Vector3.Distance(localPlayer.Position, obj.Position);
    }

    /// <summary>該地壟最近一次 Scan 讀到的可用選項（不含「取消」）；沒掃過回空清單。</summary>
    public List<string> GetScannedActions(ulong gameObjectId) =>
        scannedActions.TryGetValue(gameObjectId, out var actions) ? [.. actions] : [];

    /// <summary>
    /// 由最近一次 Scan 的可用選項推導的地壟狀態：
    /// unscanned（沒掃過）／mature（可收穫）／empty（可播種）／growing（生長中，可護理或施肥）／unknown。
    /// 注意：作物狀態無法從記憶體離線讀取，必須先呼叫 Scan 互動一次。
    /// </summary>
    public string GetPatchState(ulong gameObjectId)
    {
        if (!scannedActions.TryGetValue(gameObjectId, out var actions)) return "unscanned";
        if (actions.Any(x => x.Contains(textHarvest, StringComparison.Ordinal))) return "mature";
        if (actions.Any(x => x.Contains(textPlant, StringComparison.Ordinal))) return "empty";
        if (actions.Any(x => x.Contains(textTend, StringComparison.Ordinal) ||
                             x.Contains(textFertilize, StringComparison.Ordinal))) return "growing";
        return "unknown";
    }

    /// <summary>
    /// 對單一地壟排入一個動作。<paramref name="gameObjectId"/> 傳 0 代表使用目前的目標。
    /// 回傳空字串代表已排入佇列，否則為 zh-TW 失敗原因。
    /// </summary>
    public string EnqueueSingle(GardenAction action, ulong gameObjectId, uint fertilizerItemId, uint seedItemId, uint soilItemId)
    {
        if (!IsEnabled) return "自動園圃作業模組未啟用（請在 TC Toolbox 設定視窗開啟）。";
        if (queue.IsBusy) return $"目前有作業執行中（{queue.CurrentStep}），請先等待或呼叫 Stop。";

        if (!TryGetGardenPatches(out var patches, out var error))
            return error;

        if (gameObjectId == 0)
        {
            var target = Svc.Targets.Target;
            if (target == null) return "沒有指定地壟／花盆，且目前沒有選取任何目標。";
            gameObjectId = target.GameObjectId;
        }

        if (!patches.Any(x => x.Id == gameObjectId))
            return "指定的目標不是附近的園圃地壟或園藝花盆。";

        var distance = GetPatchDistance(gameObjectId);
        if (distance < 0 || distance > InteractRange)
            return $"距離太遠（{distance:F1} 碼，上限 {InteractRange} 碼），請先走近。";

        switch (action)
        {
            case GardenAction.Fertilize when fertilizerItemId == 0 || FindInventoryItem(fertilizerItemId) == null:
                return "背包內沒有指定的肥料。";
            case GardenAction.Plant when seedItemId == 0 || soilItemId == 0:
                return "播種必須同時指定種子與土壤 ItemId。";
            case GardenAction.Plant when FindInventoryItem(seedItemId) == null || FindInventoryItem(soilItemId) == null:
                return "背包內沒有指定的種子或土壤。";
        }

        doneCount = 0;
        skippedCount = 0;
        lastSummary = string.Empty;

        EnqueuePatch(gameObjectId, action, fertilizerItemId, seedItemId, soilItemId);
        queue.Enqueue("彙總結果", () =>
        {
            lastSummary = action == GardenAction.Scan
                ? $"掃描完成：狀態 {GetPatchState(gameObjectId)}。"
                : $"單格「{ActionText(action)}」完成：處理 {doneCount} 格、跳過 {skippedCount} 格。";
            return true;
        });

        return string.Empty;
    }

    /// <summary>停止目前佇列中的所有作業。</summary>
    public void StopBatch()
    {
        if (!queue.IsBusy) return;
        queue.Abort();

        // 🔴 佇列清掉不會讓角色停下來——走位是交給 vnavmesh 跑的，它不知道我們放棄了。
        //    這裡不補一句停止的話，表現是「按了停止，角色照樣走到下一格才站住」。
        StopWalkIfMoving();

        lastSummary = $"已停止（完成 {doneCount} 格、跳過 {skippedCount} 格）。";
    }

    #endregion

    #region 設定 UI

    public override void DrawConfig()
    {
        EnsureItemLists();

        if (queue.IsBusy)
        {
            ImGui.TextColored(new Vector4(1f, 0.85f, 0.35f, 1f), $"執行中：{queue.CurrentStep}（完成 {doneCount}／跳過 {skippedCount}）");
            if (ImGui.Button("停止批次"))
            {
                StopBatch();
                Svc.Chat.Print($"[TC Toolbox] 已手動停止園圃批次（完成 {doneCount} 格、跳過 {skippedCount} 格）。");
            }

            return;
        }

        DrawAutoSection();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextUnformatted("單一動作批次（對附近所有地壟與花盆，不看狀態）：");
        if (ImGui.Button($"{textHarvest}##garden"))
            StartBatch(GardenAction.Harvest);
        ImGui.SameLine();
        if (ImGui.Button($"{textTend}##garden"))
            StartBatch(GardenAction.Tend);
        ImGui.SameLine();
        if (ImGui.Button($"{textFertilize}##garden"))
            StartBatch(GardenAction.Fertilize);

        DrawItemCombo("肥料", fertilizerItems!, ref fertilizerSearch, Config.FertilizerItemId, id =>
        {
            Config.FertilizerItemId = id;
            Plugin.Instance.Config.Save();
        });

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted($"{textPlant}（先選種子與土壤，會種滿附近所有空地壟與空花盆）：");

        DrawItemCombo("種子", seedItems!, ref seedSearch, Config.SeedItemId, id =>
        {
            Config.SeedItemId = id;
            Plugin.Instance.Config.Save();
        });

        DrawItemCombo("土壤", soilItems!, ref soilSearch, Config.SoilItemId, id =>
        {
            Config.SoilItemId = id;
            Plugin.Instance.Config.Save();
        });

        if (ImGui.Button($"開始{textPlant}##garden"))
            StartBatch(GardenAction.Plant);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextDisabled($"支援庭院園圃地壟與室內園藝花盆（{string.Join("／", GetPotNames())}）；");
        ImGui.TextDisabled("純裝飾用的花盆（例如南瓜花盆）不能種東西，不會被列入。");
        ImGui.Spacing();
        ImGui.TextDisabled("本模組啟用時另提供 TCToolbox.Gardening.* IPC，讓本機腳本（如 SND）逐格操作；");
        ImGui.TextDisabled("腳本只能一次操作一格，整座庭院的批次入口只有上面這些按鈕與 /tcgarden 指令。");
        ImGui.Spacing();
        ImGui.TextDisabled("指令：/tcgarden＝跑一輪自動整理（可以放進快捷列）；");
        ImGui.TextDisabled("　　　/tcgarden ui／status／stop／harvest／tend／fertilize／plant。");
        ImGui.TextDisabled("⚠️ 指令與按鈕唯一的差別：指令會先看「現在方不方便」（戰鬥中、別的外掛正在移動、");
        ImGui.TextDisabled("　　剛按過全艦隊急停等），擋下來時會在聊天視窗說是被什麼擋的。");
    }

    /// <summary>種子／土壤／肥料清單資料驅動：Item 表 ItemUICategory 82（園藝用品），FilterGroup 20／21／22。</summary>
    private void EnsureItemLists()
    {
        if (seedItems != null) return;

        seedItems = [];
        soilItems = [];
        fertilizerItems = [];

        foreach (var item in Svc.Data.GetExcelSheet<Item>())
        {
            if (item.ItemUICategory.RowId != 82) continue;

            var list = item.FilterGroup switch
            {
                20 => seedItems,
                21 => soilItems,
                22 => fertilizerItems,
                _ => null,
            };
            list?.Add((item.RowId, item.Name.ExtractText()));
        }
    }

    private static void DrawItemCombo(string label, List<(uint Id, string Name)> items, ref string search, uint currentId, Action<uint> onSelect)
    {
        var current = currentId == 0
            ? "（未選擇）"
            : items.FirstOrDefault(x => x.Id == currentId).Name ?? $"#{currentId}";

        ImGui.SetNextItemWidth(280f);
        if (!ImGui.BeginCombo($"{label}##combo", current)) return;

        ImGui.SetNextItemWidth(-1f);
        ImGui.InputTextWithHint($"##{label}filter", "篩選…", ref search, 64);

        var manager = InventoryManager.Instance();
        foreach (var (id, name) in items)
        {
            if (!string.IsNullOrWhiteSpace(search) &&
                !name.Contains(search, StringComparison.OrdinalIgnoreCase)) continue;

            var owned = manager != null ? manager->GetInventoryItemCount(id) : 0;
            if (ImGui.Selectable($"{name}（庫存 {owned}）##{label}{id}", id == currentId))
                onSelect(id);
        }

        ImGui.EndCombo();
    }

    #endregion
}

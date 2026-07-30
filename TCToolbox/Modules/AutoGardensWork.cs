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
public sealed unsafe class AutoGardensWork : TcModule
{
    public override string InternalName => "AutoGardensWork";
    public override string DisplayName => "自動園圃作業";
    public override string Description => "站在自家（或部隊）庭院的園圃旁，一鍵批次收穫／護理／施肥附近所有地壟；亦可選定種子與土壤後批次播種。距離太遠或狀態不符的地壟會自動跳過。";

    public override bool HasConfigUI => true;

    /// <summary>園圃地壟 EObj（EObjName 2003757「園圃」，事件 721047）。</summary>
    private const uint GardenPatchDataId = 2003757;

    /// <summary>互動距離上限（碼）；超出的地壟跳過。</summary>
    private const float InteractRange = 6f;

    private const string GardeningTextSheet = "custom/001/CmnDefHousingGardeningPlant_00151";

    /// <summary>園圃動作；<see cref="Scan"/> 只互動讀取可用選項後取消，不改變任何狀態。</summary>
    public enum GardenAction
    {
        Harvest,
        Tend,
        Fertilize,
        Plant,
        Scan,
    }

    private sealed class PatchJob
    {
        public bool Skipped;

        /// <summary>是否已對此地壟發出互動（決定收尾時要不要等互動狀態結束）。</summary>
        public bool Interacted;
    }

    private readonly TaskQueue queue = new();

    // 選單文字（啟用時自 Lumina 表載入；讀取失敗時保留台服 7.20 實測值）
    private string textCancel = "取消";
    private string textPlant = "播種";
    private string textFertilize = "施肥";
    private string textTend = "護理";
    private string textHarvest = "收穫";

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
        Svc.Framework.Update += OnUpdate;
        Svc.ClientState.TerritoryChanged += OnTerritoryChanged;
    }

    protected override void OnDisable()
    {
        Svc.ClientState.TerritoryChanged -= OnTerritoryChanged;
        Svc.Framework.Update -= OnUpdate;
        queue.Abort();
        scannedActions.Clear();
    }

    /// <summary>換區後地壟 ObjectId 會重來，掃描結果一律作廢，避免拿到別座庭院的舊狀態。</summary>
    private void OnTerritoryChanged(ushort territoryType) => scannedActions.Clear();

    private void OnUpdate(IFramework framework) => queue.Tick();

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

        foreach (var patchId in patches)
            EnqueuePatch(patchId, action, Config.FertilizerItemId, Config.SeedItemId, Config.SoilItemId);

        queue.Enqueue("彙總結果", () =>
        {
            lastSummary = $"園圃「{ActionText(action)}」批次完成：處理 {doneCount} 格、跳過 {skippedCount} 格。";
            Svc.Chat.Print($"[TC Toolbox] {lastSummary}");
            return true;
        });
    }

    /// <param name="fertilizerItemId">施肥用；批次走設定值，IPC 走呼叫端指定值。</param>
    /// <param name="seedItemId">播種用種子。</param>
    /// <param name="soilItemId">播種用土壤。</param>
    private void EnqueuePatch(ulong gameObjectId, GardenAction action, uint fertilizerItemId, uint seedItemId, uint soilItemId)
    {
        var job = new PatchJob();

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
            if (UiHelper.ClickTalkIfOpen()) return false;
            return UiHelper.IsAddonReady("SelectString") ? true : false;
        }, 8_000);

        queue.Enqueue("選擇動作", () =>
        {
            if (job.Skipped) return true;

            var addon = UiHelper.GetAddon("SelectString");
            if (!UiHelper.IsReady(addon)) return false;

            var entries = UiHelper.GetSelectStringEntries(addon);
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

            var target = ActionText(action);
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

        switch (action)
        {
            case GardenAction.Fertilize:
                EnqueueFertilizeSteps(job, fertilizerItemId);
                break;
            case GardenAction.Plant:
                EnqueuePlantSteps(job, seedItemId, soilItemId);
                break;
        }

        queue.Enqueue("等待互動結束", () =>
        {
            // 因距離跳過（從未互動）的地壟直接放行；取消跳過的仍要等選單收起、互動狀態結束
            if (job.Skipped && !job.Interacted) return true;

            UiHelper.ClickTalkIfOpen();

            if (UiHelper.IsAddonReady("SelectString")) return false;
            if (Svc.Condition[ConditionFlag.OccupiedInQuestEvent]) return false;

            if (!job.Skipped)
                doneCount++;
            return true;
        }, 15_000);
    }

    private void EnqueueFertilizeSteps(PatchJob job, uint fertilizerItemId)
    {
        queue.Enqueue("開啟肥料選單", () =>
        {
            if (job.Skipped) return true;
            if (UiHelper.IsAddonReady("SelectString")) return false; // 等選單收起

            var fertilizer = FindInventoryItem(fertilizerItemId);
            if (fertilizer == null)
            {
                Svc.Chat.PrintError("[TC Toolbox] 肥料已用完，批次停止。");
                return null;
            }

            AgentInventoryContext.Instance()->OpenForItemSlot(
                fertilizer->Container,
                fertilizer->Slot,
                0,
                AgentModule.Instance()->GetAgentByInternalId(AgentId.Inventory)->AddonId);
            return true;
        }, 8_000);

        queue.Enqueue("點選施肥", () =>
        {
            if (job.Skipped) return true;

            var context = UiHelper.GetAddon("ContextMenu");
            if (!UiHelper.IsReady(context)) return false;

            var entryCount = (int)context->AtkValues[0].UInt;
            for (var i = 0; i < entryCount; i++)
            {
                var value = context->AtkValues[7 + i];
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
            if (job.Skipped) return true;

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
            if (job.Skipped) return true;

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

    private static bool TryGetGardenPatches(out List<ulong> patchIds, out string error)
    {
        patchIds = [];
        error = string.Empty;

        var localPlayer = Svc.Objects.LocalPlayer;
        if (localPlayer == null)
        {
            error = "無法取得玩家狀態。";
            return false;
        }

        var housing = HousingManager.Instance();
        if (housing == null || housing->OutdoorTerritory == null)
        {
            error = "必須站在住宅區的庭院（室外）才能使用園圃批次。";
            return false;
        }

        var currentHouse = housing->GetCurrentHouseId().Id;
        if (currentHouse == 0 || !IsOwnedHouse(currentHouse))
        {
            error = "這裡不是你擁有（或有權限）的房屋庭院。";
            return false;
        }

        patchIds = Svc.Objects
            .Where(o => o.ObjectKind == ObjectKind.EventObj &&
                        o.BaseId == GardenPatchDataId &&
                        Vector3.Distance(localPlayer.Position, o.Position) <= 30f)
            .OrderBy(o => Vector3.Distance(localPlayer.Position, o.Position))
            .Select(o => o.GameObjectId)
            .ToList();

        if (patchIds.Count == 0)
        {
            error = "附近沒有園圃地壟（請站到園圃旁）。";
            return false;
        }

        return true;
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
            if (container == null || !container->IsLoaded) continue;

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

    /// <summary>環境是否可執行園圃操作；回傳空字串代表可用，否則為 zh-TW 失敗原因。</summary>
    public string GetUnavailableReason()
    {
        if (!IsEnabled) return "自動園圃作業模組未啟用（請在 TC Toolbox 設定視窗開啟）。";
        return TryGetGardenPatches(out _, out var error) ? string.Empty : error;
    }

    /// <summary>附近（30 碼內）園圃地壟的 GameObjectId，依距離排序；環境不符時回空清單。</summary>
    public List<ulong> GetNearbyPatchIds() =>
        TryGetGardenPatches(out var patches, out _) ? patches : [];

    /// <summary>地壟與玩家的距離（碼）；找不到時回 -1。</summary>
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
            if (target == null) return "沒有指定地壟，且目前沒有選取任何目標。";
            gameObjectId = target.GameObjectId;
        }

        if (!patches.Contains(gameObjectId))
            return "指定的目標不是這座庭院附近的園圃地壟。";

        var distance = GetPatchDistance(gameObjectId);
        if (distance < 0 || distance > InteractRange)
            return $"地壟距離太遠（{distance:F1} 碼，上限 {InteractRange} 碼），請先走近。";

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

        ImGui.TextUnformatted("批次作業（對附近所有地壟）：");
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
        ImGui.TextUnformatted($"{textPlant}（先選種子與土壤，會種滿附近所有空地壟）：");

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
        ImGui.TextDisabled("本模組啟用時另提供 TCToolbox.Gardening.* IPC，讓本機腳本（如 SND）逐格操作；");
        ImGui.TextDisabled("腳本只能一次操作一格，整座庭院的批次入口只有上面這些按鈕。");
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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using Lumina.Excel.Sheets;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 自動維持食物 buff：在你指定的時機（進副本／倒數開始／戰鬥條件變更）檢查「元氣」buff，快到期或吃錯／
/// 沒吃就自動補一份你設定的食物。走 <c>ActionManager.UseActionLocation(ActionType.Item, id, extraParam:65535)</c>
/// （艦隊無 UI 用道具慣例）。<b>事件驅動的自動用道具</b>——只在模組開著時才會動作。
/// 參考 DailyRoutines AutoCheckFoodUsage 設計重寫（API13、無 OmenTools 相依）。
/// </summary>
/// <remarks>
/// 🔴🔴 <b>使用者裁決：預設關 ＋ 明確開關。</b>這是唯一會在你沒按任何按鈕的情況下替你使用道具的模組，
/// 所以模組本身預設關（<c>EnabledModules</c> 不含它），而且三個「觸發時機」也<b>全部預設關</b>——
/// 開了模組但一個時機都沒勾＝完全不動作。要它真的動，得自己勾食物、勾時機。
/// <para>
/// 🔴 <b>修掉 DR 的一個內部 bug</b>：DR 的 <c>LastFoodUsageTime</c> 是 <c>readonly</c> 且永遠是
/// <c>DateTime.MinValue</c>，於是它宣稱的「10 秒冷卻」<b>是死碼、從不生效</b>。這裡改成真的記錄上次用道具
/// 的時間並據以冷卻，避免短時間內連續喂食。
/// </para>
/// <para>
/// 🔴 所有原生指標都<b>在同一個 tick／detour 內同步取用</b>，不跨幀保存（每次要用時重新
/// <c>Control.GetLocalPlayer()</c>）。解不到 CountdownInit 特徵碼＝那個時機停用並記 Information，
/// 其餘兩個時機（進副本／條件變更）不受影響。
/// </para>
/// </remarks>
public sealed unsafe class AutoCheckFoodUsage : TcModule
{
    public override string InternalName => "AutoCheckFoodUsage";
    public override string DisplayName => "自動維持食物 buff";

    public override string Description =>
        "在指定時機（進副本／倒數開始／戰鬥條件變更）自動補上你設定的食物，維持元氣 buff。" +
        "預設關，且三個觸發時機也預設全關——要動得自己勾食物與時機。";

    public override ModuleCategory Category => ModuleCategory.Combat;

    public override bool HasConfigUI => true;

    /// <summary>倒數計時初始化函式（sig 已對台服 7.20 主程式離線驗證，唯一命中 0x140918760）。</summary>
    private const string CountdownInitSignature =
        "48 89 5C 24 10 57 48 83 EC 40 48 8B DA 48 8B F9 48 8B 49 08";

    private delegate nint CountdownInitDelegate(nint a1, nint a2);

    /// <summary>元氣（Well Fed）狀態 id（台服 Status 48＝「進食」，已離線驗證）。</summary>
    private const uint WellFedStatusId = 48;

    /// <summary>食物道具的介面分類（台服 ItemUICategory 46＝「食品」，已離線驗證）。</summary>
    private const uint FoodItemUiCategory = 46;

    /// <summary>用道具之間的最短冷卻（秒）。</summary>
    private const int FoodUsageCooldownSeconds = 10;

    private Dalamud.Hooking.Hook<CountdownInitDelegate>? countdownHook;

    private readonly TaskQueue queue = new() { DefaultTimeoutMs = 15_000 };

    /// <summary>上次真的送出「用道具」的時間（修掉 DR 的死碼冷卻）。</summary>
    private DateTime lastFoodUsage = DateTime.MinValue;

    private AutoCheckFoodUsageConfig Config => Plugin.Instance.Config.CheckFoodUsage;

    // ── UI 狀態 ──
    private static List<(uint Id, string Name)>? foodCache;
    private string foodSearch = string.Empty;
    private uint pendingFoodId;
    private bool pendingFoodHq = true;
    private string zoneSearch = string.Empty;
    private string jobSearch = string.Empty;
    private string conditionSearch = string.Empty;

    protected override void OnEnable()
    {
        Svc.Framework.Update += OnFrameworkUpdate;
        Svc.ClientState.TerritoryChanged += OnZoneChanged;
        Svc.Condition.ConditionChange += OnConditionChanged;

        if (Svc.SigScanner.TryScanText(CountdownInitSignature, out var address) && address != nint.Zero)
        {
            countdownHook = Svc.Hooks.HookFromAddress<CountdownInitDelegate>(address, CountdownInitDetour);
            countdownHook.Enable();
        }
        else
        {
            Svc.Log.Information(
                $"[{InternalName}] 找不到倒數計時初始化函式的特徵碼，「倒數開始時」這個時機不會生效" +
                "（進副本／條件變更兩個時機不受影響）。");
        }
    }

    protected override void OnDisable()
    {
        Svc.Framework.Update -= OnFrameworkUpdate;
        Svc.ClientState.TerritoryChanged -= OnZoneChanged;
        Svc.Condition.ConditionChange -= OnConditionChanged;
        countdownHook?.Dispose();
        countdownHook = null;
        queue.Abort();
    }

    private void OnFrameworkUpdate(IFramework framework) => queue.Tick();

    // ── 觸發時機 ──────────────────────────────────────────────

    private nint CountdownInitDetour(nint a1, nint a2)
    {
        var hook = countdownHook;
        var result = hook?.OriginalDisposeSafe(a1, a2) ?? nint.Zero;

        try
        {
            if (Config.OnCountdown && !IsInPvp())
                StartRefresh();
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, $"[{InternalName}] 倒數時機處理失敗");
        }
        return result;
    }

    private void OnZoneChanged(ushort zone)
    {
        if (Config.OnZoneChange && !IsInPvp())
            StartRefresh();
    }

    private void OnConditionChanged(ConditionFlag flag, bool value)
    {
        if (!Config.OnConditionChange || IsInPvp()) return;
        if ((value && Config.ConditionStart.Contains((uint)flag)) ||
            (!value && Config.ConditionEnd.Contains((uint)flag)))
            StartRefresh();
    }

    private void StartRefresh()
    {
        queue.Abort();
        queue.Enqueue("檢查食物", EnqueueFoodRefresh);
    }

    // ── 核心邏輯 ──────────────────────────────────────────────

    private bool? EnqueueFoodRefresh()
    {
        if (!IsValidState()) return false;               // 還不能用，下一 tick 重試
        if (!IsCooldownElapsed()) return true;           // 剛喂過，這一輪不做

        var presets = GetValidPresets();
        if (presets.Count == 0) return true;

        // 已經吃著清單裡的食物、且還沒到補充門檻 → 不動。
        if (TryGetWellFed(out var currentFoodRow, out var remaining) &&
            presets.Any(p => ToFoodRowId(p.ItemId) == currentFoodRow) &&
            !ShouldRefresh(remaining))
            return true;

        var target = presets[0];
        queue.Enqueue("送出用食物", () => TakeFood(target.ItemId, target.IsHq));
        return true;
    }

    private bool? TakeFood(uint itemId, bool isHq)
    {
        if (!Throttle.Pass("AutoCheckFoodUsage-TakeFood", 1000)) return false;
        if (!IsValidState()) return false;

        // 已經吃著正確食物且時間充裕（≥25 分）→ 收工。
        if (TryGetWellFed(out var row, out var remaining) &&
            row == ToFoodRowId(itemId) && remaining.TotalMinutes >= 25.0)
            return true;

        var manager = ActionManager.Instance();
        if (manager == null) return true;

        // HQ 食物的 actionId = itemId + 1000000；extraParam 65535＝無 UI 用道具慣例。
        manager->UseActionLocation(ActionType.Item, isHq ? itemId + 1_000_000 : itemId,
                                   0xE000_0000, null, 65535, 0);
        lastFoodUsage = DateTime.Now;

        if (Config.NotifyInChat)
            Svc.Chat.Print($"[TC Toolbox] 自動使用食物：{FoodName(itemId)}{(isHq ? "(HQ)" : string.Empty)}");
        Svc.Log.Information($"[{InternalName}] 送出使用食物 item={itemId} hq={isHq}");

        queue.EnqueueDelay(3000, "等待食物生效");
        queue.Enqueue("確認食物狀態", () => CheckFoodState(itemId));
        return true;
    }

    private bool? CheckFoodState(uint itemId)
    {
        // 成功吃上（正確食物且 ≥25 分）→ 完成；否則整條結束（不無限重試，避免燒食物）。
        if (TryGetWellFed(out var row, out var remaining) &&
            row == ToFoodRowId(itemId) && remaining.TotalMinutes >= 25.0)
            return true;

        Svc.Log.Information($"[{InternalName}] 食物尚未生效（item={itemId}），本輪結束、等下次時機再試。");
        return true;
    }

    // ── 判斷輔助（全部同步取指標，不跨幀）──────────────────────

    private static bool IsInPvp() => GameMain.IsInPvPArea();

    private static bool IsValidState()
    {
        if (Svc.Objects.LocalPlayer == null) return false;
        if (Svc.Condition[ConditionFlag.BetweenAreas] ||
            Svc.Condition[ConditionFlag.BetweenAreas51] ||
            Svc.Condition[ConditionFlag.OccupiedInEvent] ||
            Svc.Condition[ConditionFlag.Casting])
            return false;

        var manager = ActionManager.Instance();
        if (manager == null) return false;
        // GeneralAction 2＝物品欄；狀態 0＝現在可以用道具。
        return manager->GetActionStatus(ActionType.GeneralAction, 2, 0xE000_0000, true, true, null) == 0;
    }

    private bool IsCooldownElapsed() =>
        (DateTime.Now - lastFoodUsage).TotalSeconds >= FoodUsageCooldownSeconds;

    private List<FoodPreset> GetValidPresets()
    {
        var player = Svc.Objects.LocalPlayer;
        var inventory = InventoryManager.Instance();
        var zone = Svc.ClientState.TerritoryType;
        if (player == null || inventory == null || zone == 0)
            return [];

        var jobId = player.ClassJob.RowId;
        return Config.Presets
            .Where(p => p.Enabled
                        && (p.Zones.Count == 0 || p.Zones.Contains(zone))
                        && (p.Jobs.Count == 0 || p.Jobs.Contains(jobId))
                        && inventory->GetInventoryItemCount(p.ItemId, p.IsHq, true, true, 0) > 0)
            .OrderByDescending(p => p.Zones.Contains(zone))
            .ToList();
    }

    private bool ShouldRefresh(TimeSpan remaining) =>
        remaining <= TimeSpan.FromSeconds(Config.RefreshThresholdSeconds) &&
        remaining <= TimeSpan.FromMinutes(55);

    private static bool TryGetWellFed(out uint foodRowId, out TimeSpan remaining)
    {
        foodRowId = 0;
        remaining = TimeSpan.Zero;

        var player = Control.GetLocalPlayer();
        if (player == null) return false;

        var index = player->StatusManager.GetStatusIndex(WellFedStatusId);
        if (index == -1) return false;

        var status = player->StatusManager.Status[index];
        foodRowId = (uint)status.Param % 10000u;
        remaining = TimeSpan.FromSeconds(status.RemainingTime);
        return true;
    }

    private static uint ToFoodRowId(uint itemId)
    {
        var item = Svc.Data.GetExcelSheet<Item>().GetRowOrDefault(itemId);
        var action = item?.ItemAction.ValueNullable;
        if (action == null) return 0;
        // 食物的 ItemAction.Data[1] 指向 ItemFood 列。
        return action.Value.Data[1];
    }

    private static string FoodName(uint itemId) =>
        Svc.Data.GetExcelSheet<Item>().GetRowOrDefault(itemId)?.Name.ExtractText() ?? itemId.ToString();

    // ── 設定 UI ───────────────────────────────────────────────

    private static List<(uint Id, string Name)> FoodList()
    {
        if (foodCache != null) return foodCache;
        var list = new List<(uint, string)>();
        foreach (var item in Svc.Data.GetExcelSheet<Item>())
        {
            if (item.ItemUICategory.RowId != FoodItemUiCategory) continue;
            var name = item.Name.ExtractText();
            if (string.IsNullOrEmpty(name)) continue;
            list.Add((item.RowId, name));
        }
        foodCache = list;
        return list;
    }

    public override void DrawConfig()
    {
        ImGui.TextColored(new Vector4(1f, 0.75f, 0.4f, 1f),
            "此模組會在你沒按按鈕的情況下替你使用食物。三個時機預設全關，要動請自己勾。");

        ImGui.Separator();
        ImGui.TextDisabled("觸發時機：");
        DrawCheckpoint("進副本／切換區域時", () => Config.OnZoneChange, v => Config.OnZoneChange = v);
        ImGui.SameLine();
        DrawCheckpoint("倒數開始時", () => Config.OnCountdown, v => Config.OnCountdown = v);
        ImGui.SameLine();
        DrawCheckpoint("戰鬥條件變更時", () => Config.OnConditionChange, v => Config.OnConditionChange = v);

        if (Config.OnConditionChange)
        {
            using (ImRaii.PushIndent())
            {
                DrawConditionMultiSelect("開始時觸發", "##CondStart", Config.ConditionStart);
                DrawConditionMultiSelect("結束時觸發", "##CondEnd", Config.ConditionEnd);
            }
        }

        ImGui.Separator();
        var threshold = Config.RefreshThresholdSeconds;
        ImGui.SetNextItemWidth(120f);
        if (ImGui.InputInt("剩餘幾秒內就補充", ref threshold))
        {
            Config.RefreshThresholdSeconds = Math.Clamp(threshold, 0, 3600);
            Plugin.Instance.Config.Save();
        }
        ImGui.SameLine();
        var notify = Config.NotifyInChat;
        if (ImGui.Checkbox("用食物時在聊天欄說一聲", ref notify))
        {
            Config.NotifyInChat = notify;
            Plugin.Instance.Config.Save();
        }

        ImGui.Separator();
        DrawFoodPicker();
        DrawPresetTable();
    }

    private void DrawCheckpoint(string label, Func<bool> get, Action<bool> set)
    {
        var v = get();
        if (ImGui.Checkbox(label, ref v))
        {
            set(v);
            Plugin.Instance.Config.Save();
        }
    }

    private void DrawFoodPicker()
    {
        ImGui.TextDisabled("加入食物：");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(240f);
        var previewName = pendingFoodId == 0 ? "（挑一個食物）" : FoodName(pendingFoodId);
        using (var combo = ImRaii.Combo("##FoodPick", previewName))
        {
            if (combo)
            {
                var search = foodSearch;
                ImGui.SetNextItemWidth(-1f);
                if (ImGui.InputTextWithHint("##FoodSearch", "搜尋…", ref search, 64))
                    foodSearch = search;
                ImGui.Separator();
                foreach (var (id, name) in FoodList())
                {
                    if (foodSearch.Length > 0 &&
                        !name.Contains(foodSearch, StringComparison.OrdinalIgnoreCase) &&
                        !id.ToString().Contains(foodSearch))
                        continue;
                    if (ImGui.Selectable($"{name} ({id})", id == pendingFoodId))
                        pendingFoodId = id;
                }
            }
        }

        ImGui.SameLine();
        var hq = pendingFoodHq;
        if (ImGui.Checkbox("HQ", ref hq)) pendingFoodHq = hq;

        ImGui.SameLine();
        using (ImRaii.Disabled(pendingFoodId == 0))
        {
            if (ImGui.Button("加入"))
            {
                if (!Config.Presets.Any(p => p.ItemId == pendingFoodId && p.IsHq == pendingFoodHq))
                {
                    Config.Presets.Add(new FoodPreset { ItemId = pendingFoodId, IsHq = pendingFoodHq });
                    Plugin.Instance.Config.Save();
                }
            }
        }
    }

    private void DrawPresetTable()
    {
        if (Config.Presets.Count == 0)
        {
            ImGui.TextDisabled("還沒有食物。加入至少一個，才會有東西可補。");
            return;
        }

        for (var i = 0; i < Config.Presets.Count; i++)
        {
            var preset = Config.Presets[i];
            using var id = ImRaii.PushId(i);

            var enabled = preset.Enabled;
            if (ImGui.Checkbox("##en", ref enabled))
            {
                preset.Enabled = enabled;
                Plugin.Instance.Config.Save();
            }

            ImGui.SameLine();
            ImGui.Text($"{FoodName(preset.ItemId)}{(preset.IsHq ? " (HQ)" : string.Empty)}");

            ImGui.SameLine();
            if (ImGui.SmallButton("刪除"))
            {
                Config.Presets.RemoveAt(i);
                Plugin.Instance.Config.Save();
                break;
            }

            using (ImRaii.PushIndent())
            {
                DrawJobMultiSelect(preset);
                DrawZoneMultiSelect(preset);
            }
        }
    }

    private void DrawJobMultiSelect(FoodPreset preset)
    {
        ImGui.SetNextItemWidth(240f);
        var preview = preset.Jobs.Count == 0 ? "所有職業" : $"限 {preset.Jobs.Count} 個職業";
        using var combo = ImRaii.Combo("限定職業##job", preview);
        if (!combo) return;

        var search = jobSearch;
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.InputTextWithHint("##jobSearch", "搜尋…", ref search, 32))
            jobSearch = search;
        ImGui.Separator();

        foreach (var job in Svc.Data.GetExcelSheet<ClassJob>())
        {
            if (job.RowId == 0) continue;
            var abbr = job.Abbreviation.ExtractText();
            var name = job.Name.ExtractText();
            if (string.IsNullOrEmpty(abbr) || string.IsNullOrEmpty(name)) continue;
            var label = $"{name} ({abbr})";
            if (jobSearch.Length > 0 && !label.Contains(jobSearch, StringComparison.OrdinalIgnoreCase)) continue;

            var chosen = preset.Jobs.Contains(job.RowId);
            if (ImGui.Selectable(label, chosen, ImGuiSelectableFlags.DontClosePopups))
            {
                if (!preset.Jobs.Remove(job.RowId)) preset.Jobs.Add(job.RowId);
                Plugin.Instance.Config.Save();
            }
        }
    }

    private void DrawZoneMultiSelect(FoodPreset preset)
    {
        ImGui.SetNextItemWidth(240f);
        var preview = preset.Zones.Count == 0 ? "所有區域" : $"限 {preset.Zones.Count} 個區域";
        using var combo = ImRaii.Combo("限定區域##zone", preview);
        if (!combo) return;

        var search = zoneSearch;
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.InputTextWithHint("##zoneSearch", "搜尋區域名…", ref search, 64))
            zoneSearch = search;
        ImGui.Separator();

        if (zoneSearch.Length == 0)
        {
            ImGui.TextDisabled("輸入區域名開始搜尋（區域很多，不預先全列）。");
            // 仍列出目前已選的，方便取消。
            foreach (var zid in preset.Zones.ToList())
                DrawZoneRow(preset, zid);
            return;
        }

        var shown = 0;
        foreach (var terr in Svc.Data.GetExcelSheet<TerritoryType>())
        {
            var place = terr.PlaceName.ValueNullable?.Name.ExtractText();
            if (string.IsNullOrEmpty(place)) continue;
            if (!place.Contains(zoneSearch, StringComparison.OrdinalIgnoreCase)) continue;

            var chosen = preset.Zones.Contains(terr.RowId);
            if (ImGui.Selectable($"{place} ({terr.RowId})", chosen, ImGuiSelectableFlags.DontClosePopups))
            {
                if (!preset.Zones.Remove(terr.RowId)) preset.Zones.Add(terr.RowId);
                Plugin.Instance.Config.Save();
            }
            if (++shown >= 100) { ImGui.TextDisabled("…只顯示前 100 筆，縮小搜尋。"); break; }
        }
    }

    private void DrawZoneRow(FoodPreset preset, uint zoneId)
    {
        var place = Svc.Data.GetExcelSheet<TerritoryType>().GetRowOrDefault(zoneId)?
                       .PlaceName.ValueNullable?.Name.ExtractText() ?? zoneId.ToString();
        if (ImGui.Selectable($"{place} ({zoneId})", true, ImGuiSelectableFlags.DontClosePopups))
        {
            preset.Zones.Remove(zoneId);
            Plugin.Instance.Config.Save();
        }
    }

    private void DrawConditionMultiSelect(string label, string id, HashSet<uint> target)
    {
        ImGui.SetNextItemWidth(240f);
        using var combo = ImRaii.Combo(label + id, $"已選 {target.Count} 個");
        if (!combo) return;

        var search = conditionSearch;
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.InputTextWithHint(id + "search", "搜尋條件…", ref search, 64))
            conditionSearch = search;
        ImGui.Separator();

        foreach (var flag in Enum.GetValues<ConditionFlag>())
        {
            if ((uint)flag <= 1) continue;
            var name = flag.ToString();
            if (conditionSearch.Length > 0 && !name.Contains(conditionSearch, StringComparison.OrdinalIgnoreCase))
                continue;

            var chosen = target.Contains((uint)flag);
            if (ImGui.Selectable(name, chosen, ImGuiSelectableFlags.DontClosePopups))
            {
                if (!target.Remove((uint)flag)) target.Add((uint)flag);
                Plugin.Instance.Config.Save();
            }
        }
    }
}

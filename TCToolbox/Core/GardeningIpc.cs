using System;
using System.Collections.Generic;
using System.Linq;
using TCToolbox.Modules;

namespace TCToolbox.Core;

/// <summary>
/// 自動園圃作業的 IPC 對外介面（前綴 <c>TCToolbox.Gardening.</c>）。
///
/// 定位：**給本機腳本（SND 等）用的細項操作層**，一次一格、由呼叫端決策與推進；
/// 刻意不提供「一鍵跑完整座庭院」的外部入口——批次入口只保留在模組自己的 UI 上，
/// 維持「使用者觸發、隨時可取消」。
///
/// 前置條件：使用者必須先在 TC Toolbox 設定視窗啟用「自動園圃作業」模組
/// （模組停用時 Framework.Update 沒有掛勾，佇列不會推進），動作類端點會直接
/// 回傳失敗原因而不是靜默無作用。
///
/// 端點一覽（動作類一律回傳字串：空字串＝已排入佇列，非空＝zh-TW 失敗原因；
/// 地壟參數傳 0 代表使用目前的目標）：
/// <list type="bullet">
/// <item>動作：Harvest(id) / Tend(id) / Fertilize(id, 肥料ItemId) /
///       Plant(id, 種子ItemId, 土壤ItemId) / Scan(id)</item>
/// <item>狀態：IsAvailable() / GetUnavailableReason() / IsBusy() / GetCurrentStep() /
///       GetDoneCount() / GetSkippedCount() / GetLastSummary() / GetNearbyPatches() /
///       GetNearbyPatchesOfKind("plot"|"pot") / GetPatchKind(id) /
///       GetPatchDistance(id) / GetPatchActions(id) / GetPatchState(id)</item>
/// <item>控制：Stop()</item>
/// </list>
/// 排入後呼叫端應輪詢 <c>IsBusy</c> 等待完成，再讀 <c>GetLastSummary</c>。
///
/// 🔴 執行緒：<b>每一支端點都經過 <see cref="IpcFrameworkGate"/></b>。CallGate 是直接方法呼叫，
/// 提供端跑在呼叫端的執行緒上，而這裡的實作同步可達遊戲原生記憶體（物件表、目標、
/// <c>HousingManager</c>、<c>InventoryManager</c>）以及模組自己的裸 <c>List</c> 佇列。
/// 等不到主執行緒時回的是「模組不存在」時的同一個值（見各端點），呼叫端本來就要處理。
/// </summary>
public sealed class GardeningIpc : IDisposable
{
    private const string Prefix = "TCToolbox.Gardening.";

    private readonly List<Action> unregister = [];

    public GardeningIpc()
    {
        // 動作類
        RegisterFunc<ulong, string>("Harvest", id => Enqueue(AutoGardensWork.GardenAction.Harvest, id, 0, 0, 0), GateTimeout);
        RegisterFunc<ulong, string>("Tend", id => Enqueue(AutoGardensWork.GardenAction.Tend, id, 0, 0, 0), GateTimeout);
        RegisterFunc<ulong, uint, string>("Fertilize",
            (id, fertilizerItemId) => Enqueue(AutoGardensWork.GardenAction.Fertilize, id, fertilizerItemId, 0, 0), GateTimeout);
        RegisterFunc<ulong, uint, uint, string>("Plant",
            (id, seedItemId, soilItemId) => Enqueue(AutoGardensWork.GardenAction.Plant, id, 0, seedItemId, soilItemId), GateTimeout);
        RegisterFunc<ulong, string>("Scan", id => Enqueue(AutoGardensWork.GardenAction.Scan, id, 0, 0, 0), GateTimeout);

        // 狀態類
        RegisterFunc("IsAvailable", () => Module is { IsEnabled: true } m && m.GetUnavailableReason().Length == 0, false);
        RegisterFunc("GetUnavailableReason", () => Module?.GetUnavailableReason() ?? ModuleMissing, GateTimeout);
        RegisterFunc("IsBusy", () => Module?.IsBusy ?? false, true);
        RegisterFunc("GetCurrentStep", () => Module?.CurrentStepName ?? string.Empty, string.Empty);
        RegisterFunc("GetDoneCount", () => Module?.DoneCount ?? 0, 0);
        RegisterFunc("GetSkippedCount", () => Module?.SkippedCount ?? 0, 0);
        RegisterFunc("GetLastSummary", () => Module?.LastSummary ?? string.Empty, string.Empty);
        RegisterFunc<List<ulong>>("GetNearbyPatches", () => Module?.GetNearbyPatchIds() ?? [], []);
        RegisterFunc<string, List<ulong>>("GetNearbyPatchesOfKind", kind => Module?.GetNearbyPatchIdsOfKind(kind) ?? [], []);
        RegisterFunc<ulong, string>("GetPatchKind", id => Module?.GetPatchKind(id) ?? "unknown", "unknown");
        RegisterFunc<ulong, float>("GetPatchDistance", id => Module?.GetPatchDistance(id) ?? -1f, -1f);
        RegisterFunc<ulong, List<string>>("GetPatchActions", id => Module?.GetScannedActions(id) ?? [], []);
        RegisterFunc<ulong, string>("GetPatchState", id => Module?.GetPatchState(id) ?? "unscanned", "unscanned");

        // 控制類
        const string stopEndpoint = Prefix + "Stop";
        var stopGate = Svc.PluginInterface.GetIpcProvider<object?>(stopEndpoint);
        stopGate.RegisterAction(() => IpcFrameworkGate.Run(stopEndpoint, () => Module?.StopBatch()));
        unregister.Add(stopGate.UnregisterAction);
    }

    /// <summary>
    /// 閘門逾時（等不到遊戲主執行緒）時，字串類端點回傳的內容。
    /// </summary>
    /// <remarks>
    /// ⚠️ 動作類端點拿到這個字串時，<b>不代表動作一定沒發生</b>——閘門只能保證「還沒開始跑的
    /// 工作會被取消」，已經開始的那一件會照常跑完。所以呼叫端拿到它之後應該先問
    /// <c>IsBusy</c>／<c>GetLastSummary</c>，不要直接重送。
    /// </remarks>
    private const string GateTimeout =
        "等待遊戲主執行緒逾時（通常是讀取畫面或嚴重掉幀）；請先查 IsBusy 與 GetLastSummary 再決定要不要重試。";

    private const string ModuleMissing = "TC Toolbox 的自動園圃作業模組不存在。";

    private static AutoGardensWork? Module =>
        Plugin.Instance?.Modules.OfType<AutoGardensWork>().FirstOrDefault();

    private static string Enqueue(AutoGardensWork.GardenAction action, ulong gameObjectId, uint fertilizerItemId, uint seedItemId, uint soilItemId) =>
        Module is { } module
            ? module.EnqueueSingle(action, gameObjectId, fertilizerItemId, seedItemId, soilItemId)
            : ModuleMissing;

    // 🔴 閘門套在這四個 helper 上而不是逐支端點手動包：漏包一支的失敗形式是「那一支照樣
    // 在呼叫端的執行緒上讀原生記憶體」——沒有錯誤訊息，只有偶發的崩潰。放在這裡的話
    // 「每一支都經過閘門」是編譯器保證的（不傳 unavailable 值就編不過）。

    private void RegisterFunc<TRet>(string name, Func<TRet> func, TRet unavailable)
    {
        var endpoint = $"{Prefix}{name}";
        var gate = Svc.PluginInterface.GetIpcProvider<TRet>(endpoint);
        gate.RegisterFunc(() => IpcFrameworkGate.Get(endpoint, func, unavailable));
        unregister.Add(gate.UnregisterFunc);
    }

    private void RegisterFunc<T1, TRet>(string name, Func<T1, TRet> func, TRet unavailable)
    {
        var endpoint = $"{Prefix}{name}";
        var gate = Svc.PluginInterface.GetIpcProvider<T1, TRet>(endpoint);
        gate.RegisterFunc((T1 a1) => IpcFrameworkGate.Get(endpoint, () => func(a1), unavailable));
        unregister.Add(gate.UnregisterFunc);
    }

    private void RegisterFunc<T1, T2, TRet>(string name, Func<T1, T2, TRet> func, TRet unavailable)
    {
        var endpoint = $"{Prefix}{name}";
        var gate = Svc.PluginInterface.GetIpcProvider<T1, T2, TRet>(endpoint);
        gate.RegisterFunc((T1 a1, T2 a2) => IpcFrameworkGate.Get(endpoint, () => func(a1, a2), unavailable));
        unregister.Add(gate.UnregisterFunc);
    }

    private void RegisterFunc<T1, T2, T3, TRet>(string name, Func<T1, T2, T3, TRet> func, TRet unavailable)
    {
        var endpoint = $"{Prefix}{name}";
        var gate = Svc.PluginInterface.GetIpcProvider<T1, T2, T3, TRet>(endpoint);
        gate.RegisterFunc((T1 a1, T2 a2, T3 a3) => IpcFrameworkGate.Get(endpoint, () => func(a1, a2, a3), unavailable));
        unregister.Add(gate.UnregisterFunc);
    }

    public void Dispose()
    {
        foreach (var action in unregister)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Svc.Log.Error(ex, "[GardeningIpc] 取消註冊 IPC 端點時發生例外");
            }
        }

        unregister.Clear();
    }
}

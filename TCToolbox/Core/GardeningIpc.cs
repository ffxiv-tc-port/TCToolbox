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
///       GetPatchDistance(id) / GetPatchActions(id) / GetPatchState(id)</item>
/// <item>控制：Stop()</item>
/// </list>
/// 排入後呼叫端應輪詢 <c>IsBusy</c> 等待完成，再讀 <c>GetLastSummary</c>。
/// </summary>
public sealed class GardeningIpc : IDisposable
{
    private const string Prefix = "TCToolbox.Gardening.";

    private readonly List<Action> unregister = [];

    public GardeningIpc()
    {
        // 動作類
        RegisterFunc<ulong, string>("Harvest", id => Enqueue(AutoGardensWork.GardenAction.Harvest, id, 0, 0, 0));
        RegisterFunc<ulong, string>("Tend", id => Enqueue(AutoGardensWork.GardenAction.Tend, id, 0, 0, 0));
        RegisterFunc<ulong, uint, string>("Fertilize",
            (id, fertilizerItemId) => Enqueue(AutoGardensWork.GardenAction.Fertilize, id, fertilizerItemId, 0, 0));
        RegisterFunc<ulong, uint, uint, string>("Plant",
            (id, seedItemId, soilItemId) => Enqueue(AutoGardensWork.GardenAction.Plant, id, 0, seedItemId, soilItemId));
        RegisterFunc<ulong, string>("Scan", id => Enqueue(AutoGardensWork.GardenAction.Scan, id, 0, 0, 0));

        // 狀態類
        RegisterFunc("IsAvailable", () => Module is { IsEnabled: true } m && m.GetUnavailableReason().Length == 0);
        RegisterFunc("GetUnavailableReason", () => Module?.GetUnavailableReason() ?? ModuleMissing);
        RegisterFunc("IsBusy", () => Module?.IsBusy ?? false);
        RegisterFunc("GetCurrentStep", () => Module?.CurrentStepName ?? string.Empty);
        RegisterFunc("GetDoneCount", () => Module?.DoneCount ?? 0);
        RegisterFunc("GetSkippedCount", () => Module?.SkippedCount ?? 0);
        RegisterFunc("GetLastSummary", () => Module?.LastSummary ?? string.Empty);
        RegisterFunc<List<ulong>>("GetNearbyPatches", () => Module?.GetNearbyPatchIds() ?? []);
        RegisterFunc<ulong, float>("GetPatchDistance", id => Module?.GetPatchDistance(id) ?? -1f);
        RegisterFunc<ulong, List<string>>("GetPatchActions", id => Module?.GetScannedActions(id) ?? []);
        RegisterFunc<ulong, string>("GetPatchState", id => Module?.GetPatchState(id) ?? "unscanned");

        // 控制類
        var stopGate = Svc.PluginInterface.GetIpcProvider<object?>($"{Prefix}Stop");
        stopGate.RegisterAction(() => Module?.StopBatch());
        unregister.Add(stopGate.UnregisterAction);
    }

    private const string ModuleMissing = "TC Toolbox 的自動園圃作業模組不存在。";

    private static AutoGardensWork? Module =>
        Plugin.Instance?.Modules.OfType<AutoGardensWork>().FirstOrDefault();

    private static string Enqueue(AutoGardensWork.GardenAction action, ulong gameObjectId, uint fertilizerItemId, uint seedItemId, uint soilItemId) =>
        Module is { } module
            ? module.EnqueueSingle(action, gameObjectId, fertilizerItemId, seedItemId, soilItemId)
            : ModuleMissing;

    private void RegisterFunc<TRet>(string name, Func<TRet> func)
    {
        var gate = Svc.PluginInterface.GetIpcProvider<TRet>($"{Prefix}{name}");
        gate.RegisterFunc(func);
        unregister.Add(gate.UnregisterFunc);
    }

    private void RegisterFunc<T1, TRet>(string name, Func<T1, TRet> func)
    {
        var gate = Svc.PluginInterface.GetIpcProvider<T1, TRet>($"{Prefix}{name}");
        gate.RegisterFunc(func);
        unregister.Add(gate.UnregisterFunc);
    }

    private void RegisterFunc<T1, T2, TRet>(string name, Func<T1, T2, TRet> func)
    {
        var gate = Svc.PluginInterface.GetIpcProvider<T1, T2, TRet>($"{Prefix}{name}");
        gate.RegisterFunc(func);
        unregister.Add(gate.UnregisterFunc);
    }

    private void RegisterFunc<T1, T2, T3, TRet>(string name, Func<T1, T2, T3, TRet> func)
    {
        var gate = Svc.PluginInterface.GetIpcProvider<T1, T2, T3, TRet>($"{Prefix}{name}");
        gate.RegisterFunc(func);
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

using System;
using System.Collections.Generic;
using System.Reflection;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;

namespace TCToolbox.Core;

// 下面三個是 Artisan 回傳型別的鏡像（Artisan.IPC.CraftingListSummary／ListShoppingPlan／ShoppingPlanItem）。
// 🔴 成員名是跨插件契約：CallGate 對不同型別走 JSON 來回，名字打錯＝那一欄靜默變成預設值。

public sealed class ArtisanListSummary
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int RecipeCount { get; set; }
}

public sealed class ArtisanShoppingPlan
{
    public bool Found { get; set; }
    public int ListId { get; set; }
    public string ListName { get; set; } = string.Empty;
    public bool UniversalisEnabled { get; set; }
    public bool PriceScopeSingleWorld { get; set; }
    public List<ArtisanShoppingItem> Items { get; set; } = [];
}

public sealed class ArtisanShoppingItem
{
    public uint ItemId { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public int Required { get; set; }
    public int Owned { get; set; }
    public int Remaining { get; set; }
    public bool CraftedOnList { get; set; }

    /// <summary>0＝沒有價格資料、1＝Artisan 開著的清單編輯器、2＝Artisan 的 Universalis 快取。</summary>
    public int PriceSource { get; set; }

    public bool HasListings { get; set; }

    /// <summary>空字串＝不知道。</summary>
    public string OptimalWorld { get; set; } = string.Empty;

    /// <summary>0＝Artisan 對不回 World 表的列號（只能比名字）。</summary>
    public uint OptimalWorldId { get; set; }

    public double OptimalQuantity { get; set; }
    public double OptimalCost { get; set; }
    public bool Approximate { get; set; }
    public uint NpcUnitPrice { get; set; }
    public bool NpcCheaper { get; set; }
}

/// <summary>Artisan 製作清單「還缺什麼、在哪個世界買最便宜」的唯讀查詢。</summary>
/// <remarks>
/// 🔴 對方兩支端點都是純讀取（不查價、不送封包）。每次呼叫都是一次 JSON 來回，
/// 呼叫端只在使用者按「重新整理」或換世界時呼叫，不要每幀問。
/// </remarks>
internal static class ArtisanShoppingIpc
{
    // Artisan/IPC/ShoppingPlanIPC.cs GetIpcProvider<CraftingListSummary[]>("Artisan.GetCraftingListSummaries")
    private static readonly Lazy<ICallGateSubscriber<ArtisanListSummary[]>> SummariesGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<ArtisanListSummary[]>("Artisan.GetCraftingListSummaries"));

    // Artisan/IPC/ShoppingPlanIPC.cs GetIpcProvider<int, ListShoppingPlan>("Artisan.GetListShoppingPlan")
    private static readonly Lazy<ICallGateSubscriber<int, ArtisanShoppingPlan>> PlanGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<int, ArtisanShoppingPlan>("Artisan.GetListShoppingPlan"));

    // JSON 轉換失敗擲的是 Newtonsoft 的例外，不是 IpcError；用型別名判，免得多參照一個組件。
    private static bool IsIpcFailure(Exception ex) =>
        ex is IpcError or TargetInvocationException or InvalidCastException
        || ex.GetType().FullName?.StartsWith("Newtonsoft.Json.", StringComparison.Ordinal) == true;

    /// <summary>讀全部清單的購物計畫。</summary>
    /// <param name="reason">回 <see langword="false"/> 時可以直接顯示的原因。</param>
    /// <remarks>
    /// 回 <see langword="true"/> 而 <paramref name="plans"/> 是空的＝Artisan 有回答、但一份清單都沒有。
    /// 只在遊戲主執行緒呼叫：對方在別的執行緒被呼叫時會轉派回主執行緒並同步等它。
    /// </remarks>
    public static bool TryGetAllPlans(out List<ArtisanShoppingPlan> plans, out string reason)
    {
        plans = [];

        ArtisanListSummary[]? summaries;
        try
        {
            summaries = SummariesGate.Value.InvokeFunc();
        }
        catch (IpcNotReadyError)
        {
            reason = "需要 Artisan（沒有偵測到，或這一版的 Artisan 沒有購物清單端點）。";
            return false;
        }
        catch (Exception ex) when (IsIpcFailure(ex))
        {
            Svc.Log.Information(
                $"[ArtisanShoppingIpc] Artisan.GetCraftingListSummaries 呼叫失敗（{ex.GetType().Name}）：{ex.Message}");
            reason = $"Artisan 沒有回答清單查詢（{ex.GetType().Name}）。";
            return false;
        }

        foreach (var summary in summaries ?? [])
        {
            if (summary == null) continue;

            try
            {
                var plan = PlanGate.Value.InvokeFunc(summary.Id);
                if (plan is { Found: true })
                {
                    plan.Items ??= [];
                    if (string.IsNullOrEmpty(plan.ListName)) plan.ListName = summary.Name ?? string.Empty;
                    plans.Add(plan);
                }
            }
            catch (Exception ex) when (ex is IpcNotReadyError || IsIpcFailure(ex))
            {
                Svc.Log.Information(
                    $"[ArtisanShoppingIpc] Artisan.GetListShoppingPlan({summary.Id}) 呼叫失敗（{ex.GetType().Name}）：{ex.Message}");
                plans.Clear();
                reason = ex is IpcNotReadyError
                    ? "需要較新的 Artisan（這一版沒有購物清單端點）。"
                    : $"Artisan 沒有回答購物清單查詢（{ex.GetType().Name}）。";
                return false;
            }
        }

        reason = string.Empty;
        return true;
    }
}

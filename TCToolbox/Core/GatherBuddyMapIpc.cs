using System;
using System.Collections.Generic;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;

namespace TCToolbox.Core;

/// <summary>
/// GatherBuddy Reborn「採一張寶物地圖」的呼叫端包裝。
/// </summary>
/// <remarks>
/// 🔴 端點名與型別逐字對應 GatherBuddyReborn 的 <c>GatherBuddy/Plugin/GatherBuddyIpc.cs</c>（第 5 版）；
/// 前綴是對方寫死的常數 <c>GatherBuddyReborn</c>（大寫 B），不是 feed 的 InternalName 拼法。
/// 🔴 本檔只准在 <c>Framework.Update</c> 呼叫：對方端點走主執行緒閘門，在主執行緒上就地執行、
/// 從別的執行緒呼叫會讓呼叫端等最多 5 秒。錯誤記錄的節流用的也是主執行緒專用的 <see cref="Throttle"/>。
/// 🔴 <see cref="TryGatherTreasureMap"/> 只准接在使用者按下的按鈕上：不輪詢、不重試、不自動接續。
/// </remarks>
internal static class GatherBuddyMapIpc
{
    /// <summary>寶物地圖的兩支端點從第 5 版才有；判準是 <c>對方版本 &gt;= RequiredVersion</c>。</summary>
    public const int RequiredVersion = 5;

    /// <summary><see cref="TryGatherTreasureMap"/> 的回傳碼。0 是「不可用」，不是「已開始」。</summary>
    public const int MapUnavailable = 0;

    public const int MapStarted = 1;

    public const int MapAlreadyRunning = 2;

    public const int MapNotGatherable = 3;

    public const int MapAllowanceNotUp = 4;

    public const int MapAlreadyHeld = 5;

    public const int MapLevelTooLow = 6;

    public const int MapNotLoggedIn = 7;

    public const int MapListDisabled = 8;

    private const string Prefix = "GatherBuddyReborn";

    private const int ErrorLogIntervalMs = 60_000;

    private static readonly Lazy<ICallGateSubscriber<int>> VersionGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<int>($"{Prefix}.Version"));

    private static readonly Lazy<ICallGateSubscriber<List<uint>>> MapsGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<List<uint>>($"{Prefix}.GetGatherableTreasureMaps"));

    private static readonly Lazy<ICallGateSubscriber<uint, int>> GatherMapGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<uint, int>($"{Prefix}.GatherTreasureMap"));

    private static readonly Lazy<ICallGateSubscriber<int>> ActiveCountGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<int>($"{Prefix}.GetActiveAutoGatherItemCount"));

    /// <summary>GatherBuddy Reborn 在不在（順便拿版本）。<see langword="false"/>＝沒裝、沒載入或還沒註冊。</summary>
    public static bool TryGetVersion(out int version)
    {
        version = 0;

        try
        {
            version = VersionGate.Value.InvokeFunc();
            return true;
        }
        catch (IpcError)
        {
            return false;
        }
        catch (Exception ex)
        {
            LogUnexpected("Version", ex);
            return false;
        }
    }

    /// <summary>對方找得到採集點的寶物地圖道具 id（等級高的在前）。<see langword="false"/>＝問不到。</summary>
    public static bool TryGetGatherableMaps(out List<uint> itemIds)
    {
        itemIds = [];

        try
        {
            if (MapsGate.Value.InvokeFunc() is not { } result) return false;

            itemIds = result;
            return true;
        }
        catch (IpcError)
        {
            return false;
        }
        catch (Exception ex)
        {
            LogUnexpected("GetGatherableTreasureMaps", ex);
            return false;
        }
    }

    /// <summary>所有已啟用清單合起來還有幾種道具要採；<c>-1</c>＝問不到（不是 0）。</summary>
    public static int GetActiveItemCount()
    {
        try
        {
            return ActiveCountGate.Value.InvokeFunc();
        }
        catch (IpcError)
        {
            return -1;
        }
        catch (Exception ex)
        {
            LogUnexpected("GetActiveAutoGatherItemCount", ex);
            return -1;
        }
    }

    /// <summary>
    /// 替使用者按下「交給 GBR 採一張」。<see langword="false"/>＝呼叫本身失敗，<paramref name="code"/> 沒有意義。
    /// </summary>
    public static bool TryGatherTreasureMap(uint itemId, out int code)
    {
        code = MapUnavailable;

        try
        {
            code = GatherMapGate.Value.InvokeFunc(itemId);
            return true;
        }
        catch (IpcError)
        {
            return false;
        }
        catch (Exception ex)
        {
            LogUnexpected("GatherTreasureMap", ex);
            return false;
        }
    }

    /// <summary><see cref="TryGatherTreasureMap"/> 回傳碼的中文說明（記錄與畫面共用）。</summary>
    public static string DescribeCode(int code) => code switch
    {
        MapUnavailable => "不可用：GatherBuddy Reborn 等不到遊戲主執行緒，或正在卸載",
        MapStarted => "已放進它的「來自其他插件」清單並開啟自動採集",
        MapAlreadyRunning => "已放進它的「來自其他插件」清單；自動採集本來就開著",
        MapNotGatherable => "它沒有這張地圖的採集點資料",
        MapAllowanceNotUp => "這個角色現在還不能採寶物地圖（遊戲的採集額度還沒恢復）",
        MapAlreadyHeld => "背包裡已經有這張地圖了（同一種地圖只能持有一張）",
        MapLevelTooLow => "採礦工／園藝工的等級還不夠採這張地圖",
        MapNotLoggedIn => "還沒登入",
        MapListDisabled => "GatherBuddy 的「來自其他插件」清單或這張地圖已停用，請到 GatherBuddy 自動採集分頁勾回",
        _ => $"不認得的回傳值 {code}",
    };

    private static void LogUnexpected(string endpoint, Exception ex)
    {
        if (!Throttle.Pass($"TCToolbox.GatherBuddyMapIpc.Error.{endpoint}", ErrorLogIntervalMs)) return;

        Svc.Log.Information(
            $"[GatherBuddyMapIpc] 呼叫 {Prefix}.{endpoint} 時發生非預期例外（{ErrorLogIntervalMs / 1000} 秒內只報一次）：{ex}");
    }
}

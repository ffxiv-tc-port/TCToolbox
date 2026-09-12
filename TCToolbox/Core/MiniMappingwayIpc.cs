using System;
using System.Numerics;
using System.Reflection;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;

namespace TCToolbox.Core;

/// <summary>
/// Mini-Mappingway 標記 IPC 的呼叫端包裝（只加／只刪自己那一組標記）。
/// </summary>
/// <remarks>
/// 🔴 <b>參數順序在「加」與「刪」之間是相反的</b>：<c>AddPerson</c> 是（來源, 名字, id），
/// <c>RemovePersonByUint</c> 是（<b>id, 來源</b>）。兩個都是「string 與另一個東西」的組合，
/// 傳反了不會有任何錯誤訊息——<c>RemovePersonByUint</c> 拿 id 去 <c>PersonDict</c> 查來源，
/// 查不到就回 <see langword="false"/>，表現成「刪不掉但也不報錯」。
/// </remarks>
internal static class MiniMappingwayIpc
{
    /// <summary>需要的主版號。<b>主版號是相等比對</b>——主版號改變＝破壞相容性。</summary>
    public const int RequiredMajor = 1;

    /// <summary>
    /// 需要的次版號。
    /// </summary>
    /// <remarks>
    /// 🔴 判準是<c>對方次版號 &gt;= RequiredMinor</c>，<b>不是相等</b>：
    /// 用相等比對的話，對方合法地遞增次版號就會讓這一整條功能靜默失效。
    /// </remarks>
    public const int RequiredMinor = 2;

    /// <summary>非預期例外的記錄節流間隔（毫秒）。</summary>
    private const int ErrorLogIntervalMs = 60_000;

    // 建立 subscriber 只是本地物件，零成本；真正的探測發生在 InvokeFunc()。
    private static readonly Lazy<ICallGateSubscriber<Tuple<int, int>>> CheckVersionGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<Tuple<int, int>>("MiniMappingway.CheckVersion"));

    private static readonly Lazy<ICallGateSubscriber<string, Vector4, bool>> RegisterSourceGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<string, Vector4, bool>("MiniMappingway.RegisterOrUpdateSourceVec"));

    private static readonly Lazy<ICallGateSubscriber<string, string, uint, bool>> AddPersonGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<string, string, uint, bool>("MiniMappingway.AddPerson"));

    private static readonly Lazy<ICallGateSubscriber<uint, string, bool>> RemovePersonGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<uint, string, bool>("MiniMappingway.RemovePersonByUint"));

    private static readonly Lazy<ICallGateSubscriber<string, bool>> RemoveSourceGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<string, bool>("MiniMappingway.RemoveSourceAndPeople"));

    /// <summary>Mini-Mappingway 在不在（順便拿版本）。回 <see langword="false"/>＝沒裝或還沒註冊 IPC。</summary>
    public static bool TryGetVersion(out int major, out int minor)
    {
        major = 0;
        minor = 0;

        try
        {
            var version = CheckVersionGate.Value.InvokeFunc();
            if (version == null) return false;

            major = version.Item1;
            minor = version.Item2;
            return true;
        }
        catch (IpcError)
        {
            // 沒裝／還沒載入完成，這是正常狀態，不寫記錄。
            return false;
        }
        catch (TargetInvocationException ex)
        {
            LogUnexpected("MiniMappingway.CheckVersion", ex);
            return false;
        }
        catch (Exception ex)
        {
            LogUnexpected("MiniMappingway.CheckVersion", ex);
            return false;
        }
    }

    /// <summary>
    /// 註冊（或更新）一個標記來源。
    /// </summary>
    /// <remarks>🔴 <b>會清掉該來源已經加進去的所有人</b>，理由見類別說明。</remarks>
    public static bool RegisterSource(string sourceName, Vector4 initialColour)
        => Invoke("MiniMappingway.RegisterOrUpdateSourceVec",
                  () => RegisterSourceGate.Value.InvokeFunc(sourceName, initialColour));

    /// <summary>
    /// 把一個在場物件加進某個來源的標記清單。
    /// </summary>
    /// <param name="sourceName">來源名稱（<b>第一個</b>參數）。</param>
    /// <param name="name">物件名稱（對方只拿來做人類可讀的紀錄，非玩家物件不會拿它比對）。</param>
    /// <param name="id">物件的 <c>GameObjectId</c>，<b>必須塞得進 32 位元</b>。</param>
    /// <returns>
    /// <see langword="false"/> 代表沒加成功，而且<b>是常態不是錯誤</b>：物件已經不在物件表裡、
    /// 或它本來就已經在清單裡了。呼叫端下一輪再試即可。
    /// </returns>
    public static bool AddPerson(string sourceName, string name, uint id)
        => Invoke("MiniMappingway.AddPerson",
                  () => AddPersonGate.Value.InvokeFunc(sourceName, name, id));

    /// <summary>
    /// 從某個來源的標記清單移除一個物件。
    /// </summary>
    /// <param name="id">物件的 <c>GameObjectId</c>（<b>第一個</b>參數，順序與 <see cref="AddPerson"/> 相反）。</param>
    /// <param name="sourceName">來源名稱（第二個參數）。</param>
    /// <returns>
    /// <see langword="false"/>＝本來就不在清單裡（對方會在物件離開物件表時自己移除），這是常態。
    /// </returns>
    public static bool RemovePerson(uint id, string sourceName)
        => Invoke("MiniMappingway.RemovePersonByUint",
                  () => RemovePersonGate.Value.InvokeFunc(id, sourceName));

    /// <summary>把整個來源連同它的標記一起移除。<b>只在模組停用／卸載時用。</b></summary>
    public static bool RemoveSourceAndPeople(string sourceName)
        => Invoke("MiniMappingway.RemoveSourceAndPeople",
                  () => RemoveSourceGate.Value.InvokeFunc(sourceName));

    /// <summary>
    /// 共用的呼叫殼：失敗一律回 <see langword="false"/>，不讓例外離開這一層。
    /// </summary>
    /// <remarks>
    /// 🔴🔴 <b><c>catch (IpcError)</c> 攔不住「提供端自己擲的例外」。</b>Dalamud 的 CallGate 走
    /// <c>Delegate.DynamicInvoke</c>，提供端實作裡擲出來的東西一律被包成
    /// <see cref="TargetInvocationException"/>——那不是 <c>IpcError</c> 的子類，
    /// 全艦隊慣用的 <c>catch (IpcError)</c>／<c>SafeWrapper.IPCException</c> 一個都攔不到。
    /// </remarks>
    private static bool Invoke(string endpoint, Func<bool> call)
    {
        try
        {
            return call();
        }
        catch (IpcError)
        {
            return false;
        }
        catch (TargetInvocationException ex)
        {
            LogUnexpected(endpoint, ex);
            return false;
        }
        catch (Exception ex)
        {
            LogUnexpected(endpoint, ex);
            return false;
        }
    }

    private static void LogUnexpected(string endpoint, Exception ex)
    {
        if (!Throttle.Pass($"TCToolbox.MiniMappingwayIpc.Error.{endpoint}", ErrorLogIntervalMs)) return;

        // 🔴 Information 級：使用者跑 LogLevel 1，盲區只有 Verbose。
        Svc.Log.Information(
            $"[MiniMappingwayIpc] 呼叫 {endpoint} 時發生非預期例外（{ErrorLogIntervalMs / 1000} 秒內只報一次）：{ex}");
    }
}

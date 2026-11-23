using System;
using System.Reflection;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;

namespace TCToolbox.Core;

/// <summary>
/// AutoRetainer「探險帶回數量」端點（前綴 <c>AutoRetainer.VentureYield.</c>）的唯讀包裝。
/// </summary>
/// <remarks>
/// 🔴 型別逐字對齊提供端 AutoRetainer <c>Modules/EzIPCManagers/IPC_VentureYield.cs</c>：全是 CoreLib 的 ValueTuple／陣列／字串，
/// 兩邊同一個 <c>Type</c>、不走 JSON；對方要改形狀會開新名字（V2），這裡不跟著改舊名。
/// 🔴 只在框架執行緒呼叫；回的是複本，只讀，不寫回 AutoRetainer、也不呼叫它的任何寫入端點（例如 SetVenture）。
/// </remarks>
internal static class AutoRetainerVentureIpc
{
    /// <summary>本插件認得的最低版本。</summary>
    public const int RequiredApiVersion = 1;

    /// <summary>僱員屬性「不知道」（還沒在 AutoRetainer 的記錄下打開過那位僱員）。</summary>
    public const int UnknownStat = -1;

    private const string Prefix = "AutoRetainer.VentureYield.";

    private static readonly Lazy<ICallGateSubscriber<int>> ApiVersionGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<int>(Prefix + "ApiVersion"));

    private static readonly Lazy<ICallGateSubscriber<(ulong CID, string Name, string World)[]?>> CharactersGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<(ulong CID, string Name, string World)[]?>(Prefix + "CharactersV1"));

    private static readonly Lazy<ICallGateSubscriber<ulong,
        (string Name, uint Job, int Level, uint VentureID, bool HasVenture, long VentureEndsAt, int Ilvl, int Gathering, int Perception)[]?>> RetainersGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<ulong,
            (string Name, uint Job, int Level, uint VentureID, bool HasVenture, long VentureEndsAt, int Ilvl, int Gathering, int Perception)[]?>(
            Prefix + "RetainersV1"));

    private static readonly Lazy<ICallGateSubscriber<ulong, string, uint[]?,
        (uint VentureID, uint ItemID, int Minutes, bool Available, int Amount, int Tier, int Stat, int NextStat, int NextAmount, bool UsesPerception)[]?>> YieldsGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<ulong, string, uint[]?,
            (uint VentureID, uint ItemID, int Minutes, bool Available, int Amount, int Tier, int Stat, int NextStat, int NextAmount, bool UsesPerception)[]?>(
            Prefix + "YieldsV1"));

    /// <summary>對方的端點版本。</summary>
    /// <returns>false＝問不到（沒裝、沒載入、版本太舊沒有這組端點），<paramref name="error"/> 是例外型別名。</returns>
    public static bool TryGetApiVersion(out int version, out string error)
    {
        try
        {
            version = ApiVersionGate.Value.InvokeFunc();
            error = string.Empty;
            return true;
        }
        catch (Exception ex) when (IsIpcFailure(ex))
        {
            version = 0;
            error = ex.GetType().Name;
            return false;
        }
    }

    /// <returns>null＝沒答案（逾時或呼叫失敗），不是「沒有角色」。</returns>
    public static (ulong CID, string Name, string World)[]? GetCharacters(out string error) =>
        Call(() => CharactersGate.Value.InvokeFunc(), out error);

    /// <returns>null＝沒答案或查無此角色。</returns>
    public static (string Name, uint Job, int Level, uint VentureID, bool HasVenture, long VentureEndsAt, int Ilvl, int Gathering, int Perception)[]?
        GetRetainers(ulong cid, out string error) => Call(() => RetainersGate.Value.InvokeFunc(cid), out error);

    /// <param name="ventureIds">null＝那位僱員職業類別的全部指定探險。</param>
    /// <returns>null＝沒答案或查無此僱員。</returns>
    public static (uint VentureID, uint ItemID, int Minutes, bool Available, int Amount, int Tier, int Stat, int NextStat, int NextAmount, bool UsesPerception)[]?
        GetYields(ulong cid, string retainerName, uint[]? ventureIds, out string error) =>
        Call(() => YieldsGate.Value.InvokeFunc(cid, retainerName, ventureIds), out error);

    private static T? Call<T>(Func<T?> call, out string error) where T : class
    {
        try
        {
            error = string.Empty;
            return call();
        }
        catch (Exception ex) when (IsIpcFailure(ex))
        {
            error = $"{ex.GetType().Name}：{ex.Message}";
            return null;
        }
    }

    /// <remarks>不裸 catch：本插件自己的程式錯誤要照樣冒出來。</remarks>
    private static bool IsIpcFailure(Exception ex) =>
        ex is IpcError or TargetInvocationException or InvalidCastException;
}

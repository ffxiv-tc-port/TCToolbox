using System;
using System.Collections.Generic;
using System.Reflection;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;

namespace TCToolbox.Core;

/// <summary>
/// 對 AutoRetainer 的唯讀／手動 IPC 包裝。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 <b>只呼叫「查詢」與「使用者明確要求的切換」兩類端點。</b>
/// 絕不註冊 AutoRetainer 的 post-process 事件（<c>OnCharacterPostprocessStep</c> 那一類）——
/// 那會把本外掛接進「雇員作業完成→自動接手下一件事」的自動化鏈裡，是艦隊紅線。
/// </para>
/// <para>
/// 📌 <b>為什麼用反射讀角色資料</b>：<c>AutoRetainer.GetOfflineCharacterData</c> 回傳的是
/// AutoRetainer 自己的 <c>OfflineCharacterData</c> 型別。要在編譯期用它就得把
/// <c>AutoRetainerAPI</c>（連帶 ECommons）拉進來當相依——而本外掛是<b>刻意零相依</b>的。
/// 所以這裡把回傳值當成 <see cref="object"/> 收下，再用反射取 <c>Name</c>／<c>World</c>。
/// </para>
/// <para>
/// ⚠️ 反射是會隨對方改版而失效的，但<b>失效方向是安全的</b>：
/// 取不到名字 → 我們算不出目標 → 根本不會呼叫 <c>Relog</c>。
/// 而且就算取到的是錯的字串，AutoRetainer 端的 <c>Relog</c> 會拿它跟自己的角色清單比對，
/// 對不上就回 <c>false</c> 什麼都不做（見 AutoRetainer <c>IPC_PluginState.Relog</c>）。
/// 兩層都是 fail-closed，不存在「切到錯的角色」這個結果。
/// </para>
/// <para>
/// ⚠️ <c>OfflineCharacterData</c> 的 <c>Name</c>／<c>World</c> 是<b>欄位不是屬性</b>，
/// 所以反射兩種都要找——只找屬性會得到「一直取不到」而且完全不報錯。
/// </para>
/// </remarks>
internal static class AutoRetainerIpc
{
    // 建立 subscriber 本身是零成本的純本地物件；真正的探測發生在 InvokeFunc()。
    private static readonly Lazy<ICallGateSubscriber<List<ulong>>> RegisteredCids =
        new(() => Svc.PluginInterface.GetIpcSubscriber<List<ulong>>("AutoRetainer.GetRegisteredCIDs"));

    // 🔴 刻意宣告成 <ulong, object>：對方註冊的是 <ulong, OfflineCharacterData>，
    //    而 object 對任何參考型別都是合法的接收型別，這樣就不必把對方的型別編進來。
    private static readonly Lazy<ICallGateSubscriber<ulong, object>> OfflineCharacterData =
        new(() => Svc.PluginInterface.GetIpcSubscriber<ulong, object>("AutoRetainer.GetOfflineCharacterData"));

    private static readonly Lazy<ICallGateSubscriber<string, bool>> RelogGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<string, bool>("AutoRetainer.PluginState.Relog"));

    private static readonly Lazy<ICallGateSubscriber<bool>> CanAutoLoginGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<bool>("AutoRetainer.PluginState.CanAutoLogin"));

    private static readonly Lazy<ICallGateSubscriber<bool>> IsBusyGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<bool>("AutoRetainer.PluginState.IsBusy"));

    /// <summary>反射查一次就快取（同一個型別不會變）。</summary>
    private static Type? cachedType;
    private static MemberInfo? cachedNameMember;
    private static MemberInfo? cachedWorldMember;

    /// <summary>AutoRetainer 是否已安裝並載入（用唯讀的 IsBusy 探測，無副作用）。</summary>
    public static bool IsAvailable()
    {
        try
        {
            IsBusyGate.Value.InvokeFunc();
            return true;
        }
        catch (IpcError)
        {
            return false;
        }
    }

    /// <summary>
    /// 一次問出「AutoRetainer 在不在」與「它忙不忙」。
    /// </summary>
    /// <remarks>
    /// 🔑 <see cref="IsAvailable"/> 與 <see cref="IsBusy"/> 各自都會打一次 IPC，
    /// 而它們探的是<b>同一個端點</b>——要輪詢的呼叫端用這一支就只打一次。
    /// <para>
    /// ⚠️ 回傳 <see langword="false"/> 有兩種意思都合法：AutoRetainer 沒裝、或它沒開這個 IPC。
    /// 兩種情況下 <paramref name="busy"/> 都是 <see langword="false"/>，
    /// 呼叫端該把它當成「不知道，所以不擋」而不是「確定閒置」。
    /// </para>
    /// </remarks>
    /// <param name="busy">AutoRetainer 是否正在忙；IPC 打不通時為 <see langword="false"/>。</param>
    /// <returns>IPC 打得通（＝AutoRetainer 已安裝並載入）就回 <see langword="true"/>。</returns>
    public static bool TryGetIsBusy(out bool busy)
    {
        try
        {
            busy = IsBusyGate.Value.InvokeFunc();
            return true;
        }
        catch (IpcError)
        {
            busy = false;
            return false;
        }
    }

    /// <summary>AutoRetainer 目前是不是正在忙（有作業在跑）。</summary>
    public static bool IsBusy()
    {
        try
        {
            return IsBusyGate.Value.InvokeFunc();
        }
        catch (IpcError)
        {
            return false;
        }
    }

    /// <summary>AutoRetainer 認為現在可不可以自動登入切換。</summary>
    /// <remarks>📌 讀不到時回 <c>false</c>——不確定就別切，這個方向的錯誤只是「沒切成」。</remarks>
    public static bool CanAutoLogin()
    {
        try
        {
            return CanAutoLoginGate.Value.InvokeFunc();
        }
        catch (IpcError)
        {
            return false;
        }
    }

    /// <summary>AutoRetainer 已登記的角色 CID（有順序，就是它自己清單上的順序）。</summary>
    public static List<ulong> GetRegisteredCharacters()
    {
        try
        {
            return RegisteredCids.Value.InvokeFunc() ?? [];
        }
        catch (IpcError)
        {
            return [];
        }
    }

    /// <summary>用 CID 取角色的「名稱＠伺服器」。</summary>
    /// <returns>取不到時回 <c>false</c>（AutoRetainer 沒裝、沒有這個角色，或反射對不上欄位）。</returns>
    public static bool TryGetCharacterName(ulong cid, out string name, out string world)
    {
        name = string.Empty;
        world = string.Empty;

        object? data;
        try
        {
            data = OfflineCharacterData.Value.InvokeFunc(cid);
        }
        catch (IpcError)
        {
            return false;
        }
        catch (InvalidCastException)
        {
            // 對方換了回傳型別的形狀時會落在這裡。當成「取不到」，不要讓例外往上炸。
            return false;
        }

        if (data == null) return false;

        var type = data.GetType();
        if (!ReferenceEquals(type, cachedType))
        {
            cachedType = type;
            cachedNameMember = FindMember(type, "Name");
            cachedWorldMember = FindMember(type, "World");

            // Information 級：使用者跑 LogLevel 1。反射有沒有對上，只有這行說得出來。
            Svc.Log.Information(
                $"[AutoRetainerIpc] 角色資料型別＝{type.FullName}；" +
                $"Name {(cachedNameMember == null ? "找不到" : "已對上")}、" +
                $"World {(cachedWorldMember == null ? "找不到" : "已對上")}");
        }

        if (cachedNameMember == null || cachedWorldMember == null) return false;

        name = ReadString(cachedNameMember, data);
        world = ReadString(cachedWorldMember, data);

        return name.Length > 0 && world.Length > 0;
    }

    /// <summary>⚠️ 欄位與屬性都要找：對方那兩個是<b>欄位</b>，只找屬性會永遠取不到而且不報錯。</summary>
    private static MemberInfo? FindMember(Type type, string name)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;
        return (MemberInfo?)type.GetField(name, flags) ?? type.GetProperty(name, flags);
    }

    private static string ReadString(MemberInfo member, object instance)
    {
        try
        {
            var value = member switch
            {
                FieldInfo f => f.GetValue(instance),
                PropertyInfo p => p.GetValue(instance),
                _ => null,
            };

            return value as string ?? string.Empty;
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, $"[AutoRetainerIpc] 讀取角色欄位 {member.Name} 失敗");
            return string.Empty;
        }
    }

    /// <summary>
    /// 要求 AutoRetainer 切換到指定角色。
    /// </summary>
    /// <param name="charaNameWithWorld">「名稱＠伺服器」，必須與 AutoRetainer 自己的紀錄完全相符。</param>
    /// <param name="accepted">AutoRetainer 是否接受了這次請求。</param>
    /// <returns>IPC 呼叫本身是否成功（false＝AutoRetainer 未安裝／未載入）。</returns>
    /// <remarks>
    /// 📌 對方會拿這個字串去比對自己的角色清單，對不上就回 <c>false</c> 且不做任何事——
    /// 所以這裡不需要（也無法）自己驗證名稱正確性。
    /// </remarks>
    public static bool TryRelog(string charaNameWithWorld, out bool accepted)
    {
        try
        {
            accepted = RelogGate.Value.InvokeFunc(charaNameWithWorld);
            return true;
        }
        catch (IpcError ex)
        {
            Svc.Log.Warning(ex, "[AutoRetainerIpc] 呼叫 AutoRetainer.PluginState.Relog 失敗");
            accepted = false;
            return false;
        }
    }
}

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
/// 那會把本外掛接進「僱員作業完成→自動接手下一件事」的自動化鏈裡，是艦隊紅線。
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

    // ── 僱員道具取回（AutoRetainer.PluginState，手動觸發） ────────────────────

    // 🔴 端點名逐字對齊提供端：AutoRetainer 的 IPC_PluginState 建構子呼叫的是
    //    EzIPC.Init(this, $"{Svc.PluginInterface.InternalName}.PluginState")，
    //    而 EzIPC 的標籤是「Prefix.方法名」⇒ 前綴固定為 "AutoRetainer.PluginState"。
    //    ⚠️ 對方的 EzIPC.Init 沒有帶 SafeWrapper，所以提供端擲出的例外會原樣往外傳，
    //    再被 CallGate 的 Func.DynamicInvoke 包成 TargetInvocationException。

    /// <summary>本外掛認得的取回 API 版本下限。對方回的版本小於這個值就不要用下面那些端點。</summary>
    public const int RetrieveApiVersion = 1;

    // 取回端點的結果碼，逐字對齊 AutoRetainer 的 RetainerRetrieve 常數。
    // 🔴 0 與 -1 是<b>不同</b>的答案：0＝「走遍僱員容器，確定沒有這件」，
    //    -1＝「根本讀不到僱員容器，什麼都不能斷定」。把兩者併成一個 falsey
    //    就是「僱員只是還在載入，卻被當成空的」那個缺陷。
    public const int RetrieveResultNotPresent = 0;
    public const int RetrieveResultRetainerUnavailable = -1;
    public const int RetrieveResultCommandInFlight = -2;
    public const int RetrieveResultInventoryFull = -3;
    public const int RetrieveResultBlockedUnique = -4;
    public const int RetrieveResultInCrystals = -5;

    private static readonly Lazy<ICallGateSubscriber<int>> RetrieveApiVersionGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<int>(
            "AutoRetainer.PluginState.GetRetainerItemRetrieveApiVersion"));

    private static readonly Lazy<ICallGateSubscriber<uint, bool, bool, int>> RetrieveSlotByIdGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<uint, bool, bool, int>(
            "AutoRetainer.PluginState.RetrieveRetainerItemSlotById"));

    private static readonly Lazy<ICallGateSubscriber<uint, bool, bool, int>> OpenRetainerQuantityGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<uint, bool, bool, int>(
            "AutoRetainer.PluginState.GetOpenRetainerItemQuantity"));

    private static readonly Lazy<ICallGateSubscriber<object>> ResetRetrieveTrackingGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<object>(
            "AutoRetainer.PluginState.ResetRetainerRetrieveTracking"));

    /// <summary>
    /// 這一類例外全部代表「這條 IPC 現在不能用」，而不是我們自己算錯。
    /// </summary>
    /// <remarks>
    /// 🔴 <see cref="TargetInvocationException"/> 是<b>必須</b>攔的那一個，而且
    /// <see cref="IpcError"/> 攔不到它：Dalamud 的 <c>CallGateChannel.InvokeFunc</c>
    /// 是用 <c>Func.DynamicInvoke</c> 呼叫提供端，所以<b>提供端自己實作裡擲出來的</b>
    /// 東西一律被包成 <see cref="TargetInvocationException"/>。
    /// <para>
    /// ⚠️ 「同步端點才是這個形狀」——提供端若是 <c>async</c>，例外會進 faulted Task、
    /// <c>await</c> 時擲的是原始型別。這裡四支端點的提供端都是同步的
    /// （AutoRetainer 的 <c>IpcFrameworkGate.Run</c> 是同步等待並用
    /// <c>ExceptionDispatchInfo</c> 原樣重擲），所以只需要攔這一個。
    /// </para>
    /// <para>
    /// 🔴 不裸 <c>catch (Exception)</c>：那會把本外掛自己的程式錯誤一起吞掉，
    /// 表現成「AutoRetainer 怪怪的」而不是一則堆疊。
    /// </para>
    /// </remarks>
    private static bool IsIpcFailure(Exception ex) =>
        ex is IpcError or TargetInvocationException or InvalidCastException;

    /// <summary>
    /// AutoRetainer 有沒有提供「指定道具取回」這組端點，而且版本是本外掛認得的。
    /// </summary>
    /// <param name="version">對方回報的版本；問不到時為 0。</param>
    /// <param name="reason">回 <see langword="false"/> 時可以直接顯示在畫面上的原因。</param>
    public static bool SupportsItemRetrieve(out int version, out string reason)
    {
        version = 0;

        try
        {
            version = RetrieveApiVersionGate.Value.InvokeFunc();
        }
        catch (Exception ex) when (IsIpcFailure(ex))
        {
            reason = "沒有偵測到 AutoRetainer 的取回端點（未安裝、未載入，或版本太舊）。";
            return false;
        }

        if (version < RetrieveApiVersion)
        {
            reason = $"AutoRetainer 回報取回 API v{version}，本外掛需要 v{RetrieveApiVersion} 以上。";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// 對「目前開著的僱員」身上第一個放著 <paramref name="itemId"/> 的格子送出一次取回指令。
    /// </summary>
    /// <remarks>
    /// 📌 <b>永遠是整格</b>：遊戲那個指令沒有「取回 N 個」而不跳數量對話框的形式。
    /// 回傳的正整數就是那一格的數量，也就是呼叫端「想要少一點卻拿到更多」時的唯一告知管道。
    /// <para>
    /// 🔴 <b>送出不等於成功。</b>這支只送指令、不等結果（提供端的註解寫得很清楚）。
    /// 台服對這類指令的拒絕是<b>完全靜默</b>的——伺服器不受理時那一格不會有任何變化，
    /// 也不會有訊息。呼叫端<b>必須</b>自己觀察僱員容器的存量有沒有真的下降，
    /// 不可以拿「回傳正整數」當成「已取回」。
    /// </para>
    /// </remarks>
    /// <param name="result">
    /// 送出成功時是那一格的數量（≥1），否則是 <c>RetrieveResult*</c> 其中一個負值或 0。
    /// IPC 打不通時為 <see cref="RetrieveResultRetainerUnavailable"/>。
    /// </param>
    /// <returns>IPC 呼叫本身是否成功（<see langword="false"/>＝AutoRetainer 不能用了）。</returns>
    public static bool TryRetrieveSlotById(uint itemId, bool hqOnly, bool includeCrystals, out int result)
    {
        try
        {
            result = RetrieveSlotByIdGate.Value.InvokeFunc(itemId, hqOnly, includeCrystals);
            return true;
        }
        catch (Exception ex) when (IsIpcFailure(ex))
        {
            Svc.Log.Information(
                $"[AutoRetainerIpc] RetrieveRetainerItemSlotById 呼叫失敗（{ex.GetType().Name}）：{ex.Message}");
            // 🔴 失敗時給的是對方原本就定義過的「讀不到」值，不是新語意，也不是 0——
            //    0 會被呼叫端讀成「確定沒有這件」。
            result = RetrieveResultRetainerUnavailable;
            return false;
        }
    }

    /// <summary>目前開著的僱員身上有幾個 <paramref name="itemId"/>。</summary>
    /// <remarks>⚠️ <see cref="RetrieveResultRetainerUnavailable"/>（-1）是「不知道」，不是「沒有」。</remarks>
    public static int GetOpenRetainerQuantity(uint itemId, bool hqOnly, bool includeCrystals)
    {
        try
        {
            return OpenRetainerQuantityGate.Value.InvokeFunc(itemId, hqOnly, includeCrystals);
        }
        catch (Exception ex) when (IsIpcFailure(ex))
        {
            return RetrieveResultRetainerUnavailable;
        }
    }

    /// <summary>
    /// 請 AutoRetainer 忘掉「哪些格子已經送過取回指令」，讓下一次呼叫重新考慮每一格。
    /// </summary>
    /// <remarks>
    /// 📌 每一輪開始時叫一次：伺服器拒絕（或丟掉）的指令不會讓格子有任何變化，
    /// 對方的在途追蹤要等 10 秒逾時才會重新提供那一格。這支是最佳化不是正確性需求，
    /// 所以失敗了也不必反應。
    /// </remarks>
    public static void ResetRetrieveTracking()
    {
        try
        {
            ResetRetrieveTrackingGate.Value.InvokeAction();
        }
        catch (Exception ex) when (IsIpcFailure(ex))
        {
            // 刻意吞掉：這支純粹是最佳化，失敗只代表「那一格要多等 10 秒才會被重新提供」。
        }
    }
}

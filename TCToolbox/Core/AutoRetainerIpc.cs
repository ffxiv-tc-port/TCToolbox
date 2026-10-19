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
/// 🔴 <b>只呼叫「查詢」與「使用者明確要求的切換」兩類端點。</b>
/// 絕不註冊 AutoRetainer 的 post-process 事件（<c>OnCharacterPostprocessStep</c> 那一類）——
/// 那會把本外掛接進「僱員作業完成→自動接手下一件事」的自動化鏈裡，是艦隊紅線。
/// </remarks>
internal static class AutoRetainerIpc
{
    /// <summary>AutoRetainer 多角色模式（MultiMode）的開關狀態。</summary>
    /// <remarks>
    /// 🔴 <b><see cref="Unknown"/> 是零值。</b>「問不到」與「關著」是兩件事：多角色模式開著時 AutoRetainer 隨時可能把角色登出，
    /// 把「問不到」畫成「關著」等於在最需要提醒的那一刻保持沉默。
    /// </remarks>
    internal enum MultiModeState
    {
        /// <summary>AutoRetainer 在，但兩支多角色模式端點都問不到（或還沒問過）。</summary>
        Unknown = 0,

        /// <summary>AutoRetainer 沒安裝／沒載入——沒有衝突可談。</summary>
        NotInstalled,

        Off,

        On,
    }

    // 建立 subscriber 本身是零成本的純本地物件；真正的探測發生在 InvokeFunc()。
    private static readonly Lazy<ICallGateSubscriber<List<ulong>>> RegisteredCids =
        new(() => Svc.PluginInterface.GetIpcSubscriber<List<ulong>>("AutoRetainer.GetRegisteredCIDs"));

    // 🔴 接收型別必須是鏡像，不能是 object：型別不同時 CallGate 一律走 JSON 來回，
    //    宣告成 object 收到的是 JObject，上面找不到任何欄位。
    private static readonly Lazy<ICallGateSubscriber<ulong, OfflineCharacterInfo?>> CharacterInfoGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<ulong, OfflineCharacterInfo?>(
            "AutoRetainer.GetOfflineCharacterData"));

    private static readonly Lazy<ICallGateSubscriber<string, bool>> RelogGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<string, bool>("AutoRetainer.PluginState.Relog"));

    private static readonly Lazy<ICallGateSubscriber<bool>> CanAutoLoginGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<bool>("AutoRetainer.PluginState.CanAutoLogin"));

    private static readonly Lazy<ICallGateSubscriber<bool>> IsBusyGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<bool>("AutoRetainer.PluginState.IsBusy"));

    /// <summary>
    /// AutoRetainer <c>OfflineCharacterData</c> 的鏡像，只取本外掛要用的欄位。
    /// </summary>
    /// <remarks>
    /// 🔴 成員名逐字照抄對方的欄位名：拼錯不會擲例外，只會靜默留在初始值。
    /// </remarks>
    internal sealed class OfflineCharacterInfo
    {
        public ulong CID = 0;
        public string? Name = string.Empty;
        public string? World = string.Empty;

        public uint GCSeals = 0;

        /// <summary>軍票上限。-1＝從沒讀到；0＝沒有加入大國防聯軍。</summary>
        public int GCSealsMax = -1;

        /// <summary>軍票這一組的記錄時間。null＝從沒讀到。</summary>
        public DateTime? GCSealsUpdatedAt = null;

        /// <summary>初始值就是「不知道」，對方舊版沒有這些欄位時也落在這裡而不是 0。</summary>
        public SealsState Seals =>
            GCSealsUpdatedAt == null || GCSealsMax < 0 ? SealsState.Unknown
            : GCSealsMax == 0 ? SealsState.NotInGrandCompany
            : SealsState.Known;
    }

    /// <summary>另一個角色的軍票在 AutoRetainer 紀錄裡是哪一種狀態。</summary>
    internal enum SealsState
    {
        /// <summary>從沒讀到過（該角色還沒在 AutoRetainer 的記錄下登入過）。零值刻意給這個。</summary>
        Unknown = 0,

        /// <summary>讀到了：這個角色沒有加入大國防聯軍，軍票對它不成立。</summary>
        NotInGrandCompany,

        /// <summary>讀到了：<c>GCSeals</c>／<c>GCSealsMax</c> 是那個時間點的真值。</summary>
        Known,
    }

    private static bool loggedCharacterInfoFailure;
    private static bool loggedEmptyCharacterName;

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

    /// <summary>用 CID 取 AutoRetainer 記錄的角色資料。</summary>
    /// <param name="info">AutoRetainer 沒有這個角色時為 <see langword="null"/>。</param>
    /// <returns>IPC 打不通（沒裝、沒載入，或對方改了形狀）時回 <see langword="false"/>。</returns>
    /// <remarks>⚠️ 每次都是整份角色資料的 JSON 來回，只在框架執行緒上、節流後呼叫。</remarks>
    public static bool TryGetCharacterInfo(ulong cid, out OfflineCharacterInfo? info)
    {
        try
        {
            info = CharacterInfoGate.Value.InvokeFunc(cid);
            return true;
        }
        catch (Exception ex) when (IsIpcFailure(ex))
        {
            if (ex is not IpcNotReadyError && !loggedCharacterInfoFailure)
            {
                loggedCharacterInfoFailure = true;
                Svc.Log.Information(
                    $"[AutoRetainerIpc] 讀取 AutoRetainer 角色資料失敗（{ex.GetType().Name}）：{ex.Message}");
            }

            info = null;
            return false;
        }
    }

    /// <summary>用 CID 取角色的「名稱＠伺服器」。</summary>
    /// <returns>取不到時回 <c>false</c>（AutoRetainer 沒裝、沒有這個角色，或名稱／伺服器是空的）。</returns>
    public static bool TryGetCharacterName(ulong cid, out string name, out string world)
    {
        name = string.Empty;
        world = string.Empty;

        if (!TryGetCharacterInfo(cid, out var info) || info == null) return false;

        name = info.Name ?? string.Empty;
        world = info.World ?? string.Empty;

        if ((name.Length == 0 || world.Length == 0) && !loggedEmptyCharacterName)
        {
            loggedEmptyCharacterName = true;
            // 鏡像欄位名跟對方對不上時，只有這行說得出來（使用者的 log 收得到 Information）。
            Svc.Log.Information(
                $"[AutoRetainerIpc] AutoRetainer 回傳的角色資料沒有名稱或伺服器（CID {cid:X}），前／後切換無法使用");
        }

        return name.Length > 0 && world.Length > 0;
    }

    // 🔴 回傳型別照提供端宣告成 long?：宣告成 long 時，對方回 null（沒有進行中的探險）就擲 NullReferenceException。
    private static readonly Lazy<ICallGateSubscriber<ulong, long?>> ClosestVentureGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<ulong, long?>(
            "AutoRetainer.PluginState.GetClosestRetainerVentureSecondsRemaining"));

    private static bool loggedClosestVentureFailure;

    /// <summary>某角色「最早回來的那位僱員」還剩幾秒（負數＝已經回來了）。</summary>
    /// <param name="seconds">
    /// <see langword="null"/>＝AutoRetainer 答「沒有進行中的探險」（沒有派遣中的僱員，或都沒勾選給它管理）。
    /// </param>
    /// <returns>IPC 打不通時回 <see langword="false"/>——那是「不知道」，與 <see langword="null"/> 不同。</returns>
    /// <remarks>⚠️ 提供端包在它自己的 IpcFrameworkGate 裡：只在 Update 裡節流呼叫，不要在繪製時呼叫。</remarks>
    public static bool TryGetClosestVentureSeconds(ulong cid, out long? seconds)
    {
        try
        {
            seconds = ClosestVentureGate.Value.InvokeFunc(cid);
            return true;
        }
        catch (Exception ex) when (IsIpcFailure(ex))
        {
            if (ex is not IpcNotReadyError && !loggedClosestVentureFailure)
            {
                loggedClosestVentureFailure = true;
                Svc.Log.Information(
                    $"[AutoRetainerIpc] 讀取僱員探險剩餘時間失敗（{ex.GetType().Name}）：{ex.Message}");
            }

            seconds = null;
            return false;
        }
    }

    /// <summary>
    /// 要求 AutoRetainer 切換到指定角色。
    /// </summary>
    /// <param name="charaNameWithWorld">「名稱＠伺服器」，必須與 AutoRetainer 自己的紀錄完全相符。</param>
    /// <param name="accepted">AutoRetainer 是否接受了這次請求。</param>
    /// <returns>IPC 呼叫本身是否成功（false＝AutoRetainer 未安裝／未載入）。</returns>
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
    /// 🔴 不裸 <c>catch (Exception)</c>：那會把本外掛自己的程式錯誤一起吞掉，
    /// 表現成「AutoRetainer 怪怪的」而不是一則堆疊。
    /// </remarks>
    private static bool IsIpcFailure(Exception ex) =>
        ex is IpcError or TargetInvocationException or InvalidCastException;

    // ── 多角色模式（MultiMode）狀態 ─────────────────────────────────────────

    // 🔑 兩支都問，是因為「端點被改名」與「同名改型別」是對方隨時可能做的事，
    //    而只問一支的話，對方整理端點的那一天我們會<b>靜默</b>退成「不知道」。
    // 📌 兩支在提供端都是直接讀一個 static 欄位，<b>沒有</b>經過 IpcFrameworkGate，
    //    所以不會有「從繪製路徑呼叫、在主執行緒上等一個要靠主執行緒才跑得到的 tick」那個風險。

    private static readonly Lazy<ICallGateSubscriber<bool>> MultiModeStatusGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<bool>("AutoRetainer.PluginState.GetMultiModeStatus"));

    private static readonly Lazy<ICallGateSubscriber<bool>> MultiModeEnabledGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<bool>("AutoRetainer.GetMultiModeEnabled"));

    /// <summary>排程開著／多角色模式執行中／任務佇列在跑，三者任一即為真。</summary>
    private static readonly Lazy<ICallGateSubscriber<bool>> SchedulerBusyGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<bool>("AutoRetainer.IsBusy"));

    /// <summary>
    /// AutoRetainer 的多角色模式現在開著嗎。
    /// </summary>
    /// <param name="detail">
    /// 一句話說明這個答案是怎麼來的（給 tooltip 用）。<b>永遠不是 <see langword="null"/></b>。
    /// </param>
    /// <remarks>
    /// ⚠️ 只在框架執行緒上呼叫（IPC 的實作跑在<b>呼叫端</b>的執行緒上）。
    /// </remarks>
    public static MultiModeState GetMultiModeState(out string detail)
    {
        if (TryQueryBool(MultiModeStatusGate, out var enabled, out var newError))
        {
            detail = $"AutoRetainer.PluginState.GetMultiModeStatus 回 {enabled}";
            return enabled ? MultiModeState.On : MultiModeState.Off;
        }

        if (TryQueryBool(MultiModeEnabledGate, out enabled, out var oldError))
        {
            detail =
                $"AutoRetainer.GetMultiModeEnabled 回 {enabled}"
                + $"（新端點 AutoRetainer.PluginState.GetMultiModeStatus 打不通：{newError}）";
            return enabled ? MultiModeState.On : MultiModeState.Off;
        }

        // 🔑 兩支都打不通時，再問一支<b>完全無關</b>的端點，才分得出
        //    「AutoRetainer 根本不在」與「它在、只是這兩支端點沒了」。
        try
        {
            IsBusyGate.Value.InvokeFunc();
        }
        catch (IpcError)
        {
            // 連最基本的狀態端點都沒註冊 ⇒ 沒安裝／沒載入。
            detail = "沒有偵測到 AutoRetainer（未安裝或未載入）。";
            return MultiModeState.NotInstalled;
        }
        catch (Exception ex) when (IsIpcFailure(ex))
        {
            // 端點註冊著、只是提供端自己擲了例外 ⇒ 它<b>在</b>，所以不是「沒裝」。
            Svc.Log.Information(
                $"[AutoRetainerIpc] 探測 AutoRetainer 是否在時遇到 {ex.GetType().Name}：{ex.Message}");
        }

        detail =
            "AutoRetainer 在，但兩支多角色模式端點都打不通——多半是對方改了端點名或回傳型別。\n"
            + $"AutoRetainer.PluginState.GetMultiModeStatus：{newError}\n"
            + $"AutoRetainer.GetMultiModeEnabled：{oldError}";
        return MultiModeState.Unknown;
    }

    /// <summary>問一個無參數的 bool 端點。回 <see langword="false"/>＝問不到（不是「值是 false」）。</summary>
    private static bool TryQueryBool(Lazy<ICallGateSubscriber<bool>> gate, out bool value, out string error)
    {
        try
        {
            value = gate.Value.InvokeFunc();
            error = string.Empty;
            return true;
        }
        catch (Exception ex) when (IsIpcFailure(ex))
        {
            value = false;
            error = ex.GetType().Name;
            return false;
        }
    }

    // 原因代碼逐字對齊 AutoRetainer 的 MultiModeChangeCause（數值是對方的 IPC 契約）。
    public const int MultiModeCauseNone = 0;
    public const int MultiModeCauseUser = 1;
    public const int MultiModeCauseAutoRetainer = 2;
    public const int MultiModeCauseOtherPlugin = 3;

    // 回傳是 CoreLib 的 ValueTuple：兩邊同一個 Type、不走 JSON。
    private static readonly Lazy<ICallGateSubscriber<(bool, int, long)>> MultiModeLastChangeGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<(bool, int, long)>("AutoRetainer.GetMultiModeLastChangeV1"));

    /// <summary>AutoRetainer 多角色模式最後一次開／關：變更後的值、原因代碼、距今毫秒（-1＝它載入後還沒變過）。</summary>
    /// <returns><see langword="false"/>＝問不到（舊版沒有這支端點、沒安裝，或呼叫失敗）；永不擲 IPC 的例外。</returns>
    public static bool TryGetMultiModeLastChange(out bool enabled, out int cause, out long millisecondsAgo, out string error)
    {
        try
        {
            (enabled, cause, millisecondsAgo) = MultiModeLastChangeGate.Value.InvokeFunc();
            error = string.Empty;
            return true;
        }
        catch (Exception ex) when (IsIpcFailure(ex) || ex.GetType().Namespace?.StartsWith("Newtonsoft.Json", StringComparison.Ordinal) == true)
        {
            enabled = false;
            cause = MultiModeCauseNone;
            millisecondsAgo = -1;
            error = ex.GetType().Name;
            return false;
        }
    }

    private static readonly Lazy<ICallGateSubscriber<(long, int)>> LastManualRelogGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<(long, int)>("AutoRetainer.GetLastManualRelogV1"));

    /// <summary>AutoRetainer 最後一次手動換角真的排入：距今毫秒（-1＝它載入後還沒有過）、原因代碼（同多角色模式那組）。</summary>
    /// <returns><see langword="false"/>＝問不到（舊版沒有這支端點、沒安裝，或呼叫失敗）；永不擲。</returns>
    public static bool TryGetLastManualRelog(out long millisecondsAgo, out int cause, out string error)
    {
        try
        {
            (millisecondsAgo, cause) = LastManualRelogGate.Value.InvokeFunc();
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            millisecondsAgo = -1;
            cause = MultiModeCauseNone;
            error = ex.GetType().Name;
            return false;
        }
    }

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
            reason = $"AutoRetainer 回報取回 API v{version}，本插件需要 v{RetrieveApiVersion} 以上。";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// 對「目前開著的僱員」身上第一個放著 <paramref name="itemId"/> 的格子送出一次取回指令。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>送出不等於成功。</b>這支只送指令、不等結果。
    /// 台服對這類指令的拒絕是<b>完全靜默</b>的——伺服器不受理時那一格不會有任何變化，
    /// 也不會有訊息。呼叫端<b>必須</b>自己觀察僱員容器的存量有沒有真的下降，
    /// 不可以拿「回傳正整數」當成「已取回」。
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

    // ── 從外面驅動僱員流程（AutoRetainer.PluginState，手動觸發） ──────────────

    // 🔴 這三支是 Enqueue 不是同步動作：呼叫後任務進 AutoRetainer 的佇列，
    //    呼叫端要自己輪詢 IsBusy() 等它做完。
    // 🔴 本外掛刻意<b>不</b>用 RequestCharacterPostprocess／不訂閱 AR 的任何事件——
    //    那會把自己接進「僱員作業完成→自動接手下一件事」的自動化鏈裡，是艦隊紅線。

    private static readonly Lazy<ICallGateSubscriber<List<string>>> RetainersWithEntrustPlanGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<List<string>>(
            "AutoRetainer.PluginState.GetRetainersWithEntrustPlan"));

    private static readonly Lazy<ICallGateSubscriber<string, bool>> OpenRetainerItemStorageGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<string, bool>(
            "AutoRetainer.PluginState.EnqueueOpenRetainerItemStorage"));

    // 提供端是 [EzIPC] public void ⇒ EzIPC 註冊的是 Action，消費端用 <object> + InvokeAction()。
    private static readonly Lazy<ICallGateSubscriber<object>> CloseRetainerGate =
        new(() => Svc.PluginInterface.GetIpcSubscriber<object>(
            "AutoRetainer.PluginState.EnqueueCloseRetainer"));

    /// <summary>AutoRetainer 自己的排程／多角色模式在不在跑。回傳值是「IPC 打不打得通」。</summary>
    /// <remarks>
    /// 🔴 <b>與 <see cref="IsBusy"/> 不是同一件事，兩支都要問</b>：<c>AutoRetainer.IsBusy</c>＝
    /// <c>排程開關 || MultiMode.Active || TaskManager.IsBusy</c>，<c>PluginState.IsBusy</c>＝
    /// <c>TaskManager.IsBusy || GC 繳交中 || Lifestream 忙</c>，<b>不含</b>前兩者 ⇒ 只問後者，
    /// 多角色模式開著但這一瞬間沒在跑任務會被讀成「閒著」。
    /// </remarks>
    public static bool TryGetSchedulerRunning(out bool running) =>
        TryQueryBool(SchedulerBusyGate, out running, out _);

    /// <summary>
    /// AutoRetainer 有沒有提供「從外面驅動僱員流程」這組端點。
    /// </summary>
    /// <remarks>📌 探針挑唯讀的 <c>GetRetainersWithEntrustPlan</c>：它沒有任何副作用。</remarks>
    /// <param name="reason">回 <see langword="false"/> 時可以直接顯示在畫面上的原因。</param>
    public static bool SupportsRetainerFlowControl(out string reason)
    {
        try
        {
            RetainersWithEntrustPlanGate.Value.InvokeFunc();
            reason = string.Empty;
            return true;
        }
        catch (Exception ex) when (IsIpcFailure(ex))
        {
            reason = "沒有偵測到 AutoRetainer 的僱員流程端點（未安裝、未載入，或版本太舊）。";
            return false;
        }
    }

    /// <summary>
    /// 目前角色有哪幾位僱員被指派了「寄存計畫」。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>這不是「哪幾位身上有東西可取」。</b>AutoRetainer 沒有那種端點；
    /// 有沒有東西可取只有打開那位僱員的道具欄才知道。這份清單只適合當畫面上的註記。
    /// </remarks>
    /// <returns>問不到時回 <see langword="null"/>——與「一位都沒有」是不同的答案。</returns>
    public static List<string>? GetRetainersWithEntrustPlan()
    {
        try
        {
            return RetainersWithEntrustPlanGate.Value.InvokeFunc() ?? [];
        }
        catch (Exception ex) when (IsIpcFailure(ex))
        {
            return null;
        }
    }

    /// <summary>請 AutoRetainer 排入「走到傳喚鈴→選這位僱員→開道具欄」那一串任務。</summary>
    /// <remarks>
    /// ⚠️ 回 <see langword="true"/> 只代表<b>排進去了</b>，不代表已經開好；呼叫端要自己等到
    /// <see cref="IsBusy"/> 變 <see langword="false"/> 而且視窗真的在。
    /// </remarks>
    /// <param name="retainerName">必須是目前登入角色的僱員。</param>
    /// <param name="accepted">AutoRetainer 收下了沒有（前置條件不成立時它回 false 並寫一行記錄）。</param>
    public static bool TryEnqueueOpenRetainerItemStorage(string retainerName, out bool accepted)
    {
        try
        {
            accepted = OpenRetainerItemStorageGate.Value.InvokeFunc(retainerName);
            return true;
        }
        catch (Exception ex) when (IsIpcFailure(ex))
        {
            Svc.Log.Information(
                $"[AutoRetainerIpc] EnqueueOpenRetainerItemStorage 呼叫失敗（{ex.GetType().Name}）：{ex.Message}");
            accepted = false;
            return false;
        }
    }

    /// <summary>請 AutoRetainer 排入「把開著的僱員視窗關掉、退回野外」。</summary>
    /// <remarks>📌 什麼都沒開時呼叫也是安全的（提供端自己會回報沒事可做）。</remarks>
    /// <returns>IPC 呼叫本身是否成功。</returns>
    public static bool TryEnqueueCloseRetainer()
    {
        try
        {
            CloseRetainerGate.Value.InvokeAction();
            return true;
        }
        catch (Exception ex) when (IsIpcFailure(ex))
        {
            Svc.Log.Information(
                $"[AutoRetainerIpc] EnqueueCloseRetainer 呼叫失敗（{ex.GetType().Name}）：{ex.Message}");
            return false;
        }
    }
}

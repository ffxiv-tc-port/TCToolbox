using System;
using System.Collections.Generic;
using System.Text;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace TCToolbox.Core;

/// <summary>
/// 「同一扇視窗按過就不要再按，直到它真的收掉」的共用閘門。
/// </summary>
/// <remarks>
/// 位址<b>只做等值比較，永遠不解參考</b>。
/// ⚠️ 刻意<b>不</b>把 <c>PostRefresh</c> 也當解除點：它有可能在「關閉中」那幾幀觸發，
/// 那會把封鎖提早解除，正好把這道防線變成沒有。
/// <para>⚠️ 只在主執行緒使用（與 <see cref="Throttle"/> 同一個前提）。</para>
/// </remarks>
internal static unsafe class AddonPressGuard
{
    /// <summary>按下之後最久封鎖多久（毫秒）。到期＝判定「上一次沒生效」而不是「正在關閉」。</summary>
    private const int ReleaseTimeoutMs = 2_000;

    /// <summary>
    /// 多次互動窗（Talk 類）的逃生口：同一實例按過之後，至少隔這麼多個 framework tick 才准再按。
    /// </summary>
    public const int RoutineRePressEscapeFrames = 15;

    /// <summary>槽位回收掃描的間隔（幀）。</summary>
    private const int SweepIntervalFrames = 60;

    /// <summary>
    /// 槽位回收的<b>年齡下限</b>（毫秒）：只回收「既有規則已經一定會放行」的紀錄。
    /// </summary>
    private const int SweepMinAgeMs = 30_000;

    /// <summary>位址表膨脹到這個數量時，順手清掉太久沒動的紀錄（正常情況下表裡只有個位數）。</summary>
    private const int PruneThreshold = 64;

    /// <summary>超過這麼久沒再按過的位址紀錄，清理時可以丟（早就過了 <see cref="ReleaseTimeoutMs"/>）。</summary>
    private const int PruneAgeMs = 60_000;

    /// <summary>
    /// 「回答一次即終結」的視窗：一個實例不管參數，只准按一次（直到走完生命週期或逾時）。
    /// </summary>
    /// <remarks>
    /// 收錄判準＝「對它送任何一組 callback／點任何一顆鈕，窗就會關掉」：
    /// 之後不管送什麼都是對關閉中的窗送第二次。
    /// </remarks>
    private static readonly HashSet<string> MergedKeyAddons = new(StringComparer.Ordinal)
    {
        "SelectYesno",
        "SelectOk",
        "SelectString",
        "SelectIconString",
        "InputString",
        "InputNumeric",
        "ContextMenu",
        "ContextIconMenu",
        "MaterializeDialog",
        "CharaMakeDataImport",
        "RetainerTaskAsk",
        "RetainerTaskResult",
        "SatisfactionSupplyResult",
        "JournalAccept",
        "PvpReward",
        "Talk",
    };

    /// <summary>
    /// 多次互動窗：按了不會關、重按是流程本身。逃生口用 <see cref="RoutineRePressEscapeFrames"/>。
    /// </summary>
    /// <remarks>
    /// 這些窗按下去不會進入「關閉中」，2 秒封鎖只會把既有的重試節奏拉長，沒有換到任何防護。
    /// </remarks>
    private static readonly HashSet<string> RoutineAddons = new(StringComparer.Ordinal)
    {
        "Talk",
        "CollectablesShop",
        "FreeCompanyChest",
        "GrandCompanyExchange",
        "TripleTriadCoinExchange",
        "_Notification",
    };

    /// <summary>
    /// 已經<b>實機證實</b>是「常駐」的視窗名：關閉只是被設成不可見，實例與位址永遠留在
    /// <c>AllLoadedUnitsList</c> 裡，<see cref="AddonEvent.PreFinalize"/> 與
    /// <see cref="AddonEvent.PostSetup"/> <b>兩個都不會再發生</b>。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>加名字進來的代價：這份名單同時是「送出前必須可見」的名單。</b>
    /// </remarks>
    private static readonly HashSet<string> PersistentAddons = new(StringComparer.Ordinal)
    {
        "ContextMenu",
        "ContextIconMenu",
    };

    /// <summary>
    /// <see cref="PersistentAddons"/> 裡的窗被按下之後，最少要<b>連續</b>觀察到它「還在清單裡、
    /// 但已經被隱藏」這麼多幀，才把按下記號解除。
    /// </summary>
    private const int HiddenReleaseFrames = 20;

    private readonly record struct PressRecord(DateTime At, ulong Frame);

    private sealed class AddressRecords(string addonName)
    {
        public string AddonName { get; } = addonName;
        public Dictionary<string, PressRecord> ByParam { get; } = new(StringComparer.Ordinal);
        public DateTime LastAt { get; set; }

        /// <summary>這個位址最後一次被按下時的幀序（給 <c>SweepReleasedRecords</c> 判「幀數也夠久了」）。</summary>
        public ulong LastFrame { get; set; }

        /// <summary>
        /// 連續觀察到「這個位址還在清單裡、但那扇窗已經被隱藏」的幀數。
        /// 只有 <see cref="PersistentAddons"/> 裡的窗名會累加；看到可見（或查不到）就歸零，
        /// 重新按下也歸零。累到 <see cref="HiddenReleaseFrames"/> 就整筆解除。
        /// </summary>
        public int HiddenFrames { get; set; }
    }

    /// <summary>位址 → 這個實例被按過的參數組。位址只當字典鍵，從不解參考。</summary>
    private static readonly Dictionary<nint, AddressRecords> Pressed = new();

    private static bool watching;
    private static ulong frameCount;
    private static IAddonLifecycle.AddonEventDelegate? lifecycleHandler;

    /// <summary>
    /// 登記「即將對這扇視窗送出動作」（不分參數：一個實例只准一次）。<b>回 <see langword="false"/> ＝這一幀絕對不能送。</b>
    /// </summary>
    /// <remarks>
    /// 呼叫點要放在<b>緊接著送出動作之前</b>——這支一回 <see langword="true"/> 就已經把
    /// 「按過了」記下去，登記完卻不按的話會白白封鎖到解除為止。
    /// </remarks>
    public static bool TryBeginPress(string addonName, AtkUnitBase* addon) =>
        TryBeginPress(addonName, addon, string.Empty);

    /// <summary>
    /// 登記「即將對這扇視窗送出這一組參數」。名稱直接從 addon 讀（呼叫端已經解參考過它才會走到這裡）。
    /// </summary>
    /// <remarks>
    /// 🔴🔴 名稱一定要走 <c>UiHelper.ReadAddonName</c> 這種<b>有界</b>讀法，
    /// <b>不可以</b>用 CS 產生的 <c>NameString</c>（無上限的 null-terminated 掃描）：
    /// 這支守衛被呼叫的時機正好是「這扇窗可能正在關閉」，
    /// 在判定安全<b>之前</b>先對它做無界讀取，等於守衛自己去踩它要防的那顆雷。
    /// </remarks>
    public static bool TryBeginPress(AtkUnitBase* addon, string paramKey)
    {
        if (addon == null) return false;
        return TryBeginPress(UiHelper.ReadAddonName(addon), addon, paramKey);
    }

    /// <summary>
    /// 登記「即將對這扇視窗送出這一組參數」。<b>回 <see langword="false"/> ＝這一幀絕對不能送。</b>
    /// </summary>
    /// <param name="addonName">視窗名稱（決定是不是 <see cref="MergedKeyAddons"/>／<see cref="RoutineAddons"/>，也用在 log）。</param>
    /// <param name="addon">實例位址，只做等值比較。</param>
    /// <param name="paramKey">參數組的字串形狀（<see cref="DescribeCallback"/>／<c>btn:NodeId</c>…）。</param>
    public static bool TryBeginPress(string addonName, AtkUnitBase* addon, string paramKey)
    {
        if (addon == null) return false;

        addonName ??= string.Empty;
        var key = MergedKeyAddons.Contains(addonName) ? string.Empty : paramKey ?? string.Empty;
        return TryBeginPressCore(addonName, addon, key, RoutineAddons.Contains(addonName));
    }

    /// <summary>
    /// 多次互動窗（Talk 類）專用：同一實例按過之後 <see cref="RoutineRePressEscapeFrames"/> 幀內不再按。
    /// </summary>
    public static bool TryBeginRoutinePress(string addonName, AtkUnitBase* addon)
    {
        if (addon == null) return false;
        return TryBeginPressCore(addonName ?? string.Empty, addon, string.Empty, routine: true);
    }

    /// <summary>把一組 callback 參數壓成鍵（型別＋數值，順序有意義）。</summary>
    public static string DescribeCallback(bool updateState, object[] values)
    {
        var sb = new StringBuilder(8 + (values?.Length ?? 0) * 6);
        sb.Append(updateState ? "cb1:" : "cb0:");
        if (values == null) return sb.ToString();

        for (var i = 0; i < values.Length; i++)
        {
            if (i > 0) sb.Append(',');
            switch (values[i])
            {
                case int v: sb.Append('i').Append(v); break;
                case uint v: sb.Append('u').Append(v); break;
                case bool v: sb.Append(v ? "b1" : "b0"); break;
                case AtkValue v: sb.Append('a').Append((int)v.Type).Append(':').Append(v.Int64); break;
                default: sb.Append('?'); break;
            }
        }

        return sb.ToString();
    }

    /// <summary>外掛卸載時硬拆所有監聽器（不留指向本組件的委派）。</summary>
    public static void ForceTeardown()
    {
        if (watching)
        {
            Svc.Framework.Update -= OnFrameworkUpdate;
            if (lifecycleHandler != null)
            {
                Svc.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, lifecycleHandler);
                Svc.AddonLifecycle.UnregisterListener(AddonEvent.PreFinalize, lifecycleHandler);
            }
        }

        lifecycleHandler = null;
        watching = false;
        Pressed.Clear();
    }

    private static bool TryBeginPressCore(string addonName, AtkUnitBase* addon, string paramKey, bool routine)
    {
        EnsureWatching();

        var address = (nint)addon;
        var now = DateTime.UtcNow;

        //    ⚠️ 這一道**不是**「擋得住正在關閉中的窗」的檢查 —— IsReady 的三關（非 null／IsVisible／
        //    LoadedState == Loaded）在拆除途中是**全過**的（本檔開頭那段講的就是這件事），
        //    單獨看它一個東西都擋不到。**這個結論不可以當成通用結論搬去別的地方用。**
        // 🔴 只對 PersistentAddons 生效。名單外的窗完全不走這一行，行為與改動前逐字相同——
        //    這是刻意的：本 repo 至少有兩處**刻意不判可見**的送出（AutoPlayerCommend 對常駐 HUD
        //    _Notification、AutoMaterialize 在 PostSetup 當下就按且沒有 PostDraw 重試），
        //    無差別加上這道檢查會把它們靜默改成不送。
        if (PersistentAddons.Contains(addonName) && !UiHelper.IsReady(addon)) return false;

        if (Pressed.TryGetValue(address, out var records) && records.ByParam.TryGetValue(paramKey, out var pressed))
        {
            if (routine)
            {
                var frames = frameCount - pressed.Frame;
                if (frames < (ulong)RoutineRePressEscapeFrames)
                {
                    // 多次互動窗的正常等待：每幀都會回來問，寫 Debug 且節流。
                    if (Throttle.Pass($"AddonPressGuard-RoutineHold-{addonName}", 1_000))
                        Svc.Log.Debug(
                            $"[AddonPressGuard] 「{addonName}」（實例 0x{address:X}）{frames} 幀前才按過，" +
                            $"等滿 {RoutineRePressEscapeFrames} 幀再按。");

                    return false;
                }

                if (Throttle.Pass($"AddonPressGuard-RoutineEscape-{addonName}", 1_000))
                    Svc.Log.Debug(
                        $"[AddonPressGuard] 「{addonName}」（實例 0x{address:X}）按下後 {frames} 幀仍是同一實例，" +
                        "多次互動窗走逃生口再按一次。");
            }
            else
            {
                var waitedMs = (now - pressed.At).TotalMilliseconds;
                if (waitedMs < ReleaseTimeoutMs)
                {
                    // 🔴 這就是崩潰的那一幀。診斷寫 Information（使用者跑 LogLevel 1），並節流免得洗版。
                    if (Throttle.Pass($"AddonPressGuard-Hold-{addonName}", 1_000))
                        Svc.Log.Information(
                            $"[AddonPressGuard] 「{addonName}」（實例 0x{address:X}，參數 {DescribeKey(paramKey)}）" +
                            "按過之後還沒觀察到它收掉，這一幀不再送——對關閉中的視窗送 callback 是攔不到的存取違規。");

                    return false;
                }

                if (Throttle.Pass($"AddonPressGuard-Release-{addonName}", 10_000))
                    Svc.Log.Information(
                        $"[AddonPressGuard] 「{addonName}」（實例 0x{address:X}，參數 {DescribeKey(paramKey)}）按下後 {waitedMs:F0} 毫秒" +
                        "既沒有被銷毀也沒有重新建立，判定為「上一次按下沒生效」而不是「正在關閉」，解除封鎖讓呼叫端重試。");
            }
        }

        if (records == null)
        {
            PruneIfCrowded(now);
            records = new AddressRecords(addonName);
            Pressed[address] = records;
        }

        records.ByParam[paramKey] = new PressRecord(now, frameCount);
        records.LastAt = now;
        records.LastFrame = frameCount;
        // 🔴 承重：隱藏解除是「從最後一次按下起算」連續隱藏幾幀。漏掉這一行的話，
        //    「窗已經隱藏了 15 幀 → 逃生口補按一次」之後只要再 5 幀就會解除記號。
        records.HiddenFrames = 0;
        LogPressDiag(addonName, address, paramKey);
        return true;
    }

    /// <summary>
    /// 跨外掛「按窗診斷」：在<b>真的送出按壓</b>的那一刻寫一行 <c>Debug</c>。
    /// </summary>
    /// <remarks>
    /// 🔴 刻意<b>不節流</b>（漏掉一次就是漏掉一個對照樣本）；
    /// 🔴 位址只印數值，<b>不解參考</b>。
    /// </remarks>
    private static void LogPressDiag(string addonName, nint address, string paramKey)
    {
        var name = string.IsNullOrEmpty(addonName) ? "?" : addonName;
        Svc.Log.Debug($"[按窗診斷] plugin=TCToolbox addon={name} addr=0x{address:X} key={paramKey ?? string.Empty}");
    }

    private static string DescribeKey(string paramKey) => paramKey.Length == 0 ? "（不分）" : paramKey;

    /// <summary>
    /// 掛上解除封鎖用的全域監聽器與幀計數器（重複呼叫是 no-op）。
    /// </summary>
    /// <remarks>
    /// 掛上去之後就不再拆（只在 <see cref="ForceTeardown"/> 拆）：監聽器只做一次字典移除，
    /// 成本可忽略，而動態掛／拆比較容易留下懸空的監聽器。
    /// </remarks>
    public static void EnsureWatching()
    {
        if (watching) return;

        lifecycleHandler = OnAddonLifecycle;
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, lifecycleHandler);
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, lifecycleHandler);
        Svc.Framework.Update += OnFrameworkUpdate;
        watching = true;
    }

    private static void OnFrameworkUpdate(IFramework framework)
    {
        frameCount++;
        ReleaseHiddenPersistent();
        if (frameCount % SweepIntervalFrames == 0) SweepReleasedRecords();
    }

    /// <summary>這一幀對某個位址觀察到的可見狀態。</summary>
    /// <remarks>
    /// 🔴 <b>零值必須是一個「不解除」的答案。</b>任何忘了指派／查詢失敗的路徑落在零值上時，
    /// 結果都必須是「維持封鎖」而不是「放行」——後者是崩潰，前者只是多等一下。
    /// </remarks>
    private enum AddonVisibility
    {
        /// <summary>這一幀在同名清單裡找不到這個位址。<b>這是零值</b>，語意是「不知道」。</summary>
        Unknown = 0,

        /// <summary>找到了，而且看得見。</summary>
        Visible = 1,

        /// <summary>找到了，但已經被設成不可見。</summary>
        Hidden = 2,
    }

    /// <summary>要移除的位址（可重用緩衝；只在 framework 執行緒上用）。</summary>
    private static readonly List<nint> HiddenReleaseBuf = [];

    /// <summary>「這個窗名第一次走隱藏解除」只寫一行 Information，之後不再寫。</summary>
    private static readonly HashSet<string> HiddenReleaseReported = new(StringComparer.Ordinal);

    /// <summary>
    /// <see cref="PersistentAddons"/> 專用的第二條解除路徑：連續觀察到「位址還在清單裡、
    /// 但那扇窗已經隱藏」<see cref="HiddenReleaseFrames"/> 幀就解除按下記號。
    /// </summary>
    /// <remarks>
    /// <b>只有「找到了、而且不可見」才累加</b>，「查不到」與「找到但看得見」<b>一律歸零</b>。
    /// ⇒ 同名實例超過掃描上限、窗已從清單移除、查詢本身出錯……每一種都表現成「繼續封鎖」。
    /// </remarks>
    private static void ReleaseHiddenPersistent()
    {
        if (Pressed.Count == 0) return;

        HiddenReleaseBuf.Clear();
        foreach (var (address, records) in Pressed)
        {
            if (!PersistentAddons.Contains(records.AddonName)) continue;

            if (LookUpVisibility(records.AddonName, address) != AddonVisibility.Hidden)
            {
                // 看得見（還開著，或落在關閉中的危險窗口內）／這一幀查不到 ⇒ 歸零重數。
                records.HiddenFrames = 0;
                continue;
            }

            if (++records.HiddenFrames < HiddenReleaseFrames) continue;

            HiddenReleaseBuf.Add(address);
            if (HiddenReleaseReported.Add(records.AddonName))
            {
                Svc.Log.Information(
                    $"[AddonPressGuard] 「{records.AddonName}」是常駐視窗（隱藏而不銷毀），" +
                    $"按下記號改由「連續隱藏 {HiddenReleaseFrames} 幀」解除。這一行每個視窗名每次遊戲只寫一次。");
            }
        }

        if (HiddenReleaseBuf.Count == 0) return;

        foreach (var address in HiddenReleaseBuf) Pressed.Remove(address);
        HiddenReleaseBuf.Clear();
    }

    /// <summary>
    /// 在 <paramref name="addonName"/> 的<b>所有</b>同名實例裡找出 <paramref name="address"/>，
    /// 回報它這一幀看不看得見；找不到就回 <see cref="AddonVisibility.Unknown"/>。
    /// </summary>
    /// <remarks>
    /// 🔴 位址只用來做等值比較；解參考的是 <c>GetAddonByName</c> 這一幀交回來的指標。
    /// </remarks>
    private static AddonVisibility LookUpVisibility(string addonName, nint address)
    {
        if (string.IsNullOrEmpty(addonName) || address == nint.Zero) return AddonVisibility.Unknown;

        for (var index = 1; index <= UiHelper.MaxAddonInstanceScan; index++)
        {
            var unit = Svc.GameGui.GetAddonByName<AtkUnitBase>(addonName, index);
            if (unit == null) break;
            if ((nint)unit != address) continue;

            return unit->IsVisible ? AddonVisibility.Visible : AddonVisibility.Hidden;
        }

        return AddonVisibility.Unknown;
    }

    /// <summary>回收「已經不可能再擋住任何人」的槽位。</summary>
    /// <remarks>
    /// 🔑 <b>為什麼這不會弱化防護</b>：回收條件是「距離最後一次按下超過
    /// <see cref="SweepMinAgeMs"/>」<b>而且</b>「已經過了 <see cref="RoutineRePressEscapeFrames"/> 幀」。
    /// 同時滿足這兩條的紀錄，<see cref="TryBeginPressCore"/> 本來就<b>一定會放行</b>
    /// （逾時放行只要 <see cref="ReleaseTimeoutMs"/>＝2 秒、逃生口只要 15 幀），
    /// 所以移除它<b>不會改變任何一次「按／不按」的判定</b>，只是少寫一行 log、少佔一格字典。
    /// </remarks>
    private static void SweepReleasedRecords()
    {
        if (Pressed.Count == 0) return;

        var now = DateTime.UtcNow;
        List<nint>? stale = null;

        foreach (var (address, records) in Pressed)
        {
            if ((now - records.LastAt).TotalMilliseconds < SweepMinAgeMs) continue;
            if (frameCount - records.LastFrame < (ulong)RoutineRePressEscapeFrames) continue;

            (stale ??= []).Add(address);
        }

        if (stale == null) return;

        foreach (var address in stale) Pressed.Remove(address);
    }

    /// <summary>該位址走完（或重新開始）生命週期：把它底下的紀錄清掉。</summary>
    /// <remarks>
    /// 🔴🔴 <b><see cref="AddonEvent.PostSetup"/> 只清「不是這一幀才登記的」紀錄。</b>
    /// 「這一幀才登記的不清」把順序這個變數整個拿掉：不管誰先誰後，結果都一樣。
    /// <see cref="AddonEvent.PreFinalize"/> 不做這個豁免：它的意思是「這一扇確定走到終點」，
    /// 清掉才是對的；而且窗都沒了，後面也不會再有人對它送東西。
    /// </remarks>
    private static void OnAddonLifecycle(AddonEvent type, AddonArgs args)
    {
        var address = args.Addon.Address;
        if (address == nint.Zero) return;

        if (type != AddonEvent.PostSetup)
        {
            Pressed.Remove(address);
            return;
        }

        if (!Pressed.TryGetValue(address, out var records)) return;

        List<string>? stale = null;
        foreach (var (paramKey, record) in records.ByParam)
        {
            if (record.Frame == frameCount) continue;
            (stale ??= []).Add(paramKey);
        }

        // 整筆都是這一幀才登記的 ⇒ 是「模組剛在這次 PostSetup 派送裡按下」，不是新的一扇。
        if (stale == null) return;

        foreach (var paramKey in stale) records.ByParam.Remove(paramKey);
        if (records.ByParam.Count == 0) Pressed.Remove(address);
    }

    private static void PruneIfCrowded(DateTime now)
    {
        if (Pressed.Count < PruneThreshold) return;

        var stale = new List<nint>();
        foreach (var (address, records) in Pressed)
        {
            if ((now - records.LastAt).TotalMilliseconds > PruneAgeMs) stale.Add(address);
        }

        foreach (var address in stale) Pressed.Remove(address);
    }
}

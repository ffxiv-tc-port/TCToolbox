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
/// 位址<b>只做等值比較，永遠不解參考</b>。解除點：位址從同名清單消失（每幀輪詢）、同位址 <c>PostSetup</c>
/// （這一幀才登記的不清）、常駐窗連續隱藏 N 幀、逃生口。<c>PreFinalize</c> 是<b>上鎖點</b>：鎖到位址消失或同位址重建。
/// ⚠️ 刻意<b>不</b>把 <c>PostRefresh</c> 也當解除點：它有可能在「關閉中」那幾幀觸發，會把封鎖提早解除。
/// 時鐘一律是本類別在 <c>Framework.Update</c> 數的幀，不用牆鐘（卡頓時牆鐘會比危險窗口先到期）。
/// <para>⚠️ 只在主執行緒使用（與 <see cref="Throttle"/> 同一個前提）。任何不確定一律回 <see langword="false"/>。</para>
/// </remarks>
internal static unsafe class AddonPressGuard
{
    /// <summary>終結按法（併鍵窗、或不帶參數）的逃生口與熱窗長度（幀）。到期＝判定「上一次沒生效」而不是「正在關閉」。</summary>
    private const int TerminalEscapeFrames = 90;

    /// <summary>
    /// 多次互動窗（Talk 類）的逃生口：同一實例按過之後，至少隔這麼多個 framework tick 才准再按。
    /// </summary>
    public const int RoutineRePressEscapeFrames = 15;

    private const int SweepIntervalFrames = 60;

    /// <summary>槽位回收的年齡下限（幀）：只回收「既有規則已經一定會放行」的紀錄，遠大於逃生口。</summary>
    private const int SweepMinAgeFrames = 1800;

    /// <summary>位址表膨脹到這個數量時，先回收已過逃生口的冷紀錄；回收不到就這一發不放行。</summary>
    private const int PruneThreshold = 64;

    /// <summary>輪詢同名實例的上限（遊戲同名實例上限）；掃到第一個空的就提早停。</summary>
    private const int MaxAddonIndex = 256;

    /// <summary>
    /// 「回答一次即終結」的視窗：一個實例不管參數，只准按一次（直到走完生命週期或逃生口）。
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
    /// 已經<b>實機證實</b>是「常駐」的視窗名：關閉只是被設成不可見，位址永遠留在清單裡，
    /// <see cref="AddonEvent.PreFinalize"/> 與 <see cref="AddonEvent.PostSetup"/> 都不會再發生。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>這份名單同時是「送出前必須可見」的名單</b>（與「連續隱藏才解除」互為反面）。
    /// 把會被銷毀的窗誤收進來，等於在它拆除途中提早解除封鎖。
    /// </remarks>
    private static readonly HashSet<string> PersistentAddons = new(StringComparer.Ordinal)
    {
        "ContextMenu",
        "ContextIconMenu",
        "Talk",
    };

    /// <summary>常駐窗按下後，要<b>連續</b>觀察到「還在清單裡、但已隱藏」這麼多幀才解除。</summary>
    private const int HiddenReleaseFrames = 20;

    /// <param name="Frame">按下時的守衛幀號。</param>
    /// <param name="Routine">以多次互動窗身分登記；為 <see langword="false"/> 且鍵為空＝終結按法。</param>
    private readonly record struct PressRecord(ulong Frame, bool Routine);

    private sealed class AddressRecords(string addonName)
    {
        public string AddonName { get; set; } = addonName;
        public Dictionary<string, PressRecord> ByParam { get; } = new(StringComparer.Ordinal);
        public ulong LastFrame { get; set; }

        /// <summary>連續觀察到「還在清單裡、但已隱藏」的幀數；只有常駐窗會累加，看得見或重新按下就歸零。</summary>
        public int HiddenFrames { get; set; }

        /// <summary>登記時在 <see cref="AddonName"/> 的同名清單裡找得到這個位址；只有它成立才准用「從清單消失」解除。</summary>
        /// <remarks>名稱對不上時清單裡永遠找不到它，不設這道就會在下一幀被當成「已消失」而提早解除。</remarks>
        public bool Listed { get; set; }

        /// <summary>看過 <see cref="AddonEvent.PreFinalize"/>：在位址消失或同位址重建之前，任何按法都不准，也沒有逃生口。</summary>
        public bool Finalizing { get; set; }
    }

    /// <summary>位址 → 這個實例被按過的參數組。位址只當字典鍵，從不解參考。</summary>
    private static readonly Dictionary<nint, AddressRecords> Pressed = new();

    /// <summary>曾經守護過的窗名：這些名字的實例一進 <see cref="AddonEvent.PreFinalize"/> 就上鎖（即使還沒按過）。</summary>
    private static readonly HashSet<string> GuardedNames = new(StringComparer.Ordinal);

    private static readonly List<nint> ReleaseBuf = [];
    private static readonly List<string> StaleKeysBuf = [];

    /// <summary>「這個窗名第一次走隱藏解除」只寫一行 Information，之後不再寫。</summary>
    private static readonly HashSet<string> HiddenReleaseReported = new(StringComparer.Ordinal);

    private static bool watching;
    private static ulong frameCount;
    private static IAddonLifecycle.AddonEventDelegate? lifecycleHandler;

    /// <summary>
    /// 登記「即將對這扇視窗送出終結動作」（不分參數：一個實例只准一次）。<b>回 <see langword="false"/> ＝這一幀絕對不能送。</b>
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
    /// 這支守衛被呼叫的時機正好是「這扇窗可能正在關閉」。讀出空字串＝不放行。
    /// </remarks>
    public static bool TryBeginPress(AtkUnitBase* addon, string paramKey)
    {
        if (addon == null) return false;
        return TryBeginPress(UiHelper.ReadAddonName(addon), addon, paramKey);
    }

    /// <summary>
    /// 登記「即將對這扇視窗送出這一組參數」。<b>回 <see langword="false"/> ＝這一幀絕對不能送。</b>
    /// </summary>
    /// <param name="addonName">視窗名稱（決定併鍵／多次互動／常駐，也是輪詢解除用的名字）。空字串一律不放行。</param>
    /// <param name="addon">實例位址，只做等值比較。</param>
    /// <param name="paramKey">參數組的字串形狀（<see cref="DescribeCallback"/>／<c>btn:NodeId</c>…）。</param>
    public static bool TryBeginPress(string addonName, AtkUnitBase* addon, string paramKey)
    {
        if (addon == null || string.IsNullOrEmpty(addonName)) return false;

        var key = MergedKeyAddons.Contains(addonName) ? string.Empty : paramKey ?? string.Empty;
        return TryBeginPressCore(addonName, addon, key, RoutineAddons.Contains(addonName));
    }

    /// <summary>
    /// 多次互動窗（Talk 類）專用：同一實例按過之後 <see cref="RoutineRePressEscapeFrames"/> 幀內不再按。
    /// </summary>
    public static bool TryBeginRoutinePress(string addonName, AtkUnitBase* addon)
    {
        if (addon == null || string.IsNullOrEmpty(addonName)) return false;
        return TryBeginPressCore(addonName, addon, string.Empty, routine: true);
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
        GuardedNames.Clear();
    }

    private static bool TryBeginPressCore(string addonName, AtkUnitBase* addon, string paramKey, bool routine)
    {
        EnsureWatching();

        var address = (nint)addon;
        var frame = frameCount;

        // 🔴 常駐窗送出前必須可見；只對 PersistentAddons 判。
        // 🔴 刻意不對名單外的窗判可見：AutoPlayerCommend（_Notification）與 AutoMaterialize（PostSetup 當下就按）
        //    刻意不判可見，無差別加上這道檢查會把它們靜默改成不送。
        if (PersistentAddons.Contains(addonName) && !UiHelper.IsReady(addon)) return false;

        GuardedNames.Add(addonName);
        Pressed.TryGetValue(address, out var records);
        if (records != null)
        {
            if (records.Finalizing)
            {
                LogHold(addonName, address, paramKey, "已經看到它 PreFinalize、還沒看到它消失");
                return false;
            }

            // 終結按法還熱著：同一扇窗換任何一組參數都不准——那之後它就是在關閉中。
            if (paramKey.Length != 0 &&
                records.ByParam.TryGetValue(string.Empty, out var answeredRec) &&
                !answeredRec.Routine &&
                frame - answeredRec.Frame < TerminalEscapeFrames)
            {
                LogHold(addonName, address, paramKey, "終結按法按過之後還沒觀察到它收掉");
                return false;
            }

            if (records.ByParam.TryGetValue(paramKey, out var pressed) && !PassesEscape(addonName, address, paramKey, pressed, routine, frame))
                return false;
        }

        if (records == null)
        {
            if (!MakeRoom())
            {
                if (Throttle.Pass("AddonPressGuard-Full", 10_000))
                    Svc.Log.Information($"[AddonPressGuard] 按下紀錄已滿 {PruneThreshold} 筆且都還熱著，「{addonName}」這一發不送。");
                return false;
            }

            records = new AddressRecords(addonName);
            Pressed[address] = records;
        }

        if (!records.Listed && IsListed(addonName, address))
        {
            records.AddonName = addonName;
            records.Listed = true;
        }

        records.ByParam[paramKey] = new PressRecord(frame, routine);
        records.LastFrame = frame;
        // 🔴 承重：隱藏解除是「從最後一次按下起算」連續隱藏幾幀。
        records.HiddenFrames = 0;
        LogPressDiag(addonName, address, paramKey);
        return true;
    }

    /// <summary>同位址同參數按過：還在逃生口內回 <see langword="false"/>；到期就記一行並放行。</summary>
    private static bool PassesEscape(string addonName, nint address, string paramKey, PressRecord pressed, bool routine, ulong frame)
    {
        var frames = frame - pressed.Frame;
        if (routine)
        {
            if (frames < (ulong)RoutineRePressEscapeFrames)
            {
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
            return true;
        }

        if (frames < TerminalEscapeFrames)
        {
            LogHold(addonName, address, paramKey, "按過之後還沒觀察到它收掉");
            return false;
        }

        if (Throttle.Pass($"AddonPressGuard-Release-{addonName}", 10_000))
            Svc.Log.Information(
                $"[AddonPressGuard] 「{addonName}」（實例 0x{address:X}，參數 {DescribeKey(paramKey)}）按下後 {frames} 幀" +
                "既沒有被銷毀也沒有重新建立，判定為「上一次按下沒生效」而不是「正在關閉」，解除封鎖讓呼叫端重試。");
        return true;
    }

    /// <summary>被擋那一幀的診斷：要使用者回報的診斷寫 Information，每個窗名 1 秒一則。</summary>
    private static void LogHold(string addonName, nint address, string paramKey, string reason)
    {
        if (Throttle.Pass($"AddonPressGuard-Hold-{addonName}", 1_000))
            Svc.Log.Information(
                $"[AddonPressGuard] 「{addonName}」（實例 0x{address:X}，參數 {DescribeKey(paramKey)}）{reason}，" +
                "這一幀不再送——對關閉中的視窗送 callback 是攔不到的存取違規。");
    }

    /// <summary>
    /// 跨外掛「按窗診斷」：在<b>真的送出按壓</b>的那一刻寫一行 <c>Verbose</c>。
    /// </summary>
    /// <remarks>
    /// 🔴 刻意<b>不節流</b>（漏掉一次就是漏掉一個對照樣本）；格式全艦隊逐字統一。
    /// 🔴 位址只印數值，<b>不解參考</b>。
    /// </remarks>
    private static void LogPressDiag(string addonName, nint address, string paramKey)
    {
        Svc.Log.Verbose($"[按窗診斷] plugin=TCToolbox addon={addonName} addr=0x{address:X} key={paramKey ?? string.Empty}");
    }

    private static string DescribeKey(string paramKey) => paramKey.Length == 0 ? "（不分）" : paramKey;

    /// <summary>
    /// 掛上解除封鎖用的全域監聽器與幀計數器（重複呼叫是 no-op）。
    /// </summary>
    /// <remarks>
    /// 掛上去之後就不再拆（只在 <see cref="ForceTeardown"/> 拆）：動態掛／拆比較容易留下懸空的監聽器。
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

    /// <summary>🔴 遞增必須是第一行、前面不可以有任何條件：時鐘一停，逃生口就跟著失準。</summary>
    private static void OnFrameworkUpdate(IFramework framework)
    {
        frameCount++;
        PollRecords();
        if (frameCount % SweepIntervalFrames == 0) SweepColdRecords();
    }

    /// <summary>這一幀在同名清單裡對某個位址的觀察。</summary>
    /// <remarks>🔴 <b>零值必須是一個「不解除」的答案</b>：查詢本身不成立時維持封鎖，後者只是多等一下。</remarks>
    private enum AddonPresence
    {
        /// <summary>查詢不成立（名稱空、位址 0、同名實例超過掃描上限）。<b>這是零值</b>。</summary>
        Unknown = 0,

        /// <summary>掃完同名清單都沒有這個位址＝那扇窗已經收乾淨。</summary>
        NotListed = 1,

        Visible = 2,

        Hidden = 3,
    }

    /// <summary>
    /// 每幀的解除路徑：①登記時找得到、現在從同名清單消失 ⇒ 解除（含 PreFinalize 鎖）；
    /// ②常駐窗連續隱藏 <see cref="HiddenReleaseFrames"/> 幀 ⇒ 解除。
    /// </summary>
    /// <remarks>
    /// 判準刻意<b>不</b>用「還可不可見／還 ready 嗎」當「窗不見了」（常駐窗例外）：
    /// 拆除途中那幾幀三關全過，拿那些當解除點會正好在最危險的幾幀把封鎖拆掉。
    /// </remarks>
    private static void PollRecords()
    {
        if (Pressed.Count == 0) return;

        ReleaseBuf.Clear();
        foreach (var (address, records) in Pressed)
        {
            var persistent = PersistentAddons.Contains(records.AddonName);
            var presence = LookUp(records.AddonName, address, readVisibility: persistent);
            var present = presence != AddonPresence.NotListed;
            if (!present)
            {
                if (records.Listed) ReleaseBuf.Add(address);
                else records.HiddenFrames = 0;
                continue;
            }

            if (!persistent || records.Finalizing) continue;
            if (presence != AddonPresence.Hidden)
            {
                records.HiddenFrames = 0;
                continue;
            }

            if (++records.HiddenFrames < HiddenReleaseFrames) continue;

            ReleaseBuf.Add(address);
            if (HiddenReleaseReported.Add(records.AddonName))
            {
                Svc.Log.Information(
                    $"[AddonPressGuard] 「{records.AddonName}」是常駐視窗（隱藏而不銷毀），" +
                    $"按下記號改由「連續隱藏 {HiddenReleaseFrames} 幀」解除。這一行每個視窗名每次遊戲只寫一次。");
            }
        }

        foreach (var address in ReleaseBuf) Pressed.Remove(address);
        ReleaseBuf.Clear();
    }

    private static bool IsListed(string addonName, nint address) =>
        LookUp(addonName, address, readVisibility: false) is AddonPresence.Visible or AddonPresence.Hidden;

    /// <summary>在 <paramref name="addonName"/> 的<b>所有</b>同名實例裡找 <paramref name="address"/>。</summary>
    /// <remarks>
    /// 🔴 位址只用來做等值比較；解參考的是 <c>GetAddonByName</c> 這一幀交回來的指標，而且只在
    /// <paramref name="readVisibility"/> 時才讀。不讀可見性時，找到一律回 <see cref="AddonPresence.Visible"/>。
    /// </remarks>
    private static AddonPresence LookUp(string addonName, nint address, bool readVisibility)
    {
        if (string.IsNullOrEmpty(addonName) || address == nint.Zero) return AddonPresence.Unknown;

        for (var index = 1; index <= MaxAddonIndex; index++)
        {
            var unit = Svc.GameGui.GetAddonByName<AtkUnitBase>(addonName, index);
            if (unit == null) return AddonPresence.NotListed;
            if ((nint)unit != address) continue;

            if (!readVisibility) return AddonPresence.Visible;
            return unit->IsVisible ? AddonPresence.Visible : AddonPresence.Hidden;
        }

        return AddonPresence.Unknown;
    }

    /// <summary>回收「已經不可能再擋住任何人」的槽位（不改變任何一次「按／不按」的判定）。</summary>
    private static void SweepColdRecords()
    {
        if (Pressed.Count == 0) return;

        ReleaseBuf.Clear();
        foreach (var (address, records) in Pressed)
        {
            if (records.Finalizing) continue;
            if (frameCount - records.LastFrame < SweepMinAgeFrames) continue;
            ReleaseBuf.Add(address);
        }

        foreach (var address in ReleaseBuf) Pressed.Remove(address);
        ReleaseBuf.Clear();
    }

    /// <summary>表滿時只回收已過終結逃生口、沒上鎖的冷紀錄；回收完仍滿回 <see langword="false"/>。</summary>
    private static bool MakeRoom()
    {
        if (Pressed.Count < PruneThreshold) return true;

        ReleaseBuf.Clear();
        foreach (var (address, records) in Pressed)
        {
            if (records.Finalizing) continue;
            if (frameCount - records.LastFrame >= TerminalEscapeFrames) ReleaseBuf.Add(address);
        }

        foreach (var address in ReleaseBuf) Pressed.Remove(address);
        ReleaseBuf.Clear();
        return Pressed.Count < PruneThreshold;
    }

    /// <summary>該位址進入銷毀（上鎖）或重新開始（解除）生命週期。</summary>
    /// <remarks>
    /// 🔴🔴 <b><see cref="AddonEvent.PostSetup"/> 只清「不是這一幀才登記的」紀錄</b>：監聽器之間的呼叫順序不保證，
    /// 模組在自己的 PostSetup 處理常式裡按下時，不設這條豁免就會被守衛當場清掉。
    /// <see cref="AddonEvent.PreFinalize"/> 把位址鎖住到它從清單消失：事件內回頭按同一扇窗的路徑也擋得住。
    /// </remarks>
    private static void OnAddonLifecycle(AddonEvent type, AddonArgs args)
    {
        var address = args.Addon.Address;
        if (address == nint.Zero) return;

        if (type == AddonEvent.PreFinalize)
        {
            LockFinalizing(address, args);
            return;
        }

        if (!Pressed.TryGetValue(address, out var records)) return;

        records.Finalizing = false;
        StaleKeysBuf.Clear();
        foreach (var (paramKey, record) in records.ByParam)
        {
            if (record.Frame == frameCount) continue;
            StaleKeysBuf.Add(paramKey);
        }

        foreach (var paramKey in StaleKeysBuf) records.ByParam.Remove(paramKey);
        StaleKeysBuf.Clear();
        if (records.ByParam.Count == 0) Pressed.Remove(address);
    }

    private static void LockFinalizing(nint address, AddonArgs args)
    {
        if (Pressed.TryGetValue(address, out var records))
        {
            records.Finalizing = true;
            return;
        }

        if (GuardedNames.Count == 0) return;

        // 鎖不受容量上限限制：上鎖只會讓這扇窗多擋幾幀，消失輪詢會收掉它。
        var name = args.AddonName;
        if (!GuardedNames.Contains(name)) return;

        Pressed[address] = new AddressRecords(name) { Listed = true, Finalizing = true, LastFrame = frameCount };
    }
}

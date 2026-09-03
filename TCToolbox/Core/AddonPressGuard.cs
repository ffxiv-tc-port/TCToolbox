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
/// 🔴🔴 <b>存在的唯一理由是 2026-08-31 的實機崩潰</b>（<c>crash-20260831205734</c>，
/// <c>C0000005</c>，堆疊 <c>ConfirmRequest→FireCallback→ffxiv_dx11+5BE756</c>）：
/// <c>SelectYesno</c> 按下「是」之後有<b>「正在關閉中」的幾幀</b>，這段期間
/// <c>GetAddonByName</c> 仍然回得到實例、<c>IsVisible</c> 與
/// <c>UldManager.LoadedState == Loaded</c> 也<b>三關全過</b>——
/// 也就是說 <see cref="UiHelper.IsReady"/>／<see cref="UiHelper.IsAddonReady"/>
/// <b>擋不住這個窗口</b>。此時再對它 <c>FireCallback</c>／送輸入事件就是原生
/// AccessViolationException（.NET Core 的 corrupted-state exception，
/// <c>try/catch</c> 與任何 SafeWrapper 都攔不到，遊戲當場關閉）。
/// <para>
/// 🔑 <b>做法</b>：按下之前先登記「哪一個實例位址、用哪一組參數被按過」，
/// 在觀察到那扇窗<b>真的走完生命週期</b>之前不准再對同一個位址送同一組參數。
/// 位址<b>只做等值比較，永遠不解參考</b>。
/// 解除封鎖的觀察點是 <c>AddonLifecycle</c> 的兩個事件（<b>不是輪詢</b>，
/// 因為 <c>PostDraw</c> 型的呼叫端在窗消失的那一幀根本不會被叫到）：
/// <list type="bullet">
/// <item><see cref="AddonEvent.PreFinalize"/>＝這一扇正在被銷毀 ⇒ 按過的那扇已經到終點。</item>
/// <item><see cref="AddonEvent.PostSetup"/>＝有新的一扇被建立起來（含位址重用） ⇒ 我們按過的那扇已經不是它了。</item>
/// </list>
/// 兩條監聽器不指定 addon 名稱（全域一對），用位址對照——這樣走 <see cref="UiHelper.FireCallback"/>
/// 的任何視窗都自動涵蓋，不必逐名登記。
/// ⚠️ 刻意<b>不</b>把 <c>PostRefresh</c> 也當解除點：它有可能在「關閉中」那幾幀觸發，
/// 那會把封鎖提早解除，正好把這道防線變成沒有。
/// </para>
/// <para>
/// 📌 <b>粒度＝（視窗位址，參數組）</b>：「同一扇窗連送不同參數」是正常流程
/// （交納視窗逐格填入、僱員清單「選人」之後再「關閉」），只擋「同位址同參數在窗走完前再按」。
/// 例外是 <see cref="MergedKeyAddons"/>：「回答一次即終結」的窗（<c>SelectYesno</c> 族、
/// 按下確認即關的視窗）不管參數是什麼，一個實例只准按一次——對它們而言「是」之後再送「否」
/// 一樣是對關閉中的窗送第二次。
/// </para>
/// <para>
/// 🔴 <b>逾時放行是刻意的</b>（<see cref="ReleaseTimeoutMs"/>）：萬一某扇窗既不 finalize
/// 也不重新 setup（例如上一次的 callback 根本沒生效、視窗就是還開著），
/// 沒有逾時的話呼叫端會<b>永遠</b>按不下去，等於把崩潰換成靜默失效。
/// 逾時值遠大於「關閉中」那幾幀（60fps 下數十毫秒、卡頓時也就數百毫秒），
/// 撐到這個長度還在的視窗依定義是「還開著」而不是「正在關閉」。
/// </para>
/// <para>
/// 📌 <b>多次互動窗（<see cref="RoutineAddons"/>，代表是 <c>Talk</c>）</b>：按一次翻一頁、
/// 窗不會因為被按而消失，重按是常態。守衛照樣記位址，但逃生口改用
/// <see cref="RoutineRePressEscapeFrames"/>（15 幀）而不是 2 秒——關閉中的危險窗口不到 10 幀，
/// 15 幀不落在裡面；每頁多等 0.25 秒幾乎無感。走這個逃生口是常態，寫 Debug 不洗版。
/// （2026-09-02 使用者裁決。）
/// </para>
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
    /// <c>PvpReward</c> 也在裡面：領取一格之後遊戲會把整扇窗重建（AutoClaimPVPRewards 實機觀察），
    /// 在重建前對舊實例點下一格就是同一種形狀。
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
    /// <c>Talk</c>＝翻頁；<c>CollectablesShop</c>＝每按一次交一件；<c>FreeCompanyChest</c>／
    /// <c>GrandCompanyExchange</c>＝分頁圓鈕與軍階分頁（點了視窗還在，直到選上為止重試）；
    /// <c>TripleTriadCoinExchange</c>＝幻卡回收，每按一次送出一張；真正的回收發生在子視窗
    /// <c>ShopCardDialog</c> 上，這扇父窗不會因為被按而關（使用者手動連按是正常流程，
    /// 2 秒封鎖會讓第二次靜默不送、呼叫端再空等到逾時）；
    /// <c>_Notification</c>＝常駐 HUD，永遠不會 finalize。
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

    private readonly record struct PressRecord(DateTime At, ulong Frame);

    private sealed class AddressRecords(string addonName)
    {
        public string AddonName { get; } = addonName;
        public Dictionary<string, PressRecord> ByParam { get; } = new(StringComparer.Ordinal);
        public DateTime LastAt { get; set; }
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
    /// 給「不是走 <see cref="UiHelper"/> 送 callback」的按法用（<c>DispatchItemEvent</c>、
    /// <c>FireCloseCallback</c>、對 agent 送事件但錨在某扇窗上）。
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
    /// 除了偏移 0x8 那 32 個 byte 的固定欄位之外，位址一樣不解任何二級指標。
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
                    // 🔴 這就是崩潰的那一幀。診斷寫 Information（使用者跑 LogLevel 2），並節流免得洗版。
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
        LogPressDiag(addonName, address, paramKey);
        return true;
    }

    /// <summary>
    /// 跨外掛「按窗診斷」：在<b>真的送出按壓</b>的那一刻寫一行 <c>Information</c>。
    /// </summary>
    /// <remarks>
    /// 全艦隊 15 份各自獨立的 <c>AddonPressGuard</c> 只擋自己按過的位址：外掛 A 按下之後
    /// 「關閉中」那幾幀，外掛 B 的表是空的 ⇒ 照按 ⇒ 攔不到的存取違規。
    /// 這一行的用途是用一輪實機 log 回答「跨外掛重按是不是真的在發生」，
    /// 格式<b>逐字</b>與其他外掛統一，才能按 <c>addr</c> 交叉比對。
    /// 🔴 刻意<b>不節流</b>（漏掉一次就是漏掉一個對照樣本）；
    /// 🔴 位址只印數值，<b>不解參考</b>。
    /// </remarks>
    private static void LogPressDiag(string addonName, nint address, string paramKey)
    {
        var name = string.IsNullOrEmpty(addonName) ? "?" : addonName;
        Svc.Log.Information($"[按窗診斷] plugin=TCToolbox addon={name} addr=0x{address:X} key={paramKey ?? string.Empty}");
    }

    private static string DescribeKey(string paramKey) => paramKey.Length == 0 ? "（不分）" : paramKey;

    /// <summary>
    /// 掛上解除封鎖用的全域監聽器與幀計數器（重複呼叫是 no-op）。
    /// </summary>
    /// <remarks>
    /// 掛上去之後就不再拆（只在 <see cref="ForceTeardown"/> 拆）：監聽器只做一次字典移除，
    /// 成本可忽略，而動態掛／拆比較容易留下懸空的監聽器。
    /// <para>
    /// 📌 <b>外掛啟動時就先呼叫一次</b>（<c>Plugin</c> 建構子裡、模組 <c>Enable()</c> 之前）：
    /// 留給第一次按下才懶註冊的話，守衛的監聽器必定排在所有模組之後，
    /// 而同一次事件派送是依清單順序逐一呼叫的。
    /// ⚠️ <b>但順序不能當保證</b>：<c>RegisterListener</c> 走 <c>Framework.RunOnTick</c>，
    /// 而它底下的 <c>ThreadBoundTaskScheduler</c> 用 <c>ConcurrentDictionary</c> 存待跑的工作、
    /// <c>Run()</c> 直接列舉它的 <c>Keys</c>——<b>列舉順序與排入順序無關</b>。
    /// 真正把順序這個變數拿掉的是 <see cref="OnAddonLifecycle"/> 裡的「這一幀才登記的不清」。
    /// </para>
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

    private static void OnFrameworkUpdate(IFramework framework) => frameCount++;

    /// <summary>該位址走完（或重新開始）生命週期：把它底下的紀錄清掉。</summary>
    /// <remarks>
    /// 🔴🔴 <b><see cref="AddonEvent.PostSetup"/> 只清「不是這一幀才登記的」紀錄。</b>
    /// 本 pin 的 Dalamud 對同一個事件是<b>在同一次派送裡依清單順序逐一呼叫監聽器</b>的
    /// （<c>AddonLifecycle.InvokeListenersSafely</c> 直接 <c>foreach</c> 那份全域清單、<b>不做快照</b>），
    /// 而排序不是我們能決定的（見 <see cref="EnsureWatching"/> 的說明）。
    /// 凡是<b>在 PostSetup 處理常式裡就按下</b>的模組（本 repo 至少六支：
    /// <c>AutoCustomDeliveryResult</c>、<c>AutoRequestItemSubmit</c> 的兩支、<c>LetterCollectAll</c>、
    /// <c>OptimizedFreeShop</c>、<c>AutoMaterialize</c>），只要守衛排在它後面，
    /// 模組剛登記完位址就輪到這支把同一個位址清掉，
    /// 接下來那扇窗的 <c>PostDraw</c> 重送<b>完全沒有守衛</b>（＝ crash-20260831205734 的形狀）。
    /// 「這一幀才登記的不清」把順序這個變數整個拿掉：不管誰先誰後，結果都一樣。
    /// <para>
    /// ⚙️ 一幀之內不可能發生「舊的還在、新的已經建在同一個位址」：位址要被重用得先 finalize，
    /// 而 <see cref="AddonEvent.PreFinalize"/> 沒有這個豁免（下一段），所以重用場景裡紀錄早就被清掉了。
    /// </para>
    /// <para>
    /// <see cref="AddonEvent.PreFinalize"/> 不做這個豁免：它的意思是「這一扇確定走到終點」，
    /// 清掉才是對的；而且窗都沒了，後面也不會再有人對它送東西。
    /// </para>
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

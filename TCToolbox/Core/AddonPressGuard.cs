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

    /// <summary>槽位回收掃描的間隔（幀）。</summary>
    private const int SweepIntervalFrames = 60;

    /// <summary>
    /// 槽位回收的<b>年齡下限</b>（毫秒）：只回收「既有規則已經一定會放行」的紀錄。
    /// </summary>
    /// <remarks>
    /// 取 30 秒是為了同時滿足兩件事：①遠大於 <see cref="ReleaseTimeoutMs"/>（2 秒），
    /// 所以 <c>SelectYesno</c> 族那條「按下後 N 毫秒既沒被銷毀也沒重建」的診斷不會被吃掉；
    /// ②幀數那一關的安全邊際——要撐滿 30 秒還湊不到
    /// <see cref="RoutineRePressEscapeFrames"/>（15）幀，得掉到 0.5 fps。
    /// </remarks>
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

    /// <summary>
    /// 已經<b>實機證實</b>是「常駐」的視窗名：關閉只是被設成不可見，實例與位址永遠留在
    /// <c>AllLoadedUnitsList</c> 裡，<see cref="AddonEvent.PreFinalize"/> 與
    /// <see cref="AddonEvent.PostSetup"/> <b>兩個都不會再發生</b>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 🔴 <b>為什麼需要這一份名單</b>：這支守衛的解除點是那兩個生命週期事件（＋只回收記憶體的
    /// <see cref="SweepReleasedRecords"/>），對常駐窗而言<b>兩條都是死路</b> ⇒ 記號一旦記下就
    /// <b>永不解除</b>，整扇窗退化成「每 <see cref="ReleaseTimeoutMs"/> 毫秒才准動作一次」。
    /// 使用者實際看到的現象是「右鍵按了沒反應／要連按好幾次」。
    /// </para>
    /// <para>
    /// 🔑 <b>加名字進來的門檻＝實機 log 裡「真的送出的次數」≈「走逃生口的次數」</b>
    /// （兩者幾乎相等＝記號從來沒有被生命週期事件解除過）。<c>ContextMenu</c> 在 AutoRetainer
    /// 的同一場實機記錄裡是 295 次送出 : 290 次逃生口 : 144 次被吞掉，因此收錄。
    /// </para>
    /// <para>
    /// ⚠️ <b>沒有這種證據的名字一律不要加。</b>猜錯的方向是危險的：把一扇「會被銷毀」的窗誤標成常駐，
    /// 等於在它拆除途中提早 <see cref="HiddenReleaseFrames"/> 幀解除封鎖。已知的候選但<b>證據不足、
    /// 刻意沒收</b>的是 <c>ContextIconMenu</c>（與 <c>ContextMenu</c> 同一個 <c>AgentContext</c> 家族，
    /// 直覺上同樣常駐，但實機 log 裡沒有它的逃生口紀錄，無從證實）——
    /// 憑直覺把它加進來，就是把一個沒被驗證的假設寫進安全關鍵路徑。
    /// </para>
    /// <para>
    /// 🔴 <b>加名字進來的代價：這份名單同時是「送出前必須可見」的名單。</b>
    /// <see cref="TryBeginPressCore"/> 對名單內的窗多要求一道就地 <see cref="UiHelper.IsReady"/>，
    /// 那是 <see cref="ReleaseHiddenPersistent"/> 解除條件的邏輯反面（見那支的說明）。
    /// 名單外的窗一個字都沒有改。
    /// </para>
    /// <para>
    /// 📌 <c>_Notification</c> 也是常駐 HUD，但它<b>刻意不在這裡</b>：它走
    /// <see cref="RoutineAddons"/>（15 幀逃生口）已經沒有卡住的問題，而且
    /// <c>AutoPlayerCommend</c> 對它送 callback 時<b>刻意只判 null 不判可見</b>
    /// （那支的註解寫明理由：判了會把「推薦沒送出」變成靜默失敗）——
    /// 收進來會連帶把那個刻意的決定回退掉。
    /// </para>
    /// </remarks>
    private static readonly HashSet<string> PersistentAddons = new(StringComparer.Ordinal)
    {
        "ContextMenu",
    };

    /// <summary>
    /// <see cref="PersistentAddons"/> 裡的窗被按下之後，最少要<b>連續</b>觀察到它「還在清單裡、
    /// 但已經被隱藏」這麼多幀，才把按下記號解除。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 🔴 <b>為什麼是「連續 N 幀」而不是「這一幀不可見就解除」</b>：窗在拆除途中會有幾幀「已經被設成
    /// 不可見、但還沒拆完」，那正是這個守衛在防的危險窗口（實測 &lt;10 幀）。要求連續觀察到隱藏
    /// 20 幀（約兩倍於危險窗口）才解除，等於「等到穩定隱藏＝拆除已經結束」才放行；
    /// 中間只要看到一幀可見（或這一幀查不到）就歸零重數。
    /// </para>
    /// <para>
    /// 🔑 <b>為什麼要遠小於 <see cref="ReleaseTimeoutMs"/></b>（2 秒，60fps 下約 120 幀）：這條路徑要救的
    /// 就是「使用者的下一次右鍵被擋掉」，解除得比逃生口還慢的話等於沒做。20 幀在 60fps 下約 0.33 秒。
    /// AutoRetainer 同一份實機記錄量到被吞掉的右鍵距上一次送出最快 14 幀、p5＝16 幀、中位數 41 幀，
    /// 20 幀救得回其中約 85%；剩下的擠在 145~170 毫秒那一叢，形狀比較像滑鼠彈跳而不是人的第二次點擊。
    /// </para>
    /// <para>
    /// 🔴 <b>但這個數字終究只是「使用者要等多久」，不是防護本身。</b>真正把崩潰面拆掉的是
    /// <see cref="TryBeginPressCore"/> 對常駐窗多要求的那道「送出前必須可見」——
    /// 它與這裡的解除條件（連續不可見）互為邏輯反面 ⇒ <b>就算這個幀數設得太短</b>，
    /// 最壞的結果也只是「那一發沒送出」，不會變成對正在拆除的窗再送一次。
    /// </para>
    /// </remarks>
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

        // 🔴🔴 常駐窗專用的「送出前就地就緒檢查」（2026-09-04）。
        //    ⚠️ 這一道**不是**「擋得住正在關閉中的窗」的檢查 —— IsReady 的三關（非 null／IsVisible／
        //    LoadedState == Loaded）在拆除途中是**全過**的（本檔開頭那段講的就是這件事），
        //    單獨看它一個東西都擋不到。**這個結論不可以當成通用結論搬去別的地方用。**
        //
        //    它在這裡有效的唯一理由是：**與 ReleaseHiddenPersistent 的解除條件互為邏輯反面**。
        //    那支的解除條件是「連續 HiddenReleaseFrames 幀**不可見**」，這裡的放行條件是「**可見**」
        //    ⇒ 記號被解除之後還要能再送出一發，中間**必須**有一次遊戲自己把這扇窗重新 Show 起來，
        //    而正在拆除的窗不會被重新 Show。兩者一組才是防護；只做其中一半的話，
        //    記號可以在拆除中途被解除，下一發直接打在正在拆的窗上 ＝ 攔不到的存取違規。
        //
        // 🔴 只對 PersistentAddons 生效。名單外的窗完全不走這一行，行為與改動前逐字相同——
        //    這是刻意的：本 repo 至少有兩處**刻意不判可見**的送出（AutoPlayerCommend 對常駐 HUD
        //    _Notification、AutoMaterialize 在 PostSetup 當下就按且沒有 PostDraw 重試），
        //    無差別加上這道檢查會把它們靜默改成不送。
        // 🔴 解參考的是**呼叫端這一幀剛拿到**的指標（走到這裡之前呼叫端已經解參考過它讀名字），
        //    不是 Pressed 字典裡的鍵——那些位址從頭到尾只做等值比較。
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
    /// <para>
    /// 🔴 <b>兩條安全不變量原封不動</b>：
    /// ①<b>存下來的位址只做等值比較，永不解參考</b>——這裡解參考的是
    /// <c>GetAddonByName</c> <b>這一幀剛交回來</b>的指標，讀的是 <c>AtkUnitBase</c> 固定位移的
    /// <c>IsVisible</c>（不再往下追任何二級指標）；
    /// ②解除<b>按位址</b>一筆一筆移除，不是按名稱整批清。
    /// </para>
    /// <para>
    /// 🔑 <b>這裡的查詢即使查歪了也只會更保守。</b><see cref="SweepReleasedRecords"/> 的說明裡
    /// 反對過「<c>GetAddonByName</c> 查不到就清」——那條規則的失敗方向是<b>放行</b>（關閉中的窗
    /// 可能已經從清單移除 ⇒ 查不到 ⇒ 誤判成收乾淨 ⇒ 解除封鎖 ⇒ 崩潰）。這裡的規則方向相反：
    /// <b>只有「找到了、而且不可見」才累加</b>，「查不到」與「找到但看得見」<b>一律歸零</b>。
    /// ⇒ 同名實例超過掃描上限、窗已從清單移除、查詢本身出錯……每一種都表現成「繼續封鎖」。
    /// </para>
    /// <para>
    /// ⚙️ 位址被下一扇新窗重用不會造成誤判：位址要被重用得先 finalize，而
    /// <see cref="AddonEvent.PreFinalize"/> 會先把這筆紀錄整個移除。
    /// </para>
    /// <para>
    /// 📌 成本：只在「表裡有常駐窗名的紀錄」時才掃，而那種紀錄正常情況下最多活
    /// <see cref="HiddenReleaseFrames"/> 幀（窗一收起來就被解除）。
    /// </para>
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
    /// 掃到第一個 null 就停（那代表這個名字底下沒有更多實例了），上限
    /// <see cref="UiHelper.MaxAddonInstanceScan"/>。
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
    /// 🔴🔴 <b>存在的理由</b>：解除封鎖靠的是 <see cref="AddonEvent.PreFinalize"/>／
    /// <see cref="AddonEvent.PostSetup"/>，而<b>常駐 HUD 兩個都不會發生</b>——
    /// <c>_Notification</c> 從登入到登出都是同一個實例、同一個位址。
    /// 2026-09-04 實機：<c>_Notification</c> 的同一筆紀錄活了 <b>58066 幀</b>（約 10 分鐘、
    /// 橫跨好幾場副本），於是下一場副本的第一次按壓被迫走逃生口，還每秒多寫一行 log。
    /// <para>
    /// 🔑 <b>兩條安全不變量原封不動</b>：
    /// ①位址只當字典鍵與等值比較，<b>永不解參考</b>（這支從頭到尾沒碰過任何 addon）；
    /// ②解除是<b>按位址</b>一筆一筆移除，不是按名稱整批清。
    /// </para>
    /// <para>
    /// 🔑 <b>為什麼這不會弱化防護</b>：回收條件是「距離最後一次按下超過
    /// <see cref="SweepMinAgeMs"/>」<b>而且</b>「已經過了 <see cref="RoutineRePressEscapeFrames"/> 幀」。
    /// 同時滿足這兩條的紀錄，<see cref="TryBeginPressCore"/> 本來就<b>一定會放行</b>
    /// （逾時放行只要 <see cref="ReleaseTimeoutMs"/>＝2 秒、逃生口只要 15 幀），
    /// 所以移除它<b>不會改變任何一次「按／不按」的判定</b>，只是少寫一行 log、少佔一格字典。
    /// </para>
    /// <para>
    /// ⚠️ 刻意<b>不</b>用「<c>GetAddonByName</c> 查不到就清」當回收條件，兩個理由：
    /// ①本案的 <c>_Notification</c> 是常駐 HUD，<b>永遠查得到</b>，那條規則對它完全無效，
    /// 修不到實際發生的那個形狀；
    /// ②在保護窗<b>之內</b>拿「用名字查位址」的結果去決定要不要解除封鎖，
    /// 等於把「攔不到的存取違規」押在一次可能查歪的查詢上
    /// （同名多實例只看得到第 1 格、關閉中的視窗可能已經從清單移除）。
    /// 年齡門檻能達成同樣的回收，而且不需要相信任何查詢。
    /// </para>
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

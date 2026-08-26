using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 收藏櫃視窗上多一顆按鈕：把背包／兵裝庫裡「可以收進收藏櫃、而且收進去不會弄壞東西」的裝備一次存入。
/// </summary>
/// <remarks>
/// <para>
/// 🔑 <b>命令碼 425 是離線對台服 7.20 主程式驗過的，不是照抄 DailyRoutines 的常數</b>
/// （2026-08-25 鑑識，五條互相獨立的證據）：
/// <list type="bullet">
/// <item><c>ExecuteCommand</c> ＝ <c>0x140A6E730</c>，內部<b>沒有 switch</b>——它只是把 5 個整數
/// 編組成封包送出，所有本機語意都在各自的包裝函式裡。</item>
/// <item>425 在整個 <c>.text</c> <b>只有 1 個呼叫點</b>（<c>0x140A1DABC</c>），
/// 在包裝函式 <c>Cabinet::StoreItem</c> 裡；424（請求載入）與 426（取出）是記憶體上的連號三胞胎，
/// 426 與 425 逐位元組相同、唯一差別是 <c>btr</c>（清位元）取代 <c>bts</c>（設位元）。</item>
/// <item>包裝函式的上界是 <c>cmp edx, 0x417</c>（1047），而台服 <c>Cabinet.csv</c> 剛好
/// <b>1048 列、row id 0..1047</b>（其中 958 列有真實 <c>Item</c>）——逐一吻合，
/// 證明 <c>param1</c> 是 <b><c>Cabinet</c> 表的列號，不是道具 ID</b>。</item>
/// <item>它動到的位元陣列就是 CS <c>Cabinet._unlockedItems</c>；同一個位元被 CS 已文件化的
/// <c>IsItemInCabinet</c> 讀取 ⇒ <c>bts</c>＝「收藏櫃裡有了」＝<b>存入</b>。</item>
/// <item>唯一呼叫它的是 <c>AgentCabinet</c> 的 <c>ReceiveEvent</c>，
/// 同一個 agent 跳的確認框取 <c>Addon</c> 列 4650：「確定要將「」放入收藏櫃嗎？」。</item>
/// </list>
/// </para>
/// <para>
/// 🔴🔴 <b>可是「命令碼語意成立」不等於「可以無腦一鍵全存」。</b>
/// 台服自己的確認文字寫死了三件事，而<b>客戶端在送 425 之前完全沒有做這些檢查</b>
/// （包裝函式只有上界檢查）——也就是說那些是伺服器端規則，我們踩下去的後果是<b>使用者資料損失</b>：
/// <list type="bullet">
/// <item>「染色、徽章以及武具投影等外觀效果均會消除」——<b>不可逆</b>。</item>
/// <item>「精煉度會變回 0%」——<b>不可逆</b>。</item>
/// <item>「※耐久度不足 100% 的道具無法放入」——伺服器<b>靜默拒絕</b>，使用者會以為存進去了。</item>
/// </list>
/// ⇒ 本模組<b>一律把這幾類排除在外</b>，並在畫面上寫清楚排除了幾件、為什麼。
/// 想連染過色的一起存的人請自己到收藏櫃視窗手動存——那時遊戲會親口告訴他外觀會消失。
/// </para>
/// <para>
/// 🔴 <b>每一件都等伺服器確認才送下一件。</b>送出 425 之後盯著
/// <c>Cabinet::IsItemInCabinet</c> 翻成 <see langword="true"/> 才算成功
/// （那個位元是伺服器回推的，客戶端自己送 425 時<b>不會</b>先樂觀設起來）。
/// 這道閘門存在的理由是：封包裡只帶命令碼與列號，<b>不帶任何「我正站在收藏櫃前」的證明</b>，
/// 那個狀態在伺服器端，而台服的拒絕是<b>完全靜默</b>的。
/// 沒有這道閘門的話，「伺服器不受理」會表現成「按了沒反應，還印了已存入」，
/// 而且會一口氣送出好幾百個不被受理的封包。
/// </para>
/// <para>
/// 📌 判斷依據是 Lumina <c>Cabinet</c> 表，<b>不寫死道具清單</b>（同
/// <see cref="GlamourArmoireCleanup"/>）。改版新增可收納道具時自動跟上。
/// </para>
/// </remarks>
public sealed unsafe class CabinetStoreAll : TcModule
{
    public override string InternalName => "CabinetStoreAll";
    public override string DisplayName => "收藏櫃：可收納裝備一鍵存入";

    public override string Description =>
        "開啟遊戲的「收藏櫃」視窗後，視窗上方會多出一顆按鈕：按一次就把背包與兵裝庫裡" +
        "「可以收進收藏櫃」的裝備依序存入，不必一件一件點。" +
        "染過色、上了武具投影、有精煉度、鑲了魔晶石、或耐久度不足 100% 的一律跳過（那些存進去會出事），" +
        "跳過幾件、為什麼，畫面上都看得到。每一件都等伺服器確認後才送下一件；視窗關閉即停止。";

    public override ModuleCategory Category => ModuleCategory.Inventory;

    /// <summary>開著不按按鈕的話，一件裝備都不會被存進去。</summary>
    public override bool IsManualTrigger => true;

    public override bool HasConfigUI => true;

    /// <summary>收藏櫃視窗的 addon 名。</summary>
    private const string CabinetAddon = "Cabinet";

    /// <summary>「把道具存進收藏櫃」的命令碼（見類別說明的離線鑑識）。</summary>
    private const uint StoreToCabinetCommand = 425;

    /// <summary>
    /// <c>ExecuteCommand</c> 的<b>呼叫點</b>特徵碼。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>刻意用呼叫點而不是函式序言。</b>台服 7.20 的 <c>ExecuteCommand</c> 家族有
    /// <b>9 個近乎逐位元組相同的函式</b>（<c>0x140A6E730</c> 起連續九支），
    /// 用序言寫特徵碼必得 9 個命中；<c>ScanText</c> 取第一個雖然剛好是對的那一支，
    /// <b>但那是運氣不是保證</b>——歧義比斷裂更糟，因為它會靜默呼叫錯的函式。
    /// 這一條（取自 OmenTools）在台服<b>唯一命中</b>，跟隨 <c>E8</c> 位移後落在 <c>0x140A6E730</c>。
    /// <para>
    /// ⚠️ <c>ScanAllText</c> <b>不會</b>跟隨 <c>E8</c>，<c>ScanText</c> 會——
    /// 所以「數命中數」與「取位址」必須分兩支呼叫，不能只用其中一支。
    /// </para>
    /// </remarks>
    private const string ExecuteCommandSig =
        "E8 ?? ?? ?? ?? 48 8B 06 48 8B CE FF 50 ?? E9 ?? ?? ?? ?? 49 8B CC";

    private delegate nint ExecuteCommandDelegate(
        uint command, uint param1, uint param2, uint param3, uint param4);

    private ExecuteCommandDelegate? executeCommand;

    /// <summary>特徵碼解析失敗的原因（空字串＝成功）。列上與面板上都要看得見。</summary>
    private string sigFailure = string.Empty;

    /// <summary>
    /// 掃描時被排除的理由。
    /// </summary>
    /// <remarks>
    /// 🔴 零值是「可以存」——這個列舉只在本檔內用、不進設定檔，但把零值放在某個排除理由上，
    /// 任何忘了指派的路徑都會表現成「這件不能存」而不是崩潰，那種靜默的少做最難發現。
    /// </remarks>
    private enum SkipReason
    {
        None = 0,
        Dyed = 1,
        Glamoured = 2,
        Spiritbond = 3,
        Materia = 4,
        Damaged = 5,
    }

    private static string DescribeSkip(SkipReason reason) => reason switch
    {
        SkipReason.Dyed => "已染色（存進去染色會消失）",
        SkipReason.Glamoured => "有武具投影（存進去投影會消失）",
        SkipReason.Spiritbond => "有精煉度（存進去會歸零）",
        SkipReason.Materia => "已鑲嵌魔晶石",
        SkipReason.Damaged => "耐久度不足 100%（伺服器不會受理）",
        _ => "可以存入",
    };

    /// <summary>要掃的容器。<b>刻意不含裝備欄</b>——身上穿著的東西不該被一鍵收走。</summary>
    private static readonly InventoryType[] ScanContainers =
    [
        InventoryType.Inventory1, InventoryType.Inventory2,
        InventoryType.Inventory3, InventoryType.Inventory4,
        InventoryType.ArmoryMainHand, InventoryType.ArmoryOffHand,
        InventoryType.ArmoryHead, InventoryType.ArmoryBody, InventoryType.ArmoryHands,
        InventoryType.ArmoryLegs, InventoryType.ArmoryFeets,
        InventoryType.ArmoryEar, InventoryType.ArmoryNeck, InventoryType.ArmoryWrist,
        InventoryType.ArmoryRings,
    ];

    /// <summary>道具 ID → <c>Cabinet</c> 表列號。啟用時建一次，之後只讀。</summary>
    private Dictionary<uint, uint> cabinetRows = [];

    private CabinetStoreAllConfig Config => Plugin.Instance.Config.CabinetStoreAll;

    private readonly TaskQueue queue = new() { DefaultTimeoutMs = 15_000 };

    /// <summary>本輪要存的列號（掃描時定案，執行中不重掃）。</summary>
    private readonly List<uint> plan = [];

    private int storedCount;

    /// <summary>上一次掃描的結果快照（畫面用）。</summary>
    private int? eligibleCount;

    private readonly Dictionary<SkipReason, int> skipCounts = [];

    private string scanBlockReason = string.Empty;

    /// <summary>使用者按了「一鍵存入」，等 <see cref="OnUpdate"/> 那一側接手。</summary>
    private bool startRequested;

    protected override void OnEnable()
    {
        cabinetRows = BuildCabinetRowMap();

        // 🔑 「回 0」比「報錯」常見：資料表讀不到時整個模組會安靜地什麼都不做。
        //    台服 7.20 的期望值是 958 筆（Cabinet.csv 共 1048 列，其中 958 列有真實 Item）。
        Svc.Log.Information($"[{InternalName}] Cabinet 表載入 {cabinetRows.Count} 筆可收納道具（台服 7.20 期望 958）。");

        ResolveExecuteCommand();

        queue.OnTimeout = step => Svc.Chat.PrintError($"[TC Toolbox] 收藏櫃存入逾時，已停止：{step}");

        Svc.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, CabinetAddon, OnCabinetFinalize);
        Svc.Framework.Update += OnUpdate;
        Svc.PluginInterface.UiBuilder.Draw += DrawOverlay;
    }

    protected override void OnDisable()
    {
        Svc.AddonLifecycle.UnregisterListener(OnCabinetFinalize);
        Svc.Framework.Update -= OnUpdate;
        Svc.PluginInterface.UiBuilder.Draw -= DrawOverlay;

        queue.Abort();
        plan.Clear();
        cabinetRows = [];
        eligibleCount = null;
        skipCounts.Clear();
        executeCommand = null;
        startRequested = false;
        storedCount = 0;
    }

    /// <summary>
    /// 解析 <c>ExecuteCommand</c>：<b>要求特徵碼恰好命中一次</b>，否則整個功能不安裝。
    /// </summary>
    /// <remarks>
    /// 做法照 <see cref="AutoInventoryTransfer"/> 的置物櫃搬移函式：離線的唯一性結論
    /// 改寫成<b>執行期的閘門</b>，而不是相信它會永遠成立。命中 0 或 ≥2 都寧可整個功能不能用——
    /// 這支函式送的是封包，呼叫錯的那一支不是「沒反應」而是不知道會做什麼。
    /// </remarks>
    private void ResolveExecuteCommand()
    {
        executeCommand = null;
        sigFailure = string.Empty;

        try
        {
            var hits = Svc.SigScanner.ScanAllText(ExecuteCommandSig);
            if (hits.Length != 1)
            {
                sigFailure = hits.Length == 0
                    ? "找不到 ExecuteCommand 的特徵碼"
                    : $"ExecuteCommand 的特徵碼命中 {hits.Length} 次（有歧義）";
                Svc.Log.Information($"[{InternalName}] {sigFailure}，一鍵存入功能停用。");
                return;
            }

            // ScanAllText 不跟隨 E8，ScanText 會——要的是被呼叫的那支函式本體。
            var address = Svc.SigScanner.ScanText(ExecuteCommandSig);
            if (address == nint.Zero)
            {
                sigFailure = "ExecuteCommand 位址解析失敗";
                Svc.Log.Information($"[{InternalName}] {sigFailure}，一鍵存入功能停用。");
                return;
            }

            executeCommand = Marshal.GetDelegateForFunctionPointer<ExecuteCommandDelegate>(address);

            var rva = address - Svc.SigScanner.Module.BaseAddress;
            Svc.Log.Information(
                $"[{InternalName}] ExecuteCommand 位址 0x{address:X}（RVA 0x{rva:X}），唯一命中，功能可用。");
        }
        catch (Exception ex)
        {
            sigFailure = "ExecuteCommand 特徵碼掃描擲出例外";
            Svc.Log.Error(ex, $"[{InternalName}] {sigFailure}，一鍵存入功能停用。");
        }
    }

    private static Dictionary<uint, uint> BuildCabinetRowMap()
    {
        var result = new Dictionary<uint, uint>();
        var sheet = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Cabinet>();

        foreach (var row in sheet)
        {
            var itemId = row.Item.RowId;
            // 同一件道具只會對應一列；重複時保留先出現的那一列（TryAdd 不覆蓋）。
            if (itemId != 0) result.TryAdd(itemId, row.RowId);
        }

        return result;
    }

    /// <summary>
    /// 🔴 <b>所有會碰到原生結構的事情都在這裡做，不在 Draw 裡做。</b>
    /// </summary>
    /// <remarks>
    /// Draw 只讀本模組自己算好的欄位。理由是 <c>UiBuilder.Draw</c> 一旦逸出例外，
    /// Dalamud 會把整個 Draw 委派設成 null，介面到重開遊戲前都不會回來；
    /// 而 <c>Framework.Update</c> 的例外只會被記錄下來。
    /// 掃描與「按下按鈕之後要做的事」都相依於 <c>UIState</c>／<c>InventoryManager</c>
    /// 這類靠特徵碼解出來的東西，所以一律搬到這一側。
    /// </remarks>
    private void OnUpdate(IFramework framework)
    {
        queue.Tick();

        if (startRequested)
        {
            startRequested = false;
            StartStoring();
        }

        // 收藏櫃視窗沒開就完全不掃（也不必掃）。
        if (!UiHelper.IsAddonReady(CabinetAddon))
        {
            eligibleCount = null;
            scanBlockReason = "收藏櫃視窗沒有開著";
            return;
        }

        if (queue.IsBusy) return;
        if (!Throttle.Pass("CabinetStoreAll-Scan", 1_000)) return;

        RefreshScan();
    }

    private void OnCabinetFinalize(AddonEvent type, AddonArgs args)
    {
        if (!queue.IsBusy) return;

        queue.Abort();
        plan.Clear();
        Svc.Chat.Print($"[TC Toolbox] 收藏櫃視窗已關閉，存入流程已停止（本次已存入 {storedCount} 件）。");
        Svc.Log.Information($"[{InternalName}] 收藏櫃視窗關閉，中止佇列（已存入 {storedCount} 件）。");
    }

    /// <summary>收藏櫃是不是「開著而且資料已經從伺服器拿到」。</summary>
    /// <remarks>
    /// 🔴 <b>兩個條件都要。</b><c>Cabinet.State</c> 只在玩家真的去旅館房間開收藏櫃時才會變成
    /// <c>Loaded</c>（CS 的散文註解直說了這件事）；而視窗開著是「伺服器願意受理我們的請求」
    /// 目前唯一拿得到的在場證據——封包本身不帶任何場所資訊。
    /// <para>
    /// ⚠️ 這仍然只是<b>間接</b>證據。真正的判準在伺服器端，離線證不出來。
    /// 所以下游還有一道「等伺服器把旗標翻過來」的驗證（見 <see cref="StartStoring"/>）。
    /// </para>
    /// </remarks>
    private static bool IsCabinetUsable(out string reason)
    {
        if (!UiHelper.IsAddonReady(CabinetAddon))
        {
            reason = "收藏櫃視窗沒有開著";
            return false;
        }

        // UIState 是 [StaticAddress] 且沒有 isPointer:true ⇒ 取的是物件本身的位址，永不回 null
        // （對它判空是死碼）。特徵碼失配時擲的是受管理例外，不是 AVE。
        var cabinet = &UIState.Instance()->Cabinet;
        if (!cabinet->IsCabinetLoaded())
        {
            reason = $"收藏櫃資料尚未載入（State={cabinet->State}）";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// 這一列在收藏櫃裡了沒有。<b>自己讀位元，不呼叫 <c>Cabinet::IsItemInCabinet</c>。</b>
    /// </summary>
    /// <remarks>
    /// 🔴 存在的理由是<b>不要相依於一條可能失配的特徵碼</b>。
    /// CS 的 <c>IsItemInCabinet</c> 是 <c>[MemberFunction]</c>，特徵碼失配時是在呼叫的當下
    /// 擲受管理例外——而這條路徑會被 UI 相關的程式碼走到，例外一旦逸出到 Dalamud 的 Draw，
    /// 整個介面到重開遊戲前都不會回來。
    /// <para>
    /// 位元佈局是離線對台服 7.20 主程式驗過的（2026-08-25）：包裝函式做的正是
    /// <c>byte[this + 4 + idx/8]</c> 的 <c>bts</c>／<c>btr</c>，而 CS 宣告
    /// <c>State</c> @0x00、<c>FixedSizeArray132&lt;byte&gt; _unlockedItems</c> @0x04——
    /// 132 bytes ＝ 1056 bits ⊇ 1048 列，兩邊逐一對得上。
    /// </para>
    /// <para>
    /// ⚠️ 上界照 <c>UnlockedItems</c> 自己的長度收斂，不照 <c>Cabinet</c> 表的列數——
    /// 表比陣列長的話（改版新增道具而結構還沒跟上）越界讀的是別人的記憶體。
    /// </para>
    /// </remarks>
    private static bool IsStored(Cabinet* cabinet, uint cabinetRow)
    {
        var bits = cabinet->UnlockedItems;
        var byteIndex = (int)(cabinetRow >> 3);
        if (byteIndex < 0 || byteIndex >= bits.Length) return false;

        return (bits[byteIndex] & (1 << (int)(cabinetRow & 7))) != 0;
    }

    /// <summary>掃一遍容器，決定哪些可以存、哪些要跳過。</summary>
    private (List<uint> Eligible, Dictionary<SkipReason, int> Skipped) Scan()
    {
        var eligible = new List<uint>();
        var skipped = new Dictionary<SkipReason, int>();

        // InventoryManager 是 A 類 [StaticAddress]；GetInventoryContainer／GetInventorySlot
        // 才是合法回 null 的那一跳，下面逐一判空。
        var manager = InventoryManager.Instance();
        if (manager == null) return (eligible, skipped);

        var cabinet = &UIState.Instance()->Cabinet;
        var seen = new HashSet<uint>();

        foreach (var type in ScanContainers)
        {
            var container = manager->GetInventoryContainer(type);
            if (container == null) continue;

            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot == null) continue;

                // 🔴 讀**欄位** ItemId，不呼叫 GetBaseItemId()：欄位存的本來就是基礎列號
                //    （HQ 記在 Flags 上，是 GetItemId() 才會把 +1,000,000 套上去）。
                //    每少呼叫一支 [MemberFunction] 就少一條特徵碼相依。
                var itemId = slot->ItemId;
                if (itemId == 0) continue;
                if (!cabinetRows.TryGetValue(itemId, out var cabinetRow)) continue;

                // 已經在收藏櫃裡了。
                if (IsStored(cabinet, cabinetRow)) continue;

                var reason = ClassifySkip(slot);
                if (reason != SkipReason.None)
                {
                    skipped[reason] = skipped.GetValueOrDefault(reason) + 1;
                    continue;
                }

                // 同一件道具在多個格子裡時只送一次：命令只帶 Cabinet 列號，
                // 送第二次是重複封包（而且第二次一定會因為「已經在裡面」而失敗）。
                if (seen.Add(cabinetRow)) eligible.Add(cabinetRow);
            }
        }

        return (eligible, skipped);
    }

    /// <summary>
    /// 這一件存進收藏櫃會不會弄壞東西。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>順序是刻意的</b>：先報「不可逆的損失」（染色／投影／精煉度／魔晶石），
    /// 最後才報「伺服器會拒絕」（耐久）。一件東西同時符合多項時，
    /// 使用者最需要知道的是那個會讓他東西壞掉的理由。
    /// </remarks>
    private static SkipReason ClassifySkip(InventoryItem* slot)
    {
        // 🔴 讀 Stains 這個產生出來的 Span（純欄位），不呼叫 GetStain()——同上，少一條特徵碼相依。
        var stains = slot->Stains;
        if (stains[0] != 0 || stains[1] != 0) return SkipReason.Dyed;
        if (slot->GlamourId != 0) return SkipReason.Glamoured;
        if (slot->SpiritbondOrCollectability != 0) return SkipReason.Spiritbond;

        for (var m = 0; m < 5; m++)
        {
            if (slot->Materia[m] != 0) return SkipReason.Materia;
        }

        // Condition 是 0..30000 的原始值，30000＝100%。
        if (slot->Condition < 30_000) return SkipReason.Damaged;

        return SkipReason.None;
    }

    private void RefreshScan()
    {
        if (!IsCabinetUsable(out var reason))
        {
            eligibleCount = null;
            scanBlockReason = reason;
            skipCounts.Clear();
            return;
        }

        if (cabinetRows.Count == 0)
        {
            eligibleCount = null;
            scanBlockReason = "Cabinet 資料表載入失敗";
            skipCounts.Clear();
            return;
        }

        scanBlockReason = string.Empty;
        var (eligible, skipped) = Scan();
        eligibleCount = eligible.Count;

        skipCounts.Clear();
        foreach (var pair in skipped) skipCounts[pair.Key] = pair.Value;
    }

    private void StartStoring()
    {
        queue.Abort();
        plan.Clear();
        storedCount = 0;

        if (executeCommand == null)
        {
            Svc.Chat.PrintError($"[TC Toolbox] 一鍵存入無法使用：{sigFailure}。");
            return;
        }

        if (!IsCabinetUsable(out var reason))
        {
            Svc.Chat.PrintError($"[TC Toolbox] 一鍵存入取消：{reason}。");
            return;
        }

        var (eligible, _) = Scan();
        if (eligible.Count == 0)
        {
            Svc.Chat.Print("[TC Toolbox] 沒有可以存入收藏櫃的裝備。");
            return;
        }

        plan.AddRange(eligible);
        Svc.Log.Information($"[{InternalName}] 開始存入 {plan.Count} 件（Cabinet 列號：{string.Join(",", plan)}）。");

        foreach (var cabinetRow in plan)
        {
            var row = cabinetRow;

            queue.Enqueue($"送出存入 #{row}", () =>
            {
                if (!IsCabinetUsable(out var why))
                {
                    Svc.Chat.PrintError($"[TC Toolbox] 存入中止：{why}（已存入 {storedCount} 件）。");
                    return null;
                }

                if (executeCommand == null) return null;

                executeCommand(StoreToCabinetCommand, row, 0, 0, 0);
                return true;
            });

            // 🔴 這一步就是整個模組的安全閥。伺服器把 _unlockedItems 的那個位元回推過來才算數。
            //    客戶端自己送 425 時不會先把位元設起來（那是遊戲自己走包裝函式時才做的樂觀更新），
            //    所以這裡看到的翻轉是**真的伺服器確認**，不是我們自己寫的。
            //    逾時＝伺服器沒受理 ⇒ 整條佇列中止，不會再送出剩下幾百個封包。
            queue.Enqueue($"等待伺服器確認 #{row}", () =>
            {
                if (!IsStored(&UIState.Instance()->Cabinet, row)) return false;

                storedCount++;
                return true;
            }, Config.ConfirmTimeoutMs);

            queue.EnqueueDelay(Config.IntervalMs, $"間隔 #{row}");
        }

        queue.Enqueue("收尾", () =>
        {
            Svc.Chat.Print($"[TC Toolbox] 收藏櫃存入完成：共 {storedCount} 件。");
            Svc.Log.Information($"[{InternalName}] 完成：{storedCount}/{plan.Count} 件。");
            return true;
        });
    }

    private void DrawOverlay()
    {
        // 📌 這裡只讀 Dalamud 的受管理 API 與本模組自己的欄位——掃描在 OnUpdate 那一側。
        var addon = UiHelper.GetAddon(CabinetAddon);
        if (!UiHelper.IsReady(addon)) return;

        const ImGuiWindowFlags flags = ImGuiWindowFlags.AlwaysAutoResize |
                                       ImGuiWindowFlags.NoTitleBar |
                                       ImGuiWindowFlags.NoScrollbar |
                                       ImGuiWindowFlags.NoSavedSettings |
                                       ImGuiWindowFlags.NoFocusOnAppearing;

        ImGui.SetNextWindowPos(
            new Vector2(addon->GetX() + 6f, addon->GetY() - 46f), ImGuiCond.Always);

        if (ImGui.Begin("###TCToolboxCabinetStoreAll", flags))
            DrawBody();

        ImGui.End();
    }

    private void DrawBody()
    {
        ImGui.AlignTextToFramePadding();

        // 🔑 「不知道」本身要在列上看得見——把未知畫成 0 會直接誤導使用者。
        if (eligibleCount is { } count)
            ImGui.TextUnformatted($"可存入 {count} 件");
        else
            ImGui.TextDisabled($"可存入 ？（{scanBlockReason}）");

        ImGui.SameLine();

        var blocked = executeCommand == null || eligibleCount is null or 0 || queue.IsBusy;
        using (ImRaii.Disabled(blocked))
        {
            // 只立旗標，真正的工作在 OnUpdate（Draw 裡不碰原生結構）。
            if (ImGui.Button("一鍵存入##cabinet-store-all")) startRequested = true;
        }

        ImGui.SameLine();
        using (ImRaii.Disabled(!queue.IsBusy))
        {
            if (ImGui.Button("停止##cabinet-store-all"))
            {
                queue.Abort();
                plan.Clear();
                Svc.Chat.Print($"[TC Toolbox] 已停止收藏櫃存入（本次已存入 {storedCount} 件）。");
            }
        }

        if (queue.IsBusy)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"{queue.CurrentStep}（{storedCount}/{plan.Count}）");
        }

        // 特徵碼沒解出來時，按鈕為什麼是灰的必須看得見，不能只寫在記錄裡。
        if (executeCommand == null)
        {
            ImGui.TextColored(new Vector4(1f, 0.55f, 0.35f, 1f), $"⚠ 功能停用：{sigFailure}");
        }
        else if (skipCounts.Count > 0)
        {
            var total = 0;
            foreach (var pair in skipCounts) total += pair.Value;

            ImGui.TextColored(new Vector4(1f, 0.8f, 0.35f, 1f), $"已跳過 {total} 件");
            if (ImGui.IsItemHovered())
            {
                var lines = new List<string> { "這些存進去會出事，所以一律不碰：" };
                foreach (var pair in skipCounts)
                    lines.Add($"　{DescribeSkip(pair.Key)}：{pair.Value} 件");

                lines.Add(string.Empty);
                lines.Add("要存這些的話請在收藏櫃視窗自己點——");
                lines.Add("遊戲會親口告訴你外觀效果與精煉度會消失。");
                ImGui.SetTooltip(string.Join("\n", lines));
            }
        }
    }

    public override void DrawConfig()
    {
        ImGui.TextUnformatted("在收藏櫃視窗上方顯示按鈕。這裡只有節奏設定，實際操作在那顆按鈕上。");
        ImGui.Spacing();

        ImGui.SetNextItemWidth(160f);
        var interval = Config.IntervalMs;
        if (ImGui.SliderInt("每件之間的間隔（毫秒）", ref interval, 100, 2_000))
        {
            Config.IntervalMs = interval;
            Plugin.Instance.Config.Save();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "每一件都已經等到伺服器確認才會送下一件，所以這個間隔只是額外的餘裕。\n" +
                "⚠️ 台服的速率限制沒有公開數字；調到很低而遇到斷線時請調回來。");
        }

        ImGui.SetNextItemWidth(160f);
        var timeout = Config.ConfirmTimeoutMs;
        if (ImGui.SliderInt("等待伺服器確認的上限（毫秒）", ref timeout, 1_000, 15_000))
        {
            Config.ConfirmTimeoutMs = timeout;
            Plugin.Instance.Config.Save();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "超過這個時間伺服器還沒把「已在收藏櫃」的旗標翻過來，就整條中止。\n" +
                "🔴 這道閘門是刻意的：封包不帶「我正站在收藏櫃前」的證明，\n" +
                "而伺服器的拒絕是完全靜默的——沒有它，不受理會表現成「按了沒反應」。");
        }

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(1f, 0.8f, 0.35f, 1f),
                          "⚠ 染色／武具投影／精煉度／魔晶石／耐久不足 100% 的裝備一律跳過。");
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "遊戲自己的確認文字寫著：放入收藏櫃會消除染色、徽章與武具投影，精煉度會歸零，\n" +
                "而耐久度不足 100% 的道具無法放入。\n" +
                "客戶端在送出指令前不做這些檢查，所以踩下去的後果是不可逆的資料損失。\n" +
                "真的要存的話請自己在收藏櫃視窗點——那時遊戲會先問過你。");
        }
    }

    /// <summary>
    /// 特徵碼沒解出來時，在模組列上就要看得見。
    /// </summary>
    /// <remarks>
    /// 🔴 這個屬性在 ImGui 的 Draw 路徑上被讀，實作不得擲例外——這裡只讀兩個欄位。
    /// </remarks>
    public override ModuleNotice? RowNotice =>
        IsEnabled && sigFailure.Length > 0
            ? new ModuleNotice(ModuleNoticeLevel.Warning, "功能停用",
                               $"{sigFailure}。台服改版後特徵碼失效時會是這個狀態；" +
                               "模組不會做任何事，也不會出錯。")
            : null;
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Hooking;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Memory;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using TCToolbox.Core;
using LuminaAddon = Lumina.Excel.Sheets.Addon;
using ValueType = FFXIVClientStructs.FFXIV.Component.GUI.ValueType;

namespace TCToolbox.Modules;

/// <summary>
/// 批次僱員改名：對目前登入角色的僱員，逐一跑完「用一瓶僱員幻想藥 → 傳喚 → 卸下全身裝備 →
/// （由使用者手動改名）→ 穿回裝備 → 讓僱員返回」。
///
/// <para>
/// 🔴🔴 <b>第一版<u>不</u>自動操作改名畫面（CharaMake）。</b>改名那一步由使用者自己完成，
/// 本模組在那段時間進入「錄製模式」，把畫面上發生的事情寫進記錄供之後開發使用。理由是離線查證的結果：
/// <list type="bullet">
/// <item>僱員選單上「重新設定容貌」那一項的字串<b>在台服 7.20 的 EXD 裡找不到</b>——
/// <c>Addon.csv</c>（僱員選單那段是 2378~2407，逐列讀過）、<c>CustomTalk.csv</c>、<c>Lobby.csv</c>
/// 三張表搜「容貌」只有 <c>Addon</c> 14770（個人肖像說明）與 <c>Lobby</c> 625/626/627/2133
/// （角色編輯確認框），<b>沒有一列是那個選單項</b>。</item>
/// <item>CharaMake 的僱員模式在整個艦隊<b>零先例</b>：沒有任何一個外掛碰過 <c>CharaMake</c> /
/// <c>AgentCharaMake</c>（含 AutoRetainer，2026-08-23 逐字 grep 確認 0 命中）。</item>
/// </list>
/// 兩樣都只能靠實機問出來，而<b>猜 callback 序號去點改名畫面</b>是這個模組最貴的失敗形式
/// （點錯一下就把僱員容貌改成別的東西，不可逆）。所以第一版只自動化周邊苦工，改名本身留給使用者。
/// </para>
///
/// <para>
/// 🔴 <b>幻想藥是「單一旗標」不是「計數」。</b><c>LogMessage</c> 404
/// 「無法使用，現在已經擁有重新設定僱員容貌的權利了。」證明同時只能掛一份權利：
/// 一瓶只能改一隻。所以批次＝每隻各跑一次「用一瓶→改名」，<b>不能</b>先一口氣喝 N 瓶。
/// </para>
///
/// <para>
/// 🔴 <b>僱員名字是全世界唯一的</b>，所以改名不是「填一個名字」而是「從候選名單裡挑一個沒被占用的」。
/// 本模組因此採<b>候選名單池</b>：使用者準備一份名單，勾選的僱員依序各取「下一個還沒用過的候選」；
/// 被伺服器拒絕（名字已被使用）時自動換下一個，同一隻最多連試
/// <see cref="RetainerBatchRenameConfig.MaxCandidateAttemptsPerRetainer"/> 個。
/// 候選的狀態<b>存進設定</b>，重開遊戲也不會重試已知被占用的名字。
/// </para>
///
/// <para>
/// 📌 離線查證過的台服 7.20 資料（全部出自 <c>D:/ffxiv-tc-port/exd-tc/7.20/</c>，非國際服）：
/// <list type="bullet">
/// <item><c>Item</c> 8841 ＝「僱員幻想藥」</item>
/// <item><c>LogMessage</c> 404「無法使用，現在已經擁有重新設定僱員容貌的權利了。」（權利已在身上）</item>
/// <item><c>LogMessage</c> 405「可以重新設定僱員的容貌了。請脫下僱員的防具和飾品並到僱員窗口辦理業務。」
/// （權利剛立起來；同時證明<b>改名前必須全身卸裝</b>）</item>
/// <item><c>LogMessage</c> 3904「僱員在探險的過程中無法更換裝備。」（探險中不能卸裝 ⇒ 前置閘門）</item>
/// <item><c>Addon</c> 2383「讓僱員返回」（AutoRetainer 也是讀這一列，不是寫死字串）</item>
/// <item><c>EObjName</c> 2000401「傳喚鈴」</item>
/// <item>名字被占用／不合法：<c>Addon</c> 2864「該名字已經被使用。」、2863「該名字無法使用。」；
/// <c>LogMessage</c> 1335「該名字無法使用。」、346／7618／9248「該名稱已被使用，請更換為其他名稱。」
/// （三筆同文，分屬通訊貝／戰隊／跨界通訊貝）、1485／5605／10142「名字中含有無法使用的詞。」、
/// 3375「輸入的名稱中存在無法使用的字詞。」。
/// ⚠️ <b>CharaMake 實際走哪一筆未知</b>——所以偵測是「這些字串<u>任一</u>出現」而不是綁定某個 id，
/// 而且錄製模式會把命中的 id 一起印出來，好讓第二版把錨收斂。</item>
/// </list>
/// 這些<b>一律在執行期從 Excel 讀</b>，不把中文字串寫死在程式碼裡。
/// </para>
///
/// <para>
/// 🔴 <b>絕不跨幀保存原生指標。</b>僱員只存 <c>RetainerId</c>（<c>ulong</c>），每次要用時重新掃；
/// 傳喚鈴只在<b>同一幀</b>之內用 <c>IGameObject</c>，用完就丟。
/// </para>
/// <para>
/// 🔴 <b>零封包偽造。</b>所有動作走遊戲自己的 handler：道具用 <c>ActionManager.UseAction</c>、
/// 鈴用 <c>TargetSystem.InteractWithObject</c>、選單用 addon 自己的 callback、
/// 搬裝備用 <c>InventoryManager.MoveItemSlot</c>。
/// </para>
/// </summary>
public sealed unsafe class RetainerBatchRename : TcModule
{
    public override string InternalName => "RetainerBatchRename";

    public override string DisplayName => "批次僱員改名";

    public override string Description =>
        "手動流程：對目前角色的僱員逐一「用一瓶僱員幻想藥 → 傳喚 → 自動卸下全身裝備 → " +
        "（由你手動改名）→ 自動穿回裝備 → 讓僱員返回」。僱員名字全世界唯一，所以改用候選名單池：" +
        "名字被占用會自動換下一個候選。改名畫面本身這一版不自動操作，停在那裡等你改完；" +
        "期間會把畫面與背包變化寫進記錄供之後開發。幻想藥要自備，預設關、按「開始」才動、隨時可停。";

    public override ModuleCategory Category => ModuleCategory.Inventory;

    /// <summary>設定面板按「開始」才動；開著不按，遊戲行為完全不變。</summary>
    public override bool IsManualTrigger => true;

    public override bool HasConfigUI => true;

    // ──────────────────────────── 常數 ────────────────────────────

    /// <summary>僱員幻想藥。<c>exd-tc/7.20/Item.csv</c> 第 8841 列。</summary>
    private const uint FantasiaItemId = 8841;

    private const uint LogMessageAlreadyHasRight = 404;
    private const uint LogMessageRightGranted = 405;

    /// <summary>
    /// 「名字已被占用」的候選訊息列（<c>Addon</c> 表）。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>CharaMake 實際印的是哪一筆，離線無法確定</b>——所以列出全部候選、任一命中就算數。
    /// 這是刻意的過度涵蓋：多認一句的代價是「可能把不是占用的情況也當成占用」，
    /// 那只會多換一個候選；少認一句的代價是流程卡在那裡等一個永遠不會來的訊號。
    /// </remarks>
    private static readonly uint[] AddonRowsNameTaken = [2864];

    /// <inheritdoc cref="AddonRowsNameTaken"/>
    private static readonly uint[] LogMessageRowsNameTaken = [346, 7618, 9248];

    /// <summary>「名字不合法／含禁用詞」的候選訊息列。命中＝這個候選整個不能用（不是被占用）。</summary>
    private static readonly uint[] AddonRowsNameRejected = [2863];

    /// <inheritdoc cref="AddonRowsNameRejected"/>
    private static readonly uint[] LogMessageRowsNameRejected = [1335, 1485, 3375, 5605, 10142];

    /// <summary><c>Addon</c>「讓僱員返回」。AutoRetainer <c>RetainerHandlers.SelectQuit</c> 讀的也是這一列。</summary>
    private const uint AddonRowRetainerQuit = 2383;

    /// <summary><c>EObjName</c>「傳喚鈴」。AutoRetainer <c>Lang.BellName</c> 讀的也是這一列。</summary>
    private const uint EObjNameRowSummoningBell = 2000401;

    /// <summary>日文客戶端的傳喚鈴名（AutoRetainer 的第二候選，照抄；台服用不到但留著無害）。</summary>
    private const string SummoningBellNameJp = "リテイナーベル";

    private const string RetainerListAddon = "RetainerList";
    private const string SelectStringAddon = "SelectString";

    /// <summary>使用者放候選名單的檔名（在外掛設定資料夾底下）。</summary>
    private const string NamePoolFileName = "retainer_names.txt";

    /// <summary>
    /// <c>RetainerList</c> 的 AtkValue 版面：第一筆僱員從 index 3 開始、每筆佔 10 個值、共 10 筆。
    /// </summary>
    /// <remarks>
    /// 來源 ECommons <c>ReaderRetainerList</c>：<c>Loop&lt;Retainer&gt;(3, 10, 10)</c>，
    /// 名字＝該筆第 0 個值、<c>IsActive</c>＝第 8 個值。
    /// 🔴 <b>10 筆是固定長度，沒用到的格子照樣存在</b>——一定要先看 <c>IsActive</c>，
    /// 否則會選到空欄位（AutoRetainer 的台服分支就是為了這個 bug 才補上這道檢查）。
    /// ⚠️ 即使如此我們<b>仍然不靠這個版面決定點誰</b>：讀到的名字必須與目標僱員逐字相同才會點。
    /// </remarks>
    private const int RetainerListFirstValueIndex = 3;

    /// <inheritdoc cref="RetainerListFirstValueIndex"/>
    private const int RetainerListValuesPerEntry = 10;

    /// <inheritdoc cref="RetainerListFirstValueIndex"/>
    private const int RetainerListEntryCount = 10;

    /// <inheritdoc cref="RetainerListFirstValueIndex"/>
    private const int RetainerListNameOffset = 0;

    /// <inheritdoc cref="RetainerListFirstValueIndex"/>
    private const int RetainerListActiveOffset = 8;

    /// <summary>
    /// <c>RetainerList</c> 選擇某位僱員的 callback 事件序號。
    /// </summary>
    /// <remarks>
    /// 逐字對應 ECommons <c>AddonMaster.RetainerList.Entry.Select()</c>：
    /// <c>Callback.Fire(Base, true, 2, (uint)index, ZeroAtkValue, ZeroAtkValue)</c>。
    /// ⚠️ 這是<b>寫死的事件序號</b>，改版可能失效。失效的表現是「按了沒反應」，
    /// 會被 <see cref="TaskQueue"/> 的逾時接住並停下，不會誤點到別的東西。
    /// </remarks>
    private const int RetainerListSelectEventId = 2;

    /// <summary>
    /// 僱員裝備欄的格數（保守估計用）。
    /// </summary>
    /// <remarks>
    /// 📌 13 來自 AutoRetainer <c>Helpers/ItemLevel.cs</c>（<c>for i in 0..13</c>）。
    /// ⚠️ 實際執行一律以 <c>InventoryContainer.Size</c> 為準；這個常數只用在
    /// 「還沒傳喚、算不出真實件數」時的前置閘門。
    /// </remarks>
    private const int RetainerEquipSlotCountEstimate = 13;

    /// <summary>
    /// 傳喚鈴互動距離。數字照抄 AutoRetainer <c>Utils.GetValidInteractionDistance</c>
    /// （Housing 6.5f／旅館 4.75f／其他 4.6f）。
    /// </summary>
    /// <remarks>
    /// 📌 我們<b>不做旅館區域判斷</b>（需要一份 territory 清單而我們沒有可離線驗證的來源），
    /// 非 Housing 一律用比較寬鬆的 4.75f。
    /// 🔑 這樣選的理由是<b>失敗方向</b>：抓太寬只會讓互動那步沒反應而逾時停下（有訊息、可重來）；
    /// 抓太嚴則是使用者明明站在鈴前面卻被擋著，而且無從得知為什麼。
    /// 真正的把關不是這個數字，是後面「<c>RetainerList</c> 到底有沒有開起來」。
    /// </remarks>
    private const float BellInteractDistanceHousing = 6.5f;

    /// <inheritdoc cref="BellInteractDistanceHousing"/>
    private const float BellInteractDistanceDefault = 4.75f;

    /// <summary>單一僱員最多允許的搬移次數（保險絲，不是業務規則）。</summary>
    private const int MaxGearMovesPerRetainer = 40;

    /// <summary>等使用者手動改完名的逾時（毫秒）。30 分鐘——這一步本來就是人在操作。</summary>
    private const int ManualRenameTimeoutMs = 30 * 60 * 1000;

    /// <summary>錄製時每個 addon 最多印幾個 AtkValue，以及字串截斷長度。</summary>
    private const int RecordMaxValues = 32;

    /// <inheritdoc cref="RecordMaxValues"/>
    private const int RecordMaxStringLength = 64;

    private static readonly InventoryType[] PlayerBags =
    [
        InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4,
    ];

    /// <summary>錄製時要盯著看變化的容器。</summary>
    /// <remarks>
    /// 🔑 <b>僱員裝備欄要放在第一個</b>：使用者手動卸裝時，這裡的變化就是
    /// 「遊戲自己走的是哪條搬移路徑」的直接證據——來源容器／格號／道具 id 全在記錄裡。
    /// </remarks>
    private static readonly InventoryType[] WatchedContainers =
    [
        InventoryType.RetainerEquippedItems,
        InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4,
    ];

    // ──────────────────────────── 資料模型 ────────────────────────────

    /// <summary>
    /// 清單上的一列。
    /// 🔴 只存 <see cref="RetainerId"/>，其餘欄位都是<b>每次重掃時覆寫的快照</b>，不是真值來源。
    /// </summary>
    private sealed class RetainerRow
    {
        public ulong RetainerId;
        public string CurrentName = string.Empty;
        public bool Selected;
        public bool OnVenture;
        public int DisplayOrder;

        /// <summary>上一輪跑完後留在背包裡沒穿回去的件數（0＝沒有殘留）。</summary>
        public int StrandedGear;
    }

    /// <summary>名單池裡的一個候選（執行期檢視；狀態的真值在設定裡）。</summary>
    private sealed class NameCandidate
    {
        public string Name = string.Empty;
        public RetainerNameCandidateState State;
        public ulong UsedByRetainerId;
        public ulong UsedByContentId;
        public string UsedByCharacterName = string.Empty;
        public string UsedByRetainerName = string.Empty;
        public string Note = string.Empty;
    }

    /// <summary>卸下來的一件裝備。只存值，不存指標。</summary>
    private readonly record struct StashedGear(int Slot, uint ItemId, bool HighQuality, string Name);

    /// <summary>這一輪要處理的一位僱員。<b>不含新名字</b>——名字是跑到那一步才從名單池取的。</summary>
    private readonly record struct WorkItem(ulong RetainerId, string OldName);

    /// <summary>錄製用的一格庫存快照。</summary>
    private readonly record struct SlotSnapshot(uint ItemId, int Quantity, bool HighQuality);

    // ──────────────────────────── 狀態 ────────────────────────────

    private readonly TaskQueue queue = new() { DefaultTimeoutMs = 20_000 };

    /// <summary>UI 上的僱員清單。以 <c>RetainerId</c> 為鍵長期保留。</summary>
    private readonly List<RetainerRow> rows = [];

    /// <summary>名單池的執行期檢視（由 <see cref="RetainerBatchRenameConfig.NamePoolText"/> 解析而來）。</summary>
    private readonly List<NameCandidate> candidates = [];

    /// <summary>
    /// 上一次解析名單池時的原文，用來判斷要不要重新解析。
    /// </summary>
    /// <remarks>
    /// ⚠️ 初值刻意<b>不是空字串</b>：空字串是合法的名單內容（使用者可以把名單清空），
    /// 用空字串當「還沒解析過」的哨兵會讓第一次解析被跳過。這裡用一個名單裡不可能出現的值。
    /// </remarks>
    private string parsedPoolText = "\uFFFF";

    private readonly List<WorkItem> workList = [];
    private readonly List<StashedGear> stashed = [];

    private int workIndex = -1;
    private int renamedCount;
    private string lastSummary = string.Empty;

    // 快照（Framework tick 更新；Draw 只讀這些，不碰原生記憶體）
    private bool snapshotValid;
    private string snapshotProblem = "尚未讀取";
    private int fantasiaCount = -1;
    private int emptyBagSlots = -1;
    private bool bellReachable;
    private bool bellLookupUsable;

    // 幻想藥旗標偵測
    private bool sawRightGranted;
    private bool sawAlreadyHasRight;
    private int fantasiaCountBeforeUse = -1;

    // 手動改名握手 ＋ 候選名字狀態機
    private bool waitingForManualRename;
    private bool manualRenameConfirmed;
    private string currentCandidate = string.Empty;
    private int candidateAttempts;
    private bool candidateExhausted;
    private string candidateExhaustedReason = string.Empty;

    /// <summary>剛剛偵測到「名字被占用」的訊號（由聊天訊息或使用者按鈕設起）。</summary>
    private bool sawNameTaken;

    /// <summary>剛剛偵測到「名字不能用」的訊號。</summary>
    private bool sawNameRejected;

    // 錄製
    private delegate bool FireCallbackDelegate(AtkUnitBase* addon, uint valueCount, AtkValue* values, bool close);

    private Hook<FireCallbackDelegate>? fireCallbackHook;
    private bool recordingActive;

    /// <summary>錄製是使用者自己開的（不是流程開的）——流程結束時不要把它關掉。</summary>
    private bool recordingStandalone;

    private string recordingUnavailableReason = string.Empty;

    /// <summary>錄製用的庫存快照（(容器, 格號) → 內容）。只存值，不存指標。</summary>
    private readonly Dictionary<(InventoryType Type, int Slot), SlotSnapshot> inventorySnapshot = [];

    private RetainerBatchRenameConfig Config => Plugin.Instance.Config.RetainerBatchRename;

    // ──────────────────────────── 生命週期 ────────────────────────────

    protected override void OnEnable()
    {
        queue.OnTimeout = step =>
        {
            Svc.Log.Information(
                $"[{InternalName}] 流程在「{step}」逾時中止（已完成 {renamedCount} 位）。" +
                $"目前卸下未穿回：{stashed.Count} 件。");
            Svc.Chat.PrintError($"[TC Toolbox] 批次僱員改名在「{step}」逾時，已停止。");
            StopRun($"「{step}」逾時");
        };

        // 🔴 未解析的 CS MemberFunction 位址是 0，掛上去等於 hook 到 0 位址。
        //    這裡把它當成「這一版 UI callback 錄製不能用」而不是硬掛——錄製是附加價值，
        //    取不到位址不該讓整個模組不能用。
        var address = AtkUnitBase.Addresses.FireCallback.Value;
        if (address == 0)
        {
            recordingUnavailableReason = "取不到 AtkUnitBase::FireCallback 的位址";
            Svc.Log.Information(
                $"[{InternalName}] {recordingUnavailableReason}，UI callback 錄製停用（其餘錄製與流程照常）。");
        }
        else
        {
            try
            {
                fireCallbackHook = Svc.Hooks.HookFromAddress<FireCallbackDelegate>(address, FireCallbackDetour);
                Svc.Log.Information($"[{InternalName}] 錄製用 hook 已建立，FireCallback 位址 0x{address:X}（預設停用）。");
            }
            catch (Exception ex)
            {
                recordingUnavailableReason = $"建立 FireCallback hook 失敗：{ex.Message}";
                Svc.Log.Information($"[{InternalName}] {recordingUnavailableReason}，UI callback 錄製停用。");
            }
        }

        Svc.Chat.ChatMessage += OnChatMessage;
        Svc.Framework.Update += OnUpdate;
    }

    protected override void OnDisable()
    {
        Svc.Framework.Update -= OnUpdate;
        Svc.Chat.ChatMessage -= OnChatMessage;

        recordingStandalone = false;
        StopRecording();

        fireCallbackHook?.Dispose();
        fireCallbackHook = null;

        queue.Abort();
        workList.Clear();
        stashed.Clear();
        rows.Clear();
        candidates.Clear();
        inventorySnapshot.Clear();
        parsedPoolText = "\uFFFF";
        workIndex = -1;
        renamedCount = 0;
        waitingForManualRename = false;
        manualRenameConfirmed = false;
        currentCandidate = string.Empty;
        lastSummary = string.Empty;
    }

    private void OnUpdate(IFramework _)
    {
        if (Throttle.Pass($"{InternalName}-Snapshot", 500))
        {
            try
            {
                RefreshSnapshot();
            }
            catch (Exception ex)
            {
                snapshotValid = false;
                snapshotProblem = "讀取僱員資料時發生例外";
                if (Throttle.Pass($"{InternalName}-SnapshotError", 60_000))
                    Svc.Log.Error(ex, $"[{InternalName}] 重掃僱員清單失敗");
            }
        }

        if (recordingActive)
        {
            try
            {
                RecordInventoryChanges();
            }
            catch (Exception ex)
            {
                if (Throttle.Pass($"{InternalName}-RecordInvError", 60_000))
                    Svc.Log.Error(ex, $"[{InternalName}] 錄製庫存變化時發生例外");
            }
        }

        if (waitingForManualRename && !manualRenameConfirmed)
        {
            // 伺服器說名字被占用／不能用 ⇒ 換下一個候選（不必使用者自己按）。
            if (sawNameTaken)
            {
                sawNameTaken = false;
                MarkCurrentCandidate(RetainerNameCandidateState.Taken, "伺服器回報名字已被使用");
            }
            else if (sawNameRejected)
            {
                sawNameRejected = false;
                MarkCurrentCandidate(RetainerNameCandidateState.Rejected, "伺服器回報名字不能使用");
            }

            if (Config.AutoDetectRenameDone) TryAutoDetectRenameDone();
        }

        queue.Tick();
    }

    // ──────────────────────────── 快照 ────────────────────────────

    /// <summary>
    /// 重掃僱員清單與各項閘門數字。
    /// </summary>
    /// <remarks>
    /// 🔴 <c>RetainerManager.Instance()</c> 的宣告是 <c>[StaticAddress(sig, 3)]</c>，<b>沒有</b>
    /// <c>isPointer: true</c>——<c>StaticAddressAttribute</c> 的 <c>isPointer</c> 預設是 <c>false</c>，
    /// 產生器對這種情況回傳的是<b>靜態位址本身</b>，所以它<b>永遠不會回 null</b>
    /// （特徵碼解析失敗時是擲 <c>InvalidOperationException</c>，不是回 null）。
    /// ⇒ 這裡<b>刻意不寫判空</b>（那會是死碼），改成整段包 try：要防的是「擲例外」不是「回 null」。
    /// ⚠️ 這與 <c>AtkStage.Instance()</c> 那種 <c>isPointer: true</c> 的<b>相反</b>，
    /// 那種才會回 null、才必須判空（見 <see cref="UiHelper.GetAddonById"/>）。
    /// </remarks>
    private void RefreshSnapshot()
    {
        SyncCandidatesFromConfig();

        if (!Svc.ClientState.IsLoggedIn || Svc.Objects.LocalPlayer == null ||
            Svc.Condition[ConditionFlag.BetweenAreas] || Svc.Condition[ConditionFlag.BetweenAreas51])
        {
            snapshotValid = false;
            snapshotProblem = "正在切換區域或尚未登入";
            return;
        }

        var manager = RetainerManager.Instance();
        if (!manager->IsReady)
        {
            snapshotValid = false;
            snapshotProblem = "僱員資料尚未就緒（請先開一次傳喚鈴）";
            return;
        }

        var seen = new HashSet<ulong>();
        var retainers = manager->Retainers;
        for (var i = 0; i < retainers.Length; i++)
        {
            var retainer = retainers[i];
            if (retainer.RetainerId == 0) continue;

            var name = ReadRetainerName(ref retainer);
            if (name.Length == 0) continue;

            seen.Add(retainer.RetainerId);

            var row = rows.Find(r => r.RetainerId == retainer.RetainerId);
            if (row == null)
            {
                row = new RetainerRow { RetainerId = retainer.RetainerId };
                rows.Add(row);
            }

            row.CurrentName = name;
            row.OnVenture = retainer.VentureId != 0;
            row.DisplayOrder = manager->DisplayOrder.IndexOf((byte)i);
        }

        rows.RemoveAll(r => !seen.Contains(r.RetainerId));
        rows.Sort((a, b) => a.DisplayOrder.CompareTo(b.DisplayOrder));

        var inventory = InventoryManager.Instance();
        fantasiaCount = inventory == null ? -1 : inventory->GetInventoryItemCount(FantasiaItemId);
        emptyBagSlots = inventory == null ? -1 : (int)inventory->GetEmptySlotsInBag();

        RefreshBellSnapshot();

        snapshotValid = true;
        snapshotProblem = string.Empty;
    }

    /// <summary>
    /// 讀僱員名。
    /// </summary>
    /// <remarks>
    /// ⚠️ 刻意不用產生器給的 <c>NameString</c>：那支是
    /// <c>MemoryMarshal.CreateReadOnlySpanFromNullTerminated</c>，欄位<b>剛好塞滿沒有結尾 0</b>
    /// 時會一路往後掃。這裡自己做<b>有界</b>的讀取，找不到 0 就整段當名字用。
    /// </remarks>
    private static string ReadRetainerName(ref RetainerManager.Retainer retainer)
    {
        var span = retainer.Name;
        var length = span.IndexOf((byte)0);
        if (length < 0) length = span.Length;
        return length == 0 ? string.Empty : Encoding.UTF8.GetString(span[..length]);
    }

    /// <summary>
    /// 目前站得到傳喚鈴旁邊嗎。
    /// </summary>
    /// <remarks>
    /// 判法照抄 AutoRetainer <c>Utils.GetReachableRetainerBell</c>：掃 ObjectTable 找
    /// <c>ObjectKind.Housing</c> 或 <c>ObjectKind.EventObj</c> 且<b>名字等於「傳喚鈴」</b>的物件
    /// ——<b>不是</b>比對 DataId（AutoRetainer 對傳喚鈴完全沒用 DataId）。
    /// 名字從 <c>EObjName</c> 表讀，所以改版換字串也不會壞。
    /// <para>🔴 這裡拿到的 <c>IGameObject</c> 絕不留到下一幀。距離算完就丟。</para>
    /// </remarks>
    private void RefreshBellSnapshot()
    {
        bellReachable = false;

        var names = GetBellNames();
        bellLookupUsable = names.Count > 0;
        if (!bellLookupUsable) return;

        var player = Svc.Objects.LocalPlayer;
        if (player == null) return;
        var playerPosition = player.Position;

        foreach (var obj in Svc.Objects)
        {
            if (obj.ObjectKind != ObjectKind.Housing && obj.ObjectKind != ObjectKind.EventObj) continue;
            if (!obj.IsTargetable) continue;
            if (!names.Contains(obj.Name.TextValue)) continue;

            var limit = obj.ObjectKind == ObjectKind.Housing
                ? BellInteractDistanceHousing
                : BellInteractDistanceDefault;

            if (Vector3.Distance(obj.Position, playerPosition) < limit)
            {
                bellReachable = true;
                return;
            }
        }
    }

    private static List<string> GetBellNames()
    {
        var result = new List<string>();

        var row = Svc.Data.GetExcelSheet<EObjName>().GetRowOrDefault(EObjNameRowSummoningBell);
        var text = row?.Singular.ExtractText() ?? string.Empty;
        if (!string.IsNullOrEmpty(text)) result.Add(text);

        result.Add(SummoningBellNameJp);
        return result;
    }

    private static string GetAddonText(uint rowId) =>
        Svc.Data.GetExcelSheet<LuminaAddon>().GetRowOrDefault(rowId)?.Text.ExtractText() ?? string.Empty;

    private static string GetLogMessageText(uint rowId) =>
        Svc.Data.GetExcelSheet<LogMessage>().GetRowOrDefault(rowId)?.Text.ExtractText() ?? string.Empty;

    // ──────────────────────────── 名單池 ────────────────────────────

    /// <summary>
    /// 把設定裡的名單原文解析成候選清單，並套上持久化的狀態。
    /// </summary>
    /// <remarks>
    /// 🔑 狀態的鍵是<b>候選名字本身</b>（不是行號）：使用者重新排序或增刪名單時，
    /// 已知被占用的名字不會因為位置變了就被重試。
    /// <para>
    /// 預檢（寬鬆，只剔除「幾乎確定不能用」的形狀）：空行→不列入；名單內重複→標
    /// <see cref="RetainerNameCandidateState.Rejected"/>；含全形符號／連續空白→標 <c>Rejected</c>；
    /// 首尾空白→自動去除並留一句說明（不擋）。<b>伺服器才是權威</b>，其餘一律放行。
    /// </para>
    /// </remarks>
    private void SyncCandidatesFromConfig()
    {
        var poolText = Config.NamePoolText ?? string.Empty;
        if (poolText == parsedPoolText) return;

        parsedPoolText = poolText;
        candidates.Clear();

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var lines = poolText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        foreach (var rawLine in lines)
        {
            var name = rawLine.Trim();
            if (name.Length == 0) continue;

            var candidate = new NameCandidate { Name = name };

            if (!seen.Add(name))
            {
                candidate.State = RetainerNameCandidateState.Rejected;
                candidate.Note = "名單內重複";
            }
            else if (rawLine != name)
            {
                candidate.Note = "已自動去除首尾空白";
            }

            if (candidate.State == RetainerNameCandidateState.Available &&
                !IsPlausibleName(name, out var problem))
            {
                candidate.State = RetainerNameCandidateState.Rejected;
                candidate.Note = problem;
            }

            // 持久化的狀態優先於預檢結果（「已用」「已被占用」是事實，預檢只是猜測）。
            if (Config.CandidateStatus.TryGetValue(name, out var saved) &&
                saved.State != RetainerNameCandidateState.Available)
            {
                candidate.State = saved.State;
                candidate.UsedByRetainerId = saved.UsedByRetainerId;
                candidate.UsedByContentId = saved.UsedByContentId;
                candidate.UsedByCharacterName = saved.UsedByCharacterName ?? string.Empty;
                candidate.UsedByRetainerName = saved.UsedByRetainerName ?? string.Empty;
                if (!string.IsNullOrEmpty(saved.Note)) candidate.Note = saved.Note;
            }

            candidates.Add(candidate);
        }

        Svc.Log.Information(
            $"[{InternalName}] 名單池已重新解析：共 {candidates.Count} 個候選，" +
            $"可用 {AvailableCandidateCount()} 個。");

        foreach (var candidate in candidates)
        {
            if (candidate.State == RetainerNameCandidateState.Rejected)
                Svc.Log.Information($"[{InternalName}] 候選「{candidate.Name}」不列入：{candidate.Note}");
        }
    }

    /// <summary>把一個候選的狀態寫回設定並存檔。</summary>
    /// <remarks>🔴 這份狀態是<b>全域</b>的：僱員名全世界唯一，換角色不重置。</remarks>
    private void PersistCandidate(NameCandidate candidate)
    {
        Config.CandidateStatus[candidate.Name] = new RetainerNameCandidateStatus
        {
            State = candidate.State,
            UsedByRetainerId = candidate.UsedByRetainerId,
            UsedByContentId = candidate.UsedByContentId,
            UsedByCharacterName = candidate.UsedByCharacterName,
            UsedByRetainerName = candidate.UsedByRetainerName,
            Note = candidate.Note,
        };
        Plugin.Instance.Config.Save();
    }

    private int AvailableCandidateCount() =>
        candidates.FindAll(c => c.State == RetainerNameCandidateState.Available).Count;

    private NameCandidate? PeekNextCandidate() =>
        candidates.Find(c => c.State == RetainerNameCandidateState.Available);

    /// <summary>
    /// 指派下一個可用候選給目前這隻僱員。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>不會跳過任何候選，也不會重用</b>：一律取名單順序上第一個
    /// <see cref="RetainerNameCandidateState.Available"/> 的。
    /// 用完（或同一隻連試次數用盡）就把 <see cref="candidateExhausted"/> 立起來。
    /// </remarks>
    private bool TryAssignNextCandidate()
    {
        if (candidateAttempts >= Config.MaxCandidateAttemptsPerRetainer)
        {
            candidateExhausted = true;
            candidateExhaustedReason =
                $"同一位僱員已經連續試了 {candidateAttempts} 個候選都不能用" +
                $"（上限 {Config.MaxCandidateAttemptsPerRetainer}）。";
            currentCandidate = string.Empty;
            Svc.Log.Information($"[{InternalName}] {candidateExhaustedReason}");
            return false;
        }

        var next = PeekNextCandidate();
        if (next == null)
        {
            candidateExhausted = true;
            candidateExhaustedReason = "名單池裡已經沒有可用的候選名字了。";
            currentCandidate = string.Empty;
            Svc.Log.Information($"[{InternalName}] {candidateExhaustedReason}");
            return false;
        }

        currentCandidate = next.Name;
        candidateAttempts++;
        Svc.Log.Information(
            $"[{InternalName}] 指派候選名字「{currentCandidate}」（這位僱員的第 {candidateAttempts} 個候選）。");
        return true;
    }

    /// <summary>把目前候選標成某個狀態，然後自動換下一個。</summary>
    private void MarkCurrentCandidate(RetainerNameCandidateState state, string note)
    {
        if (currentCandidate.Length == 0) return;

        var candidate = candidates.Find(c => c.Name == currentCandidate);
        if (candidate != null)
        {
            candidate.State = state;
            candidate.Note = note;
            PersistCandidate(candidate);
        }

        Svc.Log.Information($"[{InternalName}] 候選「{currentCandidate}」標記為 {state}：{note}。改用下一個候選。");
        TryAssignNextCandidate();
    }

    /// <summary>把某個候選標成「已用於這隻僱員」，並記下當時是哪個角色的哪一位。</summary>
    /// <remarks>
    /// 📌 記角色資訊純粹是為了視窗上看得懂「這個名字被誰用掉了」——使用者的 81 位僱員
    /// 分散在多個角色上，只記 <c>RetainerId</c> 的話人是對不出來的。
    /// ⚠️ 取角色資訊失敗不影響主要功能，所以整段包 try、失敗就留空。
    /// </remarks>
    private void MarkCandidateUsed(string name, ulong retainerId, string previousRetainerName)
    {
        var candidate = candidates.Find(c => c.Name == name);
        if (candidate == null) return;

        candidate.State = RetainerNameCandidateState.Used;
        candidate.UsedByRetainerId = retainerId;
        candidate.UsedByRetainerName = previousRetainerName;

        try
        {
            candidate.UsedByContentId = Svc.PlayerState.ContentId;
            candidate.UsedByCharacterName = Svc.PlayerState.CharacterName ?? string.Empty;
        }
        catch (Exception ex)
        {
            Svc.Log.Information($"[{InternalName}] 取角色資訊失敗（不影響改名）：{ex.Message}");
        }

        candidate.Note = "已使用";
        PersistCandidate(candidate);

        Svc.Log.Information(
            $"[{InternalName}] 候選「{name}」記為已用：角色「{candidate.UsedByCharacterName}」的僱員" +
            $"（原名「{previousRetainerName}」，RetainerId {retainerId}）。");
    }

    /// <summary>把文字切成一行一名（去空白、丟空行）。</summary>
    private static List<string> SplitLines(string text)
    {
        var result = new List<string>();
        foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length > 0) result.Add(line);
        }

        return result;
    }

    /// <summary>
    /// 把一批名字<b>追加</b>到名單池，池子裡已經有的就跳過。
    /// </summary>
    /// <remarks>
    /// 🔴 刻意是<b>追加</b>不是取代：取代會把使用者自己加的名字洗掉，
    /// 而且候選狀態（已用／已被占用）是以名字為鍵存的，洗掉名單等於把那些紀錄變成孤兒。
    /// 回傳實際新增的筆數。
    /// </remarks>
    private int AppendNamesToPool(List<string> names)
    {
        var current = Config.NamePoolText ?? string.Empty;
        var existing = new HashSet<string>(SplitLines(current), StringComparer.Ordinal);

        var added = new List<string>();
        foreach (var name in names)
        {
            var line = name.Trim();
            if (line.Length == 0) continue;
            if (!existing.Add(line)) continue;
            added.Add(line);
        }

        if (added.Count == 0) return 0;

        var builder = new StringBuilder(current);
        if (builder.Length > 0 && !current.EndsWith("\n", StringComparison.Ordinal))
            builder.Append('\n');
        foreach (var line in added) builder.Append(line).Append('\n');

        Config.NamePoolText = builder.ToString();
        Plugin.Instance.Config.Save();
        parsedPoolText = "\uFFFF";
        return added.Count;
    }

    /// <summary>
    /// 新名字的<b>寬鬆</b>本機預檢。
    /// </summary>
    /// <remarks>
    /// 📌 規則參考 <c>Addon</c> 15233（寵物暱稱規則）：可含漢字；符號限
    /// <c>' . , : ; ! ? &amp; - _</c>；無全形符號；半形空格與底線不可連續；空格不可在首尾；不可全是符號。
    /// 🔴 <b>僱員是不是同一套規則我們沒有離線證據</b>，所以這裡只擋「幾乎確定會被拒絕」的形狀，
    /// 其餘一律放行——<b>伺服器才是權威</b>。擋太嚴的代價是使用者取不了合法的名字而且不知道為什麼。
    /// </remarks>
    private static bool IsPlausibleName(string name, out string problem)
    {
        problem = string.Empty;

        if (name.Length != name.Trim().Length)
        {
            problem = "頭尾有空白";
            return false;
        }

        if (name.Contains("  ", StringComparison.Ordinal) || name.Contains("__", StringComparison.Ordinal))
        {
            problem = "含有連續的空格或底線";
            return false;
        }

        var hasLetterOrDigit = false;
        foreach (var c in name)
        {
            if (char.IsLetterOrDigit(c))
            {
                hasLetterOrDigit = true;
                continue;
            }

            // 全形符號（CJK 標點 U+3000~U+303F、全形 ASCII 區 U+FF01~U+FF65 裡的非文數字）。
            // ⚠️ 全形數字／全形英文本身是 IsLetterOrDigit，上面已經放行，不會落到這裡。
            if ((c >= '\u3000' && c <= '\u303F') || (c >= '\uFF01' && c <= '\uFF65'))
            {
                problem = $"含有全形符號「{c}」";
                return false;
            }
        }

        if (!hasLetterOrDigit)
        {
            problem = "不能只由符號組成";
            return false;
        }

        return true;
    }

    // ──────────────────────────── 前置閘門 ────────────────────────────

    private List<string> BuildBlockers()
    {
        var blockers = new List<string>();

        if (!snapshotValid)
        {
            blockers.Add(snapshotProblem);
            return blockers;
        }

        var selected = rows.FindAll(r => r.Selected);
        if (selected.Count == 0)
        {
            blockers.Add("沒有勾選任何僱員。");
            return blockers;
        }

        var available = AvailableCandidateCount();
        if (available < selected.Count)
        {
            blockers.Add(
                $"名單池可用候選不足：勾了 {selected.Count} 位僱員，只有 {available} 個沒用過的候選名字。");
        }

        if (fantasiaCount < 0)
            blockers.Add("讀不到背包內容。");
        else if (fantasiaCount < selected.Count)
            blockers.Add($"{ItemNames.Get(FantasiaItemId)}不足：需要 {selected.Count} 瓶，只有 {fantasiaCount} 瓶。");

        if (emptyBagSlots < 0)
            blockers.Add("讀不到背包空位。");
        else if (emptyBagSlots < RetainerEquipSlotCountEstimate)
        {
            blockers.Add(
                $"背包空位不足：卸裝最多需要 {RetainerEquipSlotCountEstimate} 格，目前只有 {emptyBagSlots} 格。");
        }

        foreach (var row in selected)
        {
            if (row.OnVenture)
                blockers.Add($"「{row.CurrentName}」正在探險中，探險中無法更換裝備。");
        }

        if (!bellLookupUsable)
            blockers.Add("讀不到傳喚鈴的名稱資料（EObjName 表），無法確認你站在鈴旁邊。");
        else if (!bellReachable)
            blockers.Add("附近沒有可互動的傳喚鈴，請先走到傳喚鈴旁邊。");

        if (GetAddonText(AddonRowRetainerQuit).Length == 0)
            blockers.Add("讀不到「讓僱員返回」的選單文字（Addon 表），流程無法收尾。");

        return blockers;
    }

    // ──────────────────────────── 流程 ────────────────────────────

    private void Start()
    {
        if (queue.IsBusy) return;

        var blockers = BuildBlockers();
        if (blockers.Count > 0)
        {
            Svc.Log.Information($"[{InternalName}] 前置檢查未過，未開始：{string.Join("／", blockers)}");
            Svc.Chat.PrintError($"[TC Toolbox] 批次僱員改名未開始：{blockers[0]}");
            return;
        }

        workList.Clear();
        foreach (var row in rows)
        {
            if (!row.Selected) continue;
            workList.Add(new WorkItem(row.RetainerId, row.CurrentName));
        }

        workIndex = -1;
        renamedCount = 0;
        stashed.Clear();
        lastSummary = string.Empty;

        Svc.Log.Information(
            $"[{InternalName}] 開始：{workList.Count} 位僱員、可用候選 {AvailableCandidateCount()} 個、" +
            $"{ItemNames.Get(FantasiaItemId)} {fantasiaCount} 瓶、背包空位 {emptyBagSlots} 格。" +
            $"錄製＝{(Config.RecordDuringManualRename ? "開" : "關")}" +
            (recordingUnavailableReason.Length > 0
                ? $"（UI callback 錄製不可用：{recordingUnavailableReason}）"
                : string.Empty));

        EnqueueNextRetainer();
    }

    private void EnqueueNextRetainer()
    {
        queue.Enqueue("挑選下一位僱員", () =>
        {
            workIndex++;
            if (workIndex >= workList.Count)
            {
                FinishRun("全部完成");
                return true;
            }

            var work = workList[workIndex];
            Svc.Log.Information(
                $"[{InternalName}] ({workIndex + 1}/{workList.Count}) 開始處理「{work.OldName}」。");

            EnqueueOneRetainer(work);
            return true;
        });
    }

    private void EnqueueOneRetainer(WorkItem work)
    {
        // ── a. 用一瓶幻想藥 ──
        queue.Enqueue($"使用{ItemNames.Get(FantasiaItemId)}（{work.OldName}）", () =>
        {
            if (!TryReady(out var reason)) return AbortWith(reason);

            var inventory = InventoryManager.Instance();
            if (inventory == null) return AbortWith("讀不到背包內容。");

            var count = inventory->GetInventoryItemCount(FantasiaItemId);
            if (count <= 0) return AbortWith($"背包裡沒有{ItemNames.Get(FantasiaItemId)}了。");

            sawRightGranted = false;
            sawAlreadyHasRight = false;
            fantasiaCountBeforeUse = count;

            var actionManager = ActionManager.Instance();
            if (actionManager == null) return AbortWith("取不到 ActionManager。");

            // 📌 extraParam: 65535 ＝「從背包裡挑一件」，艦隊先例：本 repo AutoGysahlGreens／OpenAllCoffers。
            //    ⚠️ 回傳 true 不代表伺服器受理，所以下一步是等真正的訊號，不是相信這個回傳值。
            actionManager->UseAction(ActionType.Item, FantasiaItemId, extraParam: 65535);
            return true;
        });

        queue.Enqueue("等待改名權利生效", () =>
        {
            // 🔑 三個獨立訊號，任一成立就往下走：
            //    ① LogMessage 404「已經擁有…的權利了」＝旗標本來就在（上一輪沒消耗掉）⇒ 可續行
            //    ② LogMessage 405「可以重新設定僱員的容貌了…」＝旗標剛立起來
            //    ③ 背包裡的幻想藥少了一瓶＝伺服器確實受理了（最不會騙人的那個）
            if (sawAlreadyHasRight)
            {
                Svc.Log.Information(
                    $"[{InternalName}] 收到「已經擁有重新設定僱員容貌的權利」（LogMessage {LogMessageAlreadyHasRight}）：" +
                    "旗標本來就在，這一瓶沒有被消耗，視為可續行。");
                return true;
            }

            if (sawRightGranted) return true;

            var inventory = InventoryManager.Instance();
            if (inventory == null) return false;

            return inventory->GetInventoryItemCount(FantasiaItemId) < fantasiaCountBeforeUse;
        }, 15_000);

        // ── b. 開鈴 → 選僱員 ──
        queue.Enqueue("互動傳喚鈴", () =>
        {
            if (!TryReady(out var reason)) return AbortWith(reason);

            if (Svc.Condition[ConditionFlag.OccupiedSummoningBell]) return true;
            if (UiHelper.IsAddonReady(RetainerListAddon)) return true;

            if (!Throttle.Pass($"{InternalName}-Bell", 1_500)) return false;

            InteractWithNearbyBell();
            return false;
        }, 30_000);

        queue.EnqueueWait("等待僱員清單開啟", () => UiHelper.IsAddonReady(RetainerListAddon), 30_000);

        queue.Enqueue($"選擇僱員「{work.OldName}」", () =>
        {
            if (!Throttle.Pass($"{InternalName}-SelectRetainer", 1_000)) return false;

            var addon = UiHelper.GetAddon(RetainerListAddon);
            if (!UiHelper.IsReady(addon)) return false;

            if (!TryFindRetainerListIndex(addon, work.OldName, out var index, out var seenNames))
                return AbortWith($"僱員清單上找不到「{work.OldName}」（讀到的是：{seenNames}）。");

            // 🔴 只有名字逐字相同才會點。版面若改變＝找不到＝上面已經停下，不會誤點別人。
            UiHelper.FireCallback(
                addon, true, RetainerListSelectEventId, (uint)index, default(AtkValue), default(AtkValue));
            return true;
        }, 20_000);

        queue.EnqueueWait("等待僱員裝備欄載入", () =>
            Svc.Condition[ConditionFlag.OccupiedSummoningBell] && TryGetRetainerEquipContainer(out _),
            30_000);

        // ── c. 卸裝 ──
        queue.Enqueue("記錄目前裝備", () =>
        {
            if (!TryGetRetainerEquipContainer(out var container))
                return AbortWith("讀不到僱員裝備欄（RetainerEquippedItems 尚未載入）。");

            stashed.Clear();
            for (var i = 0; i < container->Size; i++)
            {
                var item = container->GetInventorySlot(i);
                if (item == null || item->ItemId == 0) continue;

                var hq = (item->Flags & InventoryItem.ItemFlags.HighQuality) != 0;
                stashed.Add(new StashedGear(i, item->ItemId, hq, ItemNames.Get(item->ItemId, hq)));
            }

            Svc.Log.Information(
                $"[{InternalName}] 「{work.OldName}」目前穿著 {stashed.Count} 件：" +
                $"{string.Join("、", stashed.ConvertAll(g => $"#{g.Slot} {g.Name}"))}");

            if (stashed.Count == 0) return true;

            var inventory = InventoryManager.Instance();
            var empty = inventory == null ? 0 : (int)inventory->GetEmptySlotsInBag();
            if (empty < stashed.Count)
                return AbortWith($"背包空位不足：要卸 {stashed.Count} 件，只有 {empty} 格。");

            return true;
        });

        EnqueueUndress(work);

        queue.EnqueueDelay(Config.UndressVerifyDelayMs, "等待伺服器確認卸裝");

        queue.Enqueue("驗證卸裝結果", () =>
        {
            if (stashed.Count == 0) return true;

            if (!TryGetRetainerEquipContainer(out var container))
                return AbortWith("驗證卸裝時讀不到僱員裝備欄。");

            var remaining = 0;
            for (var i = 0; i < container->Size; i++)
            {
                var item = container->GetInventorySlot(i);
                if (item != null && item->ItemId != 0) remaining++;
            }

            if (remaining > 0)
            {
                // 🔴 本機容器會被 MoveItemSlot 同步改掉，所以「當下看起來成功」不算數；
                //    等一段時間之後裝備又出現在身上＝伺服器把它退回來了。這時候不能繼續改名。
                return AbortWith(
                    $"卸裝沒有真的生效：等待 {Config.UndressVerifyDelayMs}ms 後僱員身上還有 {remaining} 件裝備" +
                    "（很可能是伺服器把搬移退回了）。");
            }

            return true;
        });

        // ── d. 指派候選名字 → 錄製模式 → 等使用者手動改名 ──
        queue.Enqueue("指派候選名字", () =>
        {
            candidateAttempts = 0;
            candidateExhausted = false;
            candidateExhaustedReason = string.Empty;
            currentCandidate = string.Empty;
            sawNameTaken = false;
            sawNameRejected = false;

            if (!TryAssignNextCandidate())
                return AbortWith(candidateExhaustedReason);

            waitingForManualRename = true;
            manualRenameConfirmed = false;
            StartRecording(work);
            return true;
        });

        queue.Enqueue($"等待手動改名（{work.OldName}）", () =>
        {
            if (manualRenameConfirmed) return true;
            if (candidateExhausted) return AbortWith(candidateExhaustedReason);
            return false;
        }, ManualRenameTimeoutMs);

        queue.Enqueue("結束錄製模式", () =>
        {
            waitingForManualRename = false;
            if (!recordingStandalone) StopRecording();

            var nowName = LookupRetainerName(work.RetainerId);
            Svc.Log.Information(
                $"[{InternalName}] 改名前「{work.OldName}」→ 目前「{nowName}」（最後指派的候選「{currentCandidate}」）。");

            if (nowName.Length == 0 || nowName == work.OldName)
            {
                Svc.Log.Information(
                    $"[{InternalName}] 名字沒有變化——可能是使用者取消了改名。仍然照常把裝備穿回去。");
            }
            else
            {
                renamedCount++;

                // 🔑 用「僱員現在真正叫什麼」去對名單，不是用我們指派的候選 ——
                //    使用者完全可能在改名畫面裡打了別的名字，那時候我們的指派是錯的。
                if (candidates.Exists(c => c.Name == nowName))
                {
                    MarkCandidateUsed(nowName, work.RetainerId, work.OldName);
                }
                else
                {
                    Svc.Log.Information(
                        $"[{InternalName}] 僱員現名不在名單內：「{nowName}」（不阻擋，只是這個名字沒有被記成已用）。");
                }
            }

            currentCandidate = string.Empty;
            return true;
        });

        // ── e. 穿回去 ──
        EnqueueRedress(work);

        // ── f. 讓僱員返回 ──
        queue.Enqueue("讓僱員返回", () =>
        {
            var quitText = GetAddonText(AddonRowRetainerQuit);
            if (quitText.Length == 0) return AbortWith("讀不到「讓僱員返回」的選單文字。");

            if (!Throttle.Pass($"{InternalName}-Quit", 1_000)) return false;

            var addon = UiHelper.GetAddon(SelectStringAddon);
            if (!UiHelper.IsReady(addon)) return false;

            var entries = UiHelper.GetSelectStringEntries(addon);
            var index = entries.FindIndex(e => e.StartsWith(quitText, StringComparison.Ordinal));
            if (index < 0) return false;

            UiHelper.SelectStringEntry(addon, index);
            return true;
        }, 30_000);

        queue.EnqueueDelay(1_000, "間隔");

        EnqueueNextRetainer();
    }

    /// <summary>把「卸下一件」排進佇列，直到裝備欄空為止。</summary>
    private void EnqueueUndress(WorkItem work)
    {
        var moves = 0;

        queue.Enqueue($"卸下裝備（{work.OldName}）", () =>
        {
            if (stashed.Count == 0) return true;

            if (++moves > MaxGearMovesPerRetainer)
                return AbortWith($"卸裝反覆超過 {MaxGearMovesPerRetainer} 次，強制停止。請回報。");

            if (!Throttle.Pass($"{InternalName}-Undress", 200)) return false;

            if (!TryGetRetainerEquipContainer(out var container))
                return AbortWith("卸裝途中讀不到僱員裝備欄。");

            var slot = -1;
            for (var i = 0; i < container->Size; i++)
            {
                var item = container->GetInventorySlot(i);
                if (item != null && item->ItemId != 0)
                {
                    slot = i;
                    break;
                }
            }

            if (slot < 0) return true; // 空了＝這一步完成

            var manager = InventoryManager.Instance();
            if (manager == null) return AbortWith("取不到 InventoryManager。");

            if (!TryFindEmptyBagSlot(manager, out var destination, out var destinationSlot))
                return AbortWith("背包已滿，無法繼續卸裝。");

            // ⚠️ 名稱一定要在搬移之前抓：MoveItemSlot 會同步清空來源格。
            var source = container->GetInventorySlot(slot);
            var itemId = source->ItemId;
            var name = ItemNames.Get(itemId, (source->Flags & InventoryItem.ItemFlags.HighQuality) != 0);

            // 🔴 a6: true 絕對不能省。省略＝預設 false＝只改本機容器、一個封包都不送
            //    （台服 7.20 二進位鑑識定案，細節見 AutoInventoryTransfer 的型別註解）。
            var result = manager->MoveItemSlot(
                InventoryType.RetainerEquippedItems, (ushort)slot, destination, (ushort)destinationSlot, a6: true);

            var after = container->GetInventorySlot(slot);
            if (result != 0 || after == null || after->ItemId != 0)
            {
                return AbortWith(
                    $"卸下「{name}」失敗（MoveItemSlot 回傳 {result}），已停止。" +
                    "僱員裝備欄走 MoveItemSlot 這條路徑沒有實機證據，這很可能就是它不成立——" +
                    "請改用「開始錄製（我自己操作）」手動卸一次，記錄裡會顯示遊戲自己走的是哪條路徑。");
            }

            Svc.Log.Debug($"[{InternalName}] 卸下 #{slot}「{name}」→ {destination}#{destinationSlot}");
            return false;
        }, 60_000);
    }

    /// <summary>把先前記錄的裝備逐件穿回原本的格子。</summary>
    private void EnqueueRedress(WorkItem work)
    {
        var index = 0;
        var failed = 0;

        queue.Enqueue($"穿回裝備（{work.OldName}）", () =>
        {
            if (index >= stashed.Count)
            {
                if (failed > 0)
                {
                    Svc.Log.Information(
                        $"[{InternalName}] 「{work.OldName}」有 {failed} 件裝備沒能穿回去，留在背包裡。");
                    MarkStranded(work.RetainerId, failed);
                }

                stashed.Clear();
                return true;
            }

            if (!Throttle.Pass($"{InternalName}-Redress", 200)) return false;

            var gear = stashed[index];

            if (!TryGetRetainerEquipContainer(out var container))
                return AbortWith("穿回裝備時讀不到僱員裝備欄。");

            var manager = InventoryManager.Instance();
            if (manager == null) return AbortWith("取不到 InventoryManager。");

            if (!TryFindInBags(manager, gear.ItemId, gear.HighQuality, out var source, out var sourceSlot))
            {
                Svc.Log.Information($"[{InternalName}] 背包裡找不到「{gear.Name}」，這件跳過（它會留在原處）。");
                failed++;
                index++;
                return false;
            }

            var result = manager->MoveItemSlot(
                source, (ushort)sourceSlot, InventoryType.RetainerEquippedItems, (ushort)gear.Slot, a6: true);

            var landed = container->GetInventorySlot(gear.Slot);
            if (result != 0 || landed == null || landed->ItemId != gear.ItemId)
            {
                Svc.Log.Information(
                    $"[{InternalName}] 穿回「{gear.Name}」到 #{gear.Slot} 失敗（回傳 {result}），這件跳過。");
                failed++;
            }
            else
            {
                Svc.Log.Debug($"[{InternalName}] 穿回 #{gear.Slot}「{gear.Name}」");
            }

            index++;
            return false;
        }, 120_000);
    }

    /// <summary>中止整條佇列並留下一行說明。回傳 <c>null</c> 給 <see cref="TaskQueue"/>。</summary>
    private bool? AbortWith(string reason)
    {
        Svc.Log.Information($"[{InternalName}] 中止：{reason}（已完成 {renamedCount} 位）");
        Svc.Chat.PrintError($"[TC Toolbox] 批次僱員改名已停止：{reason}");
        StopRun(reason);
        return null;
    }

    private void StopRun(string reason)
    {
        if (!recordingStandalone) StopRecording();
        waitingForManualRename = false;
        manualRenameConfirmed = false;
        currentCandidate = string.Empty;

        if (stashed.Count > 0)
        {
            // 🔴 刻意不做回滾：已經卸下來的裝備留在背包裡，由使用者自己決定怎麼處理。
            //    自動穿回去要再送一輪封包，而我們此刻正處在「有一步沒照預期發生」的狀態。
            var work = workIndex >= 0 && workIndex < workList.Count ? workList[workIndex] : default;
            MarkStranded(work.RetainerId, stashed.Count);
            Svc.Log.Information(
                $"[{InternalName}] 停止時仍有 {stashed.Count} 件裝備在背包裡沒穿回：" +
                $"{string.Join("、", stashed.ConvertAll(g => g.Name))}");
            stashed.Clear();
        }

        lastSummary = $"已停止（{reason}）：完成 {renamedCount}／{workList.Count} 位。";
        queue.Abort();
    }

    private void FinishRun(string reason)
    {
        if (!recordingStandalone) StopRecording();
        waitingForManualRename = false;
        lastSummary = $"{reason}：完成 {renamedCount}／{workList.Count} 位。";
        Svc.Log.Information($"[{InternalName}] {lastSummary}");
        Svc.Chat.Print($"[TC Toolbox] 批次僱員改名{lastSummary}");
        queue.Abort();
    }

    private void MarkStranded(ulong retainerId, int count)
    {
        if (retainerId == 0 || count <= 0) return;
        var row = rows.Find(r => r.RetainerId == retainerId);
        if (row != null) row.StrandedGear = count;
    }

    // ──────────────────────────── 原生小工具 ────────────────────────────

    private static bool TryReady(out string reason)
    {
        reason = string.Empty;

        if (!Svc.ClientState.IsLoggedIn || Svc.Objects.LocalPlayer == null ||
            Svc.Condition[ConditionFlag.BetweenAreas] || Svc.Condition[ConditionFlag.BetweenAreas51])
        {
            reason = "正在切換區域或尚未登入。";
            return false;
        }

        if (InventoryManager.Instance() == null)
        {
            reason = "取不到背包資料。";
            return false;
        }

        return true;
    }

    /// <summary>
    /// 取僱員裝備欄容器。
    /// ⚠️ <c>RetainerEquippedItems</c> <b>只有在僱員視窗開著時才載入</b>，沒開就是 null
    /// ——那是常態不是異常（AutoRetainer 的除錯視窗註解也這麼寫）。
    /// </summary>
    private static bool TryGetRetainerEquipContainer(out InventoryContainer* container)
    {
        container = null;

        var manager = InventoryManager.Instance();
        if (manager == null) return false;

        var found = manager->GetInventoryContainer(InventoryType.RetainerEquippedItems);
        if (found == null || !found->IsLoaded || found->Size <= 0) return false;

        container = found;
        return true;
    }

    private static bool TryFindEmptyBagSlot(
        InventoryManager* manager, out InventoryType destination, out int destinationSlot)
    {
        destination = InventoryType.Invalid;
        destinationSlot = -1;

        foreach (var type in PlayerBags)
        {
            var inventory = manager->GetInventoryContainer(type);
            if (inventory == null || !inventory->IsLoaded) continue;

            for (var i = 0; i < inventory->Size; i++)
            {
                var item = inventory->GetInventorySlot(i);
                if (item == null || item->ItemId != 0) continue;

                destination = type;
                destinationSlot = i;
                return true;
            }
        }

        return false;
    }

    private static bool TryFindInBags(
        InventoryManager* manager, uint itemId, bool highQuality, out InventoryType source, out int sourceSlot)
    {
        source = InventoryType.Invalid;
        sourceSlot = -1;

        foreach (var type in PlayerBags)
        {
            var inventory = manager->GetInventoryContainer(type);
            if (inventory == null || !inventory->IsLoaded) continue;

            for (var i = 0; i < inventory->Size; i++)
            {
                var item = inventory->GetInventorySlot(i);
                if (item == null || item->ItemId != itemId) continue;

                var hq = (item->Flags & InventoryItem.ItemFlags.HighQuality) != 0;
                if (hq != highQuality) continue;

                source = type;
                sourceSlot = i;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 對附近的傳喚鈴送出互動。
    /// 🔴 <c>IGameObject</c> 只在這一幀之內使用，不留到下一幀。
    /// </summary>
    private static bool InteractWithNearbyBell()
    {
        var names = GetBellNames();
        if (names.Count == 0) return false;

        var player = Svc.Objects.LocalPlayer;
        if (player == null) return false;
        var playerPosition = player.Position;

        foreach (var obj in Svc.Objects)
        {
            if (obj.ObjectKind != ObjectKind.Housing && obj.ObjectKind != ObjectKind.EventObj) continue;
            if (!obj.IsTargetable) continue;
            if (!names.Contains(obj.Name.TextValue)) continue;

            var limit = obj.ObjectKind == ObjectKind.Housing
                ? BellInteractDistanceHousing
                : BellInteractDistanceDefault;
            if (Vector3.Distance(obj.Position, playerPosition) >= limit) continue;

            var targetSystem = TargetSystem.Instance();
            if (targetSystem == null) return false;

            targetSystem->InteractWithObject(
                (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)obj.Address, false);
            return true;
        }

        return false;
    }

    /// <summary>
    /// 在 <c>RetainerList</c> 上找出名字逐字相符的那一筆。
    /// 🔴 找不到就回 false ——<b>絕不退而求其次點第 N 個</b>。
    /// </summary>
    private static bool TryFindRetainerListIndex(
        AtkUnitBase* addon, string name, out int index, out string seenNames)
    {
        index = -1;
        var seen = new List<string>();

        var values = addon->AtkValues;
        var count = addon->AtkValuesCount;

        for (var i = 0; i < RetainerListEntryCount; i++)
        {
            var nameIndex = RetainerListFirstValueIndex + (i * RetainerListValuesPerEntry) + RetainerListNameOffset;
            var activeIndex = RetainerListFirstValueIndex + (i * RetainerListValuesPerEntry) + RetainerListActiveOffset;
            if (values == null || activeIndex >= count) break;

            // 🔴 固定 10 格，沒用到的格子照樣存在——先看 IsActive，否則會選到空欄位。
            var active = values[activeIndex].Type == ValueType.Bool && values[activeIndex].Byte != 0;
            if (!active) continue;

            var entryName = ReadAtkString(values[nameIndex]);
            if (entryName.Length == 0) continue;

            seen.Add(entryName);
            if (index < 0 && entryName == name) index = i;
        }

        seenNames = seen.Count == 0 ? "（空）" : string.Join("、", seen);
        return index >= 0;
    }

    private static string ReadAtkString(AtkValue value)
    {
        if (value.Type != ValueType.String && value.Type != ValueType.ManagedString &&
            value.Type != ValueType.String8)
        {
            return string.Empty;
        }

        var pointer = value.String.Value;
        return pointer == null
            ? string.Empty
            : MemoryHelper.ReadSeStringNullTerminated((nint)pointer).TextValue;
    }

    private static string LookupRetainerName(ulong retainerId)
    {
        if (retainerId == 0) return string.Empty;

        var manager = RetainerManager.Instance();
        if (!manager->IsReady) return string.Empty;

        var retainers = manager->Retainers;
        for (var i = 0; i < retainers.Length; i++)
        {
            var retainer = retainers[i];
            if (retainer.RetainerId != retainerId) continue;
            return ReadRetainerName(ref retainer);
        }

        return string.Empty;
    }

    private void TryAutoDetectRenameDone()
    {
        if (workIndex < 0 || workIndex >= workList.Count) return;

        var work = workList[workIndex];
        var now = LookupRetainerName(work.RetainerId);
        if (now.Length == 0 || now == work.OldName) return;

        Svc.Log.Information($"[{InternalName}] 偵測到僱員名已從「{work.OldName}」變成「{now}」，自動進入下一步。");
        manualRenameConfirmed = true;
    }

    // ──────────────────────────── 錄製模式 ────────────────────────────

    /// <summary>
    /// 錄製模式：把畫面上與背包裡發生的事情印進記錄。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>只印字串與數值，不跨幀保存任何原生指標。</b>
    /// 🔴 hook <b>只在錄製期間 enable</b>——<c>AtkUnitBase::FireCallback</c> 是全遊戲每個視窗都會走的
    /// 熱路徑，常態掛著會對每一次 UI 互動收費。
    /// 📌 一律寫 <c>Information</c>：使用者跑 LogLevel 2，Debug／Verbose 收不到。
    /// <para>
    /// 🔑 <b>庫存變化是這裡最有價值的產物</b>：使用者手動卸一次裝，記錄裡就會出現
    /// 「<c>RetainerEquippedItems</c>#N 少了什麼、<c>Inventory</c>#M 多了什麼」，
    /// 那就是遊戲自己走的搬移路徑的直接證據——不必再猜 <c>MoveItemSlot</c> 對這個容器成不成立。
    /// </para>
    /// </remarks>
    private void StartRecording(WorkItem work)
    {
        if (!Config.RecordDuringManualRename && !recordingStandalone)
        {
            Svc.Log.Information($"[{InternalName}] 錄製已在設定裡關閉，只等待手動改名。");
            return;
        }

        StartRecordingCore($"「{work.OldName}」→ 建議候選「{currentCandidate}」");
    }

    private void StartRecordingCore(string context)
    {
        if (recordingActive) return;

        if (fireCallbackHook == null)
        {
            Svc.Log.Information(
                $"[{InternalName}] UI callback 錄製不可用（{recordingUnavailableReason}），" +
                "其餘錄製（addon 開關、選單文字、庫存變化）照常。");
        }
        else
        {
            fireCallbackHook.Enable();
        }

        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, OnAnyAddonSetup);
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, OnAnyAddonFinalize);

        inventorySnapshot.Clear();
        TakeInventorySnapshot(inventorySnapshot);

        recordingActive = true;
        Svc.Log.Information($"[{InternalName}] === 錄製開始：{context} ===");
    }

    private void StopRecording()
    {
        if (!recordingActive) return;

        try
        {
            fireCallbackHook?.Disable();
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, $"[{InternalName}] 停用 FireCallback hook 時發生例外");
        }

        Svc.AddonLifecycle.UnregisterListener(OnAnyAddonSetup);
        Svc.AddonLifecycle.UnregisterListener(OnAnyAddonFinalize);

        inventorySnapshot.Clear();
        recordingActive = false;

        Svc.Log.Information($"[{InternalName}] === 錄製結束 ===");
    }

    private static void TakeInventorySnapshot(Dictionary<(InventoryType Type, int Slot), SlotSnapshot> into)
    {
        var manager = InventoryManager.Instance();
        if (manager == null) return;

        foreach (var type in WatchedContainers)
        {
            var container = manager->GetInventoryContainer(type);
            if (container == null || !container->IsLoaded) continue;

            for (var i = 0; i < container->Size; i++)
            {
                var item = container->GetInventorySlot(i);
                if (item == null || item->ItemId == 0) continue;

                into[(type, i)] = new SlotSnapshot(
                    item->ItemId,
                    item->Quantity,
                    (item->Flags & InventoryItem.ItemFlags.HighQuality) != 0);
            }
        }
    }

    /// <summary>把庫存的變化印出來（只在錄製期間呼叫）。</summary>
    private void RecordInventoryChanges()
    {
        var current = new Dictionary<(InventoryType Type, int Slot), SlotSnapshot>();
        TakeInventorySnapshot(current);

        foreach (var pair in inventorySnapshot)
        {
            if (current.TryGetValue(pair.Key, out var now))
            {
                if (now == pair.Value) continue;
                Svc.Log.Information(
                    $"[{InternalName}] [錄製][庫存] {pair.Key.Type}#{pair.Key.Slot} 變更：" +
                    $"{DescribeSlot(pair.Value)} → {DescribeSlot(now)}");
            }
            else
            {
                Svc.Log.Information(
                    $"[{InternalName}] [錄製][庫存] {pair.Key.Type}#{pair.Key.Slot} 清空：{DescribeSlot(pair.Value)}");
            }
        }

        foreach (var pair in current)
        {
            if (inventorySnapshot.ContainsKey(pair.Key)) continue;
            Svc.Log.Information(
                $"[{InternalName}] [錄製][庫存] {pair.Key.Type}#{pair.Key.Slot} 新增：{DescribeSlot(pair.Value)}");
        }

        inventorySnapshot.Clear();
        foreach (var pair in current) inventorySnapshot[pair.Key] = pair.Value;
    }

    private static string DescribeSlot(SlotSnapshot slot) =>
        $"itemId={slot.ItemId}（{ItemNames.Get(slot.ItemId, slot.HighQuality)}）×{slot.Quantity}";

    private void OnAnyAddonSetup(AddonEvent type, AddonArgs args)
    {
        try
        {
            var addon = (AtkUnitBase*)args.Addon.Address;
            if (addon == null)
            {
                Svc.Log.Information($"[{InternalName}] [錄製] Setup {args.AddonName}（addon 位址為 0）");
                return;
            }

            Svc.Log.Information($"[{InternalName}] [錄製] Setup {args.AddonName} {DescribeValues(addon)}");

            if (args.AddonName == SelectStringAddon)
            {
                var entries = UiHelper.GetSelectStringEntries(addon);
                Svc.Log.Information(
                    $"[{InternalName}] [錄製] SelectString 選項（{entries.Count}）：{string.Join(" | ", entries)}");
            }
        }
        catch (Exception ex)
        {
            if (Throttle.Pass($"{InternalName}-RecordSetupError", 30_000))
                Svc.Log.Error(ex, $"[{InternalName}] 錄製 Setup 時發生例外");
        }
    }

    private void OnAnyAddonFinalize(AddonEvent type, AddonArgs args)
    {
        try
        {
            Svc.Log.Information($"[{InternalName}] [錄製] Finalize {args.AddonName}");
        }
        catch (Exception ex)
        {
            if (Throttle.Pass($"{InternalName}-RecordFinalizeError", 30_000))
                Svc.Log.Error(ex, $"[{InternalName}] 錄製 Finalize 時發生例外");
        }
    }

    /// <summary>
    /// <c>FireCallback</c> 的錄製 detour。
    /// 🔴 只讀值、只印字串，<b>不改參數、不保存指標</b>，任何例外都吞掉並照常呼叫原函式
    /// ——錄製壞掉不可以讓遊戲的 UI 壞掉。
    /// </summary>
    private bool FireCallbackDetour(AtkUnitBase* addon, uint valueCount, AtkValue* values, bool close)
    {
        try
        {
            if (addon != null)
            {
                Svc.Log.Information(
                    $"[{InternalName}] [錄製] FireCallback {addon->NameString} close={close} " +
                    DescribeValueArray(values, (int)valueCount));
            }
        }
        catch
        {
            // 錄製失敗一律靜默：這條是遊戲的熱路徑，不能因為記錄而中斷。
        }

        return fireCallbackHook!.Original(addon, valueCount, values, close);
    }

    private static string DescribeValues(AtkUnitBase* addon) =>
        DescribeValueArray(addon->AtkValues, addon->AtkValuesCount);

    private static string DescribeValueArray(AtkValue* values, int count)
    {
        if (values == null || count <= 0) return "values=[]";

        var limit = Math.Min(count, RecordMaxValues);
        var builder = new StringBuilder();
        builder.Append("values[").Append(count).Append("]=[");

        for (var i = 0; i < limit; i++)
        {
            if (i > 0) builder.Append(", ");
            builder.Append(i).Append(':');

            var value = values[i];
            switch (value.Type)
            {
                case ValueType.Bool:
                    builder.Append("bool=").Append(value.Byte != 0);
                    break;
                case ValueType.Int:
                    builder.Append("int=").Append(value.Int);
                    break;
                case ValueType.UInt:
                    builder.Append("uint=").Append(value.UInt);
                    break;
                case ValueType.Int64:
                    builder.Append("i64=").Append(value.Int64);
                    break;
                case ValueType.UInt64:
                    builder.Append("u64=").Append(value.UInt64);
                    break;
                case ValueType.Float:
                    builder.Append("float=").Append(value.Float);
                    break;
                case ValueType.String:
                case ValueType.String8:
                case ValueType.ManagedString:
                    var text = ReadAtkString(value);
                    if (text.Length > RecordMaxStringLength) text = text[..RecordMaxStringLength] + "…";
                    builder.Append("str=\"").Append(text).Append('"');
                    break;
                default:
                    builder.Append(value.Type);
                    break;
            }
        }

        if (limit < count) builder.Append(", …");
        builder.Append(']');
        return builder.ToString();
    }

    // ──────────────────────────── 聊天訊息 ────────────────────────────

    /// <summary>
    /// 盯幻想藥旗標與「名字被占用／不能用」的訊息。
    /// </summary>
    /// <remarks>
    /// 🔑 比對的字串<b>從 Excel 讀</b>，不是寫死在程式碼裡。
    /// ⚠️ 幻想藥那兩句只是<b>加速訊號</b>（真正的判準是背包數量），認不出來最多只是慢一點；
    /// 但「名字被占用」<b>沒有第二個訊號</b>，所以那裡刻意列了全部候選列號、任一命中就算，
    /// 而且 UI 上另外給了一顆手動按鈕當後備。
    /// 📌 錄製期間<b>命中的列號會一起印出來</b>，好讓第二版把錨收斂到真正那一筆。
    /// </remarks>
    private void OnChatMessage(
        XivChatType type, int timestamp, ref SeString sender, ref SeString message, ref bool isHandled)
    {
        try
        {
            if (!queue.IsBusy && !recordingActive) return;

            var text = message.TextValue;
            if (text.Length == 0) return;

            if (queue.IsBusy)
            {
                var granted = GetLogMessageText(LogMessageRightGranted);
                if (granted.Length > 0 && text.Contains(granted, StringComparison.Ordinal))
                {
                    sawRightGranted = true;
                    return;
                }

                var already = GetLogMessageText(LogMessageAlreadyHasRight);
                if (already.Length > 0 && text.Contains(already, StringComparison.Ordinal))
                {
                    sawAlreadyHasRight = true;
                    return;
                }
            }

            if (MatchesAny(text, AddonRowsNameTaken, LogMessageRowsNameTaken, out var takenSource))
            {
                if (recordingActive)
                    Svc.Log.Information($"[{InternalName}] [錄製][訊息] 名字已被使用（錨：{takenSource}）：{text}");
                sawNameTaken = true;
                return;
            }

            if (MatchesAny(text, AddonRowsNameRejected, LogMessageRowsNameRejected, out var rejectedSource))
            {
                if (recordingActive)
                    Svc.Log.Information($"[{InternalName}] [錄製][訊息] 名字不能使用（錨：{rejectedSource}）：{text}");
                sawNameRejected = true;
            }
        }
        catch (Exception ex)
        {
            if (Throttle.Pass($"{InternalName}-ChatError", 60_000))
                Svc.Log.Error(ex, $"[{InternalName}] 處理聊天訊息時發生例外");
        }
    }

    /// <summary>訊息是否命中任一候選列；<paramref name="source"/> 回報命中的是哪一張表的哪一列。</summary>
    private static bool MatchesAny(string text, uint[] addonRows, uint[] logMessageRows, out string source)
    {
        foreach (var rowId in addonRows)
        {
            var anchor = GetAddonText(rowId);
            if (anchor.Length > 0 && text.Contains(anchor, StringComparison.Ordinal))
            {
                source = $"Addon {rowId}";
                return true;
            }
        }

        foreach (var rowId in logMessageRows)
        {
            var anchor = GetLogMessageText(rowId);
            if (anchor.Length > 0 && text.Contains(anchor, StringComparison.Ordinal))
            {
                source = $"LogMessage {rowId}";
                return true;
            }
        }

        source = string.Empty;
        return false;
    }

    // ──────────────────────────── UI ────────────────────────────

    private static readonly Vector4 BadColor = new(1f, 0.45f, 0.4f, 1f);
    private static readonly Vector4 WarnColor = new(1f, 0.8f, 0.35f, 1f);
    private static readonly Vector4 GoodColor = new(0.5f, 0.9f, 0.55f, 1f);

    public override void DrawConfig()
    {
        // 🔴 這裡只讀 Framework tick 算好的快照，不碰原生記憶體 ——
        //    DrawConfig 沒有被呼叫端包 try，在這裡擲例外就是整個介面到重開遊戲前都不回來。
        DrawStatusLine();
        ImGui.Spacing();

        DrawRetainerTable();
        ImGui.Spacing();

        DrawNamePool();
        ImGui.Spacing();

        DrawBlockers();
        ImGui.Spacing();

        DrawControls();
        ImGui.Spacing();

        DrawOptions();
    }

    private void DrawStatusLine()
    {
        var fantasiaName = ItemNames.Get(FantasiaItemId);

        ImGui.AlignTextToFramePadding();
        if (fantasiaCount < 0)
            ImGui.TextDisabled($"{fantasiaName}：？");
        else
            ImGui.TextUnformatted($"{fantasiaName}：{fantasiaCount} 瓶");

        ImGui.SameLine();
        ImGui.TextUnformatted("　");
        ImGui.SameLine();

        if (emptyBagSlots < 0)
            ImGui.TextDisabled("背包空位：？");
        else
            ImGui.TextUnformatted($"背包空位：{emptyBagSlots} 格");

        ImGui.SameLine();
        ImGui.TextUnformatted("　");
        ImGui.SameLine();

        if (!bellLookupUsable)
            ImGui.TextDisabled("傳喚鈴：？");
        else if (bellReachable)
            ImGui.TextColored(GoodColor, "傳喚鈴：在旁邊");
        else
            ImGui.TextColored(BadColor, "傳喚鈴：不在旁邊");

        ImGui.SameLine();
        ImGui.TextUnformatted("　");
        ImGui.SameLine();
        ImGui.TextUnformatted($"可用候選：{AvailableCandidateCount()} 個");
    }

    private void DrawRetainerTable()
    {
        if (!snapshotValid)
        {
            ImGui.TextDisabled($"僱員清單：？（{snapshotProblem}）");
            return;
        }

        if (rows.Count == 0)
        {
            ImGui.TextDisabled("這個角色沒有僱員，或僱員資料還沒載入（請先開一次傳喚鈴）。");
            return;
        }

        using var table = ImRaii.Table("##retainer-batch-rename", 4,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp);
        if (!table) return;

        ImGui.TableSetupColumn("改", ImGuiTableColumnFlags.WidthFixed, 30f);
        ImGui.TableSetupColumn("現名", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn("預定新名", ImGuiTableColumnFlags.WidthStretch, 1.2f);
        ImGui.TableSetupColumn("狀態", ImGuiTableColumnFlags.WidthStretch, 1.3f);
        ImGui.TableHeadersRow();

        var busy = queue.IsBusy;

        // 預覽：勾選的僱員依序會拿到哪個候選（純顯示，不改動任何狀態）。
        var preview = new List<string>();
        foreach (var candidate in candidates)
        {
            if (candidate.State == RetainerNameCandidateState.Available) preview.Add(candidate.Name);
        }

        var previewIndex = 0;

        foreach (var row in rows)
        {
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            using (ImRaii.Disabled(busy))
            {
                var selected = row.Selected;
                if (ImGui.Checkbox($"##sel-{row.RetainerId}", ref selected))
                    row.Selected = selected;
            }

            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(row.CurrentName);

            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            if (!row.Selected)
            {
                ImGui.TextDisabled("—");
            }
            else if (previewIndex < preview.Count)
            {
                ImGui.TextUnformatted(preview[previewIndex]);
                previewIndex++;
            }
            else
            {
                ImGui.TextColored(BadColor, "候選不足");
                previewIndex++;
            }

            ImGui.TableNextColumn();
            DrawRowStatus(row);
        }
    }

    /// <summary>
    /// 狀態欄。
    /// 🔑 「隨時掃視」的放列上、「起疑才查」的放 tooltip；
    /// 但<b>「不知道」本身要在列上看得見</b>——裝備件數在傳喚之前是真的不知道，畫 <c>?</c> 不畫 0。
    /// </summary>
    private void DrawRowStatus(RetainerRow row)
    {
        ImGui.AlignTextToFramePadding();

        if (row.StrandedGear > 0)
        {
            ImGui.TextColored(BadColor, $"{row.StrandedGear} 件裝備在背包未穿回");
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    "上一輪流程中止時，這位僱員的裝備已經卸到你的背包裡，但還沒穿回去。\n" +
                    "本模組刻意不做自動回滾——請自己把它們放回僱員身上，\n" +
                    "或是再跑一次流程（穿回去那一步會重新執行）。");
            }

            return;
        }

        if (row.OnVenture)
        {
            ImGui.TextColored(WarnColor, "探險中（不能換裝）");
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    "遊戲訊息「僱員在探險的過程中無法更換裝備。」（LogMessage 3904）。\n" +
                    "改名前必須把裝備全部卸下，所以探險中的僱員無法處理。\n" +
                    "請先讓他回來（或收取探險成果）再試。");
            }

            return;
        }

        ImGui.TextDisabled("裝備：？");
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "僱員身上穿幾件，只有在他被傳喚出來的時候才讀得到\n" +
                "（RetainerEquippedItems 這個容器沒開僱員視窗時根本沒載入）。\n" +
                $"所以開始之前一律以最壞情況估算：背包至少要有 {RetainerEquipSlotCountEstimate} 格空位。");
        }
    }

    private void DrawNamePool()
    {
        if (!ImGui.CollapsingHeader($"候選名單池（{candidates.Count} 個）###retainer-name-pool")) return;

        ImGui.TextDisabled("一行一個名字。僱員名字是全世界唯一的，所以這裡準備一份備選，被占用時自動換下一個。");

        var poolText = Config.NamePoolText ?? string.Empty;
        if (ImGui.InputTextMultiline("##name-pool", ref poolText, 16_384,
                new Vector2(-1f, ImGui.GetTextLineHeight() * 8f)))
        {
            Config.NamePoolText = poolText;
            Plugin.Instance.Config.Save();
        }

        var path = GetNamePoolFilePath();

        if (ImGui.Button("從檔案載入##name-pool"))
            LoadNamePoolFromFile();

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"從這個檔案讀取（UTF-8，一行一名）：\n{path}");

        ImGui.SameLine();
        if (ImGui.Button("複製檔案路徑##name-pool"))
            ImGui.SetClipboardText(path);

        ImGui.SameLine();
        if (ImGui.Button("重設所有候選狀態##name-pool"))
        {
            Config.CandidateStatus.Clear();
            Plugin.Instance.Config.Save();
            parsedPoolText = "\uFFFF"; // 強制重新解析
            Svc.Log.Information($"[{InternalName}] 使用者重設了所有候選名字的狀態。");
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "把「已用」「已被占用」全部清掉，所有候選回到「未用」。\n" +
                "⚠ 這會讓流程重新嘗試已知被占用的名字。");
        }

        if (candidates.Count == 0)
        {
            ImGui.TextDisabled($"名單是空的。可以直接貼上，或把檔案放到：{path}");
            return;
        }

        using var table = ImRaii.Table("##name-pool-table", 3,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp |
            ImGuiTableFlags.ScrollY, new Vector2(-1f, ImGui.GetTextLineHeightWithSpacing() * 8f));
        if (!table) return;

        ImGui.TableSetupColumn("候選名字", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn("狀態", ImGuiTableColumnFlags.WidthStretch, 0.7f);
        ImGui.TableSetupColumn("說明", ImGuiTableColumnFlags.WidthStretch, 1.3f);
        ImGui.TableHeadersRow();

        foreach (var candidate in candidates)
        {
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(candidate.Name);

            ImGui.TableNextColumn();
            switch (candidate.State)
            {
                case RetainerNameCandidateState.Available:
                    ImGui.TextColored(GoodColor, "未用");
                    break;
                case RetainerNameCandidateState.Used:
                    ImGui.TextDisabled("已用");
                    break;
                case RetainerNameCandidateState.Taken:
                    ImGui.TextColored(BadColor, "已被占用");
                    break;
                default:
                    ImGui.TextColored(WarnColor, "剔除");
                    break;
            }

            ImGui.TableNextColumn();
            if (candidate.State == RetainerNameCandidateState.Used &&
                (candidate.UsedByCharacterName.Length > 0 || candidate.UsedByRetainerName.Length > 0))
            {
                var character = candidate.UsedByCharacterName.Length > 0 ? candidate.UsedByCharacterName : "？";
                var previous = candidate.UsedByRetainerName.Length > 0
                    ? $"（原名 {candidate.UsedByRetainerName}）"
                    : string.Empty;
                ImGui.TextDisabled($"已用於 角色{character}{previous}");
            }
            else
            {
                ImGui.TextDisabled(candidate.Note.Length > 0 ? candidate.Note : "—");
            }
        }
    }

    private static string GetNamePoolFilePath()
    {
        try
        {
            return Path.Combine(Svc.PluginInterface.GetPluginConfigDirectory(), NamePoolFileName);
        }
        catch
        {
            return NamePoolFileName;
        }
    }

    private void LoadNamePoolFromFile()
    {
        var path = GetNamePoolFilePath();

        try
        {
            if (!File.Exists(path))
            {
                Svc.Log.Information($"[{InternalName}] 候選名單檔不存在：{path}");
                Svc.Chat.PrintError($"[TC Toolbox] 找不到名單檔，請把它放到：{path}");
                return;
            }

            // 📌 UTF-8；帶不帶 BOM 都讀得對（UTF8Encoding 預設會吃掉開頭的 BOM）。
            var names = SplitLines(File.ReadAllText(path, Encoding.UTF8));
            if (names.Count == 0)
            {
                Svc.Log.Information($"[{InternalName}] 名單檔讀到 0 筆：{path}");
                Svc.Chat.PrintError("[TC Toolbox] 名單檔裡沒有任何名字。");
                return;
            }

            // 🔴 追加不取代，理由同 AppendNamesToPool。
            var added = AppendNamesToPool(names);
            Svc.Log.Information($"[{InternalName}] 從 {path} 讀到 {names.Count} 筆，新增 {added} 筆。");
            Svc.Chat.Print($"[TC Toolbox] 名單檔共 {names.Count} 筆，新增 {added} 筆。");
        }
        catch (Exception ex)
        {
            Svc.Log.Information($"[{InternalName}] 讀取候選名單檔失敗（{path}）：{ex.Message}");
            Svc.Chat.PrintError($"[TC Toolbox] 讀取名單檔失敗：{ex.Message}");
        }
    }

    private void DrawBlockers()
    {
        if (queue.IsBusy) return;

        var blockers = BuildBlockers();
        if (blockers.Count == 0)
        {
            ImGui.TextColored(GoodColor, "前置檢查全部通過。");
            return;
        }

        foreach (var blocker in blockers)
            ImGui.TextColored(BadColor, $"● {blocker}");
    }

    private void DrawControls()
    {
        var busy = queue.IsBusy;

        using (ImRaii.Disabled(busy))
        {
            if (ImGui.Button("開始##retainer-batch-rename"))
                Start();
        }

        ImGui.SameLine();
        using (ImRaii.Disabled(!busy))
        {
            if (ImGui.Button("停止##retainer-batch-rename"))
            {
                Svc.Log.Information($"[{InternalName}] 使用者按下停止。");
                StopRun("使用者按下停止");
                Svc.Chat.Print("[TC Toolbox] 批次僱員改名已停止。");
            }
        }

        // 🔑 獨立的錄製開關：使用者可以完全不跑流程，自己手動走一遍全程（用幻想藥→鈴→傳喚→
        //    卸裝→改名→穿回→讓僱員返回），把整條路徑錄下來。
        //    第一版的自動化有任何一段不成立時，這就是備援，也是第二版的資料來源。
        ImGui.SameLine();
        if (!recordingActive)
        {
            using (ImRaii.Disabled(busy))
            {
                if (ImGui.Button("開始錄製（我自己操作）##retainer-batch-rename"))
                {
                    recordingStandalone = true;
                    StartRecordingCore("使用者手動操作全程");
                    Svc.Chat.Print("[TC Toolbox] 已開始錄製，請照平常的方式操作一遍，完成後按「停止錄製」。");
                }
            }
        }
        else if (recordingStandalone)
        {
            if (ImGui.Button("停止錄製##retainer-batch-rename"))
            {
                recordingStandalone = false;
                StopRecording();
                Svc.Chat.Print("[TC Toolbox] 已停止錄製。");
            }
        }
        else
        {
            ImGui.TextDisabled("錄製中（流程）");
        }

        if (recordingActive && recordingStandalone)
        {
            ImGui.TextColored(WarnColor,
                "錄製中：請自己完整操作一次（用幻想藥 → 開鈴 → 傳喚 → 卸裝 → 改名 → 穿回 → 讓僱員返回）。");
            ImGui.TextDisabled("addon 開關、選單文字、UI callback、以及每一格背包／僱員裝備欄的變化都會寫進記錄。");
        }

        if (busy)
        {
            ImGui.TextColored(WarnColor, $"進行中：{queue.CurrentStep ?? "（無）"}");

            if (waitingForManualRename && workIndex >= 0 && workIndex < workList.Count)
                DrawManualRenamePanel(workList[workIndex]);
        }
        else if (lastSummary.Length > 0)
        {
            ImGui.TextDisabled(lastSummary);
        }
    }

    private void DrawManualRenamePanel(WorkItem work)
    {
        ImGui.Separator();
        ImGui.TextColored(WarnColor,
            $"請在遊戲裡手動把「{work.OldName}」改名（僱員選單 →「重新設定容貌」→ 改名 → 確認）。");

        ImGui.AlignTextToFramePadding();
        if (currentCandidate.Length > 0)
        {
            ImGui.TextUnformatted("這隻建議用：");
            ImGui.SameLine();
            ImGui.TextColored(GoodColor, currentCandidate);

            ImGui.SameLine();
            if (ImGui.Button("複製到剪貼簿##manual-rename"))
            {
                ImGui.SetClipboardText(currentCandidate);
                Svc.Chat.Print($"[TC Toolbox] 已複製「{currentCandidate}」到剪貼簿。");
            }
        }
        else
        {
            ImGui.TextColored(BadColor, $"沒有可用的候選名字了。{candidateExhaustedReason}");
        }

        ImGui.TextDisabled(
            $"（這位僱員已經試到第 {candidateAttempts} 個候選，上限 {Config.MaxCandidateAttemptsPerRetainer}）");

        if (ImGui.Button("改名完成##manual-rename"))
        {
            Svc.Log.Information($"[{InternalName}] 使用者按下「改名完成」。");
            manualRenameConfirmed = true;
        }

        ImGui.SameLine();
        using (ImRaii.Disabled(currentCandidate.Length == 0))
        {
            if (ImGui.Button("這個名字被占用了##manual-rename"))
            {
                Svc.Log.Information($"[{InternalName}] 使用者回報「{currentCandidate}」已被占用。");
                MarkCurrentCandidate(RetainerNameCandidateState.Taken, "使用者回報已被占用");
            }
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "把目前這個候選標成「已被占用」並換下一個。\n" +
                "這個狀態會存進設定，之後不會再拿它去試。\n" +
                "\n" +
                "📌 如果遊戲有印出「該名稱已被使用…」之類的訊息，本模組多半會自己認出來、\n" +
                "　 不必按這顆；認不出來的時候才需要你手動按。");
        }

        ImGui.TextDisabled("裝備已經卸下來放在你的背包裡，改完之後會自動穿回去。");
    }

    private void DrawOptions()
    {
        var record = Config.RecordDuringManualRename;
        if (ImGui.Checkbox("手動改名期間自動錄製", ref record))
        {
            Config.RecordDuringManualRename = record;
            Plugin.Instance.Config.Save();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "預設開啟。改名畫面（CharaMake）的僱員模式在台服完全沒有可離線查證的資料，\n" +
                "所以這一版不自動操作它——改成在你手動改名的時候，把 addon 開關、選單文字、\n" +
                "每一次 UI callback、以及背包／僱員裝備欄的每一格變化寫進 Dalamud 記錄\n" +
                "（Information 等級，你的 LogLevel 2 收得到）。\n" +
                "\n" +
                "這些資料是之後要不要（以及能不能）把改名也自動化的唯一依據。\n" +
                "關掉的話流程一樣可以跑，只是不會留下資料。" +
                (recordingUnavailableReason.Length > 0
                    ? $"\n\n⚠ 這一版 UI callback 錄製不可用：{recordingUnavailableReason}"
                    : string.Empty));
        }

        var autoDetect = Config.AutoDetectRenameDone;
        if (ImGui.Checkbox("偵測到僱員名已改變就自動進入下一步", ref autoDetect))
        {
            Config.AutoDetectRenameDone = autoDetect;
            Plugin.Instance.Config.Save();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "預設開啟：改完名之後不必按「改名完成」，本模組發現名字變了就會自己往下走。\n" +
                "關掉的話一律等你按按鈕（按鈕永遠都在）。");
        }

        ImGui.SetNextItemWidth(180f);
        var attempts = Config.MaxCandidateAttemptsPerRetainer;
        if (ImGui.SliderInt("同一位僱員最多試幾個候選", ref attempts, 1, 20))
        {
            Config.MaxCandidateAttemptsPerRetainer = attempts;
            Plugin.Instance.Config.Save();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "名字被占用時會自動換下一個候選。連續試到這個次數還是不行就停下來，\n" +
                "免得一路把整份名單燒光。預設 5。");
        }

        ImGui.SetNextItemWidth(180f);
        var delay = Config.UndressVerifyDelayMs;
        if (ImGui.SliderInt("卸裝後等待伺服器確認（毫秒）", ref delay, 500, 20_000))
        {
            Config.UndressVerifyDelayMs = delay;
            Plugin.Instance.Config.Save();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "搬移道具的函式會「同步」更新本機的容器內容，所以呼叫完立刻去看一定是成功的；\n" +
                "伺服器若不接受，要幾秒之後才會把東西退回來。\n" +
                "這段等待就是為了看清楚裝備到底有沒有真的離開僱員身上——\n" +
                "還在身上就代表卸裝沒生效，這時候繼續改名只會失敗。");
        }

        ImGui.Spacing();
        ImGui.TextColored(WarnColor,
            "⚠ 改名這一步這一版不自動執行，需要你自己在遊戲裡操作。\n" +
            "　 幻想藥的「重新設定容貌權利」一次只能掛一份，所以是一隻一隻輪流做的。\n" +
            "　 流程中途停止時已卸下的裝備會留在你的背包裡，不會自動放回去。\n" +
            "　 自動卸裝走的路徑沒有實機證據——不確定的話請先用「開始錄製（我自己操作）」手動走一遍。");
    }
}

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
using AddonRetainerTaskAsk = FFXIVClientStructs.FFXIV.Client.UI.AddonRetainerTaskAsk;
using AddonRetainerTaskResult = FFXIVClientStructs.FFXIV.Client.UI.AddonRetainerTaskResult;
using CSFramework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;

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
        "期間會把畫面與背包變化寫進記錄供之後開發。幻想藥要自備，預設關、按「開始」才動、隨時可停。" +
        "另外附一顆獨立的「收回所有已完成探險（不重派）」：把探險結束的僱員逐一收成果並讓他閒置，" +
        "不重新派遣（探險中不能換裝，所以改名之前得先讓僱員閒下來）。";

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

    /// <summary>
    /// <c>Addon</c>「查看僱員探險情況　[結束]」——<b>探險已經結束、成果可以收回</b>的那一項。
    /// </summary>
    /// <remarks>
    /// 📌 AutoRetainer <c>RetainerHandlers.SelectViewVentureReport()</c> 讀的也是這一列。
    /// ⚠️ 同一個選單位置在不同狀態下是<b>不同的資料列</b>：
    /// <see cref="AddonRowVentureReportInProgress"/>（探險中，帶結束時間）、
    /// <see cref="AddonRowVentureReportHeld"/>（結束保留中）。三列共用前綴，
    /// 所以比對一律用<b>整列文字</b>而不是前綴——見 <see cref="GetVentureEntryPrefix"/> 的說明。
    /// </remarks>
    private const uint AddonRowVentureReportComplete = 2385;

    /// <summary>
    /// <c>Addon</c>「查看僱員探險情況　[結束保留中]」。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>本模組刻意不點這一項。</b>「保留中」到底是什麼狀態，台服 7.20 的 EXD 裡
    /// 除了這一列本身之外<b>沒有任何其他文字說得出來</b>（<c>LogMessage</c> 搜「探險」＋「保留」
    /// 零命中），AutoRetainer 也完全沒有處理它（全 repo 只用 2385）。
    /// ⇒ 命中這一項時的處置是<b>跳過並印一行看得見的記錄</b>，不是猜著點下去。
    /// </remarks>
    private const uint AddonRowVentureReportHeld = 2403;

    /// <summary>
    /// <c>Addon</c>「查看僱員探險情況　[～2013/8/27 8:00]」——探險<b>還沒結束</b>。
    /// </summary>
    /// <remarks>
    /// ⚠️ 這一列的文字裡帶著一個<b>範例日期</b>，執行期的實際文字不會跟它逐字相同。
    /// 所以它只拿來算「共同前綴」（<see cref="GetVentureEntryPrefix"/>），不拿來逐字比對。
    /// </remarks>
    private const uint AddonRowVentureReportInProgress = 2384;

    private const string RetainerTaskResultAddon = "RetainerTaskResult";

    private const string RetainerTaskAskAddon = "RetainerTaskAsk";

    /// <summary>快速探險收取偶爾會彈的確認框。台服 addon 名沿用既有慣例（小寫 n）。</summary>
    private const string SelectYesnoAddon = "SelectYesno";

    /// <summary><c>EObjName</c>「傳喚鈴」。AutoRetainer <c>Lang.BellName</c> 讀的也是這一列。</summary>
    private const uint EObjNameRowSummoningBell = 2000401;

    /// <summary>日文客戶端的傳喚鈴名（AutoRetainer 的第二候選，照抄；台服用不到但留著無害）。</summary>
    private const string SummoningBellNameJp = "リテイナーベル";

    private const string RetainerListAddon = "RetainerList";
    private const string SelectStringAddon = "SelectString";

    /// <summary><c>InputString</c> addon——改名輸入框（台服 7.20 實機錄製的 addon 名）。</summary>
    private const string InputStringAddon = "InputString";

    private const string TalkAddon = "Talk";

    // ── CharaMake 改名：形象相關確認框（台服 EXD Lobby 表確認，改版換字串仍能跟著走）──

    /// <summary>🔴 <c>Lobby</c>「要使用已儲存的角色形象嗎？」——保留容貌的安全閘門，按「是」。</summary>
    private const uint LobbyRowUseSavedAppearance = 2044;

    /// <summary><c>Lobby</c>「要儲存目前的形象嗎？」——按「否」（不另存範本）。</summary>
    private const uint LobbyRowSaveAppearance = 2176;

    /// <summary><c>Lobby</c>「確定要將僱員的形象設定成目前的樣子嗎？」——按「是」（此時預覽＝保留的原容貌）。</summary>
    private const uint LobbyRowSetAppearanceNow = 621;

    // ── CharaMake 改名：EXD 查不到、只能寫死的台服 7.20 實機字串 ──
    // 🔴 這些字串在台服 EXD dump 裡不存在（管理人選單／性格／名字確認都是執行期組出來的），
    //    只能照實機錄製寫死。改版換字串的失敗形式是「比對不到→不動作→逾時跳過」（fail-closed），
    //    不會誤按，更不會進到空白捏臉畫面。

    /// <summary>管理人選單「想改變僱員的樣貌、性格、名字」——用「樣貌」判別（該選單只有這一項含此詞）。</summary>
    private const string VocateChangeAppearanceMarker = "樣貌";

    /// <summary>性格選單第一項「開朗」。</summary>
    private const string PersonalityFirstMarker = "開朗";

    /// <summary>「確定要更改僱員X的設定嗎？」的兩個固定片段（X 是舊名或新名，不比對 X）。</summary>
    private const string RenameSettingConfirmMarker1 = "更改僱員";

    /// <inheritdoc cref="RenameSettingConfirmMarker1"/>
    private const string RenameSettingConfirmMarker2 = "設定";

    /// <summary>「確認好僱員的性格了嗎？」——SelectYesno 出現「性格」即此步，按「是」。</summary>
    private const string PersonalityConfirmMarker = "性格";

    /// <summary>管理人（僱員窗口）NPC 的參考列——讀第一個非空的 <c>Title</c> 當比對基準。</summary>
    /// <remarks>這些列在台服 <c>ENpcResident</c> 的 <c>Title</c> 都是「僱員窗口」（2026-08-23 EXD dump 實查）。</remarks>
    private static readonly uint[] VocateNpcTitleRows = [1000233, 1001963, 1003275, 1011198, 1018983];

    /// <summary>與管理人 NPC 的互動距離（NPC 比鈴寬鬆一點）。</summary>
    private const float VocateInteractDistance = 6f;

    /// <summary>自動改名整段的內部期限（毫秒）：到了就放棄這位、去救裝備、換下一位（不讓 TaskQueue 逾時停整批）。</summary>
    private const int AutoRenameInternalDeadlineMs = 90_000;

    /// <summary>還沒看到任何改名視窗、卻已找不到管理人這麼久（毫秒）就早退（多半是沒站在管理人旁邊）。</summary>
    private const int AutoRenameVocateSearchMs = 20_000;

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

        /// <summary>目前派出去的探險編號；<c>0</c>＝沒有派探險。</summary>
        public ushort VentureId;

        /// <summary>
        /// 探險完成時刻（UNIX 秒）。<c>0</c>＝<b>讀不到</b>（不是「現在就完成」）。
        /// </summary>
        /// <remarks>
        /// 🔴 <c>0</c> 與「已完成」必須分得開：<c>VentureId != 0</c> 但這裡是 <c>0</c> 時，
        /// 本模組一律當成 <see cref="VentureState.Unknown"/> 而<b>不去收</b>，
        /// 列上也畫「？」不畫「已完成」。把不知道畫成 0 會直接誤導使用者。
        /// </remarks>
        public uint VentureCompleteUnix;

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

    /// <summary>「收回已完成探險」這一輪要處理的僱員。</summary>
    private readonly List<WorkItem> collectList = [];

    private int workIndex = -1;
    private int renamedCount;
    private string lastSummary = string.Empty;

    // 收回探險
    private RunMode runMode = RunMode.None;
    private int collectIndex = -1;
    private int collectedCount;
    private int collectSkippedCount;
    private int collectHeldCount;

    /// <summary>收回探險時「連續」逾時的僱員數；累到 <see cref="MaxCollectConsecutiveTimeouts"/> 就判 UI 壞了、整批停下。</summary>
    private int collectConsecutiveTimeouts;

    /// <summary>收回探險連續逾時到這個數就停整批（單一隻逾時只跳過，不停）。</summary>
    private const int MaxCollectConsecutiveTimeouts = 3;

    /// <summary>快照時的伺服器時間（UNIX 秒）。<c>0</c>＝還沒讀到。</summary>
    private long serverNowUnix;

    // AutoRetainer 狀態（每 2 秒探一次；跨外掛 IPC 不該每幀做）
    private bool arProbed;
    private bool arAvailable;
    private bool arBusy;

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
            // 🔴 收回探險是逐隻獨立的：一隻卡住不該讓其餘收不到。CollectVentures 模式下，
            //    單步逾時＝記錄這一位（含最後是不是被重新派遣）＋跳下一位，不停整批；
            //    只有連續逾時累積到上限才判定 UI 真的壞了、整批停下。
            if (runMode == RunMode.CollectVentures)
            {
                HandleCollectTimeout(step);
                return;
            }

            Svc.Log.Information(
                $"[{InternalName}] 流程在「{step}」逾時中止（{ProgressText()}）。" +
                $"目前卸下未穿回：{stashed.Count} 件。");
            Svc.Chat.PrintError($"[TC Toolbox] {RunLabel()}在「{step}」逾時，已停止。");
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
        YesAlreadyIpc.Restore();
        Svc.Framework.Update -= OnUpdate;
        Svc.Chat.ChatMessage -= OnChatMessage;

        recordingStandalone = false;
        StopRecording();

        fireCallbackHook?.Dispose();
        fireCallbackHook = null;

        queue.Abort();
        workList.Clear();
        collectList.Clear();
        stashed.Clear();
        rows.Clear();
        candidates.Clear();
        inventorySnapshot.Clear();
        parsedPoolText = "\uFFFF";
        workIndex = -1;
        renamedCount = 0;
        runMode = RunMode.None;
        collectIndex = -1;
        collectedCount = 0;
        collectSkippedCount = 0;
        collectHeldCount = 0;
        collectConsecutiveTimeouts = 0;
        serverNowUnix = 0;
        arProbed = false;
        arAvailable = false;
        arBusy = false;
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
            row.VentureId = retainer.VentureId;
            row.VentureCompleteUnix = retainer.VentureComplete;
            row.DisplayOrder = manager->DisplayOrder.IndexOf((byte)i);
        }

        rows.RemoveAll(r => !seen.Contains(r.RetainerId));
        rows.Sort((a, b) => a.DisplayOrder.CompareTo(b.DisplayOrder));

        var inventory = InventoryManager.Instance();
        fantasiaCount = inventory == null ? -1 : inventory->GetInventoryItemCount(FantasiaItemId);
        emptyBagSlots = inventory == null ? -1 : (int)inventory->GetEmptySlotsInBag();

        serverNowUnix = CurrentServerTime();
        RefreshAutoRetainerState();
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

    private static string GetLobbyText(uint rowId) =>
        Svc.Data.GetExcelSheet<Lobby>().GetRowOrDefault(rowId)?.Text.ExtractText() ?? string.Empty;

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

        // 🔴 探險中不能換裝（LogMessage 3904），所以探險中的僱員照舊擋下來。
        //    <b>唯一放行的新路徑</b>是「探險已經完成」＋使用者開著「改名前先收回」——
        //    那時候流程會先把成果收回來（不重新派遣）再卸裝。
        var willCollect = 0;
        foreach (var row in selected)
        {
            if (!row.OnVenture) continue;

            var state = ClassifyVenture(row.VentureId, row.VentureCompleteUnix, serverNowUnix);

            if (state == VentureState.Complete)
            {
                if (Config.CollectCompletedVentureBeforeRename)
                {
                    willCollect++;
                    continue;
                }

                blockers.Add(
                    $"「{row.CurrentName}」的探險已完成但還沒收回。請先按「收回所有已完成探險（不重派）」，" +
                    "或把下面的「改名前先收回已完成的探險」打開。");
                continue;
            }

            if (state == VentureState.Running)
            {
                blockers.Add(
                    $"「{row.CurrentName}」正在探險中（剩 {FormatRemaining((long)row.VentureCompleteUnix - serverNowUnix)}），" +
                    "探險中無法更換裝備。");
                continue;
            }

            // ⚠️ 讀不到完成時刻：把「不知道」說出來，不要假裝知道。
            blockers.Add($"「{row.CurrentName}」正在探險中（剩餘時間讀不到），探險中無法更換裝備。");
        }

        // 🔴 只有「這一輪真的會去收探險」時才擋 AutoRetainer。
        //    無條件加這道閘門等於對既有的改名流程新增一個以前沒有的阻擋條件（＝回退既有行為）。
        if (willCollect > 0 && arProbed && arAvailable && arBusy)
        {
            blockers.Add(
                $"這一輪要先收回 {willCollect} 位僱員已完成的探險，但 AutoRetainer 正在忙——" +
                "它會把剛收回的僱員立刻重新派遣。請先停止 AutoRetainer（多角模式／自動收派）。");
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

        runMode = RunMode.Rename;
        workIndex = -1;
        renamedCount = 0;
        collectedCount = 0;
        collectSkippedCount = 0;
        collectHeldCount = 0;
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
        EnqueueOpenRetainer(work);

        // ── b2. 探險已完成的話先收回（不重新派遣）──
        // 🔴 收不回就卸不了裝（LogMessage 3904「僱員在探險的過程中無法更換裝備。」）。
        //    所以這裡不只是「順手收一下」——後面那道硬閘門存在的理由是：
        //    收回失敗若讓它繼續往下跑，錯誤會以「卸裝沒生效」的樣貌出現，
        //    把人帶去查 MoveItemSlot，而真正的原因在這裡。
        if (Config.CollectCompletedVentureBeforeRename)
        {
            EnqueueCollectVenture(work.RetainerId, work.OldName);

            queue.Enqueue($"確認「{work.OldName}」不在探險中", () =>
            {
                if (!TryLookupVenture(work.RetainerId, out var state, out var remaining))
                    return AbortWith($"讀不到「{work.OldName}」的僱員資料，無法確認探險狀態。");

                if (state == VentureState.Idle) return true;

                if (state == VentureState.Complete)
                {
                    return AbortWith(
                        $"「{work.OldName}」的探險成果沒有收回成功（僱員資料仍顯示探險中），無法卸裝。" +
                        "請自己開僱員選單看一下探險那一項現在是什麼狀態。");
                }

                if (state == VentureState.Running)
                {
                    return AbortWith(
                        $"「{work.OldName}」仍在探險中（剩 {FormatRemaining(remaining)}），無法卸裝。" +
                        "若剛剛才收回過，很可能是 AutoRetainer 立刻把他重新派遣了——請先停掉 AutoRetainer。");
                }

                return AbortWith($"「{work.OldName}」有派探險但讀不到完成時刻，保守起見不繼續。");
            });
        }

        // 🔴 等容器<b>排在收回探險之後</b>，順序不可對調。
        //    收回探險只需要僱員選單，卸裝才需要 RetainerEquippedItems；
        //    「僱員在探險中時這個容器會不會載入」我們沒有實機證據。
        //    若把等容器排在前面，而那個假設剛好不成立，流程就會卡在等容器、
        //    永遠走不到那個唯一能解開它的步驟——自己把自己鎖死，而且逾時訊息會指向錯的地方。
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
                // 🔴 改名只需脫「防具與飾品」（槽 2~12）。主手(0)／副手(1)／靈魂水晶(13) 不用卸——
                //    LM 405 原文只要求脫防具和飾品，而且對主手槽呼叫 MoveItemSlot 會回傳 10 失敗
                //    （2026-08-23 實機：卸「卡扎納爾之書」失敗導致整段中止、對話沒推進）。
                if (i < 2 || i > 12) continue;

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

            // 只檢查我們卸下的那些槽（防具＋飾品）；武器與靈魂水晶本來就留著，不算殘留。
            var remaining = 0;
            foreach (var g in stashed)
            {
                var item = container->GetInventorySlot(g.Slot);
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

        // ── d. 改名：自動 CharaMake（預設）或退回錄製＋等手動 ──
        if (Config.AutoCharaMakeRename)
        {
            EnqueueAssignCandidateAuto(work);
            EnqueueQuitRetainer();
            EnqueueLeaveBell(work);
            EnqueueAutoCharaMakeRename(work);
            EnqueueReturnToNeutral(work);
            EnqueueReopenRetainerForRedress(work);
        }
        else
        {
            EnqueueManualRenameFlow(work);
        }

        // ── e. 穿回去 ──
        EnqueueRedress(work);

        // ── f. 讓僱員返回 ──
        EnqueueQuitRetainer();

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

    // ──────────────────────────── 收回探險（不重派） ────────────────────────────

    /// <summary>
    /// 目前這條佇列在做哪一件事。
    /// </summary>
    /// <remarks>
    /// 🔑 兩個流程<b>共用同一個 <see cref="TaskQueue"/> 實例</b>，所以「同時只能跑一個」是免費得到的
    /// （兩顆按鈕都用 <c>queue.IsBusy</c> 擋）。這個列舉只是讓收尾訊息說得出剛才在做什麼。
    /// ⚠️ <c>None = 0</c> 是刻意的：沒有零值的列舉會讓 <c>default</c> 落在某個有效值上。
    /// </remarks>
    private enum RunMode
    {
        None = 0,

        /// <summary>批次改名。</summary>
        Rename = 1,

        /// <summary>只收回已完成的探險，不重新派遣。</summary>
        CollectVentures = 2,
    }

    /// <summary>一位僱員的探險狀態。</summary>
    private enum VentureState
    {
        /// <summary>沒有派探險（<c>VentureId == 0</c>）。這是可以直接換裝的狀態。</summary>
        Idle = 0,

        /// <summary>探險中，還沒到完成時刻。</summary>
        Running = 1,

        /// <summary>探險已完成，成果可以收回。</summary>
        Complete = 2,

        /// <summary>
        /// 有派探險，但完成時刻讀不到（<c>VentureComplete == 0</c>）。
        /// 🔴 <b>不當成可收回</b>——保守的方向是「什麼都不做」。
        /// </summary>
        Unknown = 3,
    }

    /// <summary>
    /// 取伺服器時間（UNIX 秒）。
    /// </summary>
    /// <remarks>
    /// 📌 <c>Framework.GetServerTime()</c> 是<b>靜態</b> <c>[MemberFunction]</c>（不吃 <c>this</c>），
    /// 所以<b>不需要</b> <c>Framework.Instance()</c>——那一支才是 <c>isPointer: true</c>、會合法回 null。
    /// 特徵碼解析失敗時是擲 <c>InvalidOperationException</c>，不是回 0，所以這裡包 try。
    /// <para>
    /// 🔑 用伺服器時間而不是本機時鐘：<c>VentureComplete</c> 是伺服器寫進來的 UNIX 秒，
    /// 拿本機時鐘去比，使用者的時鐘偏差多少就誤判多少。AutoRetainer 走的也是同一條
    /// （<c>Utils.GetVentureSecondsRemaining</c> 拿 <c>CSFramework.GetServerTime()</c> 相減）。
    /// ⚠️ 退回本機 UTC 的路徑<b>不會靜默</b>：每分鐘最多印一行 Information 說明退回了。
    /// </para>
    /// </remarks>
    private long CurrentServerTime()
    {
        try
        {
            var serverTime = CSFramework.GetServerTime();
            if (serverTime > 0) return serverTime;
        }
        catch (Exception ex)
        {
            if (Throttle.Pass($"{InternalName}-ServerTime", 60_000))
                Svc.Log.Information($"[{InternalName}] 取不到伺服器時間，改用本機 UTC：{ex.Message}");
        }

        return DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    /// <summary>把 <c>VentureId</c> ＋ <c>VentureComplete</c> 兩個原始欄位翻成一個狀態。</summary>
    private static VentureState ClassifyVenture(ushort ventureId, uint ventureComplete, long nowUnix)
    {
        if (ventureId == 0) return VentureState.Idle;
        if (ventureComplete == 0) return VentureState.Unknown;
        return ventureComplete <= nowUnix ? VentureState.Complete : VentureState.Running;
    }

    /// <summary>
    /// 用 <c>RetainerId</c> 重新查一位僱員的探險狀態。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>只存 id、每次重掃</b>，不跨幀保存任何原生指標。
    /// <c>RetainerManager.Instance()</c> 是無 <c>isPointer</c> 的 <c>[StaticAddress]</c>，
    /// 永遠不回 null（詳見 <see cref="RefreshSnapshot"/> 的說明），所以這裡不寫判空。
    /// </remarks>
    /// <returns>找得到這位僱員就回 <see langword="true"/>；找不到（換角色／資料未就緒）回 <see langword="false"/>。</returns>
    private bool TryLookupVenture(ulong retainerId, out VentureState state, out long secondsRemaining)
    {
        state = VentureState.Idle;
        secondsRemaining = 0;

        if (retainerId == 0) return false;

        var manager = RetainerManager.Instance();
        if (!manager->IsReady) return false;

        var now = CurrentServerTime();
        var retainers = manager->Retainers;
        for (var i = 0; i < retainers.Length; i++)
        {
            var retainer = retainers[i];
            if (retainer.RetainerId != retainerId) continue;

            state = ClassifyVenture(retainer.VentureId, retainer.VentureComplete, now);
            secondsRemaining = retainer.VentureComplete == 0 ? 0 : (long)retainer.VentureComplete - now;
            return true;
        }

        return false;
    }

    /// <summary>把剩餘秒數寫成人看的字串。</summary>
    private static string FormatRemaining(long seconds)
    {
        if (seconds <= 0) return "已完成";

        var span = TimeSpan.FromSeconds(seconds);
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours} 小時 {span.Minutes} 分";
        return span.TotalMinutes >= 1 ? $"{span.Minutes} 分 {span.Seconds} 秒" : $"{span.Seconds} 秒";
    }

    /// <summary>數一數目前的探險狀態分佈（只讀 <see cref="rows"/> 快照，不碰原生記憶體）。</summary>
    private int CountVentureStates(out int running, out long nearestRemaining, out int unknown)
    {
        var collectable = 0;
        running = 0;
        unknown = 0;

        var nearest = long.MaxValue;
        nearestRemaining = 0;

        // 🔴 這一支會從 Draw 路徑被呼叫，所以<b>只讀 Framework tick 算好的快照</b>，
        //    絕不在這裡呼叫 CurrentServerTime()（那是原生 MemberFunction）。
        //    還沒有快照就回 0——呼叫端負責把「不知道」畫成「？」而不是 0。
        if (serverNowUnix == 0) return 0;

        var now = serverNowUnix;

        foreach (var row in rows)
        {
            switch (ClassifyVenture(row.VentureId, row.VentureCompleteUnix, now))
            {
                case VentureState.Complete:
                    collectable++;
                    break;
                case VentureState.Running:
                    running++;
                    var remaining = (long)row.VentureCompleteUnix - now;
                    if (remaining < nearest) nearest = remaining;
                    break;
                case VentureState.Unknown:
                    unknown++;
                    break;
            }
        }

        nearestRemaining = nearest == long.MaxValue ? 0 : nearest;
        return collectable;
    }

    /// <summary>
    /// 「查看僱員探險情況」那三列的共同前綴。
    /// </summary>
    /// <remarks>
    /// 🔑 <b>從資料表算出來，不寫死中文。</b>2384（探險中，帶範例日期）與 2385（結束）在
    /// 「[」之前是同一段文字，取兩者的最長共同前綴就得到「這一列在講探險報告」的判別式。
    /// 這樣改版換字串也不會壞，而且不必把中文字串釘進程式碼。
    /// <para>⚠️ 只拿它判「有沒有探險報告那一項」；<b>要點下去的那一項一律用整列文字比對</b>，
    /// 否則會分不出「結束」「結束保留中」「還在跑」。</para>
    /// </remarks>
    private static string GetVentureEntryPrefix()
    {
        var complete = GetAddonText(AddonRowVentureReportComplete);
        var inProgress = GetAddonText(AddonRowVentureReportInProgress);
        if (complete.Length == 0 || inProgress.Length == 0) return string.Empty;

        var shared = 0;
        while (shared < complete.Length && shared < inProgress.Length && complete[shared] == inProgress[shared])
            shared++;

        return complete[..shared];
    }

    /// <summary>
    /// 把「收回這一位僱員已完成的探險（<b>不重新派遣</b>）」排進佇列。
    /// </summary>
    /// <remarks>
    /// 前提：呼叫時流程已經停在<b>這位僱員自己的選單</b>（<c>SelectString</c>）上。
    /// <para>
    /// 🔴🔴 <b>「重新派遣」那顆按鈕本模組一次都不碰。</b>使用者要的是讓僱員閒置下來好換裝改名，
    /// 收完又派出去等於整件事白做。AutoRetainer 的 <c>ClickResultReassign</c> 是<b>刻意</b>不採用的，
    /// 我們只走它的 <c>ClickResultConfirm</c> 那一條。
    /// </para>
    /// <para>
    /// 🔴 每一步 fail-closed：預期的選單項／視窗／按鈕不在就<b>停下並指名是哪一步</b>。
    /// 唯一「跳過而不停下」的情況是<b>遊戲自己說沒有可收的探險</b>（選單上沒有那一項，
    /// 或那一項是「結束保留中」）——那不是我們的假設破了，是合法的遊戲狀態，
    /// 而且一律印一行 <c>Information</c>，不會靜默。
    /// </para>
    /// <para>
    /// ⚠️ 這裡<b>不</b>負責「收不到就不准往下走」。改名流程另外加了一道
    /// 「確認不在探險中」的硬閘門——因為在那條流程裡，收不回探險會讓後面的卸裝以
    /// 完全不同的樣貌失敗（<c>LogMessage</c> 3904），錯誤訊息會把人帶去錯的地方。
    /// </para>
    /// </remarks>
    private void EnqueueCollectVenture(ulong retainerId, string retainerName)
    {
        // 這一位的執行期旗標。⚠️ 每一步都自己重查遊戲狀態，這個變數只承載「上一步決定了什麼」。
        var shouldCollect = false;

        queue.Enqueue($"檢查探險狀態（{retainerName}）", () =>
        {
            shouldCollect = false;

            if (!TryLookupVenture(retainerId, out var state, out var remaining))
                return AbortWith($"讀不到「{retainerName}」的僱員資料，無法判斷探險狀態。");

            switch (state)
            {
                case VentureState.Complete:
                    shouldCollect = true;
                    Svc.Log.Information($"[{InternalName}] 「{retainerName}」探險已完成，準備收回成果（不重新派遣）。");
                    return true;

                case VentureState.Idle:
                    Svc.Log.Information($"[{InternalName}] 「{retainerName}」目前沒有進行中的探險，收回這一步跳過。");
                    if (runMode == RunMode.CollectVentures) collectSkippedCount++;
                    return true;

                case VentureState.Running:
                    Svc.Log.Information(
                        $"[{InternalName}] 「{retainerName}」的探險還沒結束（剩 {FormatRemaining(remaining)}），不收回。");
                    if (runMode == RunMode.CollectVentures) collectSkippedCount++;
                    return true;

                default:
                    // 🔴 有派探險但讀不到完成時刻。不知道就不要動。
                    Svc.Log.Information(
                        $"[{InternalName}] 「{retainerName}」有派探險但讀不到完成時刻（VentureComplete＝0），保守起見不收回。");
                    if (runMode == RunMode.CollectVentures) collectSkippedCount++;
                    return true;
            }
        });

        queue.Enqueue($"選擇「查看僱員探險情況」（{retainerName}）", () =>
        {
            if (!shouldCollect) return true;

            var addon = UiHelper.GetAddon(SelectStringAddon);
            if (!UiHelper.IsReady(addon)) return false;

            var quitText = GetAddonText(AddonRowRetainerQuit);
            if (quitText.Length == 0)
                return AbortWith($"讀不到「讓僱員返回」的選單文字（Addon {AddonRowRetainerQuit}）。");

            var entries = UiHelper.GetSelectStringEntries(addon);

            // 🔑「選單建好了沒」用一個資料表裡真的存在、而且僱員選單上永遠有的項目來判
            //    （「讓僱員返回」），不用「至少幾項」這種魔術數字。
            //    沒看到它就代表版面還在建，下一幀再看——不會對半成品的選單下判斷。
            if (!entries.Exists(e => e.StartsWith(quitText, StringComparison.Ordinal))) return false;

            var completeText = GetAddonText(AddonRowVentureReportComplete);
            if (completeText.Length == 0)
            {
                return AbortWith(
                    $"讀不到「查看僱員探險情況　[結束]」的選單文字（Addon {AddonRowVentureReportComplete}），無法確認要點哪一項。");
            }

            var index = entries.FindIndex(e => e.StartsWith(completeText, StringComparison.Ordinal));
            if (index >= 0)
            {
                if (!Throttle.Pass($"{InternalName}-ViewVenture", 1_000)) return false;

                Svc.Log.Information(
                    $"[{InternalName}] 「{retainerName}」選單第 {index} 項命中 Addon {AddonRowVentureReportComplete}：" +
                    $"「{entries[index]}」，點下去。");
                UiHelper.SelectStringEntry(addon, index);
                return true;
            }

            // ── 以下都是「遊戲說沒有可收的探險」：跳過，但一定要看得見。 ──
            var heldText = GetAddonText(AddonRowVentureReportHeld);
            if (heldText.Length > 0 && entries.Exists(e => e.StartsWith(heldText, StringComparison.Ordinal)))
            {
                shouldCollect = false;
                collectHeldCount++;
                Svc.Log.Information(
                    $"[{InternalName}] 「{retainerName}」的探險是「結束保留中」（Addon {AddonRowVentureReportHeld}）。" +
                    "這個狀態離線查不到語意，本模組不動它——請自己開僱員選單看一下。");
                return true;
            }

            var prefix = GetVentureEntryPrefix();
            if (prefix.Length > 0 && entries.Exists(e => e.StartsWith(prefix, StringComparison.Ordinal)))
            {
                shouldCollect = false;
                if (runMode == RunMode.CollectVentures) collectSkippedCount++;
                Svc.Log.Information(
                    $"[{InternalName}] 「{retainerName}」的僱員資料說探險已完成，但選單上那一項不是「[結束]」" +
                    "（多半是還差幾秒，或是剛被重新派遣）。這一位跳過。");
                return true;
            }

            shouldCollect = false;
            if (runMode == RunMode.CollectVentures) collectSkippedCount++;
            Svc.Log.Information(
                $"[{InternalName}] 「{retainerName}」的選單上完全沒有探險報告那一項，這一位跳過。" +
                $"（讀到的選項：{string.Join(" | ", entries)}）");
            return true;
        }, 20_000);

        // 收回探險成果——容錯狀態機。每 tick 看「當下哪個視窗開著」就做對應動作，不假設遊戲一定照
        // 「成果視窗→確認→再派視窗」的線性順序走：再派視窗可能提早出現、成果視窗可能一閃而過。
        // 直到 RetainerManager 讀到這位僱員閒置（探險編號歸零）且沒有任何探險視窗開著，才算真的收回完成。
        //
        // 🔴 這一步「不重派」的關鍵：任何時候看到 RetainerTaskAsk 就按 ReturnButton，絕不按
        //    AssignButton；看到 RetainerTaskResult 就按 ConfirmButton，絕不按 ReassignButton。
        //    這一步的逾時交給 TaskQueue，由 OnTimeout 的 CollectVentures 分支處理（單隻收不到只記錄＋
        //    跳下一位，不會停掉整批）。
        queue.Enqueue($"收回探險成果（{retainerName}）", () =>
        {
            if (!shouldCollect) return true;

            // (1) 任何時候彈出「要再派遣嗎」＝按「返回」關掉，絕不重新派遣。
            //     AutoRetainer 取消派遣走的也是這顆 ReturnButton。
            var ask = UiHelper.GetAddon(RetainerTaskAskAddon);
            if (UiHelper.IsReady(ask))
            {
                if (Throttle.Pass($"{InternalName}-AskReturn", 500))
                {
                    Svc.Log.Information(
                        $"[{InternalName}] 「{retainerName}」彈出派遣視窗（{RetainerTaskAskAddon}），按「返回」，不重新派遣。");
                    UiHelper.ClickButton(ask, ((AddonRetainerTaskAsk*)ask)->ReturnButton);
                }

                return false;
            }

            // (2) 確認框（快速探險收取偶爾會彈）——讀文字留紀錄，保守按「否」，避免誤觸再派。
            //     照艦隊慣例：不認得的 Yesno 一律按「否」較安全。
            if (UiHelper.IsAddonReady(SelectYesnoAddon))
            {
                if (Throttle.Pass($"{InternalName}-CollectYesno", 500))
                {
                    var prompt = UiHelper.GetSelectYesnoText();
                    Svc.Log.Information(
                        $"[{InternalName}] 「{retainerName}」收取途中彈出確認框「{prompt}」，保守按「否」（不重新派遣）。");
                    UiHelper.ClickSelectYesnoNo();
                }

                return false;
            }

            // (3) 探險成果視窗＝按「確認」收下。
            //     🔴🔴 只碰 ConfirmButton（+0x260）；ReassignButton（+0x258）整個模組不出現第二次。
            //     📌 UiHelper.ClickButton 內部先判 OwnerNode 再讀 IsEnabled（§47），按鈕還沒 enable
            //        時回 false ⇒ 自然變成「等按鈕可按」。
            var result = UiHelper.GetAddon(RetainerTaskResultAddon);
            if (UiHelper.IsReady(result))
            {
                if (Throttle.Pass($"{InternalName}-ResultConfirm", 500))
                    UiHelper.ClickButton(result, ((AddonRetainerTaskResult*)result)->ConfirmButton);

                return false;
            }

            // (4) 沒有任何探險視窗開著——先看是不是真的收完了（真值只認 RetainerManager）。
            if (TryLookupVenture(retainerId, out var state, out _) && state == VentureState.Idle)
            {
                collectedCount++;
                collectConsecutiveTimeouts = 0;
                Svc.Log.Information($"[{InternalName}] 「{retainerName}」的探險成果已收回，現在是閒置狀態。");
                return true;
            }

            // (5) 還沒閒置、也沒有成果視窗：多半是上一次點擊沒生效、視窗一閃而過，選單又回到僱員子選單。
            //     重新點「查看僱員探險情況　[結束]」（enforce，等同 AutoRetainer 的 EnforceSelectString）。
            var menu = UiHelper.GetAddon(SelectStringAddon);
            if (UiHelper.IsReady(menu))
            {
                var completeText = GetAddonText(AddonRowVentureReportComplete);
                if (completeText.Length > 0)
                {
                    var entries = UiHelper.GetSelectStringEntries(menu);
                    var index = entries.FindIndex(e => e.StartsWith(completeText, StringComparison.Ordinal));
                    if (index >= 0)
                    {
                        if (Throttle.Pass($"{InternalName}-ReViewVenture", 1_000))
                        {
                            Svc.Log.Information(
                                $"[{InternalName}] 「{retainerName}」成果視窗沒出現、選單回到僱員子選單，重新點「[結束]」那一項。");
                            UiHelper.SelectStringEntry(menu, index);
                        }

                        return false;
                    }

                    // 「[結束]」那一項已經不在了：可能已被（AutoRetainer 或使用者）重新派遣，或本來就
                    //  沒得收。留給 TaskQueue 逾時去判（那裡會讀 RetainerManager 說明最後狀態），不卡死。
                }
            }

            return false;
        }, 40_000);
    }

    /// <summary>
    /// 「互動傳喚鈴 → 等僱員清單 → 點名選這一位」三步。
    /// </summary>
    /// <remarks>📌 改名流程與收回探險流程共用；內容與原先寫在改名流程裡的三步<b>逐字相同</b>。</remarks>
    private void EnqueueOpenRetainer(WorkItem work)
    {
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
    }

    /// <summary>「讓僱員返回」一步。</summary>
    /// <remarks>📌 內容與原先寫在改名流程裡的那一步<b>逐字相同</b>，只是抽出來共用。</remarks>
    private void EnqueueQuitRetainer()
    {
        queue.Enqueue("讓僱員返回", () =>
        {
            // 🔴 傳喚後僱員會先講一句招呼（Talk），指令選單要等這句被點掉才會出現。
            //    YesAlready 對僱員／傳喚鈴的 Talk 是「Not proceeding」（2026-08-23 實機確認不自動推進），
            //    而自動改名期間我們又暫停了 YesAlready ⇒ 這句招呼一定得自己點，否則卡在等指令選單。
            if (UiHelper.IsAddonReady(TalkAddon))
            {
                if (Throttle.Pass($"{InternalName}-QuitTalk", 300)) UiHelper.ClickTalkIfOpen();
                return false;
            }

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
    }


    // ──────────────────────────── 自動 CharaMake 改名 ────────────────────────────

    /// <summary>指派候選名字（自動改名版）——只設 <see cref="currentCandidate"/> 與旗標，不啟動錄製、不等手動。</summary>
    private void EnqueueAssignCandidateAuto(WorkItem work)
    {
        queue.Enqueue($"指派候選名字（自動改名，{work.OldName}）", () =>
        {
            YesAlreadyIpc.Suppress();

            candidateAttempts = 0;
            candidateExhausted = false;
            candidateExhaustedReason = string.Empty;
            currentCandidate = string.Empty;
            sawNameTaken = false;
            sawNameRejected = false;

            if (!TryAssignNextCandidate())
                return AbortWith(candidateExhaustedReason);

            return true;
        });
    }

    /// <summary>讓僱員返回之後，關掉僱員清單、離開傳喚鈴，才能跟管理人互動開「有什麼事？」。</summary>
    /// <remarks>📌 關法照抄 AutoRetainer <c>CloseRetainerList</c>：對 <c>RetainerList</c> 送 <c>FireCallback(1, [Int=-1])</c>。</remarks>
    private void EnqueueLeaveBell(WorkItem work)
    {
        queue.Enqueue($"離開傳喚鈴（改名前，{work.OldName}）", () =>
        {
            if (!Svc.Condition[ConditionFlag.OccupiedSummoningBell] && !UiHelper.IsAddonReady(RetainerListAddon))
                return true;

            // 還停在僱員子選單就先選「讓僱員返回」退回清單。
            var menu = UiHelper.GetAddon(SelectStringAddon);
            if (UiHelper.IsReady(menu))
            {
                var quitText = GetAddonText(AddonRowRetainerQuit);
                if (quitText.Length > 0)
                {
                    var entries = UiHelper.GetSelectStringEntries(menu);
                    var index = entries.FindIndex(e => e.StartsWith(quitText, StringComparison.Ordinal));
                    if (index >= 0)
                    {
                        if (Throttle.Pass($"{InternalName}-LeaveQuit", 800))
                            UiHelper.SelectStringEntry(menu, index);
                        return false;
                    }
                }
            }

            var list = UiHelper.GetAddon(RetainerListAddon);
            if (UiHelper.IsReady(list))
            {
                if (Throttle.Pass($"{InternalName}-CloseRL", 800))
                    UiHelper.FireCallback(list, false, -1);
            }

            return false;
        }, 20_000);
    }

    /// <summary>
    /// 自動操作 CharaMake 改名：互動管理人 → 「有什麼事？」→ 改變樣貌性格名字 → 選這位僱員 →
    /// 一路按確認、保留容貌、性格選第一項、填名字送出。整段一步到底、內部自限。
    /// </summary>
    /// <remarks>
    /// 🔴🔴 安全不變式：只在正面比對到「要使用已儲存的角色形象嗎？」時才按「是」並把 <c>gatePassed</c> 立起來；
    /// 沒通過這道閘門，絕不確認任何「設定成目前的樣子」——那一步若在閘門前出現就當場中止整批，
    /// 因為確認一個沒保留原容貌的預覽＝不可逆地把外觀改成空白。
    /// <para>🔴 除了「儲存目前形象？＝否」，本狀態機只會按「是」，且每個「是」都要正面比對到對應提示；
    /// 認不得的 SelectYesno 一律不按（fail-closed）。</para>
    /// <para>🔴 失敗（找不到管理人／某步比不到／逾內部期限）不停整批：記一行 Information、回 true 往下走，
    /// 讓後面的「重新傳喚＋穿回」把已卸的裝備救回來，再換下一位。只有安全閘門違例才 AbortWith。</para>
    /// </remarks>
    private void EnqueueAutoCharaMakeRename(WorkItem work)
    {
        DateTime? started = null;
        var everSawRenameUi = false;
        var gatePassed = false;
        var nameSubmitted = false;

        queue.Enqueue($"自動改名（{work.OldName}）", () =>
        {
            if (!TryReady(out var reason)) return AbortWith(reason);
            started ??= DateTime.UtcNow;
            var elapsed = (DateTime.UtcNow - started.Value).TotalMilliseconds;

            // (0) 成功判定最優先：RetainerManager 讀到候選名＝改名生效。避免完成後又被管理人選單勾回去重跑。
            var liveName = LookupRetainerName(work.RetainerId);
            if (currentCandidate.Length > 0 && liveName == currentCandidate)
            {
                renamedCount++;
                MarkCandidateUsed(currentCandidate, work.RetainerId, work.OldName);
                Svc.Log.Information($"[{InternalName}] 「{work.OldName}」已自動改名為「{currentCandidate}」。");
                currentCandidate = string.Empty;
                return true;
            }

            // 名字被占用／不能用（OnChatMessage 設起）：換下一個候選、允許重新驅動輸入框。
            if (sawNameTaken)
            {
                sawNameTaken = false;
                MarkCurrentCandidate(RetainerNameCandidateState.Taken, "伺服器回報名字已被使用（自動改名）");
                nameSubmitted = false;
                if (candidateExhausted)
                {
                    Svc.Log.Information($"[{InternalName}] 「{work.OldName}」候選用盡（{candidateExhaustedReason}），放棄改名這一位。");
                    currentCandidate = string.Empty;
                    return true;
                }
            }
            else if (sawNameRejected)
            {
                sawNameRejected = false;
                MarkCurrentCandidate(RetainerNameCandidateState.Rejected, "伺服器回報名字不能使用（自動改名）");
                nameSubmitted = false;
                if (candidateExhausted)
                {
                    Svc.Log.Information($"[{InternalName}] 「{work.OldName}」候選用盡（{candidateExhaustedReason}），放棄改名這一位。");
                    currentCandidate = string.Empty;
                    return true;
                }
            }

            // 內部期限：保證早於 TaskQueue 逾時放棄，永不觸發 OnTimeout→停整批。
            if (elapsed >= AutoRenameInternalDeadlineMs)
            {
                Svc.Log.Information(
                    $"[{InternalName}] 「{work.OldName}」自動改名逾內部期限（{AutoRenameInternalDeadlineMs / 1000} 秒）未完成，放棄這一位（去救裝備、換下一位）。");
                currentCandidate = string.Empty;
                return true;
            }
            if (!everSawRenameUi && elapsed >= AutoRenameVocateSearchMs)
            {
                Svc.Log.Information(
                    $"[{InternalName}] 「{work.OldName}」等 {AutoRenameVocateSearchMs / 1000} 秒仍開不了管理人選單，放棄改名這一位（去救裝備、換下一位）。");
                currentCandidate = string.Empty;
                return true;
            }

            // (A) SelectYesno —— 一律讀提示文字決定。這裡是安全核心。
            if (UiHelper.IsAddonReady(SelectYesnoAddon))
            {
                everSawRenameUi = true;
                var prompt = UiHelper.GetSelectYesnoText();
                if (prompt.Length == 0) return false;

                var useSaved = GetLobbyText(LobbyRowUseSavedAppearance);
                if (useSaved.Length > 0 && prompt.Contains(useSaved, StringComparison.Ordinal))
                {
                    if (Throttle.Pass($"{InternalName}-YesUseSaved", 600))
                    {
                        Svc.Log.Information($"[{InternalName}] 安全閘門命中「{useSaved}」→ 按「是」，保留原容貌。");
                        UiHelper.ClickSelectYesnoYes();
                        gatePassed = true;
                    }
                    return false;
                }

                var saveNow = GetLobbyText(LobbyRowSaveAppearance);
                if (saveNow.Length > 0 && prompt.Contains(saveNow, StringComparison.Ordinal))
                {
                    if (Throttle.Pass($"{InternalName}-NoSaveAppearance", 600))
                    {
                        Svc.Log.Information($"[{InternalName}] 「{saveNow}」→ 按「否」（不另存範本）。");
                        UiHelper.ClickSelectYesnoNo();
                    }
                    return false;
                }

                var setNow = GetLobbyText(LobbyRowSetAppearanceNow);
                if (setNow.Length > 0 && prompt.Contains(setNow, StringComparison.Ordinal))
                {
                    if (!gatePassed)
                        return AbortWith(
                            $"安全中止：還沒通過「要使用已儲存的角色形象嗎？」的閘門，就出現「{setNow}」。" +
                            $"不確認，以免把「{work.OldName}」的外觀改成空白（不可逆）。請改用手動改名，或把自動改名關掉。");

                    if (Throttle.Pass($"{InternalName}-YesSetAppearance", 600))
                    {
                        Svc.Log.Information($"[{InternalName}] 「{setNow}」→ 按「是」（預覽＝保留的原容貌）。");
                        UiHelper.ClickSelectYesnoYes();
                    }
                    return false;
                }

                if (prompt.Contains(RenameSettingConfirmMarker1, StringComparison.Ordinal) &&
                    prompt.Contains(RenameSettingConfirmMarker2, StringComparison.Ordinal))
                {
                    if (Throttle.Pass($"{InternalName}-YesRenameSetting", 600))
                    {
                        Svc.Log.Information($"[{InternalName}] 更改設定確認框「{prompt}」→ 按「是」。");
                        UiHelper.ClickSelectYesnoYes();
                    }
                    return false;
                }

                if (prompt.Contains(PersonalityConfirmMarker, StringComparison.Ordinal))
                {
                    if (Throttle.Pass($"{InternalName}-YesPersonality", 600))
                    {
                        Svc.Log.Information($"[{InternalName}] 性格確認框「{prompt}」→ 按「是」。");
                        UiHelper.ClickSelectYesnoYes();
                    }
                    return false;
                }

                if (Throttle.Pass($"{InternalName}-UnknownYesno", 3000))
                    Svc.Log.Information($"[{InternalName}] 自動改名遇到未預期的確認框「{prompt}」，不按任何鈕（fail-closed），等內部期限放棄。");
                return false;
            }

            // (B) InputString —— 填候選名字。
            if (UiHelper.IsAddonReady(InputStringAddon))
            {
                everSawRenameUi = true;

                var nameLen = currentCandidate.Length == 0
                    ? 0
                    : new System.Globalization.StringInfo(currentCandidate).LengthInTextElements;
                if (nameLen < 1 || nameLen > 6)
                {
                    Svc.Log.Information($"[{InternalName}] 候選「{currentCandidate}」長度 {nameLen} 不在 1~6，改用下一個。");
                    MarkCurrentCandidate(RetainerNameCandidateState.Rejected, $"名字長度 {nameLen} 不符 1~6");
                    nameSubmitted = false;
                    if (candidateExhausted)
                    {
                        Svc.Log.Information($"[{InternalName}] 「{work.OldName}」候選用盡（{candidateExhaustedReason}），放棄改名這一位。");
                        currentCandidate = string.Empty;
                        return true;
                    }
                    return false;
                }

                if (Throttle.Pass($"{InternalName}-AutoInput", 2000))
                {
                    if (UiHelper.FireInputStringConfirm(currentCandidate))
                    {
                        nameSubmitted = true;
                        Svc.Log.Information($"[{InternalName}] 於 InputString 填入候選「{currentCandidate}」並送出。");
                    }
                }
                return false;
            }

            // (C) SelectString —— 依內容分派（不靠寫死索引）。
            var ss = UiHelper.GetAddon(SelectStringAddon);
            if (UiHelper.IsReady(ss))
            {
                var entries = UiHelper.GetSelectStringEntries(ss);

                // 1. 管理人「有什麼事？」→ 改變樣貌性格名字。送出名字後不再重入。
                var idxChange = entries.FindIndex(e => e.Contains(VocateChangeAppearanceMarker, StringComparison.Ordinal));
                if (idxChange >= 0 && !nameSubmitted)
                {
                    everSawRenameUi = true;
                    if (Throttle.Pass($"{InternalName}-VocateChange", 1000))
                    {
                        Svc.Log.Information($"[{InternalName}] 管理人選單第 {idxChange} 項「{entries[idxChange]}」→ 選它（改變樣貌性格名字）。");
                        UiHelper.SelectStringEntry(ss, idxChange);
                    }
                    return false;
                }

                // 2. 選擇僱員 —— 用舊名首段比對（改名尚未生效，清單仍顯示舊名）。
                var idxRetainer = entries.FindIndex(e => FirstNameToken(e) == work.OldName);
                if (idxRetainer >= 0)
                {
                    everSawRenameUi = true;
                    if (Throttle.Pass($"{InternalName}-SelectRetainerRename", 1000))
                    {
                        Svc.Log.Information($"[{InternalName}] 選擇僱員清單第 {idxRetainer} 項「{entries[idxRetainer]}」（比對舊名「{work.OldName}」）。");
                        UiHelper.SelectStringEntry(ss, idxRetainer);
                    }
                    return false;
                }

                // 3. 性格選單 —— 第一項若是「開朗」就選它（int=0）。
                if (entries.Count > 0 && entries[0].StartsWith(PersonalityFirstMarker, StringComparison.Ordinal))
                {
                    everSawRenameUi = true;
                    if (Throttle.Pass($"{InternalName}-PersonalityFirst", 1000))
                    {
                        Svc.Log.Information($"[{InternalName}] 性格選單→ 選第一項「{entries[0]}」（int=0）。");
                        UiHelper.SelectStringEntry(ss, 0);
                    }
                    return false;
                }

                if (Throttle.Pass($"{InternalName}-UnknownSelectString", 3000))
                    Svc.Log.Information($"[{InternalName}] 自動改名遇到未預期的選單（{string.Join(" | ", entries)}），不動作，等內部期限。");
                return false;
            }

            // (D) Talk —— 點掉推進。
            if (UiHelper.IsAddonReady(TalkAddon))
            {
                if (Throttle.Pass($"{InternalName}-RenameTalk", 300))
                    UiHelper.ClickTalkIfOpen();
                return false;
            }

            // (E) 沒有任何改名視窗開著：還沒進到改名流程就互動管理人；否則等轉場。
            if (!everSawRenameUi)
            {
                if (Throttle.Pass($"{InternalName}-Vocate", 1500))
                {
                    if (!InteractWithNearbyVocate() && Throttle.Pass($"{InternalName}-VocateMiss", 3000))
                        Svc.Log.Information(
                            $"[{InternalName}] 附近找不到可互動的僱員管理人（僱員窗口）或互動未生效——自動改名走管理人選單，請站在管理人旁邊。");
                }
            }

            return false;
        }, AutoRenameInternalDeadlineMs + 15_000);
    }

    /// <summary>改名之後（無論成敗）重新傳喚並選這位僱員，讓 <see cref="EnqueueRedress"/> 有僱員裝備欄可用。</summary>
    /// <remarks>🔴 依現名（每幀重查 <see cref="LookupRetainerName"/>）選僱員，不是舊名。沒卸下裝備就整段跳過。</remarks>
    private void EnqueueReopenRetainerForRedress(WorkItem work)
    {
        queue.Enqueue($"重新互動傳喚鈴（穿回前，{work.OldName}）", () =>
        {
            if (stashed.Count == 0) return true;
            if (!TryReady(out var reason)) return AbortWith(reason);

            if (Svc.Condition[ConditionFlag.OccupiedSummoningBell]) return true;
            if (UiHelper.IsAddonReady(RetainerListAddon)) return true;

            if (!Throttle.Pass($"{InternalName}-BellRedress", 1_500)) return false;

            InteractWithNearbyBell();
            return false;
        }, 30_000);

        queue.EnqueueWait("等待僱員清單開啟（穿回前）", () =>
            stashed.Count == 0 || UiHelper.IsAddonReady(RetainerListAddon), 30_000);

        queue.Enqueue("選擇僱員（穿回前，依現名）", () =>
        {
            if (stashed.Count == 0) return true;
            if (!Throttle.Pass($"{InternalName}-SelectRetainerRedress", 1_000)) return false;

            var addon = UiHelper.GetAddon(RetainerListAddon);
            if (!UiHelper.IsReady(addon)) return false;

            var liveName = LookupRetainerName(work.RetainerId);
            if (liveName.Length == 0) return AbortWith("穿回前讀不到僱員現名，無法重新選取。");

            if (!TryFindRetainerListIndex(addon, liveName, out var index, out var seenNames))
                return AbortWith($"穿回前在僱員清單找不到「{liveName}」（讀到的是：{seenNames}）。");

            UiHelper.FireCallback(
                addon, true, RetainerListSelectEventId, (uint)index, default(AtkValue), default(AtkValue));
            return true;
        }, 20_000);

        queue.EnqueueWait("等待僱員裝備欄載入（穿回前）", () =>
            stashed.Count == 0 ||
            (Svc.Condition[ConditionFlag.OccupiedSummoningBell] && TryGetRetainerEquipContainer(out _)),
            30_000);
    }

    /// <summary>
    /// 改名之後（或放棄之後）把殘留的改名視窗清乾淨、回到中立狀態，讓後面的「重新傳喚」互動得了鈴。
    /// 回到中立時順便還原 YesAlready。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>不碰殘留的 SelectYesno</b>：改名中途放棄時可能停在安全關鍵的「要使用已儲存的角色形象嗎？」，
    /// 亂按會進空白捏臉畫面（不可逆）。看到確認框就不動、等逾時 <see cref="StopRun"/>（安全，只卡這一位）。
    /// <para>🔴 這是自動改名序列 YesAlready 的正常還原點；中止／停止／停用路徑由 <see cref="StopRun"/>／
    /// <see cref="FinishRun"/>／<c>OnDisable</c> 各自還原（冪等）。</para>
    /// </remarks>
    private void EnqueueReturnToNeutral(WorkItem work)
    {
        queue.Enqueue($"離開改名畫面回到中立（{work.OldName}）", () =>
        {
            if (UiHelper.IsAddonReady(TalkAddon))
            {
                if (Throttle.Pass($"{InternalName}-NeutralTalk", 300))
                    UiHelper.ClickTalkIfOpen();
                return false;
            }

            var ss = UiHelper.GetAddon(SelectStringAddon);
            if (UiHelper.IsReady(ss))
            {
                if (Throttle.Pass($"{InternalName}-NeutralCancelSS", 600))
                    UiHelper.FireCallback(ss, false, -1);
                return false;
            }

            var input = UiHelper.GetAddon(InputStringAddon);
            if (UiHelper.IsReady(input))
            {
                if (Throttle.Pass($"{InternalName}-NeutralCancelInput", 600))
                    UiHelper.FireCallback(input, false, -1);
                return false;
            }

            if (UiHelper.IsAddonReady(SelectYesnoAddon))
            {
                // 🔴 殘留確認框可能是改名途中放棄時停在安全關鍵的那一步——絕不亂按，等逾時停止。
                if (Throttle.Pass($"{InternalName}-NeutralYesnoWait", 3000))
                    Svc.Log.Information($"[{InternalName}] 回中立時仍有確認框開著（改名可能中途放棄），不亂按，等逾時停止。");
                return false;
            }

            // 中立（可能停在 RetainerList／世界）：還原 YesAlready，往下走去重新傳喚穿回。
            YesAlreadyIpc.Restore();
            return true;
        }, 20_000);
    }

    /// <summary>取選單項第一段（僱員名）——僱員名不含空白，後面接全形空白＋狀態文字。</summary>
    private static string FirstNameToken(string entry)
    {
        if (string.IsNullOrEmpty(entry)) return string.Empty;
        var s = entry.TrimStart();
        var cut = s.Length;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == ' ' || c == (char)0x3000 || c == (char)9 || c == '[' || c == (char)0xFF3B)
            {
                cut = i;
                break;
            }
        }

        return s[..cut].TrimEnd();
    }

    /// <summary>退回舊行為：指派候選 → 錄製＋等使用者手動改名 → 結束錄製。與自動改名互斥。</summary>
    private void EnqueueManualRenameFlow(WorkItem work)
    {
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
    }

    // ─────────────────── 獨立流程：收回所有已完成探險 ───────────────────

    /// <summary>「收回所有已完成探險（不重派）」的前置閘門。</summary>
    private List<string> BuildCollectBlockers()
    {
        var blockers = new List<string>();

        if (!snapshotValid)
        {
            blockers.Add(snapshotProblem);
            return blockers;
        }

        // 🔴 AutoRetainer 若正在跑自己的收派循環，會把我們剛收回的僱員立刻重新派遣出去，
        //    使用者永遠等不到僱員閒置。⚠️ 只在「探得到而且說忙」時擋：
        //    沒裝、IPC 不在、或還沒探到，一律放行（不確定就不要多擋一道）。
        if (arProbed && arAvailable && arBusy)
        {
            blockers.Add(
                "AutoRetainer 正在忙（PluginState.IsBusy），它會把剛收回的僱員立刻重新派遣。" +
                "請先停止 AutoRetainer 的多角模式／自動收派。");
        }

        var collectable = CountVentureStates(out var running, out var nearestRemaining, out var unknown);
        if (collectable == 0)
        {
            if (running > 0)
            {
                blockers.Add(
                    $"目前沒有已完成的探險（{running} 位還在探險中，最近一位還要 {FormatRemaining(nearestRemaining)}）。");
            }
            else if (unknown > 0)
            {
                blockers.Add($"有 {unknown} 位僱員在探險但讀不到完成時刻，無法判斷可不可以收回。");
            }
            else
            {
                blockers.Add("目前沒有僱員在探險。");
            }
        }

        if (!bellLookupUsable)
            blockers.Add("讀不到傳喚鈴的名稱資料（EObjName 表），無法確認你站在鈴旁邊。");
        else if (!bellReachable)
            blockers.Add("附近沒有可互動的傳喚鈴，請先走到傳喚鈴旁邊。");

        if (GetAddonText(AddonRowRetainerQuit).Length == 0)
            blockers.Add("讀不到「讓僱員返回」的選單文字（Addon 表），流程無法收尾。");

        if (GetAddonText(AddonRowVentureReportComplete).Length == 0)
        {
            blockers.Add(
                $"讀不到「查看僱員探險情況　[結束]」的選單文字（Addon {AddonRowVentureReportComplete}），無法確認要點哪一項。");
        }

        return blockers;
    }

    /// <summary>
    /// 開始「收回所有已完成探險（不重派）」。
    /// </summary>
    /// <remarks>
    /// 📌 只處理<b>目前登入的這個角色</b>的僱員。使用者的僱員分散在多個角色上，
    /// 換角色要自己切（本模組不碰 AutoRetainer 任何會「做事」的 IPC）。
    /// </remarks>
    private void StartCollectVentures()
    {
        if (queue.IsBusy) return;

        var blockers = BuildCollectBlockers();
        if (blockers.Count > 0)
        {
            Svc.Log.Information($"[{InternalName}] 收回探險的前置檢查未過，未開始：{string.Join("／", blockers)}");
            Svc.Chat.PrintError($"[TC Toolbox] 收回探險未開始：{blockers[0]}");
            return;
        }

        collectList.Clear();

        var skippedRunning = new List<string>();
        var skippedUnknown = new List<string>();

        foreach (var row in rows)
        {
            // 🔴 用 RetainerManager 當場重查，不用 UI 快照——快照最多可能舊 500ms。
            if (!TryLookupVenture(row.RetainerId, out var state, out var remaining)) continue;

            switch (state)
            {
                case VentureState.Complete:
                    collectList.Add(new WorkItem(row.RetainerId, row.CurrentName));
                    break;
                case VentureState.Running:
                    skippedRunning.Add($"{row.CurrentName}（剩 {FormatRemaining(remaining)}）");
                    break;
                case VentureState.Unknown:
                    skippedUnknown.Add(row.CurrentName);
                    break;
            }
        }

        if (collectList.Count == 0)
        {
            Svc.Log.Information($"[{InternalName}] 重查之後沒有可收回的探險，未開始。");
            Svc.Chat.PrintError("[TC Toolbox] 目前沒有已完成的探險可以收回。");
            return;
        }

        runMode = RunMode.CollectVentures;
        collectIndex = -1;
        collectedCount = 0;
        collectSkippedCount = 0;
        collectHeldCount = 0;
        collectConsecutiveTimeouts = 0;
        lastSummary = string.Empty;

        Svc.Log.Information(
            $"[{InternalName}] 開始收回探險（不重新派遣）：{collectList.Count} 位可收回。" +
            (skippedRunning.Count > 0 ? $"探險中跳過 {skippedRunning.Count} 位：{string.Join("、", skippedRunning)}。" : string.Empty) +
            (skippedUnknown.Count > 0 ? $"讀不到完成時刻跳過 {skippedUnknown.Count} 位：{string.Join("、", skippedUnknown)}。" : string.Empty) +
            $"AutoRetainer＝{DescribeAutoRetainerState()}。");

        EnqueueNextCollect();
    }

    private void EnqueueNextCollect()
    {
        queue.Enqueue("挑選下一位要收回探險的僱員", () =>
        {
            collectIndex++;
            if (collectIndex >= collectList.Count)
            {
                FinishRun("全部處理完畢");
                return true;
            }

            var work = collectList[collectIndex];
            Svc.Log.Information(
                $"[{InternalName}] ({collectIndex + 1}/{collectList.Count}) 收回「{work.OldName}」的探險。");

            EnqueueOneCollect(work);
            return true;
        });
    }

    private void EnqueueOneCollect(WorkItem work)
    {
        EnqueueOpenRetainer(work);
        EnqueueCollectVenture(work.RetainerId, work.OldName);
        EnqueueQuitRetainer();
        queue.EnqueueDelay(1_000, "間隔");
        EnqueueNextCollect();
    }

    /// <summary>
    /// 收回探險時某一步逾時的處理：記錄這一位（含最後是不是被重新派遣），跳下一位，不停整批。
    /// </summary>
    /// <remarks>
    /// 🔴 只在 <see cref="RunMode.CollectVentures"/> 模式下由 <c>OnTimeout</c> 呼叫；
    ///    改名流程（<see cref="RunMode.Rename"/>）的逾時維持原本的「整條停下」。
    /// ⚠️ 逾時當下 <c>OnTimeout</c> 觸發前 <c>TaskQueue</c> 已 <c>Abort()</c> 清空佇列，
    ///    所以這裡是在空佇列上重新排「復原＋下一位」。
    /// </remarks>
    private void HandleCollectTimeout(string step)
    {
        collectConsecutiveTimeouts++;

        var hasWork = collectIndex >= 0 && collectIndex < collectList.Count;
        var name = hasWork ? collectList[collectIndex].OldName : "（未知）";
        var retainerId = hasWork ? collectList[collectIndex].RetainerId : 0;

        string outcome;
        if (retainerId != 0 && TryLookupVenture(retainerId, out var state, out var remaining))
        {
            switch (state)
            {
                case VentureState.Idle:
                    // 其實已經收回了，只是流程沒在逾時前認出來——成果沒有損失，重置連續計數。
                    outcome = "但這一位現在是閒置狀態，探險其實已收回、成果沒有損失（只是流程沒及時認出）";
                    collectedCount++;
                    collectConsecutiveTimeouts = 0;
                    break;
                case VentureState.Running:
                    outcome = $"而且這一位現在又在探險中（剩 {FormatRemaining(remaining)}）——很可能已被重新派遣";
                    collectSkippedCount++;
                    break;
                case VentureState.Complete:
                    outcome = "這一位的探險仍是「已完成、未收回」——沒有被重新派遣，只是這次沒收到";
                    collectSkippedCount++;
                    break;
                default:
                    outcome = "讀不到這一位的探險完成時刻";
                    collectSkippedCount++;
                    break;
            }
        }
        else
        {
            outcome = "讀不到這一位的僱員資料";
            collectSkippedCount++;
        }

        Svc.Log.Information(
            $"[{InternalName}] 「{name}」在「{step}」逾時，{outcome}。跳過這一位，連續逾時 {collectConsecutiveTimeouts} 位。");

        if (collectConsecutiveTimeouts >= MaxCollectConsecutiveTimeouts)
        {
            Svc.Chat.PrintError(
                $"[TC Toolbox] 收回探險連續 {collectConsecutiveTimeouts} 位逾時，整批停止（UI 狀態可能不對，請回報）。");
            StopRun($"連續 {collectConsecutiveTimeouts} 位逾時");
            return;
        }

        Svc.Chat.PrintError($"[TC Toolbox] 「{name}」收回逾時，跳過改收下一位。");
        EnqueueCollectRecovery();
    }

    /// <summary>
    /// 收回逾時後的「盡量回到僱員清單，再跳下一位」復原步。
    /// </summary>
    /// <remarks>
    /// 🔴 自限：內部 10 秒期限到就回 <c>true</c> 往下走，絕不讓 <c>TaskQueue</c> 逾時再觸發一次
    ///    <c>OnTimeout</c>（TaskQueue 逾時設 15 秒 &gt; 內部期限）。復原不成功也沒關係——下一位的
    ///    傳喚鈴那一步會重新建立狀態。全程只按「返回／否／確認／讓僱員返回」，絕不重新派遣。
    /// </remarks>
    private void EnqueueCollectRecovery()
    {
        DateTime? deadline = null;
        queue.Enqueue("收回逾時後回到僱員清單", () =>
        {
            deadline ??= DateTime.UtcNow.AddMilliseconds(10_000);
            var expired = DateTime.UtcNow >= deadline.Value;

            // 已經回到僱員清單＝復原完成。
            if (UiHelper.IsAddonReady(RetainerListAddon)) return true;

            var ask = UiHelper.GetAddon(RetainerTaskAskAddon);
            if (UiHelper.IsReady(ask))
            {
                if (Throttle.Pass($"{InternalName}-AskReturn", 500))
                    UiHelper.ClickButton(ask, ((AddonRetainerTaskAsk*)ask)->ReturnButton);
                return expired ? true : false;
            }

            if (UiHelper.IsAddonReady(SelectYesnoAddon))
            {
                if (Throttle.Pass($"{InternalName}-CollectYesno", 500))
                    UiHelper.ClickSelectYesnoNo();
                return expired ? true : false;
            }

            var result = UiHelper.GetAddon(RetainerTaskResultAddon);
            if (UiHelper.IsReady(result))
            {
                if (Throttle.Pass($"{InternalName}-ResultConfirm", 500))
                    UiHelper.ClickButton(result, ((AddonRetainerTaskResult*)result)->ConfirmButton);
                return expired ? true : false;
            }

            var menu = UiHelper.GetAddon(SelectStringAddon);
            if (UiHelper.IsReady(menu))
            {
                var quitText = GetAddonText(AddonRowRetainerQuit);
                if (quitText.Length > 0)
                {
                    var entries = UiHelper.GetSelectStringEntries(menu);
                    var index = entries.FindIndex(e => e.StartsWith(quitText, StringComparison.Ordinal));
                    if (index >= 0 && Throttle.Pass($"{InternalName}-Quit", 1_000))
                        UiHelper.SelectStringEntry(menu, index);
                }

                return expired ? true : false;
            }

            return expired ? true : false;
        }, 15_000);

        EnqueueNextCollect();
    }

    /// <summary>收工那一行的細節（收了幾隻／跳過幾隻／最近一位還要多久）。</summary>
    private string BuildCollectSummaryDetail()
    {
        CountVentureStates(out var running, out var nearestRemaining, out var unknown);

        var parts = new List<string> { $"收回 {collectedCount} 位" };

        if (collectHeldCount > 0) parts.Add($"「結束保留中」跳過 {collectHeldCount} 位");
        if (collectSkippedCount > 0) parts.Add($"其他跳過 {collectSkippedCount} 位");

        parts.Add(running > 0
            ? $"還在探險 {running} 位（最近一位還要 {FormatRemaining(nearestRemaining)}）"
            : "沒有僱員還在探險");

        if (unknown > 0) parts.Add($"{unknown} 位讀不到完成時刻");

        return string.Join("／", parts);
    }

    // ─────────────────── AutoRetainer 狀態（唯讀） ───────────────────

    /// <summary>
    /// 探一次 AutoRetainer 的忙碌狀態。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>只讀，不做事。</b>絕不呼叫 AutoRetainer 任何會讓它動起來的 IPC，
    /// 也不註冊它的 post-process 事件（那是艦隊紅線：會把本外掛接進自動接手鏈）。
    /// 📌 兩秒探一次就夠了——跨外掛 IPC 不該每幀做（同 <c>ARSwitcher</c> 的節流）。
    /// </remarks>
    private void RefreshAutoRetainerState()
    {
        if (!Throttle.Pass($"{InternalName}-ArState", 2_000)) return;

        arAvailable = AutoRetainerIpc.TryGetIsBusy(out var busy);
        arBusy = arAvailable && busy;
        arProbed = true;
    }

    private string DescribeAutoRetainerState()
    {
        if (!arProbed) return "？";
        if (!arAvailable) return "未安裝";
        return arBusy ? "忙碌中" : "閒置";
    }

    /// <summary>這一輪在做什麼（給聊天欄與記錄用）。</summary>
    private string RunLabel() =>
        runMode == RunMode.CollectVentures ? "收回探險" : "批次僱員改名";

    /// <summary>目前進度（給中止／逾時訊息用）。</summary>
    private string ProgressText() =>
        runMode == RunMode.CollectVentures ? $"已收回 {collectedCount} 位" : $"已完成 {renamedCount} 位";

    /// <summary>中止整條佇列並留下一行說明。回傳 <c>null</c> 給 <see cref="TaskQueue"/>。</summary>
    private bool? AbortWith(string reason)
    {
        Svc.Log.Information($"[{InternalName}] 中止：{reason}（{ProgressText()}）");
        Svc.Chat.PrintError($"[TC Toolbox] {RunLabel()}已停止：{reason}");
        StopRun(reason);
        return null;
    }

    private void StopRun(string reason)
    {
        YesAlreadyIpc.Restore();
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

        lastSummary = runMode == RunMode.CollectVentures
            ? $"已停止（{reason}）：{BuildCollectSummaryDetail()}。"
            : $"已停止（{reason}）：完成 {renamedCount}／{workList.Count} 位。";
        runMode = RunMode.None;
        queue.Abort();
    }

    private void FinishRun(string reason)
    {
        YesAlreadyIpc.Restore();
        if (!recordingStandalone) StopRecording();
        waitingForManualRename = false;
        lastSummary = runMode == RunMode.CollectVentures
            ? $"{reason}：{BuildCollectSummaryDetail()}。"
            : $"{reason}：完成 {renamedCount}／{workList.Count} 位。";

        var label = RunLabel();
        Svc.Log.Information($"[{InternalName}] {lastSummary}");
        Svc.Chat.Print($"[TC Toolbox] {label}{lastSummary}");
        runMode = RunMode.None;
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
    /// <summary>
    /// 對附近的僱員管理人（<c>ENpcResident.Title</c>＝「僱員窗口」）送出互動，開「有什麼事？」選單。
    /// 🔴 <c>IGameObject</c> 只在這一幀之內使用，不留到下一幀。
    /// </summary>
    /// <remarks>🔑 用 <c>BaseId</c> 查 <c>ENpcResident</c>（查表用 BaseId 安全）、比對 <c>Title</c>，不寫死中文、也不比對身分。</remarks>
    private static bool InteractWithNearbyVocate()
    {
        var vocateTitle = GetVocateReferenceTitle();
        if (vocateTitle.Length == 0) return false;

        var player = Svc.Objects.LocalPlayer;
        if (player == null) return false;
        var playerPosition = player.Position;

        Dalamud.Game.ClientState.Objects.Types.IGameObject? best = null;
        var bestDistance = float.MaxValue;

        foreach (var obj in Svc.Objects)
        {
            if (obj.ObjectKind != ObjectKind.EventNpc) continue;
            if (!obj.IsTargetable) continue;
            if (ResolveNpcTitle(obj.BaseId) != vocateTitle) continue;

            var distance = Vector3.Distance(obj.Position, playerPosition);
            if (distance >= VocateInteractDistance || distance >= bestDistance) continue;

            bestDistance = distance;
            best = obj;
        }

        if (best == null) return false;

        var targetSystem = TargetSystem.Instance();
        if (targetSystem == null) return false;

        targetSystem->InteractWithObject(
            (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)best.Address, false);
        return true;
    }

    /// <summary>讀管理人（僱員窗口）NPC 的參考 <c>Title</c>——取候選列裡第一個非空的。</summary>
    private static string GetVocateReferenceTitle()
    {
        foreach (var rowId in VocateNpcTitleRows)
        {
            var title = Svc.Data.GetExcelSheet<ENpcResident>().GetRowOrDefault(rowId)?.Title.ExtractText() ?? string.Empty;
            if (title.Length > 0) return title;
        }

        return string.Empty;
    }

    /// <summary>用物件 <c>BaseId</c> 查它的 <c>ENpcResident.Title</c>（查表用 BaseId 安全）。</summary>
    private static string ResolveNpcTitle(uint dataId)
    {
        if (dataId == 0) return string.Empty;
        return Svc.Data.GetExcelSheet<ENpcResident>().GetRowOrDefault(dataId)?.Title.ExtractText() ?? string.Empty;
    }

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

        DrawCollectSection();
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

        ImGui.SameLine();
        ImGui.TextUnformatted("　");
        ImGui.SameLine();

        // 🔑 AutoRetainer 的狀態要「隨時掃視」得到：它一忙起來，收回的探險會立刻被重新派遣，
        //    而那件事在僱員清單上看起來就只是「怎麼又在探險」。所以放列上不放 tooltip。
        if (!arProbed)
            ImGui.TextDisabled("AutoRetainer：？");
        else if (!arAvailable)
            ImGui.TextDisabled("AutoRetainer：未安裝");
        else if (arBusy)
            ImGui.TextColored(BadColor, "AutoRetainer：忙碌中");
        else
            ImGui.TextColored(GoodColor, "AutoRetainer：閒置");

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "唯讀查詢 AutoRetainer 的 PluginState.IsBusy（不會叫它做任何事）。\n" +
                "\n" +
                "「忙碌中」＝它正在跑自己的收派循環。這時候收回探險沒有意義——\n" +
                "剛收回的僱員會被它立刻重新派遣出去，你永遠等不到僱員閒置。\n" +
                "請先停掉它的多角模式／自動收派。\n" +
                "\n" +
                "「未安裝」或「？」都不會擋你，只是這一項幫不上忙。");
        }
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
            // 🔑「還要多久」是掃視型資訊（決定現在能不能動這一位），放列上；
            //    「為什麼不能換裝」是起疑才查的，放 tooltip。
            //    🔴 讀不到完成時刻時畫「？」不畫「已完成」也不畫 0 ——把不知道畫成數字會直接誤導。
            var state = ClassifyVenture(row.VentureId, row.VentureCompleteUnix, serverNowUnix);

            switch (state)
            {
                case VentureState.Complete:
                    ImGui.TextColored(GoodColor, "探險已完成（可收回）");
                    break;
                case VentureState.Running:
                    ImGui.TextColored(
                        WarnColor, $"探險中（剩 {FormatRemaining((long)row.VentureCompleteUnix - serverNowUnix)}）");
                    break;
                default:
                    ImGui.TextDisabled("探險中（剩餘：？）");
                    break;
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    "遊戲訊息「僱員在探險的過程中無法更換裝備。」（LogMessage 3904）。\n" +
                    "改名前必須把裝備全部卸下，所以探險中的僱員無法處理。\n" +
                    "\n" +
                    (state == VentureState.Complete
                        ? "這一位的探險已經結束了：按上面的「收回所有已完成探險（不重派）」\n" +
                          "就會把成果收下來並讓他閒置（不會重新派遣）。"
                        : state == VentureState.Running
                            ? "探險還沒結束，沒有辦法讓它提早結束——只能等。"
                            : "有派探險，但完成時刻讀不到（VentureComplete＝0）。\n" +
                              "這種情況本模組一律不動它：不知道就不要碰。\n" +
                              "開一次傳喚鈴讓遊戲重新拉一次僱員資料通常就會有了。"));
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

    /// <summary>
    /// 「收回所有已完成探險（不重派）」的按鈕與它的前置檢查。
    /// </summary>
    /// <remarks>
    /// 🔑 擋住按鈕的<b>理由要看得見</b>——按鈕灰掉但不說為什麼，使用者只會以為外掛壞了。
    /// 所以未過的檢查逐條印在按鈕下面，不藏 tooltip。
    /// </remarks>
    private void DrawCollectSection()
    {
        var busy = queue.IsBusy;
        var blockers = busy ? new List<string>() : BuildCollectBlockers();

        using (ImRaii.Disabled(busy || blockers.Count > 0))
        {
            if (ImGui.Button("收回所有已完成探險（不重派）##retainer-collect-ventures"))
                StartCollectVentures();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "走一遍傳喚鈴：把探險已經結束的僱員逐一傳喚出來、收下探險成果，\n" +
                "然後直接讓他返回——「重新派遣」那顆按鈕完全不碰。\n" +
                "目的是讓僱員變成閒置，這樣才換得了裝、改得了名。\n" +
                "\n" +
                "探險還沒結束的僱員會跳過（列上看得到剩餘時間），沒派探險的不動。\n" +
                "只處理目前登入的這個角色——僱員分散在多個角色時要自己切換。\n" +
                "\n" +
                "按了才動，隨時可以按「停止」。");
        }

        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();

        if (!snapshotValid)
        {
            // ⚠️「不知道」要在列上看得見：畫 0 會讓人以為真的沒有可收的。
            ImGui.TextDisabled("可收回：？");
        }
        else
        {
            var collectable = CountVentureStates(out var running, out var nearestRemaining, out var unknown);

            if (collectable > 0)
                ImGui.TextColored(GoodColor, $"可收回：{collectable} 位");
            else
                ImGui.TextDisabled("可收回：0 位");

            ImGui.SameLine();
            ImGui.TextDisabled(running > 0
                ? $"（還在探險 {running} 位，最近一位還要 {FormatRemaining(nearestRemaining)}）"
                : "（沒有僱員還在探險）");

            if (unknown > 0)
            {
                ImGui.SameLine();
                ImGui.TextColored(WarnColor, $"（{unknown} 位讀不到完成時刻）");
            }
        }

        foreach (var blocker in blockers)
            ImGui.TextColored(BadColor, $"● {blocker}");
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
        var auto = Config.AutoCharaMakeRename;
        if (ImGui.Checkbox("自動操作改名畫面（保留容貌、只改名字）", ref auto))
        {
            Config.AutoCharaMakeRename = auto;
            Plugin.Instance.Config.Save();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "預設開啟。改完裝之後自動走管理人選單「有什麼事？」→ 改變樣貌性格名字 → 選這位僱員 →\n" +
                "一路按確認，並在「要使用已儲存的角色形象嗎？」按「是」保留原本的容貌，\n" +
                "性格統一選第一項（開朗），最後填入候選名字送出。名字被占用會自動換下一個候選。\n" +
                "\n" +
                "每一步都要比對到預期的對話文字才動作，對不上就停手、逾時跳過這一位（fail-closed）——\n" +
                "尤其「要使用已儲存的角色形象嗎？」那道閘門沒先通過，絕不確認任何「設定成目前的樣子」，\n" +
                "以免把外觀改成空白（不可逆）。\n" +
                "\n" +
                "關掉＝退回舊行為：只自動卸裝／穿裝，改名畫面停在那裡等你手動操作（期間照舊錄製）。\n" +
                "自動化萬一在某步卡住，關掉這格就能一鍵切回手動。");
        }

        var collectFirst = Config.CollectCompletedVentureBeforeRename;
        if (ImGui.Checkbox("改名前先收回已完成的探險（不重新派遣）", ref collectFirst))
        {
            Config.CollectCompletedVentureBeforeRename = collectFirst;
            Plugin.Instance.Config.Save();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "預設開啟。探險中的僱員卸不了裝（遊戲訊息「僱員在探險的過程中無法更換裝備。」），\n" +
                "所以改名流程碰到「探險已經結束」的僱員時，會先幫你把成果收下來、\n" +
                "讓他閒置，再往下卸裝——「重新派遣」那顆按鈕不會碰。\n" +
                "\n" +
                "⚠ 只對「已經結束」的探險有效。探險還在跑的僱員照舊會被前置檢查擋下來，\n" +
                "　 因為沒有辦法讓探險提早結束。\n" +
                "\n" +
                "關掉的話行為跟以前一樣：探險已完成但沒收回的僱員會直接擋住整輪流程。");
        }

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

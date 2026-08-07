using System;
using Dalamud.Memory;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Game.Text;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using Dalamud.Plugin.Services;
using TCToolbox.Core;
using CSFramework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;

namespace TCToolbox.Modules;

/// <summary>
/// 自動物品頁面轉移：按住指定鍵右鍵點物品，直接把它搬到對應的另一個頁面。
/// 零封包偽造、不寫記憶體、不做 patch。
///
/// ⚠️ 觸發點有**兩個**，因為單靠任一個都蓋不全（2026-08-01 實機釐清）：
///  1. hook <c>AgentInventoryContext.OpenForItemSlot</c>：同步、資訊最完整，
///     但**只有玩家背包會走這個函式**。實機 log 全部的觸發紀錄清一色是 Inventory*，
///     部隊置物櫃右鍵**一次都沒有**進來過。
///  2. Dalamud 的 <c>IContextMenu.OnMenuOpened</c>：它掛在 <c>RaptureAtkModule</c> vtable[22]，
///     只要開的是 ContextMenu 且 agent 是 AgentInventoryContext 就會觸發，**跟哪個函式開的無關**，
///     所以部隊置物櫃也蓋得到（同一份 log 裡 InventoryTools 就是靠這個看到 FreeCompanyChest 的）。
///     ⚠️ 它在 addon 真正開起來**之前**觸發，所以只能先記下來、下一個 framework tick 再執行。
///
/// 兩者對背包會重複觸發，靠 <see cref="DuplicateGuardMs"/> 的同格同物去重：
/// hook 是同步的、先跑完並設好 lastHandled*，延後那筆就會被擋掉。
///
/// 🔴🔴 <c>InventoryManager.MoveItemSlot</c> 的**第 6 個參數 <c>a6</c> 是「這次搬移要不要送給
/// 伺服器」的總開關**，不是無關緊要的 unknown（2026-08-02 台服 7.20 二進位鑑識定案）：
///  - <c>a6 == false</c>（**預設值，省略不寫就是它**）→ 遊戲只更新本機容器 + 刷新 UI，
///    **一個封包都不送**。這對**任何**容器都成立，背包→背包也一樣。
///  - <c>a6 == true</c> → 把作業排進 <c>_pendingOperations</c> 並組封包送出。
///    遊戲自己的拖放處理常式（0x1400F7160）對雇員頁走的就是 <c>MoveItemSlot(..., a6: true)</c>，
///    **沒有雇員特例**。
///
/// ⚠️⚠️ 本檔 2026-07-31／08-01 那幾批「MoveItemSlot 假成功、伺服器 3.9～10.6 秒後退回」的實機紀錄，
/// **全部是在 <c>a6</c> 被省略的情況下量到的**（git 歷史查證：這個呼叫從第一版到 2026-08-02
/// 只存在過四引數形式）。所以那些紀錄證明的是「封包沒送出」，**不是**「這些容器拒絕 MoveItemSlot」。
/// 先前寫在這裡的「伺服器權威容器 vs 本機容器」因果模型是**錯的** —— 真正的軸是這個旗標。
/// 每一則實機觀察本身仍然成立，只有歸因被推翻。
///
/// ⚠️ 即使如此，下面三條專用路徑**照舊保留**，因為它們是實機來回驗證過會動的，
/// 而「改用 <c>MoveItemSlot(a6: true)</c> 也能動」目前**只是推論、沒有實機證據**。
/// 要換路徑請先實測，不要憑這段說明就改：
///  - **雇員**：走遊戲自己的雇員道具命令（見 <see cref="RetainerItemCommandDelegate"/>）。
///  - **鞍袋**：點遊戲右鍵選單自己的項目（見 <see cref="TryFireContextMenuEntry"/>）。
///    它**不走**雇員道具命令（實機驗證過）。
///  - **部隊置物櫃**：走 <c>AgentFreeCompanyChest::MoveItemInChest</c>
///    （見 <see cref="MoveItemInChestDelegate"/>）。
///  - **兵裝庫**：走 <c>InventoryManager.MoveItemSlot</c>，**且一定要帶 <c>a6: true</c>**。
/// 參考 DailyRoutines AutoInventoryTransfer 的用途重寫（API13、無 OmenTools 相依）。
/// </summary>
public sealed unsafe class AutoInventoryTransfer : TcModule
{
    public override string InternalName => "AutoInventoryTransfer";
    public override string DisplayName => "自動物品頁面轉移";

    public override string Description =>
        "按住指定鍵右鍵點物品，直接把它搬去對應頁面：背包⇄雇員、背包⇄部隊置物櫃、背包⇄陸行鳥鞍袋、" +
        "兵裝庫→背包。目的地沒有空位（或可疊的同款）時不動作並提示。預設「無」＝停用。";

    public override ModuleCategory Category => ModuleCategory.Inventory;

    public override bool HasConfigUI => true;

    /// <summary>可選的修飾鍵（0＝停用）。</summary>
    private static readonly (int Code, string Label)[] SelectableKeys =
    [
        (0, "無（停用）"),
        ((int)VirtualKey.SHIFT, "SHIFT"),
        ((int)VirtualKey.CONTROL, "CTRL"),
        ((int)VirtualKey.MENU, "ALT"),
    ];

    private delegate void OpenForItemSlotDelegate(
        AgentInventoryContext* agent, InventoryType inventoryType, int slot, int a4, uint addonId);

    private Hook<OpenForItemSlotDelegate>? openForItemSlotHook;

    /// <summary>
    /// 🔴 雇員存取走遊戲自己的道具命令，**不是**因為 <c>MoveItemSlot</c> 對雇員無效
    /// （見類別說明：那個結論的實機證據是在 <c>a6</c> 被省略的情況下量到的，已被推翻），
    /// 而是因為這條路徑是實機來回驗證過會動的，而 <c>MoveItemSlot(a6: true)</c> 對雇員
    /// **還沒有實機證據**。要換請先實測。
    ///
    /// 2026-07-31 實機 log（⚠️ 當時的呼叫**沒帶 a6**，所以封包根本沒送出）：
    /// 對 RetainerPage 呼叫 MoveItemSlot 會「成功」——回傳 0、
    /// 本機容器立刻更新、來源格清空 —— 但**伺服器從來沒收到**。決定性證據是 17:55 那批：
    ///   17:55:07  RetainerPage2#9 → Inventory1#20 itemId=45637 verified=True
    ///   17:55:47  RetainerPage2#9 → Inventory1#20 itemId=45637 verified=True   ← 40 秒後一模一樣
    /// 同一格、同一道具、同一目的地整批七筆重來，代表第一批完全沒生效、道具還在雇員身上，
    /// 只是本機狀態被樂觀改掉、直到下一次伺服器同步才還原。
    /// （所以先前那個 800ms 的延遲確認也抓不到——退回發生得比它晚太多。）
    ///
    /// 所以改走遊戲自己的雇員道具命令，也就是右鍵選單「取回／寄放」實際呼叫的函式。
    /// 特徵碼與 agent 取法都照抄 AutoRetainer（`Internal/Memory.cs`、`InventorySpaceManager.cs`），
    /// 那是**當天實測有效**的：log 裡 20:40 有 AutoRetainer 自己的自動化連續呼叫
    /// slot 0→6（由 NeoTaskManager 驅動，不是旁觀遊戲），證明特徵碼與 +40 偏移在台服 7.20 都對。
    /// </summary>
    private delegate void RetainerItemCommandDelegate(
        nint agentRetainerItemCommandModule, uint slot, InventoryType inventoryType,
        uint a4, RetainerItemCommand command);

    private const string RetainerItemCommandSig =
        "48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 48 83 EC 30 48 8B 5C 24 ?? 41 8B F0";

    private RetainerItemCommandDelegate? retainerItemCommand;

    private enum RetainerItemCommand : long
    {
        RetrieveFromRetainer = 0,
        EntrustToRetainer = 1,
    }

    /// <summary>
    /// 🔴 部隊置物櫃的正解：<c>AgentFreeCompanyChest::MoveItemInChest</c>。
    ///
    /// 我們 Dalamud 內建的 CS **沒有**這顆函式、也沒有 <c>AgentFreeCompanyChest</c> 這個結構
    /// （2026-08-01 實查：`MoveItemInChest` 在 CS 樹裡 0 個 .cs 命中，`AgentFreeCompanyChest`
    /// 只出現在 `ida/` 的中繼資料裡、0 個 .cs 命中；同一支 grep 對 `AgentInventoryContext`
    /// 有 83 個 .cs 命中，所以回 0 是真的沒有、不是查詢壞掉）。
    /// 因為不准動 CS pin（會波及全艦隊），這裡自己宣告 + 自己掃特徵碼。
    ///
    /// ── 以下每一項都是對台服 7.20 `ffxiv_dx11.exe` 離線鑑識得到的，不是照抄國服 ──
    ///
    /// 【函式本體】VA 0x14051D630（模組 RVA 0x51D630）。兩個候選特徵碼**各命中一次、
    /// 而且指向同一顆函式**：
    ///   上游 CS 的 `E9 ?? ?? ?? ?? 84 C0 75 5C` → 命中 0x14051C52E 的 E9 tail-call，跟隨後得 0x14051D630
    ///   DailyRoutines 的 `40 53 55 56 57 41 57 48 83 EC ?? 45 33 FF` → 直接命中 0x14051D630 的 prologue
    /// 我們選後者，理由見 <see cref="MoveItemInChestSig"/>。
    ///
    /// 【不是內聯掉的死碼】xref 四軸全查：E8 call ×3（0x1400F7478、0x14051CFB0、0x14051F04E）、
    /// E9 jmp ×1（0x14051C52E）、rip-relative lea ×0、絕對 8 位元組指標（vtable）×0 —— **共 4 個引用**。
    /// ⚠️ 對照組校準過：同一支掃描器對雇員道具命令那顆函式是 rel xref 0 但 .rdata 有 1 個 vtable 指標，
    /// 而那顆是當天實機驗證有效的 —— 證明四個軸都真的會命中，「回 0」不是查詢壞掉。
    ///
    /// 【參數形狀】反編譯逐一對上，共 5 個參數：
    ///   rcx=agent、edx=sourceInventory、r8d=sourceSlot、r9d=destinationInventory、
    ///   [rsp+0x80]=destinationSlot（5 個 push ＋ sub rsp,0x30 ＝ 0x58，加上進入時的 [rsp+0x28] 正好 0x80）。
    /// 函式內把 (edx, r8d) 與 (r9d, [rsp+0x80]) 分別餵給同一顆「取道具格」函式 0x140829CC0，
    /// 兩次呼叫的形狀就是來源／目的地各一組 (容器, 格號) —— 與上游宣告完全一致。
    ///
    /// 【身分確認】函式裡對 InventoryType 的常數比較全部對得上 CS 的列舉值：
    ///   0x7D1=2001 Crystals、0x55F1=22001 FreeCompanyCrystals、0x3E8=1000 EquippedItems、
    ///   `lea eax,[reg-0x4E20]; cmp eax,4; jbe` ＝ 20000..20004 亦即 FreeCompanyPage1..5。
    /// 這顆函式就是部隊置物櫃的搬移入口，沒有第二種解讀。
    ///
    /// 【agent 取法】呼叫點 0x1400F744F（那是**拖放**處理常式，也就是使用者手動拖得動的那條路）：
    ///   mov edx, 0x55            ; 0x55 = 85 = AgentId.FreeCompanyChest（我們 CS pin 裡也是 85）
    ///   call GetAgentByInternalId
    ///   mov rcx, rax             ; → MoveItemInChest 的 this
    /// 所以 `AgentModule::GetAgentByInternalId(AgentId.FreeCompanyChest)` 就是正確的 this，
    /// 我們的取法與遊戲自己**逐指令一致**，不是猜的。
    ///
    /// 【回傳值是 void】反編譯所有 return 路徑都沒有設定 eax。上游 CS 宣告 void 是對的；
    /// DailyRoutines 宣告成 `nint` 只是方便，那個值沒有意義，**不要拿它判斷成敗**。
    /// </summary>
    private delegate void MoveItemInChestDelegate(
        nint agent, InventoryType sourceInventory, uint sourceSlot,
        InventoryType destinationInventory, uint destinationSlot);

    /// <summary>
    /// 選 DailyRoutines 的函式本體 prologue，**不用**上游 CS 那條 `E9 ...` thunk 特徵碼。
    /// 兩條在台服 7.20 都是唯一命中、且指向同一位址，所以這是純粹的穩健度取捨：
    ///
    ///  1. 本體特徵碼只押在**這顆函式自己的 prologue** 上。thunk 那條押的是
    ///     「別的函式尾端的 tail-call」＋「再下一個基本區塊開頭的 4 個位元組」
    ///     （`84 C0 75 5C` ＝ test al,al / jnz +0x5C），也就是**同時**押在兩顆不相干函式的
    ///     碼產生結果上，改版時被打斷的面積比較大。
    ///  2. thunk 那條要靠 Dalamud `ScanText` 自動跟隨 E8/E9 位移才會落到本體
    ///     （`Dalamud/Game/SigScanner.cs:291-295`）。跟隨機制本身沒問題，但多一個環節就多一種
    ///     「解錯而且靜默」的可能。本體特徵碼首位元組是 0x40（REX，push rbx），
    ///     不會觸發跟隨，直接就是答案。
    ///
    /// ⚠️ 寫死的位元組樣式一律視為「下次改版必壞，而且靜默」。所以解析用
    /// <see cref="ISigScanner.ScanAllText(string)"/> 檢查**命中次數必須恰好是 1**，
    /// 不是 1 就拒絕安裝（見 <see cref="OnEnable"/>）—— 樣式變得不唯一時我們寧可整個功能不能用，
    /// 也不要去呼叫一顆碰巧長得像的函式。
    /// </summary>
    private const string MoveItemInChestSig = "40 53 55 56 57 41 57 48 83 EC ?? 45 33 FF";

    private MoveItemInChestDelegate? moveItemInChest;

    /// <summary>
    /// <c>AgentFreeCompanyChest</c> 裡記錄「右鍵點到的是哪一格」的兩個欄位。
    ///
    /// 數值來自 DailyRoutines（`OptimizedFreeCompanyChest.cs`，國服實測 6956/6960），
    /// 但**台服的二進位獨立佐證過**，不是照抄：
    ///   0x14051B6D8  mov dword ptr [rdi+0x1B2C], 0x270F   ; 0x270F = 9999 = InventoryType.Invalid
    ///   0x14051B6E2  mov word  ptr [rdi+0x1B30], bp       ; bp = 0
    /// 也就是 +0x1B2C 是「重設時填 InventoryType.Invalid」的 4 位元組容器欄位、
    /// +0x1B30 是 2 位元組格號 —— **大小與重設值都**跟 DR 宣告的
    /// `InventoryType ContextInventoryType` / `short ContextInventorySlot` 吻合。
    ///
    /// 而且它確實是「右鍵選單的目標」：右鍵選單的分派跳表（0x14051B8AB 的 `jmp rax`）其中一格是
    ///   0x14051B8AD  lea  rcx, [rbx+0x1B2C]   ; 把這組 (容器,格號) 交出去
    ///   0x14051B8B4  call 0x1401116D0         ; 解析成 InventoryItem*
    ///   0x14051B8BF  call 0x14051EE10         ; → 內部呼叫 MoveItemInChest(.., Invalid, 0)
    /// 處理完再把 +0x1B2C 寫回 0x270F、+0x1B30 寫回 0（0x14051B97F / 0x14051C44F）。
    ///
    /// 🔑 順帶一提，「台服的 agent 配置涵蓋得到這兩個偏移」因此是**證明**而不是假設 ——
    /// 台服自己的程式碼就在讀寫它們，不可能落在配置範圍外。所以這裡沒有越界讀取的風險。
    ///
    /// ⚠️ **仍然沒被證明的**是「我們在 OnMenuOpened 之後那一幀讀到的值，是這次右鍵的、
    /// 不是上一次殘留的」。這一點靠 <see cref="TryResolveChestSource"/> 的交叉比對擋住，不靠信任。
    /// </summary>
    private const int ChestContextInventoryTypeOffset = 0x1B2C;  // 6956

    /// <inheritdoc cref="ChestContextInventoryTypeOffset"/>
    private const int ChestContextInventorySlotOffset = 0x1B30;  // 6960

    /// <summary>雇員道具命令模組。⚠️ `+ 40` 是未文件化偏移，照抄 AutoRetainer 的實測值。</summary>
    private static nint GetAgentRetainerItemCommandModule()
    {
        var agentModule = AgentModule.Instance();
        if (agentModule == null) return 0;
        var agent = agentModule->GetAgentByInternalId(AgentId.Retainer);
        return agent == null ? 0 : (nint)agent + 40;
    }

    private static readonly InventoryType[] PlayerBags =
        [InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4];

    private static readonly InventoryType[] RetainerPages =
    [
        InventoryType.RetainerPage1, InventoryType.RetainerPage2, InventoryType.RetainerPage3,
        InventoryType.RetainerPage4, InventoryType.RetainerPage5, InventoryType.RetainerPage6,
        InventoryType.RetainerPage7,
    ];

    private static readonly InventoryType[] FreeCompanyPages =
    [
        InventoryType.FreeCompanyPage1, InventoryType.FreeCompanyPage2, InventoryType.FreeCompanyPage3,
        InventoryType.FreeCompanyPage4, InventoryType.FreeCompanyPage5,
    ];

    private static readonly InventoryType[] SaddleBags =
    [
        InventoryType.SaddleBag1, InventoryType.SaddleBag2,
        InventoryType.PremiumSaddleBag1, InventoryType.PremiumSaddleBag2,
    ];

    private static readonly InventoryType[] ArmoryContainers =
    [
        InventoryType.ArmoryMainHand, InventoryType.ArmoryOffHand, InventoryType.ArmoryHead,
        InventoryType.ArmoryBody, InventoryType.ArmoryHands, InventoryType.ArmoryWaist,
        InventoryType.ArmoryLegs, InventoryType.ArmoryFeets, InventoryType.ArmoryEar,
        InventoryType.ArmoryNeck, InventoryType.ArmoryWrist, InventoryType.ArmoryRings,
    ];

    /// <summary>同一格重複觸發的忽略視窗（毫秒）。</summary>
    private const int DuplicateGuardMs = 500;

    private InventoryType lastHandledSource = InventoryType.Invalid;
    private int lastHandledSlot = -1;
    private uint lastHandledItemId;
    private long lastHandledTick;

    private AutoInventoryTransferConfig Config => Plugin.Instance.Config.InventoryTransfer;

    /// <summary>
    /// 觀察器的兩種判準。⚠️ **兩條路徑的「成功長相」是相反的**，不能共用一套判斷。
    /// </summary>
    private enum VerificationKind
    {
        /// <summary>
        /// <c>MoveItemSlot</c> 用。它會**同步**改好本機狀態，所以呼叫完道具就已經不在來源格了；
        /// 要盯的是「伺服器稍後把它退回來」。
        /// </summary>
        MoveItemSlotRollback,

        /// <summary>
        /// <c>MoveItemInChest</c> 用。它是**非同步請求**：呼叫當下本機什麼都不會變，
        /// 要等伺服器回覆才會動。所以判準是反過來的 ——「來源格到底有沒有清空」。
        /// 🔴 不能沿用退回那一套：對這條路徑而言「道具還在來源格」在剛呼叫完是**正常**的，
        /// 拿它當退回會 100% 誤報成失敗。
        /// </summary>
        ChestDeparture,
    }

    /// <summary>
    /// 等伺服器確認的轉移。雇員／部隊置物櫃／鞍袋這三個容器伺服器有可能拒絕
    /// （另一名玩家正在用置物櫃、雇員 session 狀態…），所以本機狀態不等於最終狀態。
    /// </summary>
    private readonly record struct PendingVerification(
        VerificationKind Kind,
        InventoryType Source, int Slot,
        InventoryType Destination, int DestinationSlot,
        uint BaseItemId, string DisplayName, long StartTick, long DeadlineTick);

    private readonly List<PendingVerification> pendingVerifications = [];

    /// <summary>
    /// 🔴 觀察退回的時間長度。**不是**「等這麼久再看一眼」——是「持續盯到這麼久為止」。
    ///
    /// 原本寫 800ms 而且只在期限到的那一刻檢查一次，這是錯的工具：2026-08-01 實機那筆
    /// 部隊置物櫃存入，伺服器的拒絕（「無法保存道具，其他玩家正在使用儲物櫃。」）
    /// 是在 **5.1 秒後**才到的（02:04:52.166 搬移 → 02:04:57.281 錯誤訊息），
    /// 800ms 的窗口從頭到尾都在「還沒退回」的狀態，必然誤報成功。
    /// ⚠️ 這跟先前修雇員時踩過的是同一個坑，別再把它調回短窗口。
    /// </summary>
    private const int RollbackWatchMs = 12_000;

    /// <summary>
    /// 這次搬移需不需要盯伺服器退回。**兩邊都要看**：來源是這三個容器（取出）固然要，
    /// 目的地是這三個容器（存入）一樣要——原本只驗來源，所以「背包→部隊置物櫃」
    /// 這個方向完全不進確認流程，伺服器退回時我們早就印了「已轉移」。
    ///
    /// ⚠️ 這個判斷式原本叫 <c>IsServerAuthoritative</c>，那個名字建立在一個**已被推翻的**
    /// 因果模型上（「這些容器是伺服器權威的，所以 MoveItemSlot 只能假成功」——見類別說明，
    /// 真正的軸是 <c>a6</c> 旗標，而且**所有**容器的內容其實都是伺服器說了算）。
    /// 改成只描述呼叫端真正用它做的事：這三個容器伺服器**有可能拒絕**，值得盯著看會不會退回。
    /// 判斷內容一個字都沒動。
    /// </summary>
    private static bool NeedsRollbackWatch(InventoryType type)
        => Array.IndexOf(RetainerPages, type) >= 0
        || Array.IndexOf(FreeCompanyPages, type) >= 0
        || Array.IndexOf(SaddleBags, type) >= 0;

    protected override void OnEnable()
    {
        openForItemSlotHook = Svc.Hooks.HookFromAddress<OpenForItemSlotDelegate>(
            AgentInventoryContext.Addresses.OpenForItemSlot.Value, OpenForItemSlotDetour);
        openForItemSlotHook.Enable();

        // 解析不到就讓 retainerItemCommand 留 null，雇員轉移會明確告知而不是靜默走錯路徑。
        if (Svc.SigScanner.TryScanText(RetainerItemCommandSig, out var retainerCmdAddr))
        {
            retainerItemCommand =
                Marshal.GetDelegateForFunctionPointer<RetainerItemCommandDelegate>(retainerCmdAddr);
            Svc.Log.Information($"[{InternalName}] 雇員道具命令位址 0x{retainerCmdAddr:X}");
        }
        else
        {
            Svc.Log.Warning($"[{InternalName}] 找不到雇員道具命令的特徵碼，雇員轉移將無法使用。");
        }

        // 🔴 置物櫃搬移函式：**要求特徵碼恰好命中一次**，否則拒絕安裝。
        // 離線鑑識時在台服 7.20 是唯一命中（RVA 0x51D630），但寫死的位元組樣式一律當成
        // 「下次改版必壞而且靜默」——所以這裡把離線的唯一性結論改成執行期的閘門，
        // 而不是相信它會永遠成立。命中 0 或 ≥2 都寧可整個功能不能用。
        var chestHits = Svc.SigScanner.ScanAllText(MoveItemInChestSig);
        if (chestHits.Length == 1)
        {
            moveItemInChest =
                Marshal.GetDelegateForFunctionPointer<MoveItemInChestDelegate>(chestHits[0]);
            var rva = chestHits[0] - Svc.SigScanner.Module.BaseAddress;
            Svc.Log.Information(
                $"[{InternalName}] 置物櫃搬移函式位址 0x{chestHits[0]:X}（RVA 0x{rva:X}，" +
                $"離線鑑識預期 0x51D630、{(rva == 0x51D630 ? "相符" : "**不相符，請回報**")}）");
        }
        else
        {
            Svc.Log.Warning(
                $"[{InternalName}] 置物櫃搬移函式的特徵碼命中 {chestHits.Length} 次（需要剛好 1 次），" +
                "為了不呼叫到錯的函式，部隊置物櫃轉移已停用。");
        }

        // 第二個觸發點：部隊置物櫃不走 OpenForItemSlot，只有這條蓋得到（見類別說明）。
        Svc.ContextMenu.OnMenuOpened += OnMenuOpened;

        Svc.Framework.Update += OnUpdate;
    }

    protected override void OnDisable()
    {
        Svc.Framework.Update -= OnUpdate;
        Svc.ContextMenu.OnMenuOpened -= OnMenuOpened;
        openForItemSlotHook?.Dispose();
        openForItemSlotHook = null;
        moveItemInChest = null;

        pendingMenu = null;
        pendingVerifications.Clear();
        lastHandledSource = InventoryType.Invalid;
        lastHandledSlot = -1;
        lastHandledItemId = 0;
        lastHandledTick = 0;
    }

    /// <summary>
    /// 由 <see cref="OnMenuOpened"/> 記下、下一個 framework tick 才執行的右鍵選單。
    /// ⚠️ 不能在 OnMenuOpened 當下就做事：那時 ContextMenu addon 還沒開起來，
    /// <c>agent-&gt;AgentInterface.GetAddonId()</c> 拿到的是上一個選單（或 0）。
    ///
    /// <para><see cref="HoverBaseItemId"/>／<see cref="HoverHighQuality"/> 只在
    /// <see cref="IsChestHover"/> 為 true 時有意義，而且**必須在右鍵當下就抓**：
    /// 它是「使用者到底點了什麼」的唯一可信快照，延後一幀滑鼠可能已經移開了。</para>
    /// </summary>
    private readonly record struct PendingMenu(
        InventoryType Source, int Slot,
        bool IsChestHover, uint HoverBaseItemId, bool HoverHighQuality);

    private PendingMenu? pendingMenu;

    private void OnMenuOpened(IMenuOpenedArgs args)
    {
        if (Config.ModifierKeyCode == 0) return;

        if (args.MenuType == ContextMenuType.Inventory)
        {
            if (args.Target is not MenuTargetInventory inv || inv.TargetItem is not { } item)
            {
                Svc.Log.Information(
                    $"[{InternalName}] 收到道具右鍵選單（addon={args.AddonName ?? "?"}）但讀不到目標道具。");
                return;
            }

            // GameInventoryType 的數值與 InventoryType 完全一致（Inventory1=0 … FreeCompanyPage1=20000）。
            pendingMenu = new PendingMenu(
                (InventoryType)(ushort)item.ContainerType, (int)item.InventorySlot,
                IsChestHover: false, HoverBaseItemId: 0, HoverHighQuality: false);
            return;
        }

        // 🔴 部隊置物櫃的右鍵選單**不是道具選單**。
        // 2026-08-01 實機證實：Dalamud 只在 agent 是 AgentInventoryContext 時才標成
        // ContextMenuType.Inventory，而部隊置物櫃走的是 AgentContext（一般選單），
        // 所以 MenuType 是 Default、MenuTargetInventory 拿不到、`OpenForItemSlot` 也不會被呼叫
        // ——兩條路同時斷在同一個原因上，這就是它一直沒反應的真正理由。
        //
        // 一般選單給不了容器與格號，只好從「滑鼠正懸停在哪個道具上」反推。
        // ⚠️ 不去讀 AgentContext 的選單項目來點：那需要未經驗證的索引對應，
        //    而部隊置物櫃的選單裡有「丟棄」，點錯一格的代價太高。
        if (args.AddonName != "FreeCompanyChest") return;

        if (!TryResolveHoveredFreeCompanySlot(out var source, out var slot, out var hoverId, out var hoverHq))
        {
            Svc.Log.Information(
                $"[{InternalName}] 部隊置物櫃右鍵，但對不出懸停的格號（HoveredItem={Svc.GameGui.HoveredItem}）。");
            return;
        }

        pendingMenu = new PendingMenu(source, slot, IsChestHover: true, hoverId, hoverHq);
    }

    /// <summary>
    /// 用 <c>GameGui.HoveredItem</c> 反推部隊置物櫃裡的來源格。
    /// 同款道具有多份時取第一個——對「把這個拿出來」而言彼此可互換。
    /// <para>⚠️ 這是交叉驗證的 (B) 側。它精確到**道具**、不精確到格號，
    /// 所以真正拿去搬的格號取自 (A) 側，見 <see cref="TryResolveChestSource"/>。</para>
    /// </summary>
    private static bool TryResolveHoveredFreeCompanySlot(
        out InventoryType source, out int slot, out uint baseItemId, out bool highQuality)
    {
        source = InventoryType.Invalid;
        slot = -1;
        baseItemId = 0;
        highQuality = false;

        var hovered = Svc.GameGui.HoveredItem;
        if (hovered == 0) return false;

        // HoveredItem 的 HQ 是 +1000000（和 InventoryItem.ItemId 的編碼方式不同）。
        var wantedId = (uint)(hovered % 1_000_000);
        var wantHq = hovered >= 1_000_000;
        if (wantedId == 0) return false;

        var manager = InventoryManager.Instance();
        if (manager == null) return false;

        foreach (var page in FreeCompanyPages)
        {
            var container = manager->GetInventoryContainer(page);
            if (container == null || !container->IsLoaded) continue;

            for (var i = 0; i < container->Size; i++)
            {
                var item = container->GetInventorySlot(i);
                if (item == null || item->ItemId == 0) continue;
                if (item->GetBaseItemId() != wantedId) continue;
                if (((item->Flags & InventoryItem.ItemFlags.HighQuality) != 0) != wantHq) continue;

                source = page;
                slot = i;
                baseItemId = wantedId;
                highQuality = wantHq;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 決定「右鍵點的到底是置物櫃哪一格」。**兩個獨立來源都要成立、而且要互相吻合**才回 true：
    ///   (A) <c>AgentFreeCompanyChest</c> 的 +0x1B2C/+0x1B30（精確到格號，見
    ///       <see cref="ChestContextInventoryTypeOffset"/> 的鑑識紀錄）
    ///   (B) 右鍵當下 <c>GameGui.HoveredItem</c> 反推的道具（精確到道具，同款多份取第一個）
    ///
    /// 🔴 這個「必須一致」**不是保險絲，是這一版的核心安全設計**。
    /// 那兩個偏移雖然在台服二進位裡佐證過大小與重設值，但「我們讀到的是這次右鍵寫進去的、
    /// 而不是上一次的殘留或別的欄位」**無法離線證明**。
    /// 萬一它其實是別的東西，(A) 讀出來的會是不相干的容器／格號，算出來的道具 ID
    /// 幾乎不可能剛好等於使用者正懸停的那個 → 比對失敗 → 我們**什麼都不做**。
    /// 也就是說「偏移是錯的」的後果是**不動作**，不是搬錯道具。
    ///
    /// 三道白名單同時把「欄位還沒被填」擋成 fail-closed：
    ///   容器必須是 FreeCompanyPage1..5（重設值 <c>Invalid</c>(9999) 天然不通過）、
    ///   格號必須落在該容器的 Size 內、該格必須真的有東西。
    ///
    /// 一致時採用 (A) 的格號 —— 它精確，(B) 在同款多份時只能取第一個。
    /// </summary>
    private static bool TryResolveChestSource(
        nint agent, uint hoverBaseItemId, bool hoverHighQuality,
        out InventoryType source, out int slot, out string diagnosis)
    {
        source = InventoryType.Invalid;
        slot = -1;

        var agentType = (InventoryType)(*(uint*)(agent + ChestContextInventoryTypeOffset));
        var agentSlot = *(short*)(agent + ChestContextInventorySlotOffset);

        if (Array.IndexOf(FreeCompanyPages, agentType) < 0)
        {
            diagnosis = $"(A) 容器={agentType}({(uint)agentType}) 不是部隊置物櫃分頁";
            return false;
        }

        var manager = InventoryManager.Instance();
        if (manager == null)
        {
            diagnosis = "(A) 拿不到 InventoryManager";
            return false;
        }

        var container = manager->GetInventoryContainer(agentType);
        if (container == null || !container->IsLoaded)
        {
            diagnosis = $"(A) 容器 {agentType} 未載入";
            return false;
        }

        if (agentSlot < 0 || agentSlot >= container->Size)
        {
            diagnosis = $"(A) 格號 {agentSlot} 超出 {agentType} 範圍（Size={container->Size}）";
            return false;
        }

        var item = container->GetInventorySlot(agentSlot);
        if (item == null || item->ItemId == 0)
        {
            diagnosis = $"(A) {agentType}#{agentSlot} 是空格";
            return false;
        }

        var agentBaseItemId = item->GetBaseItemId();
        var agentHighQuality = (item->Flags & InventoryItem.ItemFlags.HighQuality) != 0;

        if (agentBaseItemId != hoverBaseItemId || agentHighQuality != hoverHighQuality)
        {
            diagnosis =
                $"(A) {agentType}#{agentSlot} 是 itemId={agentBaseItemId} hq={agentHighQuality}，" +
                $"但 (B) 懸停的是 itemId={hoverBaseItemId} hq={hoverHighQuality} —— 不一致";
            return false;
        }

        source = agentType;
        slot = agentSlot;
        diagnosis = $"(A)(B) 一致：{agentType}#{agentSlot} itemId={agentBaseItemId} hq={agentHighQuality}";
        return true;
    }

    private void OnUpdate(IFramework _)
    {
        if (pendingMenu is { } menu)
        {
            pendingMenu = null;
            var agent = AgentInventoryContext.Instance();
            if (agent != null)
            {
                try
                {
                    HandleContextMenu(agent, menu);
                }
                catch (Exception ex)
                {
                    Svc.Log.Error(ex, $"[{InternalName}] 處理延後的右鍵轉移時發生例外");
                }
            }
        }

        if (pendingVerifications.Count == 0) return;

        var now = Environment.TickCount64;
        var manager = InventoryManager.Instance();
        if (manager == null) return;

        for (var i = pendingVerifications.Count - 1; i >= 0; i--)
        {
            var p = pendingVerifications[i];
            var elapsed = now - p.StartTick;

            // ── MoveItemInChest：非同步請求，成功的長相是「道具離開來源格」 ──
            //
            // 🔑 這一組 log 就是判斷「只裝 MoveItemInChest 夠不夠」的依據
            //    （我們刻意沒裝 SendInventoryRefresh 的 op-lock hook，見 TransferViaChest 的說明）。
            //    看到「已生效」＝夠了；看到「逾時未生效」＝不夠，那時才需要重新評估 op-lock。
            if (p.Kind == VerificationKind.ChestDeparture)
            {
                if (!IsItemAt(manager, p.Source, p.Slot, p.BaseItemId))
                {
                    pendingVerifications.RemoveAt(i);

                    var landed = p.Destination != InventoryType.Invalid &&
                                 IsItemAt(manager, p.Destination, p.DestinationSlot, p.BaseItemId);
                    var where = p.Destination == InventoryType.Invalid
                        ? "（落點由遊戲決定）"
                        : $"{p.Destination}#{p.DestinationSlot} 落在指定格={landed}";

                    Svc.Log.Information(
                        $"[{InternalName}] 置物櫃搬移已生效：{p.Source}#{p.Slot} → {where} " +
                        $"itemId={p.BaseItemId} 耗時={elapsed}ms");

                    if (Config.NotifyOnTransfer)
                        Svc.Chat.Print($"[TC Toolbox] 已轉移「{p.DisplayName}」。");
                    continue;
                }

                if (now < p.DeadlineTick) continue;

                pendingVerifications.RemoveAt(i);
                Svc.Log.Warning(
                    $"[{InternalName}] 置物櫃搬移逾時未生效：{p.Source}#{p.Slot} itemId={p.BaseItemId} " +
                    $"——{RollbackWatchMs}ms 後道具仍在來源格，MoveItemInChest 沒有被伺服器受理。");

                if (Throttle.Pass("AutoInventoryTransfer-ChestTimeout", 3_000))
                    Svc.Chat.PrintError(
                        $"[TC Toolbox] 「{p.DisplayName}」沒有轉移成功（置物櫃沒有回應），請改用手動拖放。");
                continue;
            }

            // 🔴 每一幀都看，不是等到期限才看一眼。退回可能要好幾秒才回來，
            // 但也可能一秒內就回來——只在期限那一刻取樣，兩種都會漏。
            //
            // 兩個方向的退回長得不一樣，所以兩個都驗：
            //   取出被退回 → 道具**回到來源格**
            //   存入被退回 → 道具**從目的地格消失**（部隊置物櫃 2026-08-01 實機就是這個）
            var backAtSource = IsItemAt(manager, p.Source, p.Slot, p.BaseItemId);
            var goneFromDestination = !IsItemAt(manager, p.Destination, p.DestinationSlot, p.BaseItemId);

            if (backAtSource || goneFromDestination)
            {
                pendingVerifications.RemoveAt(i);
                Svc.Log.Warning(
                    $"[{InternalName}] 伺服器退回：{p.Source}#{p.Slot} → {p.Destination}#{p.DestinationSlot} " +
                    $"itemId={p.BaseItemId} 回到來源={backAtSource} 目的地消失={goneFromDestination} " +
                    $"（搬移後 {elapsed}ms）");

                if (Throttle.Pass("AutoInventoryTransfer-RolledBack", 3_000))
                    Svc.Chat.PrintError(
                        $"[TC Toolbox] 「{p.DisplayName}」沒有真的轉移過去（伺服器已退回），請改用手動拖放。");
                continue;
            }

            // 盯滿了都沒退回才算數，靜默移除（成功訊息在搬移當下就印過了）。
            if (now >= p.DeadlineTick)
                pendingVerifications.RemoveAt(i);
        }
    }

    /// <summary>
    /// 走遊戲自己的雇員道具命令。與 MoveItemSlot 不同，這條會真的送到伺服器，
    /// 落點也由遊戲決定（取回進背包空位／寄放進雇員空位）。
    /// </summary>
    private void TransferViaRetainerCommand(
        AgentInventoryContext* agent, InventoryType source, int slot,
        uint itemId, string displayName, bool retrieving)
    {
        if (retainerItemCommand == null)
        {
            if (Throttle.Pass("AutoInventoryTransfer-NoRetainerCmd", 3_000))
                Svc.Chat.PrintError($"[TC Toolbox] 找不到雇員道具命令，「{displayName}」未轉移，請改用手動拖放。");
            return;
        }

        var module = GetAgentRetainerItemCommandModule();
        if (module == 0)
        {
            if (Throttle.Pass("AutoInventoryTransfer-NoRetainerAgent", 3_000))
                Svc.Chat.PrintError($"[TC Toolbox] 雇員視窗未就緒，「{displayName}」未轉移。");
            return;
        }

        var command = retrieving
            ? RetainerItemCommand.RetrieveFromRetainer
            : RetainerItemCommand.EntrustToRetainer;

        Svc.Log.Debug($"[{InternalName}] 雇員命令 {command}：{source}#{slot} itemId={itemId}");
        retainerItemCommand(module, (uint)slot, source, 0, command);

        CloseContextMenu(agent);

        if (Config.NotifyOnTransfer)
            Svc.Chat.Print($"[TC Toolbox] 已{(retrieving ? "取回" : "寄放")}「{displayName}」。");
    }

    /// <summary>
    /// 🔴 鞍袋走遊戲自己的右鍵選單項目。它**不走**雇員道具命令 ——
    /// 2026-07-31 請使用者手動取出一次，AutoRetainer 的 RetainerItemCommand hook
    /// 全程沒有任何輸出，而道具確實從 InventoryBuddy 移到了 InventoryExpansion。
    ///
    /// 所以改成「點玩家自己會點的那個選單項目」。判準用 Addon 表的 row id 去查**客戶端
    /// 自己的字串**，不是寫死翻譯，所以跟語言無關：
    ///   881 = 放入陸行鳥鞍囊 / 887 = 從陸行鳥鞍囊中取回
    /// 索引方式照抄 Artisan `Tasks/TaskSelectRetainer.cs`（台服實測有效）：選單實際佔用
    /// EventParams[ContexItemStartIndex .. +ContextItemCount]，**不能掃完 98 格再數字串** ——
    /// 那樣會掃到上一次選單的殘留，算出來的序號也不是 callback 要的列號。
    /// </summary>
    private const uint AddonRowDepositToSaddlebag = 881;
    private const uint AddonRowRetrieveFromSaddlebag = 887;

    // ⚠️ 部隊置物櫃刻意不走 TryFireContextMenuEntry：它的右鍵選單是 AgentContext 的一般選單，
    // 而那個函式讀的是 AgentInventoryContext 的 EventParams，索引對不上；
    // 而且那個選單裡有「丟棄」，點錯一格的代價太高。置物櫃走 TransferViaChest。

    private bool TryFireContextMenuEntry(AgentInventoryContext* agent, uint addonRowId, string displayName)
    {
        var wanted = Svc.Data.GetExcelSheet<Addon>()?.GetRowOrDefault(addonRowId)?.Text.ExtractText().Trim();
        if (string.IsNullOrEmpty(wanted))
        {
            Svc.Log.Warning($"[{InternalName}] 讀不到 Addon#{addonRowId} 的字串，無法比對選單項目。");
            return false;
        }

        var startIndex = Math.Clamp(agent->ContexItemStartIndex, 0, 98);
        var itemCount = Math.Clamp(agent->ContextItemCount, 0, 98 - startIndex);

        var index = -1;
        var labels = new string[itemCount];
        for (var entry = 0; entry < itemCount; entry++)
        {
            var v = agent->EventParams[startIndex + entry];
            if (v.Type is not FFXIVClientStructs.FFXIV.Component.GUI.ValueType.String
                and not FFXIVClientStructs.FFXIV.Component.GUI.ValueType.ManagedString)
                continue;

            var ptr = v.String.Value;
            if (ptr == null) continue;
            labels[entry] = MemoryHelper.ReadSeStringNullTerminated((nint)ptr).TextValue.Trim();
            if (index == -1 && labels[entry] == wanted) index = entry;
        }

        // 選單長相一律記下來（Information 級，因為使用者的記錄等級會濾掉 DBG）。
        // 「▸」標的是被收進二級指令的項目——那是最可能讓找不到的原因，
        // 而且從錯誤訊息裡看不出來，只能靠這行分辨「沒有這個項目」與「在二級選單裡」。
        var dump = new string[itemCount];
        for (var entry = 0; entry < itemCount; entry++)
        {
            var inSubmenu = (agent->ContextItemSubmenuMask & (1u << entry)) != 0;
            dump[entry] = $"{entry}{(inSubmenu ? "▸" : "")}:{labels[entry]}";
        }

        Svc.Log.Information(
            $"[{InternalName}] 找「{wanted}」→ {(index == -1 ? "沒找到" : $"第 {index} 項")}；" +
            $"選單 {itemCount} 項（起點 EventParams[{startIndex}]，▸＝二級指令）：{string.Join(" | ", dump)}");

        if (index == -1)
        {
            if (Throttle.Pass("AutoInventoryTransfer-NoMenuEntry", 3_000))
                Svc.Chat.PrintError($"[TC Toolbox] 右鍵選單裡找不到「{wanted}」，「{displayName}」未轉移。");
            return false;
        }

        // 被收進次選單的項目不能直接用這個序號觸發（那是主選單的列號）。
        if ((agent->ContextItemSubmenuMask & (1u << index)) != 0)
        {
            Svc.Log.Warning($"[{InternalName}] 「{wanted}」在次選單裡（submenu mask），無法直接觸發。");
            if (Throttle.Pass("AutoInventoryTransfer-Submenu", 3_000))
                Svc.Chat.PrintError($"[TC Toolbox] 「{wanted}」被收在次選單裡，請改用手動拖放。");
            return false;
        }

        if (agent->IsContextItemDisabled(index))
        {
            Svc.Log.Warning($"[{InternalName}] 選單項目 {index}（{wanted}）是停用狀態。");
            if (Throttle.Pass("AutoInventoryTransfer-MenuDisabled", 3_000))
                Svc.Chat.PrintError($"[TC Toolbox] 「{wanted}」目前無法使用，「{displayName}」未轉移。");
            return false;
        }

        var addonId = agent->AgentInterface.GetAddonId();
        var addon = addonId == 0
            ? null
            : AtkStage.Instance()->RaptureAtkUnitManager->GetAddonById((ushort)addonId);
        if (addon == null)
        {
            Svc.Log.Warning($"[{InternalName}] 取不到右鍵選單 addon，「{displayName}」未轉移。");
            return false;
        }

        Svc.Log.Debug($"[{InternalName}] 觸發選單項目 {index}（{wanted}）給「{displayName}」");

        var values = stackalloc AtkValue[5];
        for (var i = 0; i < 5; i++)
        {
            values[i].Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int;
            values[i].Int = 0;
        }
        values[1].Int = index;
        addon->FireCallback(5, values, true);
        return true;
    }

    private void OpenForItemSlotDetour(
        AgentInventoryContext* agent, InventoryType inventoryType, int slot, int a4, uint addonId)
    {
        openForItemSlotHook!.Original(agent, inventoryType, slot, a4, addonId);

        try
        {
            HandleContextMenu(agent, new PendingMenu(
                inventoryType, slot, IsChestHover: false, HoverBaseItemId: 0, HoverHighQuality: false));
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, $"[{InternalName}] 處理右鍵轉移時發生例外");
        }
    }

    private void HandleContextMenu(AgentInventoryContext* agent, PendingMenu menu)
    {
        var source = menu.Source;
        var slot = menu.Slot;

        if (Config.ModifierKeyCode == 0) return;
        if (CSFramework.Instance()->WindowInactive) return;

        // 2026-08-01：這行救了一次診斷——部隊置物櫃右鍵時它**完全沒出現**，
        // 而它記在修飾鍵檢查之前，所以直接證明是「hook 根本沒被呼叫」而不是「修飾鍵沒按到」，
        // 才找到 OpenForItemSlot 不是置物櫃入口這件事。留著，並改成 Information
        // （使用者的記錄等級會濾掉 DBG，DBG 只是這台機器剛好開著）。只有右鍵才觸發，不會洗版。
        var modifierHeld = Svc.Keys[(VirtualKey)Config.ModifierKeyCode];
        Svc.Log.Information($"[{InternalName}] 右鍵選單開啟：{source}#{slot} 修飾鍵={(modifierHeld ? "有按" : "沒按")}");

        if (!modifierHeld) return;

        // 🔴 置物櫃「取出」方向：右鍵選單給不了格號，所以在動任何東西之前先做 (A)(B) 交叉驗證。
        // 兩邊算出來的道具不一致就整段放棄 —— 寧可不動作，也不要搬錯道具。
        if (menu.IsChestHover)
        {
            // ⚠️ 一定要先確定 agent 非 null 再去讀它的欄位。
            // 少了這一步就會變成對 0x1B2C 這種低位址解參考，那是自找的存取違規。
            var chestAgent = ResolveFreeCompanyChestAgent();
            if (chestAgent == null)
            {
                Svc.Log.Information($"[{InternalName}] 置物櫃 agent 未就緒，不動作。");
                return;
            }

            if (!TryResolveChestSource(
                    (nint)chestAgent, menu.HoverBaseItemId, menu.HoverHighQuality,
                    out var chestSource, out var chestSlot, out var diagnosis))
            {
                Svc.Log.Information($"[{InternalName}] 置物櫃來源格交叉驗證失敗，不動作：{diagnosis}");
                return;
            }

            Svc.Log.Information($"[{InternalName}] 置物櫃來源格交叉驗證通過：{diagnosis}");
            source = chestSource;
            slot = chestSlot;
        }

        var manager = InventoryManager.Instance();
        if (manager == null) return;

        var item = manager->GetInventorySlot(source, slot);

        // 2026-07-31：部隊置物櫃「取出」完全沒有任何輸出（存入正常），代表在這之前就 return 了，
        // 不是搬移失敗。這行把遊戲實際傳進來的容器與格號記下來，才能分辨是
        //「hook 沒被呼叫」還是「讀不到那一格」。右鍵才會觸發，不會洗版。
        if (item == null || item->ItemId == 0)
        {
            Svc.Log.Debug($"[{InternalName}] 讀不到來源格：{source}#{slot}（item={(item == null ? "null" : "ItemId=0")}）");
            return;
        }

        // ⚠️ 道具識別資料一定要在 MoveItemSlot 之前抓下來：MoveItemSlot 會同步清空來源格，
        // 之後再讀這個指標只會拿到空欄位（Item sheet 的 row 0 是有效列但名稱為空字串，
        // 所以連 null 判斷都不會觸發，訊息就變成「已轉移『』」）。
        var itemId = item->ItemId;
        var baseItemId = item->GetBaseItemId();
        var isHighQuality = (item->Flags & InventoryItem.ItemFlags.HighQuality) != 0;
        var displayName = ResolveItemName(baseItemId, isHighQuality);

        // 同一格、同一道具在極短時間內重複觸發只處理一次（防手滑連點與任何再進入路徑）
        if (lastHandledSource == source && lastHandledSlot == slot && lastHandledItemId == itemId &&
            Environment.TickCount64 - lastHandledTick < DuplicateGuardMs)
        {
            Svc.Log.Debug($"[{InternalName}] 略過重複觸發：{source}#{slot} itemId={itemId}");
            return;
        }

        lastHandledSource = source;
        lastHandledSlot = slot;
        lastHandledItemId = itemId;
        lastHandledTick = Environment.TickCount64;

        // 🔴 雇員方向優先走遊戲自己的道具命令（見 RetainerItemCommandDelegate 的說明）。
        // 這條路徑由遊戲決定落點，所以不需要我們自己挑目的地格。
        var sourceIsRetainer = Array.IndexOf(RetainerPages, source) >= 0;
        var retainerWindowOpen = GetAgentRetainerItemCommandModule() != 0
            && (Svc.GameGui.GetAddonByName("InventoryRetainer", 1).Address != nint.Zero
                || Svc.GameGui.GetAddonByName("InventoryRetainerLarge", 1).Address != nint.Zero);

        if (sourceIsRetainer || (retainerWindowOpen && Array.IndexOf(PlayerBags, source) >= 0))
        {
            TransferViaRetainerCommand(agent, source, slot, itemId, displayName, sourceIsRetainer);
            return;
        }

        // 🔴 鞍袋方向走右鍵選單（見 TryFireContextMenuEntry）——實機驗證過會動的那條。
        // ⚠️ 不要改成 MoveItemSlot：即使帶了 a6: true 也**沒有實機證據**，先測再說。
        var sourceIsSaddleBag = Array.IndexOf(SaddleBags, source) >= 0;
        var saddleBagWindowOpen = Svc.GameGui.GetAddonByName("InventoryBuddy", 1).Address != nint.Zero;

        if (sourceIsSaddleBag || (saddleBagWindowOpen && Array.IndexOf(PlayerBags, source) >= 0))
        {
            if (TryFireContextMenuEntry(
                    agent,
                    sourceIsSaddleBag ? AddonRowRetrieveFromSaddlebag : AddonRowDepositToSaddlebag,
                    displayName)
                && Config.NotifyOnTransfer)
            {
                Svc.Chat.Print($"[TC Toolbox] 已{(sourceIsSaddleBag ? "取回" : "放入")}「{displayName}」。");
            }
            return;
        }

        // 🔴 部隊置物櫃：走 AgentFreeCompanyChest::MoveItemInChest（見 MoveItemInChestDelegate）。
        //
        // 2026-08-01 實機：**沒帶 a6 的** MoveItemSlot 在這個容器上兩個方向都被退回。
        //   02:48:59 FreeCompanyPage1#0 → Inventory1#26  verified=True
        //   02:49:09 伺服器退回：回到來源=True（10625ms）
        //   02:49:39 FreeCompanyPage1#0 → Inventory1#5   verified=True
        //   02:49:43 伺服器退回：回到來源=True（3922ms）
        //   存入方向同樣連兩次被退回（置物櫃歷史查無該筆，是真退回不是誤判）。
        // ⚠️ 這批紀錄證明的是「封包沒送出」（a6 省略＝預設 false，見類別說明），
        //    **不是**「置物櫃拒絕 MoveItemSlot」。歸因已修正，但這條路徑照舊保留：
        //    它是實機來回驗證過會動的，而 MoveItemSlot(a6: true) 對置物櫃沒有實機證據。
        //
        // ⚠️ 兩條已經排除、不要再回頭嘗試的路：
        //  - `ExecuteCommand(405)`：台服二進位反證。405 在整個 .text 只有 2 個呼叫點、
        //    **兩個都是 param2=0**，而且失敗路徑印的是 LogMessage #1860「獲得公會儲物櫃資料失敗。」
        //    ——405 是「請求載入置物櫃頁面資料」的前置動作，不是搬移手段。
        //  - 點原生右鍵選單項目：那是 AgentContext 的一般選單，索引基準有兩個互相矛盾的來源
        //    （Dalamud 的表頭算 7 格、OmenTools 讀 [i+8]），而**那個選單裡有「丟棄」**，
        //    差 1 就是點到隔壁那項。DailyRoutines 與 FCCH 兩個獨立實作也都刻意不走這條。
        var sourceIsChest = Array.IndexOf(FreeCompanyPages, source) >= 0;
        if (sourceIsChest
            || (Array.IndexOf(PlayerBags, source) >= 0 && UiHelper.IsAddonReady("FreeCompanyChest")))
        {
            TransferViaChest(agent, source, slot, baseItemId, displayName, sourceIsChest);
            return;
        }

        if (!TryResolveDestination(source, out var candidates, out var reason))
        {
            if (reason.Length > 0 && Throttle.Pass("AutoInventoryTransfer-NoDest", 3_000))
                Svc.Chat.Print($"[TC Toolbox] {reason}");
            return;
        }

        if (!TryFindTargetSlot(manager, candidates, item, out var destination, out var destinationSlot))
        {
            if (Throttle.Pass("AutoInventoryTransfer-Full", 3_000))
                Svc.Chat.PrintError($"[TC Toolbox] 目的地沒有空位也沒有可疊的同款道具，「{displayName}」未轉移。");
            return;
        }

        // 🔴 <c>a6: true</c> 絕對不能省。省略＝預設 false＝遊戲只改本機容器、**一個封包都不送**，
        // 畫面上道具會動但伺服器不知道，下次同步就彈回原處（見類別說明的旗標語意）。
        // 這條路徑實務上只有「兵裝庫→背包」走得到（雇員／鞍袋／置物櫃都在上面提早 return），
        // 而該路徑實機命中 0 次，所以這個缺陷從來沒有人回報過——不是它沒壞，是沒人走過。
        var result = manager->MoveItemSlot(source, (ushort)slot, destination, (ushort)destinationSlot, a6: true);

        // 立即檢查：MoveItemSlot 會同步更新本機容器（a6 只影響送不送封包，不影響本機同步更新），
        // 所以這一步能擋掉「呼叫當下就沒成功」。
        // 兩邊都驗：疊加到既有堆疊時目的地本來就有同款道具，只驗目的地會恆真，
        // 所以真正的判準是「來源格已經不是這個道具了」。
        var moved = result == 0 &&
                    !IsItemAt(manager, source, slot, baseItemId) &&
                    IsItemAt(manager, destination, destinationSlot, baseItemId);

        Svc.Log.Debug($"[{InternalName}] {source}#{slot} → {destination}#{destinationSlot} " +
                      $"itemId={itemId} result={result} verified={moved}");

        if (!moved)
        {
            if (Throttle.Pass("AutoInventoryTransfer-Failed", 3_000))
                Svc.Chat.PrintError($"[TC Toolbox] 「{displayName}」轉移失敗（遊戲回傳 {result}），請改用手動拖放。");
            return;
        }

        CloseContextMenu(agent);

        if (Config.NotifyOnTransfer)
            Svc.Chat.Print($"[TC Toolbox] 已轉移「{displayName}」。");

        // 🔴 立即檢查通過「不代表真的搬過去了」。本機容器是同步就改好的，
        // 但封包送出後伺服器仍可能拒絕（雇員／部隊置物櫃／鞍袋），道具會彈回原處
        // —— 而我們早就印了「已轉移」。
        //
        // ⚠️ 原本這裡引用 AutoDuty AutoEquipHelper「呼叫後立刻回讀就能判定」當依據，
        //    那個先例搬的是裝備欄↔兵裝庫，伺服器實務上不會拒絕，所以回讀就夠。
        //    會被別人搶用的容器（置物櫃）或有 session 狀態的容器（雇員）不適用。
        //
        // 上面的「已轉移」照樣先印（畫面上道具確實動了，不印反而更困惑），
        // 真的被退回時再補一則錯誤訊息蓋掉它。
        if (NeedsRollbackWatch(source) || NeedsRollbackWatch(destination))
        {
            var startTick = Environment.TickCount64;
            pendingVerifications.Add(new PendingVerification(
                VerificationKind.MoveItemSlotRollback,
                source, slot, destination, destinationSlot, baseItemId, displayName,
                startTick, startTick + RollbackWatchMs));
        }
    }

    /// <summary>
    /// 取部隊置物櫃 agent。取法與遊戲自己的拖放處理常式逐指令一致
    /// （0x1400F7456 `mov edx, 0x55` → GetAgentByInternalId → 當成 MoveItemInChest 的 this）。
    /// </summary>
    private static AgentInterface* ResolveFreeCompanyChestAgent()
    {
        var agentModule = AgentModule.Instance();
        if (agentModule == null) return null;

        var agent = agentModule->GetAgentByInternalId(AgentId.FreeCompanyChest);
        return agent == null || !agent->IsAgentActive() ? null : agent;
    }

    /// <summary>
    /// 用 <c>AgentFreeCompanyChest::MoveItemInChest</c> 搬移。兩個方向的呼叫形狀**不一樣**，
    /// 而且兩種都是照抄遊戲自己的呼叫點，不是自己發明的：
    ///
    ///  - **取出**（置物櫃 → 背包）：目的地填 <c>InventoryType.Invalid</c>(9999) 與格號 0，
    ///    由**遊戲**決定落在背包哪一格。這正是遊戲右鍵選單處理常式的做法
    ///    （0x14051F03C `mov r9d, 0x270F`、`[rsp+0x20] = 0`）。
    ///    🔑 我們刻意**不自己挑背包空格**：少挑一次就少一次挑錯的機會。
    ///  - **存入**（背包 → 置物櫃）：必須給實際的 (分頁, 格號)，這是遊戲**拖放**處理常式的形狀
    ///    （0x1400F7465-0x1400F7478）。落點沿用既有的 <see cref="TryFindTargetSlot"/>，
    ///    它只會選「可疊的同款」或「真正的空格」，不會挑到別人的道具上去覆蓋。
    ///
    /// ⚠️ **這一版刻意不安裝 <c>SendInventoryRefresh</c> 的 op-lock hook。**
    /// DailyRoutines 與 FreeCompanyChestHelper 兩邊都有裝，但那個 hook 是**取代原函式**
    /// （detour 裡不呼叫 Original），而我們**無法離線證明**「拿掉它就搬不動」或「裝了它沒有副作用」。
    /// 在還沒有證據以前就去攔截遊戲的庫存刷新，風險比它可能解決的問題大。
    /// 所以先只裝 MoveItemInChest ——「這樣是不是已經夠了」可以用**一次實機操作**證偽，
    /// 那是能把未知數收斂掉的最小一步。判斷依據是 <see cref="VerificationKind.ChestDeparture"/>
    /// 那組 log：「已生效」＝夠了；「逾時未生效」＝不夠，那時才重新評估 op-lock。
    /// </summary>
    private void TransferViaChest(
        AgentInventoryContext* menuAgent, InventoryType source, int slot,
        uint baseItemId, string displayName, bool withdrawing)
    {
        if (moveItemInChest == null)
        {
            if (Throttle.Pass("AutoInventoryTransfer-NoChestFn", 3_000))
                Svc.Chat.PrintError($"[TC Toolbox] 找不到置物櫃搬移函式，「{displayName}」未轉移，請改用手動拖放。");
            return;
        }

        var chestAgent = ResolveFreeCompanyChestAgent();
        if (chestAgent == null)
        {
            Svc.Log.Warning($"[{InternalName}] 置物櫃 agent 未就緒，「{displayName}」未轉移。");
            if (Throttle.Pass("AutoInventoryTransfer-NoChestAgent", 3_000))
                Svc.Chat.PrintError($"[TC Toolbox] 部隊置物櫃視窗未就緒，「{displayName}」未轉移。");
            return;
        }

        InventoryType destination;
        int destinationSlot;

        if (withdrawing)
        {
            destination = InventoryType.Invalid;
            destinationSlot = 0;
        }
        else
        {
            if (!TryResolveDestination(source, out var candidates, out var reason))
            {
                if (reason.Length > 0 && Throttle.Pass("AutoInventoryTransfer-NoDest", 3_000))
                    Svc.Chat.Print($"[TC Toolbox] {reason}");
                return;
            }

            var manager = InventoryManager.Instance();
            var sourceItem = manager == null ? null : manager->GetInventorySlot(source, slot);
            if (manager == null || sourceItem == null ||
                !TryFindTargetSlot(manager, candidates, sourceItem, out destination, out destinationSlot))
            {
                if (Throttle.Pass("AutoInventoryTransfer-Full", 3_000))
                    Svc.Chat.PrintError($"[TC Toolbox] 部隊置物櫃沒有空位也沒有可疊的同款道具，「{displayName}」未轉移。");
                return;
            }
        }

        // 遊戲自己在轉呼叫這顆函式之前也做同一道白名單檢查（0x1400F7429-0x1400F744D）：
        // 來源或目的地必須是置物櫃分頁，否則就不該走這條路。免費的 fail-closed，照抄。
        if (Array.IndexOf(FreeCompanyPages, source) < 0 &&
            Array.IndexOf(FreeCompanyPages, destination) < 0)
        {
            Svc.Log.Warning($"[{InternalName}] {source} → {destination} 兩邊都不是置物櫃分頁，放棄。");
            return;
        }

        Svc.Log.Information(
            $"[{InternalName}] 置物櫃搬移送出（{(withdrawing ? "取出" : "存入")}）：{source}#{slot} → " +
            $"{(withdrawing ? "（落點由遊戲決定）" : $"{destination}#{destinationSlot}")} itemId={baseItemId}");

        moveItemInChest((nint)chestAgent, source, (uint)slot, destination, (uint)destinationSlot);

        CloseContextMenu(menuAgent);

        // ⚠️ 這裡**故意不印**「已轉移」。MoveItemInChest 是非同步請求，呼叫當下本機什麼都還沒變，
        // 先報成功就是重蹈先前「本機動了就宣告成功」的覆轍。
        // 成功訊息改由 ChestDeparture 觀察器在道具真的離開來源格時才印。
        var now = Environment.TickCount64;
        pendingVerifications.Add(new PendingVerification(
            VerificationKind.ChestDeparture,
            source, slot, destination, destinationSlot, baseItemId, displayName,
            now, now + RollbackWatchMs));
    }

    /// <summary>道具名稱一律走 Lumina Item 表（台服自帶繁中），不讀 addon 上的文字。</summary>
    private static string ResolveItemName(uint baseItemId, bool isHighQuality)
    {
        var row = Svc.Data.GetExcelSheet<Item>().GetRowOrDefault(baseItemId);
        var name = row?.Name.ExtractText() ?? string.Empty;

        // Item 表的 row 0 是有效列但名稱為空，所以不能只判斷 null
        if (string.IsNullOrEmpty(name))
            return $"#{baseItemId}";

        return isHighQuality ? $"{name} {(char)SeIconChar.HighQuality}" : name;
    }

    private static bool IsItemAt(InventoryManager* manager, InventoryType type, int slot, uint baseItemId)
    {
        var item = manager->GetInventorySlot(type, slot);
        return item != null && item->GetBaseItemId() == baseItemId;
    }

    /// <summary>依來源容器與目前開著的視窗決定目的地候選（依序嘗試）。</summary>
    private static bool TryResolveDestination(
        InventoryType source, out List<InventoryType> candidates, out string reason)
    {
        candidates = [];
        reason = string.Empty;

        // 雇員／部隊置物櫃／鞍袋 → 背包
        if (Array.IndexOf(RetainerPages, source) >= 0 ||
            Array.IndexOf(FreeCompanyPages, source) >= 0 ||
            Array.IndexOf(SaddleBags, source) >= 0 ||
            Array.IndexOf(ArmoryContainers, source) >= 0)
        {
            candidates.AddRange(PlayerBags);
            return true;
        }

        if (Array.IndexOf(PlayerBags, source) < 0) return false;

        // 背包 → 目前開著的那個容器
        if (UiHelper.IsAddonReady("InventoryRetainer") || UiHelper.IsAddonReady("InventoryRetainerLarge"))
        {
            candidates.AddRange(RetainerPages);
            return true;
        }

        if (UiHelper.IsAddonReady("FreeCompanyChest"))
        {
            // 優先送到目前顯示的那一頁（AtkValues[1]!=0＝水晶頁，不接受一般道具）
            var chest = UiHelper.GetAddon("FreeCompanyChest");
            if (chest != null && chest->AtkValuesCount > 2)
            {
                if (chest->AtkValues[1].UInt != 0)
                {
                    reason = "部隊置物櫃目前在水晶頁，請切到道具頁再轉移。";
                    return false;
                }

                var pageIndex = (int)chest->AtkValues[2].UInt;
                if (pageIndex >= 0 && pageIndex < FreeCompanyPages.Length)
                    candidates.Add(FreeCompanyPages[pageIndex]);
            }

            foreach (var page in FreeCompanyPages)
            {
                if (!candidates.Contains(page)) candidates.Add(page);
            }

            return true;
        }

        if (UiHelper.IsAddonReady("InventoryBuddy"))
        {
            candidates.AddRange(SaddleBags);
            return true;
        }

        reason = "沒有開著可互轉的頁面（雇員、部隊置物櫃或陸行鳥鞍袋）。";
        return false;
    }

    /// <summary>找目的地的落點：優先可疊的同款道具，其次空格。</summary>
    private static bool TryFindTargetSlot(
        InventoryManager* manager,
        List<InventoryType> candidates,
        InventoryItem* source,
        out InventoryType destination,
        out int destinationSlot)
    {
        destination = InventoryType.Invalid;
        destinationSlot = -1;

        var sheetItem = Svc.Data.GetExcelSheet<Item>().GetRowOrDefault(source->GetBaseItemId());
        var stackSize = sheetItem?.StackSize ?? 1u;

        // 先找可以疊上去的
        if (stackSize > 1)
        {
            foreach (var type in candidates)
            {
                var container = manager->GetInventoryContainer(type);
                if (container == null || !container->IsLoaded) continue;

                for (var i = 0; i < container->Size; i++)
                {
                    var slot = container->GetInventorySlot(i);
                    if (slot == null || slot->ItemId == 0) continue;
                    if (slot->GetBaseItemId() != source->GetBaseItemId()) continue;
                    if (slot->Flags != source->Flags) continue;
                    if (slot->Quantity + source->Quantity > stackSize) continue;

                    destination = type;
                    destinationSlot = i;
                    return true;
                }
            }
        }

        foreach (var type in candidates)
        {
            var container = manager->GetInventoryContainer(type);
            if (container == null || !container->IsLoaded) continue;

            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot == null || slot->ItemId != 0) continue;

                destination = type;
                destinationSlot = i;
                return true;
            }
        }

        return false;
    }

    private static void CloseContextMenu(AgentInventoryContext* agent)
    {
        // 兩種選單都要關得掉：道具選單掛在 AgentInventoryContext，
        // 部隊置物櫃那種一般選單掛在 AgentContext。
        // ⚠️ 原本只問 AgentInventoryContext 要 addon id，一般選單那條路拿到 0 就直接 return，
        // 所以實機看到的是「道具搬走了但右鍵選單還開著」。
        var addonId = agent->AgentInterface.GetAddonId();
        AgentInterface* owner = &agent->AgentInterface;

        if (addonId == 0)
        {
            var agentContext = AgentContext.Instance();
            if (agentContext == null) return;
            addonId = agentContext->AgentInterface.GetAddonId();
            owner = &agentContext->AgentInterface;
        }

        if (addonId == 0) return;

        var addon = AtkStage.Instance()->RaptureAtkUnitManager->GetAddonById((ushort)addonId);
        if (addon == null) return;

        owner->Hide();
        addon->Close(true);
    }

    public override void DrawConfig()
    {
        var currentIndex = 0;
        for (var i = 0; i < SelectableKeys.Length; i++)
        {
            if (SelectableKeys[i].Code == Config.ModifierKeyCode) currentIndex = i;
        }

        ImGui.SetNextItemWidth(180f);
        if (ImGui.BeginCombo("觸發鍵", SelectableKeys[currentIndex].Label))
        {
            for (var i = 0; i < SelectableKeys.Length; i++)
            {
                if (!ImGui.Selectable(SelectableKeys[i].Label, i == currentIndex)) continue;
                Config.ModifierKeyCode = SelectableKeys[i].Code;
                Plugin.Instance.Config.Save();
            }

            ImGui.EndCombo();
        }

        var notify = Config.NotifyOnTransfer;
        if (ImGui.Checkbox("轉移後顯示聊天訊息", ref notify))
        {
            Config.NotifyOnTransfer = notify;
            Plugin.Instance.Config.Save();
        }

        ImGui.TextColored(new System.Numerics.Vector4(1f, 0.8f, 0.35f, 1f),
                          "⚠ 觸發鍵請避開 Marketbuddy 的「快速上架」與 AutoRetainer 的「快速販賣」鍵：\n" +
                          "三者都掛在同一個右鍵事件上，設成同一顆鍵會同時動作。");

        ImGui.TextDisabled("兵裝庫只支援「兵裝庫→背包」方向；反向請照常用裝備／整理流程。");
    }
}

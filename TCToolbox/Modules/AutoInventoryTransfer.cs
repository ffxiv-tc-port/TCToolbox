using System;
using System.IO;
using Dalamud.Memory;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
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
/// </summary>
/// <remarks>
/// 🔴 僱員／鞍袋／公會儲物櫃各走專用路徑，都是實機來回驗證過會動的；「改用 <c>MoveItemSlot(a6: true)</c> 也能動」**只是推論**，要換請先實測：
///  - **僱員**：遊戲自己的僱員道具命令（<see cref="RetainerItemCommandDelegate"/>）。
///  - **鞍袋**：點遊戲右鍵選單自己的項目（<see cref="TryFireContextMenuEntry"/>），**不走**僱員道具命令。
///  - **公會儲物櫃**：<c>AgentFreeCompanyChest::MoveItemInChest</c>（<see cref="MoveItemInChestDelegate"/>）；置物櫃裡的右鍵不經過 <c>OpenForItemSlot</c>，由 <see cref="OnMenuOpened"/> 接。
///  - **兵裝庫**：<c>InventoryManager.MoveItemSlot</c>，**一定要帶 <c>a6: true</c>**（省略＝只改本機、不送封包）。
/// 參考 DailyRoutines AutoInventoryTransfer 的用途重寫（API13、無 OmenTools 相依）。
/// </remarks>
public sealed unsafe partial class AutoInventoryTransfer : TcModule
{
    public override string InternalName => "AutoInventoryTransfer";
    public override string DisplayName => "自動物品頁面轉移";

    public override string Description =>
        "按住指定鍵右鍵點物品，直接把它搬去對應頁面：背包⇄僱員、背包⇄公會儲物櫃、背包⇄陸行鳥鞍袋、" +
        "兵裝庫→背包。目的地沒有空位（或可疊的同款）時不動作並提示。預設「無」＝停用。";

    public override ModuleCategory Category => ModuleCategory.Inventory;

    public override bool HasConfigUI => true;

    /// <summary>
    /// SimpleTweaks 裡掛在<b>同一支遊戲函式</b>上的 tweak 鍵（裸鍵，沒有提供者前綴）。
    /// </summary>
    /// <remarks>
    /// 🔴 而且<b>互動方式一模一樣</b>：對方有 <c>HotkeyIsHeld</c>，也是「按住修飾鍵右鍵點物品」。
    /// 兩邊的修飾鍵若設成同一顆，同一個右鍵動作會同時觸發賣出與轉移。
    /// </remarks>
    private const string SimpleTweaksQuickSellTweak = "QuickSellItems";

    /// <inheritdoc/>
    public override ModuleNotice? RowNotice => SimpleTweaksProbe.BuildNotice(
        SimpleTweaksQuickSellTweak,
        "快速賣出道具 (QuickSellItems)",
        "兩邊掛的是遊戲同一支背包右鍵函式（AgentInventoryContext::OpenForItemSlot），\n" +
        "而且觸發方式一樣是「按住修飾鍵 + 右鍵點物品」。\n" +
        "\n" +
        "🔴 如果兩邊的修飾鍵設成同一顆，同一個右鍵動作會同時被兩邊接手——\n" +
        "對方把道具賣掉、我們把它搬去另一個頁面，而先發生哪一件並不保證。\n" +
        "\n" +
        "要兩個都留的話，請把修飾鍵設成不同的鍵（本模組的修飾鍵在下方設定裡）。");

    /// <summary>可選的修飾鍵（0＝停用）。</summary>
    private static readonly (int Code, string Label)[] SelectableKeys =
    [
        (0, "無（停用）"),
        ((int)VirtualKey.SHIFT, "SHIFT"),
        ((int)VirtualKey.CONTROL, "CTRL"),
        ((int)VirtualKey.MENU, "ALT"),
    ];

    private static readonly VirtualKey[] ModifierGroups = [VirtualKey.SHIFT, VirtualKey.CONTROL, VirtualKey.MENU];

    /// <summary>Shift／Ctrl／Alt（含左右鍵）歸到通用鍵碼；其他鍵回 0。</summary>
    private static int ModifierGroup(int vk) => vk switch
    {
        0x10 or 0xA0 or 0xA1 => 0x10,
        0x11 or 0xA2 or 0xA3 => 0x11,
        0x12 or 0xA4 or 0xA5 => 0x12,
        _ => 0,
    };

    /// <summary>Shift／Ctrl／Alt 中，<paramref name="ownKeyCode"/> 那一組以外的有沒有按著。</summary>
    private static bool OtherModifierHeld(int ownKeyCode)
    {
        var own = ModifierGroup(ownKeyCode);
        foreach (var group in ModifierGroups)
        {
            if ((int)group != own && Svc.Keys[group]) return true;
        }

        return false;
    }

    /// <summary>
    /// 🔴 僱員存取走遊戲自己的道具命令：這條路徑是實機來回驗證過會動的，
    /// 而 <c>MoveItemSlot(a6: true)</c> 對僱員**還沒有實機證據**。要換請先實測。
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
    /// 🔴 公會儲物櫃的正解：<c>AgentFreeCompanyChest::MoveItemInChest</c>。
    /// 我們 Dalamud 內建的 CS **沒有**這顆函式、也沒有 <c>AgentFreeCompanyChest</c> 這個結構；
    /// 因為不准動 CS pin（會波及全艦隊），這裡自己宣告 + 自己掃特徵碼。
    /// 【回傳值是 void】反編譯所有 return 路徑都沒有設定 eax。上游 CS 宣告 void 是對的；
    /// DailyRoutines 宣告成 `nint` 只是方便，那個值沒有意義，**不要拿它判斷成敗**。
    /// </summary>
    private delegate void MoveItemInChestDelegate(
        nint agent, InventoryType sourceInventory, uint sourceSlot,
        InventoryType destinationInventory, uint destinationSlot);

    /// <summary>
    /// 選 DailyRoutines 的函式本體 prologue，**不用**上游 CS 那條 `E9 ...` thunk 特徵碼。
    /// ⚠️ 寫死的位元組樣式一律視為「下次改版必壞，而且靜默」。所以解析用
    /// <see cref="ISigScanner.ScanAllText(string)"/> 檢查**命中次數必須恰好是 1**，
    /// 不是 1 就拒絕安裝（見 <see cref="OnEnable"/>）—— 樣式變得不唯一時我們寧可整個功能不能用，
    /// 也不要去呼叫一顆碰巧長得像的函式。
    /// </summary>
    private const string MoveItemInChestSig = "40 53 55 56 57 41 57 48 83 EC ?? 45 33 FF";

    private MoveItemInChestDelegate? moveItemInChest;

    /// <summary>
    /// 各遊戲版本（<c>ffxivgame.ver</c> 全字串）上 <see cref="MoveItemInChestSig"/> 唯一命中的 RVA，
    /// 由離線掃描該版執行檔取得。只拿來寫診斷，不影響要不要安裝；改版後新增一筆。
    /// </summary>
    private static readonly Dictionary<string, long> ChestFunctionOfflineRva = new(StringComparer.Ordinal)
    {
        ["2026.07.22.0000.0000"] = 0x51D630, // 7.20
        ["2026.09.14.0000.0000"] = 0x51D4B0, // 7.25
        ["2026.11.05.0000.0000"] = 0x51D9E0, // 7.3
    };

    /// <summary>比對執行期 RVA 與這一版的離線基準；沒有這一版的基準時照實說，不叫使用者回報。</summary>
    private static string DescribeChestBaseline(long rva)
    {
        var gameVersion = ReadGameVersion();
        if (gameVersion == null)
            return "讀不到遊戲版本，沒有離線基準可比";

        if (!ChestFunctionOfflineRva.TryGetValue(gameVersion, out var expected))
            return $"遊戲版本 {gameVersion} 沒有這個版本的離線基準";

        return $"遊戲版本 {gameVersion} 的離線基準 0x{expected:X}、" +
               (rva == expected ? "相符" : "**不相符，請回報**");
    }

    /// <summary>讀遊戲目錄的 <c>ffxivgame.ver</c>；讀不到回 null。</summary>
    private static string? ReadGameVersion()
    {
        try
        {
            var dir = Path.GetDirectoryName(Environment.ProcessPath);
            if (string.IsNullOrEmpty(dir)) return null;

            var path = Path.Combine(dir, "ffxivgame.ver");
            if (!File.Exists(path)) return null;

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var version = reader.ReadToEnd().Trim();
            return version.Length == 0 ? null : version;
        }
        catch (Exception ex)
        {
            Svc.Log.Information($"[AutoInventoryTransfer] 讀取 ffxivgame.ver 失敗：{ex.GetType().Name} {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// <c>AgentFreeCompanyChest</c> 裡記錄「右鍵點到的是哪一格」的兩個欄位。
    /// ⚠️ **仍然沒被證明的**是「我們在 OnMenuOpened 之後那一幀讀到的值，是這次右鍵的、
    /// 不是上一次殘留的」。這一點靠 <see cref="TryResolveChestSource"/> 的交叉比對擋住，不靠信任。
    /// </summary>
    private const int ChestContextInventoryTypeOffset = 0x1B2C;  // 6956

    /// <inheritdoc cref="ChestContextInventoryTypeOffset"/>
    private const int ChestContextInventorySlotOffset = 0x1B30;  // 6960

    /// <summary>僱員道具命令模組。⚠️ `+ 40` 是未文件化偏移，照抄 AutoRetainer 的實測值。</summary>
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
    /// 等伺服器確認的轉移。僱員／公會儲物櫃／鞍袋這三個容器伺服器有可能拒絕
    /// （另一名玩家正在用置物櫃、僱員 session 狀態…），所以本機狀態不等於最終狀態。
    /// </summary>
    /// <remarks>
    /// <c>SourceQuantity</c>／<c>SourceFlags</c> 是<b>送出那一刻</b>來源格的內容，
    /// 只有 <see cref="VerificationKind.ChestDeparture"/> 會拿它們比對。
    /// <c>SendCount</c>＝合併進這一筆的送出次數（同一格同一件重複點會累加，最小 1）。
    /// </remarks>
    private readonly record struct PendingVerification(
        VerificationKind Kind,
        InventoryType Source, int Slot,
        InventoryType Destination, int DestinationSlot,
        uint BaseItemId, string DisplayName, long StartTick, long DeadlineTick,
        int SourceQuantity, uint SourceFlags, int SendCount);

    private readonly List<PendingVerification> pendingVerifications = [];

    /// <summary>
    /// 排隊等著送出的置物櫃搬移。同一時間只讓一筆在途，其餘排在這裡逐筆送出。
    /// 🔴 這裡**不存任何原生指標**：agent 與來源格都在送出那一幀重查，
    /// 排隊期間只保存容器、格號與 itemId。
    /// </summary>
    private readonly record struct QueuedChestMove(
        InventoryType Source, int Slot, uint BaseItemId,
        string DisplayName, bool Withdrawing, int SendCount);

    private readonly List<QueuedChestMove> chestQueue = [];

    /// <summary>佇列上限。置物櫃一頁 50 格，超過就是點得比搬得快。</summary>
    private const int ChestQueueMax = 60;

    /// <summary>
    /// 🔴 觀察退回的時間長度。**不是**「等這麼久再看一眼」——是「持續盯到這麼久為止」。
    /// ⚠️ 別把它調回短窗口：伺服器的退回可能要好幾秒才回來。
    /// </summary>
    private const int RollbackWatchMs = 12_000;

    /// <summary>上一筆置物櫃搬移「已生效」之後，下一筆<b>存入</b>至少隔這麼久才送（毫秒）。</summary>
    /// <remarks>緊接著送的存入會被伺服器靜默吞掉、卡滿 <see cref="RollbackWatchMs"/> 才逾時；取出方向沒有這個現象，照舊立即送。</remarks>
    private const int ChestSettleMs = 300;

    /// <summary>在這個 <c>Environment.TickCount64</c> 之前不送下一筆存入；0＝不用等。只在框架執行緒讀寫。</summary>
    private long chestSettleUntilTick;

    private bool MustSettleBefore(bool withdrawing) => !withdrawing && Environment.TickCount64 < chestSettleUntilTick;

    /// <summary>
    /// 這次搬移需不需要盯伺服器退回。**兩邊都要看**：來源是這三個容器（取出）固然要，
    /// 目的地是這三個容器（存入）一樣要——只驗來源的話，「背包→公會儲物櫃」
    /// 這個方向完全不進確認流程，伺服器退回時我們早就印了「已轉移」。
    /// </summary>
    private static bool NeedsRollbackWatch(InventoryType type)
        => Array.IndexOf(RetainerPages, type) >= 0
        || Array.IndexOf(FreeCompanyPages, type) >= 0
        || Array.IndexOf(SaddleBags, type) >= 0;

    protected override void OnEnable()
    {
        InventoryContextHook.Subscribe(OpenForItemSlotDetour);

        // 解析不到就讓 retainerItemCommand 留 null，僱員轉移會明確告知而不是靜默走錯路徑。
        if (Svc.SigScanner.TryScanText(RetainerItemCommandSig, out var retainerCmdAddr))
        {
            retainerItemCommand =
                Marshal.GetDelegateForFunctionPointer<RetainerItemCommandDelegate>(retainerCmdAddr);
            Svc.Log.Information($"[{InternalName}] 僱員道具命令位址 0x{retainerCmdAddr:X}");
        }
        else
        {
            Svc.Log.Warning($"[{InternalName}] 找不到僱員道具命令的特徵碼，僱員轉移將無法使用。");
        }

        // 🔴 置物櫃搬移函式：**要求特徵碼恰好命中一次**，否則拒絕安裝。
        // 寫死的位元組樣式一律當成「下次改版必壞而且靜默」——所以離線的唯一性結論改成執行期的閘門，
        // 命中 0 或 ≥2 都寧可整個功能不能用。離線基準（ChestFunctionOfflineRva）只進診斷。
        var chestHits = Svc.SigScanner.ScanAllText(MoveItemInChestSig);
        if (chestHits.Length == 1)
        {
            moveItemInChest =
                Marshal.GetDelegateForFunctionPointer<MoveItemInChestDelegate>(chestHits[0]);
            var rva = (long)(chestHits[0] - Svc.SigScanner.Module.BaseAddress);
            Svc.Log.Information(
                $"[{InternalName}] 置物櫃搬移函式位址 0x{chestHits[0]:X}（RVA 0x{rva:X}，{DescribeChestBaseline(rva)}）");
        }
        else
        {
            Svc.Log.Warning(
                $"[{InternalName}] 置物櫃搬移函式的特徵碼命中 {chestHits.Length} 次（需要剛好 1 次），" +
                "為了不呼叫到錯的函式，公會儲物櫃轉移已停用。");
        }

        // 第二個觸發點：公會儲物櫃不走 OpenForItemSlot，只有這條蓋得到（見類別說明）。
        Svc.ContextMenu.OnMenuOpened += OnMenuOpened;

        Svc.Framework.Update += OnUpdate;
    }

    protected override void OnDisable()
    {
        Svc.Framework.Update -= OnUpdate;
        Svc.ContextMenu.OnMenuOpened -= OnMenuOpened;
        InventoryContextHook.Unsubscribe(OpenForItemSlotDetour);
        moveItemInChest = null;

        pendingMenu = null;
        pendingVerifications.Clear();
        chestQueue.Clear();
        chestSettleUntilTick = 0;
        lastHandledSource = InventoryType.Invalid;
        lastHandledSlot = -1;
        lastHandledItemId = 0;
        lastHandledTick = 0;
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
                          "三者都掛在同一個右鍵事件上，設成同一顆鍵會同時動作。\n" +
                          "右鍵時除了觸發鍵還按著別的 Shift／Ctrl／Alt，就不會轉移。");

        ImGui.TextDisabled("兵裝庫只支援「兵裝庫→背包」方向；反向請照常用裝備／整理流程。");
    }
}

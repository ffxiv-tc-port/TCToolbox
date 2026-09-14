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
/// ⚠️ 即使如此，下面四條專用路徑**照舊保留**，因為它們是實機來回驗證過會動的，
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

    private delegate void OpenForItemSlotDelegate(
        AgentInventoryContext* agent, InventoryType inventoryType, int slot, int a4, uint addonId);

    private Hook<OpenForItemSlotDelegate>? openForItemSlotHook;

    /// <summary>
    /// 🔴 雇員存取走遊戲自己的道具命令，**不是**因為 <c>MoveItemSlot</c> 對雇員無效
    /// （見類別說明：那個結論的實機證據是在 <c>a6</c> 被省略的情況下量到的，已被推翻），
    /// 而是因為這條路徑是實機來回驗證過會動的，而 <c>MoveItemSlot(a6: true)</c> 對雇員
    /// **還沒有實機證據**。要換請先實測。
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
    /// 我們 Dalamud 內建的 CS **沒有**這顆函式、也沒有 <c>AgentFreeCompanyChest</c> 這個結構
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
    /// <c>AgentFreeCompanyChest</c> 裡記錄「右鍵點到的是哪一格」的兩個欄位。
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
    /// ⚠️ 這跟先前修雇員時踩過的是同一個坑，別再把它調回短窗口。
    /// </summary>
    private const int RollbackWatchMs = 12_000;

    /// <summary>
    /// 這次搬移需不需要盯伺服器退回。**兩邊都要看**：來源是這三個容器（取出）固然要，
    /// 目的地是這三個容器（存入）一樣要——原本只驗來源，所以「背包→部隊置物櫃」
    /// 這個方向完全不進確認流程，伺服器退回時我們早就印了「已轉移」。
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
        chestQueue.Clear();
        lastHandledSource = InventoryType.Invalid;
        lastHandledSlot = -1;
        lastHandledItemId = 0;
        lastHandledTick = 0;
    }

    /// <summary>
    /// 由 <see cref="OnMenuOpened"/> 記下、下一個 framework tick 才執行的右鍵選單。
    /// ⚠️ 不能在 OnMenuOpened 當下就做事：那時 ContextMenu addon 還沒開起來，
    /// <c>agent-&gt;AgentInterface.GetAddonId()</c> 拿到的是上一個選單（或 0）。
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
            // 🔴 判的是 Items 不是 GetInventorySlot 的回傳值：Items 為 null 而 Size > 0 時，
            //    GetInventorySlot 回的是「null + 偏移」這種非 null 的假指標，下面的判空一定通過，
            //    解參考就是攔不到的 AVE（corrupted-state exception，try/catch 無效）。
            //    樣板同 DiscardList.ScanMatches／TriadCardRecycle 的背包掃描。
            if (container == null || !container->IsLoaded || container->Items == null) continue;

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
    /// 🔴 這個「必須一致」**不是保險絲，是這一版的核心安全設計**。
    /// 那兩個偏移雖然在台服二進位裡佐證過大小與重設值，但「我們讀到的是這次右鍵寫進去的、
    /// 而不是上一次的殘留或別的欄位」**無法離線證明**。
    /// 也就是說「偏移是錯的」的後果是**不動作**，不是搬錯道具。
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
        // 🔴 Items 為 null 而 Size > 0 時，GetInventorySlot 回的是非 null 的假指標（理由同本檔上一處）。
        if (container == null || !container->IsLoaded || container->Items == null)
        {
            diagnosis = $"(A) 容器 {agentType} 未載入（或格位陣列尚未配置）";
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

        if (pendingVerifications.Count == 0)
        {
            DrainChestQueue();
            return;
        }

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
                // 只比 itemId 分不出「真的沒搬走」跟「搬走一部分」／「同款另一疊回填進來」：
                // 數量要用「沒有少掉」而不是「不相等」，否則背包格被塞進新東西會被誤判成已生效。
                var stillAtSource =
                    TryReadSlot(manager, p.Source, p.Slot, out var curItemId, out var curQty, out var curFlags)
                    && curItemId == p.BaseItemId && curFlags == p.SourceFlags && curQty >= p.SourceQuantity;

                if (!stillAtSource)
                {
                    pendingVerifications.RemoveAt(i);

                    var landed = p.Destination != InventoryType.Invalid &&
                                 IsItemAt(manager, p.Destination, p.DestinationSlot, p.BaseItemId);
                    var where = p.Destination == InventoryType.Invalid
                        ? "（落點由遊戲決定）"
                        : $"{p.Destination}#{p.DestinationSlot} 落在指定格={landed}";

                    Svc.Log.Information(
                        $"[{InternalName}] 置物櫃搬移已生效：{p.Source}#{p.Slot} → {where} " +
                        $"itemId={p.BaseItemId} 耗時={elapsed}ms 送出次數={p.SendCount} " +
                        $"來源格現況={DescribeSlot(manager, p.Source, p.Slot)}（送出時 {p.BaseItemId}×{p.SourceQuantity}）");

                    if (Config.NotifyOnTransfer)
                        Svc.Chat.Print($"[TC Toolbox] 已轉移「{p.DisplayName}」。");
                    continue;
                }

                if (now < p.DeadlineTick) continue;

                pendingVerifications.RemoveAt(i);
                Svc.Log.Warning(
                    $"[{InternalName}] 置物櫃搬移逾時未生效：{p.Source}#{p.Slot} itemId={p.BaseItemId} " +
                    $"送出次數={p.SendCount} 在途={CountChestDepartures()} " +
                    $"來源格現況={DescribeSlot(manager, p.Source, p.Slot)}（送出時 {p.BaseItemId}×{p.SourceQuantity}） " +
                    $"——{RollbackWatchMs}ms 後來源格內容沒變，MoveItemInChest 沒有被伺服器受理。");

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
            //   存入被退回 → 道具**從目的地格消失**
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

        DrainChestQueue();
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
    /// 所以改成「點玩家自己會點的那個選單項目」。判準用 Addon 表的 row id 去查**客戶端
    /// 自己的字串**，不是寫死翻譯，所以跟語言無關：
    ///   881 = 放入陸行鳥鞍囊 / 887 = 從陸行鳥鞍囊中取回
    /// </summary>
    private const uint AddonRowDepositToSaddlebag = 881;
    private const uint AddonRowRetrieveFromSaddlebag = 887;

    // ⚠️ 部隊置物櫃刻意不走 TryFireContextMenuEntry：它的右鍵選單是 AgentContext 的一般選單，
    // 而那個函式讀的是 AgentInventoryContext 的 EventParams，索引對不上；
    // 而且那個選單裡有「丟棄」，點錯一格的代價太高。置物櫃走 TransferViaChest。

    /// <remarks>
    /// 📌 判斷順序、索引算法與那行選單傾印診斷都原封不動搬進
    /// <see cref="InventoryContextMenu.TryFireEntry"/> 供「道具快速拆分」共用，
    /// 這裡只剩「哪種失敗印哪一句聊天訊息」——那部分是轉移專用的措辭，沒有搬。
    /// 對使用者可見的行為（聊天訊息內容、節流鍵、Information 診斷）一個字都沒有改。
    /// ⚠️ 唯一的差異在 <c>Debug</c> 級那一行：搬過去之後不再附帶道具名。
    /// 使用者跑 LogLevel 1 收得到 Debug,但單檔數十萬行會淹沒，所以那行對回報沒有影響。
    /// </remarks>
    private bool TryFireContextMenuEntry(AgentInventoryContext* agent, uint addonRowId, string displayName)
    {
        var result = InventoryContextMenu.TryFireEntry(agent, addonRowId, InternalName, out var wanted);

        switch (result)
        {
            case ContextMenuFireResult.Fired:
                return true;

            case ContextMenuFireResult.NotFound:
                if (Throttle.Pass("AutoInventoryTransfer-NoMenuEntry", 3_000))
                    Svc.Chat.PrintError($"[TC Toolbox] 右鍵選單裡找不到「{wanted}」，「{displayName}」未轉移。");
                return false;

            case ContextMenuFireResult.InSubmenu:
                if (Throttle.Pass("AutoInventoryTransfer-Submenu", 3_000))
                    Svc.Chat.PrintError($"[TC Toolbox] 「{wanted}」被收在次選單裡，請改用手動拖放。");
                return false;

            case ContextMenuFireResult.Disabled:
                if (Throttle.Pass("AutoInventoryTransfer-MenuDisabled", 3_000))
                    Svc.Chat.PrintError($"[TC Toolbox] 「{wanted}」目前無法使用，「{displayName}」未轉移。");
                return false;

            case ContextMenuFireResult.Guarded:
                // 同一扇選單實例剛送過、還在關閉中：這一下不送。使用者再點一次即可，不當成失敗。
                if (Throttle.Pass("AutoInventoryTransfer-MenuGuarded", 3_000))
                    Svc.Chat.PrintError($"[TC Toolbox] 剛剛才對同一個右鍵選單送出過，「{displayName}」這一下沒送，請再試一次。");
                return false;

            // LabelUnavailable／AddonUnavailable 原本就只寫 Warning、不對使用者說話（已在共用端寫過）。
            default:
                return false;
        }
    }

    private void OpenForItemSlotDetour(
        AgentInventoryContext* agent, InventoryType inventoryType, int slot, int a4, uint addonId)
    {
        // 🔴 OnDisable() 會把 hook 欄位設回 null，而 detour 可能還在執行中（in-flight 呼叫）。
        //    `!.` 只是叫編譯器閉嘴，執行期照樣是裸解參考 —— 欄位一為 null 就把
        //    NullReferenceException 擲回原生呼叫端，而且原始函式完全沒被呼叫。
        //    快照一次到區域變數，之後只用區域變數，不要對欄位做第二次讀取。
        var hook = openForItemSlotHook;
        if (hook == null)
        {
            Svc.Log.Information(
                $"[{InternalName}] 右鍵選單 hook 已在呼叫途中被卸載，略過本次原始呼叫。");
            return;
        }

        hook.OriginalDisposeSafe(agent, inventoryType, slot, a4, addonId);

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

        // 這行救了一次診斷——部隊置物櫃右鍵時它**完全沒出現**，
        // 而它記在修飾鍵檢查之前，所以直接證明是「hook 根本沒被呼叫」而不是「修飾鍵沒按到」，
        // 才找到 OpenForItemSlot 不是置物櫃入口這件事。位置要留著，它的價值就在「記得比什麼都早」。
        // 而它記在下面那道「略過重複觸發」去重**之前**，所以去重擋得住動作、擋不住這行記錄。
        // ⇒ 這裡降成 Debug（保住「hook 有沒有被呼叫」的診斷），Information 移到去重之後，
        //   只有真的要處理那一格時才印一筆。
        var modifierHeld = Svc.Keys[(VirtualKey)Config.ModifierKeyCode];
        Svc.Log.Debug($"[{InternalName}] 右鍵選單開啟：{source}#{slot} 修飾鍵={(modifierHeld ? "有按" : "沒按")}");

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

        // 部隊置物櫃「取出」完全沒有任何輸出（存入正常），代表在這之前就 return 了，
        // 不是搬移失敗。這行把遊戲實際傳進來的容器與格號記下來，才能分辨是
        //「hook 沒被呼叫」還是「讀不到那一格」。右鍵才會觸發，不會洗版。
        if (item == null || item->ItemId == 0)
        {
            Svc.Log.Information($"[{InternalName}] 讀不到來源格，不動作：{source}#{slot}（item={(item == null ? "null" : "ItemId=0")}）");
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

        // 過了去重才印：一次右鍵＝一筆 Information（使用者跑 LogLevel 1，收得到的就是這筆）。
        // 修飾鍵沒按、或同格 500ms 內的重複觸發都已經在上面 return 掉了。
        Svc.Log.Information(
            $"[{InternalName}] 右鍵轉移觸發：{source}#{slot} itemId={itemId}「{displayName}」");

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
        // 上面的「已轉移」照樣先印（畫面上道具確實動了，不印反而更困惑），
        // 真的被退回時再補一則錯誤訊息蓋掉它。
        if (NeedsRollbackWatch(source) || NeedsRollbackWatch(destination))
        {
            var startTick = Environment.TickCount64;
            pendingVerifications.Add(new PendingVerification(
                VerificationKind.MoveItemSlotRollback,
                source, slot, destination, destinationSlot, baseItemId, displayName,
                startTick, startTick + RollbackWatchMs,
                0, 0, 1));
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
    /// ⚠️ **這一版刻意不安裝 <c>SendInventoryRefresh</c> 的 op-lock hook。**
    /// DailyRoutines 與 FreeCompanyChestHelper 兩邊都有裝，但那個 hook 是**取代原函式**
    /// （detour 裡不呼叫 Original），而我們**無法離線證明**「拿掉它就搬不動」或「裝了它沒有副作用」。
    /// 在還沒有證據以前就去攔截遊戲的庫存刷新，風險比它可能解決的問題大。
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

        if (ResolveFreeCompanyChestAgent() == null)
        {
            Svc.Log.Warning($"[{InternalName}] 置物櫃 agent 未就緒，「{displayName}」未轉移。");
            if (Throttle.Pass("AutoInventoryTransfer-NoChestAgent", 3_000))
                Svc.Chat.PrintError($"[TC Toolbox] 部隊置物櫃視窗未就緒，「{displayName}」未轉移。");
            return;
        }

        // menuAgent 只在這一幀有效，排隊路徑帶不過去（跨幀保存原生指標會靜默換人或懸空），
        // 所以選單在分派之前就關掉，兩條路徑的收尾因此一致。
        CloseContextMenu(menuAgent);

        // 一次只讓一筆在途。實機量到同時在途愈多、請求被伺服器丟掉的比例愈高：
        // 在途 0 時 13 送 13 中，在途 6 筆以上時有 23% 等不到生效。
        if (CountChestDepartures() > 0)
        {
            EnqueueChestMove(source, slot, baseItemId, displayName, withdrawing);
            return;
        }

        FireChestMove(source, slot, baseItemId, displayName, withdrawing, 1);
    }

    /// <summary>
    /// 真的送出一筆置物櫃搬移。<b>呼叫端要保證此刻沒有其他在途的置物櫃搬移</b>。
    /// 置物櫃 agent 與來源格都在這一幀重查，不吃呼叫端的快取。
    /// <paramref name="queuedSendCount"/>＝這筆累積的點擊次數（最小 1），只進診斷。
    /// </summary>
    private void FireChestMove(
        InventoryType source, int slot, uint baseItemId, string displayName,
        bool withdrawing, int queuedSendCount)
    {
        if (moveItemInChest == null) return;

        var chestAgent = ResolveFreeCompanyChestAgent();
        if (chestAgent == null)
        {
            Svc.Log.Warning($"[{InternalName}] 置物櫃 agent 未就緒，「{displayName}」未轉移。");
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

            // 讀不到來源格與「置物櫃沒有位子」是兩種原因，訊息不能共用：
            // 合在一起時使用者看到的一律是「沒有空位」，真正發生的事反而看不見。
            if (manager == null || sourceItem == null)
            {
                Svc.Log.Information(
                    $"[{InternalName}] 讀不到來源格，無法挑目的地：{source}#{slot} " +
                    $"itemId={baseItemId}「{displayName}」（manager={(manager == null ? "null" : "ok")}）");

                if (Throttle.Pass("AutoInventoryTransfer-NoSourceItem", 3_000))
                    Svc.Chat.PrintError(
                        $"[TC Toolbox] 讀不到「{displayName}」所在的格位（{source} 第 {slot} 格），未轉移。");
                return;
            }

            if (!TryFindTargetSlot(manager, candidates, sourceItem, out destination, out destinationSlot))
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

        var sourceManager = InventoryManager.Instance();
        if (sourceManager == null ||
            !TryReadSlot(sourceManager, source, slot, out _, out var sourceQuantity, out var sourceFlags))
        {
            Svc.Log.Warning($"[{InternalName}] 來源格 {source}#{slot} 已經讀不到內容，「{displayName}」未轉移。");
            return;
        }

        // 同一格同一件重複點：兩筆觀察記錄盯的是**同一個條件**，分不出來也沒意義。
        // 合併成一筆：起算點留第一次送出（耗時才是真的），期限跟著最新一次送出往後推。
        var now = Environment.TickCount64;
        var startTick = now;
        var sendCount = queuedSendCount;
        for (var i = pendingVerifications.Count - 1; i >= 0; i--)
        {
            var q = pendingVerifications[i];
            if (q.Kind != VerificationKind.ChestDeparture ||
                q.Source != source || q.Slot != slot || q.BaseItemId != baseItemId) continue;

            startTick = q.StartTick;
            sendCount = q.SendCount + queuedSendCount;
            pendingVerifications.RemoveAt(i);
            break;
        }

        Svc.Log.Information(
            $"[{InternalName}] 置物櫃搬移送出（{(withdrawing ? "取出" : "存入")}）：{source}#{slot} → " +
            $"{(withdrawing ? "（落點由遊戲決定）" : $"{destination}#{destinationSlot}")} itemId={baseItemId}×{sourceQuantity} " +
            $"送出次數={sendCount} 在途={CountChestDepartures() + 1}");

        moveItemInChest((nint)chestAgent, source, (uint)slot, destination, (uint)destinationSlot);

        // ⚠️ 這裡**故意不印**「已轉移」。MoveItemInChest 是非同步請求，呼叫當下本機什麼都還沒變，
        // 先報成功就是重蹈先前「本機動了就宣告成功」的覆轍。
        // 成功訊息改由 ChestDeparture 觀察器在道具真的離開來源格時才印。
        pendingVerifications.Add(new PendingVerification(
            VerificationKind.ChestDeparture,
            source, slot, destination, destinationSlot, baseItemId, displayName,
            startTick, now + RollbackWatchMs,
            sourceQuantity, sourceFlags, sendCount));
    }

    /// <summary>
    /// 把一筆搬移排進佇列。同一格同一件重複點只累加次數，不排第二筆。
    /// </summary>
    private void EnqueueChestMove(
        InventoryType source, int slot, uint baseItemId, string displayName, bool withdrawing)
    {
        for (var i = 0; i < chestQueue.Count; i++)
        {
            var q = chestQueue[i];
            if (q.Source != source || q.Slot != slot || q.BaseItemId != baseItemId) continue;

            chestQueue[i] = q with { SendCount = q.SendCount + 1 };
            Svc.Log.Information(
                $"[{InternalName}] 置物櫃搬移已在佇列中，累加點擊：{source}#{slot} " +
                $"itemId={baseItemId} 點擊次數={q.SendCount + 1} 佇列={chestQueue.Count}");
            return;
        }

        if (chestQueue.Count >= ChestQueueMax)
        {
            if (Throttle.Pass("AutoInventoryTransfer-ChestQueueFull", 3_000))
                Svc.Chat.PrintError(
                    $"[TC Toolbox] 置物櫃搬移已排到 {ChestQueueMax} 筆，「{displayName}」沒有排進去，請等前面搬完再試。");
            return;
        }

        chestQueue.Add(new QueuedChestMove(source, slot, baseItemId, displayName, withdrawing, 1));
        Svc.Log.Information(
            $"[{InternalName}] 置物櫃搬移排隊中：{source}#{slot} itemId={baseItemId}「{displayName}」 " +
            $"佇列={chestQueue.Count} 在途={CountChestDepartures()}");
    }

    /// <summary>
    /// 前一筆確認（或逾時）之後才送下一筆。
    /// 送出前重讀來源格：排隊期間使用者可能自己動過那一格，itemId 對不上就跳過，
    /// 免得把後來補進同一格的另一件道具搬走。
    /// </summary>
    private void DrainChestQueue()
    {
        if (chestQueue.Count == 0 || CountChestDepartures() > 0) return;

        if (ResolveFreeCompanyChestAgent() == null)
        {
            Svc.Log.Information($"[{InternalName}] 置物櫃已關閉，取消排隊中的 {chestQueue.Count} 筆搬移。");
            chestQueue.Clear();
            return;
        }

        var next = chestQueue[0];
        chestQueue.RemoveAt(0);

        var manager = InventoryManager.Instance();
        if (manager == null ||
            !TryReadSlot(manager, next.Source, next.Slot, out var curItemId, out _, out _) ||
            curItemId != next.BaseItemId)
        {
            Svc.Log.Information(
                $"[{InternalName}] 排隊中的搬移已失效，跳過：{next.Source}#{next.Slot} " +
                $"itemId={next.BaseItemId}「{next.DisplayName}」 現況=" +
                (manager == null ? "讀不到背包" : DescribeSlot(manager, next.Source, next.Slot)));
            return;
        }

        FireChestMove(next.Source, next.Slot, next.BaseItemId, next.DisplayName,
            next.Withdrawing, next.SendCount);
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
        // 🔴 Items 為 null 而 Size > 0 時，GetInventorySlot 回的是非 null 的假指標（理由同本檔上一處）。
        // 刻意不連 IsLoaded 一起擋：它為 false 時 Items 仍可能有效，擋掉會把既有判斷改成「讀不到」。
        var container = manager->GetInventoryContainer(type);
        if (container == null || container->Items == null) return false;

        var item = manager->GetInventorySlot(type, slot);
        return item != null && item->GetBaseItemId() == baseItemId;
    }

    /// <summary>讀出一格的內容。空格（itemId 0）一律回 <c>false</c>。</summary>
    private static bool TryReadSlot(
        InventoryManager* manager, InventoryType type, int slot,
        out uint baseItemId, out int quantity, out uint flags)
    {
        baseItemId = 0;
        quantity = 0;
        flags = 0;

        // 🔴 假指標防線與 IsItemAt 同一條，理由見那裡。
        var container = manager->GetInventoryContainer(type);
        if (container == null || container->Items == null) return false;

        var item = manager->GetInventorySlot(type, slot);
        if (item == null || item->ItemId == 0) return false;

        baseItemId = item->GetBaseItemId();
        quantity = item->Quantity;
        flags = (uint)item->Flags;
        return true;
    }

    /// <summary>診斷用：把一格的現況寫成一段可讀的字串。</summary>
    private static string DescribeSlot(InventoryManager* manager, InventoryType type, int slot)
        => TryReadSlot(manager, type, slot, out var id, out var qty, out var flags)
            ? $"{id}×{qty}(flags={flags})"
            : "空格";

    /// <summary>目前還在等置物櫃回應的筆數（診斷用，不影響判斷）。</summary>
    private int CountChestDepartures()
    {
        var n = 0;
        foreach (var p in pendingVerifications)
            if (p.Kind == VerificationKind.ChestDeparture) n++;
        return n;
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

            // 🔴 光是判 chest 與長度還不夠。AtkValuesSpan 的實作是
            // new Span<AtkValue>(AtkValues, AtkValuesCount)，它自己不判 AtkValues 這個欄位，
            // 而 Span 的建構子也不驗指標。addon 拆解時 AtkValues 會先被釋放成 null、
            // AtkValuesCount 卻可能還留著殘值，這個組合會合法建構出一個長度非零的 Span，
            // 連 Span 自己的邊界檢查都會放行，一直到真的索引下去才對位址 0 解參考 ＝
            // AccessViolationException（corrupted-state exception，try/catch 攔不到）。
            // ⇒ 讀不到就跳過「優先目前分頁」這段，照下面的迴圈把所有分頁都列為候選。
            if (chest != null && chest->AtkValues != null)
            {
                var values = chest->AtkValuesSpan;
                if (values.Length > 2)
                {
                    if (values[1].UInt != 0)
                    {
                        reason = "部隊置物櫃目前在水晶頁，請切到道具頁再轉移。";
                        return false;
                    }

                    var pageIndex = (int)values[2].UInt;
                    if (pageIndex >= 0 && pageIndex < FreeCompanyPages.Length)
                        candidates.Add(FreeCompanyPages[pageIndex]);
                }
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
                // 🔴 Items 為 null 而 Size > 0 時，GetInventorySlot 回的是非 null 的假指標（理由同本檔上一處）。
                if (container == null || !container->IsLoaded || container->Items == null) continue;

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
            // 🔴 Items 為 null 而 Size > 0 時，GetInventorySlot 回的是非 null 的假指標（理由同本檔上一處）。
            if (container == null || !container->IsLoaded || container->Items == null) continue;

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

        // 判空集中在 UiHelper.GetAddonById（AtkStage.Instance() 與 RaptureAtkUnitManager 兩層都可能 null）。
        var addon = UiHelper.GetAddonById(addonId);
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

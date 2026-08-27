using System;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using Lumina.Excel.Sheets;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 自動取出全部魔晶石：使用者對某件裝備手動取出一顆魔晶石之後，
/// 自動把<b>同一件裝備上剩下的</b>魔晶石接著取完。
/// <para>
/// 🔴 <b>這個模組只會「接續」使用者已經開始的動作，永遠不會自己發起。</b>
/// 沒有掃背包、沒有批次入口、沒有指令 —— 第一顆一定是使用者自己在遊戲裡點的。
/// 取出魔晶石是會送到伺服器而且不可逆的操作，所以入口收得越窄越好。
/// </para>
/// <para>
/// 對照 DailyRoutines <c>AutoMateriaRetrive</c>（自動回收魔晶石）：那邊除了 hook 之外還有一個
/// 「選道具名稱 → 開始」的批次面板，會掃過背包＋兵裝庫找同名裝備自己動手。
/// 這裡<b>刻意沒有移植那一半</b> —— 它讓「按錯一次」的代價變成整套裝備的魔晶石。
/// </para>
/// </summary>
/// <remarks>
/// <para>
/// <b>為什麼是 hook：</b>遊戲沒有「一次取完」的原生按鈕可以按，而「使用者剛剛取出了哪一格」
/// 沒有任何具名欄位讀得到。這條 hook 的範圍是<b>單一函式</b>
/// （<c>EventFramework::MaterializeItem</c>），不是 DR 那種掛在所有 agent 共用的
/// <c>ReceiveEvent</c> 上再用寬鬆條件猜的做法。
/// </para>
/// <para>
/// 🔑 <b>事件 ID 不寫死。</b>DR 送的是字面常數 <c>3735553</c>；這裡用 ClientStructs 的具名列舉
/// 組出來（<see cref="EventHandlerContent.Materialize"/> ＋ <see cref="MaterializeEntryId.Retrieve"/>），
/// 改版時是編譯期就看得到的東西，不是一個沒人看得懂的數字。
/// </para>
/// <para>
/// ⚠️ <b>ClientStructs 的散文註解在這裡是錯的，不要照著讀。</b><c>EventHandler.cs</c> 那行寫
/// 「Materia Extraction (0x390001)」，但 <c>MaterializeEntryId.Retrieve = 0x0001</c> 才是對的。
/// 台服 7.20 主程式離線反組譯的證據（三條互相獨立）：
/// <list type="number">
/// <item>送 <c>0x390000</c> 的兩個呼叫點都落在 <c>SalvageDialog</c>／<c>SalvageResult</c>
/// 那塊（分解），送 <c>0x390002</c> 的六個都落在 <c>Purify*</c> 那塊（精選）
/// —— 也就是 Desynth=0、Purify=2 兩端都對得上具名列舉。</item>
/// <item>唯一送 <c>0x390001</c> 的呼叫點（RVA <c>0xEAFE1D</c>）所在的那個函式，
/// <b>另一條分支</b>呼叫的是 <c>0x14084BEC0</c> —— 那正是本外掛
/// <see cref="AutoMaterialize"/> 已經在用的「精製魔晶石」函式。
/// 精製自己有專用函式，所以 <c>0x390001</c> 不可能又是精製。</item>
/// <item>DailyRoutines 自己的 <c>AutoMaterialize</c>（精製）hook 的是同一個函式裡
/// <b>那條分支</b>的 call，跟本模組走的完全不是同一條。</item>
/// </list>
/// 反過來說，若這個判斷錯了，最壞情況是把「取出魔晶石」送成「分解裝備」。
/// 上面第 1 點已經把分解釘死在 <c>0x390000</c>，所以這條路徑不成立；
/// 但這也是為什麼這個模組寧可只做「接續」而不做批次。
/// </para>
/// </remarks>
public sealed unsafe class AutoMateriaRetrieveAll : TcModule
{
    public override string InternalName => "AutoMateriaRetrieveAll";
    public override string DisplayName => "自動取出全部魔晶石";

    public override string Description =>
        "手動取出一件裝備上的魔晶石之後，自動把同一件裝備上剩下的魔晶石接著取完。" +
        "只會接續你自己開始的動作，不會主動去動任何裝備；" +
        "裝備被移動、背包沒空位、進入戰鬥或伺服器沒有回應時都會自動停止。";

    public override ModuleCategory Category => ModuleCategory.Inventory;

    // 🔴 這個模組刻意**沒有**標 IsManualTrigger。
    //    描述裡的「手動」講的是使用者在遊戲裡自己點的那一下，不是我們的按鈕：
    //    本模組是掛 MaterializeItem 的 hook，開著就會在你取出第一顆之後自己接手把剩下的取完，
    //    面板上只有「立即停止」沒有「開始」。判準是「開著但不去按它，遊戲行為完全不變嗎」——
    //    這裡是「會變」，所以它屬於自動介入型，不進「手動觸發」分頁。

    public override bool HasConfigUI => true;

    private AutoMateriaRetrieveAllConfig Config => Plugin.Instance.Config.MateriaRetrieveAll;

    /// <summary>
    /// 遊戲的「精製系」事件入口。四種操作共用這一個函式，靠事件 ID 區分：
    /// 分解 / 取出魔晶石 / 精選。
    /// <para>
    /// ⚠️ 回傳型別跟著 ClientStructs 宣告成 <c>void</c>。DR 宣告成 <c>bool</c> 並且拿它當判斷，
    /// 但實際呼叫端（RVA <c>0xEAFE1D</c> 之後那幾行）根本沒有讀 <c>eax</c> ——
    /// 宣告成 <c>bool</c> 讀到的是上一個函式留在暫存器裡的殘值。
    /// 這裡不需要回傳值：每一輪都用「魔晶石數量有沒有真的變少」當進度判準，比回傳碼可靠。
    /// </para>
    /// </summary>
    private delegate void MaterializeItemDelegate(
        EventFramework* framework, EventId eventId, InventoryType container, short slot, int extraParam);

    private Hook<MaterializeItemDelegate>? materializeItemHook;

    private readonly TaskQueue queue = new() { DefaultTimeoutMs = 15_000 };

    /// <summary>「取出魔晶石」的事件 ID。用具名列舉組出來，不寫字面常數。</summary>
    private static EventId RetrieveEventId => new()
    {
        ContentId = EventHandlerContent.Materialize,
        EntryId = (ushort)MaterializeEntryId.Retrieve,
    };

    // ── 這一輪盯著的那一格。🔴 只存 ID，不存原生指標，每個 tick 重新解析。 ──
    private InventoryType watchedContainer = InventoryType.Invalid;
    private short watchedSlot = -1;
    private uint watchedItemId;
    private string watchedItemName = string.Empty;

    /// <summary>本輪已經接手取出的顆數（不含使用者自己點的那一顆）。</summary>
    private int retrievedCount;

    /// <summary>
    /// 保險絲，不是業務規則。裝備最多 5 孔（含禁斷），正常情況下一輪不會超過 5 次；
    /// 這裡放寬到 8 只是為了在「數量判斷本身出乎意料」時仍然停得下來。
    /// </summary>
    private const int MaxRounds = 8;

    private int roundsDone;

    protected override void OnEnable()
    {
        // 🔴 未解析的 CS MemberFunction 位址是 0，直接掛上去等於 hook 到 0 位址。
        // 這裡把它當成「這一版不能用」而不是硬掛。
        var address = EventFramework.Addresses.MaterializeItem.Value;
        if (address == 0)
        {
            Svc.Log.Information(
                $"[{InternalName}] 找不到 EventFramework::MaterializeItem 的位址，本模組這一版無法使用。");
            return;
        }

        materializeItemHook = Svc.Hooks.HookFromAddress<MaterializeItemDelegate>(address, MaterializeItemDetour);
        materializeItemHook.Enable();

        queue.OnTimeout = step =>
        {
            Svc.Chat.PrintError($"[TC Toolbox] 自動取出魔晶石在「{step}」逾時，已停止。");
            ClearWatch();
        };

        Svc.Framework.Update += OnUpdate;

        Svc.Log.Information($"[{InternalName}] 已掛載，MaterializeItem 位址 0x{address:X}。");
    }

    protected override void OnDisable()
    {
        Svc.Framework.Update -= OnUpdate;

        materializeItemHook?.Dispose();
        materializeItemHook = null;

        queue.Abort();
        ClearWatch();
    }

    private void OnUpdate(IFramework framework) => queue.Tick();

    /// <summary>
    /// 🔴 先呼叫 Original，讓使用者的操作原封不動地先送出去；我們只在它之後決定要不要接手。
    /// 這個 detour 在遊戲主執行緒上被呼叫（agent 的事件處理裡），和 <see cref="OnUpdate"/> 同一條執行緒。
    /// </summary>
    private void MaterializeItemDetour(
        EventFramework* framework, EventId eventId, InventoryType container, short slot, int extraParam)
    {
        // 🔴 OnDisable() 會把 hook 欄位設回 null，而 detour 可能還在執行中（in-flight 呼叫）。
        //    `!.` 只是叫編譯器閉嘴，執行期照樣是裸解參考 —— 欄位一為 null 就把
        //    NullReferenceException 擲回原生呼叫端，而且原始函式完全沒被呼叫。
        //    快照一次到區域變數，之後只用區域變數，不要對欄位做第二次讀取。
        var hook = materializeItemHook;
        if (hook == null)
        {
            Svc.Log.Information(
                $"[{InternalName}] 取出魔晶石 hook 已在呼叫途中被卸載，略過本次原始呼叫。");
            return;
        }

        hook.OriginalDisposeSafe(framework, eventId, container, slot, extraParam);

        try
        {
            // 只認「取出魔晶石」。分解與精選走同一個函式，必須整個事件 ID 比對，不能只看 ContentId。
            if (eventId.Id != RetrieveEventId.Id) return;

            // 已經在跑就不重新開始 —— 我們自己重送時是走 Original，不會回到這裡，
            // 所以能走到這一行的都是使用者自己又點了一次。
            if (queue.IsBusy) return;

            if (!Config.Enabled) return;

            BeginWatch(container, slot);
        }
        catch (Exception ex)
        {
            // detour 裡漏出去的例外會直接打到遊戲，這裡一律吞掉並記錄。
            Svc.Log.Error(ex, $"[{InternalName}] detour 發生例外，本次不接手。");
            queue.Abort();
            ClearWatch();
        }
    }

    private void BeginWatch(InventoryType container, short slot)
    {
        var item = ResolveSlot(container, slot);
        if (item == null || item->ItemId == 0) return;

        var count = MateriaCount(item);

        // 使用者取的是最後一顆的話就沒有要接手的東西了。
        // ⚠️ 這裡讀到的是「送出前」的數量：伺服器還沒回，本機還沒扣。
        if (count <= 1) return;

        watchedContainer = container;
        watchedSlot = slot;
        watchedItemId = item->ItemId;
        watchedItemName = ItemContextResolver.TryGetItemName(item->ItemId, out _, out var name)
            ? name
            : $"#{item->ItemId}";

        retrievedCount = 0;
        roundsDone = 0;

        Svc.Log.Information(
            $"[{InternalName}] 接手：{watchedItemName}（{container}#{slot}），送出前 {count} 顆。");

        EnqueueRound(count);
    }

    /// <summary>
    /// 一輪＝「等上一次真的生效」→「檢查還能不能繼續」→「送出下一次」。
    /// 🔑 進度判準是<b>魔晶石數量有沒有真的變少</b>，不是回傳碼、也不是固定延遲：
    /// 伺服器沒回應就不會有下一次，所以不會愈送愈快、也不會對著已經空了的裝備空轉。
    /// </summary>
    private void EnqueueRound(int countBefore)
    {
        queue.Enqueue("等待伺服器回應", () =>
        {
            var item = ResolveSlot(watchedContainer, watchedSlot);
            if (!StillTheSameItem(item)) return null;

            return MateriaCount(item) < countBefore ? true : false;
        }, 10_000);

        queue.Enqueue("檢查是否繼續", () =>
        {
            var item = ResolveSlot(watchedContainer, watchedSlot);
            if (!StillTheSameItem(item)) return null;

            var remaining = MateriaCount(item);
            if (remaining == 0)
            {
                Report($"[TC Toolbox] {watchedItemName} 的魔晶石已全部取出（自動接手 {retrievedCount} 顆）。");
                ClearWatch();
                return null;
            }

            if (roundsDone >= MaxRounds)
            {
                Svc.Log.Information(
                    $"[{InternalName}] 已達回合上限 {MaxRounds}，強制停止（剩 {remaining} 顆）。");
                Report($"[TC Toolbox] 自動取出魔晶石已達次數上限，剩下的請手動取出（剩 {remaining} 顆）。");
                ClearWatch();
                return null;
            }

            if (Svc.Condition[ConditionFlag.InCombat])
            {
                Report("[TC Toolbox] 進入戰鬥，已停止自動取出魔晶石。");
                ClearWatch();
                return null;
            }

            // ⚠️ 取出的魔晶石要有地方放。就算實際上不需要背包空位，這個判斷也只會讓我們
            // 在背包全滿這種罕見狀況下早一步停手，不會造成別的影響。
            var manager = InventoryManager.Instance();
            if (manager == null) return null;
            if (manager->GetEmptySlotsInBag() == 0)
            {
                Report("[TC Toolbox] 背包已滿，已停止自動取出魔晶石。");
                ClearWatch();
                return null;
            }

            return true;
        }, 10_000);

        queue.Enqueue("取出下一顆", () =>
        {
            // 快照一次：下面還要用同一個欄位，分兩次讀會在中間被 OnDisable() 清空。
            var hook = materializeItemHook;
            if (hook == null) return null;

            // 動作還在進行中就等它結束再送。兩個旗標都擋是因為離線分不出取出走的是哪一個，
            // 兩個一起等的代價只是多等幾個 tick。
            if (Svc.Condition[ConditionFlag.Occupied39]) return false;
            if (Svc.Condition[ConditionFlag.MeldingMateria]) return false;
            if (!Throttle.Pass("AutoMateriaRetrieveAll-Step", Math.Max(200, Config.DelayMs))) return false;

            var item = ResolveSlot(watchedContainer, watchedSlot);
            if (!StillTheSameItem(item)) return null;

            var countNow = MateriaCount(item);
            if (countNow == 0) return null;

            var framework = EventFramework.Instance();
            if (framework == null) return null;

            // 走 Original：這是我們自己送的，不需要也不應該再經過自己的 detour。
            hook.OriginalDisposeSafe(
                framework, RetrieveEventId, watchedContainer, watchedSlot, 0);

            retrievedCount++;
            roundsDone++;

            EnqueueRound(countNow);
            return true;
        }, 15_000);
    }

    /// <summary>
    /// 🔴 每次都重新解析，<b>絕不跨幀保存 <c>InventoryItem*</c></b>。
    /// 容器可能在期間被卸載（換區、開關雇員），保存下來的指標會變成野指標，
    /// 而 AccessViolation 是 corrupted-state exception，外面的 try/catch 攔不到。
    /// </summary>
    private static InventoryItem* ResolveSlot(InventoryType container, short slot)
    {
        if (container == InventoryType.Invalid || slot < 0) return null;

        var manager = InventoryManager.Instance();
        if (manager == null) return null;

        var inventory = manager->GetInventoryContainer(container);
        // 🔴 判的是 Items 不是 GetInventorySlot 的回傳值：Items 為 null 而 Size > 0 時，
        //    GetInventorySlot 回的是「null + 偏移」這種非 null 的假指標，下面的判空一定通過，
        //    解參考就是攔不到的 AVE（corrupted-state exception，try/catch 無效）。
        //    樣板同 DiscardList.ScanMatches／TriadCardRecycle 的背包掃描。
        if (inventory == null || !inventory->IsLoaded || inventory->Items == null) return null;
        if (slot >= inventory->Size) return null;

        return inventory->GetInventorySlot(slot);
    }

    /// <summary>
    /// 那一格裡的還是不是我們一開始盯上的那件東西。
    /// ⚠️ 只比對得了道具<b>種類</b>：同款裝備被換到同一格是分辨不出來的。
    /// 但那種情況下接著取出的仍然是同款裝備上的魔晶石，不會跑去動到別的東西。
    /// </summary>
    private bool StillTheSameItem(InventoryItem* item)
    {
        if (item == null) return false;
        if (item->ItemId == 0 || item->ItemId != watchedItemId)
        {
            Svc.Log.Information($"[{InternalName}] 目標格內容已改變，停止接手。");
            return false;
        }

        return true;
    }

    private static int MateriaCount(InventoryItem* item)
    {
        if (item == null) return 0;

        var count = 0;
        var materia = item->Materia;
        for (var i = 0; i < materia.Length; i++)
        {
            if (materia[i] != 0) count++;
        }

        return count;
    }

    private void ClearWatch()
    {
        watchedContainer = InventoryType.Invalid;
        watchedSlot = -1;
        watchedItemId = 0;
        watchedItemName = string.Empty;
        roundsDone = 0;
    }

    private void Report(string message)
    {
        Svc.Log.Information($"[{InternalName}] {message}");
        if (Config.AnnounceInChat) Svc.Chat.Print(message);
    }

    public override void DrawConfig()
    {
        var enabled = Config.Enabled;
        if (ImGui.Checkbox("啟用自動接手##materiaRetrieveAll", ref enabled))
        {
            Config.Enabled = enabled;
            if (!enabled)
            {
                queue.Abort();
                ClearWatch();
            }

            Plugin.Instance.Config.Save();
        }

        ImGui.TextDisabled("關閉時模組仍然掛著但不會接手，等同只保留遊戲原本的行為。");

        var delay = Config.DelayMs;
        ImGui.SetNextItemWidth(200f);
        if (ImGui.SliderInt("每顆之間的間隔（毫秒）##materiaRetrieveAllDelay", ref delay, 200, 3000))
        {
            Config.DelayMs = Math.Clamp(delay, 200, 3000);
            Plugin.Instance.Config.Save();
        }

        var announce = Config.AnnounceInChat;
        if (ImGui.Checkbox("在聊天視窗回報結果##materiaRetrieveAllAnnounce", ref announce))
        {
            Config.AnnounceInChat = announce;
            Plugin.Instance.Config.Save();
        }

        if (queue.IsBusy)
        {
            ImGui.Separator();
            ImGui.Text($"進行中：{watchedItemName} —— {queue.CurrentStep}");
            if (ImGui.Button("立即停止##materiaRetrieveAllStop"))
            {
                queue.Abort();
                ClearWatch();
            }
        }
    }
}

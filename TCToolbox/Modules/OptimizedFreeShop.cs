using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 報酬界面最佳化：在「報酬」視窗（FreeShop）上加一排一鍵領取按鈕，並在領取時自動按掉確認對話框。
/// 機制：直接讀 addon 自己的 AtkValues 取得清單（第 3 格＝筆數、第 65 格起＝各筆的道具 ID），
/// 領取用 <c>AgentFreeShop</c> 的 ReceiveEvent——與點擊清單項目同一條路徑。
/// 不 hook、不寫記憶體、不做 patch。
/// 參考 DailyRoutines OptimizedFreeShop 設計重寫（API13、無 OmenTools／KamiToolKit 相依）。
/// </summary>
/// <remarks>
/// 與 DR 原版的兩點差異：
/// <list type="number">
/// <item>DR 是 hook <b>AgentInterface::ReceiveEvent</b> 這個所有 agent 共用的函式，靠
/// <c>eventKind==0 &amp;&amp; values[0].Int==0</c> 判斷「有人在領東西」再去按 Yes。那條 hook 每一個
/// agent 的每一次事件都會經過，判斷條件又極寬鬆。這裡改成：只在「報酬」視窗開著時才自動確認
/// SelectYesno，零 hook、作用範圍收斂到這個視窗。</item>
/// <item>DR 用 KamiToolKit 把按鈕注入成原生節點，這裡沿用本外掛既有作法用 ImGui 疊圖，
/// 不動遊戲的節點樹。</item>
/// </list>
/// </remarks>
public sealed unsafe class OptimizedFreeShop : TcModule
{
    public override string InternalName => "OptimizedFreeShop";
    public override string DisplayName => "報酬界面最佳化";

    public override string Description =>
        "開啟「報酬」視窗時，上方會多出一排依職業分類的一鍵領取按鈕（該職業的裝備一次領完），" +
        "並可省掉每一件的領取確認對話框。只在這個視窗開著時作用。";

    public override ModuleCategory Category => ModuleCategory.Inventory;

    public override bool HasConfigUI => true;

    private const string AddonName = "FreeShop";

    /// <summary>清單筆數所在的 AtkValue 索引。</summary>
    private const int CountValueIndex = 3;

    /// <summary>第一筆道具 ID 所在的 AtkValue 索引。</summary>
    private const int FirstItemValueIndex = 65;

    /// <summary>單次批次領取的上限，避免 addon 版面改動時無限跑。</summary>
    private const int MaxBatchItems = 200;

    /// <summary>
    /// 判定「這個 SelectYesno 是不是報酬視窗的領取確認框」用的 <c>Addon</c> 列。
    /// </summary>
    /// <remarks>
    /// 🔑 台服 7.20 實查：<c>Addon#11431~11437</c> 正是報酬視窗自己的字串區塊
    /// （「可領取道具」「可領取」「已獲得」「無可領取道具」「領取條件：達成成就」），
    /// 其中 <c>#11437</c>＝「確定要領取＿道具＿×＿數量＿嗎？」就是領取確認句；
    /// <c>#11506/#11507/#11508/#11515</c> 是四句「確定要領取嗎？＋這件你穿不了／已學會」的變體
    /// （它們的「用不了也要領」按鈕文字在相鄰的 <c>#11509/#11516</c>，同一個區塊）。
    /// 用列號查客戶端自己的字串，所以跟語言無關。
    /// ❌ <b>不能整句逐字比對</b>：句子裡的道具名與數量是 placeholder。
    /// 比對規則見 <see cref="AddonPrompt"/>（只留固定片段、全部依序出現才算命中）。
    /// ⚠️ 台服實機到底跳哪一句無法離線證明 —— 所以未命中時會把原句寫進 log（見
    /// <see cref="OnSelectYesno"/>），照那行補列號即可。
    /// </remarks>
    private static readonly uint[] ClaimPromptRows = [11437, 11506, 11507, 11508, 11515];

    /// <summary>解析好的領取確認框樣板；<see cref="OnEnable"/> 時建一次。</summary>
    private readonly List<List<string>> claimPrompts = [];

    private readonly TaskQueue queue = new() { DefaultTimeoutMs = 15_000 };

    private OptimizedFreeShopConfig Config => Plugin.Instance.Config.FreeShop;

    protected override void OnEnable()
    {
        queue.OnTimeout = step => Svc.Chat.PrintError($"[TC Toolbox] 批次領取逾時，已停止：{step}");

        claimPrompts.Clear();
        claimPrompts.AddRange(AddonPrompt.GetTemplates(ClaimPromptRows));

        // 使用者回報用：樣板一條都解不出來的話「自動按掉確認框」會完全失效，
        // 而那個徵兆在畫面上跟「這個選項沒打開」分不出來。
        Svc.Log.Information(
            $"[{InternalName}] 領取確認框判準 {claimPrompts.Count}/{ClaimPromptRows.Length} 條：{AddonPrompt.Describe(claimPrompts)}");

        // 🔴 PostSetup ＋ PostDraw 兩條都掛（慣例同 AutoRequestItemSubmit／LetterCollectAll）：
        //    PostSetup 那一刻確認框不一定已經可以互動（IsVisible／LoadedState 還沒到位），
        //    只掛 PostSetup 的話錯過就沒有第二次機會 —— 而我們在下面要讀它的提示文字。
        //    兩者共用同一個節流器，所以 PostDraw 不會變成每幀重發事件。
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "SelectYesno", OnSelectYesno);
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostDraw, "SelectYesno", OnSelectYesno);
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, AddonName, OnFreeShopClosed);
        Svc.Framework.Update += OnUpdate;
        Svc.PluginInterface.UiBuilder.Draw += DrawOverlay;
    }

    protected override void OnDisable()
    {
        Svc.AddonLifecycle.UnregisterListener(OnSelectYesno);
        Svc.AddonLifecycle.UnregisterListener(OnFreeShopClosed);
        Svc.Framework.Update -= OnUpdate;
        Svc.PluginInterface.UiBuilder.Draw -= DrawOverlay;
        queue.Abort();
        claimPrompts.Clear();
    }

    private void OnUpdate(IFramework framework) => queue.Tick();

    private void OnFreeShopClosed(AddonEvent type, AddonArgs args)
    {
        InvalidateCache();

        if (!queue.IsBusy) return;
        queue.Abort();
        Svc.Chat.Print("[TC Toolbox] 報酬視窗已關閉，停止批次領取。");
    }

    /// <summary>
    /// 只在報酬視窗開著、<b>而且提示文字真的是領取確認句</b>時才自動確認。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>光靠「報酬視窗開著」不夠</b>：使用者開著報酬視窗瀏覽（沒按任何領取鈕）時，
    /// 任何來源的 SelectYesno 都會落進這裡 —— 最具體的是<b>別的玩家的交易申請確認框</b>，
    /// 或其他外掛（AutoRetainer 之類）此刻彈出的確認框。少了文字閘門就等於「看到 Yes/No 就按是」。
    /// 📌 <b>也不能改用 <c>queue.IsBusy</c> 當閘門</b>：本模組的賣點包含「手動點領取也跳過確認」，
    /// 手動路徑根本不經過佇列 —— 文字白名單才是必要且充分的那一道。
    /// ⚠️ 未命中一律不動作並寫一行 Information 級 log（使用者跑 LogLevel 1 收得到），
    /// 那行同時是「台服實際跳的是哪一句」的唯一線索。
    /// </remarks>
    private void OnSelectYesno(AddonEvent type, AddonArgs args)
    {
        if (!Config.SkipConfirmation) return;

        // PostDraw 每幀都會進來，所以節流放最前面——後面每一步都要取 addon、讀字串。
        if (!Throttle.Pass($"{InternalName}-Yesno", 200)) return;

        if (!UiHelper.IsAddonReady(AddonName)) return;

        var addon = (AtkUnitBase*)args.Addon.Address;
        if (!UiHelper.IsReady(addon)) return;

        var prompt = AddonPrompt.ReadSelectYesnoText(addon);

        // 🔴 讀出替換字元＝視窗的記憶體正在變動（多半是正在關閉），這一幀什麼都不要碰，下一幀重讀。
        //    ⚠️ 這一步要排在文字比對之前：比對不到會寫一行「未認得的確認框」的診斷 log，
        //    把讀壞的半句話寫進去只會誤導以後看 log 的人去改判準。
        if (AddonPrompt.LooksMidUpdate(prompt)) return;

        if (!AddonPrompt.MatchesAny(prompt, claimPrompts))
        {
            if (Throttle.Pass($"{InternalName}-PromptMiss", 10_000))
            {
                Svc.Log.Information(
                    $"[{InternalName}] 報酬視窗開著時出現未認得的確認框，不動作：「{prompt}」" +
                    $"（目前判準：{AddonPrompt.Describe(claimPrompts)}）");
            }

            return;
        }

        // 🔴 最後一道：按過的那個實例在觀察到它收掉之前不再按。這裡掛了 PostDraw ＝每幀都會回來
        //    （節流 200ms），而「按下之後正在關閉」的那幾幀 IsReady 三關照樣全過，
        //    再送 callback 就是攔不到的存取違規（2026-08-31 實機崩潰 crash-20260831205734 的形狀）。
        //    守衛已下沉到 UiHelper.TryFireCallback 裡：回 false ＝這一幀沒送。
        UiHelper.TryFireCallback(addon, true, 0);
    }

    private sealed record ShopEntry(int Index, uint ItemId);

    private sealed class JobGroup(uint categoryRowId, string name, uint iconId)
    {
        public uint CategoryRowId { get; } = categoryRowId;
        public string Name { get; } = name;
        public uint IconId { get; } = iconId;
        public List<ShopEntry> Entries { get; } = [];

        /// <summary>尚未入手的件數（快取值，由 <see cref="RefreshCache"/> 更新）。</summary>
        public int Remaining;
    }

    private List<JobGroup> cachedGroups = [];
    private DateTime cacheValidUntil = DateTime.MinValue;

    /// <summary>
    /// 疊圖每幀都會畫，但清單解析與「還缺幾件」的背包查詢都是原生呼叫，
    /// 每幀對數十件道具各查一次背包會實打實地吃 frame time——所以節流成每 500ms 一次。
    /// </summary>
    private List<JobGroup> GetGroups(AtkUnitBase* addon)
    {
        if (DateTime.UtcNow < cacheValidUntil) return cachedGroups;

        cacheValidUntil = DateTime.UtcNow.AddMilliseconds(500);
        cachedGroups = ReadGroups(addon);

        foreach (var group in cachedGroups)
        {
            var remaining = 0;
            foreach (var entry in group.Entries)
            {
                if (GetItemCount(entry.ItemId) == 0) remaining++;
            }

            group.Remaining = remaining;
        }

        return cachedGroups;
    }

    private void InvalidateCache()
    {
        cachedGroups = [];
        cacheValidUntil = DateTime.MinValue;
    }

    /// <summary>
    /// 讀出目前報酬清單並依「職業分類」分組。
    /// AtkValues 一律走 <see cref="AtkUnitBase.AtkValuesSpan"/>（帶長度），不用裸索引——
    /// 原生陣列沒有邊界檢查，addon 版面一改就會變成任意記憶體讀取。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>光是走 <c>AtkValuesSpan</c> 還不夠。</b>它的實作是
    /// <c>new Span&lt;AtkValue&gt;(AtkValues, AtkValuesCount)</c>，<b>自己不判 <c>AtkValues</c> 欄位</b>，
    /// 而 <c>Span</c> 的建構子也不驗指標。addon 拆解時 <c>AtkValues</c> 會先被釋放成 null、
    /// <c>AtkValuesCount</c> 卻可能還留著殘值，這個組合會<b>合法建構出一個長度非零的 Span</b>，
    /// 連 Span 自己的邊界檢查都會放行，一直到真的索引下去才對位址 0 解參考 ＝
    /// AccessViolationException（corrupted-state exception，<c>try/catch</c> 攔不到）。
    /// ⇒ <c>addon == null</c> 與 <c>Length</c> 都擋不住這條，必須自己判 <c>AtkValues</c> 欄位。
    /// </remarks>
    private static List<JobGroup> ReadGroups(AtkUnitBase* addon)
    {
        var groups = new List<JobGroup>();
        if (addon == null) return groups;
        if (addon->AtkValues == null) return groups;

        var values = addon->AtkValuesSpan;
        if (values.Length <= CountValueIndex) return groups;

        var count = (int)values[CountValueIndex].UInt;
        if (count <= 0 || count > MaxBatchItems) return groups;
        if (values.Length < FirstItemValueIndex + count) return groups;

        var itemSheet = Svc.Data.GetExcelSheet<Item>();
        var jobSheet = Svc.Data.GetExcelSheet<ClassJob>();
        var byCategory = new Dictionary<uint, JobGroup>();

        for (var i = 0; i < count; i++)
        {
            var itemId = values[FirstItemValueIndex + i].UInt;
            if (itemId == 0) continue;

            var item = itemSheet.GetRowOrDefault(itemId);
            if (item == null) continue;

            var categoryId = item.Value.ClassJobCategory.RowId;
            if (!byCategory.TryGetValue(categoryId, out var group))
            {
                var categoryName = item.Value.ClassJobCategory.ValueNullable?.Name.ExtractText() ?? string.Empty;
                if (categoryName.Length == 0) categoryName = $"分類 #{categoryId}";

                group = new JobGroup(categoryId, categoryName, ResolveJobIcon(jobSheet, categoryName));
                byCategory[categoryId] = group;
                groups.Add(group);
            }

            group.Entries.Add(new ShopEntry(i, itemId));
        }

        return groups;
    }

    /// <summary>職業分類名稱剛好等於單一職業名稱時取該職業圖示（62100 + ClassJob.RowId），否則不給圖示。</summary>
    private static uint ResolveJobIcon(Lumina.Excel.ExcelSheet<ClassJob> jobSheet, string categoryName)
    {
        foreach (var job in jobSheet)
        {
            if (job.RowId == 0) continue;
            if (string.Equals(job.Name.ExtractText(), categoryName, StringComparison.Ordinal))
                return 62100 + job.RowId;
        }

        return 0;
    }

    private static int GetItemCount(uint itemId)
    {
        var manager = InventoryManager.Instance();
        return manager == null
                   ? 0
                   : manager->GetInventoryItemCount(itemId, false, true, true);
    }

    private void DrawOverlay()
    {
        var addon = UiHelper.GetAddon(AddonName);
        if (!UiHelper.IsReady(addon)) return;

        const ImGuiWindowFlags flags = ImGuiWindowFlags.AlwaysAutoResize |
                                       ImGuiWindowFlags.NoTitleBar |
                                       ImGuiWindowFlags.NoCollapse |
                                       ImGuiWindowFlags.NoScrollbar |
                                       ImGuiWindowFlags.NoSavedSettings;

        if (ImGui.Begin("###TCToolboxFreeShop", flags))
        {
            ImGui.SetWindowPos(new Vector2(addon->GetX() + 6, addon->GetY() - ImGui.GetWindowSize().Y - 4));

            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(new Vector4(1f, 0.85f, 0.35f, 1f), "一鍵領取");

            var groups = GetGroups(addon);
            if (groups.Count == 0)
            {
                ImGui.SameLine();
                ImGui.TextDisabled("（讀不到報酬清單）");
            }

            foreach (var group in groups)
            {
                using var id = ImRaii.PushId((int)group.CategoryRowId);
                ImGui.SameLine();

                using (ImRaii.Disabled(queue.IsBusy || group.Remaining == 0))
                {
                    var clicked = group.IconId != 0
                                      ? GameIcons.IconButton(group.IconId, group.Name, 30f, group.Remaining == 0)
                                      : ImGui.Button(group.Name, new Vector2(0, 30f));

                    if (clicked) StartBatch(group);
                }

                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip($"批次領取：{group.Name}（尚未領取 {group.Remaining} / {group.Entries.Count} 件）");
            }

            if (queue.IsBusy)
            {
                ImGui.SameLine();
                ImGui.TextDisabled(queue.CurrentStep ?? string.Empty);
                ImGui.SameLine();
                if (ImGui.Button("停止##freeshop"))
                {
                    queue.Abort();
                    Svc.Chat.Print("[TC Toolbox] 已手動停止批次領取。");
                }
            }
        }

        ImGui.End();
    }

    private void StartBatch(JobGroup group)
    {
        if (queue.IsBusy) return;

        var itemSheet = Svc.Data.GetExcelSheet<Item>();

        foreach (var entry in group.Entries)
        {
            var itemName = itemSheet.GetRowOrDefault(entry.ItemId)?.Name.ExtractText() ?? $"#{entry.ItemId}";
            var throttleKey = $"OptimizedFreeShop-{entry.Index}";

            queue.Enqueue($"領取 {itemName}", () =>
            {
                if (!UiHelper.IsAddonReady(AddonName)) return null;

                // 已經有了就跳過（重複領取會被遊戲擋下並跳錯誤訊息）
                if (GetItemCount(entry.ItemId) > 0) return true;

                var manager = InventoryManager.Instance();
                if (manager != null && manager->GetEmptySlotsInBag() == 0)
                {
                    Svc.Chat.PrintError("[TC Toolbox] 背包已滿，停止批次領取。");
                    return null;
                }

                if (!Throttle.Pass(throttleKey, 400)) return false;

                UiHelper.SendAgentEvent(AgentId.FreeShop, 0, 0, entry.Index);

                // 不當場回 true：下一輪再確認道具真的入手了，沒入手就重送
                return false;
            }, 12_000);
        }

        queue.Enqueue("批次領取完成", () =>
        {
            Svc.Chat.Print($"[TC Toolbox] {group.Name} 的報酬已全部領取。");
            return true;
        });
    }

    public override void DrawConfig()
    {
        var skip = Config.SkipConfirmation;
        if (ImGui.Checkbox("領取時自動按掉確認對話框", ref skip))
        {
            Config.SkipConfirmation = skip;
            Plugin.Instance.Config.Save();
        }

        using (ImRaii.PushIndent())
        {
            ImGui.TextDisabled("只在「報酬」視窗開著、而且提示文字確實是領取確認句時才按。");
            ImGui.TextDisabled("視窗開著時冒出來的交易申請或其他外掛的對話框一律不碰。");
        }
    }
}

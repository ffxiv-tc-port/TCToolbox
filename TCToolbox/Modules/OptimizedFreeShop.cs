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

    public override bool HasConfigUI => true;

    private const string AddonName = "FreeShop";

    /// <summary>清單筆數所在的 AtkValue 索引。</summary>
    private const int CountValueIndex = 3;

    /// <summary>第一筆道具 ID 所在的 AtkValue 索引。</summary>
    private const int FirstItemValueIndex = 65;

    /// <summary>單次批次領取的上限，避免 addon 版面改動時無限跑。</summary>
    private const int MaxBatchItems = 200;

    private readonly TaskQueue queue = new() { DefaultTimeoutMs = 15_000 };

    private OptimizedFreeShopConfig Config => Plugin.Instance.Config.FreeShop;

    protected override void OnEnable()
    {
        queue.OnTimeout = step => Svc.Chat.PrintError($"[TC Toolbox] 批次領取逾時，已停止：{step}");

        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "SelectYesno", OnSelectYesno);
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
    }

    private void OnUpdate(IFramework framework) => queue.Tick();

    private void OnFreeShopClosed(AddonEvent type, AddonArgs args)
    {
        InvalidateCache();

        if (!queue.IsBusy) return;
        queue.Abort();
        Svc.Chat.Print("[TC Toolbox] 報酬視窗已關閉，停止批次領取。");
    }

    /// <summary>只在報酬視窗開著時才自動確認——其他地方的 Yes/No 一律不碰。</summary>
    private void OnSelectYesno(AddonEvent type, AddonArgs args)
    {
        if (!Config.SkipConfirmation) return;
        if (!UiHelper.IsAddonReady(AddonName)) return;

        var addon = (AtkUnitBase*)args.Addon.Address;
        if (addon == null) return;

        UiHelper.FireCallback(addon, true, 0);
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
    private static List<JobGroup> ReadGroups(AtkUnitBase* addon)
    {
        var groups = new List<JobGroup>();
        if (addon == null) return groups;

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
            ImGui.TextDisabled("只在「報酬」視窗開著時生效，其他地方的是／否對話框不受影響。");
    }
}

using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 一鍵全修（跨容器）：開著遊戲的「修理」視窗時，在視窗旁邊多一顆按鈕，
/// 一次把「裝備中的裝備」與下拉選單裡的每一個容器全部修理完，不必自己切七次下拉再按七次。
/// </summary>
/// <remarks>
/// <para>
/// 📌 <b>做的事情與使用者自己操作完全相同</b>：呼叫的是遊戲自己在「全部修理」按鈕背後跑的那兩支函式
/// （<c>RepairManager::RepairEquipped</c> 與 <c>RepairManager::RepairAllItems</c>），
/// 參數也用遊戲自己會傳的那些值。不偽造封包、不改記憶體、不 hook。
/// </para>
/// <para>
/// 🔑 <b>參數不是猜的，是離線從台服 7.20 主程式的 <c>AgentRepair::ReceiveEvent</c> 反組譯出來的</b>
/// （2026-08-19）：
/// <list type="bullet">
/// <item>下拉索引 0 ⇒ <c>RepairEquipped(1000, isNpc, 0)</c>（1000＝<c>InventoryType.EquippedItems</c>）。
/// ⚠️ FFXIVClientStructs 的散文註解寫「7 ＝ Equipped」，<b>與台服實際的分派相反</b>——
/// 實際上 <c>InventoryContainerIndex == 0</c> 才是「裝備中的裝備」。散文註解沒有被驗證過，不要照抄。</item>
/// <item>下拉索引 1..6 ⇒ 先查一張 <c>.rdata</c> 的對照表（VA 0x1420642B0，內容
/// <c>{7, 0, 1, 2, 3, 4, 5}</c>）再呼叫 <c>RepairAllItems(isNpc, 表[索引], 0)</c>，
/// 也就是實際送出去的類別值是 <b>0..5 共六個</b>。</item>
/// <item>上界來自遊戲自己的 <c>cmp eax, 7 / jge</c>——索引 7 以上會被遊戲直接丟掉。</item>
/// <item><c>isNpc</c> ＝ <c>AgentRepair-&gt;UseSelfRepair == false</c>（遊戲用 <c>cmp byte[rsi+0x31], 0 / sete</c> 算出來的）。</item>
/// </list>
/// 🔴 因此本模組<b>不寫死任何原生節點 ID</b>。上游 Automaton <c>FasterRepairAll</c> 是去掛
/// <c>Repair</c> addon 的 <b>NodeId 12</b>，但台服 7.20 的 <c>ui/uld/Repair.uld</c> 裡
/// <b>Node 12 是下拉選單（Component#1013 DropDown），「全部修理」按鈕是 Node 16</b>
/// （2026-08-19 離線 ULD 傾印實證）——照抄那個常數會掛到錯的節點上。
/// </para>
/// <para>
/// ⚠️ 找不到視窗／取不到 agent／取不到 manager 時一律<b>什麼都不做</b>並寫一行 Information 記錄，
/// 最壞的情況是「按了沒反應」，不會是崩潰。
/// </para>
/// </remarks>
public sealed unsafe class RepairAllContainers : TcModule
{
    public override string InternalName => "RepairAllContainers";
    public override string DisplayName => "一鍵全修（跨容器）";

    public override string Description =>
        "開啟遊戲的「修理」視窗後，視窗上方會多出一顆按鈕：按一次就把裝備中的裝備與下拉選單裡的" +
        "每一個容器依序修理完，不必自己切換下拉選單。修理工與暗物質自行修理都適用；" +
        "視窗關閉、戰鬥中或步驟逾時會自動停止。";

    public override ModuleCategory Category => ModuleCategory.Inventory;

    /// <summary>開著不按按鈕的話，一件裝備都不會被修理。</summary>
    public override bool IsManualTrigger => true;

    public override bool HasConfigUI => true;

    /// <summary>遊戲修理視窗的 addon 名。</summary>
    private const string RepairAddon = "Repair";

    /// <summary>
    /// 「裝備中的裝備」在 <c>RepairEquipped</c> 的第一個參數上用的值。
    /// </summary>
    /// <remarks>
    /// 與 <c>InventoryType.EquippedItems</c> 同值；刻意寫成列舉轉型而不是 <c>1000</c>，
    /// 讓人一眼看得出這是容器代號而不是某個索引。
    /// </remarks>
    private const int EquippedInventoryType = (int)InventoryType.EquippedItems;

    /// <summary>
    /// 傳給 <c>RepairAllItems</c> 的類別值個數（0..5）。
    /// </summary>
    /// <remarks>
    /// 🔴 這個 6 不是猜的，見類別註解：台服 7.20 的對照表只有 <c>{7,0,1,2,3,4,5}</c> 七格，
    /// 而執行期真的會用到的是索引 1..6 那六格（值 0..5）。
    /// ⚠️ 值本身只是被原封不動塞進 <c>ExecuteCommand</c> 的參數，遊戲<b>沒有</b>拿它當陣列索引，
    /// 所以就算未來版本改了對照表，多送或少送一個類別的後果是「某個容器沒被修到」，不是越界。
    /// </remarks>
    private const int RepairCategoryCount = 6;

    private RepairAllContainersConfig Config => Plugin.Instance.Config.RepairAll;

    private readonly TaskQueue queue = new() { DefaultTimeoutMs = 30_000 };

    private int requestedSteps;

    protected override void OnEnable()
    {
        queue.OnTimeout = step => Svc.Chat.PrintError($"[TC Toolbox] 全修流程逾時，已停止：{step}");

        Svc.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, RepairAddon, OnRepairFinalize);
        Svc.Framework.Update += OnUpdate;
        Svc.PluginInterface.UiBuilder.Draw += DrawOverlay;

        Svc.Log.Information($"[{InternalName}] 模組啟用：等待「修理」視窗開啟後顯示按鈕。");
    }

    protected override void OnDisable()
    {
        Svc.AddonLifecycle.UnregisterListener(OnRepairFinalize);
        Svc.Framework.Update -= OnUpdate;
        Svc.PluginInterface.UiBuilder.Draw -= DrawOverlay;

        queue.Abort();
    }

    private void OnUpdate(IFramework framework) => queue.Tick();

    private void OnRepairFinalize(AddonEvent type, AddonArgs args)
    {
        if (!queue.IsBusy) return;
        queue.Abort();
        Svc.Chat.Print("[TC Toolbox] 修理視窗已關閉，全修流程已停止。");
        Svc.Log.Information($"[{InternalName}] 修理視窗關閉，中止佇列。");
    }

    private void DrawOverlay()
    {
        var addon = UiHelper.GetAddon(RepairAddon);
        if (!UiHelper.IsReady(addon)) return;

        const ImGuiWindowFlags flags = ImGuiWindowFlags.AlwaysAutoResize |
                                       ImGuiWindowFlags.NoTitleBar |
                                       ImGuiWindowFlags.NoCollapse |
                                       ImGuiWindowFlags.NoSavedSettings;

        if (ImGui.Begin("###TCToolboxRepairAll", flags))
        {
            ImGui.SetWindowPos(new Vector2(addon->GetX() + 6, addon->GetY() - ImGui.GetWindowSize().Y - 4));

            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(new Vector4(1f, 0.85f, 0.35f, 1f), DisplayName);

            ImGui.SameLine();
            using (ImRaii.Disabled(queue.IsBusy))
            {
                if (ImGui.Button("全部修理##repairAll"))
                    Start();
            }

            ImGui.SameLine();
            if (ImGui.Button("停止##repairAll"))
            {
                queue.Abort();
                Svc.Chat.Print("[TC Toolbox] 已手動停止全修流程。");
            }

            if (queue.IsBusy)
            {
                ImGui.SameLine();
                ImGui.TextDisabled(queue.CurrentStep ?? string.Empty);
            }
        }

        ImGui.End();
    }

    private void Start()
    {
        if (queue.IsBusy) return;

        requestedSteps = 0;

        // 裝備中的裝備。
        queue.Enqueue("修理裝備中的裝備", () => RunStep(isEquipped: true, category: 0));
        EnqueueGap();

        // 下拉選單裡的其餘容器（類別值 0..5）。
        for (var category = 0; category < RepairCategoryCount; category++)
        {
            var captured = category;
            queue.Enqueue($"修理容器 {captured + 1}/{RepairCategoryCount}", () => RunStep(isEquipped: false, category: captured));
            EnqueueGap();
        }

        queue.Enqueue("完成", () =>
        {
            var message = $"[TC Toolbox] 全修流程結束，共送出 {requestedSteps}/{RepairCategoryCount + 1} 次修理要求。";
            if (Config.AnnounceInChat) Svc.Chat.Print(message);
            Svc.Log.Information($"[{InternalName}] {message}");
            return true;
        });
    }

    /// <summary>
    /// 兩次修理要求之間的間隔：<b>先等固定時間，再等角色不忙</b>。
    /// </summary>
    /// <remarks>
    /// 🔴 順序不能反。暗物質自行修理會把角色設成 <see cref="ConditionFlag.Occupied39"/>
    /// （與精製／分解同一格），但那一格<b>不是在送出的那一幀就立起來的</b>——
    /// 送出後馬上檢查會看到「不忙」而直接通過，等於這道等待完全沒作用。
    /// 先過一段固定延遲再檢查，才擋得到。修理工修理不會設這一格，兩道都會很快通過。
    /// </remarks>
    private void EnqueueGap()
    {
        queue.EnqueueDelay(Math.Max(0, Config.StepIntervalMs), "間隔");
        queue.EnqueueWait("等待修理完成", NotBusyRepairing, 20_000);
    }

    private static bool NotBusyRepairing() => !Svc.Condition[ConditionFlag.Occupied39];

    /// <summary>
    /// 送出一次修理要求。
    /// 回傳 <c>true</c>＝這一步做完（含「條件不允許但可以略過」），<c>null</c>＝整條流程中止。
    /// </summary>
    private bool? RunStep(bool isEquipped, int category)
    {
        // 🔴 每一步都重新取指標，一個都不跨幀保存。
        if (!UiHelper.IsAddonReady(RepairAddon))
        {
            Svc.Log.Information($"[{InternalName}] 修理視窗已不在，停止流程。");
            return null;
        }

        if (Svc.Condition[ConditionFlag.InCombat])
        {
            Svc.Chat.PrintError("[TC Toolbox] 戰鬥中無法修理，已停止。");
            return null;
        }

        // 上一次自行修理還在跑就等它，不要疊著送（回 false＝下一 tick 重試，不是失敗）。
        if (Svc.Condition[ConditionFlag.Occupied39]) return false;

        var agent = AgentRepair.Instance();
        if (agent == null)
        {
            Svc.Log.Information($"[{InternalName}] 取不到 AgentRepair，停止流程。");
            return null;
        }

        var manager = RepairManager.Instance();
        if (manager == null)
        {
            Svc.Log.Information($"[{InternalName}] 取不到 RepairManager，停止流程。");
            return null;
        }

        // 遊戲自己的算法：cmp byte[agent+0x31], 0 / sete ⇒ 「不是自行修理」才算 NPC 修理。
        var isNpc = !agent->UseSelfRepair;

        if (agent->UseSelfRepair && !Config.IncludeSelfRepair)
        {
            Svc.Log.Information($"[{InternalName}] 目前是暗物質自行修理，且設定為不接手，跳過。");
            return null;
        }

        bool accepted;
        if (isEquipped)
            accepted = manager->RepairEquipped(EquippedInventoryType, isNpc, 0);
        else
            accepted = manager->RepairAllItems(isNpc, category, 0);

        requestedSteps++;

        Svc.Log.Information(
            $"[{InternalName}] 送出修理要求：{(isEquipped ? "裝備中" : $"類別 {category}")}"
            + $"（isNpc={isNpc}），遊戲回傳 {accepted}。");

        return true;
    }

    public override void DrawConfig()
    {
        ImGui.SetNextItemWidth(160f);
        var interval = Config.StepIntervalMs;
        if (ImGui.SliderInt("每個容器之間的間隔（毫秒）", ref interval, 0, 2000))
        {
            Config.StepIntervalMs = interval;
            Plugin.Instance.Config.Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("每一次修理要求都是真的送到伺服器。間隔太短沒有好處，也讓人來不及按停止。");

        var includeSelf = Config.IncludeSelfRepair;
        if (ImGui.Checkbox("暗物質自行修理時也接手", ref includeSelf))
        {
            Config.IncludeSelfRepair = includeSelf;
            Plugin.Instance.Config.Save();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "關閉時只在修理工那裡接手，自行修理維持遊戲原本的操作。\n" +
                "自行修理每一次都會佔用角色（與精製同一個狀態），所以流程會等上一次結束再送下一次。");
        }

        var announce = Config.AnnounceInChat;
        if (ImGui.Checkbox("結束時在聊天視窗回報", ref announce))
        {
            Config.AnnounceInChat = announce;
            Plugin.Instance.Config.Save();
        }
    }
}

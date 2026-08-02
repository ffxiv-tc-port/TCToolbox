using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 自動領取戰利水晶：開著「星裡路標」的報酬視窗（PvpReward）並按下我們的開始鈕之後，
/// 把所有待領取的系列賽階級獎勵一路領完。
/// 機制：重複觸發視窗上原本那顆「領取」按鈕自己的事件——等同你連點它，
/// 按鈕變成不可按（沒東西可領）就停。零 hook、不寫記憶體、不送任何自製指令。
/// 參考 DailyRoutines AutoClaimPVPRewards 設計重寫（API13、無 OmenTools／KamiToolKit 相依）。
/// </summary>
/// <remarks>
/// <para>與 DR 原版最大的差異在「怎麼領」：</para>
/// <list type="bullet">
/// <item>DR 是**繞過 UI**，直接呼叫遊戲的 <c>ExecuteCommand</c> 送指令碼 1200
/// （<c>CollectTrophyCrystal</c>）。指令碼是硬編碼的數字，我沒有辦法離線證明台服 7.20 的
/// 1200 就是這件事——猜錯就是對伺服器送出一個完全不相干的指令。所以這裡不採用。</item>
/// <item>改成重新觸發視窗上原生「領取」鈕的既有事件：走的是遊戲自己的驗證流程，
/// 節點編號萬一不對，最壞情況是按到別顆按鈕（例如關閉），不會送出錯誤指令。</item>
/// <item>DR 還會**把原生領取鈕藏起來**強迫使用者改用它的按鈕。這裡不動遊戲的節點樹，
/// 原本的按鈕照常留著。</item>
/// </list>
/// <para>
/// ⚠️ 未離線證明：原生「領取」鈕的節點編號 124（取自 DR 實機 DLL）。實機要驗的是——
/// 按下開始後是否真的逐階領取，以及全部領完後是否自行停止。
/// </para>
/// </remarks>
public sealed unsafe class AutoClaimPVPRewards : TcModule
{
    public override string InternalName => "AutoClaimPVPRewards";
    public override string DisplayName => "自動領取戰利水晶";

    public override string Description =>
        "在「星裡路標」報酬視窗上加一顆開始鈕，按下後把所有待領取的系列賽階級獎勵一路領完。" +
        "只有你自己按下開始才會跑；戰利水晶快到上限時會自動停下。";

    public override bool HasConfigUI => true;

    private const string AddonName = "PvpReward";

    /// <summary>原生「領取」按鈕的節點編號（取自 DR 實機 DLL，尚待實機確認）。</summary>
    private const uint ClaimButtonNodeId = 124;

    /// <summary>待領取階級數所在的 AtkValue 索引。</summary>
    private const int PendingCountValueIndex = 7;

    /// <summary>戰利水晶的 ItemId（台服 Item 表 36656＝戰利水晶）。</summary>
    private const uint TrophyCrystalItemId = 36656;

    /// <summary>單次流程的硬上限，避免按鈕狀態判讀不如預期時無限跑。</summary>
    private const int MaxClaims = 60;

    private readonly TaskQueue queue = new() { DefaultTimeoutMs = 15_000 };

    private int claimedCount;

    private AutoClaimPVPRewardsConfig Config => Plugin.Instance.Config.ClaimPvpRewards;

    protected override void OnEnable()
    {
        queue.OnTimeout = step => Svc.Chat.PrintError($"[TC Toolbox] 領取流程逾時，已停止：{step}");

        Svc.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, AddonName, OnAddonClosed);
        Svc.Framework.Update += OnUpdate;
        Svc.PluginInterface.UiBuilder.Draw += DrawOverlay;
    }

    protected override void OnDisable()
    {
        Svc.AddonLifecycle.UnregisterListener(OnAddonClosed);
        Svc.Framework.Update -= OnUpdate;
        Svc.PluginInterface.UiBuilder.Draw -= DrawOverlay;
        queue.Abort();
    }

    private void OnUpdate(IFramework framework) => queue.Tick();

    private void OnAddonClosed(AddonEvent type, AddonArgs args)
    {
        if (!queue.IsBusy) return;
        queue.Abort();
        Svc.Chat.Print($"[TC Toolbox] 報酬視窗已關閉，停止領取（本輪已領取 {claimedCount} 次）。");
    }

    private static int GetTrophyCrystalCount()
    {
        var manager = InventoryManager.Instance();
        return manager == null ? 0 : manager->GetInventoryItemCount(TrophyCrystalItemId, false, true, true);
    }

    /// <summary>讀待領取階級數；讀不到就回 -1（未知，交給按鈕狀態當終止條件）。</summary>
    private static int GetPendingCount(AtkUnitBase* addon)
    {
        if (addon == null) return -1;

        var values = addon->AtkValuesSpan;
        return values.Length <= PendingCountValueIndex ? -1 : (int)values[PendingCountValueIndex].UInt;
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

        if (ImGui.Begin("###TCToolboxPvpReward", flags))
        {
            ImGui.SetWindowPos(new Vector2(addon->GetX() + 6, addon->GetY() - ImGui.GetWindowSize().Y - 4));

            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(new Vector4(1f, 0.85f, 0.35f, 1f), "自動領取戰利水晶");

            var pending = GetPendingCount(addon);
            if (pending >= 0)
            {
                ImGui.SameLine();
                ImGui.TextDisabled($"待領取 {pending} 階");
            }

            ImGui.SameLine();
            using (ImRaii.Disabled(queue.IsBusy))
            {
                if (ImGui.Button("開始領取##pvpreward"))
                    Start();
            }

            ImGui.SameLine();
            if (ImGui.Button("停止##pvpreward"))
            {
                queue.Abort();
                Svc.Chat.Print($"[TC Toolbox] 已手動停止領取（本輪已領取 {claimedCount} 次）。");
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

        claimedCount = 0;

        for (var i = 0; i < MaxClaims; i++)
        {
            var step = i;
            queue.Enqueue($"領取第 {step + 1} 階", () =>
            {
                var addon = UiHelper.GetAddon(AddonName);
                if (!UiHelper.IsReady(addon)) return null;

                if (GetTrophyCrystalCount() >= Config.StopAtTrophyCrystals)
                {
                    Svc.Chat.PrintError(
                        $"[TC Toolbox] 戰利水晶已達 {Config.StopAtTrophyCrystals}，停止領取（本輪已領取 {claimedCount} 次）。");
                    return null;
                }

                var pending = GetPendingCount(addon);
                if (pending == 0)
                {
                    Svc.Chat.Print($"[TC Toolbox] 沒有待領取的獎勵了（本輪已領取 {claimedCount} 次）。");
                    return null;
                }

                var button = addon->GetComponentButtonById(ClaimButtonNodeId);
                if (button == null)
                {
                    Svc.Chat.PrintError("[TC Toolbox] 找不到原生的領取按鈕，已停止（節點編號可能因改版變動）。");
                    return null;
                }

                if (!Throttle.Pass("AutoClaimPVPRewards-Claim", 500)) return false;

                if (!UiHelper.ClickButton(addon, button))
                {
                    // 按鈕已經不能按＝沒東西可領了，正常收工
                    Svc.Chat.Print($"[TC Toolbox] 領取完畢（本輪已領取 {claimedCount} 次）。");
                    return null;
                }

                claimedCount++;
                return true;
            }, 15_000);

            queue.EnqueueDelay(600, "等待介面更新");
        }
    }

    public override void DrawConfig()
    {
        ImGui.SetNextItemWidth(200f);
        var stopAt = Config.StopAtTrophyCrystals;
        if (ImGui.SliderInt("戰利水晶到達此數量就停止", ref stopAt, 1000, 20000))
        {
            Config.StopAtTrophyCrystals = Math.Clamp(stopAt, 1000, 20000);
            Plugin.Instance.Config.Save();
        }

        using (ImRaii.PushIndent())
            ImGui.TextDisabled("戰利水晶持有上限是 20000，超過的部分會直接消失，所以預設留一點餘裕。");
    }
}

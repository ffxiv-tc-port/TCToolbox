using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 投影台重複幻影取出：把投影台裡同一件裝備的多餘份數取回背包，一次一件、按下按鈕才動。
///
/// 🔴 <b>純手動</b>：只有在設定面板按下「開始」才會執行，不掛任何自動觸發。
/// 參考 DailyRoutines <c>AutoRemoveDuplicateGlamours</c> 的用途重寫（API13、無 OmenTools 相依）。
///
/// 與 DR 的兩處刻意差異，理由寫在對應位置：
///  1. 每輪重新快照、一次只送一件（見 <see cref="PrismBoxRestoreRunner"/>）。
///  2. 預設把「染色不同」視為不同幻影（見 <see cref="Configuration.GlamourDuplicateCleanup"/>）。
/// </summary>
public sealed class GlamourDuplicateCleanup : TcModule
{
    public override string InternalName => "GlamourDuplicateCleanup";
    public override string DisplayName => "投影台：重複幻影取出";

    public override string Description =>
        "手動按鈕：掃描投影台，把同一件裝備多出來的份數取回背包（保留索引最前面那一份）。" +
        "需要先開啟「投影台」視窗；背包滿了會停下並提示。不會自動執行。";

    public override ModuleCategory Category => ModuleCategory.Inventory;

    public override bool HasConfigUI => true;

    private readonly PrismBoxRestoreRunner runner = new("投影台重複幻影取出");

    private GlamourDuplicateCleanupConfig Config => Plugin.Instance.Config.GlamourDuplicateCleanup;

    /// <summary>面板上顯示的預估件數。⚠️ 算不出來時是 <c>null</c>（畫成「?」），不是 0。</summary>
    private int? previewCount;

    private string previewReason = string.Empty;

    protected override void OnEnable() => Svc.Framework.Update += OnUpdate;

    protected override void OnDisable()
    {
        Svc.Framework.Update -= OnUpdate;
        runner.Reset();
        previewCount = null;
        previewReason = string.Empty;
    }

    private void OnUpdate(IFramework _) => runner.Tick();

    /// <summary>
    /// 挑出「重複」的那一件。索引小的算本尊、之後同款的都算多餘。
    ///
    /// <para>比對鍵預設是 <c>(道具ID, 染色0, 染色1)</c>，而 DR 只用道具 ID。
    /// 這是刻意的：同一件裝備染成兩個顏色在投影台裡是**兩種可用的外觀**，
    /// 只看道具 ID 會把其中一種當成多餘的取走。想完全比照 DR 可以關掉這個選項。</para>
    /// </summary>
    private GlamourDuplicateCleanupPickResult PickDuplicate(
        IReadOnlyList<PrismBox.Entry> snapshot, IReadOnlySet<uint> rejected)
    {
        var seen = new HashSet<(uint Item, byte Stain0, byte Stain1)>();
        var total = 0;
        PrismBoxRestoreRunner.Pick? first = null;

        foreach (var entry in snapshot)
        {
            var key = Config.DistinguishByDye
                ? (entry.BaseItemId, entry.Stain0, entry.Stain1)
                : (entry.BaseItemId, (byte)0, (byte)0);

            if (seen.Add(key)) continue;

            total++;
            if (first != null || rejected.Contains(entry.FullItemId)) continue;

            first = new PrismBoxRestoreRunner.Pick(
                entry.Index, entry.FullItemId, ItemNames.Get(entry.BaseItemId));
        }

        return new GlamourDuplicateCleanupPickResult(first, total);
    }

    private readonly record struct GlamourDuplicateCleanupPickResult(
        PrismBoxRestoreRunner.Pick? Pick, int TotalDuplicates);

    private void RefreshPreview()
    {
        if (!PrismBox.TryReady(out var reason))
        {
            previewCount = null;
            previewReason = reason;
            return;
        }

        previewReason = string.Empty;
        previewCount = PickDuplicate(PrismBox.Snapshot(), new HashSet<uint>()).TotalDuplicates;
    }

    public override void DrawConfig()
    {
        // 面板開著時每秒重算一次即可；每幀重算只是徒增配置。
        if (Throttle.Pass("GlamourDuplicateCleanup-Preview", 1_000)) RefreshPreview();

        var distinguish = Config.DistinguishByDye;
        if (ImGui.Checkbox("染色不同的視為不同幻影（不當成重複）", ref distinguish))
        {
            Config.DistinguishByDye = distinguish;
            Plugin.Instance.Config.Save();
            Throttle.Reset("GlamourDuplicateCleanup-Preview");
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "開啟（預設）：同一件裝備染成不同顏色會各自保留。\n" +
                "關閉：完全比照 DailyRoutines，只看道具編號，同款只留一份。");
        }

        ImGui.Spacing();

        // 「不知道」要在列上看得見：算不出來時畫「?」與原因，不畫 0。
        ImGui.AlignTextToFramePadding();
        if (previewCount is { } count)
            ImGui.TextUnformatted($"目前可取出的重複幻影：{count} 件");
        else
            ImGui.TextDisabled($"目前可取出的重複幻影：？（{previewReason}）");

        ImGui.Spacing();

        using (ImRaii.Disabled(runner.IsBusy || previewCount is null or 0))
        {
            if (ImGui.Button("開始取出##glamour-dup"))
                runner.Start((snapshot, rejected) => PickDuplicate(snapshot, rejected).Pick);
        }

        ImGui.SameLine();
        using (ImRaii.Disabled(!runner.IsBusy))
        {
            if (ImGui.Button("停止##glamour-dup"))
                runner.Abort();
        }

        if (runner.IsBusy)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"{runner.CurrentStep}（已取出 {runner.RestoredCount} 件）");
        }

        ImGui.TextColored(new Vector4(1f, 0.8f, 0.35f, 1f),
                          "⚠ 取出的幻影會回到背包，不會消失；但背包沒空位時流程會停下。");
    }
}

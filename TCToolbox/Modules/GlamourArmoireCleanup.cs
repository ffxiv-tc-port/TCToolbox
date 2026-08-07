using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 投影台裡「可以收進收藏櫃」的裝備取出：收藏櫃（Armoire）本身就能當投影來源且不佔投影台格數，
/// 所以這些裝備放在投影台是純浪費格子。手動按鈕，一次一件取回背包。
///
/// 🔴 <b>純手動</b>：只有按下「開始」才動，不掛任何自動觸發。
/// 🔴 <b>只負責取出到背包</b>，**不會**幫你放進收藏櫃 —— 那要在收藏櫃視窗自己收，
/// 我們不去點原生視窗（點錯一格的代價太高，而且那條路的索引沒有離線可驗的依據）。
///
/// 📌 判斷依據是 Lumina <c>Cabinet</c> 表，**不寫死道具清單**。
/// 台服 7.20 dump（<c>exd-tc/7.20/Cabinet.csv</c>）有 1048 列、欄位為 <c>Item</c>，
/// 與這裡讀的欄位一致。改版新增可收納道具時自動跟上。
///
/// 參考 DailyRoutines <c>AutoRemoveArmoireItemsFromDresser</c> 的用途重寫（API13、無 OmenTools 相依）。
/// </summary>
public sealed class GlamourArmoireCleanup : TcModule
{
    public override string InternalName => "GlamourArmoireCleanup";
    public override string DisplayName => "投影台：收藏櫃可收納裝備取出";

    public override string Description =>
        "手動按鈕：把投影台裡「可以收進收藏櫃」的裝備取回背包，空出投影台格數（收藏櫃本身就能當投影來源）。" +
        "需要先開啟「投影台」視窗。取出後請自行到收藏櫃收納。不會自動執行。";

    public override ModuleCategory Category => ModuleCategory.Inventory;

    public override bool HasConfigUI => true;

    private readonly PrismBoxRestoreRunner runner = new("投影台收藏櫃裝備取出");

    /// <summary>收藏櫃收得下的道具 ID。啟用時建一次，之後只讀。</summary>
    private HashSet<uint> armoireItems = [];

    private int? previewCount;
    private string previewReason = string.Empty;

    protected override void OnEnable()
    {
        armoireItems = BuildArmoireItemSet();

        // 🔑 「回 0」比「報錯」常見：資料表讀不到時整個模組會安靜地什麼都不做。
        // 所以把筆數寫進 Information 級記錄，讓「表是空的」看得出來。
        Svc.Log.Information($"[{InternalName}] Cabinet 表載入 {armoireItems.Count} 筆可收納道具。");

        Svc.Framework.Update += OnUpdate;
    }

    protected override void OnDisable()
    {
        Svc.Framework.Update -= OnUpdate;
        runner.Reset();
        armoireItems = [];
        previewCount = null;
        previewReason = string.Empty;
    }

    private void OnUpdate(IFramework _) => runner.Tick();

    private static HashSet<uint> BuildArmoireItemSet()
    {
        var result = new HashSet<uint>();
        var sheet = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Cabinet>();

        foreach (var row in sheet)
        {
            var itemId = row.Item.RowId;
            if (itemId != 0) result.Add(itemId);
        }

        return result;
    }

    private (PrismBoxRestoreRunner.Pick? Pick, int Total) PickArmoireItem(
        IReadOnlyList<PrismBox.Entry> snapshot, IReadOnlySet<uint> rejected)
    {
        var total = 0;
        PrismBoxRestoreRunner.Pick? first = null;

        foreach (var entry in snapshot)
        {
            if (!armoireItems.Contains(entry.BaseItemId)) continue;

            total++;
            if (first != null || rejected.Contains(entry.FullItemId)) continue;

            first = new PrismBoxRestoreRunner.Pick(
                entry.Index, entry.FullItemId, ItemNames.Get(entry.BaseItemId));
        }

        return (first, total);
    }

    private void RefreshPreview()
    {
        if (armoireItems.Count == 0)
        {
            previewCount = null;
            previewReason = "Cabinet 資料表載入失敗";
            return;
        }

        if (!PrismBox.TryReady(out var reason))
        {
            previewCount = null;
            previewReason = reason;
            return;
        }

        previewReason = string.Empty;
        previewCount = PickArmoireItem(PrismBox.Snapshot(), new HashSet<uint>()).Total;
    }

    public override void DrawConfig()
    {
        if (Throttle.Pass("GlamourArmoireCleanup-Preview", 1_000)) RefreshPreview();

        ImGui.AlignTextToFramePadding();
        if (previewCount is { } count)
            ImGui.TextUnformatted($"投影台裡可收進收藏櫃的裝備：{count} 件");
        else
            ImGui.TextDisabled($"投影台裡可收進收藏櫃的裝備：？（{previewReason}）");

        ImGui.Spacing();

        using (ImRaii.Disabled(runner.IsBusy || previewCount is null or 0))
        {
            if (ImGui.Button("開始取出##glamour-armoire"))
                runner.Start((snapshot, rejected) => PickArmoireItem(snapshot, rejected).Pick);
        }

        ImGui.SameLine();
        using (ImRaii.Disabled(!runner.IsBusy))
        {
            if (ImGui.Button("停止##glamour-armoire"))
                runner.Abort();
        }

        if (runner.IsBusy)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"{runner.CurrentStep}（已取出 {runner.RestoredCount} 件）");
        }

        ImGui.TextColored(new Vector4(1f, 0.8f, 0.35f, 1f),
                          "⚠ 只會取回背包，不會自動收進收藏櫃 —— 取出後請自行到收藏櫃收納，否則只是換個地方佔位。");
    }
}

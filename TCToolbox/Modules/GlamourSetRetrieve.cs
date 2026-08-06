using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 投影台整套幻影取出：依遊戲「套裝幻影化」用的裝備組合（Lumina <c>MirageStoreSetItem</c>），
/// 找出投影台裡湊得齊的整組，一件一件取回背包。手動按鈕，不自動執行。
///
/// 🔴 <b>刻意只做「取出」這一半。</b>DailyRoutines <c>AutoAttireItems</c> 還有一半是
/// 自動把「套裝幻影化」視窗填好並送出，那一半依賴
/// <c>AtkValues[15]</c>／<c>AtkValues[20 + i * 7]</c> 的版面位置、9999 這個哨兵值、
/// LogMessage 4280、以及 ContextIconMenu 的常數 1021003 —— 全部是版本相依的寫死值，
/// 而且在台服失效時的表現是**靜默做錯事**（填錯格、點錯選單項），不是報錯。
/// 使用者已裁決不做那一半，這裡連程式碼都不留，避免以後有人「順手打開」。
///
/// 📌 資料來源是 Lumina <c>MirageStoreSetItem</c>，不寫死組合。
/// 台服 7.20 dump（<c>exd-tc/7.20/MirageStoreSetItem.csv</c>）有 528 列、
/// 11 個部位欄位（MainHand/OffHand/Head/Body/Hands/Legs/Feet/Earrings/Necklace/Bracelets/Ring），
/// 與這裡讀的欄位一致 —— **沒有腰部欄位**，不要照國際服的舊資料補一欄上去。
/// </summary>
public sealed class GlamourSetRetrieve : TcModule
{
    public override string InternalName => "GlamourSetRetrieve";
    public override string DisplayName => "投影台：整套幻影取出";

    public override string Description =>
        "手動按鈕：依遊戲「套裝幻影化」的裝備組合，把投影台裡湊得齊整組的幻影一次取回背包（一件一件送出）。" +
        "需要先開啟「投影台」視窗；背包滿了會停下並提示。不會自動執行，也不會去動「套裝幻影化」視窗。";

    public override bool HasConfigUI => true;

    /// <summary>一個可整組取出的裝備組合。</summary>
    private sealed record MirageSet(uint SetItemId, string Name, IReadOnlyList<uint> Pieces);

    private readonly PrismBoxRestoreRunner runner = new("投影台整套幻影取出");

    private List<MirageSet> sets = [];

    // ── 一次執行期間的進度狀態（每次 Start 重置）──
    private uint? currentSetId;
    private readonly HashSet<uint> takenPieces = [];
    private readonly HashSet<uint> finishedSets = [];

    private int? previewSetCount;
    private int previewPieceCount;
    private string previewReason = string.Empty;

    private GlamourSetRetrieveConfig Config => Plugin.Instance.Config.GlamourSetRetrieve;

    protected override void OnEnable()
    {
        sets = BuildSets();

        // 🔑 資料表讀不到時本模組會安靜地「找不到任何整組」，跟「真的沒有」長得一模一樣。
        // 把筆數寫成 Information 級，讓這兩種情況分得開（使用者跑 LogLevel 2）。
        Svc.Log.Information($"[{InternalName}] MirageStoreSetItem 表載入 {sets.Count} 組可整套取出的裝備組合。");

        Svc.Framework.Update += OnUpdate;
    }

    protected override void OnDisable()
    {
        Svc.Framework.Update -= OnUpdate;
        runner.Reset();
        ResetProgress();
        sets = [];
        previewSetCount = null;
        previewPieceCount = 0;
        previewReason = string.Empty;
    }

    private void OnUpdate(IFramework _) => runner.Tick();

    private void ResetProgress()
    {
        currentSetId = null;
        takenPieces.Clear();
        finishedSets.Clear();
    }

    private static List<MirageSet> BuildSets()
    {
        var result = new List<MirageSet>();
        var sheet = Svc.Data.GetExcelSheet<MirageStoreSetItem>();
        var itemSheet = Svc.Data.GetExcelSheet<Item>();

        foreach (var row in sheet)
        {
            // 組合本身也是一件道具（用它的名稱當組合名）。名稱空的列是佔位列，跳過。
            var name = itemSheet.GetRowOrDefault(row.RowId)?.Name.ExtractText();
            if (string.IsNullOrWhiteSpace(name)) continue;

            var pieces = new List<uint>(11);
            AddPiece(pieces, row.MainHand.RowId);
            AddPiece(pieces, row.OffHand.RowId);
            AddPiece(pieces, row.Head.RowId);
            AddPiece(pieces, row.Body.RowId);
            AddPiece(pieces, row.Hands.RowId);
            AddPiece(pieces, row.Legs.RowId);
            AddPiece(pieces, row.Feet.RowId);
            AddPiece(pieces, row.Earrings.RowId);
            AddPiece(pieces, row.Necklace.RowId);
            AddPiece(pieces, row.Bracelets.RowId);
            AddPiece(pieces, row.Ring.RowId);

            if (pieces.Count == 0) continue;
            result.Add(new MirageSet(row.RowId, name, pieces));
        }

        return result;
    }

    /// <summary>⚠️ 判斷是 <c>&gt; 1</c> 不是 <c>!= 0</c>：這些欄位用 0 與 1 兩個值當「沒有這個部位」。</summary>
    private static void AddPiece(List<uint> pieces, uint itemId)
    {
        if (itemId > 1 && !pieces.Contains(itemId)) pieces.Add(itemId);
    }

    /// <summary>把快照整理成「道具 ID → 該道具在投影台裡的所有格」。避免每個部位都線性掃 800 格。</summary>
    private static Dictionary<uint, List<PrismBox.Entry>> BuildLookup(IReadOnlyList<PrismBox.Entry> snapshot)
    {
        var lookup = new Dictionary<uint, List<PrismBox.Entry>>();
        foreach (var entry in snapshot)
        {
            if (!lookup.TryGetValue(entry.BaseItemId, out var list))
                lookup[entry.BaseItemId] = list = [];
            list.Add(entry);
        }

        return lookup;
    }

    private bool TryFindPiece(
        Dictionary<uint, List<PrismBox.Entry>> lookup, uint pieceId, IReadOnlySet<uint> rejected,
        out PrismBox.Entry found)
    {
        found = default;
        if (!lookup.TryGetValue(pieceId, out var candidates)) return false;

        foreach (var entry in candidates)
        {
            if (Config.SkipDyedItems && entry.IsDyed) continue;
            if (rejected.Contains(entry.FullItemId)) continue;

            found = entry;
            return true;
        }

        return false;
    }

    /// <summary>這一組現在有幾個部位取得到。</summary>
    private int CountAvailablePieces(
        MirageSet set, Dictionary<uint, List<PrismBox.Entry>> lookup, IReadOnlySet<uint> rejected)
    {
        var available = 0;
        foreach (var piece in set.Pieces)
        {
            if (TryFindPiece(lookup, piece, rejected, out _)) available++;
        }

        return available;
    }

    private bool Qualifies(
        MirageSet set, Dictionary<uint, List<PrismBox.Entry>> lookup, IReadOnlySet<uint> rejected)
    {
        var available = CountAvailablePieces(set, lookup, rejected);
        return Config.OnlyCompleteSets ? available == set.Pieces.Count : available > 0;
    }

    /// <summary>
    /// 挑下一件。
    ///
    /// 🔴 <b>一旦開始處理某一組，就把它做完再換下一組。</b>
    /// 不能每輪都「重新找一組合格的」—— 取走第一件之後那一組就不再「完整」了，
    /// 下一輪重新評估會直接跳過它，結果是每組都只取出一件、湊不成整套，
    /// 而且看起來像是正常跑完（沒有任何錯誤訊息）。
    /// </summary>
    private PrismBoxRestoreRunner.Pick? PickNext(
        IReadOnlyList<PrismBox.Entry> snapshot, IReadOnlySet<uint> rejected)
    {
        var lookup = BuildLookup(snapshot);

        // guard：每一圈至少會讓一組進 finishedSets，所以圈數上限就是組數。
        for (var guard = 0; guard <= sets.Count; guard++)
        {
            if (currentSetId is null)
            {
                MirageSet? next = null;
                foreach (var set in sets)
                {
                    if (finishedSets.Contains(set.SetItemId)) continue;
                    if (!Qualifies(set, lookup, rejected)) continue;

                    next = set;
                    break;
                }

                if (next is null) return null;

                currentSetId = next.SetItemId;
                takenPieces.Clear();
            }

            var current = sets.Find(s => s.SetItemId == currentSetId.Value);
            if (current is null)
            {
                currentSetId = null;
                continue;
            }

            foreach (var piece in current.Pieces)
            {
                if (takenPieces.Contains(piece)) continue;
                if (!TryFindPiece(lookup, piece, rejected, out var entry)) continue;

                takenPieces.Add(piece);
                return new PrismBoxRestoreRunner.Pick(
                    entry.Index, entry.FullItemId, $"{current.Name}／{ItemNames.Get(piece)}");
            }

            // 這一組沒有還取得到的部位了。
            Svc.Chat.Print(
                $"[TC Toolbox]「{current.Name}」：已送出 {takenPieces.Count}/{current.Pieces.Count} 件取出請求。");
            finishedSets.Add(current.SetItemId);
            currentSetId = null;
        }

        return null;
    }

    private void RefreshPreview()
    {
        if (sets.Count == 0)
        {
            previewSetCount = null;
            previewReason = "MirageStoreSetItem 資料表載入失敗";
            return;
        }

        if (!PrismBox.TryReady(out var reason))
        {
            previewSetCount = null;
            previewReason = reason;
            return;
        }

        previewReason = string.Empty;

        var lookup = BuildLookup(PrismBox.Snapshot());
        var empty = new HashSet<uint>();
        var setCount = 0;
        var pieceCount = 0;

        foreach (var set in sets)
        {
            var available = CountAvailablePieces(set, lookup, empty);
            var ok = Config.OnlyCompleteSets ? available == set.Pieces.Count : available > 0;
            if (!ok) continue;

            setCount++;
            pieceCount += available;
        }

        previewSetCount = setCount;
        previewPieceCount = pieceCount;
    }

    public override void DrawConfig()
    {
        if (Throttle.Pass("GlamourSetRetrieve-Preview", 1_000)) RefreshPreview();

        var onlyComplete = Config.OnlyCompleteSets;
        if (ImGui.Checkbox("只取湊得齊整組的組合", ref onlyComplete))
        {
            Config.OnlyCompleteSets = onlyComplete;
            Plugin.Instance.Config.Save();
            Throttle.Reset("GlamourSetRetrieve-Preview");
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("關閉後，只要組合裡有任何一件在投影台就會取出（會拆散不完整的組合）。");

        var skipDyed = Config.SkipDyedItems;
        if (ImGui.Checkbox("跳過已染色的幻影", ref skipDyed))
        {
            Config.SkipDyedItems = skipDyed;
            Plugin.Instance.Config.Save();
            Throttle.Reset("GlamourSetRetrieve-Preview");
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("預設開啟：已染色的通常是特地調過的外觀，不要被整組取出帶走。");

        ImGui.Spacing();

        ImGui.AlignTextToFramePadding();
        if (previewSetCount is { } count)
            ImGui.TextUnformatted($"目前可取出：{count} 組（共 {previewPieceCount} 件）");
        else
            ImGui.TextDisabled($"目前可取出：？（{previewReason}）");

        ImGui.Spacing();

        using (ImRaii.Disabled(runner.IsBusy || previewSetCount is null or 0))
        {
            if (ImGui.Button("開始取出##glamour-set"))
            {
                ResetProgress();
                runner.Start(PickNext);
            }
        }

        ImGui.SameLine();
        using (ImRaii.Disabled(!runner.IsBusy))
        {
            if (ImGui.Button("停止##glamour-set"))
                runner.Abort();
        }

        if (runner.IsBusy)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"{runner.CurrentStep}（已取出 {runner.RestoredCount} 件）");
        }

        ImGui.TextColored(new Vector4(1f, 0.8f, 0.35f, 1f),
                          "⚠ 整組件數可能很多，開始前請確認背包空位夠 —— 空位用完會停在半途（已取出的不會退回）。");
    }
}

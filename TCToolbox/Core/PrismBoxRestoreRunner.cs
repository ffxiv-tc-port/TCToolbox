using System.Collections.Generic;

namespace TCToolbox.Core;

/// <summary>
/// 「從投影台一件一件取回背包」的共用執行器。三個投影台模組共用同一條流程，
/// 差別只在 <see cref="NextPicker"/>（要挑哪一件）。
///
/// 🔴 <b>每一輪都重新快照、重新挑一件</b>，而不是一次算好一整串索引再逐一送出。
/// 理由：<c>RestorePrismBoxItem</c> 是**非同步請求**，伺服器處理完之後
/// <c>PrismBoxItemIds</c> 會不會就地填 0、還是把後面的往前遞補，我們**無法離線證明**。
/// 若它會遞補，事先算好的索引從第二件起就全部指向別的道具 —— 而那個錯法是靜默的
/// （取出的是別的幻影，沒有任何錯誤訊息）。每輪重新快照讓兩種行為都正確。
/// DailyRoutines 的三個對應模組都是「一次算好整串索引再送」，這裡刻意不照抄。
///
/// 🔴 <b>送出之後一定要等投影台真的變了才做下一件。</b>判準有兩個，成立一個就算：
/// 非空格數變少（就地填 0 或遞補都會少一格）、或那一格已經不是原來那件道具。
/// 只看其中一個在「重複幻影相鄰」的情況下會誤判（遞補進來的剛好同 ID）。
/// </summary>
public sealed class PrismBoxRestoreRunner
{
    /// <summary>這一輪要取出的那一件。</summary>
    /// <param name="Index">投影台索引（<c>RestorePrismBoxItem</c> 的參數）。</param>
    /// <param name="FullItemId">含 HQ 編碼的原始 ID，用來確認索引沒有過期。</param>
    /// <param name="Label">給使用者看的名稱。</param>
    public readonly record struct Pick(int Index, uint FullItemId, string Label);

    /// <summary>挑下一件。回傳 <c>null</c> 代表沒有東西要處理了（正常結束）。</summary>
    /// <param name="snapshot">目前投影台內容的受管理複本。</param>
    /// <param name="rejected">本輪已被遊戲拒絕過的 <c>FullItemId</c>，必須排除，否則會無限重試。</param>
    public delegate Pick? NextPicker(IReadOnlyList<PrismBox.Entry> snapshot, IReadOnlySet<uint> rejected);

    private readonly string tag;
    private readonly TaskQueue queue = new() { DefaultTimeoutMs = 15_000 };
    private readonly HashSet<uint> rejected = [];

    /// <summary>
    /// 一次執行的回合上限。投影台最多 800 格，正常情況一回合處理一件，所以永遠碰不到。
    /// ⚠️ 這是**防止無限迴圈的保險絲**，不是業務規則：若出現「挑到了但沒能取出、也沒被記進
    /// <see cref="rejected"/>」的組合（例如快照與實際狀態持續對不上），流程會一直重來。
    /// TaskQueue 的逾時只管單一步驟，管不到「每步都很快但永遠跑不完」。
    /// </summary>
    private const int MaxIterations = 1_000;

    private NextPicker? picker;
    private int restoredCount;
    private int iterations;
    private bool lastRestoreAccepted;

    public PrismBoxRestoreRunner(string tag)
    {
        this.tag = tag;
        queue.OnTimeout = step =>
        {
            // Information 級：使用者跑 LogLevel 2，這是要他回報的診斷。
            Svc.Log.Information(
                $"[{tag}] 流程在「{step}」逾時中止，本輪已取出 {restoredCount} 件。" +
                "若卡在等待伺服器更新，代表 RestorePrismBoxItem 送出後投影台內容沒有變化。");
            Svc.Chat.PrintError(
                $"[TC Toolbox] {tag}：等待投影台更新逾時，已停止（本輪已取出 {restoredCount} 件）。");
        };
    }

    public bool IsBusy => queue.IsBusy;

    public string? CurrentStep => queue.CurrentStep;

    public int RestoredCount => restoredCount;

    /// <summary>由擁有者模組在 <c>Framework.Update</c> 裡呼叫。</summary>
    public void Tick() => queue.Tick();

    public void Start(NextPicker next)
    {
        if (queue.IsBusy) return;

        rejected.Clear();
        restoredCount = 0;
        iterations = 0;
        lastRestoreAccepted = false;
        picker = next;
        EnqueueNext();
    }

    public void Abort(bool notify = true)
    {
        if (!queue.IsBusy) return;

        queue.Abort();
        if (notify)
            Svc.Chat.Print($"[TC Toolbox] {tag}：已停止（本輪已取出 {restoredCount} 件）。");
    }

    /// <summary>模組停用時用；不發聊天訊息。</summary>
    public void Reset()
    {
        queue.Abort();
        rejected.Clear();
        picker = null;
        restoredCount = 0;
        iterations = 0;
        lastRestoreAccepted = false;
    }

    private void EnqueueNext()
    {
        queue.Enqueue("尋找下一件", () =>
        {
            if (picker == null) return null;

            if (++iterations > MaxIterations)
            {
                Svc.Log.Information(
                    $"[{tag}] 回合數超過上限 {MaxIterations}，強制中止（本輪已取出 {restoredCount} 件）。" +
                    "這代表流程一直挑到同一件卻取不出來，請回報。");
                Svc.Chat.PrintError($"[TC Toolbox] {tag}：流程異常反覆，已強制停止（本輪已取出 {restoredCount} 件）。");
                return null;
            }

            if (!PrismBox.TryReady(out var reason))
            {
                Svc.Log.Information($"[{tag}] 中止：{reason}（本輪已取出 {restoredCount} 件）");
                Svc.Chat.PrintError($"[TC Toolbox] {tag}：{reason}");
                return null;
            }

            if (PrismBox.EmptyBagSlots() <= 0)
            {
                Svc.Log.Information($"[{tag}] 背包已滿，中止（本輪已取出 {restoredCount} 件）");
                Svc.Chat.PrintError(
                    $"[TC Toolbox] {tag}：背包已滿，已停止（本輪已取出 {restoredCount} 件）。" +
                    "請清出空間後再按一次。");
                return null;
            }

            var snapshot = PrismBox.Snapshot();
            var before = snapshot.Count;

            var pick = picker(snapshot, rejected);
            if (pick is not { } target)
            {
                Svc.Chat.Print($"[TC Toolbox] {tag}：完成，本輪共取出 {restoredCount} 件。");
                Svc.Log.Information($"[{tag}] 完成，本輪共取出 {restoredCount} 件。");
                return true;
            }

            queue.Enqueue($"取出 {target.Label}", () =>
            {
                // 節流：一次一件，不要在同一幀連發請求。
                if (!Throttle.Pass($"PrismBoxRestore-{tag}", 250)) return false;

                // 快照與現在可能差一幀；索引不再是同一件就整輪重來，不冒險送出。
                if (!PrismBox.IsEntryAt(target.Index, target.FullItemId))
                {
                    Svc.Log.Information(
                        $"[{tag}] 索引 {target.Index} 的內容已變動（原為 {target.FullItemId}），跳過本次並重新掃描。");
                    lastRestoreAccepted = false;
                    return true;
                }

                lastRestoreAccepted = PrismBox.Restore(target.Index);
                if (!lastRestoreAccepted)
                {
                    // 遊戲當場拒絕。記進 rejected 避免下一輪又挑到同一件而無限迴圈。
                    rejected.Add(target.FullItemId);
                    Svc.Log.Information(
                        $"[{tag}] 遊戲拒絕取出「{target.Label}」（索引 {target.Index}）—— " +
                        "CS 的說明是「已持有的獨特道具」或「背包空間不足」。本輪跳過這件。");
                }

                return true;
            }, 10_000);

            queue.Enqueue("等待投影台更新", () =>
            {
                if (!lastRestoreAccepted) return true;

                if (!PrismBox.TryReady(out var whyNot))
                {
                    Svc.Log.Information($"[{tag}] 等待期間投影台失效：{whyNot}");
                    Svc.Chat.PrintError($"[TC Toolbox] {tag}：{whyNot}");
                    return null;
                }

                var live = PrismBox.LiveCount();
                if (live < 0) return false;

                // 兩個判準任一成立就算伺服器已處理（見類別說明）。
                if (live < before || !PrismBox.IsEntryAt(target.Index, target.FullItemId))
                {
                    restoredCount++;
                    return true;
                }

                return false;
            }, 15_000);

            queue.EnqueueDelay(120, "間隔");
            EnqueueNext();
            return true;
        }, 15_000);
    }
}

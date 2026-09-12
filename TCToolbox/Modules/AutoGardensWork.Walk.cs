using System;
using System.Numerics;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 自動園圃作業的<b>走位層</b>：在同一張圖裡自己走到下一格園圃旁邊。
/// </summary>
/// <remarks>
/// 🔴 <b>不上坐騎、不飛、不傳送、不跨區。</b>
/// 🔴 <b>失敗一律「安全放棄」，絕不重試。</b>任何一格走不到就設下
/// <see cref="walkGaveUpThisRound"/>，這一輪剩下的地壟全部不再嘗試走位。
/// </remarks>
public sealed unsafe partial class AutoGardensWork
{
    /// <summary>走到距離地壟這麼近就算到了（碼）。</summary>
    /// <remarks>
    /// 🔴 這個值必須<b>大於</b> <see cref="WalkNavTolerance"/>，而且<b>小於</b>
    /// <see cref="InteractRange"/>。
    /// <para>小於互動距離的理由：判定抵達之後緊接著就是「互動地壟」那一步的距離檢查。</para>
    /// </remarks>
    private const float WalkArriveDistance = 4.5f;

    /// <summary>交給 vnavmesh 的目的地容許值（碼）。</summary>
    /// <remarks>
    /// 📌 地壟／花盆本身是站不上去的障礙物，目的地就是它的位置 ⇒ <b>一定要給容許值</b>，
    /// 否則等於要求 vnavmesh 把角色走進物件裡面。
    /// </remarks>
    private const float WalkNavTolerance = 3f;

    /// <summary>單一段走位的絕對上限。</summary>
    /// <remarks>
    /// 🔴 必須明顯小於 <see cref="WalkStepTimeoutMs"/>：<see cref="TaskQueue"/> 的步驟逾時是
    /// <b>中止整條佇列</b>並跳一行紅字，那會把後面所有地壟一起取消。這條軟上限讓走不到的那一格
    /// 安靜地放棄、往下走。
    /// </remarks>
    private static readonly TimeSpan WalkHopTimeout = TimeSpan.FromSeconds(25);

    /// <summary>走位步驟交給 <see cref="TaskQueue"/> 的硬逾時（毫秒）。只是保險絲。</summary>
    private const int WalkStepTimeoutMs = 40_000;

    /// <summary>多久沒有靠近就當作卡住了。</summary>
    private static readonly TimeSpan WalkNoProgressWindow = TimeSpan.FromSeconds(8);

    /// <summary>距離要縮短多少才算「有前進」（碼）。</summary>
    /// <remarks>⚠️ 不能用 0：角色站著不動時每幀的浮點抖動也會讓距離微幅變小，卡住偵測會永遠不觸發。</remarks>
    private const float WalkProgressEpsilon = 0.5f;

    /// <summary>問「這裡有沒有導航網格」時的搜尋半徑（碼）。與 vnavmesh 端的預設值相同。</summary>
    private const float WalkMeshProbeHalfExtentXZ = 5f;

    /// <summary>同上，垂直方向。</summary>
    /// <remarks>
    /// ⚠️ vnavmesh 端的預設也是 5。這裡明寫出來，是因為「只有 X／Z、Y 隨便給」的呼叫
    /// 一定查不到而且零訊息——本模組的座標來自物件表，三個軸都是真的，所以 5 夠用。
    /// </remarks>
    private const float WalkMeshProbeHalfExtentY = 5f;

    /// <summary>這一輪已經放棄走位了（一輪只放棄一次，之後每一格直接跳過走位步驟）。</summary>
    private bool walkGaveUpThisRound;

    /// <summary>本模組這次啟用期間有沒有真的發起過移動（決定停用時要不要去收）。</summary>
    private bool walkStartedNav;

    /// <summary>最近一次放棄走位的理由，給設定畫面看（空字串＝沒放棄過）。</summary>
    private string lastWalkGiveUpReason = string.Empty;

    /// <summary>這一輪走位真的把角色帶到了幾格旁邊（給結果彙總用）。</summary>
    private int walkedCount;

    /// <summary>使用者有沒有把走位打開。</summary>
    private bool WalkEnabled => Config.WalkBetweenPatches;

    /// <summary>單一段走位的狀態。</summary>
    /// <remarks>
    /// 📌 做成具名類別而不是幾個捕獲進閉包的區域變數，理由是<b>可驗證性</b>：
    /// 編譯後的區域變數只留在 PDB 裡，對 DLL 做字串探針一律回 0，
    /// 分不出「沒編進去」與「編進去了但名字看不到」。具名型別與欄位名在 <c>#Strings</c> 堆裡。
    /// </remarks>
    private sealed class WalkHop
    {
        /// <summary>已經對 vnavmesh 下過這一段的導航請求（<b>一段只下一次</b>，沒有重試）。</summary>
        public bool NavIssued;

        /// <summary>這一段最晚什麼時候要放棄（UTC）。</summary>
        public DateTime Deadline;

        /// <summary>上一次「有靠近」是什麼時候（UTC）。</summary>
        public DateTime LastProgressAt;

        /// <summary>到目前為止最接近過的距離（碼），卡住偵測的基準。</summary>
        public float ClosestSoFar = float.MaxValue;
    }

    /// <summary>每一輪開始時重置走位狀態。</summary>
    /// <remarks>
    /// 📌 <see cref="walkStartedNav"/> <b>刻意不重置</b>：它問的是「這次啟用期間有沒有動過角色」，
    /// 用來決定停用／停止批次時要不要對 vnavmesh 送停止。跨輪次歸零的話，
    /// 上一輪留下來還沒走完的路徑就沒有人負責收。
    /// </remarks>
    private void ResetWalkRound()
    {
        walkGaveUpThisRound = false;
        walkedCount = 0;
    }

    /// <summary>本模組發起的移動若還在跑，把它收掉。</summary>
    /// <remarks>📌 沒發起過就不呼叫：不要在拆卸路徑上對別的外掛做沒必要的 IPC（做法同 ClickToMove／FlagCommands）。</remarks>
    private void StopWalkIfMoving()
    {
        if (!walkStartedNav) return;
        NavStop.RequestStop();
    }

    /// <summary>
    /// 在這一格的互動之前，先把角色帶到它旁邊。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>只有在使用者打開走位時才會被排進佇列</b>（見 <c>EnqueuePatch</c>）——
    /// 關著的時候佇列裡連這個步驟都不存在，行為與加這個功能之前完全一樣。
    /// </remarks>
    private void EnqueueWalkToPatch(PatchJob job)
    {
        // 每一格各自一份狀態（步驟之間不共用）。
        var hop = new WalkHop();

        queue.Enqueue("走到地壟旁", () =>
        {
            if (job.Skipped) return true;

            // 這一輪已經放棄走位了：直接放行，讓「互動地壟」那一步照原本的距離規則處理。
            if (walkGaveUpThisRound) return true;

            var localPlayer = Svc.Objects.LocalPlayer;

            // 🔴 只存 GameObjectId、每一幀重查。本 pin 的 ObjectTable 包裝是每格預配、
            //    存取時就地改寫 Address 的，跨幀持有會靜默換成別的物件或懸空。
            var obj = Svc.Objects.SearchById(job.GameObjectId);

            // 查不到（讀取區域、物件離開視野）＝這一格交給既有的距離檢查去判跳過，不是走位的錯。
            if (localPlayer == null || obj == null) return true;

            var distance = Vector3.Distance(localPlayer.Position, obj.Position);
            var now = DateTime.UtcNow;

            if (distance <= WalkArriveDistance)
            {
                if (hop.NavIssued)
                {
                    // 到了就把路徑收掉——vnavmesh 自己的容許值比較小，不收的話它會繼續往前擠。
                    if (ExternalNav.IsVnavmeshPathRunning()) ExternalNav.TryStopMovement();
                    walkedCount++;
                    LogPerPatch($"[{InternalName}] 已走到地壟 {job.GameObjectId:X} 旁（{distance:F1} 碼）。");
                }

                return true;
            }

            if (!hop.NavIssued)
                return StartWalk(job, hop, obj.Position, distance, now);

            // ── 監看：只判定，不再下任何新的導航請求 ──────────────────────────

            // 🔴 還在<b>算</b>路徑的期間不能算進卡住偵測——角色本來就還沒開始走。
            //    住宅區是完整的一張外景圖，尋路花掉的時間可能超過卡住視窗，
            //    少了這一行的失敗形式是「還沒起步就被自己判定成卡住」，而且每一格都一樣，
            //    看起來會像「這裡不能走位」。
            if (ExternalNav.IsVnavmeshPathfindInProgress())
                hop.LastProgressAt = now;

            if (distance < hop.ClosestSoFar - WalkProgressEpsilon)
            {
                hop.ClosestSoFar = distance;
                hop.LastProgressAt = now;
            }

            if (now >= hop.Deadline)
            {
                StopWalkIfMoving();
                GiveUpWalking($"走了 {WalkHopTimeout.TotalSeconds:F0} 秒還沒到（仍差 {distance:F1} 碼）");
                return true;
            }

            if (now - hop.LastProgressAt >= WalkNoProgressWindow)
            {
                StopWalkIfMoving();
                GiveUpWalking(
                    $"卡住了：{WalkNoProgressWindow.TotalSeconds:F0} 秒沒有再靠近（仍差 {distance:F1} 碼）"
                    + "；庭園裡的家具與柵欄不在導航網格上，路徑可能穿過它們");
                return true;
            }

            // vnavmesh 既沒在走也沒在算了，而我們還沒到。
            // 📌 這是最常見的「走不到」形狀：路徑算不出來、被使用者的輸入取消、或它自己判定卡住。
            if (!ExternalNav.IsVnavmeshPathRunning() && !ExternalNav.IsVnavmeshPathfindInProgress())
            {
                // 🔑 已經在互動距離內就別計較：vnavmesh 的容許值與我們的抵達門檻不同，
                //    落在兩者之間時「能互動」才是真正重要的事實。
                if (distance <= InteractRange - 0.5f)
                {
                    walkedCount++;
                    LogPerPatch($"[{InternalName}] 走到地壟 {job.GameObjectId:X} 的互動距離內（{distance:F1} 碼）。");
                    return true;
                }

                GiveUpWalking($"vnavmesh 已經停止移動但還沒走到（仍差 {distance:F1} 碼）");
                return true;
            }

            return false;
        }, WalkStepTimeoutMs);
    }

    /// <summary>發起這一段走位；回傳值就是步驟的回傳值。</summary>
    /// <remarks>
    /// 🔴 <b>三道前提全部是「失敗就放棄」，沒有任何一條會重試。</b>
    /// 順序是刻意的：先問我們自己能不能走（vnavmesh 在不在、網格好了沒），
    /// 再問該不該走（別人是不是正在控制角色），最後才問走不走得到（終點在不在網格上）。
    /// </remarks>
    private bool StartWalk(PatchJob job, WalkHop hop, Vector3 destination, float distance, DateTime now)
    {
        if (!ExternalNav.IsVnavmeshReady())
        {
            GiveUpWalking(ExternalNav.IsVnavmeshInstalled()
                ? "vnavmesh 的導航網格尚未就緒"
                : "未偵測到 vnavmesh（走位需要它）");
            return true;
        }

        // 🔴 尊重別人正在控制移動。判準用的是<b>實際的導航狀態查詢</b>
        //    （vnavmesh 的 Path.IsRunning／SimpleMove.PathfindInProgress、Lifestream.IsBusy、
        //    AutoDuty.IsNavigating、BossMod.AI.IsNavigating），
        //    ⚠️ <b>不是</b> NavStop 的引用計數——那個數的是「有幾個模組登記過 /tcstop」，
        //    與「現在有沒有人在動角色」完全無關，拿它判會永遠是「有人在動」。
        if (ExternalNav.TryGetActiveMover(out var mover))
        {
            GiveUpWalking($"{mover} 正在移動角色，這一輪不搶");
            return true;
        }

        // 🔑 終點在不在導航網格上——這是「庭園／室內到底能不能導航」唯一便宜可靠的判法。
        //    vnavmesh 對「終點不在網格上」的處置是算不出路徑，而呼叫端拿到的仍然是
        //    started == true，然後角色站著不動、零訊息。先問一次就能把那個靜默失敗變成一行說明。
        if (!ExternalNav.TryFindNearestMeshPoint(
                destination, WalkMeshProbeHalfExtentXZ, WalkMeshProbeHalfExtentY, out _))
        {
            GiveUpWalking(
                $"地壟 {job.GameObjectId:X} 附近沒有導航網格"
                + "（房屋內部或這座庭園可能建不出網格，走位在這裡不能用）");
            return true;
        }

        if (!ExternalNav.TryMoveCloseTo(destination, false, WalkNavTolerance, out var started, DisplayName))
        {
            GiveUpWalking("vnavmesh 沒有接下這次導航（它可能剛被停用，或導航網格還沒載入好）");
            return true;
        }

        if (!started)
        {
            // ⚠️ 實際上走不到這裡：MoveTo 恆回 true（見 ExternalNav.TryMoveCloseTo 的說明）。
            //    防護保留，但不指名一個查不到的原因。走不走得到由下面的監看去判。
            GiveUpWalking("vnavmesh 沒有開始這次導航");
            return true;
        }

        hop.NavIssued = true;
        walkStartedNav = true;
        hop.Deadline = now + WalkHopTimeout;
        hop.LastProgressAt = now;
        hop.ClosestSoFar = distance;

        LogPerPatch($"[{InternalName}] 走向地壟 {job.GameObjectId:X}（目前 {distance:F1} 碼）。");
        return false;
    }

    /// <summary>無人值守重跑時，同一個「放棄走位」最多多久寫一次記錄（毫秒）。</summary>
    /// <remarks>
    /// 🔴 這個節流<b>不影響是否放棄</b>，只影響寫不寫那一行。
    /// 走不到的原因（這裡沒有導航網格、被家具擋住）在一輪之內不會好轉，
    /// 而無人值守重跑預設 60 秒一輪 —— 不節流的話它每小時寫 60 行一模一樣的話，
    /// 把事後真正要查的東西淹掉。
    /// <para>📌 首次一定放行（<see cref="Throttle"/> 的語意），所以「第一次為什麼不走」永遠留得下來。</para>
    /// </remarks>
    private const int WalkGiveUpLogIntervalMs = 300_000;

    /// <summary>放棄走位：這一輪剩下的地壟全部退回「只處理站得到的」。</summary>
    /// <remarks>
    /// 🔴 <b>Information 級。</b>逐格的記錄在無人值守重跑時會降級成 Debug（量太大），
    /// 但「走位為什麼不動」正是使用者唯一會來問的事——它不跟著降級。
    /// </remarks>
    private void GiveUpWalking(string reason)
    {
        walkGaveUpThisRound = true;
        lastWalkGiveUpReason = reason;

        if (!loopRoundQuiet || Throttle.Pass($"{InternalName}-WalkGiveUpLog", WalkGiveUpLogIntervalMs))
        {
            Svc.Log.Information(
                $"[{InternalName}] 自動走位放棄：{reason}。這一輪剩下的地壟改回只處理站得到的那幾格。");
        }

        // 手動按的那條路徑照舊會說話；無人值守重跑不進聊天視窗（每個週期喊一次比沒有功能還糟）。
        if (!loopRoundQuiet)
            Svc.Chat.Print($"[TC Toolbox] 園圃走位已放棄：{reason}。");
    }
}

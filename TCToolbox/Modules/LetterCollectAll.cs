using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Memory;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 信箱（莫古力信件）一鍵收取所有附件與 Gil。
/// </summary>
/// <remarks>
/// 🔴 <b>只收取，永遠不刪信。</b>遊戲的信件代理人（<c>AgentLetterList</c>）用事件編號 4 做刪除，
/// 本模組<b>從頭到尾不會送出那個編號</b>——收完的信件由遊戲自己按它原本的規則處理。
/// <para>
/// 📌 <b>手動觸發</b>：模組開著也完全不會自己動，一定要在信箱開著時按下按鈕才會跑一輪。
/// </para>
/// <para>
/// 🔑 <b>「領取全部」按鈕靠文字比對認出來，不是靠節點編號認出來。</b>
/// 離線傾印台服自己的 <c>ui/uld/LetterViewer.uld</c> 確認底排三顆按鈕是節點 30／31／32
/// （由左到右，各 130x28），而<b>最右邊那顆是刪除</b>——認錯一格的代價是把信連同附件刪掉。
/// 所以按下去之前一定要先讀那顆按鈕上的字，跟遊戲自己的 <c>Addon</c> 第 430 列
/// （台服＝「領取全部」）比對，對不上就整輪拒絕執行並把三顆按鈕的實際文字寫進記錄。
/// </para>
/// <para>
/// 🔴 <b>不保存任何原生指標。</b>每一步都重新取 addon 與 <c>InfoProxyLetter</c>，
/// 信件也<b>每一輪重讀一次、只處理當下的第一封</b>——不是先抓一份索引清單再照著跑。
/// 收完一封之後遊戲會把清單壓縮，事先抓的索引<b>從第二封開始就全部指到別封信</b>，
/// 而那個錯誤不會有任何徵兆。
/// </para>
/// </remarks>
public sealed unsafe class LetterCollectAll : TcModule
{
    public override string InternalName => "LetterCollectAll";
    public override string DisplayName => "信箱一鍵收取";

    public override string Description =>
        "信箱（莫古力信件）開著時，按一下就依序把所有信件的附件與 Gil 收下來。" +
        "只收取，永遠不刪信；每收一封都重新讀一次清單，收不動就停下來。";

    public override ModuleCategory Category => ModuleCategory.Inventory;

    public override bool IsManualTrigger => true;

    public override bool HasConfigUI => true;

    private const string LetterListAddon = "LetterList";

    private const string LetterViewerAddon = "LetterViewer";

    /// <summary>
    /// 「領取全部」按鈕在 <c>LetterViewer</c> 裡的節點編號。
    /// </summary>
    /// <remarks>
    /// 離線傾印台服 <c>ui/uld/LetterViewer.uld</c>：節點 30／31／32 是底排三顆同型按鈕
    /// （Component#1009，各 130x28，x＝90／223／356）。
    /// ⚠️ 節點編號本身<b>不是</b>判準——真正的判準是 <see cref="TakeAllLabelRow"/> 的文字比對，
    /// 這個常數只是「先找哪一顆來比對」。
    /// </remarks>
    private const uint TakeAllButtonNodeId = 30;

    /// <summary>另外兩顆按鈕，只在比對失敗時一起寫進記錄，幫忙看出是不是節點編號整組漂了。</summary>
    private static readonly uint[] SiblingButtonNodeIds = [31, 32];

    /// <summary>「領取全部」在 <c>Addon</c> 表的列號（台服 7.20 離線比對＝「領取全部」）。</summary>
    private const uint TakeAllLabelRow = 430;

    private readonly TaskQueue queue = new() { DefaultTimeoutMs = 15_000 };

    private LetterCollectAllConfig Config => Plugin.Instance.Config.LetterCollectAll;

    /// <summary>解析好的按鈕文字判準（空字串＝查不到，那就整個功能不給跑）。</summary>
    private string takeAllLabel = string.Empty;

    private int collectedLetters;

    private int roundsLeft;

    private string lastResult = string.Empty;

    protected override void OnEnable()
    {
        takeAllLabel = Svc.Data.GetExcelSheet<Addon>()
                          .GetRowOrDefault(TakeAllLabelRow)?.Text.ExtractText().Trim() ?? string.Empty;

        Svc.Log.Information(
            $"[{InternalName}] 「領取全部」按鈕文字判準（Addon #{TakeAllLabelRow}）＝" +
            (takeAllLabel.Length > 0 ? $"「{takeAllLabel}」" : "（查不到，收取功能將拒絕執行）"));

        Svc.Framework.Update += OnUpdate;

        // ⚠️ 常駐註冊、由處理常式自己判前置條件（與 AutoRequestItemSubmit 同一套做法）：
        //    動態註冊／解除比較容易留下懸空的監聽器。
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "SelectYesno", OnSelectYesno);
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostDraw, "SelectYesno", OnSelectYesno);

        queue.OnTimeout = step =>
        {
            lastResult = $"逾時中止於「{step}」";
            Svc.Log.Information($"[{InternalName}] 步驟逾時，整輪中止：{step}");
        };
    }

    protected override void OnDisable()
    {
        Svc.Framework.Update -= OnUpdate;
        Svc.AddonLifecycle.UnregisterListener(OnSelectYesno);

        queue.Abort();
        roundsLeft = 0;
    }

    private void OnUpdate(IFramework framework) => queue.Tick();

    /// <summary>
    /// 收取途中的確認框。
    /// </summary>
    /// <remarks>
    /// 🔴 三個條件全部成立才按「是」：本模組的佇列正在跑、信箱開著、信件內容視窗也開著。
    /// 少任何一條就完全不動作——這樣「按下是」的時間窗只有使用者自己按下按鈕之後的那幾秒，
    /// 而且必須是在信件視窗開著的狀態下。
    /// <para>
    /// 📌 <b>不論按不按，提示文字一律寫進記錄</b>（Information 級、已節流）。
    /// 這個功能沒有辦法離線得知台服到底會不會跳確認框、跳的是哪一句，
    /// 記錄下來的話下次就知道，不必請使用者去試。
    /// </para>
    /// </remarks>
    private void OnSelectYesno(AddonEvent type, AddonArgs args)
    {
        if (!queue.IsBusy) return;
        if (!UiHelper.IsAddonReady(LetterListAddon)) return;
        if (!UiHelper.IsAddonReady(LetterViewerAddon)) return;

        if (!Throttle.Pass("LetterCollectAll-Yesno", Math.Max(200, Config.StepIntervalMs))) return;

        var addon = (AddonSelectYesno*)args.Addon.Address;
        if (!UiHelper.IsReady((AtkUnitBase*)addon)) return;

        var prompt = addon->PromptText == null
            ? string.Empty
            : MemoryHelper.ReadSeString(&addon->PromptText->NodeText).TextValue.Trim();

        if (!Config.AutoConfirm)
        {
            Svc.Log.Information($"[{InternalName}] 收取途中出現確認框，但自動確認已關閉，不動作：「{prompt}」");
            return;
        }

        // 🔴 讀出替換字元＝視窗的記憶體正在變動（多半是正在關閉），這一幀什麼都不要碰，下一幀重讀。
        if (AddonPrompt.LooksMidUpdate(prompt)) return;

        // 🔴 最後一道：按過的那個實例在觀察到它收掉之前不再按。這裡掛了 PostDraw ＝每幀都會回來，
        //    而「按下之後正在關閉」的那幾幀 IsReady 三關照樣全過，再送 callback 就是攔不到的
        //    存取違規（2026-08-31 實機崩潰 crash-20260831205734 的形狀）。
        //    守衛已下沉到 UiHelper.TryFireCallback 裡：回 false ＝這一幀沒送。
        if (!UiHelper.TryFireCallback((AtkUnitBase*)addon, true, 0)) return;

        Svc.Log.Information($"[{InternalName}] 已確認收取途中的確認框：「{prompt}」");
    }

    #region 讀取信件

    /// <summary>一封有東西可拿的信。</summary>
    private readonly record struct PendingLetter(int Index, string Sender, int ItemCount, uint Gil);

    /// <summary>
    /// 目前有附件或 Gil 可領的信件。
    /// </summary>
    /// <remarks>
    /// 🔴 每次呼叫都重新取 <c>InfoProxyLetter</c>，不留指標。
    /// 回傳的是<b>當下這一幀</b>的快照，只能拿來做一次決定。
    /// </remarks>
    private static List<PendingLetter> ReadPendingLetters()
    {
        var result = new List<PendingLetter>();

        var proxy = InfoProxyLetter.Instance();
        if (proxy == null) return result;

        var letters = proxy->Letters;
        for (var i = 0; i < letters.Length; i++)
        {
            var letter = letters[i];

            // Timestamp 0＝這一格是空的（不是「1970 年的信」）。
            if (letter.Timestamp == 0) continue;

            var itemCount = 0;
            var attachments = letter.Attachments;
            for (var a = 0; a < attachments.Length; a++)
            {
                if (attachments[a].Count != 0) itemCount++;
            }

            if (itemCount == 0 && letter.Gil == 0) continue;

            result.Add(new PendingLetter(i, letter.SenderString, itemCount, letter.Gil));
        }

        return result;
    }

    /// <summary>還沒領的東西總量，用來判斷「這一輪到底有沒有進展」。</summary>
    private static (int Letters, int Items, long Gil) Summarize()
    {
        var letters = ReadPendingLetters();
        var items = 0;
        long gil = 0;
        foreach (var letter in letters)
        {
            items += letter.ItemCount;
            gil += letter.Gil;
        }

        return (letters.Count, items, gil);
    }

    #endregion

    #region 收取流程

    private void StartCollecting()
    {
        if (queue.IsBusy) return;

        if (takeAllLabel.Length == 0)
        {
            lastResult = "查不到「領取全部」的按鈕文字，拒絕執行";
            Svc.Log.Information(
                $"[{InternalName}] 拒絕執行：Addon 第 {TakeAllLabelRow} 列在這個客戶端是空的，" +
                "沒有辦法確認要按的是哪一顆按鈕。");
            return;
        }

        if (!UiHelper.IsAddonReady(LetterListAddon))
        {
            lastResult = "信箱沒有開著";
            return;
        }

        var start = Summarize();
        if (start.Letters == 0)
        {
            lastResult = "沒有可領取的信件";
            return;
        }

        collectedLetters = 0;

        // 上限＝信件數再加幾輪的餘裕。有「沒有進展就停」當主要防線，這條只是最後的保險絲，
        // 免得任何預料外的狀況把它變成無限迴圈。
        roundsLeft = start.Letters + 5;
        lastResult = string.Empty;

        Svc.Log.Information(
            $"[{InternalName}] 開始收取：{start.Letters} 封信、{start.Items} 件道具、{start.Gil} Gil。");

        EnqueueRound();
    }

    /// <summary>
    /// 排一輪「收一封信」。
    /// </summary>
    /// <remarks>
    /// 🔑 一輪只處理<b>當下重讀後的第一封</b>，收完再排下一輪。
    /// 這樣就完全不需要處理「收完之後清單壓縮、索引整組往前移」——那是照著事先抓好的索引跑
    /// 一定會踩到的坑，而且踩到的表現是「收了別封信」而不是報錯。
    /// </remarks>
    private void EnqueueRound()
    {
        queue.Enqueue("檢查信箱", () =>
        {
            if (roundsLeft-- <= 0)
            {
                lastResult = $"到達輪數上限，已收取 {collectedLetters} 封";
                Svc.Log.Information($"[{InternalName}] 到達輪數上限，停止。已收取 {collectedLetters} 封。");
                return null;
            }

            if (!UiHelper.IsAddonReady(LetterListAddon))
            {
                lastResult = $"信箱已關閉，已收取 {collectedLetters} 封";
                Svc.Log.Information($"[{InternalName}] 信箱已關閉，停止。已收取 {collectedLetters} 封。");
                return null;
            }

            var pending = ReadPendingLetters();
            if (pending.Count == 0)
            {
                lastResult = $"完成，共收取 {collectedLetters} 封";
                Svc.Log.Information($"[{InternalName}] 全部收取完畢，共 {collectedLetters} 封。");

                if (Config.NotifyOnFinish)
                    Svc.Chat.Print($"[TC Toolbox] 信箱收取完畢，共 {collectedLetters} 封。");

                return null;
            }

            var target = pending[0];
            var before = Summarize();

            EnqueueLetter(target, before);
            return true;
        });
    }

    private void EnqueueLetter(PendingLetter target, (int Letters, int Items, long Gil) before)
    {
        // ① 選取這一封（事件編號 0）。
        //    ⚠️ 索引來自我們剛剛才讀的 InfoProxyLetter，值域與遊戲自己會傳的完全一樣。
        queue.Enqueue($"選取第 {target.Index + 1} 封", () =>
        {
            UiHelper.SendAgentEvent(AgentId.LetterList, 0, 0, target.Index, 0, 1);
        });

        queue.EnqueueDelay(Config.StepIntervalMs);

        // ② 開啟信件內容（事件編號 1）。
        //    🔴 這裡永遠不會送事件編號 4 —— 那是刪除。
        queue.Enqueue("開啟信件", () =>
        {
            UiHelper.SendAgentEvent(AgentId.LetterList, 1, 0, 0, 0u, 0, 0);
        });

        queue.EnqueueWait("等待信件視窗", () => UiHelper.IsAddonReady(LetterViewerAddon), 8_000);

        queue.EnqueueDelay(Config.StepIntervalMs);

        // ③ 認出並按下「領取全部」。認不出來就整輪中止。
        queue.Enqueue("領取全部", () =>
        {
            var addon = UiHelper.GetAddon(LetterViewerAddon);
            if (!UiHelper.IsReady(addon)) return false;

            var button = addon->GetComponentButtonById(TakeAllButtonNodeId);
            if (button == null)
            {
                lastResult = "找不到領取按鈕，已中止";
                Svc.Log.Information(
                    $"[{InternalName}] 中止：信件視窗裡沒有節點 {TakeAllButtonNodeId} 這顆按鈕。");
                return null;
            }

            var label = ReadButtonLabel(button);
            if (!string.Equals(label, takeAllLabel, StringComparison.Ordinal))
            {
                lastResult = "按鈕文字對不上，已中止";
                Svc.Log.Information(
                    $"[{InternalName}] 中止：節點 {TakeAllButtonNodeId} 的文字是「{label}」，" +
                    $"不是預期的「{takeAllLabel}」。同排其他按鈕：{DescribeSiblingButtons(addon)}。" +
                    "在確認得出哪一顆是領取之前不會按任何一顆——最右邊那顆是刪除。");
                return null;
            }

            // 按不下去（尚未啟用）就下一 tick 重試，交給步驟逾時收尾。
            return UiHelper.ClickButton(addon, button) ? true : false;
        });

        // ④ 等這一封真的收乾淨：不看任何寫死的陣列索引，只看遊戲自己的信件資料。
        queue.EnqueueWait("等待收取完成", () =>
        {
            var now = Summarize();
            return now.Items < before.Items || now.Gil < before.Gil || now.Letters < before.Letters;
        }, 10_000);

        queue.EnqueueDelay(Config.StepIntervalMs);

        // ⑤ 關掉信件視窗，回到清單。
        queue.Enqueue("關閉信件視窗", () =>
        {
            var addon = UiHelper.GetAddon(LetterViewerAddon);
            if (UiHelper.IsReady(addon))
            {
                addon->Close(true);
                UiHelper.SendAgentEvent(AgentId.LetterView, 0, -1);
            }

            collectedLetters++;
        });

        queue.EnqueueDelay(Config.StepIntervalMs);

        // ⑥ 有進展才排下一輪。
        queue.Enqueue("確認有進展", () =>
        {
            var after = Summarize();
            if (after.Items >= before.Items && after.Gil >= before.Gil && after.Letters >= before.Letters)
            {
                lastResult = $"收不動了（背包滿？），已收取 {collectedLetters} 封";
                Svc.Log.Information(
                    $"[{InternalName}] 這一輪沒有任何進展（道具 {before.Items}→{after.Items}、" +
                    $"Gil {before.Gil}→{after.Gil}、信件 {before.Letters}→{after.Letters}），停止。" +
                    "最常見的原因是背包已滿。");
                return null;
            }

            EnqueueRound();
            return true;
        });
    }

    private static string ReadButtonLabel(AtkComponentButton* button)
    {
        if (button == null) return string.Empty;

        var textNode = button->ButtonTextNode;
        if (textNode == null) return string.Empty;

        return MemoryHelper.ReadSeString(&textNode->NodeText).TextValue.Trim();
    }

    private static string DescribeSiblingButtons(AtkUnitBase* addon)
    {
        var parts = new List<string>(SiblingButtonNodeIds.Length);
        foreach (var nodeId in SiblingButtonNodeIds)
        {
            var button = addon->GetComponentButtonById(nodeId);
            parts.Add($"#{nodeId}＝「{(button == null ? "（無）" : ReadButtonLabel(button))}」");
        }

        return string.Join("、", parts);
    }

    #endregion

    public override void DrawConfig()
    {
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
        ImGui.TextDisabled(
            "先在遊戲裡打開信箱，再按下面的按鈕。只收取附件與 Gil，永遠不會刪信。");
        ImGui.PopTextWrapPos();

        ImGui.Spacing();

        var listOpen = UiHelper.IsAddonReady(LetterListAddon);
        List<PendingLetter> pending;
        try
        {
            pending = listOpen ? ReadPendingLetters() : [];
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, $"[{InternalName}] 讀取信件清單失敗");
            pending = [];
        }

        if (!listOpen)
        {
            ImGui.TextDisabled("信箱目前沒有開著。");
        }
        else if (pending.Count == 0)
        {
            ImGui.TextDisabled("信箱開著，但沒有可領取的附件或 Gil。");
        }
        else
        {
            var items = 0;
            long gil = 0;
            foreach (var letter in pending)
            {
                items += letter.ItemCount;
                gil += letter.Gil;
            }

            ImGui.TextUnformatted($"可領取 {pending.Count} 封：道具 {items} 件、{gil} Gil");
            if (ImGui.IsItemHovered())
            {
                var lines = new List<string>(pending.Count);
                foreach (var letter in pending)
                {
                    var sender = string.IsNullOrWhiteSpace(letter.Sender) ? "?" : letter.Sender;
                    lines.Add($"{sender}：道具 {letter.ItemCount} 件、{letter.Gil} Gil");
                }

                ImGui.SetTooltip(string.Join("\n", lines));
            }
        }

        ImGui.Spacing();

        var busy = queue.IsBusy;
        using (ImRaii.Disabled(busy || !listOpen || pending.Count == 0 || takeAllLabel.Length == 0))
        {
            if (ImGui.Button("收取全部附件"))
                StartCollecting();
        }

        ImGui.SameLine();
        using (ImRaii.Disabled(!busy))
        {
            if (ImGui.Button("停止"))
            {
                queue.Abort();
                lastResult = $"已手動停止，已收取 {collectedLetters} 封";
                Svc.Log.Information($"[{InternalName}] 使用者手動停止，已收取 {collectedLetters} 封。");
            }
        }

        if (busy)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"執行中：{queue.CurrentStep}");
        }
        else if (lastResult.Length > 0)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(lastResult);
        }

        if (takeAllLabel.Length == 0)
        {
            ImGui.TextColored(new Vector4(1f, 0.75f, 0.4f, 1f),
                              $"這個客戶端的 Addon 第 {TakeAllLabelRow} 列是空的，" +
                              "無法確認要按哪一顆按鈕，功能已鎖住。");
        }

        ImGui.Spacing();
        ImGui.Separator();

        ImGui.SetNextItemWidth(200f);
        var interval = Config.StepIntervalMs;
        if (ImGui.SliderInt("每步間隔（毫秒）", ref interval, 100, 2_000))
            Config.StepIntervalMs = Math.Clamp(interval, 0, 10_000);
        if (ImGui.IsItemDeactivatedAfterEdit())
            Plugin.Instance.Config.Save();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("每一步都是真的送到伺服器的操作。太快沒有好處，也讓你來不及按停止。");

        var autoConfirm = Config.AutoConfirm;
        if (ImGui.Checkbox("自動按掉收取途中的確認框", ref autoConfirm))
        {
            Config.AutoConfirm = autoConfirm;
            Plugin.Instance.Config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "只在「本模組正在跑」而且「信箱與信件視窗都開著」時才會按。\n" +
                "關掉的話，真的跳出確認框時這一輪會停在那裡等到逾時（確認框的內容一律會寫進記錄）。");

        var notify = Config.NotifyOnFinish;
        if (ImGui.Checkbox("結束時在聊天欄報告", ref notify))
        {
            Config.NotifyOnFinish = notify;
            Plugin.Instance.Config.Save();
        }
    }
}

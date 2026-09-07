using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Memory;
using FFXIVClientStructs.FFXIV.Client.UI;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 自動園圃作業的<b>決策層</b>：讀出每一格的狀態，依使用者的策略決定要做什麼。
/// </summary>
/// <remarks>
/// 🔴 <b>狀態讀不到記憶體裡。</b>本 pin 的 <c>FFXIVClientStructs</c> 沒有任何園圃作物欄位
/// （<c>HousingFurniture</c> 只有 Id／Stain／Position／Rotation／Index），
/// 全艦隊唯一專門追蹤作物的 Accountant 也是監看 UI 與聊天、自己存一份資料庫。
/// ⇒ <b>唯一的狀態來源是互動時遊戲顯示的那句 Talk</b>
/// （<c>custom/001/CmnDefHousingGardeningPlant_00151</c> 的第 0／7～10 列），
/// 次要來源是選單上出現了哪些選項。兩者都必須真的互動一次才拿得到。
/// <para>
/// 📌 這一層取代的是使用者原本寫在 SomethingNeedDoing 巨集裡的 Lua 決策
/// （那份腳本的分工註解寫著「TCToolbox 只做互動與選單操作，本腳本負責決策」）。
/// 逐格的<b>互動</b>仍然走既有的狀態機，一行都沒有改。
/// </para>
/// <para>
/// 🔴 <b>兩個來源必須互相同意才動手</b>：Talk 判出來要做的事，如果選單上根本沒有那個選項，
/// 一律略過。單靠其中一個的失敗形式是「按到別的選項」，而園圃的動作有一半是不可回復的。
/// </para>
/// </remarks>
public sealed unsafe partial class AutoGardensWork
{
    /// <summary>這一輪處理完之後要重種的地壟（只存 GameObjectId，不存指標）。</summary>
    private readonly List<ulong> replantQueue = [];

    /// <summary>這一輪每一格的決定，給聊天回報與 UI 用。</summary>
    private readonly List<string> autoDecisions = [];

    /// <summary>SelectYesno 沒出現時最多等多久就當作不需要確認。</summary>
    /// <remarks>
    /// 🔴 不能只靠步驟逾時兜底：<see cref="TaskQueue"/> 的逾時是<b>中止整條佇列</b>，
    /// 於是「這一格剛好不跳確認框」會把後面所有地壟一起取消。這條軟上限讓它安靜地往下走。
    /// </remarks>
    private static readonly TimeSpan DisposeConfirmWait = TimeSpan.FromSeconds(3);

    // ── 狀態擷取 ────────────────────────────────────────────────────────────

    /// <summary>
    /// 從 Talk 視窗把這一格的狀態與作物讀下來。<b>必須在點掉 Talk 之前呼叫。</b>
    /// </summary>
    /// <remarks>
    /// 📌 只採信第一次讀到的那一句：Talk 是可以翻頁的，後面幾頁與作物狀態無關。
    /// 讀不出已知狀態時<b>不</b>設 <c>StateCaptured</c>，下一 tick 會再試一次
    /// （那多半代表這一幀讀到的是別的對話頁，或字串正在被改寫）。
    /// </remarks>
    private void CaptureTalkState(PatchJob job)
    {
        if (job.StateCaptured) return;

        var addon = UiHelper.GetAddon(UiHelper.TalkAddonName);
        if (!UiHelper.IsReady(addon)) return;

        var node = ((AddonTalk*)addon)->AtkTextNode228;

        // 🔴 兩層判空：節點本身，以及它的字串緩衝區。AtkTextNode 建好但 NodeText 還沒填
        //    （或正在被換掉）時 StringPtr 是空的，直接讀就是攔不到的存取違規。
        if (node == null || !node->NodeText.StringPtr.HasValue) return;

        var seString = MemoryHelper.ReadSeString(&node->NodeText);
        var text = seString.TextValue;
        if (string.IsNullOrWhiteSpace(text)) return;

        var state = ClassifyTalk(text, out var cropName);
        if (state == PatchState.Unknown) return;

        job.State = state;
        job.StateCaptured = true;

        // 🔑 作物身分優先走 item 連結：那是一個 id，完全不碰文字。
        foreach (var payload in seString.Payloads)
        {
            if (payload is not ItemPayload item || item.ItemId == 0) continue;
            job.CropItemId = item.ItemId;
            break;
        }

        // 退路：那句話沒有帶連結時，用作物名去查 id。名字本身來自遊戲的 Item 表
        // （程式碼裡沒有寫死中文），而且查出來之後仍然是拿 id 去比對。
        if (job.CropItemId == 0 && cropName.Length > 0)
            job.CropItemId = GardenCropData.CropIdByName(cropName);

        Svc.Log.Information(
            $"[{InternalName}] 地壟 {job.GameObjectId:X} 狀態＝{state}"
            + $"，作物 id＝{job.CropItemId}"
            + (job.CropItemId == 0 && cropName.Length > 0 ? $"（名稱「{cropName}」查不到對應道具）" : string.Empty));
    }

    /// <summary>
    /// 把 Talk 那句話分類。<paramref name="cropName"/> 是句首那段作物名（空地壟時為空字串）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 表裡存的只有「句尾那段固定文字」，句首的作物名是一個執行期才展開的 item 巨集
    /// ⇒ 這裡一律比對<b>句尾</b>，作物名取它前面那一段。
    /// 四句話彼此沒有子字串關係（2026-09-08 直讀台服 sqpack 確認），所以判斷順序不影響結果；
    /// 仍然把「枯萎」放第一個，因為那是唯一不可回復的分支。
    /// </remarks>
    private PatchState ClassifyTalk(string text, out string cropName)
    {
        cropName = string.Empty;

        if (text.Contains(textPlotEmpty, StringComparison.Ordinal))
            return PatchState.Empty;

        if (TrySplit(text, textStatusDead, out cropName)) return PatchState.Withered;
        if (TrySplit(text, textStatusRipe, out cropName)) return PatchState.Ripe;
        if (TrySplit(text, textStatusDepressed, out cropName)) return PatchState.NeedsCare;
        if (TrySplit(text, textStatusVigorous, out cropName)) return PatchState.Growing;

        return PatchState.Unknown;
    }

    private static bool TrySplit(string text, string suffix, out string head)
    {
        head = string.Empty;
        if (string.IsNullOrEmpty(suffix)) return false;

        var index = text.IndexOf(suffix, StringComparison.Ordinal);
        if (index < 0) return false;

        head = text[..index].Trim();
        return true;
    }

    // ── 決策 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 依策略決定這一格要做什麼；回 <see langword="false"/>＝什麼都不做。
    /// </summary>
    /// <param name="job">這一格的工作狀態（狀態與作物已由 <see cref="CaptureTalkState"/> 填好）。</param>
    /// <param name="entries">選單上目前的選項（不含取消）。</param>
    private bool ResolveAutoAction(PatchJob job, List<string> entries)
    {
        var targetCrop = GardenCropData.CropOfSeed(Config.SeedItemId);

        var canPlant = Config.SeedItemId != 0 && Config.SoilItemId != 0
                       && FindInventoryItem(Config.SeedItemId) != null
                       && FindInventoryItem(Config.SoilItemId) != null;
        var canFertilize = Config.FertilizerItemId != 0 && FindInventoryItem(Config.FertilizerItemId) != null;

        var decision = GardenDecisionMaker.Decide(
            job.State, job.CropItemId, targetCrop,
            Config.TargetMaturePolicy, Config.OtherMaturePolicy,
            Config.FertilizeMode, Config.WitheredMode,
            Config.TendWhenNeeded, Config.PlantWhenEmpty,
            canPlant, canFertilize);

        job.Reason = decision.Reason;

        if (decision.Action is not { } action)
        {
            RecordDecision(job, null);
            return false;
        }

        var chosen = action switch
        {
            AutoGardenAction.Harvest => GardenAction.Harvest,
            AutoGardenAction.Tend => GardenAction.Tend,
            AutoGardenAction.Fertilize => GardenAction.Fertilize,
            AutoGardenAction.Plant => GardenAction.Plant,
            _ => GardenAction.Dispose,
        };

        // 🔴 兩個來源要互相同意：Talk 說可以做的事，選單上必須真的有那個選項。
        //    對不起來就不動——那代表我對這一格的判讀是錯的，而不是遊戲少給了一個選項。
        var optionText = ActionText(chosen);
        if (!entries.Any(x => x.Contains(optionText, StringComparison.Ordinal)))
        {
            job.Reason = $"{decision.Reason}；但選單上沒有「{optionText}」，判讀可能有誤，本次不動";
            RecordDecision(job, null);
            return false;
        }

        job.Chosen = chosen;
        job.Replant = decision.Replant;
        RecordDecision(job, optionText);
        return true;
    }

    private void RecordDecision(PatchJob job, string? optionText)
    {
        var line = optionText == null
            ? $"略過：{job.Reason}"
            : $"{optionText}：{job.Reason}" + (job.Replant ? "（稍後重種）" : string.Empty);

        autoDecisions.Add(line);
        Svc.Log.Information($"[{InternalName}] 地壟 {job.GameObjectId:X} → {line}");

        if (Config.AnnounceEachDecision)
            Svc.Chat.Print($"[TC Toolbox] 園圃：{line}");
    }

    // ── 「處理」（清除枯萎作物）的後續步驟 ──────────────────────────────────

    /// <summary>
    /// 「處理」選下去之後會跳一個確認框（表的第 11 列「確定要處理掉作物嗎？」），按是。
    /// </summary>
    /// <remarks>
    /// 🔴 這一步<b>永遠不會讓整批停下來</b>：確認框沒出現時等一小段時間就往下走，
    /// 而不是讓 <see cref="TaskQueue"/> 的步驟逾時把整條佇列中止（那會把後面所有地壟一起取消）。
    /// </remarks>
    private void EnqueueDisposeSteps(PatchJob job)
    {
        var confirmed = false;
        DateTime? waitUntil = null;

        queue.Enqueue("確認處理", () =>
        {
            if (job.Skipped || job.Chosen != GardenAction.Dispose) return true;

            waitUntil ??= DateTime.UtcNow + DisposeConfirmWait;

            if (UiHelper.IsAddonReady(UiHelper.SelectYesnoAddonName))
            {
                confirmed = true;
                if (Throttle.Pass("AutoGardensWork-DisposeYes", 300))
                    UiHelper.ClickSelectYesnoYes();
                return false;
            }

            // 已經按過而且框收起來了＝完成；一直沒出現就等到軟上限再放行。
            return confirmed || DateTime.UtcNow >= waitUntil.Value ? true : false;
        }, 10_000);
    }

    // ── 批次入口 ────────────────────────────────────────────────────────────

    /// <summary>
    /// 「自動整理」：對附近每一格讀狀態、依策略動作，最後把該重種的種回去。
    /// </summary>
    private void StartAutoBatch()
    {
        if (queue.IsBusy) return;

        if (!TryGetGardenPatches(out var patches, out var error))
        {
            Svc.Chat.PrintError($"[TC Toolbox] {error}");
            return;
        }

        doneCount = 0;
        skippedCount = 0;
        replantQueue.Clear();
        autoDecisions.Clear();

        var targetCrop = GardenCropData.CropOfSeed(Config.SeedItemId);
        Svc.Log.Information(
            $"[{InternalName}] 自動整理開始：{patches.Count} 格；目標種子 {Config.SeedItemId}"
            + $" → 目標作物 {targetCrop}；策略 目標成熟={Config.TargetMaturePolicy}"
            + $" 非目標成熟={Config.OtherMaturePolicy} 施肥={Config.FertilizeMode}"
            + $" 枯萎={Config.WitheredMode} 護理={Config.TendWhenNeeded} 空地播種={Config.PlantWhenEmpty}");

        foreach (var (patchId, _) in patches)
            EnqueuePatch(patchId, GardenAction.Auto, Config.FertilizerItemId, Config.SeedItemId, Config.SoilItemId);

        queue.Enqueue("排入重種", () =>
        {
            // 🔴 在步驟裡對同一個佇列 Enqueue 是 TaskQueue 明文支援的
            //    （它的 Tick 只在「原步驟仍在隊首」時才移除），新步驟接在尾端。
            //    這正是重種必須放在這裡而不是 StartAutoBatch 的理由：哪幾格要重種，
            //    要等前面每一格都真的做完才知道。
            foreach (var id in replantQueue)
                EnqueuePatch(id, GardenAction.Plant, 0, Config.SeedItemId, Config.SoilItemId);

            var replantCount = replantQueue.Count;
            queue.Enqueue("彙總結果", () =>
            {
                lastSummary = $"園圃「自動整理」完成：處理 {doneCount} 格、跳過 {skippedCount} 格"
                              + (replantCount > 0 ? $"，其中重種 {replantCount} 格" : string.Empty);
                Svc.Chat.Print($"[TC Toolbox] {lastSummary}");
                Svc.Log.Information($"[{InternalName}] {lastSummary}");
                return true;
            });

            return true;
        });
    }

    // ── 設定 UI ─────────────────────────────────────────────────────────────

    private static readonly string[] MatureLabels = ["跳過不動", "收穫（不重種）", "收穫並重種目標作物"];
    private static readonly string[] FertilizeLabels = ["不施肥", "只對目標作物施肥", "對所有生長中的作物施肥"];
    private static readonly string[] WitheredLabels = ["不動它", "處理掉", "處理掉並重種目標作物"];

    /// <summary>「自動整理」那一區的 UI。由 <c>DrawConfig</c> 呼叫。</summary>
    private void DrawAutoSection()
    {
        ImGui.TextUnformatted("自動整理（讀出每一格的狀態，再依下面的策略決定要做什麼）：");

        if (ImGui.Button("開始自動整理##garden"))
            StartAutoBatch();

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "對附近每一格園圃各互動一次，從遊戲自己顯示的那句話讀出狀態，\n" +
                "再依策略決定收穫／護理／施肥／處理／播種，最後把該重種的種回去。\n" +
                "策略預設是保守的：非目標作物跳過、不施肥、枯萎不動。");
        }

        using (ImRaii.PushIndent())
        {
            var targetCrop = GardenCropData.CropOfSeed(Config.SeedItemId);
            if (Config.SeedItemId == 0)
            {
                ImGui.TextDisabled("? 還沒選種子，所以沒有「目標作物」——成熟的作物一律走「非目標」那條策略。");
            }
            else if (targetCrop == 0)
            {
                ImGui.TextDisabled($"? 選的種子（{Config.SeedItemId}）推不出收穫作物，成熟判定會一律走「非目標」。");
            }
            else
            {
                var name = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Item>()
                    .GetRowOrDefault(targetCrop)?.Name.ExtractText() ?? string.Empty;
                ImGui.TextDisabled($"目標作物：{name}（#{targetCrop}，由上面選的種子推得）");
            }

            DrawEnumCombo("目標作物成熟時", MatureLabels, (int)Config.TargetMaturePolicy, v =>
            {
                Config.TargetMaturePolicy = (MatureCropPolicy)v;
                Plugin.Instance.Config.Save();
            });

            DrawEnumCombo("其他作物成熟時", MatureLabels, (int)Config.OtherMaturePolicy, v =>
            {
                Config.OtherMaturePolicy = (MatureCropPolicy)v;
                Plugin.Instance.Config.Save();
            }, "辨識不出是什麼作物的那幾格也走這一條——「不知道」與「確定不是目標」一律當成同一件事。");

            DrawEnumCombo("施肥", FertilizeLabels, (int)Config.FertilizeMode, v =>
            {
                Config.FertilizeMode = (FertilizePolicy)v;
                Plugin.Instance.Config.Save();
            });

            DrawEnumCombo("枯萎的作物", WitheredLabels, (int)Config.WitheredMode, v =>
            {
                Config.WitheredMode = (WitheredPolicy)v;
                Plugin.Instance.Config.Save();
            }, "「處理」是不可回復的，所以預設不動。");

            var tend = Config.TendWhenNeeded;
            if (ImGui.Checkbox("狀態不好時護理", ref tend))
            {
                Config.TendWhenNeeded = tend;
                Plugin.Instance.Config.Save();
            }

            var plantEmpty = Config.PlantWhenEmpty;
            if (ImGui.Checkbox("空地壟播種目標作物", ref plantEmpty))
            {
                Config.PlantWhenEmpty = plantEmpty;
                Plugin.Instance.Config.Save();
            }

            var announce = Config.AnnounceEachDecision;
            if (ImGui.Checkbox("在聊天視窗逐格說明決定", ref announce))
            {
                Config.AnnounceEachDecision = announce;
                Plugin.Instance.Config.Save();
            }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("一座 3×8 的庭院會刷 24 行；記錄一律會寫，不受這格影響。");
        }

        if (autoDecisions.Count > 0)
        {
            ImGui.Spacing();
            if (ImGui.TreeNodeEx($"上次自動整理的逐格決定（{autoDecisions.Count}）###gardenAutoLog"))
            {
                foreach (var line in autoDecisions)
                    ImGui.TextDisabled(line);
                ImGui.TreePop();
            }
        }
    }

    /// <summary>一個策略下拉。</summary>
    /// <remarks>
    /// 🔴 tooltip 必須在 <c>BeginCombo</c> 之後、還沒畫任何選項之前問 <c>IsItemHovered</c>：
    /// 展開之後每個 <c>Selectable</c> 都會把「目前這個項目」換掉，
    /// <c>EndCombo</c> 之後再問就不是這顆下拉了（tooltip 會掛錯或乾脆不出現）。
    /// </remarks>
    private static void DrawEnumCombo(
        string label, string[] labels, int current, Action<int> onSelect, string? tooltip = null)
    {
        if (current < 0 || current >= labels.Length) current = 0;

        ImGui.SetNextItemWidth(280f);
        var open = ImGui.BeginCombo($"{label}##gardenPolicy", labels[current]);

        if (tooltip != null && ImGui.IsItemHovered()) ImGui.SetTooltip(tooltip);
        if (!open) return;

        for (var i = 0; i < labels.Length; i++)
        {
            if (ImGui.Selectable(labels[i], i == current))
                onSelect(i);
        }

        ImGui.EndCombo();
    }
}

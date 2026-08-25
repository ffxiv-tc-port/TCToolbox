using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Memory;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using TCToolbox.Core;
using ValueType = FFXIVClientStructs.FFXIV.Component.GUI.ValueType;

namespace TCToolbox.Modules;

/// <summary>
/// 軍票商店快速交換：在「軍隊物資交換」視窗開著時疊一個小面板，輸入道具名與數量即可一鍵換取。
/// 純手動觸發、走遊戲自己的視窗事件，不 hook、不寫記憶體 patch、不送封包偽造。
/// 參考 DailyRoutines <c>FastGrandCompanyExchange</c> 重寫（API13、無 KamiToolKit／OmenTools 相依）。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 <b>花軍票是不可逆的，所以最後由人按。</b>本模組<b>不</b>做事件驅動的自動交換鏈：
/// 使用者要自己在面板上填好道具與數量、看到「花費 N 軍票」後按下「交換」，才會送出交換事件。
/// 而且<b>絕不代按遊戲跳出的任何是／否確認框</b>——那一步一律留給使用者自己確認
/// （與 <see cref="TradeAllCollectables"/>、<see cref="OptimizedFreeShop"/> 的原則一致，
/// 但 DR 原版是自動點掉 SelectYesno 的，這裡刻意不照抄）。
/// </para>
/// <para>
/// ⚠️⚠️ <b>導航用的 AtkValue 索引與 callback 序號（分頁 = callback 1、子分類 = callback 2、
/// 道具名列在 AtkValues[17..]）全部來自國際服版面，台服 7.20 無法離線證明。</b>
/// 安全設計讓「假設不成立」不會變成崩潰或誤花軍票：
/// <list type="bullet">
/// <item>讀道具名一律先驗 <see cref="ValueType"/> 是字串型別才解參考（<see cref="ReadAtkString"/>），
/// 索引錯了只會讀到空字串，不會把非指標當指標解 → 不會 AVE。</item>
/// <item>只有在清單裡找到<b>名稱與解析結果一字不差</b>的那一格才送交換事件；找不到就中止並提示，
/// <b>一顆軍票都不會花</b>（fail-closed）。</item>
/// </list>
/// </para>
/// <para>
/// 🔑 道具的解析走 Lumina 表（<see cref="GCScripShopItem"/> 子列 ＋ <see cref="GCScripShopCategory"/>），
/// 跟著玩家目前的軍隊與階級篩選，與語言無關。
/// </para>
/// </remarks>
public sealed unsafe class FastGrandCompanyExchange : TcModule
{
    public override string InternalName => "FastGrandCompanyExchange";

    public override string DisplayName => "軍票商店快速交換";

    public override string Description =>
        "「軍隊物資交換」視窗開著時，疊一個小面板：輸入道具名與數量，按「交換」即可換取，" +
        "省去逐頁翻找。花軍票的最後確認一律由你自己按，不會自動點掉。開著不填不按就不會動。";

    public override ModuleCategory Category => ModuleCategory.Company;

    /// <inheritdoc/>
    /// <remarks>要自己填欄位、按按鈕才會動＝手動觸發。</remarks>
    public override bool IsManualTrigger => true;

    public override bool HasConfigUI => true;

    private const string AddonName = "GrandCompanyExchange";

    /// <summary>道具名列在 AtkValues 的起始索引與掃描筆數（國際服版面，台服未驗）。</summary>
    private const int ItemNameStartIndex = 17;
    private const int ItemNameScanCount = 40;

    /// <summary>目前分頁（軍階）所在的 AtkValue 索引。</summary>
    private const int TierValueIndex = 2;

    private readonly TaskQueue queue = new() { DefaultTimeoutMs = 10_000 };

    private FastGrandCompanyExchangeConfig Config => Plugin.Instance.Config.FastGrandCompanyExchange;

    // 解析快取（避免每幀對 900+ 子列做 LINQ）。
    private string cachedQuery = string.Empty;
    private byte cachedGc;
    private byte cachedRank;
    private DateTime cacheValidUntil = DateTime.MinValue;
    private Resolved? cached;

    private string statusText = string.Empty;

    private readonly record struct Resolved(
        uint ItemId, string Name, uint CostPerUnit, int Tier, int SubCategory);

    protected override void OnEnable()
    {
        queue.OnTimeout = step => SetStatus($"逾時中止：{step}");
        Svc.Framework.Update += OnFrameworkUpdate;
        Svc.PluginInterface.UiBuilder.Draw += DrawOverlay;
    }

    protected override void OnDisable()
    {
        Svc.Framework.Update -= OnFrameworkUpdate;
        Svc.PluginInterface.UiBuilder.Draw -= DrawOverlay;
        queue.Abort();
        cached = null;
        statusText = string.Empty;
    }

    private void OnFrameworkUpdate(IFramework framework) => queue.Tick();

    private void SetStatus(string text)
    {
        statusText = text;
        if (text.Length > 0) Svc.Log.Information($"[{InternalName}] {text}");
    }

    // ── 解析 ────────────────────────────────────────────────────────────────

    private static (byte Gc, byte Rank, uint Seals) GetGcState()
    {
        var ps = PlayerState.Instance();
        if (ps == null) return (0, 0, 0);

        var gc = ps->GrandCompany;
        var rank = ps->GetGrandCompanyRank();

        var manager = InventoryManager.Instance();
        var seals = manager == null ? 0u : manager->GetCompanySeals(gc);
        return (gc, rank, seals);
    }

    /// <summary>依目前輸入與軍隊狀態解析出要換的道具（快取；輸入或狀態變了才重算）。</summary>
    private Resolved? Resolve(string query, byte gc, byte rank)
    {
        if (DateTime.UtcNow < cacheValidUntil &&
            cachedQuery == query && cachedGc == gc && cachedRank == rank)
            return cached;

        cachedQuery = query;
        cachedGc = gc;
        cachedRank = rank;
        cacheValidUntil = DateTime.UtcNow.AddMilliseconds(500);
        cached = ResolveUncached(query, gc, rank);
        return cached;
    }

    private static Resolved? ResolveUncached(string query, byte gc, byte rank)
    {
        if (string.IsNullOrWhiteSpace(query) || gc == 0) return null;

        var itemSheet = Svc.Data.GetSubrowExcelSheet<GCScripShopItem>();
        var categorySheet = Svc.Data.GetExcelSheet<GCScripShopCategory>();
        if (itemSheet == null || categorySheet == null) return null;

        Resolved? best = null;
        var bestLen = int.MaxValue;

        foreach (var subrows in itemSheet)
        {
            foreach (var shopItem in subrows)
            {
                var category = categorySheet.GetRowOrDefault(shopItem.RowId);
                if (!category.HasValue) continue;
                if (category.Value.GrandCompany.RowId != gc) continue;
                if (rank < shopItem.RequiredGrandCompanyRank.RowId) continue;

                var item = shopItem.Item.ValueNullable;
                if (item == null) continue;

                var name = item.Value.Name.ExtractText();
                if (name.Length == 0) continue;
                if (!name.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;

                // 最短名優先：跟 DR 一樣，讓「幻水」之類的部分輸入命中最貼近的品項。
                if (name.Length >= bestLen) continue;

                bestLen = name.Length;
                best = new Resolved(
                    item.Value.RowId, name, shopItem.CostGCSeals,
                    category.Value.Tier, category.Value.SubCategory);
            }
        }

        return best;
    }

    // ── 交換 ────────────────────────────────────────────────────────────────

    private void StartExchange(Resolved resolved, int count)
    {
        if (queue.IsBusy) return;
        if (count <= 0) return;

        SetStatus($"交換「{resolved.Name}」×{count}…");

        // 步驟一：切到正確的軍階分頁。
        queue.Enqueue("切換軍階分頁", () =>
        {
            var addon = UiHelper.GetAddon(AddonName);
            if (!UiHelper.IsReady(addon)) { SetStatus("已中止：交換視窗已關閉。"); return null; }

            var targetTier = resolved.Tier - 1;
            if (ReadUInt(addon, TierValueIndex) == (uint)targetTier) return true;

            UiHelper.FireCallback(addon, true, 1, targetTier);
            return false;
        });

        // 步驟二：切到正確的子分類。
        queue.Enqueue("切換子分類", () =>
        {
            var addon = UiHelper.GetAddon(AddonName);
            if (!UiHelper.IsReady(addon)) { SetStatus("已中止：交換視窗已關閉。"); return null; }

            UiHelper.FireCallback(addon, true, 2, resolved.SubCategory);
            return true;
        });

        // 讓清單刷新。
        queue.EnqueueDelay(250, "等待清單刷新");

        // 步驟三：在清單裡找到名稱完全相符的那一格，送出交換事件。找不到就中止（fail-closed）。
        queue.Enqueue("送出交換", () =>
        {
            var addon = UiHelper.GetAddon(AddonName);
            if (!UiHelper.IsReady(addon)) { SetStatus("已中止：交換視窗已關閉。"); return null; }

            for (var i = 0; i < ItemNameScanCount; i++)
            {
                var name = ReadAtkString(addon, ItemNameStartIndex + i);
                if (name.Length == 0) continue;
                if (!string.Equals(name, resolved.Name, StringComparison.Ordinal)) continue;

                UiHelper.SendAgentEvent(AgentId.GrandCompanyExchange, 0, 0, i, count, 0, true, false);
                SetStatus($"已送出交換「{resolved.Name}」×{count}——請在遊戲跳出的確認框自行確認。");
                return true;
            }

            SetStatus("已中止：清單裡找不到該道具（台服版面可能與參考不同），未花任何軍票。");
            return null;
        });
    }

    private static uint ReadUInt(AtkUnitBase* addon, int index)
    {
        if (addon == null || addon->AtkValues == null) return 0;
        if (index < 0 || index >= addon->AtkValuesCount) return 0;
        var v = addon->AtkValues[index];
        return v.Type is ValueType.Int or ValueType.UInt ? v.UInt : 0;
    }

    /// <summary>只在該格確實是字串型別時才解參考——否則回空字串（避免把非指標當指標解 → AVE）。</summary>
    private static string ReadAtkString(AtkUnitBase* addon, int index)
    {
        if (addon == null || addon->AtkValues == null) return string.Empty;
        if (index < 0 || index >= addon->AtkValuesCount) return string.Empty;

        var v = addon->AtkValues[index];
        if (v.Type is not (ValueType.String or ValueType.String8 or ValueType.ManagedString))
            return string.Empty;

        var ptr = v.String.Value;
        if (ptr == null) return string.Empty;

        return MemoryHelper.ReadSeStringNullTerminated((nint)ptr).TextValue;
    }

    // ── UI ──────────────────────────────────────────────────────────────────

    private void DrawOverlay()
    {
        var addon = UiHelper.GetAddon(AddonName);
        if (!UiHelper.IsReady(addon)) return;

        const ImGuiWindowFlags flags = ImGuiWindowFlags.AlwaysAutoResize |
                                       ImGuiWindowFlags.NoCollapse |
                                       ImGuiWindowFlags.NoSavedSettings;

        if (ImGui.Begin("軍票快速交換##TCToolboxGCE", flags))
        {
            ImGui.SetWindowPos(new Vector2(addon->GetX() - ImGui.GetWindowSize().X - 4, addon->GetY()));
            DrawPanel();
        }

        ImGui.End();
    }

    private void DrawPanel()
    {
        var (gc, rank, seals) = GetGcState();
        if (gc == 0)
        {
            ImGui.TextDisabled("你目前沒有加入軍隊。");
            return;
        }

        ImGui.Text($"現有軍票：{seals:N0}");

        var itemName = Config.ItemName;
        if (ImGui.InputText("道具名（可只輸入片段）##gce", ref itemName, 64))
        {
            Config.ItemName = itemName;
            Plugin.Instance.Config.Save();
        }

        var count = Config.Count;
        if (ImGui.InputInt("數量（-1＝可負擔上限）##gce", ref count, 1))
        {
            Config.Count = Math.Max(-1, count);
            Plugin.Instance.Config.Save();
        }

        var resolved = Resolve(itemName, gc, rank);

        if (resolved is not { } r)
        {
            ImGui.TextColored(new Vector4(1f, 0.85f, 0.35f, 1f),
                              string.IsNullOrWhiteSpace(itemName) ? "請輸入道具名。" : "找不到符合的可交換道具。");
        }
        else
        {
            var affordable = r.CostPerUnit == 0 ? 0 : (int)(seals / r.CostPerUnit);
            var planned = Config.Count == -1 ? affordable : Math.Min(Config.Count, affordable);

            ImGui.Separator();
            ImGui.Text($"品項：{r.Name}");
            ImGui.Text($"單價：{r.CostPerUnit:N0} 軍票／可負擔 {affordable:N0} 個");
            ImGui.TextColored(new Vector4(1f, 0.8f, 0.35f, 1f),
                              $"將交換 {planned:N0} 個，花費 {(long)planned * r.CostPerUnit:N0} 軍票");

            using (ImRaii.Disabled(queue.IsBusy || planned <= 0))
            {
                if (ImGui.Button($"交換 {planned:N0} 個##gceGo"))
                    StartExchange(r, planned);
            }

            if (queue.IsBusy)
            {
                ImGui.SameLine();
                if (ImGui.Button("停止##gce"))
                {
                    queue.Abort();
                    SetStatus("已手動停止。");
                }
            }
        }

        if (UiHelper.IsAddonReady("SelectYesno"))
            ImGui.TextColored(new Vector4(0.5f, 1f, 0.5f, 1f), "遊戲跳出確認框，請自行確認（不會自動點）。");

        if (statusText.Length > 0)
            ImGui.TextDisabled(statusText);

        ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f),
                          "※ 版面位置取自國際服，台服若對不上會「找不到而不交換」，不會誤花軍票。");
    }

    public override void DrawConfig()
    {
        ImGui.TextDisabled("在「軍隊物資交換」視窗開著時，畫面上會出現快速交換小面板。");
        ImGui.TextDisabled("花軍票的最後確認一律由你自己按，本模組不會自動點掉確認框。");
    }
}

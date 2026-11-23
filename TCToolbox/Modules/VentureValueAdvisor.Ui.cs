using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using TCToolbox.Core;

namespace TCToolbox.Modules;

public sealed partial class VentureValueAdvisor
{
    private static readonly Vector4 WarnColor = new(1f, 0.72f, 0.35f, 1f);

    private const string FetchTooltip =
        "按一下才向 Universalis 查價，不會自己定時查。\n"
        + "只送物品編號與世界編號（每個世界每 100 件一次請求、依序送），不送角色名。\n"
        + "查的是每位僱員可接的指定探險與目前的探險，用角色的家世界行情（只能在家世界賣）；5 分鐘內查過的不重查。";

    private const string ValueHeaderTooltip =
        "穩健價值＝扣稅淨單價 × 一趟的數量 × 賣得完係數。\n"
        + "淨單價取家世界最近成交均價與目前最低掛單較低的那個，再扣 5% 稅（稅率依城市每週變動、最高 5%，所以是保守估計）。\n"
        + "「?」＝不知道（沒有掛單也沒有成交、或沒有成交紀錄）；淡色＝資料太舊或僱員屬性不知道（數量是最低一級）。";

    private const string SellThroughHeaderTooltip =
        "賣得完係數＝min(1, 家世界每日成交件數 ÷ 一趟的數量)。\n"
        + "低於 100% 代表一天賣不完一趟帶回的量，賣不掉的部分不算錢。「無成交」＝這件在家世界沒有成交紀錄，算不出來。";

    private static readonly string[] Headers = ["##expand", "角色", "僱員", "目前探險", "最賺的探險", "每趟", "每小時", "賣得完", "資料時間"];

    private int sortColumn = -1;
    private bool sortDescending;
    private readonly List<RetainerView> sorted = [];
    private readonly HashSet<string> expanded = [];

    private void ResetSort()
    {
        sortColumn = -1;
        sortDescending = false;
        sorted.Clear();
        expanded.Clear();
    }

    public override void DrawConfig()
    {
        if (ImGui.Button("開啟視窗")) ToggleWindow();
        ImGui.SameLine();
        ImGui.TextDisabled($"或輸入 {Command}");

        var top = Config.TopCount;
        ImGui.SetNextItemWidth(120 * ImGuiHelpers.GlobalScale);
        if (ImGui.SliderInt("展開時列出前幾名", ref top, 1, 20))
        {
            Config.TopCount = top;
            Plugin.Instance.Config.Save();
        }

        var stale = Config.StaleHours;
        ImGui.SetNextItemWidth(120 * ImGuiHelpers.GlobalScale);
        if (ImGui.SliderInt("行情超過幾小時算太舊", ref stale, 1, 720))
        {
            Config.StaleHours = Math.Clamp(stale, 1, 720);
            Plugin.Instance.Config.Save();
            viewsDirty = true;
        }
        Tip("超過這個時間沒有人上傳過這件物品在家世界的行情，就畫成淡色。冷門物品常是幾十天前的資料。");
    }

    internal void DrawPanel()
    {
        DrawToolbar();

        if (arState is ArState.NotLoaded or ArState.NoEndpoint)
        {
            ImGui.TextColored(WarnColor, NeedAutoRetainerText);
            Tip(arDetail);
            return;
        }

        if (focusItemId != 0) DrawFocus();
        DrawTable();
    }

    private void DrawToolbar()
    {
        var reloadDisabled = loading;
        using (ImRaii.Disabled(reloadDisabled))
        {
            if (ImGui.Button("重新讀取僱員")) pendingLoad = true;
        }
        Tip(reloadDisabled ? "正在讀取中。" : "向 AutoRetainer 重新讀一次角色、僱員與各探險的帶回數量（不查價）。", true);

        ImGui.SameLine();
        var fetching = fetcher?.IsFetching == true;
        var fetchDisabled = loading || fetching || roster.Count == 0;
        using (ImRaii.Disabled(fetchDisabled))
        {
            if (ImGui.Button("查價")) pendingFetchAll = true;
        }
        var reason = loading ? "正在讀取僱員資料。\n\n" : fetching ? "正在查價。\n\n" : roster.Count == 0 ? "還沒有讀到任何僱員。\n\n" : string.Empty;
        Tip(reason + FetchTooltip, true);

        ImGui.SameLine();
        if (loading)
        {
            ImGui.TextDisabled($"讀取中（剩 {loadQueue.Count} 個角色）");
        }
        else if (fetching && fetcher != null)
        {
            var (done, total) = fetcher.Progress;
            ImGui.TextDisabled($"查價中 {done}/{total}");
        }
        else
        {
            var status = string.Join("；", new[] { loadSummary, fetchSummary }.Where(s => s.Length > 0));
            ImGui.TextDisabled(status.Length > 0 ? status : "還沒有讀取");
            if (lastFetchError.Length > 0) Tip($"最後一次查價錯誤：{lastFetchError}");
        }
    }

    private void DrawFocus()
    {
        ImGui.Separator();
        ImGui.TextUnformatted($"定位：{ItemName(focusItemId)}");
        ImGui.SameLine();
        var disabled = loading || fetcher?.IsFetching != false || roster.Count == 0;
        using (ImRaii.Disabled(disabled))
        {
            if (ImGui.SmallButton("只查這件的價")) pendingFetchItem = focusItemId;
        }
        Tip("只向 Universalis 查這一件物品在各角色家世界的行情。", true);
        ImGui.SameLine();
        if (ImGui.SmallButton("取消定位")) focusItemId = 0;

        var any = false;
        foreach (var v in views)
        {
            if (v.Retainer?.Yields == null) continue;
            foreach (var y in v.Retainer.Yields)
            {
                if (y.ItemId != focusItemId) continue;
                any = true;
                var e = Evaluate(v.Character, y, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Math.Max(1, Config.StaleHours) * 3_600_000L);
                ImGui.TextUnformatted($"　{v.Character.Name}@{v.Character.World}／{v.Retainer.Name}：");
                ImGui.SameLine();
                if (!y.Available)
                {
                    ImGui.TextDisabled("目前接不了");
                    Tip("等級、平均物品等級或獲得力不夠，或採集類還沒採集過這件。");
                    continue;
                }
                DrawAmount(e);
                ImGui.SameLine();
                ImGui.TextUnformatted("每趟");
                ImGui.SameLine();
                DrawGil(e, e.Value.Robust);
                ImGui.SameLine();
                ImGui.TextUnformatted("每小時");
                ImGui.SameLine();
                DrawGil(e, e.Value.RobustPerHour);
                ImGui.SameLine();
                DrawSellThrough(e);
                ImGui.SameLine();
                DrawAge(e);
                break;
            }
        }

        if (!any) ImGui.TextDisabled("　沒有僱員的指定探險會帶回這件物品（或還沒讀到僱員）。");
        ImGui.Separator();
    }

    private void DrawTable()
    {
        if (roster.Count == 0)
        {
            ImGui.TextDisabled(loading ? "讀取中…" : "還沒有讀到任何僱員。按「重新讀取僱員」。");
            return;
        }

        const ImGuiTableFlags flags = ImGuiTableFlags.Sortable | ImGuiTableFlags.SortTristate | ImGuiTableFlags.Borders
                                      | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable
                                      | ImGuiTableFlags.SizingFixedFit;
        if (!ImGui.BeginTable("##ventureValue", Headers.Length, flags)) return;

        try
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn(Headers[0], ImGuiTableColumnFlags.NoSort | ImGuiTableColumnFlags.WidthFixed, 22 * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn(Headers[1]);
            ImGui.TableSetupColumn(Headers[2]);
            ImGui.TableSetupColumn(Headers[3], ImGuiTableColumnFlags.PreferSortDescending);
            ImGui.TableSetupColumn(Headers[4], ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.PreferSortDescending);
            ImGui.TableSetupColumn(Headers[5], ImGuiTableColumnFlags.PreferSortDescending);
            ImGui.TableSetupColumn(Headers[6], ImGuiTableColumnFlags.PreferSortDescending);
            ImGui.TableSetupColumn(Headers[7], ImGuiTableColumnFlags.PreferSortDescending);
            ImGui.TableSetupColumn(Headers[8]);
            DrawHeaders();
            ReadSortSpecs();

            foreach (var v in sorted)
                DrawViewRow(v);
        }
        finally
        {
            ImGui.EndTable();
        }
    }

    private static void DrawHeaders()
    {
        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
        for (var i = 0; i < Headers.Length; i++)
        {
            if (!ImGui.TableSetColumnIndex(i)) continue;
            ImGui.TableHeader(Headers[i]);
            switch (i)
            {
                case 3:
                    Tip("這位僱員現在派出去的探險與它的穩健價值。隨機帶回的探險（例如自由探索）算不出來。");
                    break;
                case 4:
                    Tip("可接的指定探險裡，穩健每小時價值最高的那一個。點左邊的箭頭看前幾名。\n要換探險請自己在 AutoRetainer 的探險計畫加進去，這裡不會替你換。");
                    break;
                case 5:
                case 6:
                    Tip(ValueHeaderTooltip);
                    break;
                case 7:
                    Tip(SellThroughHeaderTooltip);
                    break;
                case 8:
                    Tip("Universalis 最後一次收到這件物品在家世界行情的時間。");
                    break;
            }
        }
    }

    private void ReadSortSpecs()
    {
        var specs = ImGui.TableGetSortSpecs();
        if (!specs.SpecsDirty && sorted.Count == views.Count) return;

        if (specs.SpecsDirty)
        {
            if (specs.SpecsCount > 0)
            {
                sortColumn = specs.Specs.ColumnIndex;
                sortDescending = specs.Specs.SortDirection == ImGuiSortDirection.Descending;
            }
            else
            {
                sortColumn = -1;
            }
            specs.SpecsDirty = false;
        }

        ApplySort();
    }

    private void ApplySort()
    {
        sorted.Clear();
        sorted.AddRange(views);
        if (sortColumn < 1) return;

        var desc = sortDescending;
        Comparison<RetainerView> compare = sortColumn switch
        {
            1 => (a, b) => Ordinal(a.Character.Name, b.Character.Name, desc),
            2 => (a, b) => Ordinal(a.Retainer?.Name, b.Retainer?.Name, desc),
            3 => (a, b) => VentureValueMath.CompareNullLast(a.Current?.Value.RobustPerHour, b.Current?.Value.RobustPerHour, desc),
            4 or 6 => (a, b) => VentureValueMath.CompareNullLast(a.Best?.Value.RobustPerHour, b.Best?.Value.RobustPerHour, desc),
            5 => (a, b) => VentureValueMath.CompareNullLast(a.Best?.Value.Robust, b.Best?.Value.Robust, desc),
            7 => (a, b) => VentureValueMath.CompareNullLast(a.Best?.Value.SellThrough, b.Best?.Value.SellThrough, desc),
            8 => (a, b) => VentureValueMath.CompareNullLast(UploadedAt(a.Best), UploadedAt(b.Best), desc),
            _ => (a, b) => 0,
        };

        // 穩定排序：同分維持 AutoRetainer 的順序。
        sorted.Sort((a, b) =>
        {
            var c = compare(a, b);
            return c != 0 ? c : a.Order.CompareTo(b.Order);
        });
    }

    private static int Ordinal(string? a, string? b, bool desc)
    {
        var c = string.CompareOrdinal(a ?? string.Empty, b ?? string.Empty);
        return desc ? -c : c;
    }

    private static double? UploadedAt(VentureEval? e) =>
        e?.Market is { UploadedAtUnixMs: > 0 } m ? m.UploadedAtUnixMs : null;

    private void DrawViewRow(RetainerView v)
    {
        using var id = ImRaii.PushId(v.Key);
        ImGui.TableNextRow();

        ImGui.TableNextColumn();
        var open = expanded.Contains(v.Key);
        if (v.Ranked.Count > 0 && ImGui.ArrowButton("##expand", open ? ImGuiDir.Down : ImGuiDir.Right))
        {
            if (open) expanded.Remove(v.Key);
            else expanded.Add(v.Key);
            open = !open;
        }

        ImGui.TableNextColumn();
        ImGui.TextUnformatted($"{v.Character.Name}@{v.Character.World}");
        if (v.Character.WorldId == 0) Tip("世界名在世界表裡對不上唯一一個世界，這個角色不查價。");

        ImGui.TableNextColumn();
        if (v.Retainer is not { } r)
        {
            ImGui.TextDisabled(v.Note);
            return;
        }

        ImGui.TextUnformatted($"{r.Name}  Lv{r.Level} {JobName(r.Job)}");
        if (r.Stat < 0)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("屬性?");
            Tip("AutoRetainer 還沒記到這位僱員的平均物品等級／鑑別力：到傳喚鈴打開這位僱員一次就會記下。\n在那之前數量只能算最低一級（淡色、前面加 ≥）。");
        }
        if (v.Note.Length > 0)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("?");
            Tip(v.Note);
        }

        ImGui.TableNextColumn();
        if (v.Current is { } cur)
        {
            ImGui.TextUnformatted(cur.ItemName);
            ImGui.SameLine();
            DrawGil(cur, cur.Value.RobustPerHour, "/時");
            EvalTooltip(cur);
        }
        else
        {
            ImGui.TextDisabled(v.CurrentLabel);
        }

        ImGui.TableNextColumn();
        if (v.Best is { } best)
        {
            DrawVentureName(best);
            ImGui.TableNextColumn();
            DrawGil(best, best.Value.Robust);
            ImGui.TableNextColumn();
            DrawGil(best, best.Value.RobustPerHour);
            ImGui.TableNextColumn();
            DrawSellThrough(best);
            ImGui.TableNextColumn();
            DrawAge(best);
        }
        else
        {
            ImGui.TextDisabled(r.Yields == null ? "?" : "沒有可接的指定探險");
        }

        if (!open) return;

        var rank = 0;
        foreach (var e in v.Ranked)
        {
            if (rank >= Math.Max(1, Config.TopCount)) break;
            rank++;
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TableNextColumn();
            ImGui.TableNextColumn();
            ImGui.TextDisabled($"　第 {rank} 名");
            ImGui.TableNextColumn();
            if (v.Current != null && v.Current.Yield.VentureId == e.Yield.VentureId) ImGui.TextDisabled("（目前這個）");
            ImGui.TableNextColumn();
            DrawVentureName(e);
            ImGui.TableNextColumn();
            DrawGil(e, e.Value.Robust);
            ImGui.TableNextColumn();
            DrawGil(e, e.Value.RobustPerHour);
            ImGui.TableNextColumn();
            DrawSellThrough(e);
            ImGui.TableNextColumn();
            DrawAge(e);
        }
    }

    private static void DrawVentureName(VentureEval e)
    {
        ImGui.TextUnformatted(e.ItemName);
        ImGui.SameLine();
        DrawAmount(e);
        EvalTooltip(e);
    }

    private static void DrawAmount(VentureEval e)
    {
        var text = $"×{e.Yield.Amount}";
        if (e.Value.AmountIsLowerBound) ImGui.TextDisabled("≥" + text);
        else ImGui.TextUnformatted(text);
    }

    /// <summary>Gil 數值格：不知道畫「?」／「未查價」，資料太舊或數量只是下限畫淡色，絕不把不知道畫成 0。</summary>
    private static void DrawGil(VentureEval e, double? value, string suffix = "")
    {
        if (!e.Value.PriceFetched)
        {
            ImGui.TextDisabled(e.NoData ? "查無" : "未查價");
            if (e.NoData) Tip("Universalis 不認得這件物品（多半是不能在市場交易）。");
            return;
        }

        if (value is not { } v)
        {
            ImGui.TextDisabled("?");
            Tip(UnknownReason(e));
            return;
        }

        var text = $"{(e.Value.AmountIsLowerBound ? "≥" : string.Empty)}{Math.Floor(v):N0}{suffix}";
        if (e.Value.Stale || e.Value.AmountIsLowerBound) ImGui.TextDisabled(text);
        else ImGui.TextUnformatted(text);
    }

    private static void DrawSellThrough(VentureEval e)
    {
        if (!e.Value.PriceFetched)
        {
            ImGui.TextDisabled("—");
            return;
        }

        if (e.Value.SellThrough is not { } s)
        {
            ImGui.TextDisabled("無成交");
            Tip(SellThroughHeaderTooltip);
            return;
        }

        var text = $"{s * 100:0}%";
        if (s < 1 || e.Value.Stale) ImGui.TextDisabled(text);
        else ImGui.TextUnformatted(text);
        Tip($"家世界每日成交約 {e.Market?.DailySaleVelocity:0.#} 件，一趟帶回 {(e.Value.AmountIsLowerBound ? "至少 " : string.Empty)}{e.Yield.Amount} 件。");
    }

    private static void DrawAge(VentureEval e)
    {
        if (!e.Value.PriceFetched || e.Market is not { } m)
        {
            ImGui.TextDisabled(e.NoData ? "查無" : "未查價");
            return;
        }

        if (m.UploadedAtUnixMs <= 0)
        {
            ImGui.TextDisabled("沒有資料");
            Tip("從來沒有人上傳過這件物品在家世界的行情。");
            return;
        }

        var age = TimeSpan.FromMilliseconds(Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - m.UploadedAtUnixMs));
        var text = age.TotalMinutes < 60 ? $"{Math.Max(1, (int)age.TotalMinutes)} 分鐘前"
            : age.TotalHours < 48 ? $"{(int)age.TotalHours} 小時前"
            : $"{(int)age.TotalDays} 天前";
        if (e.Value.Stale) ImGui.TextDisabled(text);
        else ImGui.TextUnformatted(text);
        Tip(DateTimeOffset.FromUnixTimeMilliseconds(m.UploadedAtUnixMs).ToLocalTime().ToString("yyyy-MM-dd HH:mm")
            + (e.Value.Stale ? "（太舊，淡色顯示）" : string.Empty));
    }

    private static string UnknownReason(VentureEval e)
    {
        if (e.Value.NetUnitPrice == null) return "家世界沒有掛單也沒有成交紀錄，估不出單價。";
        if (e.Value.SellThrough == null) return "家世界沒有成交紀錄，算不出賣得完係數，所以不估穩健價值。";
        return "不知道。";
    }

    private static void EvalTooltip(VentureEval e)
    {
        if (!ImGui.IsItemHovered()) return;

        var y = e.Yield;
        var statName = y.UsesPerception ? "鑑別力" : "平均物品等級";
        var sb = new StringBuilder();
        sb.Append($"{e.ItemName}：一趟 {(e.Value.AmountIsLowerBound ? "至少 " : string.Empty)}{y.Amount} 個（{Stars(y.Tier)}），{y.Minutes} 分鐘\n");
        var points = VentureValueMath.PointsToNextTier(y.Stat, y.NextStat);
        if (y.Stat < 0) sb.Append($"{statName}不知道：到傳喚鈴打開這位僱員一次，AutoRetainer 才記得到。\n");
        else if (points is { } p && y.NextAmount > 0) sb.Append($"再 {p} 點{statName}升一級 → 一趟 {y.NextAmount} 個（目前 {y.Stat}，門檻 {y.NextStat}）\n");
        else sb.Append("已是最高一級。\n");

        if (e.Market is { } m)
        {
            sb.Append($"成交均價 {Gil(m.AverageSale)}、最低掛單 {Gil(m.MinListing)} → 扣 5% 稅淨單價 {Gil(e.Value.NetUnitPrice)}\n");
            sb.Append($"每趟價值 {Gil(e.Value.PerTrip)} × 賣得完係數 {(e.Value.SellThrough is { } s ? $"{s * 100:0}%" : "?")} ＝ 穩健價值 {Gil(e.Value.Robust)}");
        }
        else
        {
            sb.Append(e.NoData ? "Universalis 查無這件物品。" : "還沒查價。");
        }

        using var tooltip = ImRaii.Tooltip();
        ImGui.TextUnformatted(sb.ToString());
    }

    private static string Stars(int tier)
    {
        var t = Math.Clamp(tier, 0, 4);
        return new string('★', t) + new string('☆', 4 - t);
    }

    private static string Gil(double? v) => v is { } x and > 0 ? $"{Math.Floor(x):N0}" : "?";

    /// <param name="allowWhenDisabled">停用中的控制項預設不回報 hover，要帶這個旗標說明框才會出現。</param>
    private static void Tip(string text, bool allowWhenDisabled = false)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (!ImGui.IsItemHovered(allowWhenDisabled ? ImGuiHoveredFlags.AllowWhenDisabled : ImGuiHoveredFlags.None)) return;

        using var tooltip = ImRaii.Tooltip();
        ImGui.TextUnformatted(text);
    }
}

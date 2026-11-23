using System;
using System.Collections.Generic;
using System.Text.Json;

namespace TCToolbox.Core;

/// <summary>家世界某件物品的行情（Universalis 聚合端點 <c>nq.*.world</c> 那一層）。</summary>
/// <param name="MinListing">目前最低掛單單價；0＝這個世界沒有掛單。</param>
/// <param name="AverageSale">最近成交均價；0＝沒有成交紀錄。</param>
/// <param name="DailySaleVelocity">每日成交件數；只在 <paramref name="AverageSale"/> &gt; 0 時有意義。</param>
/// <param name="UploadedAtUnixMs">這個世界的這件物品最後一次有人上傳；0＝從沒有人上傳過。</param>
public readonly record struct WorldItemMarket(double MinListing, double AverageSale, double DailySaleVelocity, long UploadedAtUnixMs);

/// <summary>一趟探險的估值。<c>null</c> 的欄位一律代表「不知道」，畫面上不可畫成 0。</summary>
/// <param name="NetUnitPrice">扣稅後的單價（成交均價與最低掛單取低者）；null＝沒查價、或沒掛單也沒成交。</param>
/// <param name="PerTrip">每趟價值＝淨單價 × 數量（還沒打賣不完的折）。</param>
/// <param name="SellThrough">賣得完係數＝min(1, 日銷量 ÷ 數量)；null＝沒有成交紀錄。</param>
/// <param name="Robust">穩健每趟價值＝每趟價值 × 賣得完係數。</param>
/// <param name="RobustPerHour">穩健價值換算成每小時。</param>
/// <param name="AmountIsLowerBound">僱員屬性不知道，數量是最低一級的數量。</param>
/// <param name="PriceFetched">這件物品在這個世界查過價。</param>
/// <param name="Stale">資料太舊或從沒人上傳過（只在查過價時有意義）。</param>
public readonly record struct VentureValuation(
    double? NetUnitPrice,
    double? PerTrip,
    double? SellThrough,
    double? Robust,
    double? RobustPerHour,
    bool AmountIsLowerBound,
    bool PriceFetched,
    bool Stale);

/// <summary>僱員探險收益的純計算（不碰任何 Dalamud 服務，離線探針直接編譯本檔）。</summary>
public static class VentureValueMath
{
    /// <summary>市場稅率取上限 5%：實際依城市每週變動、只會更低，所以估出來的淨價偏保守。</summary>
    public const double TaxRate = 0.05;

    /// <summary>扣稅淨單價：成交均價與最低掛單都有就取低者，只有一個就用那個，都沒有回 null。</summary>
    public static double? NetUnitPrice(in WorldItemMarket market, double taxRate)
    {
        double? unit = null;
        if (market.AverageSale > 0) unit = market.AverageSale;
        if (market.MinListing > 0 && (unit == null || market.MinListing < unit)) unit = market.MinListing;
        return unit * (1 - taxRate);
    }

    /// <param name="amount">一趟帶回的數量；≤0＝不知道。</param>
    /// <param name="minutes">一趟的分鐘數；≤0＝每小時價值不知道。</param>
    /// <param name="market">家世界行情；null＝還沒查價（或 Universalis 不認得這件物品）。</param>
    public static VentureValuation Evaluate(int amount, bool amountIsLowerBound, int minutes, WorldItemMarket? market,
                                            double taxRate, long nowUnixMs, long staleAfterMs)
    {
        if (market is not { } m)
            return new VentureValuation(null, null, null, null, null, amountIsLowerBound, false, false);

        var stale = m.UploadedAtUnixMs <= 0 || nowUnixMs - m.UploadedAtUnixMs > staleAfterMs;
        var net = NetUnitPrice(m, taxRate);
        double? perTrip = amount > 0 ? net * amount : null;
        double? sellThrough = amount > 0 && m.AverageSale > 0 ? Math.Min(1.0, Math.Max(0.0, m.DailySaleVelocity) / amount) : null;
        var robust = perTrip * sellThrough;
        double? perHour = minutes > 0 ? robust * 60.0 / minutes : null;
        return new VentureValuation(net, perTrip, sellThrough, robust, perHour, amountIsLowerBound, true, stale);
    }

    /// <summary>「最賺」的排序：穩健每小時價值由高到低，不知道的一律排最後；同分比每趟價值。</summary>
    public static int CompareBestFirst(in VentureValuation a, in VentureValuation b)
    {
        var c = CompareNullLast(a.RobustPerHour, b.RobustPerHour, descending: true);
        return c != 0 ? c : CompareNullLast(a.PerTrip, b.PerTrip, descending: true);
    }

    /// <summary>有值的照方向排，<c>null</c> 不論方向都排在最後。</summary>
    public static int CompareNullLast(double? a, double? b, bool descending)
    {
        if (a == null) return b == null ? 0 : 1;
        if (b == null) return -1;
        var c = a.Value.CompareTo(b.Value);
        return descending ? -c : c;
    }

    /// <summary>升到下一級還差幾點；屬性不知道或已是最高級回 null。</summary>
    public static int? PointsToNextTier(int stat, int nextStat)
    {
        if (stat < 0 || nextStat <= 0) return null;
        return Math.Max(0, nextStat - stat);
    }

    /// <summary>解析 Universalis <c>/api/v2/aggregated/{世界}/{物品…}</c> 的回應，只取查詢那個世界的普通品。</summary>
    /// <param name="items">解析成功的物品；沒有 <c>world</c> 層的欄位填 0。</param>
    /// <param name="failed">Universalis 回報查不到的物品（例如不能交易）。</param>
    /// <returns>false＝回應形狀不對，兩個集合都沒有意義。</returns>
    public static bool TryParseAggregated(ReadOnlySpan<byte> utf8, uint worldId,
                                          out Dictionary<uint, WorldItemMarket> items, out List<uint> failed)
    {
        items = new Dictionary<uint, WorldItemMarket>();
        failed = [];
        if (utf8.Length >= 3 && utf8[0] == 0xEF && utf8[1] == 0xBB && utf8[2] == 0xBF) utf8 = utf8[3..];

        JsonDocument? document;
        try
        {
            var reader = new Utf8JsonReader(utf8);
            if (!JsonDocument.TryParseValue(ref reader, out document)) return false;
        }
        catch (JsonException)
        {
            return false;
        }

        using (document)
        {
            var root = document!.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) return false;

            foreach (var r in results.EnumerateArray())
            {
                if (r.ValueKind != JsonValueKind.Object) continue;
                if (!r.TryGetProperty("itemId", out var idElement) || !idElement.TryGetUInt32(out var itemId)) continue;

                r.TryGetProperty("nq", out var nq);
                var market = new WorldItemMarket(
                    WorldNumber(nq, "minListing", "price"),
                    WorldNumber(nq, "averageSalePrice", "price"),
                    WorldNumber(nq, "dailySaleVelocity", "quantity"),
                    UploadTime(r, worldId));
                items[itemId] = market;
            }

            if (root.TryGetProperty("failedItems", out var failedElement) && failedElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var f in failedElement.EnumerateArray())
                {
                    if (f.TryGetUInt32(out var id)) failed.Add(id);
                }
            }
        }

        return true;
    }

    private static double WorldNumber(JsonElement nq, string group, string field)
    {
        if (nq.ValueKind != JsonValueKind.Object) return 0;
        if (!nq.TryGetProperty(group, out var g) || g.ValueKind != JsonValueKind.Object) return 0;
        if (!g.TryGetProperty("world", out var w) || w.ValueKind != JsonValueKind.Object) return 0;
        if (!w.TryGetProperty(field, out var v) || v.ValueKind != JsonValueKind.Number) return 0;
        return v.TryGetDouble(out var d) && double.IsFinite(d) ? d : 0;
    }

    private static long UploadTime(JsonElement result, uint worldId)
    {
        if (!result.TryGetProperty("worldUploadTimes", out var times) || times.ValueKind != JsonValueKind.Array) return 0;
        foreach (var t in times.EnumerateArray())
        {
            if (t.ValueKind != JsonValueKind.Object) continue;
            if (!t.TryGetProperty("worldId", out var w) || !w.TryGetUInt32(out var id) || id != worldId) continue;
            if (t.TryGetProperty("timestamp", out var ts) && ts.TryGetInt64(out var at)) return at;
        }
        return 0;
    }
}

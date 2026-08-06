using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Lumina.Excel.Sheets;

namespace TCToolbox.Core;

/// <summary>天書格子解析的結果種類。<see cref="None"/> 是零值，<c>default</c> 落在「解析不出來」是刻意的。</summary>
public enum BingoTargetKind
{
    /// <summary>對不到；<b>不可以</b>拿去開任何副本。</summary>
    None = 0,

    /// <summary>單一副本（ContentFinderCondition RowId）。</summary>
    Duty = 1,

    /// <summary>輪盤（ContentRoulette RowId）。</summary>
    Roulette = 2,
}

/// <summary>一個天書格子解析後要開什麼。</summary>
/// <param name="Kind">解析結果種類。</param>
/// <param name="Id">副本或輪盤的 RowId；<see cref="BingoTargetKind.None"/> 時恆為 0。</param>
/// <param name="Name">解析到的名稱，給 UI 與 log 用。</param>
/// <param name="Reason">解析不出來的原因（成功時為空字串）。</param>
public readonly record struct BingoTarget(BingoTargetKind Kind, uint Id, string Name, string Reason)
{
    public static BingoTarget Fail(string reason) => new(BingoTargetKind.None, 0, string.Empty, reason);

    public bool IsResolved => Kind != BingoTargetKind.None;
}

/// <summary>
/// 把天書（<c>WeeklyBingo</c>）某一格對應到「該開哪個副本／輪盤」。
/// </summary>
/// <remarks>
/// 🔴 <b>刻意不抄 DailyRoutines 的寫死對照表。</b>DR 的 <c>WeeklyBingoClickToOpen</c> 對 Type 4
/// 用了一張 38 筆的 <c>bingoData → ContentFinderCondition RowId</c> 硬表。2026-08-06 拿台服
/// 7.20 的 EXD 逐筆核對，那張表在台服至少有 5 筆是錯的，而且錯法完全靜默：
/// <list type="bullet">
///   <item>Data 18「萬魔殿 邊境之獄3-4」→ DR 開 807，台服 807 是<b>零式</b>萬魔殿 邊境之獄3（正解 806）。</item>
///   <item>Data 22「萬魔殿 荒天之獄3-4」→ DR 開 941，台服 941 是<b>零式</b>萬魔殿 荒天之獄3（正解 940）。</item>
///   <item>Data 34 與 36 在 DR 表裡都指向 985（重複），且 Data 36/37/38 在台服
///         <c>WeeklyBingoText</c> 的描述是空字串（內容尚未開放）。</item>
/// </list>
/// 另外 DR 的 Type 2（區間迷宮）等級範圍是 <c>Data-9 .. Data-1</c>，對台服描述「51-59級迷宮」
/// 算出來是 50..58 —— 兩端都差一，會開出<b>等級 50</b> 的迷宮而那不算完成該格。
/// <para>
/// ⚠️ 這裡不用「<c>ContentType==5</c> ＋ <c>HighEndDuty==false</c> 篩掉零式」——實測台服 7.20 全表
/// 只有 13 列 <c>HighEndDuty==true</c>（絕／滅／幻／當期零式與極），舊零式（含零式萬魔殿全系列）
/// 一律是 <c>false</c>。拿它當零式篩子會靜默失效。
/// </para>
/// <para>
/// 改採的做法是<b>資料驅動</b>：用 <c>WeeklyBingoText</c> 的描述字串去比對
/// <c>ContentUICategory.Name</c> / <c>ContentFinderCondition.Name</c>，比對一律要求<b>完全相等</b>。
/// 完全相等是關鍵：零式的名稱是「零式萬魔殿 邊境之獄3」，前綴就不同，不可能誤中。
/// </para>
/// <para>
/// 🔴 對不到就回 <see cref="BingoTargetKind.None"/>，呼叫端<b>不准</b>拿「可能是」的副本去開。
/// </para>
/// </remarks>
public static class WeeklyBingoDutyResolver
{
    /// <summary>
    /// Type 3（特殊內容）裡走輪盤的兩格，鍵是 <c>WeeklyBingoOrderData</c> 的 RowId。
    /// </summary>
    /// <remarks>
    /// 這是全檔唯一一張寫死的表，因為輪盤沒有可靠的資料側關聯。兩筆都在台服 7.20 核對過：
    /// ContentRoulette 7 =「每日挑戰：紛爭前線」、40 =「水晶衝突（練習賽）」。
    /// 呼叫時另外過一道<b>名稱閘門</b>（天書描述必須是輪盤名稱的子字串），
    /// 所以將來若列號位移，結果是「不開」而不是「開錯」。
    /// </remarks>
    private static readonly Dictionary<uint, byte> RouletteByOrderRow = new()
    {
        [52] = 40, // 水晶衝突
        [54] = 7,  // 紛爭前線
    };

    /// <summary>等級區間類的格子 → 該找哪種 <c>ContentType</c>。Type 是遊戲自己的分類欄位，比描述字串穩定。</summary>
    private static readonly Dictionary<int, uint> ContentTypeByBingoType = new()
    {
        [1] = 2, // 指定等級迷宮
        [2] = 2, // 區間迷宮
        [5] = 2, // 多段區間迷宮
        [6] = 2, // 若干個上限等級的迷宮
        [7] = 4, // 討伐殲滅戰
        [8] = 5, // 台服描述「團隊任務」＝ 24 人大型任務
        [9] = 5, // 台服描述「大型任務」＝ 8 人團隊任務
    };

    /// <summary>
    /// Type 8/9 要再用人數區分。台服 7.20 實測：<c>ContentMemberType</c> 4 = 24 人（水晶塔／虛無方舟…），
    /// 3 = 8 人（巴哈姆特／伊甸／萬魔殿…）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 台服的字面用詞和 <c>ContentUICategory</c> 對得上但和 <c>ContentType</c> 對不上
    /// （<c>ContentType</c> 5 的名字是「大型任務」卻同時含 8 人與 24 人），所以這裡不看名稱只看人數。
    /// 對應方向與 DailyRoutines 的旗標選擇一致（DR Type 8 用 <c>AllianceRoulette</c>），
    /// 而且和台服 <c>ContentUICategory</c>「團隊任務（新生艾奧傑亞）」＝水晶塔（24 人）互相印證。
    /// </remarks>
    private static readonly Dictionary<int, uint> MemberTypeByBingoType = new()
    {
        [8] = 4,
        [9] = 3,
    };

    /// <summary>描述裡的「A-B」區間，例如「51-59級迷宮」。</summary>
    private static readonly Regex RangePattern = new(@"(\d+)\s*-\s*(\d+)", RegexOptions.Compiled);

    /// <summary>描述裡的任一數字。</summary>
    private static readonly Regex NumberPattern = new(@"\d+", RegexOptions.Compiled);

    /// <summary>描述結尾的「…<起始層>-<結束層>」或「…<層>」，例如「萬魔殿 邊境之獄3-4」。</summary>
    private static readonly Regex TrailingIndexPattern = new(@"^(.*?)(\d+)(?:\s*-\s*\d+)?$", RegexOptions.Compiled);

    private static readonly Dictionary<uint, BingoTarget> Cache = [];

    private static List<ContentFinderCondition>? sortedDuties;

    /// <summary>所有有名字的副本，依 <c>SortKey</c> 排序（＝任務搜尋器裡的順序）。</summary>
    private static List<ContentFinderCondition> Duties =>
        sortedDuties ??= Svc.Data.GetExcelSheet<ContentFinderCondition>()
            .Where(c => c.RowId != 0 && c.Name.ExtractText().Length > 0)
            .OrderBy(c => c.SortKey)
            .ToList();

    /// <summary>清掉快取（切換語言／重新載入資料表時用）。</summary>
    public static void ClearCache()
    {
        Cache.Clear();
        sortedDuties = null;
    }

    /// <summary>
    /// 解析 <c>WeeklyBingoOrderData</c> 的某一列要開什麼。結果會快取（資料表不會在執行期變動）。
    /// </summary>
    public static BingoTarget Resolve(uint orderRowId)
    {
        if (Cache.TryGetValue(orderRowId, out var cached)) return cached;

        BingoTarget result;
        try
        {
            result = ResolveCore(orderRowId);
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, $"[WeeklyBingoDutyResolver] 解析第 {orderRowId} 列時發生例外");
            result = BingoTarget.Fail("解析時發生例外，詳見 log");
        }

        Cache[orderRowId] = result;
        return result;
    }

    /// <summary>取得某一列的天書敘述（給 UI 顯示用）；沒有就回空字串。</summary>
    public static string GetDescription(uint orderRowId)
    {
        var order = Svc.Data.GetExcelSheet<WeeklyBingoOrderData>().GetRowOrDefault(orderRowId);
        if (order is null) return string.Empty;
        return order.Value.Text.ValueNullable?.Description.ExtractText().Trim() ?? string.Empty;
    }

    private static BingoTarget ResolveCore(uint orderRowId)
    {
        var order = Svc.Data.GetExcelSheet<WeeklyBingoOrderData>().GetRowOrDefault(orderRowId);
        if (order is null)
            return BingoTarget.Fail($"WeeklyBingoOrderData 沒有第 {orderRowId} 列");

        var row = order.Value;
        var bingoType = (int)row.Type;
        var data = row.Data.RowId;
        var description = row.Text.ValueNullable?.Description.ExtractText().Trim() ?? string.Empty;

        // 輪盤優先：水晶衝突／紛爭前線的個別地圖無法單獨排隊，只能走輪盤。
        if (RouletteByOrderRow.TryGetValue(orderRowId, out var rouletteId))
            return ResolveRoulette(rouletteId, description);

        return bingoType switch
        {
            0 => ResolveByInstanceContent(data),
            3 => ResolveSpecial(description),
            4 => ResolveRaidGroup(description),
            _ => ResolveByLevelBand(bingoType, description),
        };
    }

    /// <summary>Type 0：<c>Data</c> 直接是 InstanceContent 的 RowId，走身分比對，沒有猜的成分。</summary>
    private static BingoTarget ResolveByInstanceContent(uint instanceContentId)
    {
        if (instanceContentId == 0)
            return BingoTarget.Fail("這一列沒有指定內容（Data = 0）");

        // ContentLinkType 1 才是 InstanceContent；不設這個條件會和其他連結種類的 RowId 撞號。
        var hits = Duties.Where(c => c.ContentLinkType == 1 && c.Content.RowId == instanceContentId).ToList();

        if (hits.Count == 1)
            return new BingoTarget(BingoTargetKind.Duty, hits[0].RowId, hits[0].Name.ExtractText(), string.Empty);

        return BingoTarget.Fail(hits.Count == 0
            ? $"台服沒有 InstanceContent {instanceContentId} 對應的副本（內容可能已下架）"
            : $"InstanceContent {instanceContentId} 對到 {hits.Count} 個副本，無法判斷要開哪個");
    }

    /// <summary>
    /// Type 3：特殊內容。只支援「描述完全等於某個 <c>ContentType</c> 名稱」這一種
    /// （台服 7.20 只有「深層迷宮」命中）。
    /// </summary>
    /// <remarks>
    /// 「寶物庫」根本不能從任務搜尋器排隊，「烈羽爭鋒」在台服對不到可靠的資料側關聯，兩者一律不開。
    /// 這是刻意的取捨：少開一格的代價是使用者自己去搜尋器點，開錯的代價是真的浪費一趟。
    /// </remarks>
    private static BingoTarget ResolveSpecial(string description)
    {
        if (description.Length == 0)
            return BingoTarget.Fail("天書描述是空的");

        var contentTypes = Svc.Data.GetExcelSheet<ContentType>()
            .Where(t => t.RowId != 0 && t.Name.ExtractText() == description)
            .ToList();

        if (contentTypes.Count == 1)
        {
            var first = Duties.FirstOrDefault(c => c.ContentType.RowId == contentTypes[0].RowId);
            if (first.RowId != 0)
                return new BingoTarget(BingoTargetKind.Duty, first.RowId, first.Name.ExtractText(), string.Empty);
        }

        return BingoTarget.Fail($"「{description}」沒有可以直接開啟的對應項目");
    }

    /// <summary>
    /// Type 4：指定的團隊／大型任務。兩條資料驅動的路，都要求<b>完全相等</b>。
    /// </summary>
    private static BingoTarget ResolveRaidGroup(string description)
    {
        if (description.Length == 0)
            return BingoTarget.Fail("天書描述是空的（此項目台服尚未開放）");

        // (a) 描述整串等於某個任務搜尋器分類名，例如「團隊任務（新生艾奧傑亞）」。
        var categories = Svc.Data.GetExcelSheet<ContentUICategory>()
            .Where(c => c.RowId != 0 && c.Name.ExtractText() == description)
            .ToList();

        if (categories.Count == 1)
        {
            var first = Duties.FirstOrDefault(c => c.ContentUICategory.RowId == categories[0].RowId);
            if (first.RowId != 0)
                return new BingoTarget(BingoTargetKind.Duty, first.RowId, first.Name.ExtractText(), string.Empty);
        }

        // (b) 描述是「<前綴><起始層>-<結束層>」，用「<前綴><起始層>」跟副本名稱完全比對。
        //     完全比對是零式防線：零式的名稱多了「零式」兩個字，前綴就不同。
        var match = TrailingIndexPattern.Match(description);
        if (match.Success)
        {
            var wanted = match.Groups[1].Value + match.Groups[2].Value;
            var exact = Duties.Where(c => c.Name.ExtractText() == wanted).ToList();
            if (exact.Count == 1)
                return new BingoTarget(BingoTargetKind.Duty, exact[0].RowId, exact[0].Name.ExtractText(), string.Empty);

            if (exact.Count > 1)
                return BingoTarget.Fail($"「{wanted}」對到 {exact.Count} 個同名副本，無法判斷");
        }

        return BingoTarget.Fail($"「{description}」對不到任何副本");
    }

    /// <summary>
    /// Type 1/2/5/6/7/8/9：等級區間類。等級範圍直接從天書描述裡的數字取，
    /// 不用寫死公式（DR 的公式在台服兩端各差一）。
    /// </summary>
    private static BingoTarget ResolveByLevelBand(int bingoType, string description)
    {
        if (!ContentTypeByBingoType.TryGetValue(bingoType, out var contentType))
            return BingoTarget.Fail($"不支援的天書項目類型 {bingoType}");

        if (description.Length == 0)
            return BingoTarget.Fail("天書描述是空的");

        var levels = ParseLevels(description);
        if (levels is null)
            return BingoTarget.Fail($"「{description}」取不到等級範圍");

        MemberTypeByBingoType.TryGetValue(bingoType, out var memberType);

        var pick = Duties.FirstOrDefault(c =>
            c.ContentType.RowId == contentType &&
            !c.HighEndDuty &&
            levels.Contains(c.ClassJobLevelRequired) &&
            (memberType == 0 || c.ContentMemberType.RowId == memberType));

        if (pick.RowId == 0)
            return BingoTarget.Fail($"「{description}」在台服找不到符合的副本");

        return new BingoTarget(BingoTargetKind.Duty, pick.RowId, pick.Name.ExtractText(), string.Empty);
    }

    /// <summary>
    /// 從天書描述裡拆出等級集合。「A-B」當閉區間，落單的數字當單點。
    /// 「51-59級/61-69級/71-79級迷宮」→ 三段聯集；「50或60級迷宮」→ {50, 60}；「90級迷宮」→ {90}。
    /// </summary>
    private static HashSet<int>? ParseLevels(string description)
    {
        var levels = new HashSet<int>();
        var consumed = new List<(int Start, int End)>();

        foreach (Match m in RangePattern.Matches(description))
        {
            if (!int.TryParse(m.Groups[1].Value, out var low)) return null;
            if (!int.TryParse(m.Groups[2].Value, out var high)) return null;
            if (low < 1 || high < low || high > 999) return null;

            for (var lv = low; lv <= high; lv++)
                levels.Add(lv);

            consumed.Add((m.Index, m.Index + m.Length));
        }

        foreach (Match m in NumberPattern.Matches(description))
        {
            if (consumed.Any(span => m.Index >= span.Start && m.Index < span.End)) continue;
            if (!int.TryParse(m.Value, out var lv)) return null;
            if (lv is < 1 or > 999) return null;
            levels.Add(lv);
        }

        return levels.Count > 0 ? levels : null;
    }

    /// <summary>輪盤：先過名稱閘門，對不上就寧可不開。</summary>
    private static BingoTarget ResolveRoulette(byte rouletteId, string description)
    {
        var roulette = Svc.Data.GetExcelSheet<ContentRoulette>().GetRowOrDefault(rouletteId);
        var name = roulette?.Name.ExtractText() ?? string.Empty;

        if (name.Length == 0)
            return BingoTarget.Fail($"台服沒有 ContentRoulette {rouletteId}");

        // 天書描述必須是輪盤名稱的一部分（「水晶衝突」⊂「水晶衝突（練習賽）」、
        // 「紛爭前線」⊂「每日挑戰：紛爭前線」）。將來列號位移時這裡會擋下來。
        if (description.Length == 0 || !name.Contains(description, StringComparison.Ordinal))
            return BingoTarget.Fail($"輪盤名稱閘門不通過：天書寫「{description}」，ContentRoulette {rouletteId} 是「{name}」");

        return new BingoTarget(BingoTargetKind.Roulette, rouletteId, name, string.Empty);
    }
}

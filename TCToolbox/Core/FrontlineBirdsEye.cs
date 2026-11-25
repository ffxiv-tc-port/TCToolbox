using System;
using System.Numerics;

namespace TCToolbox.Core;

// 本檔假設與契約：
// - 純受管理、不依賴 Dalamud：前線目標面板鳥瞰圖的範圍、隊伍判定、領先隊與圖示大小；離線探針直接連結本檔。
// - 各圖的可走範圍是 BMR 錄影裡全部玩家位置的外框（每圖 2～5 場、去掉換區瞬間的離群值），不是官方資料。
// - 隊伍 0／1／2＝黑渦團／雙蛇黨／不滅隊，與 FrontlineObjectiveTracker 同一套編號。

/// <summary>鳥瞰圖的可視範圍：該圖可走範圍 ∪ 目標點，比賽中只會變大（看到自己走出去就撐開），不會隨人群移動而跳動。</summary>
public sealed class FrontlineViewArea
{
    /// <summary>外框以外還要多留的比例（每邊）。</summary>
    public const float MarginFraction = 0.04f;

    /// <summary>離可走範圍超過這麼多米的位置當成換區瞬間的雜訊，不撐開範圍（錄影裡出現過離中心 800 米的單點）。</summary>
    public const float MaxGrowMeters = 150f;

    private Vector2 baseMin;
    private Vector2 baseMax;

    public Vector2 Min { get; private set; }

    public Vector2 Max { get; private set; }

    public bool Valid { get; private set; }

    /// <summary>比賽中被撐開過（自己的位置超出預設範圍）。</summary>
    public bool Grown { get; private set; }

    public void Reset(FrontlineMap map, (Vector2 Min, Vector2 Max)? objectiveBounds)
    {
        Valid = false;
        Grown = false;
        var area = WalkableArea(map);
        if (area is { } a)
        {
            Min = a.Min;
            Max = a.Max;
            Valid = true;
        }

        if (objectiveBounds is { } o)
        {
            Min = Valid ? Vector2.Min(Min, o.Min) : o.Min;
            Max = Valid ? Vector2.Max(Max, o.Max) : o.Max;
            Valid = true;
        }

        baseMin = Min;
        baseMax = Max;
    }

    /// <returns>true＝這個點讓範圍變大了。</returns>
    public bool Include(Vector2 p)
    {
        if (!Valid || (p.X >= Min.X && p.X <= Max.X && p.Y >= Min.Y && p.Y <= Max.Y))
            return false;
        var outside = Vector2.Max(Vector2.Max(baseMin - p, p - baseMax), Vector2.Zero);
        if (outside.X > MaxGrowMeters || outside.Y > MaxGrowMeters || !float.IsFinite(p.X) || !float.IsFinite(p.Y))
            return false;
        Min = Vector2.Min(Min, p);
        Max = Vector2.Max(Max, p);
        Grown = true;
        return true;
    }

    /// <summary>正方形可視區：中心與邊長（世界米）。</summary>
    public (Vector2 Center, float Extent) View()
    {
        var center = (Min + Max) * 0.5f;
        var side = MathF.Max(MathF.Max(Max.X - Min.X, Max.Y - Min.Y), 1f);
        return (center, side * (1f + (2f * MarginFraction)));
    }

    /// <summary>各圖可走範圍（世界 X／Z）；不認得的圖回 null（退回只看目標點）。</summary>
    public static (Vector2 Min, Vector2 Max)? WalkableArea(FrontlineMap map) => map switch
    {
        FrontlineMap.SealRock => (new(-325f, -255f), new(240f, 290f)),
        FrontlineMap.FieldsOfGlory => (new(-250f, -175f), new(240f, 280f)),
        FrontlineMap.OnsalHakair => (new(-270f, -205f), new(205f, 270f)),
        FrontlineMap.Secure => (new(-270f, -280f), new(250f, 215f)),
        _ => null,
    };
}

/// <summary>隊伍判定、領先隊與圖示大小。</summary>
public static class FrontlineTeamRules
{
    // Battalion 表裡三個互相敵對、對自己友好的連號組：0..2、6..8、9..11。
    private static readonly byte[] CandidateBases = [0, 6, 9];

    /// <summary>
    /// 由自己的 Battalion 決定三隊的起始值；自己不在任何一組＝不知道（null）。
    /// 假設組內順序＝黑渦團、雙蛇黨、不滅隊（未實機驗證；每場的 Information 行有自己的值可對）。
    /// </summary>
    public static int? BattalionBase(byte self)
    {
        foreach (var b in CandidateBases)
        {
            if (self >= b && self <= b + 2)
                return b;
        }

        return null;
    }

    /// <returns>0..2；-1＝不知道。</returns>
    public static int TeamOf(byte battalion, int? battalionBase)
    {
        if (battalionBase is not { } b)
            return -1;
        var t = battalion - b;
        return t is >= 0 and <= 2 ? t : -1;
    }

    /// <summary>分數最高（且大於 0）的隊伍位元組；並列時都算。-1 分＝還不知道，不參與比較。</summary>
    public static int LeaderMask(ReadOnlySpan<int> scores)
    {
        var max = 0;
        foreach (var s in scores)
            max = Math.Max(max, s);
        if (max <= 0)
            return 0;
        var mask = 0;
        for (var i = 0; i < scores.Length && i < 3; i++)
        {
            if (scores[i] == max)
                mask |= 1 << i;
        }

        return mask;
    }

    /// <summary>
    /// 玩家點的倍率：分數領先的敵隊放大，其餘 1。
    /// 自己隊單獨領先＝沒有敵隊在遮罩裡，不放大；和敵隊並列領先時那支敵隊照樣放大（同樣是威脅）。
    /// </summary>
    public static float PlayerScale(int team, int selfTeam, int leaderMask, float leaderScale)
    {
        if (team is < 0 or > 2 || selfTeam is < 0 or > 2 || team == selfTeam)
            return 1f;
        return (leaderMask & (1 << team)) != 0 ? leaderScale : 1f;
    }

    /// <summary>中立＝現在誰都能拿、還沒有隊伍擁有的目標（塵封秘岩與周邊遺跡群的「中立」、昂薩哈凱爾的「可契約」）。</summary>
    public static bool IsNeutral(FrontlineObjectivePhase phase, int owner) =>
        phase == FrontlineObjectivePhase.Available && owner < 0;

    public static float ObjectiveScale(FrontlineObjectivePhase phase, int owner, float neutralScale) =>
        IsNeutral(phase, owner) ? neutralScale : 1f;

    /// <summary>把螢幕點夾進正方形（留 inset 給圖示本身）；回傳是否被夾。</summary>
    public static (Vector2 Point, bool Clamped) ClampToSquare(Vector2 p, Vector2 min, Vector2 max, float inset)
    {
        var lo = min + new Vector2(inset);
        var hi = max - new Vector2(inset);
        var c = Vector2.Clamp(p, lo, Vector2.Max(lo, hi));
        return (c, c != p);
    }
}

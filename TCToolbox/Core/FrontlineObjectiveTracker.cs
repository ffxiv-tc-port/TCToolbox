using System;
using System.Collections.Generic;
using System.Numerics;

namespace TCToolbox.Core;

// 本檔假設與契約：
// - 純受管理、不依賴 Dalamud：餵 (時間, DirectorUpdate 六欄) 進來，Snapshot(時間) 吐出每個目標的顯示狀態；離線探針直接連結本檔。
// - 各圖的欄位語意、座標、名稱對應全部來自 BMR 錄影觀察，不是官方資料；對不上只會顯示錯，不會擲例外。
// - 時間一律是呼叫端給的單調秒數；本類別不加鎖，呼叫端負責同一時間只有一條執行緒在用。

public enum FrontlineMap
{
    None = 0,
    SealRock,
    FieldsOfGlory,
    OnsalHakair,
    Secure,
}

public enum FrontlineObjectivePhase
{
    /// <summary>載入後還沒收到能判斷這個目標的事件。</summary>
    Unknown = 0,
    Upcoming,
    Available,
    Active,
    Owned,
    Ended,
    Idle,
}

/// <param name="Owner">隊伍 0..2（黑渦團／雙蛇黨／不滅隊）；-1＝沒有或不知道。</param>
/// <param name="Rank">0..2（B／A／S 級）；-1＝不適用或不知道。</param>
/// <param name="Fraction">進度條 0..1；null＝不畫條（不知道或不適用）。</param>
/// <param name="SecondsLeft">倒數；null＝沒有倒數或不知道。</param>
/// <param name="Estimated">倒數是推估（觀察常數或實測流失速度），不是資料裡給的。</param>
/// <param name="Confirmed">已確認（實色）；false＝預告或推估中的狀態（淡色）。</param>
public sealed record FrontlineObjectiveView(
    string Key,
    string Name,
    FrontlineObjectivePhase Phase,
    int Owner,
    int Rank,
    float? Fraction,
    double? SecondsLeft,
    bool Estimated,
    bool Confirmed,
    Vector2? Position,
    string Status,
    string Detail)
{
    /// <summary>該有進度卻不知道（列上要畫「?」）；與「這種目標本來就沒有進度條」區分。</summary>
    public bool ProgressUnknown =>
        Phase == FrontlineObjectivePhase.Unknown
        || (Key.StartsWith('S') && Fraction == null && Phase is FrontlineObjectivePhase.Active or FrontlineObjectivePhase.Available);
}

public sealed class FrontlineObjectiveTracker
{
    public const uint SealRockTerritory = 431;
    public const uint FieldsOfGloryTerritory = 554;
    public const uint OnsalHakairTerritory = 888;
    public const uint SecureTerritory = 1273;

    /// <summary>敖包占領後到失效的秒數（觀察常數，資料裡沒有）；量到實測值後改用實測。</summary>
    public const double DefaultOvooExpireSeconds = 30;

    /// <summary>失效／枯竭的列留在清單上的秒數。</summary>
    public const double EndedLingerSeconds = 15;

    private const double StoneStepSeconds = 3;

    private uint territory;
    private uint expectedDirector;
    private readonly Dictionary<uint, Stone> stones = [];
    private readonly Dictionary<int, Ice> ices = [];
    private readonly Dictionary<int, Ovoo> ovoos = [];
    private readonly Dictionary<uint, (int Team, bool Known)> securePoints = [];

    public FrontlineMap Map { get; private set; }

    public uint Territory => territory;

    /// <summary>本區收到、且 director 對得上的事件數。</summary>
    public int EventCount { get; private set; }

    /// <summary>本區收到、但 director 對不上而丟掉的事件數（改版換了 director id 時這裡會漲）。</summary>
    public int ForeignDirectorCount { get; private set; }

    public uint LastForeignDirector { get; private set; }

    /// <summary>看到開場那批事件（勝利分數、開場設定）＝從比賽一開始就在收。</summary>
    public bool SawMatchStart { get; private set; }

    public double? MeasuredOvooExpireSeconds { get; private set; }

    public static FrontlineMap MapOf(uint territoryType) => territoryType switch
    {
        SealRockTerritory => FrontlineMap.SealRock,
        FieldsOfGloryTerritory => FrontlineMap.FieldsOfGlory,
        OnsalHakairTerritory => FrontlineMap.OnsalHakair,
        SecureTerritory => FrontlineMap.Secure,
        _ => FrontlineMap.None,
    };

    // 錄影 27 場每圖固定一個值；用精確比對擋掉換區當幀還在派送的上一區事件。
    private static uint DirectorOf(FrontlineMap map) => map switch
    {
        FrontlineMap.SealRock => 0x8003C354,
        FrontlineMap.FieldsOfGlory => 0x8003C355,
        FrontlineMap.OnsalHakair => 0x8003C356,
        FrontlineMap.Secure => 0x8003C351,
        _ => 0,
    };

    public static string TeamName(int team) => team switch
    {
        0 => "黑渦團",
        1 => "雙蛇黨",
        2 => "不滅隊",
        _ => "?",
    };

    public static string RankName(int rank) => rank switch
    {
        0 => "B級",
        1 => "A級",
        2 => "S級",
        _ => "?級",
    };

    public void Reset(uint territoryType)
    {
        territory = territoryType;
        Map = MapOf(territoryType);
        expectedDirector = DirectorOf(Map);
        stones.Clear();
        ices.Clear();
        ovoos.Clear();
        securePoints.Clear();
        EventCount = 0;
        ForeignDirectorCount = 0;
        LastForeignDirector = 0;
        SawMatchStart = false;
        MeasuredOvooExpireSeconds = null;

        if (Map == FrontlineMap.FieldsOfGlory)
        {
            foreach (var info in Ices)
                ices[info.Index] = new Ice { Info = info };
        }
        else if (Map == FrontlineMap.Secure)
        {
            foreach (var (placeName, _) in SecurePoints)
                securePoints[placeName] = (-1, false);
        }
    }

    /// <returns>true＝這筆事件屬於本區目前的比賽並已套用。</returns>
    public bool Feed(double now, uint directorId, uint updateId, uint p1, uint p2, uint p3, uint p4)
    {
        if (Map == FrontlineMap.None)
            return false;
        if (directorId != expectedDirector)
        {
            ForeignDirectorCount++;
            LastForeignDirector = directorId;
            return false;
        }

        EventCount++;
        // 開場那幀固定會有這兩種（勝利分數 0x8、開場設定 0x3）；中途載入收不到。
        if (updateId is 0x3 or 0x8)
            SawMatchStart = true;

        switch (Map)
        {
            case FrontlineMap.SealRock: FeedSealRock(now, updateId, p1, p2, p3, p4); break;
            case FrontlineMap.FieldsOfGlory: FeedFieldsOfGlory(now, updateId, p1, p2, p3, p4); break;
            case FrontlineMap.OnsalHakair: FeedOnsalHakair(now, updateId, p1, p2, p3); break;
            case FrontlineMap.Secure: FeedSecure(updateId, p1, p2); break;
        }

        return true;
    }

    public List<FrontlineObjectiveView> Snapshot(double now)
    {
        var list = new List<FrontlineObjectiveView>();
        switch (Map)
        {
            case FrontlineMap.SealRock: SnapshotSealRock(now, list); break;
            case FrontlineMap.FieldsOfGlory: SnapshotFieldsOfGlory(now, list); break;
            case FrontlineMap.OnsalHakair: SnapshotOnsalHakair(now, list); break;
            case FrontlineMap.Secure: SnapshotSecure(list); break;
        }

        list.Sort(static (a, b) =>
        {
            var c = PhaseOrder(a.Phase).CompareTo(PhaseOrder(b.Phase));
            return c != 0 ? c : string.CompareOrdinal(a.Key, b.Key);
        });
        return list;
    }

    private static int PhaseOrder(FrontlineObjectivePhase p) => p switch
    {
        FrontlineObjectivePhase.Active => 0,
        FrontlineObjectivePhase.Owned => 1,
        FrontlineObjectivePhase.Available => 2,
        FrontlineObjectivePhase.Upcoming => 3,
        FrontlineObjectivePhase.Unknown => 4,
        FrontlineObjectivePhase.Ended => 5,
        _ => 6,
    };

    // ── 塵封秘岩：A＝出現 [layout, 等級, EObj, 點數池]、B＝[layout, 擁有者 0..2／3 中立, 已流失]、C＝[layout] 枯竭 ──

    private sealed class Stone
    {
        public StoneInfo? Info;
        public uint Layout;
        public int Rank = -1;
        public int Pool = -1;
        public int Owner = -1;
        public int Lost = -1;
        public double AnchorAt = -1;
        public int AnchorLost = -1;
        public double? SecondsPerPoint;
        public double? EndedAt;
    }

    private void FeedSealRock(double now, uint updateId, uint p1, uint p2, uint p3, uint p4)
    {
        switch (updateId)
        {
            case 0xA:
                stones[p1] = new Stone
                {
                    Info = StoneByEObj(p3) ?? StoneByLayout(p1),
                    Layout = p1,
                    Rank = (int)p2,
                    Pool = (int)p4,
                    Owner = 3,
                    Lost = 0,
                };
                break;
            case 0xB:
            {
                // 枯竭（C）之後同幀還會補一筆「中立、已流失 0」的 B；新石文一定先有 A，所以枯竭後的 B 一律不理。
                if (stones.TryGetValue(p1, out var s) && s.EndedAt != null)
                    break;
                if (s == null)
                {
                    s = new Stone { Info = StoneByLayout(p1), Layout = p1 };
                    stones[p1] = s;
                }

                var owner = (int)p2;
                var lost = (int)p3;
                if (owner != s.Owner || owner > 2 || s.AnchorAt < 0)
                {
                    s.AnchorAt = now;
                    s.AnchorLost = lost;
                }
                else if (lost > s.AnchorLost)
                {
                    var dt = now - s.AnchorAt;
                    var dp = lost - s.AnchorLost;
                    if (dt > 0.5)
                    {
                        var spp = dt / dp;
                        s.SecondsPerPoint = s.SecondsPerPoint is { } prev ? (prev + spp) / 2 : spp;
                        // 錄影：B／A／S 級每 3 秒各流失 2／3／4 點（池 80／120／160，約 120 秒枯竭）；中途載入靠步幅補等級。
                        if (s.Rank < 0 && dt is > 2.5 and < 3.5 && dp is >= 2 and <= 4)
                        {
                            s.Rank = dp - 2;
                            s.Pool = 40 * dp;
                        }
                    }

                    s.AnchorAt = now;
                    s.AnchorLost = lost;
                }

                s.Owner = owner;
                s.Lost = lost;
                break;
            }
            case 0xC:
                if (stones.TryGetValue(p1, out var ended))
                    ended.EndedAt = now;
                else
                    stones[p1] = new Stone { Info = StoneByLayout(p1), Layout = p1, EndedAt = now };
                break;
        }
    }

    private void SnapshotSealRock(double now, List<FrontlineObjectiveView> list)
    {
        foreach (var s in stones.Values)
        {
            var name = s.Info?.Name ?? $"石文 #{s.Layout:X}";
            var key = $"S{s.Info?.EObj ?? s.Layout:D8}";
            var pos = s.Info?.Pos;
            if (s.EndedAt is { } endedAt)
            {
                if (now - endedAt > EndedLingerSeconds)
                    continue;
                list.Add(new(key, name, FrontlineObjectivePhase.Ended, -1, s.Rank, 0f, null, false, true, pos, "已枯竭", "情報已枯竭，這塊石文消失了。"));
                continue;
            }

            float? fraction = null;
            int? remaining = null;
            if (s.Pool > 0 && s.Lost >= 0)
            {
                remaining = Math.Max(0, s.Pool - s.Lost);
                fraction = (float)remaining.Value / s.Pool;
            }

            var poolText = s.Pool > 0 ? $"{remaining}／{s.Pool} 點" : "點數池未知（比賽中途載入，等下一次流失）";
            if (s.Owner is >= 0 and <= 2)
            {
                double? left = null;
                var spp = s.SecondsPerPoint ?? (s.Rank >= 0 ? StoneStepSeconds / (s.Rank + 2) : (double?)null);
                if (remaining is { } r && spp is { } perPoint)
                    left = Math.Max(0, r * perPoint - Math.Max(0, now - s.AnchorAt));
                var rateText = s.SecondsPerPoint is { } m ? $"實測每點 {m:0.00} 秒" : "流失速度未實測，先用每 3 秒（B／A／S 級）2／3／4 點";
                list.Add(new(key, name, FrontlineObjectivePhase.Active, s.Owner, s.Rank, fraction, left, true, true, pos,
                    $"{TeamName(s.Owner)}占領中",
                    $"{RankName(s.Rank)}，剩 {poolText}；被占領期間持續流失。{rateText}。"));
            }
            else if (s.Owner == 3)
            {
                list.Add(new(key, name, FrontlineObjectivePhase.Available, -1, s.Rank, fraction, null, false, true, pos,
                    "中立",
                    $"{RankName(s.Rank)}，剩 {poolText}；中立時不流失。"));
            }
            else
            {
                list.Add(new(key, name, FrontlineObjectivePhase.Unknown, -1, s.Rank, fraction, null, false, false, pos,
                    "?", "載入後還沒收到擁有者。"));
            }
        }
    }

    // ── 榮譽野：B（開場）＝[編號, BNpcName, 大0／小1, layout]、C＝[編號, 狀態 0 待機／1 預告／2 已啟動／3 破壞, HP%, 倒數 ms] ──

    private sealed class Ice
    {
        public IceInfo? Info;
        public int State = -1;
        public int Hp = -1;
        public double? UpcomingFrom;
        public double? UpcomingEnd;
    }

    private void FeedFieldsOfGlory(double now, uint updateId, uint p1, uint p2, uint p3, uint p4)
    {
        if (updateId == 0xB)
        {
            var info = IceByNameId(p2) ?? new IceInfo((int)p1, p2, $"冰 #{p1}", p3 == 0, null);
            if (!ices.TryGetValue((int)p1, out var ice))
                ices[(int)p1] = ice = new Ice();
            ice.Info = info;
        }
        else if (updateId == 0xC)
        {
            if (!ices.TryGetValue((int)p1, out var ice))
                ices[(int)p1] = ice = new Ice { Info = IceByIndex((int)p1) };
            ice.State = (int)p2;
            ice.Hp = (int)p3;
            if (p2 == 1)
            {
                ice.UpcomingFrom = now;
                ice.UpcomingEnd = now + p4 / 1000.0;
            }
        }
    }

    private void SnapshotFieldsOfGlory(double now, List<FrontlineObjectiveView> list)
    {
        foreach (var (index, ice) in ices)
        {
            var name = ice.Info?.Name ?? $"冰 #{index}";
            var key = $"I{index:D2}";
            var pos = ice.Info?.Pos;
            var size = ice.Info is { } info ? (info.Big ? "大冰" : "小冰") : "冰";
            switch (ice.State)
            {
                case 0:
                    list.Add(new(key, name, FrontlineObjectivePhase.Idle, -1, -1, null, null, false, true, pos, "待機", $"{size}，目前沒有啟動。"));
                    break;
                case 1:
                {
                    var left = Math.Max(0, (ice.UpcomingEnd ?? now) - now);
                    var total = (ice.UpcomingEnd - ice.UpcomingFrom) ?? 0;
                    float? fraction = total > 0 ? (float)(left / total) : null;
                    list.Add(new(key, name, FrontlineObjectivePhase.Upcoming, -1, -1, fraction, left, false, false, pos,
                        "即將啟動", $"{size}，倒數結束後變脆弱、可以打。"));
                    break;
                }
                case 2:
                    list.Add(new(key, name, FrontlineObjectivePhase.Active, -1, -1, Math.Clamp(ice.Hp / 100f, 0f, 1f), null, false, true, pos,
                        $"已啟動 {ice.Hp}%", $"{size}，已變脆弱；條是剩餘 HP。"));
                    break;
                case 3:
                    list.Add(new(key, name, FrontlineObjectivePhase.Ended, -1, -1, 0f, null, false, true, pos, "已破壞", $"{size}，已被破壞。"));
                    break;
                default:
                    list.Add(new(key, name, FrontlineObjectivePhase.Unknown, -1, -1, null, null, false, false, pos,
                        "?", "載入後還沒收到這塊冰的狀態（比賽中途載入時會這樣）。"));
                    break;
            }
        }
    }

    // ── 昂薩哈凱爾：D＝[(序號<<24)|(等級<<16)|(狀態<<8), layout, 參數, PlaceName<<16]；狀態 2 預告（參數＝倒數 ms）／3 可契約／4 已占領（參數＝隊伍）／5 失效 ──

    private sealed class Ovoo
    {
        public OvooInfo? Info;
        public int Seq;
        public int Rank = -1;
        public int State = -1;
        public int Team = -1;
        public double? UpcomingFrom;
        public double? UpcomingEnd;
        public double? CapturedAt;
        public double? EndedAt;
    }

    private void FeedOnsalHakair(double now, uint updateId, uint p1, uint p2, uint p3)
    {
        if (updateId != 0xD)
            return;
        var seq = (int)(p1 >> 24);
        var rank = (int)((p1 >> 16) & 0xFF);
        var state = (int)((p1 >> 8) & 0xFF);
        if (!ovoos.TryGetValue(seq, out var o))
            ovoos[seq] = o = new Ovoo { Seq = seq, Info = OvooBySeq(seq) };
        o.Rank = rank;
        switch (state)
        {
            case 2:
                o.UpcomingFrom = now;
                o.UpcomingEnd = now + p3 / 1000.0;
                o.CapturedAt = null;
                o.EndedAt = null;
                o.Team = -1;
                break;
            case 4:
                o.Team = (int)p3;
                o.CapturedAt = now;
                break;
            case 5:
                if (o.State == 4 && o.CapturedAt is { } capturedAt)
                {
                    var measured = now - capturedAt;
                    if (measured is > 10 and < 120)
                        MeasuredOvooExpireSeconds = measured;
                }

                o.EndedAt = now;
                break;
        }

        o.State = state;
    }

    private void SnapshotOnsalHakair(double now, List<FrontlineObjectiveView> list)
    {
        if (!SawMatchStart)
        {
            foreach (var info in Ovoos)
            {
                if (ovoos.ContainsKey(info.Seq))
                    continue;
                list.Add(new($"O{info.Seq:D2}", info.Name, FrontlineObjectivePhase.Unknown, -1, -1, null, null, false, false, info.Pos,
                    "?", "比賽中途才開始收資料，載入後這個敖包還沒有動靜。"));
            }
        }

        foreach (var o in ovoos.Values)
        {
            var name = o.Info?.Name ?? $"敖包 {o.Seq:D2}";
            var key = $"O{o.Seq:D2}";
            var pos = o.Info?.Pos;
            switch (o.State)
            {
                case 2:
                {
                    var left = Math.Max(0, (o.UpcomingEnd ?? now) - now);
                    var total = (o.UpcomingEnd - o.UpcomingFrom) ?? 0;
                    float? fraction = total > 0 ? (float)(left / total) : null;
                    list.Add(new(key, name, FrontlineObjectivePhase.Upcoming, -1, o.Rank, fraction, left, false, false, pos,
                        "即將可契約", $"{RankName(o.Rank)}敖包，倒數結束後進入可契約狀態。"));
                    break;
                }
                case 3:
                    list.Add(new(key, name, FrontlineObjectivePhase.Available, -1, o.Rank, null, null, false, true, pos,
                        "可契約", $"{RankName(o.Rank)}敖包，現在可以契約。"));
                    break;
                case 4:
                {
                    var expire = MeasuredOvooExpireSeconds ?? DefaultOvooExpireSeconds;
                    var left = Math.Max(0, expire - (now - (o.CapturedAt ?? now)));
                    var basis = MeasuredOvooExpireSeconds is { } m ? $"本場實測占領後 {m:0.0} 秒失效" : $"占領後約 {DefaultOvooExpireSeconds:0} 秒失效（觀察值，本場還沒量到）";
                    list.Add(new(key, name, FrontlineObjectivePhase.Owned, o.Team, o.Rank, (float)(left / expire), left, true, true, pos,
                        $"{TeamName(o.Team)}已占領", $"{RankName(o.Rank)}敖包；{basis}。"));
                    break;
                }
                case 5:
                    if (o.EndedAt is { } endedAt && now - endedAt <= EndedLingerSeconds)
                        list.Add(new(key, name, FrontlineObjectivePhase.Ended, -1, o.Rank, 0f, null, false, true, pos, "已失效", "這個敖包已失效，之後會在別處出現新的。"));
                    break;
                default:
                    list.Add(new(key, name, FrontlineObjectivePhase.Unknown, -1, o.Rank, null, null, false, false, pos, "?", "收到無法解讀的狀態。"));
                    break;
            }
        }
    }

    // ── 周邊遺跡群：B＝[隊伍 1..3, PlaceName] 占領、C＝[PlaceName] 失去；其餘（A、E、無人機、截擊系統）未解 ──

    private void FeedSecure(uint updateId, uint p1, uint p2)
    {
        if (updateId == 0xB && p1 is >= 1 and <= 3)
            securePoints[p2] = ((int)p1 - 1, true);
        else if (updateId == 0xC)
            securePoints[p1] = (-1, true);
    }

    private void SnapshotSecure(List<FrontlineObjectiveView> list)
    {
        foreach (var (placeName, (team, known)) in securePoints)
        {
            var name = SecureName(placeName) ?? $"據點 #{placeName}";
            var key = $"P{placeName:D5}";
            const string more = "本圖只解出據點擁有者；無人機、截擊系統與占領進度尚未支援。";
            if (!known && !SawMatchStart)
                list.Add(new(key, name, FrontlineObjectivePhase.Unknown, -1, -1, null, null, false, false, null, "?", "比賽中途才開始收資料，載入後這個據點還沒換手。" + more));
            else if (team >= 0)
                list.Add(new(key, name, FrontlineObjectivePhase.Owned, team, -1, null, null, false, true, null, $"{TeamName(team)}占領", more));
            else
                list.Add(new(key, name, FrontlineObjectivePhase.Available, -1, -1, null, null, false, true, null, "中立", more));
        }
    }

    // ── 靜態資料：名稱取自台服 7.30 EObjName／BNpcName／PlaceName；座標取自錄影裡的實際物件位置（塵封秘岩另與 Level 表對過）──

    private sealed record StoneInfo(uint EObj, uint Layout, string Name, Vector2 Pos);

    private sealed record IceInfo(int Index, uint NameId, string Name, bool Big, Vector2? Pos);

    private sealed record OvooInfo(int Seq, uint PlaceName, string Name, Vector2 Pos);

    private static StoneInfo? StoneByEObj(uint eobj) => Array.Find(Stones, s => s.EObj == eobj);

    private static StoneInfo? StoneByLayout(uint layout) => Array.Find(Stones, s => s.Layout == layout);

    private static IceInfo? IceByNameId(uint nameId) => Array.Find(Ices, i => i.NameId == nameId);

    private static IceInfo? IceByIndex(int index) => Array.Find(Ices, i => i.Index == index);

    private static OvooInfo? OvooBySeq(int seq) => Array.Find(Ovoos, o => o.Seq == seq);

    private static string? SecureName(uint placeName)
    {
        foreach (var (id, name) in SecurePoints)
        {
            if (id == placeName)
                return name;
        }

        return null;
    }

    /// <summary>有座標的圖的範圍（世界 X／Z），給俯視小圖用；沒有座標的圖回 null。</summary>
    public (Vector2 Min, Vector2 Max)? MapBounds()
    {
        IEnumerable<Vector2> points = Map switch
        {
            FrontlineMap.SealRock => Array.ConvertAll(Stones, s => s.Pos),
            FrontlineMap.FieldsOfGlory => Array.ConvertAll(Ices, i => i.Pos ?? Vector2.Zero),
            FrontlineMap.OnsalHakair => Array.ConvertAll(Ovoos, o => o.Pos),
            _ => [],
        };
        var min = new Vector2(float.MaxValue);
        var max = new Vector2(float.MinValue);
        var any = false;
        foreach (var p in points)
        {
            min = Vector2.Min(min, p);
            max = Vector2.Max(max, p);
            any = true;
        }

        return any ? (min, max) : null;
    }

    private static readonly StoneInfo[] Stones =
    [
        new(2005338, 0x73BE72, "亞拉戈石文A1", new(18.1f, 267.6f)),
        new(2005339, 0x73BE76, "亞拉戈石文A2", new(8.1f, 167.8f)),
        new(2005340, 0x73BE7A, "亞拉戈石文A3", new(96.4f, 108.2f)),
        new(2005341, 0x73BE7E, "亞拉戈石文A4", new(200.3f, 68.2f)),
        new(2005342, 0x73BE82, "亞拉戈石文B1", new(157.7f, -49.1f)),
        new(2005343, 0x73BE86, "亞拉戈石文B3", new(51.3f, -82.7f)),
        new(2005344, 0x73BE8A, "亞拉戈石文B2", new(74.8f, -16.4f)),
        new(2005345, 0x73BE8E, "亞拉戈石文B4", new(-47.2f, -92.2f)),
        new(2005346, 0x73BE92, "亞拉戈石文C2", new(-163.9f, 1.8f)),
        new(2005347, 0x73BE96, "亞拉戈石文D2", new(-32.9f, 19.1f)),
        new(2005348, 0x73BE9A, "亞拉戈石文D3", new(-62.1f, 48.8f)),
        new(2005349, 0x73BE9E, "亞拉戈石文C4", new(-148.5f, 161.5f)),
        new(2005350, 0x73BEA2, "亞拉戈石文C3", new(-167.1f, 71.1f)),
        new(2005351, 0x73BEA6, "亞拉戈石文C1", new(-120.1f, -46.9f)),
        new(2005352, 0x73BEAA, "亞拉戈石文D1", new(15.8f, 101.6f)),
    ];

    private static readonly IceInfo[] Ices =
    [
        new(0, 4822, "冰封的石文A1", true, new Vector2(0.0f, 0.0f)),
        new(1, 4823, "冰封的石文A2", true, new Vector2(-142.9f, 84.7f)),
        new(2, 4824, "冰封的石文A3", true, new Vector2(136.9f, 80.0f)),
        new(3, 4825, "冰封的石文A4", true, new Vector2(3.1f, -156.7f)),
        new(4, 4826, "冰封的石文B1", false, new Vector2(-89.7f, 97.3f)),
        new(5, 4827, "冰封的石文B2", false, new Vector2(-39.4f, 88.8f)),
        new(6, 4828, "冰封的石文B3", false, new Vector2(-133.4f, 15.5f)),
        new(7, 4829, "冰封的石文B4", false, new Vector2(-91.2f, -19.3f)),
        new(8, 4830, "冰封的石文B5", false, new Vector2(-38.0f, -127.8f)),
        new(9, 4831, "冰封的石文B6", false, new Vector2(-60.2f, -72.5f)),
        new(10, 4832, "冰封的石文B7", false, new Vector2(38.4f, -124.0f)),
        new(11, 4833, "冰封的石文B8", false, new Vector2(64.8f, -71.3f)),
        new(12, 4834, "冰封的石文B9", false, new Vector2(129.9f, 25.8f)),
        new(13, 4835, "冰封的石文B10", false, new Vector2(93.3f, -21.0f)),
        new(14, 4836, "冰封的石文B11", false, new Vector2(86.0f, 106.3f)),
        new(15, 4837, "冰封的石文B12", false, new Vector2(28.1f, 89.2f)),
        new(16, 4840, "冰封的石文B13", false, new Vector2(40.5f, 24.4f)),
        new(17, 4841, "冰封的石文B14", false, new Vector2(-40.7f, 20.2f)),
        new(18, 4842, "冰封的石文B15", false, new Vector2(0.9f, -45.4f)),
    ];

    private static readonly OvooInfo[] Ovoos =
    [
        new(1, 3412, "無垢的大地01", new(0.0f, 0.0f)),
        new(2, 3413, "無垢的大地02", new(8.3f, -72.3f)),
        new(3, 3414, "無垢的大地03", new(101.7f, -34.4f)),
        new(4, 3415, "無垢的大地04", new(63.6f, 67.9f)),
        new(5, 3416, "無垢的大地05", new(-29.3f, 60.8f)),
        new(6, 3417, "無垢的大地06", new(-65.5f, 7.1f)),
        new(7, 3418, "無垢的大地07", new(-61.5f, -92.9f)),
        new(8, 3419, "無垢的大地08", new(75.4f, -108.5f)),
        new(9, 3420, "無垢的大地09", new(35.3f, 116.6f)),
        new(10, 3421, "無垢的大地10", new(-126.5f, -35.7f)),
        new(11, 3422, "無垢的大地11", new(-27.9f, -158.1f)),
        new(12, 3423, "無垢的大地12", new(142.4f, 38.2f)),
        new(13, 3424, "無垢的大地13", new(-94.0f, 88.0f)),
    ];

    private static readonly (uint PlaceName, string Name)[] SecurePoints =
    [
        (1369, "黑渦團前哨"),
        (1367, "雙蛇黨前哨"),
        (1368, "不滅隊前哨"),
        (2578, "亞拉戈市場遺跡北側"),
        (2579, "亞拉戈市場遺跡南側"),
        (2580, "亞拉戈太陽神殿東側"),
        (2581, "亞拉戈太陽神殿西側"),
        (2582, "亞拉戈莊園遺跡南側"),
        (2583, "亞拉戈莊園遺跡北側"),
    ];
}

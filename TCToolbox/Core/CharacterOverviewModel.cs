using System;
using System.Collections.Generic;

namespace TCToolbox.Core;

// 本檔只用 System 與兩份設定紀錄類別：判斷「下一件事」、分隊狀態、排序與時間文字，離線就能重播。
// 遊戲與 IPC 的讀取全部在 CharacterOverview 模組；這裡拿到的都是值複本。

/// <summary>分隊任務的狀態。零值＝不知道。</summary>
internal enum SquadronMissionState
{
    Unknown = 0,

    /// <summary>讀到時還沒完成分隊解鎖任務。</summary>
    NotUnlocked,

    /// <summary>沒有派遣中的任務。</summary>
    Idle,

    /// <summary>派遣中，<see cref="SquadronView.MissionEndUtc"/> 歸隊。</summary>
    Away,

    /// <summary>歸隊時間已過、任務報告還沒領。</summary>
    Returned,
}

/// <summary>分隊訓練的狀態。零值＝不知道。</summary>
internal enum SquadronTrainingState
{
    Unknown = 0,
    NotUnlocked,
    Idle,
    Training,

    /// <summary>結束時間已過、結果還沒看。</summary>
    Done,
}

/// <summary>一個角色的分隊狀態與它的來源。</summary>
internal readonly record struct SquadronView(
    SquadronMissionState Mission, DateTime? MissionEndUtc, SquadronTrainingState Training, DateTime? TrainingEndUtc,
    DateTime? ReadUtc, string Source)
{
    /// <summary>現在能不能開始訓練；<see langword="null"/>＝不知道。</summary>
    /// <remarks>
    /// 遊戲規則：派遣中不能訓練、訓練中不能再開（LogMessage 3924／3926）。
    /// 報告或結果還沒領的那一段，遊戲算不算「進行中」沒有驗過，所以回不知道。
    /// </remarks>
    public bool? CanTrain =>
        Mission is SquadronMissionState.NotUnlocked or SquadronMissionState.Away || Training == SquadronTrainingState.Training ? false
        : Mission == SquadronMissionState.Idle && Training == SquadronTrainingState.Idle ? true
        : null;
}

/// <summary>某一列「下一件事」：什麼事、何時（UTC；已過＝現在就能做）。</summary>
internal readonly record struct NextEvent(DateTime Utc, string What);

internal static class CharacterOverviewModel
{
    /// <summary>取分隊狀態：玩家狀態快照與「國防聯軍分隊」模組的派遣紀錄，取比較新的那一份。</summary>
    /// <remarks>派遣紀錄只看得到任務，訓練一律算不知道；沒看到開始訊息的紀錄不採用。</remarks>
    public static SquadronView Squadron(SquadronSnapshot? snap, SquadronReturnRecord? record, DateTime nowUtc)
    {
        if (snap != null && (record == null || snap.ReadUtc >= record.SeenUtc))
        {
            if (snap.Unlocked == false && snap.MissionId == 0 && snap.TrainingId == 0)
            {
                return new SquadronView(SquadronMissionState.NotUnlocked, null, SquadronTrainingState.NotUnlocked, null,
                    snap.ReadUtc, "玩家狀態");
            }

            var (mission, missionEnd) = snap.MissionId == 0 ? (SquadronMissionState.Idle, (DateTime?)null)
                : snap.MissionEnd == 0 ? (SquadronMissionState.Unknown, null)
                : FromUnix(snap.MissionEnd) > nowUtc ? (SquadronMissionState.Away, FromUnix(snap.MissionEnd))
                : (SquadronMissionState.Returned, FromUnix(snap.MissionEnd));

            var (training, trainingEnd) = snap.TrainingId == 0 ? (SquadronTrainingState.Idle, (DateTime?)null)
                : snap.TrainingEnd == 0 ? (SquadronTrainingState.Unknown, null)
                : FromUnix(snap.TrainingEnd) > nowUtc ? (SquadronTrainingState.Training, FromUnix(snap.TrainingEnd))
                : (SquadronTrainingState.Done, FromUnix(snap.TrainingEnd));

            return new SquadronView(mission, missionEnd, training, trainingEnd, snap.ReadUtc, "玩家狀態");
        }

        if (record is { Confirmed: true })
        {
            var away = record.ReturnUtc > nowUtc;
            return new SquadronView(away ? SquadronMissionState.Away : SquadronMissionState.Returned, record.ReturnUtc,
                SquadronTrainingState.Unknown, null, record.SeenUtc, "派遣確認框");
        }

        return default;
    }

    public static DateTime FromUnix(long seconds) => DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;

    /// <summary>派遣中的僱員數，與最早回來的時間（已過＝探險歸來）。</summary>
    public static (int OnVenture, DateTime? EarliestUtc) Ventures(IReadOnlyList<(bool HasVenture, long EndsAt)> retainers)
    {
        var count = 0;
        DateTime? earliest = null;
        foreach (var (hasVenture, endsAt) in retainers)
        {
            if (!hasVenture || endsAt <= 0) continue;

            count++;
            var t = FromUnix(endsAt);
            if (earliest == null || t < earliest) earliest = t;
        }

        return (count, earliest);
    }

    /// <summary>出航中的艘數、最早返航時間，與停在船塢、且勾選給 AutoRetainer 派遣的艘數。</summary>
    /// <param name="enabled"><see langword="null"/>＝不知道哪些有勾選：停靠的一律不算進「該出航」。</param>
    public static (int Voyaging, DateTime? EarliestUtc, int DockedEnabled) Vessels(
        IReadOnlyList<(string Name, uint ReturnTime)> vessels, IReadOnlySet<string>? enabled)
    {
        var voyaging = 0;
        var docked = 0;
        DateTime? earliest = null;
        foreach (var (name, returnTime) in vessels)
        {
            if (returnTime == 0)
            {
                if (enabled != null && enabled.Contains(name)) docked++;
                continue;
            }

            voyaging++;
            var t = FromUnix(returnTime);
            if (earliest == null || t < earliest) earliest = t;
        }

        return (voyaging, earliest, docked);
    }

    /// <summary>從幾件候選裡挑最早的；全部 <see langword="null"/> 就回 <see langword="null"/>。</summary>
    public static NextEvent? Earliest(IEnumerable<NextEvent?> candidates)
    {
        NextEvent? best = null;
        foreach (var c in candidates)
        {
            if (c is { } e && (best == null || e.Utc < best.Value.Utc)) best = e;
        }

        return best;
    }

    /// <summary>分隊這一格能貢獻的「下一件事」：歸隊、訓練結束（已過的也算，代表該去軍營了）。</summary>
    public static NextEvent? SquadronEvent(in SquadronView v)
    {
        NextEvent? mission = v.Mission is SquadronMissionState.Away or SquadronMissionState.Returned && v.MissionEndUtc is { } m
            ? new NextEvent(m, "分隊歸隊") : null;
        NextEvent? training = v.Training is SquadronTrainingState.Training or SquadronTrainingState.Done && v.TrainingEndUtc is { } t
            ? new NextEvent(t, "訓練結束") : null;
        return Earliest([mission, training]);
    }

    /// <summary>
    /// 排序：有下一件事的依時間先後（已過的最前面），其次是什麼都知道但沒事的，什麼都不知道的最後；
    /// 同名次照 AutoRetainer 清單順序。
    /// </summary>
    public static int Compare(NextEvent? a, bool aKnown, int aIndex, NextEvent? b, bool bKnown, int bIndex)
    {
        static int Rank(NextEvent? e, bool known) => e != null ? 0 : known ? 1 : 2;

        var r = Rank(a, aKnown).CompareTo(Rank(b, bKnown));
        if (r != 0) return r;

        if (a is { } ea && b is { } eb)
        {
            r = ea.Utc.CompareTo(eb.Utc);
            if (r != 0) return r;
        }

        return aIndex.CompareTo(bIndex);
    }

    /// <summary>「3天2小時」「2小時5分」「5分」；已到或已過回 <see langword="null"/>。不到一分鐘算一分鐘。</summary>
    public static string? Remaining(DateTime utc, DateTime nowUtc)
    {
        var seconds = (long)Math.Ceiling((utc - nowUtc).TotalSeconds);
        if (seconds <= 0) return null;

        var minutes = (seconds + 59) / 60;
        if (minutes >= 24 * 60) return $"{minutes / (24 * 60)}天{minutes % (24 * 60) / 60}小時";
        return minutes >= 60 ? $"{minutes / 60}小時{minutes % 60}分" : $"{minutes}分";
    }

    /// <summary>「剛才」「5 分鐘前」「3 小時前」「2 天前」。</summary>
    public static string Age(DateTime utc, DateTime nowUtc)
    {
        var age = nowUtc - utc;
        if (age.TotalMinutes < 1) return "剛才";
        if (age.TotalHours < 1) return $"{age.TotalMinutes:F0} 分鐘前";
        if (age.TotalHours < 48) return $"{age.TotalHours:F0} 小時前";
        return $"{age.TotalDays:F0} 天前";
    }
}

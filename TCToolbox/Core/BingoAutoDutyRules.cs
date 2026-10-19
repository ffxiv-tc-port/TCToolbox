using System;
using System.Collections.Generic;

namespace TCToolbox.Core;

/// <summary>
/// 天書建議面板「用 AutoDuty 跑這格」的判定。全部是純函式：不碰遊戲、不打 IPC，好離線驗。
/// </summary>
internal static class BingoAutoDutyRules
{
    /// <summary>AutoDuty 現在的狀態。零值是「問不到」：失敗方向是不給按鈕。</summary>
    public enum AutoDutyState
    {
        /// <summary>問不到（沒裝、沒載入，或版本太舊）。</summary>
        Unknown = 0,

        Stopped = 1,

        /// <summary>不是停止狀態（在跑或暫停中）。</summary>
        Running = 2,
    }

    /// <summary>AutoDuty 有沒有某個副本的路徑檔。零值是「問不到」。</summary>
    public enum PathState
    {
        Unknown = 0,

        Has = 1,

        Missing = 2,
    }

    /// <summary>「跑一輪」按鈕的判定。只有 <see cref="Show"/> 會畫出按鈕；零值是「不畫」。</summary>
    public enum RunButtonVerdict
    {
        OptionOff = 0,

        NotSingleDuty = 1,

        AlreadyDone = 2,

        NoDutySupport = 3,

        AutoDutyUnavailable = 4,

        PathUnknown = 5,

        NoPath = 6,

        AutoDutyRunning = 7,

        Show = 8,
    }

    /// <summary>
    /// DawnContent 的某一列算不算「有劇情輔助器」，與 AutoDuty 自己的 Support 判準逐項相同：
    /// 列號不能小於該資料片的起始列，而且能參加的隊員（DawnContentParticipable 的子列）不只一筆。
    /// </summary>
    /// <remarks>起始列照抄 AutoDuty <c>ContentHelper.DawnIndex</c>；不認得的資料片一律不算。</remarks>
    public static bool IsSupportRow(uint dawnRowId, uint exVersion, int participableSubrows)
    {
        long start = exVersion switch
        {
            0 => 200,
            1 => 215,
            2 => 224,
            3 => 1,
            4 => 12,
            5 => 24,
            _ => -1,
        };

        return start >= 0 && dawnRowId >= 1 && dawnRowId >= start && participableSubrows > 1;
    }

    /// <summary>某一格要不要畫「跑一輪」按鈕。</summary>
    /// <param name="optionOn">子選項開著。</param>
    /// <param name="isSingleDuty">這一格解析成單一副本（不是輪盤、不是對不到）。</param>
    /// <param name="alreadyDone">這一格已經完成（只差貼印花）。</param>
    /// <param name="hasDutySupport">那個副本有劇情輔助器。</param>
    /// <param name="autoDuty">AutoDuty 現在的狀態。</param>
    /// <param name="path">AutoDuty 有沒有那個副本的路徑檔。</param>
    public static RunButtonVerdict Evaluate(
        bool optionOn, bool isSingleDuty, bool alreadyDone, bool hasDutySupport, AutoDutyState autoDuty, PathState path)
    {
        if (!optionOn) return RunButtonVerdict.OptionOff;
        if (!isSingleDuty) return RunButtonVerdict.NotSingleDuty;
        if (alreadyDone) return RunButtonVerdict.AlreadyDone;
        if (!hasDutySupport) return RunButtonVerdict.NoDutySupport;
        if (autoDuty == AutoDutyState.Unknown) return RunButtonVerdict.AutoDutyUnavailable;
        if (path == PathState.Missing) return RunButtonVerdict.NoPath;
        if (path != PathState.Has) return RunButtonVerdict.PathUnknown;
        if (autoDuty != AutoDutyState.Stopped) return RunButtonVerdict.AutoDutyRunning;

        return RunButtonVerdict.Show;
    }

    /// <summary>
    /// 等級區間類格子（例：「51-59級迷宮」）要不要畫按鈕、跑哪一個副本。候選照任務搜尋器順序傳入，四個清單逐項對應。
    /// 判定的先後與 <see cref="Evaluate"/> 相同；不是 <see cref="RunButtonVerdict.Show"/> 時 <paramref name="pick"/> 為 <c>-1</c>。
    /// </summary>
    /// <remarks>
    /// 挑法：有劇情輔助器、AutoDuty 有路徑檔、目前職業等級進得去的候選裡，挑平均品級要求最低的；同分挑要求等級最低的，
    /// 再同就挑清單前面的。天書只看通關，品級要求低＝最不容易卡在劇情輔助器視窗。
    /// 一個都進不去時挑要求等級最低的（按鈕照樣畫，由呼叫端以「等級不夠」擋著）。
    /// </remarks>
    public static RunButtonVerdict EvaluateBand(
        bool optionOn,
        bool alreadyDone,
        AutoDutyState autoDuty,
        IReadOnlyList<bool> hasSupport,
        IReadOnlyList<PathState> paths,
        IReadOnlyList<int> levels,
        IReadOnlyList<int> itemLevels,
        int playerLevel,
        out int pick)
    {
        pick = -1;
        var count = hasSupport.Count;
        if (paths.Count != count || levels.Count != count || itemLevels.Count != count)
            throw new ArgumentException("候選清單的長度不一致。");

        if (!optionOn) return RunButtonVerdict.OptionOff;
        if (count == 0) return RunButtonVerdict.NotSingleDuty;
        if (alreadyDone) return RunButtonVerdict.AlreadyDone;

        bool anySupport = false, anyPath = false, anyUnknown = false;
        for (var i = 0; i < count; i++)
        {
            if (!hasSupport[i]) continue;
            anySupport = true;
            anyPath |= paths[i] == PathState.Has;
            anyUnknown |= paths[i] == PathState.Unknown;
        }

        if (!anySupport) return RunButtonVerdict.NoDutySupport;
        if (autoDuty == AutoDutyState.Unknown) return RunButtonVerdict.AutoDutyUnavailable;
        if (!anyPath) return anyUnknown ? RunButtonVerdict.PathUnknown : RunButtonVerdict.NoPath;
        if (autoDuty != AutoDutyState.Stopped) return RunButtonVerdict.AutoDutyRunning;

        var bestFits = false;
        for (var i = 0; i < count; i++)
        {
            if (!hasSupport[i] || paths[i] != PathState.Has) continue;

            var fits = levels[i] <= playerLevel;
            if (pick >= 0 && !IsBetterBandPick(fits, levels[i], itemLevels[i], bestFits, levels[pick], itemLevels[pick]))
                continue;

            pick = i;
            bestFits = fits;
        }

        return RunButtonVerdict.Show;
    }

    /// <summary>候選 A 有沒有比目前挑中的 B 好。完全同分回 false，所以先出現的留著。</summary>
    private static bool IsBetterBandPick(bool fitsA, int levelA, int itemLevelA, bool fitsB, int levelB, int itemLevelB)
    {
        if (fitsA != fitsB) return fitsA;
        if (!fitsA) return levelA != levelB ? levelA < levelB : itemLevelA < itemLevelB;
        return itemLevelA != itemLevelB ? itemLevelA < itemLevelB : levelA < levelB;
    }

    /// <summary>等級區間類格子不給按鈕的原因；和單一副本說法不同的幾種另外寫，其餘同 <see cref="Describe"/>。</summary>
    public static string DescribeBand(RunButtonVerdict verdict, int candidates) => verdict switch
    {
        RunButtonVerdict.NotSingleDuty => "這一格在台服找不到符合的副本，不給按鈕。",
        RunButtonVerdict.NoDutySupport =>
            $"符合這一格的 {candidates} 個副本都沒有劇情輔助器。一般模式會帶你去任務搜尋器和路人排隊，所以不給按鈕。",
        RunButtonVerdict.PathUnknown => "問不到 AutoDuty 有沒有這一格副本的路徑檔。",
        RunButtonVerdict.NoPath => "符合這一格又有劇情輔助器的副本，AutoDuty 都沒有路徑檔，跑不了。",
        _ => Describe(verdict),
    };

    /// <summary>不給按鈕的原因（給 tooltip 與記錄）。<see cref="RunButtonVerdict.Show"/> 回空字串。</summary>
    public static string Describe(RunButtonVerdict verdict) => verdict switch
    {
        RunButtonVerdict.OptionOff => "「用 AutoDuty 跑這格」沒有打開。",
        RunButtonVerdict.NotSingleDuty => "這一格不是單一副本（輪盤或對不到的項目），不給按鈕。",
        RunButtonVerdict.AlreadyDone => "這一格已經完成了，貼上印花就好，不用再跑。",
        RunButtonVerdict.NoDutySupport =>
            "這個副本沒有劇情輔助器。一般模式會帶你去任務搜尋器和路人排隊，所以不給按鈕。",
        RunButtonVerdict.AutoDutyUnavailable => "問不到 AutoDuty（沒安裝、沒載入，或版本太舊）。",
        RunButtonVerdict.PathUnknown => "問不到 AutoDuty 有沒有這個副本的路徑檔。",
        RunButtonVerdict.NoPath => "AutoDuty 沒有這個副本的路徑檔，跑不了。",
        RunButtonVerdict.AutoDutyRunning => "AutoDuty 正在跑（或暫停中），停下來之後才會出現按鈕。",
        _ => string.Empty,
    };
}

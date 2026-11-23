using System;
using System.Collections.Generic;

namespace TCToolbox.Core;

// 本檔假設與契約：「本角色一鍵」（傳送→進軍營→派遣→綜合訓練）的純邏輯，不碰 Dalamud 與原生指標（離線探針直接編譯本檔）。
// 失敗方向一律是「停手」：讀不到、比不中、多一扇窗，都交還給使用者，不猜。
// 綜合訓練的按法 (12, 6)：訓練窗第 i 顆訓練鈕（i＝0..6）送 (12, i)、鈕上文字是 AtkValues[2+i]，
// 遊戲收到後把 i+1 當成 GcArmyTraining 列號開確認框（Addon 10579「開始<列名>。」）；證據在 commit body。

/// <summary>一個國防聯軍的軍營：傳送面板目的地、城內入口、軍營本身。</summary>
/// <param name="GrandCompany">GrandCompany 表列號（1 黑渦團、2 雙蛇黨、3 不滅隊）。</param>
/// <param name="FavoriteId">Lifestream 傳送面板的目的地 id（城內乙太之晶或乙太之光的 Aetheryte 列號），sub 一律 0。</param>
/// <param name="CityTerritory">入口所在的城市區域。</param>
/// <param name="DoorObject">「進入○○軍營」的 EObj（EObjName 表）。</param>
/// <param name="Barracks">軍營區域。</param>
internal readonly record struct SquadronHome(uint GrandCompany, uint FavoriteId, uint CityTerritory, uint DoorObject, uint Barracks);

internal enum SquadronTravelKind
{
    Wait = 0,
    Arrived = 1,
    Stop = 2,
}

internal readonly record struct SquadronTravelDecision(SquadronTravelKind Kind, string Reason);

/// <summary>傳送等待中一幀的觀察。</summary>
/// <param name="LifestreamBusy"><c>null</c>＝問不到。</param>
/// <param name="Zoning">讀取地圖中、或還沒有角色。</param>
internal readonly record struct SquadronTravelFrame(
    bool? LifestreamBusy, bool Zoning, uint Territory, bool SeenBusy, long MsSinceSend);

internal enum SquadronTrainingMessage
{
    Other = 0,
    Started = 1,
    Failed = 2,
}

internal static class SquadronOneKey
{
    /// <summary>台服 7.30 EXD 實查：EObjName「進入○○軍營」、TerritoryType 534～536 的 Aetheryte 欄、傳送面板的城內乙太之晶 41。</summary>
    public static readonly SquadronHome[] Homes =
    [
        new(1, 41, 128, 2007527, 536),
        new(2, 2, 132, 2006962, 534),
        new(3, 9, 130, 2007529, 535),
    ];

    /// <summary>送出傳送後，Lifestream 至少要在這段時間內轉成忙碌；一直不忙又不在目的地就停。</summary>
    public const long BusyGraceMs = 5_000;

    /// <summary>訓練窗的格數（v2-0 實機 25 次全部是 80）。</summary>
    public const int TrainingCells = 80;

    /// <summary>訓練鈕文字在 AtkValues 的起點；七顆鈕依序是 GcArmyTraining 第 1～7 列。</summary>
    public const int FirstLabelCell = 2;

    public const int ButtonCount = 7;

    /// <summary>綜合訓練＝第 7 列＝第 6 顆鈕（從 0 數）。</summary>
    public const int ComprehensiveButton = 6;

    public const uint ComprehensiveRow = ComprehensiveButton + 1;

    public static bool TryGetHome(uint grandCompany, out SquadronHome home)
    {
        foreach (var h in Homes)
        {
            if (h.GrandCompany != grandCompany) continue;
            home = h;
            return true;
        }

        home = default;
        return false;
    }

    /// <summary>Lifestream <c>*V2</c> 傳送端點回傳值（IpcQueueResult）的中文說明；0 與 1 不是錯誤。</summary>
    public static string DescribeQueueResult(int result, string destination) => result switch
    {
        0 => "已排入",
        1 => "已經在目的地",
        2 => "Lifestream 正在忙",
        3 => "角色目前不能動（讀取中、過場或正在互動）",
        4 => $"Lifestream 傳送面板的我的最愛裡沒有「{destination}」，或這隻角色還沒共鳴；請在傳送面板把它加入我的最愛",
        5 => "Lifestream 檢查都過了但沒有出發（原因在 Lifestream 的記錄）",
        6 => "Lifestream 這一刻沒辦法處理（讀取畫面或卸載中）",
        7 => "Lifestream 排程時發生錯誤",
        _ => $"Lifestream 回了看不懂的結果 {result}",
    };

    /// <summary>傳送送出之後每幀的判斷：Lifestream 跑完（不忙）而且人在城裡才算到。</summary>
    public static SquadronTravelDecision DecideTravel(in SquadronTravelFrame f, uint cityTerritory, string cityName)
    {
        if (f.LifestreamBusy == true || f.Zoning) return new(SquadronTravelKind.Wait, string.Empty);
        if (f.LifestreamBusy == null) return new(SquadronTravelKind.Stop, "問不到 Lifestream 是不是還在忙");
        if (!f.SeenBusy && f.MsSinceSend < BusyGraceMs) return new(SquadronTravelKind.Wait, string.Empty);
        if (f.Territory == cityTerritory) return new(SquadronTravelKind.Arrived, string.Empty);

        return new(SquadronTravelKind.Stop,
            f.SeenBusy
                ? $"Lifestream 已經停下，但人不在{cityName}（目前區域 {f.Territory}）"
                : $"送出傳送 {BusyGraceMs / 1000} 秒後 Lifestream 都沒有動，人也不在{cityName}");
    }

    /// <summary>
    /// 訓練窗可以按綜合訓練嗎：格數、[0] 模式＝0（選訓練）、七顆鈕的文字依序等於表的第 1～7 列。
    /// </summary>
    /// <param name="labels">AtkValues[2..8] 的文字（同一幀讀的）；讀不到的那格是 <c>null</c>。</param>
    /// <param name="sheetNames">GcArmyTraining 第 1～7 列的名稱。</param>
    /// <param name="mode">讀到的 [0]；讀不到是 -1。</param>
    public static bool CheckTrainingList(
        ReadOnlySpan<SquadronCell> cells, IReadOnlyList<string?> labels, IReadOnlyList<string> sheetNames,
        out int mode, out string problem)
    {
        mode = -1;
        if (cells.Length != TrainingCells)
        {
            problem = $"訓練窗格數 {cells.Length}，不是 {TrainingCells}";
            return false;
        }

        if (cells[0].Type != SquadronDiag.TypeInt || !cells[0].HasValue)
        {
            problem = "訓練窗 [0] 不是整數";
            return false;
        }

        mode = (int)cells[0].Value;
        switch (mode)
        {
            case 0:
                break;
            case 1:
                problem = "訓練正在進行中，現在不能開始新的訓練";
                return false;
            case 2:
                problem = "上一次訓練已完成、結果還沒確認，要先在訓練視窗按確認";
                return false;
            default:
                problem = $"訓練窗 [0] 模式是 {mode}，看不懂";
                return false;
        }

        if (sheetNames.Count != ButtonCount || labels.Count != ButtonCount)
        {
            problem = $"訓練名稱數量不對（表 {sheetNames.Count}、窗 {labels.Count}，應為 {ButtonCount}）";
            return false;
        }

        for (var i = 0; i < ButtonCount; i++)
        {
            var want = SquadronFlow.NormalizeEntry(sheetNames[i]);
            var got = SquadronFlow.NormalizeEntry(labels[i]);
            if (want.Length > 0 && got == want) continue;

            problem = $"訓練窗第 {i + 1} 顆鈕是「{labels[i] ?? "（讀不到）"}」，不是表上的「{sheetNames[i]}」";
            return false;
        }

        problem = string.Empty;
        return true;
    }

    /// <summary>
    /// 訓練確認框整句等於「開始{訓練名}。」（Addon 10579 的兩段固定片段夾訓練名；去掉所有空白後比）。
    /// 綜合訓練三項不變，遊戲不會改用附上限警告的 10580，所以那一版一律不按。
    /// </summary>
    public static bool TrainingConfirmMatches(string? text, IReadOnlyList<string> fragments, string trainingName, out string problem)
    {
        if (fragments.Count != 2)
        {
            problem = $"確認框樣板有 {fragments.Count} 段，不是 2 段";
            return false;
        }

        var name = SquadronFlow.StripWhitespace(trainingName);
        if (name.Length == 0)
        {
            problem = "沒有訓練名";
            return false;
        }

        var got = SquadronFlow.StripWhitespace(text);
        var want = fragments[0] + name + fragments[1];
        if (got == want)
        {
            problem = string.Empty;
            return true;
        }

        problem = $"確認框不是「{want}」";
        return false;
    }

    /// <summary>按了「是」之後的系統訊息：開始了（LogMessage 3928）、或不能訓練的那幾則。</summary>
    public static SquadronTrainingMessage ClassifyTrainingMessage(
        string? message, IReadOnlyList<string> startedFragments, string trainingName, IReadOnlyList<string> failures)
    {
        var text = SquadronFlow.StripWhitespace(message);
        if (text.Length == 0) return SquadronTrainingMessage.Other;

        if (startedFragments.Count == 2 &&
            text == startedFragments[0] + SquadronFlow.StripWhitespace(trainingName) + startedFragments[1])
            return SquadronTrainingMessage.Started;

        foreach (var failure in failures)
        {
            if (failure.Length > 0 && text == SquadronFlow.StripWhitespace(failure)) return SquadronTrainingMessage.Failed;
        }

        return SquadronTrainingMessage.Other;
    }
}

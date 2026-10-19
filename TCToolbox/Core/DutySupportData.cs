using System.Collections.Generic;
using Lumina.Excel.Sheets;

namespace TCToolbox.Core;

/// <summary>副本的劇情輔助器相關資料，全部從遊戲資料表即時查，不寫死。</summary>
/// <remarks>只在框架執行緒上呼叫（快取沒有鎖）。</remarks>
internal static class DutySupportData
{
    /// <summary>ContentFinderCondition RowId → 有沒有劇情輔助器。資料表在執行期不會變，建一次就好。</summary>
    private static Dictionary<uint, bool>? supportByCfc;

    /// <summary>這個副本能不能用劇情輔助器進入（判準與 AutoDuty 自己的相同）。</summary>
    public static bool HasSupportMode(uint cfcId)
    {
        supportByCfc ??= BuildSupportMap();
        return supportByCfc.TryGetValue(cfcId, out var has) && has;
    }

    /// <summary>副本所在的區域（TerritoryType RowId），AutoDuty 的端點都以它為鍵；<c>0</c>＝查不到。</summary>
    public static uint GetTerritoryType(uint cfcId) =>
        Svc.Data.GetExcelSheet<ContentFinderCondition>().GetRowOrDefault(cfcId)?.TerritoryType.RowId ?? 0;

    /// <summary>副本要求的職業等級；<c>0</c>＝查不到。</summary>
    public static int GetRequiredLevel(uint cfcId) =>
        Svc.Data.GetExcelSheet<ContentFinderCondition>().GetRowOrDefault(cfcId)?.ClassJobLevelRequired ?? 0;

    /// <summary>副本要求的平均品級；<c>0</c>＝沒有要求或查不到。</summary>
    public static int GetItemLevelRequired(uint cfcId) =>
        Svc.Data.GetExcelSheet<ContentFinderCondition>().GetRowOrDefault(cfcId)?.ItemLevelRequired ?? 0;

    /// <summary>副本名稱；查不到回空字串。</summary>
    public static string GetDutyName(uint cfcId) =>
        Svc.Data.GetExcelSheet<ContentFinderCondition>().GetRowOrDefault(cfcId)?.Name.ExtractText() ?? string.Empty;

    private static Dictionary<uint, bool> BuildSupportMap()
    {
        var map = new Dictionary<uint, bool>();
        var duties = Svc.Data.GetExcelSheet<ContentFinderCondition>();
        var participants = Svc.Data.GetSubrowExcelSheet<DawnContentParticipable>();

        // AutoDuty 對每個副本只看「第一筆」指到它的 DawnContent，這裡照同樣的列序、同樣只看第一筆。
        foreach (var dawn in Svc.Data.GetExcelSheet<DawnContent>())
        {
            var cfcId = dawn.Content.RowId;
            if (cfcId == 0 || map.ContainsKey(cfcId)) continue;

            var exVersion = duties.GetRowOrDefault(cfcId)?.TerritoryType.ValueNullable?.ExVersion.RowId ?? uint.MaxValue;
            participants.TryGetSubrowCount(dawn.RowId, out var subrows);
            map[cfcId] = BingoAutoDutyRules.IsSupportRow(dawn.RowId, exVersion, subrows);
        }

        return map;
    }
}

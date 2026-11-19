using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace TCToolbox.Core;

// 本檔假設與契約：只依賴 Lumina（離線探針用同一份 Lumina 讀台服 sqpack，逐欄對原始列驗過欄位對應）。

internal static class SquadronTableLoader
{
    public static SquadronTables Build(ExcelSheet<GcArmyExpedition> expeditions, ExcelSheet<GcArmyMemberGrow> grows)
    {
        var t = new SquadronTables();

        foreach (var row in expeditions)
        {
            if (row.RequiredLevel == 0) continue;

            var variants = new SquadronVariant[row.ExpeditionParams.Count];
            for (var i = 0; i < variants.Length; i++)
            {
                var p = row.ExpeditionParams[i];
                variants[i] = new SquadronVariant(
                    p.RequiredPhysical, p.RequiredMental, p.RequiredTactical,
                    p.PercentPhysicalMet, p.PercentMentalMet, p.PercentTacticalMet, p.PercentAllMet,
                    p.RewardItem.RowId, p.RewardQuantity);
            }

            t.Missions[row.RowId] = new SquadronMission(
                row.RowId, row.Name.ExtractText(), row.RequiredLevel, (int)row.GcArmyExpeditionType.RowId,
                row.PercentBase, row.RewardExperience, row.RequiredSeals, variants);
        }

        foreach (var row in grows)
        {
            var classJob = row.ClassJob.RowId;
            if (classJob == 0) continue;

            // 原始列有 61 組（0～60 級），Lumina 只認得 MemberParams[0..59]，第 61 組落在 Unknown1..3。
            var n = row.MemberParams.Count;
            var levels = new (int P, int M, int T)[n + 1];
            for (var i = 0; i < n; i++)
            {
                var mp = row.MemberParams[i];
                levels[i] = (mp.Physical, mp.Mental, mp.Tactical);
            }

            levels[n] = (row.Unknown1, row.Unknown2, row.Unknown3);
            t.Grow[classJob] = levels;
        }

        return t;
    }
}

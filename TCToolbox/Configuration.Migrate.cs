using System;
using System.Collections.Generic;
using TCToolbox.Core;

namespace TCToolbox;

public sealed partial class Configuration
{
    /// <summary>
    /// 一次性的設定結構遷移。<b>載入之後、模組建立之前</b>呼叫。
    /// </summary>
    /// <remarks>
    /// 🔴 改欄位名時舊鍵會變成孤兒被靜默丟掉、新鍵吃預設值；<b>改欄位語意卻沿用舊欄位名</b>更糟——舊值會被原封不動當成新語意用。
    /// 🔑 欄位語意改變時要<b>同時改欄位名</b>（例如 <c>EdgeOffsetX</c>／<c>CenterOffsetY</c>），讓舊鍵自然孤兒化去吃新預設。
    /// </remarks>
    public void Migrate()
    {
        if (Version >= CurrentVersion) return;

        var from = Version;
        var migrated = MigrateEnemyListToV2();

        // 播種只屬於 v3 → v4：之後的升級再播一次，會把使用者自己關掉的模組重新打開。
        var seeded = from < 4 ? SeedDefaultOnModules(DefaultOnModules) : [];
        if (from < 6) seeded.AddRange(SeedDefaultOnModules(DefaultOnModulesV6));
        if (from < 7) seeded.AddRange(SeedDefaultOnModules(DefaultOnModulesV7));
        var speed = from < 5
            ? MigrateSpeedWhitelistToTerritories(DutySupportData.GetTerritoryType)
            : [];

        Version = CurrentVersion;
        Save();

        // 使用者回報用：設定被動過就一定要留下痕跡。
        Svc.Log.Information(
            $"[TCToolbox] 設定結構升級 v{from} → v{CurrentVersion}"
            + (migrated ? "；已把 OptimizedEnemyList 的舊 ShowCast／偏移值遷移到新 schema" : string.Empty)
            + (seeded.Count > 0 ? $"；預設啟用了 {string.Join("、", seeded)}" : string.Empty)
            + (speed.Count > 0 ? $"；移動速度倍率的副本白名單 {speed.Count} 筆換成地圖" : string.Empty)
            + (!migrated && seeded.Count == 0 && speed.Count == 0 ? "（沒有需要遷移的舊值）" : string.Empty));

        foreach (var (duty, territory) in speed)
        {
            var dutyText = $"{DutySupportData.GetDutyName(duty)}（{duty}）";
            Svc.Log.Information(territory == 0
                ? $"[TCToolbox] 移動速度倍率：舊白名單的副本 {dutyText} 找不到對應的地圖，沒有換過來（舊清單仍保留）。"
                : $"[TCToolbox] 移動速度倍率：舊白名單的副本 {dutyText} → 地圖 {PlaceNameOf(territory)}（{territory}）。");
        }
    }

    private static string PlaceNameOf(uint territory) =>
        Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>().GetRowOrDefault(territory)?
            .PlaceName.ValueNullable?.Name.ExtractText() ?? string.Empty;

    /// <summary>
    /// 移動速度倍率 v4 → v5：舊的副本白名單換成地圖 id 併進 <see cref="MovementSpeedMultiplierConfig.TerritoryWhitelist"/>。
    /// </summary>
    /// <remarks>
    /// 契約：只加不刪，<see cref="MovementSpeedMultiplierConfig.DutyWhitelist"/> 原樣保留備查。
    /// 查表函式由呼叫端給（遊戲裡是 Lumina 的 ContentFinderCondition.TerritoryType），離線可以換成假資料測。
    /// </remarks>
    internal List<(uint Duty, uint Territory)> MigrateSpeedWhitelistToTerritories(Func<uint, uint> dutyToTerritory) =>
        Modules.SpeedWhitelist.ConvertLegacyDuties(
            MovementSpeedMultiplier.DutyWhitelist, dutyToTerritory, MovementSpeedMultiplier.TerritoryWhitelist);

    /// <summary>v4 起「預設開著」的模組。🔴 只准放<b>純顯示</b>、不碰遊戲狀態的模組。</summary>
    private static readonly string[] DefaultOnModules =
    [
        Modules.PluginUpdateNotice.ModuleInternalName,
        Modules.WeeklyBingoAdvisor.ModuleInternalName,
    ];

    /// <summary>v6 起預設開著的模組（同一條規則：只准純顯示／只寫 log）。</summary>
    private static readonly string[] DefaultOnModulesV6 =
    [
        Modules.AppraisalDiagnostics.ModuleInternalName,
    ];

    /// <summary>v7 起預設開著的模組。不碰遊戲狀態，只讀插件設定檔、把備份寫到插件設定資料夾以外。</summary>
    private static readonly string[] DefaultOnModulesV7 =
    [
        Modules.PluginConfigBackup.ModuleInternalName,
    ];

    /// <summary>把 <paramref name="names"/> 寫進 <see cref="EnabledModules"/>，回傳真的加進去的名字。</summary>
    /// <remarks>
    /// 契約：<b>只加不刪</b>，只在該鍵不在清單裡時加，清單裡其他東西一律不碰。
    /// ⚠️ 停用＝直接從清單移除（沒有 DisabledModules 也沒有旗標），所以「從沒開過」與
    /// 「開過又自己關掉」分不開；使用者已裁決按「缺鍵就加」處理。
    /// </remarks>
    private List<string> SeedDefaultOnModules(string[] names)
    {
        List<string> added = [];
        foreach (var name in names)
        {
            if (EnabledModules.Add(name)) added.Add(name);
        }

        return added;
    }

    /// <summary>
    /// OptimizedEnemyList 的 v1 → v2 遷移。有動到東西回 <see langword="true"/>。
    /// </summary>
    /// <remarks>
    /// <b>判準是孤兒鍵 <c>ShowCast</c> 存在</b>——那是「這份設定檔是由舊 schema 寫出來的」
    /// 唯一的正面證據（Dalamud 存設定時會把所有公開成員都寫出去，所以跑過舊版並存過設定的人
    /// 一定有這個鍵）。沒有它就什麼都不做，全新安裝與早已是新 schema 的人都不受影響。
    /// </remarks>
    private bool MigrateEnemyListToV2()
    {
        if (EnemyList.ShowCast is not { } legacyShowCast) return false;

        // ① 舊 ShowCast(bool) → 新 CastDisplay(enum)。
        //    關掉過的人升級後詠唱顯示會被靜默重新打開（舊鍵孤兒、新鍵吃預設 Auto），這條就是修那個。
        EnemyList.CastDisplay = legacyShowCast ? CastDisplayMode.Auto : CastDisplayMode.Off;

        // ② 偏移值：語意變了，自訂過的值換算不回來，所以<b>只</b>處理「剛好等於舊預設」的組合
        //    ——那一族（絕大多數人）直接給新預設；自訂過的視為刻意版面，一律不動。
        //    舊預設在新語意下會把疊圖畫在垂直置中再往下 20px（貼列底／出列），與全新安裝差 20px。
        if (EnemyList.OffsetX == LegacyEnemyListOffsetX && EnemyList.OffsetY == LegacyEnemyListOffsetY)
        {
            EnemyList.OffsetX = OptimizedEnemyListConfig.DefaultOffsetX;
            EnemyList.OffsetY = OptimizedEnemyListConfig.DefaultOffsetY;
        }

        // 孤兒鍵用完就丟，不要再寫回設定檔。
        EnemyList.ShowCast = null;
        return true;
    }

    /// <summary>v1 時代 OptimizedEnemyList 的 <c>OffsetX</c> 預設值。</summary>
    private const float LegacyEnemyListOffsetX = 4f;

    /// <summary>v1 時代 OptimizedEnemyList 的 <c>OffsetY</c> 預設值。</summary>
    private const float LegacyEnemyListOffsetY = 20f;
}

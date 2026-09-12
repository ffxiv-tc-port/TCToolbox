using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Lumina.Excel.Sheets;

namespace TCToolbox.Core;

/// <summary>
/// 「這個在場物件是不是寶箱」的共用判準。
/// </summary>
/// <remarks>
/// ⚠️ 第二軸建不起來時（那一列不見了／名字是空的）會退回<b>只用第一軸</b>，並把原因留在
/// <see cref="DegradedReason"/> 讓呼叫端顯示出來——「只找得到銅寶箱」與「都找得到」是兩件事，
/// 不能靜默。
/// </remarks>
public static class ChestIdentity
{
    /// <summary>
    /// 拿來取「寶箱」這個字的 <c>EObjName</c> 列號：深宮的銀寶箱。
    /// </summary>
    /// <remarks>
    /// 📌 為什麼是一個寫死的<b>列號</b>而不是寫死的字串：列號在各語系客戶端是同一個，
    /// 讀出來的字自然是該語系的用字；寫死字串則是「換個語系就靜默零命中」。
    /// </remarks>
    public const uint ReferenceChestEObjNameRow = 2007357;

    private static readonly HashSet<uint> EventObjIdSet = [];

    private static bool built;

    /// <summary>被判定成「寶箱」的 <c>EventObj</c> <c>BaseId</c> 集合。</summary>
    public static IReadOnlyCollection<uint> EventObjIds => EventObjIdSet;

    /// <summary>第二軸建不起來時的原因；空字串＝一切正常。</summary>
    public static string DegradedReason { get; private set; } = string.Empty;

    /// <summary>
    /// 確保對照表已經建好。已經建好而且沒有退化時是零成本的。
    /// </summary>
    /// <remarks>
    /// 📌 <b>退化的時候刻意不設「已建好」旗標</b>，讓下一次啟用再試一次——資料表在外掛剛載入的
    /// 那一瞬間偶爾還沒就緒，而永久記住一個失敗的結果會讓使用者只能重開遊戲。
    /// </remarks>
    public static void EnsureBuilt()
    {
        if (built) return;

        Build();
    }

    /// <summary>這個物件算不算「寶箱」。兩軸，見類別說明。</summary>
    /// <remarks>
    /// 🔴 身分比對用 <c>BaseId</c>：本 pin 的 <c>DataId</c> 只是 <c>BaseId</c> 的過時別名，
    /// 但別名帶著 <c>[Obsolete]</c>，而且「查表安全、比對要用 BaseId」這條規矩不在這裡開例外。
    /// </remarks>
    public static bool IsChest(IGameObject obj)
    {
        if (obj.ObjectKind == ObjectKind.Treasure) return true;
        if (obj.ObjectKind != ObjectKind.EventObj) return false;

        return EventObjIdSet.Contains(obj.BaseId);
    }

    /// <remarks>
    /// ⚠️ 一次性的整表走訪（台服 7.20 是 15000 列）。失敗一律降級而不是擲例外：
    /// 呼叫端多半是在 <c>OnEnable</c> 裡叫它，而 <c>OnEnable</c> 擲例外會讓整個模組被標成
    /// 「啟用失敗」，可是第一軸（<see cref="ObjectKind.Treasure"/>）其實還是好的。
    /// </remarks>
    private static void Build()
    {
        EventObjIdSet.Clear();
        DegradedReason = string.Empty;

        try
        {
            var sheet = Svc.Data.GetExcelSheet<EObjName>();
            var reference = sheet.GetRowOrDefault(ReferenceChestEObjNameRow)?.Singular.ExtractText();

            if (string.IsNullOrEmpty(reference))
            {
                DegradedReason =
                    $"EObjName 第 {ReferenceChestEObjNameRow} 列讀不到名字，"
                    + "所以無法辨識銀／金寶箱（那些是 EventObj，只能靠名稱表比對）。\n"
                    + "本次只會找到銅寶箱（ObjectKind.Treasure）。";
                Svc.Log.Information($"[ChestIdentity] {DegradedReason.Replace("\n", " ")}");
                return;
            }

            foreach (var row in sheet)
            {
                if (row.Singular.ExtractText() == reference)
                    EventObjIdSet.Add(row.RowId);
            }

            built = true;

            Svc.Log.Information(
                $"[ChestIdentity] 以 EObjName#{ReferenceChestEObjNameRow} 的名稱為基準，"
                + $"收集到 {EventObjIdSet.Count} 個同名的 EventObj 列號。");
        }
        catch (Exception ex)
        {
            EventObjIdSet.Clear();
            DegradedReason =
                $"建立 EventObj 寶箱對照表時發生例外：{ex.GetType().Name}：{ex.Message}\n"
                + "本次只會找到銅寶箱（ObjectKind.Treasure）。";
            Svc.Log.Information($"[ChestIdentity] {DegradedReason.Replace("\n", " ")}");
        }
    }
}

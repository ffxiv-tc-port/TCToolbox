using System;
using Dalamud.Memory;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using ValueType = FFXIVClientStructs.FFXIV.Component.GUI.ValueType;

namespace TCToolbox.Core;

/// <summary>觸發背包右鍵選單項目的結果。</summary>
/// <remarks>
/// 🔴 <b>零值必須是一個有意義的失敗</b>，而不是「成功」——這個列舉會被
/// <c>default</c>／反序列化落點碰到的機會雖然低，但把零值放在成功上會讓任何
/// 「忘了指派」的路徑靜默表現成「已經點下去了」。
/// </remarks>
public enum ContextMenuFireResult
{
    /// <summary>讀不到 <c>Addon</c> 表上的選單文字，連比對都做不了。<b>這是零值。</b></summary>
    LabelUnavailable = 0,

    /// <summary>已送出點擊。</summary>
    Fired = 1,

    /// <summary>選單裡沒有這一項。</summary>
    NotFound = 2,

    /// <summary>項目被收在次選單裡，主選單的列號點不到它。</summary>
    InSubmenu = 3,

    /// <summary>項目存在但是停用狀態。</summary>
    Disabled = 4,

    /// <summary>取不到右鍵選單所屬的 addon。</summary>
    AddonUnavailable = 5,
}

/// <summary>
/// 背包右鍵選單（<c>AgentInventoryContext</c>）的共用操作。
/// </summary>
/// <remarks>
/// 📌 內容是從 <see cref="Modules.AutoInventoryTransfer"/> 的私有方法
/// <c>TryFireContextMenuEntry</c> <b>原封不動搬過來的</b>（判斷順序、索引算法、
/// 診斷輸出一個字都沒改，只是把「印哪一句聊天訊息」交還給呼叫端），
/// 讓 <see cref="Modules.QuickSplitStacks"/> 共用同一條已經實機驗證過的路徑。
/// 做法與 <see cref="ItemContextResolver"/> 當初的抽取完全相同。
/// <para>
/// 🔴 <b>只對 <c>AgentInventoryContext</c> 的選單有效。</b>一般視窗的右鍵選單是
/// <c>AgentContext</c>，值表的索引基準不一樣；拿這裡的算法去點那種選單會差幾格，
/// 而那種選單裡有「丟棄」。
/// </para>
/// <para>
/// 🔑 <b>比對用的是遊戲自己的 <c>Addon</c> 表字串，不是寫死的翻譯</b>，所以跟語言無關。
/// </para>
/// </remarks>
public static unsafe class InventoryContextMenu
{
    /// <summary>
    /// 在背包右鍵選單裡找出 <c>Addon#<paramref name="addonRowId"/></c> 那一項並點下去。
    /// </summary>
    /// <param name="agent">背包右鍵選單的 agent（呼叫端負責判空）。</param>
    /// <param name="addonRowId">選單文字所在的 <c>Addon</c> 列號。</param>
    /// <param name="logTag">記錄行的前綴（通常是模組的 <c>InternalName</c>）。</param>
    /// <param name="label">實際比對用的選單文字（讀不到時是空字串）。</param>
    /// <remarks>
    /// 索引方式照抄 Artisan <c>Tasks/TaskSelectRetainer.cs</c>（台服實測有效）：選單實際佔用
    /// <c>EventParams[ContexItemStartIndex .. +ContextItemCount]</c>，<b>不能掃完 98 格再數字串</b>
    /// —— 那樣會掃到上一次選單的殘留，算出來的序號也不是 callback 要的列號。
    /// </remarks>
    public static ContextMenuFireResult TryFireEntry(
        AgentInventoryContext* agent, uint addonRowId, string logTag, out string label)
    {
        label = string.Empty;
        if (agent == null) return ContextMenuFireResult.AddonUnavailable;

        var wanted = Svc.Data.GetExcelSheet<Addon>()?.GetRowOrDefault(addonRowId)?.Text.ExtractText().Trim();
        if (string.IsNullOrEmpty(wanted))
        {
            Svc.Log.Warning($"[{logTag}] 讀不到 Addon#{addonRowId} 的字串，無法比對選單項目。");
            return ContextMenuFireResult.LabelUnavailable;
        }

        label = wanted;

        var startIndex = Math.Clamp(agent->ContexItemStartIndex, 0, 98);
        var itemCount = Math.Clamp(agent->ContextItemCount, 0, 98 - startIndex);

        var index = -1;
        var labels = new string[itemCount];
        for (var entry = 0; entry < itemCount; entry++)
        {
            var v = agent->EventParams[startIndex + entry];
            if (v.Type is not ValueType.String and not ValueType.ManagedString) continue;

            var ptr = v.String.Value;
            if (ptr == null) continue;
            labels[entry] = MemoryHelper.ReadSeStringNullTerminated((nint)ptr).TextValue.Trim();
            if (index == -1 && labels[entry] == wanted) index = entry;
        }

        // 選單長相一律記下來（Information 級，因為使用者的記錄等級會濾掉 DBG）。
        // 「▸」標的是被收進二級指令的項目——那是最可能讓找不到的原因，
        // 而且從錯誤訊息裡看不出來，只能靠這行分辨「沒有這個項目」與「在二級選單裡」。
        var dump = new string[itemCount];
        for (var entry = 0; entry < itemCount; entry++)
        {
            var inSubmenu = (agent->ContextItemSubmenuMask & (1u << entry)) != 0;
            dump[entry] = $"{entry}{(inSubmenu ? "▸" : "")}:{labels[entry]}";
        }

        Svc.Log.Information(
            $"[{logTag}] 找「{wanted}」→ {(index == -1 ? "沒找到" : $"第 {index} 項")}；" +
            $"選單 {itemCount} 項（起點 EventParams[{startIndex}]，▸＝二級指令）：{string.Join(" | ", dump)}");

        if (index == -1) return ContextMenuFireResult.NotFound;

        // 被收進次選單的項目不能直接用這個序號觸發（那是主選單的列號）。
        if ((agent->ContextItemSubmenuMask & (1u << index)) != 0)
        {
            Svc.Log.Warning($"[{logTag}] 「{wanted}」在次選單裡（submenu mask），無法直接觸發。");
            return ContextMenuFireResult.InSubmenu;
        }

        if (agent->IsContextItemDisabled(index))
        {
            Svc.Log.Warning($"[{logTag}] 選單項目 {index}（{wanted}）是停用狀態。");
            return ContextMenuFireResult.Disabled;
        }

        var addonId = agent->AgentInterface.GetAddonId();
        // 判空集中在 UiHelper.GetAddonById（AtkStage.Instance() 與 RaptureAtkUnitManager 兩層都可能 null）。
        var addon = UiHelper.GetAddonById(addonId);
        if (addon == null)
        {
            Svc.Log.Warning($"[{logTag}] 取不到右鍵選單 addon。");
            return ContextMenuFireResult.AddonUnavailable;
        }

        Svc.Log.Debug($"[{logTag}] 觸發選單項目 {index}（{wanted}）");

        var values = stackalloc AtkValue[5];
        for (var i = 0; i < 5; i++)
        {
            values[i].Type = ValueType.Int;
            values[i].Int = 0;
        }

        values[1].Int = index;
        addon->FireCallback(5, values, true);
        return ContextMenuFireResult.Fired;
    }
}

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Dalamud.Memory;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using ValueType = FFXIVClientStructs.FFXIV.Component.GUI.ValueType;

namespace TCToolbox.Core;

/// <summary>Addon／Agent 互動輔助（等同 ECommons Callback / AddonMaster 的自寫最小集）。</summary>
public static unsafe class UiHelper
{
    public static AtkUnitBase* GetAddon(string name) => Svc.GameGui.GetAddonByName<AtkUnitBase>(name);

    public static bool IsReady(AtkUnitBase* addon) =>
        addon != null && addon->IsVisible && addon->UldManager.LoadedState == AtkLoadState.Loaded;

    public static bool IsAddonReady(string name) => IsReady(GetAddon(name));

    /// <summary>依 addon id 取 addon，取不到（含遊戲尚未就緒）一律回 <c>null</c>。</summary>
    /// <remarks>
    /// 🔴 這支存在的理由是 <c>AtkStage.Instance()->RaptureAtkUnitManager->GetAddonById(...)</c>
    /// 這條兩層裸鏈：<c>AtkStage.Instance()</c> 是 <c>[StaticAddress(..., isPointer: true)]</c>
    /// ——產生器讀「指標的位址」再解參考一層，遊戲尚未建立單例時回 <c>null</c>（非 isPointer 的那種
    /// 才保證不回 null，是擲 <c>InvalidOperationException</c>）；<c>RaptureAtkUnitManager</c> 又是
    /// <c>AtkStage</c> +0x20 的裸欄位，同樣可能是 null。
    /// 裸解參考 null 原生指標是 AccessViolationException，在 .NET Core 屬 corrupted-state
    /// exception，<c>try/catch</c> 攔不到 —— 只能事前擋。
    /// <para>呼叫端本來就有「addon 為 null 就放棄」的路徑，所以這裡回 null 不改變任何既有語意。</para>
    /// </remarks>
    public static AtkUnitBase* GetAddonById(uint addonId)
    {
        if (addonId == 0) return null;

        var stage = AtkStage.Instance();
        if (stage == null) return null;

        var manager = stage->RaptureAtkUnitManager;
        if (manager == null) return null;

        return manager->GetAddonById((ushort)addonId);
    }

    private static void BuildValues(AtkValue* dest, object[] values)
    {
        for (var i = 0; i < values.Length; i++)
        {
            switch (values[i])
            {
                case int v:
                    dest[i].Type = ValueType.Int;
                    dest[i].Int = v;
                    break;
                case uint v:
                    dest[i].Type = ValueType.UInt;
                    dest[i].UInt = v;
                    break;
                case bool v:
                    dest[i].Type = ValueType.Bool;
                    dest[i].Byte = (byte)(v ? 1 : 0);
                    break;
                case AtkValue v:
                    dest[i] = v;
                    break;
                default:
                    throw new ArgumentException($"不支援的 AtkValue 型別: {values[i]?.GetType().Name ?? "null"}");
            }
        }
    }

    /// <summary>對 addon 發送合成事件（等同 /callback）。</summary>
    public static void FireCallback(AtkUnitBase* addon, bool updateState, params object[] values)
    {
        if (addon == null) return;
        var atkValues = stackalloc AtkValue[Math.Max(1, values.Length)];
        BuildValues(atkValues, values);
        addon->FireCallback((uint)values.Length, atkValues, updateState);
    }

    /// <summary>對 Agent 發送事件（等同 OmenTools 的 AgentId.SendEvent）。</summary>
    public static void SendAgentEvent(AgentId agentId, ulong eventKind, params object[] values)
    {
        // AgentModule.Instance() 走 UIModule，UI 尚未建立時回 null（CS 手寫實作）。
        // 取不到就不送事件——與下面 agent == null 完全相同的失敗形式。
        var agentModule = AgentModule.Instance();
        if (agentModule == null) return;

        var agent = agentModule->GetAgentByInternalId(agentId);
        if (agent == null) return;

        var atkValues = stackalloc AtkValue[Math.Max(1, values.Length)];
        BuildValues(atkValues, values);
        var returnValue = new AtkValue();
        agent->ReceiveEvent(&returnValue, atkValues, (uint)values.Length, eventKind);
    }

    /// <summary>讀取 SelectString 的選項清單（PopupMenu 條目，不含標題行——避開台服首行標題偏移陷阱）。</summary>
    public static List<string> GetSelectStringEntries(AtkUnitBase* addon)
    {
        var result = new List<string>();
        if (addon == null) return result;

        var popup = &((AddonSelectString*)addon)->PopupMenu.PopupMenu;
        for (var i = 0; i < popup->EntryCount; i++)
        {
            var ptr = popup->EntryNames[i].Value;
            result.Add(ptr == null ? string.Empty : MemoryHelper.ReadSeStringNullTerminated((nint)ptr).TextValue);
        }

        return result;
    }

    public static void SelectStringEntry(AtkUnitBase* addon, int index) => FireCallback(addon, true, index);

    /// <summary><c>SelectYesno</c> 這扇窗在 <see cref="AddonPressGuard"/> 裡的鍵。</summary>
    public const string SelectYesnoAddonName = "SelectYesno";

    /// <summary>
    /// SelectYesno 點「是」。
    /// </summary>
    /// <remarks>
    /// 🔴 <b><see cref="IsReady"/> 過了不代表可以按。</b>按下之後有「正在關閉中」的幾幀，
    /// 那幾幀三關（非 null／<c>IsVisible</c>／<c>Loaded</c>）全過而 <c>FireCallback</c> 下去
    /// 就是攔不到的存取違規（2026-08-31 實機崩潰 <c>crash-20260831205734</c>）。
    /// 所以這裡多一道 <see cref="AddonPressGuard.TryBeginPress"/>：按過的那個實例
    /// 在觀察到它走完生命週期之前不再按。
    /// <para>
    /// ⚠️ 回 <see langword="false"/> 的語意<b>沒有改變</b>——本來就是「這次沒按到」，
    /// 呼叫端全都是「下一輪再試」。只是多了一種回 false 的理由。
    /// </para>
    /// </remarks>
    public static bool ClickSelectYesnoYes()
    {
        var addon = GetAddon(SelectYesnoAddonName);
        if (!IsReady(addon)) return false;
        if (!AddonPressGuard.TryBeginPress(SelectYesnoAddonName, addon)) return false;
        FireCallback(addon, true, 0);
        return true;
    }

    /// <summary>SelectYesno 點「否」（callback value 1，與「是」的 0 相對）。</summary>
    /// <remarks>防護與理由同 <see cref="ClickSelectYesnoYes"/>。</remarks>
    public static bool ClickSelectYesnoNo()
    {
        var addon = GetAddon(SelectYesnoAddonName);
        if (!IsReady(addon)) return false;
        if (!AddonPressGuard.TryBeginPress(SelectYesnoAddonName, addon)) return false;
        FireCallback(addon, true, 1);
        return true;
    }

    /// <summary>讀 SelectYesno 的提示文字（讀不到一律回空字串，不擲例外）。</summary>
    public static string GetSelectYesnoText()
    {
        var addon = GetAddon("SelectYesno");
        if (!IsReady(addon)) return string.Empty;

        var node = ((AddonSelectYesno*)addon)->PromptText;
        if (node == null || !node->NodeText.StringPtr.HasValue) return string.Empty;

        return node->NodeText.ToString();
    }

    /// <summary>若 Talk 對話框開著就點掉它。</summary>
    public static bool ClickTalkIfOpen()
    {
        var addon = GetAddon("Talk");
        if (!IsReady(addon)) return false;

        // 🔴 AtkStage.Instance() 是 [StaticAddress(..., isPointer: true)]，遊戲尚未建立單例時回 null。
        //    `&stage->AtkEventTarget` 在 null 上算出來是 0（AtkEventTarget 在 +0x0），交給原生
        //    ReceiveEvent 就是攔不到的 AVE。判空樣板同 ClickToMove.IsCursorOverGameUi。
        //    讀不到回 false ＝「這次沒點掉」，呼叫端會重試。
        var stage = AtkStage.Instance();
        if (stage == null) return false;

        var evt = stackalloc AtkEvent[1];
        evt[0] = new AtkEvent
        {
            Listener = (AtkEventListener*)addon,
            Target = &stage->AtkEventTarget,
            State = new AtkEventState { StateFlags = (AtkEventStateFlags)132 },
        };
        var data = stackalloc AtkEventData[1];
        *data = default;

        addon->ReceiveEvent(AtkEventType.MouseDown, 0, evt, data);
        addon->ReceiveEvent(AtkEventType.MouseClick, 0, evt, data);
        addon->ReceiveEvent(AtkEventType.MouseUp, 0, evt, data);
        return true;
    }

    /// <summary>讀 addon AtkValues 裡第一個字串值（沒有就空字串）。錄製／診斷用。</summary>
    public static string GetFirstStringValue(AtkUnitBase* addon)
    {
        // 🔴 判 addon 與長度都不夠：addon 拆解時 AtkValues 這個欄位會先被釋放成 null，
        //    AtkValuesCount 卻可能還留著殘值 —— 這個組合會讓下面的迴圈對位址 0 解參考，
        //    ＝ AccessViolationException（corrupted-state exception，try/catch 攔不到）。
        //    樣板同 FastGrandCompanyExchange.ReadAtkString／AutoFCWSDeliver.ParseDeliverables。
        //    讀不到回空字串——呼叫端本來就把空字串當「沒描述」，語意零變更。
        if (addon == null || addon->AtkValues == null) return string.Empty;
        var count = addon->AtkValuesCount;
        for (var i = 0; i < count; i++)
        {
            var v = addon->AtkValues[i];
            if (v.Type is ValueType.String or ValueType.String8 or ValueType.ManagedString)
            {
                var ptr = v.String.Value;
                if (ptr != null)
                    return Dalamud.Memory.MemoryHelper.ReadSeStringNullTerminated((nint)ptr).TextValue;
            }
        }

        return string.Empty;
    }

    /// <summary>對 addon 送單一 Int 的 callback（照實機錄製的形狀）。</summary>
    public static void FireCallbackInt(AtkUnitBase* addon, int a)
    {
        if (addon == null) return;
        var values = stackalloc AtkValue[1];
        values[0].Type = ValueType.Int;
        values[0].Int = a;
        addon->FireCallback(1, values, true);
    }

    /// <summary>對 addon 送 [Int, Int, Bool] 的 callback（照實機錄製的形狀）。</summary>
    public static void FireCallbackIntIntBool(AtkUnitBase* addon, int a, int b, bool c)
    {
        if (addon == null) return;
        var values = stackalloc AtkValue[3];
        values[0].Type = ValueType.Int;
        values[0].Int = a;
        values[1].Type = ValueType.Int;
        values[1].Int = b;
        values[2].Type = ValueType.Bool;
        values[2].Byte = (byte)(c ? 1 : 0);
        addon->FireCallback(3, values, true);
    }

    /// <summary>
    /// CharaMake 編輯器是否「帶著已載入的外觀」開著。
    /// </summary>
    /// <remarks>
    /// 🔴 實機錄製（2026-08-23）：載入儲存檔後 <c>AtkValues[0].Int==2</c>、<c>AtkValues[2]</c>＝種族字串
    /// （「拉拉菲爾族 女」）；空白（沒載檔）時 <c>[0]==0</c>、<c>[2]</c>＝「? ? ?」。
    /// 判定不到（版面變了／值型別不符）一律回 false——寧可不按「完成」也不碰空白編輯器。
    /// </remarks>
    public static bool TryGetCharaMakeLoadedState(string addonName, string blankMarker, out string raceText)
    {
        raceText = string.Empty;
        var addon = GetAddon(addonName);
        // 🔴 CharaMake 根 addon 的 IsReady（IsVisible + LoadedState）在實機恆 false（隱形容器），
        //    所以這裡拿不到 IsReady 當閘門，只能逐欄自判。
        // 🔴 AtkValues **不是**「addon 存活期間必然有效」——addon 拆解時它會先被釋放成 null，
        //    而 AtkValuesCount 可能還留著殘值，於是長度檢查通過、索引下去卻對位址 0 解參考
        //    ＝ AccessViolationException（corrupted-state exception，try/catch 攔不到）。
        //    本模組正好被 RetainerBatchRename 在 CharaMake 視窗開／關的過渡期反覆輪詢，
        //    那正是殘值組合出現的時刻 ⇒ 必須先自判 AtkValues 欄位。
        //    （同一道檢查的既有樣板：AutoFCWSDeliver.ParseDeliverables、FastGrandCompanyExchange.ReadUInt）
        //    回 false ＝「判定不到」，落入呼叫端既有的下一 tick 重試路徑，語意零變更。
        if (addon == null) return false;
        if (addon->AtkValues == null) return false;
        if (addon->AtkValuesCount < 3) return false;

        var state = addon->AtkValues[0];
        if (state.Type != ValueType.Int || state.Int == 0) return false;

        var race = addon->AtkValues[2];
        if (race.Type is not (ValueType.String or ValueType.String8 or ValueType.ManagedString)) return false;
        var ptr = race.String.Value;
        if (ptr == null) return false;

        raceText = Dalamud.Memory.MemoryHelper.ReadSeStringNullTerminated((nint)ptr).TextValue;
        if (raceText.Length == 0 || raceText.Contains(blankMarker, StringComparison.Ordinal)) return false;

        return true;
    }

    /// <summary>點擊 addon 上的按鈕元件（複用按鈕自身既有的事件）。</summary>
    public static bool ClickButton(AtkUnitBase* addon, AtkComponentButton* button)
    {
        if (addon == null || button == null) return false;

        var node = button->AtkComponentBase.OwnerNode;
        if (node == null) return false;
        if (!button->IsEnabled || !node->AtkResNode.IsVisible()) return false;

        var evt = node->AtkResNode.AtkEventManager.Event;
        if (evt == null) return false;

        addon->ReceiveEvent(evt->State.EventType, (int)evt->Param, evt);
        return true;
    }

    /// <summary>
    /// 對 <c>InputString</c> 送出「確定＋名字」的合成 callback。
    /// </summary>
    /// <remarks>
    /// 🔴 值的排列逐字對應台服 7.20 實機錄製：<c>[0:int=0（確定）, 1:str=名字, 2:str=空字串]</c>。
    /// 字串必須是非受控記憶體、FireCallback 期間有效、送完立即釋放（樣板同 ECommons <c>Callback.Fire</c>）。
    /// 取不到 addon（或尚未就緒）回 <see langword="false"/>，呼叫端會重試。
    /// </remarks>
    public static bool FireInputStringConfirm(string name)
    {
        var addon = GetAddon("InputString");
        if (!IsReady(addon)) return false;

        var nameBytes = Encoding.UTF8.GetBytes(name ?? string.Empty);
        var nameAlloc = Marshal.AllocHGlobal(nameBytes.Length + 1);
        var emptyAlloc = Marshal.AllocHGlobal(1);
        try
        {
            Marshal.Copy(nameBytes, 0, nameAlloc, nameBytes.Length);
            Marshal.WriteByte(nameAlloc, nameBytes.Length, 0);
            Marshal.WriteByte(emptyAlloc, 0, 0);

            var values = stackalloc AtkValue[3];
            values[0].Type = ValueType.Int;
            values[0].Int = 0;
            values[1].Type = ValueType.String;
            values[1].String = (byte*)nameAlloc;
            values[2].Type = ValueType.String;
            values[2].String = (byte*)emptyAlloc;

            addon->FireCallback(3, values, true);
        }
        finally
        {
            Marshal.FreeHGlobal(nameAlloc);
            Marshal.FreeHGlobal(emptyAlloc);
        }

        return true;
    }
}

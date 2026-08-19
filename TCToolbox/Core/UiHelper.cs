using System;
using System.Collections.Generic;
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

    /// <summary>SelectYesno 點「是」。</summary>
    public static bool ClickSelectYesnoYes()
    {
        var addon = GetAddon("SelectYesno");
        if (!IsReady(addon)) return false;
        FireCallback(addon, true, 0);
        return true;
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
}

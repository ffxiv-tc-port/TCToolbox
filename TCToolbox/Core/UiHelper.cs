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

    /// <summary>掃描同名視窗實例時最多看幾格（<c>GetAddonByName</c> 的 index 從 1 起算）。</summary>
    /// <remarks>
    /// 同名視窗同時掛著超過兩三個已經很罕見，這個上限只是防呆——真正的終止條件是掃到第一個
    /// <c>null</c> 就停（那代表這個名字底下沒有更多實例了）。
    /// </remarks>
    public const int MaxAddonInstanceScan = 16;

    /// <summary>
    /// 在<b>所有</b>同名實例裡找出第一扇 <see cref="IsReady"/> 的視窗；一扇都沒有就回 <c>null</c>。
    /// </summary>
    /// <remarks>
    /// 🔴🔴 <b><see cref="GetAddon"/>／<see cref="IsAddonReady"/> 只看得到第 1 格。</b>
    /// <c>GetAddonByName</c> 的 <c>index</c> 參數預設是 1，而同一個名字是<b>可以同時掛著好幾個實例</b>的
    /// ——<c>SelectYesno</c> 連著跳兩扇（「確定要交易優質道具嗎？」接「確定要為合建設備提供○○×N嗎？」）
    /// 就是現成的例子，剛關掉、還沒被回收的那一扇也可能繼續佔著格子。
    /// 這種時候第 1 格拿到的是<b>已經不可見</b>的那一扇，<see cref="IsReady"/> 對它回
    /// <see langword="false"/>，於是「確認框在不在」被答成「不在」，而真正開著、等著被按的那一扇
    /// 在第 2 格——<b>完全看不到，而且不會報錯</b>。
    /// <para>
    /// ⚠️ 這支<b>不是</b> <see cref="GetAddon"/> 的替代品：只有「我要找出那扇開著的窗」的地方才該用它
    /// （多幾次原生查詢換一個正確答案）。既有呼叫點維持原樣，不要無差別換掉。
    /// </para>
    /// </remarks>
    public static AtkUnitBase* FindReadyAddon(string name)
    {
        for (var index = 1; index <= MaxAddonInstanceScan; index++)
        {
            var addon = Svc.GameGui.GetAddonByName<AtkUnitBase>(name, index);
            if (addon == null) return null; // 這個名字底下沒有更多實例了

            if (IsReady(addon)) return addon;
        }

        return null;
    }

    /// <summary><see cref="FindReadyAddon"/> 的布林版：同名實例裡<b>任何一扇</b>開著就算數。</summary>
    public static bool IsAnyAddonReady(string name) => FindReadyAddon(name) != null;

    /// <summary>
    /// 把某個名字底下所有實例的狀態壓成一行診斷字串（第幾格、位址、可見嗎、載入完成嗎）。
    /// </summary>
    /// <remarks>
    /// 給「我明明看到視窗開著，模組卻說沒有」這種回報用：一眼就能分辨是「第 1 格擋住了」
    /// 還是「這扇窗根本不叫這個名字」。只讀不寫，位址只印出來、不留存也不解參考。
    /// </remarks>
    public static string DescribeAddonInstances(string name)
    {
        var sb = new StringBuilder();
        for (var index = 1; index <= MaxAddonInstanceScan; index++)
        {
            var addon = Svc.GameGui.GetAddonByName<AtkUnitBase>(name, index);
            if (addon == null) break;

            if (sb.Length > 0) sb.Append('、');
            sb.Append('#').Append(index)
              .Append(" 位址 0x").Append(((nint)addon).ToString("X"))
              .Append(" 可見=").Append(addon->IsVisible)
              .Append(" 載入完成=").Append(addon->UldManager.LoadedState == AtkLoadState.Loaded);
        }

        return sb.Length == 0 ? "（一個實例都沒有）" : sb.ToString();
    }

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

    /// <summary>
    /// 對 addon 發送合成事件（等同 /callback）。守衛擋下時<b>靜默不送</b>——
    /// 要知道這一幀有沒有真的送出去，用 <see cref="TryFireCallback"/>。
    /// </summary>
    /// <remarks>
    /// 📌 刻意維持 <see langword="void"/>：改成回 bool 會讓 <c>Enqueue(() => FireCallback(...))</c>
    /// 這種運算式 lambda 從 <c>Action</c> 靜默改綁 <c>Func&lt;bool?&gt;</c>，任務語意變成「做到回 true 為止」。
    /// </remarks>
    public static void FireCallback(AtkUnitBase* addon, bool updateState, params object[] values) =>
        TryFireCallback(addon, updateState, values);

    /// <summary>
    /// 對 addon 發送合成事件，送出前先過 <see cref="AddonPressGuard"/>：
    /// 同一個實例（位址）、同一組參數，在觀察到那扇窗走完生命週期之前只送一次。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>這是本 repo 所有 callback 的唯一出口</b>（<see cref="FireCallbackInt"/>／
    /// <see cref="FireCallbackIntIntBool"/>／<see cref="SelectStringEntry"/>／<see cref="ClickSelectYesnoYes"/>…
    /// 全部繞經這裡），守衛才罩得住每一條路。
    /// <para>
    /// 回 <see langword="false"/> ＝這一幀沒送（addon 為 null，或守衛判定「剛按過、還沒觀察到它收掉」）。
    /// 對呼叫端的意義一律是「這一輪沒按到，下一輪再來」，與「addon 還沒出現」走同一條既有路徑。
    /// </para>
    /// </remarks>
    public static bool TryFireCallback(AtkUnitBase* addon, bool updateState, params object[] values)
    {
        if (addon == null) return false;
        if (!AddonPressGuard.TryBeginPress(addon, AddonPressGuard.DescribeCallback(updateState, values)))
            return false;

        var atkValues = stackalloc AtkValue[Math.Max(1, values.Length)];
        BuildValues(atkValues, values);
        addon->FireCallback((uint)values.Length, atkValues, updateState);
        return true;
    }

    /// <summary>對 Agent 發送事件（等同 OmenTools 的 AgentId.SendEvent）。</summary>
    /// <remarks>
    /// 目標是常駐的 <c>AgentInterface</c>，不是某扇視窗，所以這一支<b>沒有</b>視窗守衛。
    /// 送出後遊戲會去動某扇窗（開選單、選項目）的話，改用 <see cref="TrySendAgentEvent"/> 把事件錨在那扇窗上。
    /// </remarks>
    public static void SendAgentEvent(AgentId agentId, ulong eventKind, params object[] values) =>
        SendAgentEventCore(agentId, eventKind, values);

    /// <summary>
    /// 對 Agent 發送事件，但先把它<b>錨在某扇視窗的實例上</b>過 <see cref="AddonPressGuard"/>：
    /// 同一扇 <paramref name="anchor"/>、同一個 agent 事件與參數，在那扇窗走完生命週期前只送一次。
    /// </summary>
    /// <remarks>
    /// 用在「送給 agent 的事件會去碰某扇窗」的場合（例如對 <c>NpcTrade</c> 送「選這一份」時
    /// 正在碰 <c>ContextIconMenu</c>）：agent 是常駐的，但它接下來要動的那扇窗可能正在關閉。
    /// 回 <see langword="false"/> ＝這一幀沒送（錨窗為 null／守衛擋下／agent 取不到），意義同 <see cref="TryFireCallback"/>。
    /// </remarks>
    public static bool TrySendAgentEvent(
        string anchorAddonName, AtkUnitBase* anchor, AgentId agentId, ulong eventKind, params object[] values)
    {
        if (anchor == null) return false;

        var key = $"agent:{agentId}:{eventKind}:{AddonPressGuard.DescribeCallback(false, values)}";
        if (!AddonPressGuard.TryBeginPress(anchorAddonName, anchor, key)) return false;

        return SendAgentEventCore(agentId, eventKind, values);
    }

    private static bool SendAgentEventCore(AgentId agentId, ulong eventKind, object[] values)
    {
        // AgentModule.Instance() 走 UIModule，UI 尚未建立時回 null（CS 手寫實作）。
        // 取不到就不送事件——與下面 agent == null 完全相同的失敗形式。
        var agentModule = AgentModule.Instance();
        if (agentModule == null) return false;

        var agent = agentModule->GetAgentByInternalId(agentId);
        if (agent == null) return false;

        var atkValues = stackalloc AtkValue[Math.Max(1, values.Length)];
        BuildValues(atkValues, values);
        var returnValue = new AtkValue();
        agent->ReceiveEvent(&returnValue, atkValues, (uint)values.Length, eventKind);
        return true;
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

    /// <summary>任何一段文字含 U+FFFD 替換字元＝讀到一半／視窗記憶體正在變動。</summary>
    public static bool LooksMidUpdate(IEnumerable<string> texts)
    {
        foreach (var text in texts)
        {
            if (AddonPrompt.LooksMidUpdate(text)) return true;
        }

        return false;
    }

    /// <summary>SelectString 選第 <paramref name="index"/> 項（<c>-1</c>＝取消）。</summary>
    /// <remarks>
    /// 🔴 選項文字裡讀到 U+FFFD 替換字元＝窗的記憶體正在變動（多半是關閉中），這一幀不碰。
    /// 送出本身走 <see cref="TryFireCallback"/>；<c>SelectString</c> 是「回答一次即終結」的窗，
    /// 守衛對它不分參數、一個實例只准按一次。
    /// </remarks>
    public static void SelectStringEntry(AtkUnitBase* addon, int index)
    {
        if (addon == null) return;

        if (LooksMidUpdate(GetSelectStringEntries(addon)))
        {
            if (Throttle.Pass("UiHelper-SelectStringMidUpdate", 1_000))
                Svc.Log.Information("[UiHelper] SelectString 的選項文字含替換字元（視窗記憶體變動中），這一幀不送。");
            return;
        }

        TryFireCallback(addon, true, index);
    }

    /// <summary><c>SelectYesno</c> 這扇窗在 <see cref="AddonPressGuard"/> 裡的鍵。</summary>
    public const string SelectYesnoAddonName = "SelectYesno";

    /// <summary><c>Talk</c> 對話框在 <see cref="AddonPressGuard"/> 裡的鍵（多次互動窗，逃生口 15 幀）。</summary>
    public const string TalkAddonName = "Talk";

    /// <summary><c>InputString</c> 在 <see cref="AddonPressGuard"/> 裡的鍵。</summary>
    public const string InputStringAddonName = "InputString";

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
    public static bool ClickSelectYesnoYes() => ClickSelectYesno(0);

    /// <summary>SelectYesno 點「否」（callback value 1，與「是」的 0 相對）。</summary>
    /// <remarks>防護與理由同 <see cref="ClickSelectYesnoYes"/>。</remarks>
    public static bool ClickSelectYesnoNo() => ClickSelectYesno(1);

    private static bool ClickSelectYesno(int answer)
    {
        var addon = GetAddon(SelectYesnoAddonName);
        if (!IsReady(addon)) return false;

        // 🔴 提示文字讀出 U+FFFD ＝窗的記憶體正在變動（崩潰前實機 log 的亂碼 prompt 就是這徵兆），這一幀不碰。
        if (AddonPrompt.LooksMidUpdate(ReadSelectYesnoText(addon)))
        {
            if (Throttle.Pass("UiHelper-SelectYesnoMidUpdate", 1_000))
                Svc.Log.Information("[UiHelper] SelectYesno 的提示文字含替換字元（視窗記憶體變動中），這一幀不按。");
            return false;
        }

        // SelectYesno 在守衛裡是「回答一次即終結」的窗：一個實例不管是／否只准按一次。
        return TryFireCallback(addon, true, answer);
    }

    /// <summary>讀 SelectYesno 的提示文字（讀不到一律回空字串，不擲例外）。</summary>
    public static string GetSelectYesnoText() => ReadSelectYesnoText(GetAddon(SelectYesnoAddonName));

    /// <summary>
    /// 讀<b>指定那一扇</b> SelectYesno 的提示文字（讀不到一律回空字串，不擲例外）。
    /// </summary>
    /// <remarks>
    /// 🔴 呼叫端如果是用 <see cref="FindReadyAddon"/> 找到窗的，讀文字就<b>必須</b>用這一支。
    /// 走無參數的 <see cref="GetSelectYesnoText()"/> 會退回只看第 1 格，於是
    /// 「按的是第 2 格那扇、log 印的是第 1 格那扇的字」——診斷會指著錯的窗。
    /// </remarks>
    public static string GetSelectYesnoText(AtkUnitBase* addon) => ReadSelectYesnoText(addon);

    private static string ReadSelectYesnoText(AtkUnitBase* addon)
    {
        if (!IsReady(addon)) return string.Empty;

        var node = ((AddonSelectYesno*)addon)->PromptText;
        if (node == null || !node->NodeText.StringPtr.HasValue) return string.Empty;

        return node->NodeText.ToString();
    }

    /// <summary>若 Talk 對話框開著就點掉它。</summary>
    /// <remarks>
    /// 🔴 <c>Talk</c> 是「按一次翻一頁、窗不會因為被按而消失」的多次互動窗：守衛照樣記實例位址，
    /// 但逃生口是 <see cref="AddonPressGuard.RoutineRePressEscapeFrames"/>（15 幀）而不是 2 秒——
    /// 關閉中的危險窗口不到 10 幀，15 幀不落在裡面；每頁多等 0.25 秒幾乎無感。
    /// 回 <see langword="false"/> 的語意沒有變：本來就是「這次沒點掉」，呼叫端一律下一輪再試。
    /// </remarks>
    public static bool ClickTalkIfOpen()
    {
        var addon = GetAddon(TalkAddonName);
        if (!IsReady(addon)) return false;

        // 🔴 AtkStage.Instance() 是 [StaticAddress(..., isPointer: true)]，遊戲尚未建立單例時回 null。
        //    `&stage->AtkEventTarget` 在 null 上算出來是 0（AtkEventTarget 在 +0x0），交給原生
        //    ReceiveEvent 就是攔不到的 AVE。判空樣板同 ClickToMove.IsCursorOverGameUi。
        //    讀不到回 false ＝「這次沒點掉」，呼叫端會重試。
        var stage = AtkStage.Instance();
        if (stage == null) return false;

        // 守衛放在所有「取不到就放棄」的檢查之後：一登記就算按過，登記完卻不按會白白封鎖 15 幀。
        if (!AddonPressGuard.TryBeginRoutinePress(TalkAddonName, addon)) return false;

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

    /// <summary>對 addon 送單一 Int 的 callback（照實機錄製的形狀：<c>[Int=a]</c>、updateState=true）。</summary>
    /// <remarks>值的形狀與 <see cref="BuildValues"/> 對 <see cref="int"/> 產生的完全相同；繞經 <see cref="TryFireCallback"/> 讓守衛罩到。</remarks>
    public static void FireCallbackInt(AtkUnitBase* addon, int a) => TryFireCallback(addon, true, a);

    /// <summary>對 addon 送 [Int, Int, Bool] 的 callback（照實機錄製的形狀，updateState=true）。</summary>
    /// <remarks>值的形狀與 <see cref="BuildValues"/> 對 <see cref="int"/>／<see cref="bool"/> 產生的完全相同；繞經 <see cref="TryFireCallback"/> 讓守衛罩到。</remarks>
    public static void FireCallbackIntIntBool(AtkUnitBase* addon, int a, int b, bool c) =>
        TryFireCallback(addon, true, a, b, c);

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

    /// <summary><see cref="TryClickButton"/> 的三態結果。</summary>
    /// <remarks>
    /// 🔴 零值刻意放在「沒按到」上：任何忘了指派的路徑都不會被誤判成「已經按下去了」。
    /// </remarks>
    public enum ButtonPressResult
    {
        /// <summary>按鈕現在按不動（addon／按鈕／OwnerNode 取不到、停用、不可見、沒有事件）。</summary>
        Unavailable = 0,

        /// <summary>事件已送出。</summary>
        Pressed = 1,

        /// <summary>
        /// 這一扇實例的這顆鈕剛按過、還沒觀察到那扇窗收掉——這一輪不送。
        /// 呼叫端一律當「等一下再來」，<b>不要</b>當成「按不動」去中止流程。
        /// </summary>
        Guarded = 2,
    }

    /// <summary>點擊 addon 上的按鈕元件（複用按鈕自身既有的事件）。<c>true</c>＝事件已送出。</summary>
    /// <remarks>
    /// 回 <see langword="false"/> 不分「按不動」與「守衛擋下」；把 false 當成終止條件的呼叫端
    /// 要改用 <see cref="TryClickButton"/> 分辨 <see cref="ButtonPressResult.Guarded"/>。
    /// </remarks>
    public static bool ClickButton(AtkUnitBase* addon, AtkComponentButton* button) =>
        TryClickButton(addon, button) == ButtonPressResult.Pressed;

    /// <summary>
    /// 點擊 addon 上的按鈕元件（複用按鈕自身既有的事件），送出前先過 <see cref="AddonPressGuard"/>。
    /// </summary>
    /// <remarks>
    /// 🔴 <c>ReceiveEvent</c> 比 <c>FireCallback</c> 更早踩到關閉中的窗：按下即關的鈕（交出、確認、接受）
    /// 按過之後那幾幀 <see cref="IsReady"/> 三關照過，再送一次事件就是攔不到的存取違規。
    /// 守衛的鍵＝（視窗位址，<c>btn:節點 id</c>）：同一扇窗上按不同的鈕互不干涉；
    /// 「回答一次即終結」的窗（<see cref="AddonPressGuard"/> 的併鍵名單）不分鈕、一個實例只准一次。
    /// <para>守衛放在所有「取不到就放棄」的檢查之後：一登記就算按過，登記完卻不按會白白封鎖到解除為止。</para>
    /// </remarks>
    public static ButtonPressResult TryClickButton(AtkUnitBase* addon, AtkComponentButton* button)
    {
        if (addon == null || button == null) return ButtonPressResult.Unavailable;

        var node = button->AtkComponentBase.OwnerNode;
        if (node == null) return ButtonPressResult.Unavailable;
        if (!button->IsEnabled || !node->AtkResNode.IsVisible()) return ButtonPressResult.Unavailable;

        var evt = node->AtkResNode.AtkEventManager.Event;
        if (evt == null) return ButtonPressResult.Unavailable;

        if (!AddonPressGuard.TryBeginPress(addon, $"btn:{node->AtkResNode.NodeId}"))
            return ButtonPressResult.Guarded;

        addon->ReceiveEvent(evt->State.EventType, (int)evt->Param, evt);
        return ButtonPressResult.Pressed;
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
        var addon = GetAddon(InputStringAddonName);
        if (!IsReady(addon)) return false;

        // 🔴 送出「確定」就關窗：按過的那個實例在觀察到它收掉之前不再送（InputString 在守衛裡是併鍵的單答窗）。
        //    這條路不走 TryFireCallback（BuildValues 不支援字串），所以守衛要自己接。
        if (!AddonPressGuard.TryBeginPress(InputStringAddonName, addon)) return false;

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

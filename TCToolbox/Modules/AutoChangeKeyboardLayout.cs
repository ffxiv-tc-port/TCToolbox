using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using Dalamud.Hooking;
using Dalamud.Interface.Utility.Raii;
using FFXIVClientStructs.FFXIV.Component.GUI;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 自動切換輸入法配置：文字輸入框取得焦點時切到你指定的「輸入用」鍵盤配置（例如注音／中文），
/// 失去焦點時切回「操作用」配置（例如英數，方便熱鍵移動）。若焦點瞬間輸入框裡已經是斜線指令
/// （<c>/</c> 開頭）則不切成中文，讓你打指令時仍是英數。
/// 機制：hook 遊戲設定文字輸入焦點的函式，在原函式跑完後呼叫 Win32 的 ActivateKeyboardLayout。
/// 不寫遊戲記憶體、不做 patch。
/// 參考 DailyRoutines AutoChangeKeyboardLayout（原作者 JiaXX）設計重寫（API13、無 OmenTools 相依）。
/// </summary>
/// <remarks>
/// 🔴 <b>與 DR 原版的關鍵差異：不跨幀保存原生指標。</b>DR 在 FocusStart 時把
/// <c>AtkComponentTextInput*</c> 塞進一個 50ms 之後才跑的 <c>RunOnTick</c> 閉包裡——那是把原生指標
/// 存到下一幀再解參考，輸入框在這 50ms 內被釋放就是 AccessViolation（<c>try/catch</c> 攔不到）。
/// 這裡改成<b>在 detour 內同步</b>判斷斜線並切換配置，此時指標必定有效。代價是少了那 50ms 的沉澱：
/// 極少數「焦點剛到、指令文字還沒填進來」的情形可能誤切成中文配置——那是良性失誤（自己再切回來即可），
/// 換來的是不會崩。
/// <para>
/// 🔴 <b>解不到特徵碼＝停用並記一筆 Information</b>（使用者跑 LogLevel 1 看得到），
/// 不像 DR 那樣讓位址回 0 之後照樣去 hook。
/// </para>
/// <para>
/// 📌 <b>預設 0 ＝「不切換」</b>，是一個持久的哨兵值：<see cref="SwitchTo"/> 看到 0 直接不動作。
/// 要生效請到設定畫面各挑一個配置。
/// </para>
/// <para>
/// 🔴 <b>以前的做法是啟用當下把「目前配置」烙進設定檔並存檔</b>，然後每次 focus／unfocus
/// 都無條件強制切到那個值。那讓「預設不切換」這個承諾在實務上不成立：
/// 使用者裝有多個配置（台服玩家常態是注音＋英數）而啟用模組時停在 A，
/// 之後手動切到 B 再點任何文字輸入框，就會被強制切回 A ——
/// 一個什麼都沒設定過的人拿到的是主動干預，而不是承諾的 no-op。
/// 而且烙死的值永久持久化，連「回到未設定」的路都沒有（UI 只能選具體配置）。
/// ⇒ 現在：不烙印、0 保持哨兵、<b>兩欄相同也視為不切換</b>（這一條同時把既有使用者
/// 已經烙上的值救回來——烙印一定是把兩欄設成同一個值），UI 另外提供「（不切換）」選項。
/// </para>
/// </remarks>
public sealed unsafe class AutoChangeKeyboardLayout : TcModule
{
    public override string InternalName => "AutoChangeKeyboardLayout";
    public override string DisplayName => "自動切換輸入法配置";

    public override string Description =>
        "文字輸入框取得焦點時切到指定的輸入配置（如注音），失去焦點時切回操作配置（如英數）；" +
        "焦點瞬間已是斜線指令則保持操作配置。預設不切換（兩邊都是「（不切換）」或挑成同一個時完全不動作），" +
        "需在設定裡各挑一個不同的配置才生效。";

    public override ModuleCategory Category => ModuleCategory.Misc;

    public override bool HasConfigUI => true;

    /// <summary>設定文字輸入焦點目標的函式（sig 已對台服 7.20 主程式離線驗證，唯一命中 0x1406846F0）。</summary>
    private const string SetTextInputTargetSignature =
        "4C 8B DC 55 53 57 41 54 41 57 49 8D AB ?? ?? ?? ?? 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 85 ?? ?? ?? ?? 48 8B 9D ?? ?? ?? ??";

    private delegate void SetTextInputTargetDelegate(
        AtkComponentTextInput* component, AtkEventType eventType, int eventParam,
        AtkEvent* atkEvent, AtkEventData* atkEventData);

    private Hook<SetTextInputTargetDelegate>? hook;

    private AutoChangeKeyboardLayoutConfig Config => Plugin.Instance.Config.KeyboardLayout;

    private Dictionary<ushort, KeyboardLayoutInfo>? cachedLayouts;

    protected override void OnEnable()
    {
        // 🔴 這裡刻意什麼都不寫回設定檔。以前是「0 一律補成目前配置並存檔」，
        //    那等於把啟用當下的配置烙死，之後每次 focus／unfocus 都強制切回去
        //    ——與 Description 承諾的「預設不切換」相反，而且沒有回到未設定的路。
        //    0 現在是持久的哨兵，由 SwitchTo 判掉。
        if (!Svc.SigScanner.TryScanText(SetTextInputTargetSignature, out var address) || address == nint.Zero)
        {
            Svc.Log.Information(
                $"[{InternalName}] 找不到「設定文字輸入焦點」函式的特徵碼，本模組不會生效（不影響其他模組）。" +
                "這通常代表台服主程式改版、特徵碼需要更新。");
            return;
        }

        hook = Svc.Hooks.HookFromAddress<SetTextInputTargetDelegate>(address, Detour);
        hook.Enable();
    }

    protected override void OnDisable()
    {
        hook?.Dispose();
        hook = null;
    }

    private void Detour(AtkComponentTextInput* component, AtkEventType eventType, int eventParam,
                        AtkEvent* atkEvent, AtkEventData* atkEventData)
    {
        // 🔴 快照一次到區域變數：OnDisable() 會把欄位設回 null，而 detour 可能還在執行中。
        var hook = this.hook;
        if (hook == null)
        {
            Svc.Log.Information($"[{InternalName}] hook 已在呼叫途中被卸載，略過本次。");
            return;
        }

        hook.OriginalDisposeSafe(component, eventType, eventParam, atkEvent, atkEventData);

        try
        {
            // 兩邊相同（含兩邊都是 0）＝ 使用者要的就是「不切換」，這裡直接收工。
            // 🔑 這一條同時救回既有使用者：舊版的烙印一定是把兩欄設成同一個值。
            if (Config.FocusLayoutLangID == Config.UnfocusLayoutLangID) return;

            switch (eventType)
            {
                case AtkEventType.FocusStart:
                    // 🔴 同步處理：此刻 component 必定有效，不存到下一幀。
                    if (!CurrentTextStartsWithSlash(component))
                        SwitchTo(Config.FocusLayoutLangID);
                    break;

                case AtkEventType.FocusStop:
                    SwitchTo(Config.UnfocusLayoutLangID);
                    break;
            }
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, $"[{InternalName}] 切換鍵盤配置失敗，本次略過");
        }
    }

    /// <summary>輸入框目前的文字是不是斜線指令（<c>/</c> 開頭）。指標同步讀取，不跨幀。</summary>
    private static bool CurrentTextStartsWithSlash(AtkComponentTextInput* component)
    {
        if (component == null) return false;
        var textNode = component->AtkTextNode;
        if (textNode == null) return false;
        var text = textNode->NodeText.ToString();
        return !string.IsNullOrEmpty(text) && text.StartsWith('/');
    }

    /// <summary>切到指定配置。<c>0</c>＝未設定，不動作；已經在該配置上也不動作。</summary>
    private static void SwitchTo(ushort langId)
    {
        // 0 是「未設定」的哨兵，永遠不動作（Description 承諾的預設行為就是這個）。
        if (langId == 0) return;

        // 已經在這個配置上就不要再送一次切換：那是對使用者當下狀態的無謂干預。
        if (InputMethod.CurrentLangId() == langId) return;

        var hkl = InputMethod.FindLayout(langId);
        if (hkl != nint.Zero)
            InputMethod.SwitchToLayout(hkl);
    }

    public override void DrawConfig()
    {
        ImGui.TextDisabled("兩邊挑一樣（或任一邊挑「（不切換）」）＝完全不動作。");
        ImGui.TextDisabled("常見用法：輸入配置挑注音／中文，操作配置挑英數。");

        if (Throttle.Pass("AutoChangeKeyboardLayout-Refresh", 1000))
            cachedLayouts = InputMethod.GetAllKeyboardLayouts();

        var layouts = cachedLayouts;
        if (layouts == null || layouts.Count == 0)
        {
            ImGui.TextDisabled("讀不到系統已安裝的鍵盤配置。");
            return;
        }

        DrawLayoutCombo("輸入用配置（焦點在輸入框時）", "##FocusLayout", layouts, ref Config.FocusLayoutLangID);
        ImGui.Spacing();
        DrawLayoutCombo("操作用配置（離開輸入框時）", "##UnfocusLayout", layouts, ref Config.UnfocusLayoutLangID);

        ImGui.NewLine();
        var currentId = InputMethod.CurrentLangId();
        var currentName = layouts.TryGetValue(currentId, out var info) ? info.Name : "（未知）";
        ImGui.TextDisabled($"目前系統配置：{currentName}");
    }

    /// <summary>「不切換」在下拉選單裡的顯示名（對應哨兵值 <c>0</c>）。</summary>
    private const string NoSwitchLabel = "（不切換）";

    private void DrawLayoutCombo(string label, string id,
                                 Dictionary<ushort, KeyboardLayoutInfo> layouts, ref ushort selected)
    {
        ImGui.Text(label);

        var currentName = selected == 0
                              ? NoSwitchLabel
                              : layouts.TryGetValue(selected, out var info) ? info.Name : "（未知）";

        using var combo = ImRaii.Combo(id, currentName);
        if (!combo) return;

        // 🔴 這個選項必須存在：已經被舊版烙上具體配置的使用者，沒有它就沒有路回到「未設定」
        //    （改預設值對既有設定檔一律無效——JSON 有那個鍵就會覆蓋欄位初始值）。
        var noneSelected = selected == 0;
        if (ImGui.Selectable(NoSwitchLabel, noneSelected))
        {
            selected = 0;
            Plugin.Instance.Config.Save();
        }
        if (noneSelected) ImGui.SetItemDefaultFocus();

        foreach (var (langId, layout) in layouts)
        {
            var isSelected = langId == selected;
            if (ImGui.Selectable(layout.Name, isSelected))
            {
                selected = langId;
                Plugin.Instance.Config.Save();
            }
            if (isSelected) ImGui.SetItemDefaultFocus();
        }
    }

    private readonly struct KeyboardLayoutInfo(nint handle, string name, ushort langId)
    {
        public nint Handle { get; } = handle;
        public string Name { get; } = name;
        public ushort LangId { get; } = langId;
    }

    /// <summary>Win32 鍵盤配置查詢與切換。</summary>
    private static class InputMethod
    {
        [DllImport("user32.dll")]
        private static extern void ActivateKeyboardLayout(nint hkl, uint flags);

        [DllImport("user32.dll")]
        private static extern nint GetKeyboardLayout(uint idThread);

        [DllImport("user32.dll")]
        private static extern int GetKeyboardLayoutList(int nBuff, nint[]? lpList);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern nint LoadKeyboardLayout(string pwszKlid, uint flags);

        public static ushort CurrentLangId() => (ushort)(GetKeyboardLayout(0).ToInt64() & 0xFFFF);

        public static Dictionary<ushort, KeyboardLayoutInfo> GetAllKeyboardLayouts()
        {
            var result = new Dictionary<ushort, KeyboardLayoutInfo>();
            var count = GetKeyboardLayoutList(0, null);
            if (count <= 0) return result;

            var handles = new nint[count];
            if (GetKeyboardLayoutList(count, handles) == 0) return result;

            foreach (var handle in handles)
            {
                var langId = (ushort)(handle.ToInt64() & 0xFFFF);
                result[langId] = new KeyboardLayoutInfo(handle, GetLayoutDisplayName(langId), langId);
            }
            return result;
        }

        private static string GetLayoutDisplayName(ushort langId)
        {
            try
            {
                return new CultureInfo(langId).DisplayName;
            }
            catch
            {
                return $"0x{langId:X4}";
            }
        }

        public static nint FindLayout(ushort langId)
        {
            var count = GetKeyboardLayoutList(0, null);
            if (count > 0)
            {
                var handles = new nint[count];
                if (GetKeyboardLayoutList(count, handles) != 0)
                {
                    foreach (var handle in handles)
                        if ((ushort)(handle.ToInt64() & 0xFFFF) == langId)
                            return handle;
                }
            }
            // 沒載入的話嘗試載入（1 = KLF_ACTIVATE）。
            return LoadKeyboardLayout($"{langId:X8}", 1u);
        }

        public static void SwitchToLayout(nint hkl)
        {
            try
            {
                if (CurrentLangId() != (ushort)(hkl.ToInt64() & 0xFFFF))
                    ActivateKeyboardLayout(hkl, 0u);
            }
            catch
            {
                // Win32 呼叫失敗只是切不了配置，不影響遊戲。
            }
        }
    }
}

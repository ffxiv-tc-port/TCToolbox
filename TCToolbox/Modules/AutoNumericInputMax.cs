using System;
using System.Collections.Generic;
using Dalamud.Bindings.ImGui;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 數字輸入框放大上限：把上限卡在 99 的數字輸入框（拆疊、購買數量、繳交數量…）放寬到你設定的上限，
/// 讓你能直接<b>輸入</b>更大的數字，不必一次一次點加號。可選擇順便把值預先填到上限。
/// 機制：hook 遊戲的數字輸入元件更新函式，只在該框「上限還是預設的 99」時把上限改成設定值。
/// 不寫死其他上限、不做 code patch。
/// 參考 DailyRoutines AutoNumericInputMax 設計重寫（API13、無 OmenTools 相依）。
/// </summary>
/// <remarks>
/// 🔴 <b>與 DR 原版的關鍵差異：只在上限＝99 時動作，動一次就收手。</b>DR 的判斷是「上限不等於 1
/// 就介入」，於是每次更新（每 250ms）都把值重設成上限——你想輸入比上限小的數字會被一直重設回去，
/// 所以 DR 必須另外做一個「按住某鍵暫停」的機制才能用。這裡只認「上限還是 99」的框：把上限抬成設定值
/// 之後，該框上限已不是 99，後續更新一律略過，<b>絕不再跟你的輸入打架</b>，也就不需要暫停鍵。
/// <para>
/// 🔴 <b>預設只放寬上限、不自動填值</b>（<see cref="AutoNumericInputMaxConfig.AutoFillToMax"/> 預設
/// <c>false</c>）：自動把數量填到最大是誤買／誤丟的地雷，要的人再自己打開。
/// </para>
/// <para>
/// 🔴 解不到特徵碼＝停用並記一筆 Information，不讓位址回 0 之後照樣去 hook。
/// </para>
/// <para>
/// 📌 黑名單（售價、搜尋價格、招募條件、倒數設定、系統設定）沿用 DR：這些框的「上限」有其意義，
/// 不該被放大。addon 內部名稱與伺服器地區無關。
/// </para>
/// </remarks>
public sealed unsafe class AutoNumericInputMax : TcModule
{
    public override string InternalName => "AutoNumericInputMax";
    public override string DisplayName => "數字輸入框放大上限";

    public override string Description =>
        "把上限卡在 99 的數字輸入框放寬到設定的上限，讓你能直接輸入更大的數字。" +
        "預設只放寬上限、不自動填值；售價／搜尋等輸入框排除在外。";

    public override ModuleCategory Category => ModuleCategory.Misc;

    public override bool HasConfigUI => true;

    /// <summary>遊戲的數字輸入元件更新函式（sig 已對台服 7.20 主程式離線驗證，唯一命中 0x14068A030）。</summary>
    private const string UldUpdateSignature =
        "40 53 48 83 EC ?? 48 8B D9 48 83 C1 ?? E8 ?? ?? ?? ?? 80 BB ?? ?? ?? ?? ?? 74 ?? 48 8B CB";

    /// <summary>這些 addon 取得焦點時完全不介入（售價／搜尋價格／招募條件／倒數／系統設定）。</summary>
    private static readonly HashSet<string> BlacklistAddons = new(StringComparer.Ordinal)
    {
        "RetainerSell", "ItemSearch", "LookingForGroupSearch", "LookingForGroupCondition",
        "CountDownSettingDialog", "ConfigSystem", "ConfigCharacter",
    };

    /// <summary>遊戲數字輸入框的預設上限；只認這個值的框才放大，放大後不再是 99 便自動收手。</summary>
    private const int DefaultCap = 99;

    private delegate nint UldUpdateDelegate(AtkComponentNumericInput* component);

    private Hook<UldUpdateDelegate>? hook;

    private bool isBlocked;

    private AutoNumericInputMaxConfig Config => Plugin.Instance.Config.NumericInputMax;

    protected override void OnEnable()
    {
        if (!Svc.SigScanner.TryScanText(UldUpdateSignature, out var address) || address == nint.Zero)
        {
            Svc.Log.Information(
                $"[{InternalName}] 找不到數字輸入元件更新函式的特徵碼，本模組不會生效（不影響其他模組）。" +
                "這通常代表台服主程式改版、特徵碼需要更新。");
            return;
        }

        hook = Svc.Hooks.HookFromAddress<UldUpdateDelegate>(address, Detour);
        hook.Enable();
    }

    protected override void OnDisable()
    {
        hook?.Dispose();
        hook = null;
    }

    private nint Detour(AtkComponentNumericInput* component)
    {
        // 🔴 快照到區域變數：OnDisable() 會把欄位設回 null，而 detour 可能還在執行中。
        var hook = this.hook;
        if (hook == null) return nint.Zero;

        var result = hook.OriginalDisposeSafe(component);

        try
        {
            AdjustIfNeeded(component);
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, $"[{InternalName}] 調整數字輸入上限失敗，本次略過");
        }

        return result;
    }

    private void AdjustIfNeeded(AtkComponentNumericInput* component)
    {
        if (component == null) return;

        // 每 5 秒重算一次「目前焦點是不是落在黑名單 addon」。
        if (Throttle.Pass("AutoNumericInputMax-Blacklist", 5000))
            isBlocked = IsFocusBlacklisted();
        if (isBlocked) return;

        // 同一個框 250ms 內只處理一次（key 用指標位址；只做限流用途，不跨幀解參考）。
        if (!Throttle.Pass($"AutoNumericInputMax-{(nint)component:X}", 250)) return;

        var node = component->AtkResNode;
        if (node == null || !node->IsVisible()) return;

        // 只認「上限還是預設 99」的框：放大後上限已非 99，下次更新自動略過，不與使用者輸入打架。
        if (component->Data.Max != DefaultCap) return;

        var target = Math.Clamp(Config.MaxValue, DefaultCap + 1, 9999);
        component->Data.Max = target;

        if (Config.AutoFillToMax)
        {
            component->InnerSetValue(target, triggerCallback: true, playSoundEffect: false);
            component->Value = target;
            if (component->AtkTextNode != null)
                component->AtkTextNode->SetNumber(target);
        }
    }

    private static bool IsFocusBlacklisted()
    {
        var manager = RaptureAtkUnitManager.Instance();
        if (manager == null) return false;

        var entries = manager->FocusedUnitsList.Entries;
        for (var i = 0; i < entries.Length; i++)
        {
            var addon = entries[i].Value;
            if (addon == null) continue;
            var name = addon->NameString;
            if (!string.IsNullOrWhiteSpace(name) && BlacklistAddons.Contains(name))
                return true;
        }
        return false;
    }

    public override void DrawConfig()
    {
        ImGui.TextDisabled("只放大原本上限＝99 的輸入框，動一次就收手，不會一直改回去。");

        var maxValue = Config.MaxValue;
        ImGui.SetNextItemWidth(120f);
        if (ImGui.InputInt("放寬後的上限", ref maxValue))
        {
            Config.MaxValue = Math.Clamp(maxValue, DefaultCap + 1, 9999);
            Plugin.Instance.Config.Save();
        }
        ImGui.SameLine();
        ImGui.TextDisabled($"(範圍 {DefaultCap + 1}–9999)");

        var autoFill = Config.AutoFillToMax;
        if (ImGui.Checkbox("順便把值預先填到上限", ref autoFill))
        {
            Config.AutoFillToMax = autoFill;
            Plugin.Instance.Config.Save();
        }
        ImGui.SameLine();
        ImGui.TextColored(new System.Numerics.Vector4(1f, 0.75f, 0.4f, 1f), "(誤買／誤丟風險，預設關)");
    }
}

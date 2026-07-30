using System;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.System.Input;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 長按快捷欄按鍵時自動轉為固定間隔重複觸發。
/// 機制：hook 遊戲自己的「快捷欄輸入處理」函式當作作用範圍閘門，並在該範圍內
/// hook <c>InputData::IsInputIdPressed</c>，把「按住」翻譯成週期性的「剛按下」。
/// 不寫入任何遊戲記憶體、不模擬按鍵訊息、不繞過任何冷卻或佇列判定——遊戲照原本
/// 的流程處理每一次觸發。
/// 參考 DailyRoutines AutoConstantlyClick 設計重寫（API13、無 OmenTools 相依）。
/// </summary>
public sealed unsafe class AutoConstantlyClick : TcModule
{
    public override string InternalName => "AutoConstantlyClick";
    public override string DisplayName => "自動重複點擊";

    public override string Description =>
        "長按任一快捷欄按鍵（滑鼠點住或鍵盤按住）時，自動以設定的間隔重複觸發該按鍵，不必連續手動點擊。" +
        "只在遊戲處理快捷欄輸入的期間生效，其他按鍵與介面操作完全不受影響。";

    public override bool HasConfigUI => true;

    /// <summary>遊戲的快捷欄輸入處理函式（sig 已對台服 7.20 主程式離線驗證，唯一命中）。</summary>
    private const string CheckHotbarClickedSignature =
        "E8 ?? ?? ?? ?? 48 8B 4F ?? 48 8B 01 FF 50 ?? 48 8B C8 E8 ?? ?? ?? ?? 84 C0 74";

    private delegate void CheckHotbarClickedDelegate(nint a1, byte a2);

    [return: MarshalAs(UnmanagedType.U1)]
    private delegate bool IsInputIdPressedDelegate(InputData* data, InputId id);

    private Hook<CheckHotbarClickedDelegate>? checkHotbarClickedHook;
    private Hook<IsInputIdPressedDelegate>? isInputIdPressedHook;

    private const int InputIdCount = 512;

    private readonly long[] lastFireTick = new long[InputIdCount];
    private readonly bool[] repeating = new bool[InputIdCount];

    /// <summary>只有在遊戲的快捷欄輸入處理範圍內才改寫查詢結果。</summary>
    private bool inHotbarInputHandler;

    private AutoConstantlyClickConfig Config => Plugin.Instance.Config.ConstantlyClick;

    protected override void OnEnable()
    {
        var checkHotbarClicked = Svc.SigScanner.ScanText(CheckHotbarClickedSignature);

        checkHotbarClickedHook = Svc.Hooks.HookFromAddress<CheckHotbarClickedDelegate>(
            checkHotbarClicked, CheckHotbarClickedDetour);
        isInputIdPressedHook = Svc.Hooks.HookFromAddress<IsInputIdPressedDelegate>(
            InputData.Addresses.IsInputIdPressed.Value, IsInputIdPressedDetour);

        checkHotbarClickedHook.Enable();
        isInputIdPressedHook.Enable();
    }

    protected override void OnDisable()
    {
        checkHotbarClickedHook?.Dispose();
        checkHotbarClickedHook = null;

        isInputIdPressedHook?.Dispose();
        isInputIdPressedHook = null;

        inHotbarInputHandler = false;
        Array.Clear(lastFireTick);
        Array.Clear(repeating);
    }

    private void CheckHotbarClickedDetour(nint a1, byte a2)
    {
        inHotbarInputHandler = true;
        try
        {
            checkHotbarClickedHook!.Original(a1, a2);
        }
        finally
        {
            inHotbarInputHandler = false;
        }
    }

    private bool IsInputIdPressedDetour(InputData* data, InputId id)
    {
        var original = isInputIdPressedHook!.Original(data, id);

        try
        {
            if (!inHotbarInputHandler) return original;
            if (id is < InputId.HOTBAR_UP or > InputId.HOTBAR_CONTENTS_ACT_R) return original;

            var index = (int)id;
            if (index is < 0 or >= InputIdCount) return original;

            // 按鍵已放開：清狀態，恢復原本行為
            if (!data->IsInputIdDown(id))
            {
                repeating[index] = false;
                lastFireTick[index] = 0;
                return original;
            }

            var now = Environment.TickCount64;

            // 真正的第一次按下：照原樣觸發，並開始計時
            if (original)
            {
                repeating[index] = true;
                lastFireTick[index] = now;
                return true;
            }

            // 按住期間：每滿一個間隔就回報一次「剛按下」
            if (!repeating[index]) return false;
            if (now - lastFireTick[index] < Config.RepeatIntervalMs) return false;

            lastFireTick[index] = now;
            return true;
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, $"[{InternalName}] 輸入判定改寫失敗，本次回退原始結果");
            return original;
        }
    }

    public override void DrawConfig()
    {
        ImGui.SetNextItemWidth(200f);
        var interval = Config.RepeatIntervalMs;
        if (ImGui.SliderInt("重複間隔（毫秒）", ref interval, 100, 1_000))
        {
            Config.RepeatIntervalMs = interval;
            Plugin.Instance.Config.Save();
        }

        ImGui.TextDisabled("手把（L2／R2 觸發）模式未移植：需要額外 hook 手把輪詢並改寫按鍵位元，\n" +
                           "風險與收益不成比例。鍵盤／滑鼠的快捷欄按鍵已全部涵蓋。");
    }
}

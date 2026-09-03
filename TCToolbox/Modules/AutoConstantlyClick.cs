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
/// <remarks>
/// 📌 <b>鍵盤／滑鼠與手把是兩個完全獨立的處理函式</b>（2026-08-19 對台服 7.20 主程式離線反組譯確認）：
/// <list type="bullet">
/// <item><c>0x140748090</c>＝鍵盤／滑鼠快捷欄。它查詢的 <c>InputId</c> 是 45–56（上下捲動與切換組）、
/// 57–68（快捷欄 1）、69–176（快捷欄 2–10）、177–188（擴充快捷欄）、189／190（副本專用動作）、
/// 447（切換擴充組）。<b>整段完全不碰 191 以上的十字熱鍵</b>。</item>
/// <item><c>0x1407484F0</c>＝手把十字熱鍵。它查的是 191／192（L2／R2）、193（切換組）、
/// 194–201（八個方向鍵）、202–218（展開後的左右擴充組）。</item>
/// </list>
/// 也就是說<b>只把第一個 hook 的 InputId 範圍放寬到十字熱鍵是沒有用的</b>——那些 id 根本不會在
/// 第一個函式的執行期間被查詢，範圍閘門永遠不成立，功能會靜默地什麼都不做。
/// 手把支援必須是「多掛一個範圍閘門」，這就是本模組現在的做法。
/// <para>
/// 🔴 <b>刻意不採用 PandorasBox <c>TurboController</c> 的做法。</b>那支 hook 手把輪詢函式，
/// 並在呼叫原函式前把 <c>GamepadInputData.Buttons</c> 裡對應的位元<b>減掉</b>。三個問題：
/// ①它用 <c>-=</c> 而不是清位元，位元本來就沒設時會<b>借位破壞其他按鍵</b>；
/// ②那支函式（台服 <c>0x1400BD0D0</c>，唯一命中）除了 <c>rcx</c> 之外還吃一個 <c>xmm1</c> 浮點參數，
/// 而上游的委派宣告成 <c>int(IntPtr)</c>——<b>呼叫原函式時 xmm1 是託管碼留下的垃圾</b>；
/// ③它改寫的是輸入資料本身，作用範圍是<b>整個遊戲的所有手把按鍵</b>，不只是快捷欄。
/// 本模組的做法只在十字熱鍵處理函式的執行期間改寫一支查詢函式的回答，不寫入任何遊戲記憶體。
/// </para>
/// </remarks>
public sealed unsafe class AutoConstantlyClick : TcModule
{
    public override string InternalName => "AutoConstantlyClick";
    public override string DisplayName => "自動重複點擊";

    public override string Description =>
        "長按任一快捷欄按鍵（滑鼠點住或鍵盤按住）時，自動以設定的間隔重複觸發該按鍵，不必連續手動點擊。" +
        "另可選擇讓手把的十字熱鍵也一樣連發（預設關閉）。" +
        "只在遊戲處理快捷欄輸入的期間生效，其他按鍵與介面操作完全不受影響。";

    public override ModuleCategory Category => ModuleCategory.Misc;

    public override bool HasConfigUI => true;

    /// <summary>遊戲的快捷欄輸入處理函式（sig 已對台服 7.20 主程式離線驗證，唯一命中）。</summary>
    private const string CheckHotbarClickedSignature =
        "E8 ?? ?? ?? ?? 48 8B 4F ?? 48 8B 01 FF 50 ?? 48 8B C8 E8 ?? ?? ?? ?? 84 C0 74";

    /// <summary>
    /// 遊戲的<b>十字熱鍵</b>（手把）輸入處理函式。
    /// </summary>
    /// <remarks>
    /// 台服 7.20 離線驗證：<c>.text</c> 唯一命中 <c>0x1407484F0</c>，該位址是 <c>.pdata</c> 認可的
    /// 函式進入點（非鏈結延續區塊），並有 2 個 <c>call</c> 交叉引用
    /// （<c>0x140745FB5</c>／<c>0x140745FCD</c>）——不是被內聯掉的死碼。
    /// <para>
    /// ⚠️ 這條特徵碼刻意從函式序言取而不是從呼叫點取：它的兩個呼叫點在同一個函式裡且形狀相近，
    /// 用呼叫點取樣式就得賭哪一個先被掃到。
    /// </para>
    /// </remarks>
    private const string CheckPadHotbarClickedSignature =
        "89 54 24 10 48 89 4C 24 08 56 41 55 48 81 EC ?? ?? ?? ?? 48 8B F1 44 8B EA 48 8B 49 ?? " +
        "48 8B 01 FF 90 ?? ?? ?? ?? 48 8B C8 BA ?? ?? ?? ?? E8";

    /// <summary>
    /// 兩支快捷欄處理函式的第二個參數。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>是 16 位元的旗標遮罩，不是 <c>byte</c>。</b>兩邊的呼叫點都是
    /// <c>movzx eax, word ptr [reg+0x18]</c> → <c>mov edx, eax</c>（另一條路徑傳字面值 <c>0xFF</c>），
    /// 所以宣告成 <c>byte</c> 會把第 8–15 位元<b>靜默截掉</b>，而我們是把這個值原封不動轉交給原函式的——
    /// 截掉等於幫遊戲把它自己的參數改小。用 <c>uint</c> 收下：值域上限是 <c>0xFFFF</c>，
    /// 32 位元一定裝得下，來回不會有任何轉換。
    /// <para>📌 2026-08-19 離線反組譯發現並修正；在此之前這個委派宣告的是 <c>byte</c>。</para>
    /// </remarks>
    private delegate void CheckHotbarClickedDelegate(nint a1, uint a2);

    [return: MarshalAs(UnmanagedType.U1)]
    private delegate bool IsInputIdPressedDelegate(InputData* data, InputId id);

    private Hook<CheckHotbarClickedDelegate>? checkHotbarClickedHook;
    private Hook<CheckHotbarClickedDelegate>? checkPadHotbarClickedHook;
    private Hook<IsInputIdPressedDelegate>? isInputIdPressedHook;

    private const int InputIdCount = 512;

    /// <summary>十字熱鍵上「真的是動作格」的 InputId 範圍（含端點）。</summary>
    /// <remarks>
    /// 🔴 <b>起點刻意從 194（<c>HOT_PAD_LL</c>）算起，不是 191。</b>
    /// 191／192 是 L2／R2（叫出十字熱鍵的那兩個扳機）、193 是切換組——
    /// 把那三個做成連發，十字熱鍵會在按住期間不停開開關關或跳組，等於整個不能用。
    /// 終點 218（<c>HOT_PAD_RD_R</c>）是展開後右擴充組的最後一格；
    /// 219 以後是「直接跳到第 N 組」，同樣不該連發。
    /// </remarks>
    private const InputId PadHotbarFirst = InputId.HOT_PAD_LL;

    private const InputId PadHotbarLast = InputId.HOT_PAD_RD_R;

    private readonly long[] lastFireTick = new long[InputIdCount];
    private readonly bool[] repeating = new bool[InputIdCount];

    /// <summary>只有在遊戲的快捷欄輸入處理範圍內才改寫查詢結果。</summary>
    private bool inHotbarInputHandler;

    /// <summary>只有在遊戲的十字熱鍵輸入處理範圍內才改寫查詢結果。</summary>
    private bool inPadHotbarInputHandler;

    /// <summary>手把 hook 這一次啟用有沒有掛上（特徵碼失配時為 false，鍵盤功能不受影響）。</summary>
    private bool padHookAttached;

    /// <summary>十字熱鍵處理函式被進入過幾次（本次啟用內）。</summary>
    /// <remarks>
    /// 📌 這是「hook 到底有沒有在跑」的唯一證據，直接畫在設定畫面上。
    /// 沒有它的話，手把連發沒作用時分不出「hook 沒掛上」「掛上了但遊戲沒走這條路」
    /// 「走了但沒到重複間隔」三種情形。
    /// </remarks>
    private long padHandlerEntries;

    private long keyboardRepeatsFired;

    private long padRepeatsFired;

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

        // 🔴 手把那一支單獨包 try：特徵碼失配時只該讓「手把連發」不能用，
        //    不該把整個模組（鍵盤／滑鼠那半）一起拖下水。
        //    TcModule.Enable() 會把例外吃掉並讓 IsEnabled 維持 false ——
        //    也就是說在這裡放掉例外＝鍵盤功能連同一起失效，那是回退既有行為。
        try
        {
            var checkPadHotbarClicked = Svc.SigScanner.ScanText(CheckPadHotbarClickedSignature);

            checkPadHotbarClickedHook = Svc.Hooks.HookFromAddress<CheckHotbarClickedDelegate>(
                checkPadHotbarClicked, CheckPadHotbarClickedDetour);
            checkPadHotbarClickedHook.Enable();
            padHookAttached = true;

            Svc.Log.Information(
                $"[{InternalName}] 十字熱鍵輸入處理 hook 已掛上（0x{checkPadHotbarClicked:X}）。");
        }
        catch (Exception ex)
        {
            padHookAttached = false;
            checkPadHotbarClickedHook = null;

            // Information 級：使用者跑 LogLevel 1，這行是「手把連發沒反應」唯一的線索。
            Svc.Log.Information(
                $"[{InternalName}] 十字熱鍵輸入處理函式的特徵碼對不上，手把連發本次無法使用" +
                $"（鍵盤／滑鼠不受影響）。原因：{ex.Message}");
        }
    }

    protected override void OnDisable()
    {
        checkHotbarClickedHook?.Dispose();
        checkHotbarClickedHook = null;

        checkPadHotbarClickedHook?.Dispose();
        checkPadHotbarClickedHook = null;

        isInputIdPressedHook?.Dispose();
        isInputIdPressedHook = null;

        inHotbarInputHandler = false;
        inPadHotbarInputHandler = false;
        padHookAttached = false;
        padHandlerEntries = 0;
        keyboardRepeatsFired = 0;
        padRepeatsFired = 0;
        Array.Clear(lastFireTick);
        Array.Clear(repeating);
    }

    private void CheckHotbarClickedDetour(nint a1, uint a2)
    {
        // 🔴 OnDisable() 會把 hook 欄位設回 null，而 detour 可能還在執行中（in-flight 呼叫）。
        //    `!.` 只是叫編譯器閉嘴，執行期照樣是裸解參考 —— 欄位一為 null 就把
        //    NullReferenceException 擲回原生呼叫端，而且原始函式完全沒被呼叫。
        //    快照一次到區域變數，之後只用區域變數，不要對欄位做第二次讀取。
        var hook = checkHotbarClickedHook;
        if (hook == null)
        {
            Svc.Log.Information(
                $"[{InternalName}] 快捷欄輸入 hook 已在呼叫途中被卸載，略過本次原始呼叫。");
            return;
        }

        inHotbarInputHandler = true;
        try
        {
            hook.OriginalDisposeSafe(a1, a2);
        }
        finally
        {
            inHotbarInputHandler = false;
        }
    }

    /// <summary>十字熱鍵（手把）輸入處理的範圍閘門。形狀與鍵盤那一支完全一樣。</summary>
    private void CheckPadHotbarClickedDetour(nint a1, uint a2)
    {
        var hook = checkPadHotbarClickedHook;
        if (hook == null)
        {
            Svc.Log.Information(
                $"[{InternalName}] 十字熱鍵輸入 hook 已在呼叫途中被卸載，略過本次原始呼叫。");
            return;
        }

        // 第一次進來寫一行，之後只累加計數——這行證明 hook 真的在跑，而且不會洗版。
        if (padHandlerEntries == 0)
            Svc.Log.Information($"[{InternalName}] 首次進入十字熱鍵輸入處理範圍。");

        padHandlerEntries++;

        inPadHotbarInputHandler = true;
        try
        {
            hook.OriginalDisposeSafe(a1, a2);
        }
        finally
        {
            inPadHotbarInputHandler = false;
        }
    }

    private bool IsInputIdPressedDetour(InputData* data, InputId id)
    {
        // 🔴 OnDisable() 會把 hook 欄位設回 null，而 detour 可能還在執行中（in-flight 呼叫）。
        //    `!.` 只是叫編譯器閉嘴，執行期照樣是裸解參考 —— 欄位一為 null 就把
        //    NullReferenceException 擲回原生呼叫端，而且原始函式完全沒被呼叫。
        //    快照一次到區域變數，之後只用區域變數，不要對欄位做第二次讀取。
        var hook = isInputIdPressedHook;
        if (hook == null)
        {
            // 拿不到原始答案時一律回報「沒按下」—— 這是唯一不會憑空捏造出一次輸入的答案。
            Svc.Log.Information(
                $"[{InternalName}] 輸入查詢 hook 已在呼叫途中被卸載，本次回報「未按下」。");
            return false;
        }

        var original = hook.OriginalDisposeSafe(data, id);

        try
        {
            var keyboardScope = inHotbarInputHandler
                                && id is >= InputId.HOTBAR_UP and <= InputId.HOTBAR_CONTENTS_ACT_R;

            var padScope = inPadHotbarInputHandler
                           && Config.IncludeGamepadHotbar
                           && id is >= PadHotbarFirst and <= PadHotbarLast;

            if (!keyboardScope && !padScope) return original;

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
            // 🔴 下限一定要壓在這個使用點，不能只靠 SliderInt 的範圍：
            //    slider 沒加 AlwaysClamp 時 Ctrl+點擊可以鍵入範圍外的值，設定檔手改也會持久生效
            //    （EzConfig 的既有鍵一律覆蓋欄位初始值）。RepeatIntervalMs 被設成 0 或負值時
            //    `now - lastFireTick[index] < 0` 恆為假 ⇒ 按住期間每一次查詢都回報「剛按下」
            //    ＝快捷欄動作以每幀頻率連發送向伺服器。
            //    下限值對齊艦隊慣例（AutoMerge 的 Math.Max(50,…)、TradeAllCollectables 的 Math.Max(100,…)）。
            if (!repeating[index]) return false;
            if (now - lastFireTick[index] < Math.Max(100, Config.RepeatIntervalMs)) return false;

            lastFireTick[index] = now;

            if (padScope) padRepeatsFired++;
            else keyboardRepeatsFired++;

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
            // 寫回前夾擠（slider 可以 Ctrl+點擊鍵入範圍外的值）。
            // ⚙ 這只是第二道：已經落盤的壞值只有使用點的 Math.Max 救得到。
            Config.RepeatIntervalMs = Math.Clamp(interval, 100, 1_000);
            Plugin.Instance.Config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("鍵盤／滑鼠與手把共用同一個間隔。");

        ImGui.Spacing();

        var includePad = Config.IncludeGamepadHotbar;
        if (ImGui.Checkbox("手把十字熱鍵也連發", ref includePad))
        {
            Config.IncludeGamepadHotbar = includePad;
            Plugin.Instance.Config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "十字熱鍵的八個方向鍵，以及展開後的左右擴充組（共 25 格）。\n" +
                "L2／R2 扳機與「切換組」刻意不含在內：那三個連發會讓十字熱鍵在按住期間不停開關或跳組。\n" +
                "⚠️ 預設關閉。打開之後只影響十字熱鍵，其他手把操作（移動、選單、鏡頭）完全不變。");

        ImGui.TextDisabled("狀態：");
        ImGui.SameLine();

        if (!IsEnabled)
        {
            ImGui.TextDisabled("模組未啟用");
        }
        else if (!padHookAttached)
        {
            // 「不知道」與「壞掉」都要在看得見的地方講清楚，不要只寫進記錄。
            ImGui.TextDisabled("手把處理函式的特徵碼對不上，手把連發不可用（鍵盤／滑鼠正常）");
        }
        else
        {
            ImGui.TextDisabled(
                $"十字熱鍵處理進入 {padHandlerEntries} 次　" +
                $"觸發重複：鍵盤 {keyboardRepeatsFired} 次／手把 {padRepeatsFired} 次");
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "計數從模組啟用那一刻起算，停用時歸零。\n" +
                "「十字熱鍵處理進入」一直是 0＝遊戲根本沒走十字熱鍵那條路（沒接手把，或十字熱鍵沒叫出來）。\n" +
                "有進入次數但手把重複一直是 0＝按住的時間還沒到一個重複間隔，或按的不是動作格。");
    }
}

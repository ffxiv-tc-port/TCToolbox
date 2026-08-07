using System;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 任務受理視窗（<c>JournalAccept</c>）出現後自動按下「接受」。
/// 機制：每幀確認視窗狀態，取得 addon 上 id 44 的按鈕元件，按鈕本身是啟用狀態才複用它自己的
/// 事件送出點擊（<see cref="UiHelper.ClickButton"/>）。零 hook、零特徵碼、不寫記憶體。
/// </summary>
/// <remarks>
/// 🔴 <b>刻意不抄 DailyRoutines 的寫法</b>：DR 讀 <c>addon-&gt;AtkValues[261].UInt</c> 當任務 ID
/// 再 <c>Callback(3, id)</c>。<c>AtkValues</c> 是原生陣列<b>沒有邊界檢查</b>，261 這種裸索引
/// 只要遊戲改版縮短了值表就是任意記憶體讀取——正是全艦隊踩過好幾次的形狀。
/// 這裡改用 TextAdvance 已經實機驗過的按鈕 id 44（<c>TextAdvance/Executors/ExecQuestAccept.cs</c>），
/// 走的是「使用者按下那顆按鈕」同一條路徑。
/// <para>
/// ⚠️ 與 TextAdvance 的「自動接受任務」功能重疊。兩邊同時開著不會壞（按鈕按完視窗就關），
/// 但沒有必要，擇一即可。
/// </para>
/// </remarks>
public sealed unsafe class AutoQuestAccept : TcModule
{
    public override string InternalName => "AutoQuestAccept";
    public override string DisplayName => "自動接受任務";

    public override string Description =>
        "任務受理視窗出現後自動按下「接受」。走按鈕元件本身的點擊事件，不解析任務資料、不送封包。" +
        "⚠️ 開啟後連你只是想先看一下的任務也會被接下；與 TextAdvance 的同名功能重疊，擇一開啟即可。";

    public override ModuleCategory Category => ModuleCategory.Misc;

    public override bool HasConfigUI => true;

    private const string AddonName = "JournalAccept";

    /// <summary>「接受」按鈕的元件 id（與 TextAdvance <c>ExecQuestAccept</c> 相同）。</summary>
    private const uint AcceptButtonId = 44;

    /// <summary>目前這扇視窗是什麼時候開始可以按的；視窗關閉時清空。</summary>
    private DateTime? visibleSince;

    private AutoQuestAcceptConfig Config => Plugin.Instance.Config.QuestAccept;

    protected override void OnEnable()
    {
        visibleSince = null;
        Svc.Framework.Update += OnUpdate;
    }

    protected override void OnDisable()
    {
        Svc.Framework.Update -= OnUpdate;
        visibleSince = null;
    }

    private void OnUpdate(IFramework framework)
    {
        var addon = UiHelper.GetAddon(AddonName);
        if (!UiHelper.IsReady(addon))
        {
            visibleSince = null;
            return;
        }

        // 用「視窗開著多久」計時，不用節流器——節流器第一次呼叫必定放行，
        // 拿它當延遲會變成「立刻按下去」，設定滑桿等於沒作用。
        visibleSince ??= DateTime.UtcNow;
        if (DateTime.UtcNow - visibleSince.Value < TimeSpan.FromMilliseconds(Config.DelayMs)) return;

        var button = addon->GetComponentButtonById(AcceptButtonId);
        if (button == null) return;

        // 🔴 不要在這裡先讀 button->IsEnabled。CS 的 AtkComponentButton.IsEnabled 是
        // `AtkComponentBase.OwnerNode->AtkResNode.NodeFlags.HasFlag(...)`——它解的是 +0xA8 的
        // OwnerNode（不是 +0xA0 的 AtkResNode），而且對它**零 null 檢查**，先讀等於自己開一個
        // 存取違規的口子（AVE 是 .NET Core 的 corrupted-state exception，try/catch 攔不到）。
        // 「能不能按」一律交給 UiHelper.ClickButton：它先驗 addon／button／OwnerNode 才讀
        // IsEnabled，回 false 就代表「現在按不動」，正好是我們原本要的分支條件。
        if (!UiHelper.ClickButton(addon, button)) return;

        // 按下去之後視窗就會關掉；萬一沒關（按鈕被遊戲重新啟用）也不要每幀重按。
        visibleSince = DateTime.UtcNow;

        if (Throttle.Pass("AutoQuestAccept-Log", 5_000))
            Svc.Log.Information($"[{InternalName}] 已自動接受任務（{AddonName} 按鈕 {AcceptButtonId}）。");
    }

    public override void DrawConfig()
    {
        ImGui.SetNextItemWidth(180f);
        var delay = Config.DelayMs;
        if (ImGui.SliderInt("接受前的延遲（毫秒）", ref delay, 0, 3_000))
        {
            Config.DelayMs = delay;
            Plugin.Instance.Config.Save();
        }

        using (ImRaii.PushIndent())
            ImGui.TextDisabled("視窗出現後至少等這麼久才按，讓你有機會先看清楚內容或自己關掉。");
    }
}

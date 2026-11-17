using Dalamud.Bindings.ImGui;

namespace TCToolbox.Core;

/// <summary>手動步進器（使用者按了才一步一步跑的模組）設定畫面的共用區塊。</summary>
internal static class ManualStepUi
{
    /// <summary>間隔滑桿＋「結束時在聊天欄報告」勾選＋上次結果；改了才寫回並存檔。</summary>
    /// <remarks>
    /// 兩個 ref 直接指向設定類別的欄位（不改設定類別的成員）。
    /// 標籤含 <c>##</c> 之後的 ImGui ID，由呼叫端原樣傳入，改字＝ImGui 狀態換一組。
    /// </remarks>
    public static void DrawStepSettings(ref int stepIntervalMs, ref bool notifyOnFinish,
                                        string intervalLabel, int min, int max,
                                        string notifyLabel, string lastSummary)
    {
        var interval = stepIntervalMs;
        if (ImGui.SliderInt(intervalLabel, ref interval, min, max))
        {
            stepIntervalMs = interval;
            Plugin.Instance.Config.Save();
        }

        var notify = notifyOnFinish;
        if (ImGui.Checkbox(notifyLabel, ref notify))
        {
            notifyOnFinish = notify;
            Plugin.Instance.Config.Save();
        }

        if (lastSummary.Length > 0)
            ImGui.TextDisabled($"上次結果：{lastSummary}");
    }
}

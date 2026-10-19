namespace TCToolbox;

public sealed class AutoConstantlyClickConfig
{
    /// <summary>按住期間的重複觸發間隔（毫秒）。鍵盤／滑鼠與手把共用。</summary>
    public int RepeatIntervalMs = 200;

    /// <summary>
    /// 手把的十字熱鍵也套用連發。
    /// </summary>
    /// <remarks>
    /// 🔴 預設 <c>false</c>＝<b>與這個選項加入之前的行為完全一樣</b>。
    /// 舊設定檔沒有這個鍵，反序列化不會覆寫欄位初始值，所以升級上來的人不會突然多出一種行為。
    /// 📌 範圍只含十字熱鍵的動作格（<c>HOT_PAD_LL</c>–<c>HOT_PAD_RD_R</c>，194–218）。
    /// L2／R2 扳機（191／192）與切換組（193）刻意排除：那三個連發會讓十字熱鍵
    /// 在按住期間不停開關或跳組。
    /// </remarks>
    public bool IncludeGamepadHotbar;
}

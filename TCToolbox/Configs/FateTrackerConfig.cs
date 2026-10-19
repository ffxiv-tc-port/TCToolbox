using TCToolbox.Core;

namespace TCToolbox;

/// <summary>FATE 總覽與導航。</summary>
public sealed class FateTrackerConfig
{
    /// <summary>
    /// 導航時允許 vnavmesh 用飛行路徑。
    /// </summary>
    /// <remarks>
    /// 📌 預設 <c>false</c>（沿用既有 <see cref="TCToolbox.Core.ExternalNav.TryMoveTo"/> 呼叫端的保守值）。
    /// 開飛行在未解鎖飛行的區域會讓 vnavmesh 算不出路徑而整個導航失敗，
    /// 而失敗的樣子是「按了沒反應」，比走路慢一點難察覺得多。
    /// </remarks>
    public bool AllowFly;

    /// <summary>
    /// 清單依距離由近到遠排序。
    /// </summary>
    /// <remarks>
    /// 🔴 預設 <c>false</c>（依 FateId 的穩定順序）<b>是刻意的，不是偷懶</b>：
    /// 依距離排序時，玩家一移動列就會互換位置，而這個清單上每一列都有一顆會真的
    /// 讓角色跑起來的「導航」按鈕——列在游標下方跳動＝按錯目標。
    /// 要距離資訊的人看得到那一欄，不必靠排序。
    /// </remarks>
    public bool SortByDistance;

    /// <summary>連已結束／已失敗的 FATE 也顯示。</summary>
    /// <remarks>預設 <c>false</c>：結束的 FATE 幾秒後就會從表裡消失，留著只是讓清單抖動。</remarks>
    public bool ShowEnded;
}

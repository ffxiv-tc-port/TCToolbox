namespace TCToolbox.Core;

/// <summary>「Questionable 正在跑任務」與「AutoRetainer 多角色模式開著」撞在一起的判定結果。</summary>
/// <remarks>
/// 🔴 <b>零值是「問不到 Questionable」而不是「沒有衝突」。</b>
/// 還沒問過、或對方不在的時候，答案是「不知道」——把它畫成「沒事」就是在最需要提醒的那一刻沉默。
/// </remarks>
internal enum QuestConflictVerdict
{
    /// <summary>連 Questionable 都問不到（多半是沒安裝）。<b>沒有</b>衝突可談，但也不是「確定沒事」。</summary>
    QuestionableUnavailable = 0,

    /// <summary>Questionable 在，但沒有在跑任務——沒有東西會被中斷。</summary>
    NotRunning,

    /// <summary>Questionable 在跑，但沒裝 AutoRetainer——這個問題不存在。</summary>
    NoAutoRetainer,

    /// <summary>兩邊都問到了：Questionable 在跑，而多角色模式關著。</summary>
    Clear,

    /// <summary>Questionable 在跑，AutoRetainer 也在，<b>但問不出多角色模式</b>。可能正撞著，只是說不出來。</summary>
    Unknown,

    /// <summary>兩邊都問到了，而且確定撞在一起。</summary>
    Conflict,
}

/// <summary>一次判定的結果。</summary>
/// <param name="Verdict">結論。</param>
/// <param name="Detail">這個結論是怎麼來的（問了哪些端點、回了什麼）。給 tooltip 用，<b>永不為 null</b>。</param>
internal readonly record struct QuestConflictStatus(QuestConflictVerdict Verdict, string Detail)
{
    /// <summary>確定撞在一起。</summary>
    public bool HasConflict => Verdict == QuestConflictVerdict.Conflict;

    /// <summary>在跑任務，但問不出 AutoRetainer 那一側——「不知道」，不是「沒事」。</summary>
    public bool IsUnknown => Verdict == QuestConflictVerdict.Unknown;

    /// <summary>畫面上有沒有話要說（<see cref="HasConflict"/> 或 <see cref="IsUnknown"/>）。</summary>
    public bool WorthShowing => HasConflict || IsUnknown;
}

/// <summary>
/// 「兩個自動化同時開著」的判定：Questionable 正在跑任務，而 AutoRetainer 的多角色模式也開著。
/// </summary>
/// <remarks>
/// 🔴 <b>純顯示。</b>這裡不會、也不該去關掉任何一邊——兩邊都是使用者自己開的，
/// 而「哪一邊該讓」只有他知道（先跑完這段任務，還是先讓僱員輪一圈）。
/// 整個檔案只呼叫查詢型端點，一個 <c>Set</c>／<c>Stop</c> 都沒有。
/// ⚠️ <b>呼叫執行緒</b>：IPC 的實作跑在<b>呼叫端</b>的執行緒上，所以只在遊戲主執行緒呼叫
/// （框架更新或 ImGui 的 Draw，兩者是同一條執行緒）。
/// </remarks>
internal static class QuestAutomationConflict
{
    /// <summary>
    /// 衝突提示的內文。列上只放一句話，理由放這裡。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>純顯示。</b>本外掛不會替使用者關掉任何一邊。
    /// </remarks>
    public const string Explanation =
        "AutoRetainer 的多角色模式開著，而 Questionable 正在跑任務。\n"
        + "多角色模式輪到下一個角色時會把目前這個角色登出，Questionable 就停在半路，\n"
        + "而且它不會在新角色上自己接回去——回來之後要自己再按一次開始。\n"
        + "要跑完這段任務就先把多角色模式關掉；要讓 AutoRetainer 換角就先把 Questionable 停下來。\n"
        + "這個模組只顯示，不會替你關掉任何一邊。";

    /// <summary>「還沒問過」。<b>不是</b>「沒有衝突」。</summary>
    public static readonly QuestConflictStatus None =
        new(QuestConflictVerdict.QuestionableUnavailable, string.Empty);

    /// <summary>
    /// 現在有沒有撞在一起。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>沒裝 AutoRetainer 的人不該看到「？問不到」。</b>那不是「不知道」，
    /// 那是「這個問題不存在」——所以
    /// <see cref="QuestConflictVerdict.NoAutoRetainer"/> 與
    /// <see cref="QuestConflictVerdict.Unknown"/> 是兩個不同的結論。
    /// </remarks>
    public static QuestConflictStatus Evaluate()
    {
        if (!QuestionableIpc.TryIsRunning(out var running))
        {
            return new QuestConflictStatus(
                QuestConflictVerdict.QuestionableUnavailable,
                "Questionable.IsRunning 打不通（未安裝或未載入）。");
        }

        if (!running)
        {
            return new QuestConflictStatus(
                QuestConflictVerdict.NotRunning,
                "Questionable.IsRunning 回 false：目前沒有任務流程在跑。");
        }

        var multiMode = AutoRetainerIpc.GetMultiModeState(out var detail);

        var verdict = multiMode switch
        {
            AutoRetainerIpc.MultiModeState.On => QuestConflictVerdict.Conflict,
            AutoRetainerIpc.MultiModeState.Off => QuestConflictVerdict.Clear,
            AutoRetainerIpc.MultiModeState.NotInstalled => QuestConflictVerdict.NoAutoRetainer,
            _ => QuestConflictVerdict.Unknown,
        };

        return new QuestConflictStatus(verdict, detail);
    }

    /// <summary>列上那一句（所有顯示位置共用同一份措辭）。</summary>
    /// <remarks>
    /// 🔴 <see cref="QuestConflictVerdict.Unknown"/> 與
    /// <see cref="QuestConflictVerdict.QuestionableUnavailable"/> 的句子都以「？」開頭：
    /// 「不知道」本身要在列上看得見，tooltip 藏的是為什麼，不是有沒有問題。
    /// </remarks>
    public static string ShortText(QuestConflictVerdict verdict) => verdict switch
    {
        QuestConflictVerdict.Conflict => "撞在一起：AR 換角會把任務中斷在半路",
        QuestConflictVerdict.Unknown => "？ 任務在跑，但問不到 AR 的多角色模式",
        QuestConflictVerdict.Clear => "任務在跑，AR 多角色模式關著",
        QuestConflictVerdict.NoAutoRetainer => "任務在跑（沒裝 AutoRetainer，沒有衝突可談）",
        QuestConflictVerdict.NotRunning => "Questionable 沒有在跑任務",
        _ => "？ 問不到 Questionable（多半是沒安裝）",
    };

    /// <summary>tooltip 的完整說明（＝為什麼要在意 ＋ 這個答案是怎麼來的）。</summary>
    public static string Tooltip(QuestConflictStatus status)
    {
        var head = status.Verdict switch
        {
            QuestConflictVerdict.Conflict => Explanation,

            QuestConflictVerdict.Unknown =>
                "Questionable 正在跑任務，而 AutoRetainer 在——但問不出它的多角色模式開著沒有。\n"
                + "多角色模式若正開著，它換角色時會把 Questionable 中斷在半路。",

            QuestConflictVerdict.Clear =>
                "Questionable 在跑任務，而 AutoRetainer 的多角色模式關著——不會有人把角色登出。",

            QuestConflictVerdict.NoAutoRetainer =>
                "Questionable 在跑任務，而且沒有偵測到 AutoRetainer。\n"
                + "這不是「不知道」：沒有人會來換角色，所以沒有衝突可談。",

            QuestConflictVerdict.NotRunning =>
                "Questionable 沒有在跑任務，所以多角色模式開著也不會中斷什麼。",

            _ =>
                "問不到 Questionable（多半是沒安裝）。\n"
                + "「沒安裝」與「安裝了但沒在跑」是兩件事，所以這裡不畫成「沒有衝突」。",
        };

        return string.IsNullOrEmpty(status.Detail)
            ? head
            : head + "\n\n（判斷依據：" + status.Detail + "）";
    }
}

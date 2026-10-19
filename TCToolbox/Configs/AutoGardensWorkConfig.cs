using TCToolbox.Core;

namespace TCToolbox;

public sealed class AutoGardensWorkConfig
{
    /// <summary>播種用種子 ItemId。<b>同時就是「目標作物」</b>——收穫物由它推得，不必再設一次。</summary>
    /// <remarks>
    /// 🔑 種子 → 收穫作物的對應是從遊戲資料推出來的：
    /// <c>Item.AdditionalData</c>（種子）→ <c>GardeningSeed</c> 列 → 該列的 <c>Item</c> 欄。
    /// 所以「目標作物」不需要第二個設定項，多一個只會多一種對不起來的可能。
    /// 詳見 <see cref="TCToolbox.Core.GardenCropData"/>。
    /// </remarks>
    public uint SeedItemId;

    /// <summary>播種用土壤 ItemId。</summary>
    public uint SoilItemId;

    /// <summary>施肥用肥料 ItemId（預設 7767 魚粉）。</summary>
    public uint FertilizerItemId = 7767;

    // ── 「自動整理」一鍵批次的策略 ───────────────────────────────────────
    // 📌 這些只影響「自動整理」按鈕，不影響收穫／護理／施肥／播種四顆按鈕。

    /// <summary>目標作物成熟時怎麼處理。</summary>
    /// <remarks>📌 預設「收穫並重種」——這是這個功能存在的理由；沒有種子或土壤時會自動降級成只收穫。</remarks>
    public MatureCropPolicy TargetMaturePolicy = MatureCropPolicy.HarvestAndReplant;

    /// <summary>非目標作物（含辨識不出來的）成熟時怎麼處理。</summary>
    /// <remarks>
    /// 🔴 預設<b>跳過</b>。別的作物多半是刻意種在那裡的（長時間作物、雜交用的親本），
    /// 預設就把它收掉是不可回復的。
    /// </remarks>
    public MatureCropPolicy OtherMaturePolicy = MatureCropPolicy.Skip;

    /// <summary>施肥策略。</summary>
    /// <remarks>🔴 預設<b>不施肥</b>：肥料是消耗品，而且施肥對收穫時間的影響因人而異。</remarks>
    public FertilizePolicy FertilizeMode = FertilizePolicy.None;

    /// <summary>枯萎作物怎麼處理。</summary>
    /// <remarks>🔴 預設<b>不動</b>：「處理」是不可回復的，而枯萎的判定來自一句 Talk 文字。</remarks>
    public WitheredPolicy WitheredMode = WitheredPolicy.Leave;

    /// <summary>狀態不好的作物要不要護理。</summary>
    /// <remarks>📌 預設開啟：護理不消耗任何東西、不可能有壞處，而且是那個狀態唯一能改善它的動作。</remarks>
    public bool TendWhenNeeded = true;

    /// <summary>空地壟要不要播種目標作物。</summary>
    /// <remarks>📌 預設開啟，但沒選種子／土壤或背包沒貨時是安全的無操作。</remarks>
    public bool PlantWhenEmpty = true;

    // ── 無人值守重跑「自動整理」───────────────────────────

    /// <summary>站在自家園圃旁時，每隔一段時間自動跑一輪「自動整理」。</summary>
    /// <remarks>
    /// 📌 <b>預設開啟。</b>這是模組裡唯一一個「開著不按也會自己動」的路徑，
    /// 所以 <see cref="TCToolbox.Core.TcModule.IsManualTrigger"/> 會跟著這格走（開著就不再算「手動觸發」）。
    /// 🔴 <b>預設開啟之所以安全，是因為策略的預設本身是保守的。</b>
    /// 沒選種子時成熟作物一律走 <see cref="OtherMaturePolicy"/>（預設跳過）；施肥預設關、枯萎預設不動、播種沒存貨就是無操作
    /// ⇒ 一個完全沒設定過的人，這個迴圈能做的只有「護理」（不消耗東西、不可能有壞處）。
    /// ⚠️ 反過來說：<b>調過策略的人要知道那些策略從此會自己跑</b>，設定畫面上寫明了。
    /// </remarks>
    public bool AutoLoopEnabled = true;

    /// <summary>兩輪之間至少隔多久（秒）。</summary>
    /// <remarks>
    /// 📌 間隔是從<b>上一輪跑完那一刻</b>起算，不是從開始起算——
    /// 一座 3×8 的庭院跑完一輪本身就要幾十秒，從開始起算的話間隔調小一點就變成幾乎連續不斷地開選單。
    /// ⚠️ 下限 10 秒、上限 3600 秒，讀取時夾緊（舊設定檔裡的怪值不會讓迴圈每幀跑）。
    /// </remarks>
    public int AutoLoopIntervalSeconds = 60;

    // ── 在園圃之間自動走位 ───────────────────────────────

    /// <summary>批次處理時自己走到下一格園圃旁邊（需要 vnavmesh）。</summary>
    /// <remarks>
    /// 🔴 <b>預設關閉。</b>這是「角色會自己移動」的功能，升級上來的人行為必須完全不變——
    /// 要用的人自己去勾。
    /// 📌 只在<b>目前這張圖</b>裡走，不傳送、不跨區、不上坐騎（一律地面路線）。
    /// 走不到、逾時、卡住、或別的外掛正在移動角色時，這一輪就退回原本的行為
    /// （只處理站得到的那幾格），<b>不重試</b>。
    /// </remarks>
    public bool WalkBetweenPatches;

    /// <summary>「自動整理」跑完後在聊天視窗列出逐格的決定（記錄一律會寫，不受這格影響）。</summary>
    /// <remarks>⚠️ 預設<b>關閉</b>：一座 3×8 的庭院會刷 24 行。要查為什麼某一格沒被動到時再打開。</remarks>
    public bool AnnounceEachDecision;

    /// <summary>缺種子／土壤／肥料而有格子被跳過時，在聊天視窗提醒一次。</summary>
    /// <remarks>
    /// 🔴 兩道保險讓它不會變成噪音：<b>只在缺的東西變了的時候送</b>，
    /// 而且兩則之間至少隔 5 分鐘。關掉之後缺料仍然看得到（模組列與設定畫面照舊）。
    /// </remarks>
    public bool AnnounceMaterialShortage = true;
}

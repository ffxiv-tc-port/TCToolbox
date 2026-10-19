namespace TCToolbox;

/// <summary>「僱員：批次取回」要拿什麼當白名單。</summary>
public enum RetainerBatchRetrieveScope
{
    /// <summary>AllaganTools 清單，空字串＝完全不篩選（＝這個選項加入之前的行為）。</summary>
    FilterList = 0,

    /// <summary>作用中製作清單還缺、而且打算從僱員取回的那幾款；取夠就停。</summary>
    ActiveCraftShortage = 1,
}

/// <summary>「僱員：批次取回」的設定。</summary>
/// <remarks>
/// 📌 這個模組<b>預設關閉</b>（所有模組都是——啟用與否記在
/// <see cref="Configuration.EnabledModules"/>），所以既有使用者更新後不會多出任何行為。
/// 下面每一個預設值也都選「最保守的那一邊」。
/// </remarks>
public sealed class RetainerBatchRetrieveConfig
{
    /// <summary>每次取回之間的最短間隔（毫秒）。</summary>
    /// <remarks>
    /// 📌 提供端刻意不等結果、把節奏交給呼叫端，所以這個值就是實際節奏。
    /// 伺服器大約每 0.13 秒消化一格，預設 300ms 留了一倍以上餘裕。
    /// </remarks>
    public int StepIntervalMs { get; set; } = 300;

    /// <summary>連水晶頁一起取回。</summary>
    /// <remarks>
    /// 🔴 <b>預設關閉。</b>水晶是遊戲已知在「寄放」時一定會跳數量對話框的類別，
    /// 「取回」會不會也跳在這個客戶端上<b>沒有驗證過</b>——真的跳了而沒有人回答，
    /// 這一輪會停在那裡等到逾時。開這格的人要自己承擔那個未知。
    /// </remarks>
    public bool IncludeCrystals { get; set; }

    /// <summary>結束時在聊天欄報告結果（記錄一律會寫，不受這格影響）。</summary>
    public bool NotifyOnFinish { get; set; } = true;

    /// <summary>白名單來源。</summary>
    /// <remarks>
    /// 預設是 <see cref="RetainerBatchRetrieveScope.FilterList"/>（值 0）＝既有行為，
    /// 所以升級上來的人不會因為這個欄位多出任何改變。
    /// </remarks>
    public RetainerBatchRetrieveScope Scope { get; set; }

    /// <summary>選中的 AllaganTools 搜尋清單 key；空字串＝不篩選，整批取回。</summary>
    /// <remarks>📌 <see cref="Scope"/> 是缺料模式時這個欄位不生效，但刻意保留不清空——換回來就還在。</remarks>
    public string FilterKey { get; set; } = string.Empty;

    /// <summary>改用手打的清單名稱／key（下拉只列得出搜尋清單，排序清單要靠這個）。</summary>
    public bool UseCustomFilterName { get; set; }

    /// <summary>手打的清單名稱或 key。</summary>
    public string CustomFilterName { get; set; } = string.Empty;
}

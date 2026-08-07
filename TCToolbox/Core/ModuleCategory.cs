namespace TCToolbox.Core;

/// <summary>主視窗上模組所屬的分頁分類。</summary>
/// <remarks>
/// 🔴 <b>零值必須是一個有效分類。</b><see cref="TcModule.Category"/> 的預設值是 <see cref="Misc"/>，
/// 而沒有零值的列舉會讓 <c>default</c> 落在無效值上——那樣的模組哪一個分類分頁都不屬於，
/// 會從分類分頁上整個消失（只有「全部」分頁看得到），而且不會有任何錯誤訊息。
/// <para>
/// ⚠️ 分頁的<b>顯示順序不是列舉的數值順序</b>，而是 <see cref="ModuleCategoryInfo.DisplayOrder"/>。
/// 兩者刻意脫鉤：要調整分頁左右順序時改那個陣列就好，不必動列舉值，
/// 也就不會不小心破壞「<see cref="Misc"/> 是零值」這個前提。
/// </para>
/// <para>
/// 🔴 <b>「手動觸發」與「常用」不在這個列舉裡，也不要加進來。</b>
/// 那兩個分頁是<b>篩選</b>不是分類：一個看 <see cref="TcModule.IsManualTrigger"/>、
/// 一個看設定檔裡的釘選清單，模組同時留在原本的分類分頁上。
/// 把它們做成分類成員的話，模組會從原分頁上消失——習慣在原分頁找它的人只會覺得功能不見了。
/// </para>
/// </remarks>
public enum ModuleCategory
{
    /// <summary>介面 · 雜項。<b>這是預設分類</b>，所以必須是零值。</summary>
    Misc = 0,

    /// <summary>背包 · 裝備。</summary>
    Inventory = 1,

    /// <summary>戰鬥 · 小隊。</summary>
    Combat = 2,

    /// <summary>部隊 · 生活。</summary>
    Company = 3,
}

/// <summary>分類的顯示資料（分頁順序、標題、ImGui id）。</summary>
public static class ModuleCategoryInfo
{
    /// <summary>
    /// 分頁由左到右的順序。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>這裡漏掉任何一個分類，該分類的模組就不會有自己的分頁。</b>
    /// 那不會編譯失敗也不會擲例外——模組只是從分類分頁上消失。
    /// 主視窗最後那個「全部」分頁就是為了兜住這種情況：任何模組至少在那裡看得到。
    /// </remarks>
    public static readonly ModuleCategory[] DisplayOrder =
    [
        ModuleCategory.Inventory,
        ModuleCategory.Combat,
        ModuleCategory.Company,
        ModuleCategory.Misc,
    ];

    /// <summary>分頁上顯示的標題。</summary>
    /// <remarks>
    /// 🔴 <b>不要拿這個字串當 ImGui 的 id。</b>分頁標題後面會接「已啟用/總數」，
    /// 使用者一勾一個模組數字就變，ImGui 會把它當成另一個全新的分頁
    /// （目前選取的分頁會被重設回第一頁）。id 一律用 <see cref="Id"/>。
    /// </remarks>
    public static string Title(ModuleCategory category) => category switch
    {
        ModuleCategory.Inventory => "背包 · 裝備",
        ModuleCategory.Combat => "戰鬥 · 小隊",
        ModuleCategory.Company => "部隊 · 生活",
        ModuleCategory.Misc => "介面 · 雜項",
        _ => "其他",
    };

    /// <summary>ImGui id 用的穩定英文名（跟著 <c>###</c> 走，不隨標題變動）。</summary>
    /// <remarks>
    /// 📌 刻意不用 <c>category.ToString()</c>：那是每幀做一次反射查表＋配置一個字串，
    /// 而這裡在 ImGui 的 Draw 路徑上、每幀都會被叫到。
    /// </remarks>
    public static string Id(ModuleCategory category) => category switch
    {
        ModuleCategory.Inventory => "Inventory",
        ModuleCategory.Combat => "Combat",
        ModuleCategory.Company => "Company",
        ModuleCategory.Misc => "Misc",
        _ => "Other",
    };
}

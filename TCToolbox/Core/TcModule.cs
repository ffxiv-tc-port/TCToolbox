using System;

namespace TCToolbox.Core;

/// <summary>模組列上那句提示的語氣（決定顏色）。</summary>
public enum ModuleNoticeLevel
{
    /// <summary>「不知道」——灰字。零值刻意給這個：預設狀態是無知，不是沒事。</summary>
    Unknown = 0,

    /// <summary>「有事」——橘字。</summary>
    Warning = 1,
}

/// <summary>
/// 模組列上直接顯示的一句提示。
/// </summary>
/// <param name="Level">語氣／顏色。</param>
/// <param name="Text">
/// 列上看得到的短句。🔑 判準是「隨時掃視」的放列上、「起疑才查」的放 tooltip——
/// 但<b>「不知道」本身要在列上看得見</b>，tooltip 藏的是為什麼，不是有沒有問題。
/// </param>
/// <param name="Tooltip">滑鼠移上才顯示的完整說明（可為空字串）。</param>
public readonly record struct ModuleNotice(ModuleNoticeLevel Level, string Text, string Tooltip);

/// <summary>功能模組基底：獨立開關、獨立生命週期，預設全部關閉。</summary>
public abstract class TcModule
{
    /// <summary>存檔用內部識別名（勿改，改了使用者的開關設定會遺失）。</summary>
    public abstract string InternalName { get; }

    /// <summary>顯示名稱。</summary>
    public abstract string DisplayName { get; }

    /// <summary>功能描述。</summary>
    public abstract string Description { get; }

    public bool IsEnabled { get; private set; }

    /// <summary>模組在主視窗上歸屬的分頁分類。</summary>
    /// <remarks>
    /// 📌 <b>新增模組時請顯式寫出這個 override</b>，即使結論就是 <see cref="ModuleCategory.Misc"/>。
    /// 大家新增模組都是複製一個既有模組改的，顯式寫著才會被一起帶上；
    /// 靠預設值的話，新模組會默默掉進「介面 · 雜項」頁而沒有人發現。
    /// </remarks>
    public virtual ModuleCategory Category => ModuleCategory.Misc;

    /// <summary>
    /// 這個模組的核心行為是不是「<b>使用者按了才動一次</b>」。
    /// </summary>
    /// <remarks>
    /// 判準只有一條：<b>開著但不去按它，遊戲行為完全不變</b>＝<c>true</c>；
    /// 開著就會自己介入（掛 hook 接手、盯著視窗自動點、每隔一段時間自己做事）＝<c>false</c>。
    /// <para>
    /// ⚠️ <b>不要看模組名字。</b>名字有「自動」兩個字的模組裡兩種都有
    /// （<c>AutoMaterialize</c> 要按鈕才動、<c>AutoMateriaRetrieveAll</c> 是掛 hook 自己接手），
    /// 而名字沒有「自動」的也不見得就是手動。
    /// </para>
    /// <para>
    /// 📌 這是一個<b>與 <see cref="Category"/> 正交的標記，不是第五個分類</b>：
    /// 標了 <c>true</c> 的模組仍然留在原本的分類分頁上，只是<b>額外</b>出現在「手動觸發」分頁。
    /// 刻意不做成 <see cref="ModuleCategory"/> 的新成員——那會把模組從它原本的分頁上搬走，
    /// 習慣去「背包 · 裝備」找投影台功能的人會找不到。
    /// </para>
    /// </remarks>
    public virtual bool IsManualTrigger => false;

    /// <summary>模組是否有自己的設定／操作 UI。</summary>
    public virtual bool HasConfigUI => false;

    /// <summary>
    /// 模組列上要顯示的提示（<c>null</c>＝沒有話要說）。<b>唯讀顯示</b>，與模組開關無關，
    /// 模組關著時也照樣顯示。
    /// </summary>
    /// <remarks>
    /// 🔴 這個屬性是在 ImGui 的 Draw 路徑上被讀的，<b>實作不得擲例外</b>
    /// （Draw 擲一次例外，Dalamud 會把 <c>UiBuilder.Draw</c> 設成 null，整個介面到重開遊戲前都不回來）。
    /// 呼叫端仍然會再包一層 try，但那是最後一道，不是免責。
    /// </remarks>
    public virtual ModuleNotice? RowNotice => null;

    public void Enable()
    {
        if (IsEnabled) return;
        try
        {
            OnEnable();
            IsEnabled = true;
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, $"[{InternalName}] 啟用失敗");
        }
    }

    public void Disable()
    {
        if (!IsEnabled) return;
        try
        {
            OnDisable();
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, $"[{InternalName}] 停用時發生例外");
        }
        finally
        {
            IsEnabled = false;
        }
    }

    protected abstract void OnEnable();

    protected abstract void OnDisable();

    /// <summary>模組設定 UI（在主視窗中展開繪製）。</summary>
    /// <remarks>
    /// 🔴 與 <see cref="RowNotice"/> 同一份契約：這是 ImGui 的 Draw 路徑，<b>實作不得擲例外</b>。
    /// 設定樹狀節點展開著的時候這裡<b>每幀都會被呼叫</b>，所以一個會擲例外的實作就是每幀重擲
    /// ⇒ Dalamud 的視窗錯誤閂鎖（10 秒內兩次）把主視窗永久關閉到外掛重載為止，
    /// 而模組的啟用／停用勾選框就在同一扇視窗裡 —— 使用者連關掉肇事模組的入口都一起失去。
    /// 呼叫端（<c>MainWindow.DrawModuleConfig</c>）會再包一層 try 把故障隔離在單一模組列，
    /// 但那是最後一道，<b>不是免責</b>。
    /// </remarks>
    public virtual void DrawConfig()
    {
    }
}

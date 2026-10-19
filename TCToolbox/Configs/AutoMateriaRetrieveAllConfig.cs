namespace TCToolbox;

/// <summary>
/// 自動取出全部魔晶石。
/// ⚠️ 模組本身預設是關的（<c>EnabledModules</c> 不含它），所以這裡的
/// <see cref="Enabled"/> 預設 <c>true</c> 不會讓任何人被動生效 ——
/// 它是「模組開著時要不要接手」的第二段開關，讓人可以暫時停掉接手而不必整個模組關掉。
/// </summary>
public sealed class AutoMateriaRetrieveAllConfig
{
    /// <summary>接手開關。模組開著但這格關掉＝完全維持遊戲原本的行為。</summary>
    public bool Enabled = true;

    /// <summary>每顆之間的最短間隔（毫秒）。</summary>
    public int DelayMs = 500;

    /// <summary>把結果印到聊天視窗（記錄一律會寫，不受這格影響）。</summary>
    public bool AnnounceInChat = true;
}

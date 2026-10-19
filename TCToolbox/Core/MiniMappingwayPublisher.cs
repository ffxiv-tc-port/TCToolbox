using System.Collections.Generic;

namespace TCToolbox.Core;

/// <summary>
/// 把一組「想要出現在小地圖上的在場物件」增量同步給 Mini-Mappingway：
/// 只加新的、只刪不要的，已經在上面的原地不動。
/// </summary>
/// <remarks>
/// ⚠️ <b>對方可能在我們沒看見的時候被重新載入</b>，那時它的清單是空的而我們的追蹤表還是滿的
/// ⇒「標記再也不出現，而且完全沒有徵兆」。呼叫端偵測到「從不在變成在」時要呼叫
/// <see cref="Forget"/>，另外定期用 <c>refreshAll</c> 全量重推一次當保險。
/// </remarks>
internal sealed class MiniMappingwayPublisher
{
    /// <summary>一個想要出現在小地圖上的在場物件。</summary>
    /// <param name="Id">
    /// <c>GameObjectId</c>，<b>截成 32 位元之後</b>的值。呼叫端負責先確認它塞得下
    /// （對方的端點是 <c>uint</c>）。
    /// </param>
    /// <param name="Name">物件名稱，只是給對方留個人類看得懂的紀錄。</param>
    public readonly record struct Person(uint Id, string Name);

    private readonly Dictionary<uint, string> live = [];

    private readonly HashSet<uint> seen = [];

    public MiniMappingwayPublisher(string source) => Source = source;

    /// <summary>
    /// 標記來源字串。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>不要改。</b>Mini-Mappingway 拿它當鍵把顏色、優先度、要不要顯示存進自己的設定檔，
    /// 改了之後使用者調過的設定會變成一個永遠留在對方設定裡的孤兒，而新來源會以預設值冒出來。
    /// 📌 它同時也是<b>顯示在對方設定畫面上的標籤</b>，所以要人看得懂。
    /// </remarks>
    public string Source { get; }

    /// <summary>目前確實在對方清單上的筆數。</summary>
    public int Placed => live.Count;

    /// <summary>最後一次同步時 <c>AddPerson</c> 回 <see langword="false"/> 的筆數（常態，不是錯誤）。</summary>
    public int LastRejected { get; private set; }

    /// <summary>最後一次同步實際打了幾次 IPC。</summary>
    public int LastIpcCalls { get; private set; }

    /// <summary>把對方的清單同步成 <paramref name="desired"/>。</summary>
    /// <param name="desired">這一刻想要出現在小地圖上的全部物件。</param>
    /// <param name="refreshAll">
    /// <see langword="true"/>＝先把追蹤中的整組刪掉再重加（對方可能被重載過的保險）。
    /// </param>
    public void Publish(IReadOnlyList<Person> desired, bool refreshAll = false)
    {
        LastRejected = 0;
        LastIpcCalls = 0;
        seen.Clear();

        if (refreshAll) RemoveAllTracked();

        foreach (var person in desired)
        {
            // 同一輪出現兩次同一個 id：只處理第一次（不然第二次會被當成「新的」而重複打 IPC）。
            if (!seen.Add(person.Id)) continue;
            if (live.ContainsKey(person.Id)) continue;

            LastIpcCalls++;

            if (!MiniMappingwayIpc.AddPerson(Source, person.Name, person.Id))
            {
                LastRejected++;
                continue;
            }

            live[person.Id] = person.Name;
        }

        RemoveUnwanted();
    }

    /// <summary>
    /// 把這個來源連同它的標記從對方那裡整個移除，並忘掉追蹤表。
    /// <b>只在模組停用／卸載時用。</b>
    /// </summary>
    public void Clear()
    {
        MiniMappingwayIpc.RemoveSourceAndPeople(Source);
        Forget();
    }

    /// <summary>
    /// 只忘掉追蹤表，<b>不打任何 IPC</b>。
    /// </summary>
    /// <remarks>
    /// 用在兩個狀態轉換上：「對方不見了」與「對方從不在變成在」。那時我們記著的東西全部失效，
    /// 留著會讓下一輪同步以為「已經加上去了」——表現成標記再也不出現，且毫無徵兆。
    /// 🔴 <b>重新註冊來源之後也一定要呼叫這一支</b>：對方的
    /// <c>AddOrUpdateSource</c> 會把該來源的清單換成一個新的空字典。
    /// </remarks>
    public void Forget()
    {
        live.Clear();
        LastRejected = 0;
        LastIpcCalls = 0;
    }

    private void RemoveAllTracked()
    {
        if (live.Count == 0) return;

        foreach (var id in live.Keys)
        {
            MiniMappingwayIpc.RemovePerson(id, Source);
            LastIpcCalls++;
        }

        live.Clear();
    }

    private void RemoveUnwanted()
    {
        if (live.Count == 0) return;

        List<uint>? drop = null;
        foreach (var id in live.Keys)
        {
            if (!seen.Contains(id)) (drop ??= []).Add(id);
        }

        if (drop == null) return;

        foreach (var id in drop)
        {
            live.Remove(id);
            MiniMappingwayIpc.RemovePerson(id, Source);
            LastIpcCalls++;
        }
    }
}

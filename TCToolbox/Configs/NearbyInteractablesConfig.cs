namespace TCToolbox;

/// <summary>周邊可互動物件的設定。🔴 類別名與 namespace 定了就不要改（存檔 <c>$type</c> 對不上＝設定重置）。</summary>
/// <remarks>種類篩選刻意用每種一個 bool：集合欄位缺鍵／有預設內容時會被 Dalamud 併入，改預設值也對既有使用者無效。</remarks>
public sealed class NearbyInteractablesConfig
{
    /// <summary>戰鬥中隱藏資訊列項目（輪詢照跑）。</summary>
    public bool HideInCombat = true;

    /// <summary>列出的半徑（米，角色中心到物件中心）。</summary>
    public float MaxDistance = 50f;

    public bool ShowNpc = true;
    public bool ShowEventObj = true;
    public bool ShowTreasure = true;
    public bool ShowAetheryte = true;
    public bool ShowGatheringPoint = true;
    public bool ShowHousing = true;
    public bool ShowRetainer;
    public bool ShowOther;

    /// <summary>不列沒有名字的物件（多半是輔助用的 EventObj）。</summary>
    public bool HideUnnamed = true;
}

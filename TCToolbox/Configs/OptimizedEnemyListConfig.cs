namespace TCToolbox;

/// <summary>敵對列表疊圖的詠唱顯示方式。</summary>
public enum CastDisplayMode
{
    /// <summary>完全不顯示詠唱資訊。</summary>
    Off = 0,

    /// <summary>只印剩餘秒數，技名交給原生詠唱列。</summary>
    SecondsOnly = 1,

    /// <summary>技名與秒數都印。</summary>
    NameAndSeconds = 2,

    /// <summary>依遊戲的「敵對列表詠唱列」設定自動決定（原生有顯示就只印秒數）。</summary>
    Auto = 3,
}

public sealed class OptimizedEnemyListConfig
{
    public bool ShowHp = true;
    public bool CompactNumbers = true;
    public bool ShowHpPercent;

    /// <summary>詠唱顯示方式；預設自動——原生詠唱列開著時只印秒數，避免技名重複疊字。</summary>
    public CastDisplayMode CastDisplay = CastDisplayMode.Auto;

    public bool HighlightTargetingYou = true;
    public float TextScale = 0.9f;

    /// <summary>疊圖畫在整列的右側外緣（false＝左側外緣）。</summary>
    public bool AnchorRight = true;

    /// <summary>離開列邊緣的距離（兩側都是正值往外）。</summary>
    public float OffsetX = DefaultOffsetX;

    /// <summary>相對垂直置中的微調。</summary>
    public float OffsetY = DefaultOffsetY;

    public const float DefaultOffsetX = 6f;
    public const float DefaultOffsetY = 0f;

    /// <summary>
    /// v1 時代的 <c>ShowCast</c>(bool)。<b>只為了遷移而存在</b>；<c>null</c>＝設定檔裡沒有這個鍵。
    /// </summary>
    /// <remarks>
    /// 🔴 沒有這個成員的話，舊鍵在反序列化時會被 Newtonsoft <b>靜默丟掉</b>，
    /// 「使用者當初有沒有把詠唱顯示關掉」這件事就再也讀不回來了。
    /// 📌 <c>ShouldSerializeShowCast()</c> 回 false ⇒ 只讀不寫，不會把孤兒鍵再寫回設定檔。
    /// 遷移完成後（<see cref="Configuration.Migrate"/>）會被設回 <c>null</c>。
    /// </remarks>
    public bool? ShowCast { get; set; }

    /// <summary>Newtonsoft 的慣例方法：讓 <see cref="ShowCast"/> 只讀不寫。</summary>
    public bool ShouldSerializeShowCast() => false;
}

using System;

namespace TCToolbox.Core;

/// <summary>
/// 一行疊圖文字的沿用判斷：組字用到的輸入全都和上一次相同，就回上一次組好的那個字串。
/// </summary>
/// <remarks>
/// 秒數記的是格式化之後的字元（<see cref="FormatSeconds"/>，和內插字串的 <c>{x:0.0}</c> 走同一個 <c>TryFormat</c>），
/// 不是浮點數：詠唱中浮點數每幀都不同，畫出來的字每 0.1 秒才變一次。只在繪製執行緒使用。
/// </remarks>
internal sealed class OverlayTextMemo
{
    /// <summary>秒數字元的上限；呼叫端的暫存區照這個長度開。</summary>
    public const int SecondsCapacity = 48;

    private readonly char[] seconds = new char[SecondsCapacity];
    private int secondsLength;
    private string? text;
    private int options;
    private uint hp;
    private uint maxHp;
    private int percent;
    private string name = string.Empty;

    /// <summary>把秒數照 <c>0.0</c> 格式寫進 <paramref name="buffer"/>；寫不下回 false，呼叫端這一幀就不沿用。</summary>
    public static bool FormatSeconds(float value, Span<char> buffer, out int length) =>
        value.TryFormat(buffer, out length, "0.0");

    public bool TryGet(
        int options, uint hp, uint maxHp, int percent, string name, ReadOnlySpan<char> seconds, out string text)
    {
        text = this.text ?? string.Empty;
        return this.text != null
               && this.options == options && this.hp == hp && this.maxHp == maxHp && this.percent == percent
               && this.name == name
               && seconds.SequenceEqual(this.seconds.AsSpan(0, secondsLength));
    }

    /// <param name="seconds">長度不得超過 <see cref="SecondsCapacity"/>。</param>
    public void Store(
        int options, uint hp, uint maxHp, int percent, string name, ReadOnlySpan<char> seconds, string text)
    {
        this.options = options;
        this.hp = hp;
        this.maxHp = maxHp;
        this.percent = percent;
        this.name = name;
        seconds.CopyTo(this.seconds);
        secondsLength = seconds.Length;
        this.text = text;
    }

    public void Forget() => text = null;
}

using System.Collections.Generic;

namespace TCToolbox;

/// <summary>道具丟棄清單。使用者自己維護要丟的道具，走遊戲原生「捨棄」，確認框一律由人按。</summary>
public sealed class DiscardListConfig
{
    /// <summary>
    /// 要納入丟棄清單的道具 base id（不含 HQ 位移）。
    /// </summary>
    /// <remarks>
    /// 📌 預設空清單＝開了模組也不會列出任何東西、更不會丟任何東西。這是刻意的：
    /// 這個模組唯一會動到的道具就是使用者自己放進這份清單的，任何預設項目都可能變成一件真的被丟掉的道具。
    /// </remarks>
    public List<uint> Items { get; set; } = [];

    /// <summary>整批發起時每一步之間的最短間隔（毫秒）。</summary>
    public int StepIntervalMs = 250;

    /// <summary>結束時在聊天欄報告（記錄一律會寫，不受這格影響）。</summary>
    public bool NotifyInChat = true;
}

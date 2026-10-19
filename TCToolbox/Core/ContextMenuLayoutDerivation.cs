using System;
using System.Collections.Generic;

namespace TCToolbox.Core;

/// <summary>
/// 從遊戲碼推導右鍵選單（"ContextMenu" addon）AtkValues 裡第一個項目的索引，不寫死 7／8。
/// </summary>
/// <remarks>
/// 契約：純函式、不碰遊戲記憶體——位元組由呼叫端提供（實機＝SigScanner 的模組副本；離線＝執行檔）。
/// 三個互相獨立的來源，至少兩個解得出、而且解得出的全部一致才採用；否則回 <see cref="NotDerived"/>。
/// 整檔複製自 ECommons 同名檔、只改命名空間：改推導時兩份一起改（推導見 commit 訊息）。
/// </remarks>
internal static class ContextMenuLayoutDerivation
{
    internal const int NotDerived = -1;
    internal const int AtkValueSize = 16;
    private const int MinIndex = 1;
    private const int MaxIndex = 16;

    internal sealed record Variant(string Signature, Func<byte[], int> Decode);
    internal sealed record Source(string Name, Variant[] Variants);

    internal static readonly Source[] Sources =
    [
        // AgentContext 清空選單：mov ecx,2 ; mov edx,<第一項> ; xor edi,edi ; mov [rax+d],dx ; mov byte [rax+d+2],71h
        new("AgentContext.選單重設", [new("B9 02 00 00 00 BA ?? 00 00 00 33 FF 66 89 90 ?? ?? ?? ?? C6 80 ?? ?? ?? ?? 71", DecodeAgentReset)]),
        // AddonContextMenu.OnRefresh：lea r9,[r15+16*第一項] ; … ; lea r8d,[r14-第一項]（位移 ≥0x80 時編成 disp32）
        new("AddonContextMenu.OnRefresh",
        [
            new("4D 8D 4F ?? 8B 8F ?? ?? ?? ?? 45 8D 46 ?? 88 5C 24", b => DecodeRefresh((sbyte)b[3], (sbyte)b[13])),
            new("4D 8D 8F ?? ?? ?? ?? 8B 8F ?? ?? ?? ?? 45 8D 46 ?? 88 5C 24", b => DecodeRefresh(BitConverter.ToInt32(b, 3), (sbyte)b[16])),
        ]),
        // AgentInventoryContext 開選單：有標題寫 第一項+1、沒標題寫 第一項，兩筆寫同一個欄位
        new("AgentInventoryContext.開選單", [new("C7 46 ?? ?? 00 00 00 48 8D 4E ?? E8 ?? ?? ?? ?? 4C 89 6E ?? EB 07 C7 46 ?? ?? 00 00 00", DecodeInventoryOpen)]),
    ];

    /// <param name="readMatch">給特徵碼與長度，回命中點起算的位元組；沒命中回 null。</param>
    /// <param name="evidence">每個來源的結果，寫進 log 用。</param>
    internal static int Derive(Func<string, int, byte[]?> readMatch, out string evidence)
    {
        var found = new List<int>();
        var notes = new List<string>();
        foreach(var source in Sources)
        {
            var value = NotDerived;
            var note = "沒命中";
            foreach(var variant in source.Variants)
            {
                var length = SignatureLength(variant.Signature);
                var bytes = readMatch(variant.Signature, length);
                if(bytes == null || bytes.Length != length) continue;
                if(!MatchesFixedBytes(variant.Signature, bytes))
                {
                    note = "命中處位元組與特徵碼不符";
                    continue;
                }
                var decoded = variant.Decode(bytes);
                if(decoded < MinIndex || decoded > MaxIndex)
                {
                    note = "命中但內部不一致";
                    continue;
                }
                value = decoded;
                note = decoded.ToString();
                break;
            }
            notes.Add($"{source.Name}={note}");
            if(value != NotDerived) found.Add(value);
        }
        evidence = string.Join("；", notes);
        if(found.Count < 2) return NotDerived;
        foreach(var value in found)
        {
            if(value != found[0]) return NotDerived;
        }
        return found[0];
    }

    // CurrentEventIndex 與 CurrentEventId 在同一個選單結構裡相鄰（差 2）：兩筆寫的是同一個結構才採用。
    private static int DecodeAgentReset(byte[] b)
        => BitConverter.ToInt32(b, 21) == BitConverter.ToInt32(b, 15) + 2 ? b[6] : NotDerived;

    private static int DecodeRefresh(int valuesOffset, int countDelta)
        => valuesOffset > 0 && valuesOffset % AtkValueSize == 0 && valuesOffset / AtkValueSize == -countDelta ? -countDelta : NotDerived;

    private static int DecodeInventoryOpen(byte[] b)
        => b[2] == b[24] && b[3] == b[25] + 1 ? b[25] : NotDerived;

    internal static int SignatureLength(string signature)
        => signature.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

    internal static bool MatchesFixedBytes(string signature, byte[] bytes)
    {
        var tokens = signature.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if(tokens.Length != bytes.Length) return false;
        for(var i = 0; i < tokens.Length; i++)
        {
            if(tokens[i] == "??") continue;
            if(Convert.ToByte(tokens[i], 16) != bytes[i]) return false;
        }
        return true;
    }
}

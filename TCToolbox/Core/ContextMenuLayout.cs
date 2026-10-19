using System;
using System.Runtime.InteropServices;
using System.Threading;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace TCToolbox.Core;

/// <summary>
/// 右鍵選單（"ContextMenu" addon）的 AtkValues 版面：第一個項目在哪一格，項目 i 在 <c>index + i</c>。
/// 由 <see cref="ContextMenuLayoutDerivation"/> 從遊戲碼推導，不寫死。
/// </summary>
/// <remarks>
/// 契約：整個插件生命期只推一次（第一次查詢時），用到它的地方共用同一份結果；任何執行緒都可以呼叫。
/// 推不出來時 <see cref="TryGetFirstEntryIndex"/> 回 <see langword="false"/>：呼叫端必須停用依賴項目位置的功能
/// （讀錯一格＝文字對到隔壁的選項），並由該模組在聊天視窗提示一次。
/// </remarks>
internal static unsafe class ContextMenuLayout
{
    /// <summary>給使用者看的推不出原因；每個來源的細節只寫進記錄。</summary>
    public const string FailureReason = "推不出右鍵選單的版面（遊戲改版？）";

    private static readonly Lazy<int> FirstEntry = new(Derive, LazyThreadSafetyMode.ExecutionAndPublication);

    public static bool TryGetFirstEntryIndex(out int index)
    {
        index = FirstEntry.Value;
        return index != ContextMenuLayoutDerivation.NotDerived;
    }

    /// <summary>已經推過而且推不出來。只讀快取、不觸發推導，可在繪製路徑呼叫。</summary>
    public static bool KnownUnavailable =>
        FirstEntry.IsValueCreated && FirstEntry.Value == ContextMenuLayoutDerivation.NotDerived;

    private static int Derive()
    {
        int index;
        string evidence;
        try
        {
            if (sizeof(AtkValue) != ContextMenuLayoutDerivation.AtkValueSize)
            {
                index = ContextMenuLayoutDerivation.NotDerived;
                evidence = $"sizeof(AtkValue)={sizeof(AtkValue)}";
            }
            else
            {
                index = ContextMenuLayoutDerivation.Derive(ReadMatch, out evidence);
            }
        }
        catch (Exception ex)
        {
            index = ContextMenuLayoutDerivation.NotDerived;
            evidence = $"推導時發生例外（{ex.GetType().Name}：{ex.Message}）";
        }

        if (index == ContextMenuLayoutDerivation.NotDerived)
            Svc.Log.Error($"[TCToolbox] 推不出右鍵選單第一個項目在 AtkValues 的哪一格，用到它的功能停用（{evidence}）");
        else
            Svc.Log.Information($"[TCToolbox] 右鍵選單第一個項目在 AtkValues[{index}]（{evidence}）");

        return index;
    }

    /// <summary>讀 SigScanner 的模組副本（不受別人的 hook 影響），命中點與長度都要落在 .text 內。</summary>
    private static byte[]? ReadMatch(string signature, int length)
    {
        var scanner = Svc.SigScanner;
        if (!scanner.TryScanText(signature, out var address)) return null;

        var rva = (long)address - scanner.Module.BaseAddress;
        if (rva < scanner.TextSectionOffset || rva + length > scanner.TextSectionOffset + scanner.TextSectionSize)
            return null;

        var bytes = new byte[length];
        Marshal.Copy(scanner.SearchBase + (nint)rva, bytes, 0, length);
        return bytes;
    }
}

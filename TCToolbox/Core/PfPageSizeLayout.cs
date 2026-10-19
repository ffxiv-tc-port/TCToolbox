using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.UI.Info;

namespace TCToolbox.Core;

/// <summary>招募板單頁筆數欄位的位置（由遊戲碼推導，不寫死）。</summary>
/// <param name="PageSizeOffset">欄位在 <see cref="InfoProxyCrossRealm"/> 內的位移（dword）。</param>
/// <param name="InstanceSlot">遊戲記錄該物件實例的全域指標位址；寫入前要求它等於要寫的物件。</param>
internal sealed record PfPageSizeLayout(int PageSizeOffset, nint InstanceSlot);

/// <summary>
/// 從遊戲自己的建構式（寫入預設 50）與送出搜尋請求的讀取端推導 <see cref="PfPageSizeLayout"/>，
/// 並用 CS 對同一物件的版面宣告交叉驗證；任何一關不符就回 null，不猜。
/// </summary>
/// <remarks>純函式：掃描與讀碼由呼叫端提供，離線檢查工具對執行檔跑的是同一份碼。推導依據見 commit 訊息。</remarks>
internal static class PfPageSizeLayoutDeriver
{
    /// <summary>建構式：<c>mov [rcx+P], 50</c>、登記實例全域指標、清空小隊陣列（<c>lea rcx,[rsi+G]</c>＋大小）。</summary>
    public const string CtorSignature =
        "C7 81 ?? ?? ?? ?? 32 00 00 00 89 A9 ?? ?? ?? ?? 48 89 0D ?? ?? ?? ?? 48 83 C1 ?? E8 ?? ?? ?? ?? " +
        "48 8D 8E ?? ?? ?? ?? 33 D2 41 B8 ?? ?? ?? ?? E8";

    /// <summary>送出搜尋請求：<c>mov eax,[rbx+P]</c> 寫進請求。</summary>
    public const string RequestSignature =
        "8B 83 ?? ?? ?? ?? 89 44 24 ?? C7 44 24 ?? ?? ?? 00 00 48 C7 44 24 ?? ?? 00 00 00";

    public const int CtorLength = 48;
    public const int RequestLength = 27;

    private const int CtorPageSizeDisp = 2;
    private const int CtorInstanceRel = 19;
    private const int CtorInstanceNext = 23;
    private const int CtorGroupsDisp = 35;
    private const int CtorGroupsSize = 43;
    private const int RequestDisp = 2;

    /// <summary>CS 對 <see cref="InfoProxyCrossRealm"/> 的版面宣告（執行期從載入的 CS 反射取得）。</summary>
    internal readonly record struct Expectations(int InterfaceSize, int NextNamedFieldOffset, int GroupsOffset, int GroupsSize)
    {
        public static unsafe Expectations? FromClientStructs(out string failure)
        {
            failure = string.Empty;
            const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var type = typeof(InfoProxyCrossRealm);
            var named = type.GetField(nameof(InfoProxyCrossRealm.LocalPlayerGroupIndex), Fields);
            var groups = type.GetField("_crossRealmGroups", Fields);
            var namedOffset = named?.GetCustomAttribute<FieldOffsetAttribute>()?.Value;
            var groupsOffset = groups?.GetCustomAttribute<FieldOffsetAttribute>()?.Value;
            if (groups == null || namedOffset is not { } n || groupsOffset is not { } g)
            {
                failure = "插件內建的遊戲結構定義找不到對照欄位";
                return null;
            }

            var sizeOf = typeof(Unsafe).GetMethod(nameof(Unsafe.SizeOf))!.MakeGenericMethod(groups.FieldType);
            return new Expectations(sizeof(InfoProxyInterface), n, g, (int)sizeOf.Invoke(null, null)!);
        }
    }

    /// <param name="scanText">特徵碼 → 命中位址（找不到回 0）。</param>
    /// <param name="readCode">讀命中點的指令位元組（越界回 null）。</param>
    /// <param name="dataStart">主程式 .data 起點；實例全域指標必須落在 [起點, 終點) 之內。</param>
    /// <param name="dataEnd">主程式 .data 終點（不含）。</param>
    public static PfPageSizeLayout? Derive(
        Func<string, nint> scanText, Func<nint, int, byte[]?> readCode,
        nint dataStart, nint dataEnd, Expectations cs, out string failure)
    {
        var ctor = scanText(CtorSignature);
        var ctorBytes = ctor == 0 ? null : readCode(ctor, CtorLength);
        if (ctorBytes == null || !MatchesSignature(ctorBytes, CtorSignature))
        {
            failure = "找不到遊戲建立招募資料的程式碼（特徵碼對不上）";
            return null;
        }

        var request = scanText(RequestSignature);
        var requestBytes = request == 0 ? null : readCode(request, RequestLength);
        if (requestBytes == null || !MatchesSignature(requestBytes, RequestSignature))
        {
            failure = "找不到遊戲送出招募搜尋的程式碼（特徵碼對不上）";
            return null;
        }

        var pageSize = BitConverter.ToInt32(ctorBytes, CtorPageSizeDisp);
        var groupsOffset = BitConverter.ToInt32(ctorBytes, CtorGroupsDisp);
        var groupsSize = BitConverter.ToInt32(ctorBytes, CtorGroupsSize);
        var readBack = BitConverter.ToInt32(requestBytes, RequestDisp);
        var slot = ctor + CtorInstanceNext + BitConverter.ToInt32(ctorBytes, CtorInstanceRel);

        if (groupsOffset != cs.GroupsOffset || groupsSize != cs.GroupsSize)
        {
            failure = $"遊戲的小隊陣列位置與結構定義不符（遊戲 0x{groupsOffset:X}／0x{groupsSize:X}，定義 0x{cs.GroupsOffset:X}／0x{cs.GroupsSize:X}）";
            return null;
        }

        if (pageSize % 4 != 0 || pageSize < cs.InterfaceSize || pageSize + sizeof(int) > cs.NextNamedFieldOffset)
        {
            failure = $"推出的欄位位移不合理（0x{pageSize:X}）";
            return null;
        }

        if (readBack != pageSize)
        {
            failure = $"建立端與讀取端推出的位移不一致（0x{pageSize:X}／0x{readBack:X}）";
            return null;
        }

        if (slot < dataStart || slot > dataEnd - IntPtr.Size || slot % IntPtr.Size != 0)
        {
            failure = "實例指標不在遊戲的資料區內";
            return null;
        }

        failure = string.Empty;
        return new PfPageSizeLayout(pageSize, slot);
    }

    /// <summary>位元組與特徵碼的固定部分逐位相符（擋快取位址與實際指令不一致）。</summary>
    internal static bool MatchesSignature(byte[] bytes, string signature)
    {
        var tokens = signature.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (bytes.Length < tokens.Length) return false;

        for (var i = 0; i < tokens.Length; i++)
        {
            if (tokens[i] == "??") continue;
            if (bytes[i] != Convert.ToByte(tokens[i], 16)) return false;
        }

        return true;
    }
}

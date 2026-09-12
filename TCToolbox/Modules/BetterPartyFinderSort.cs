using System;
using Dalamud.Bindings.ImGui;
using Dalamud.Hooking;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 招募清單排序：把「可加入」的招募（副本已解鎖、未被列入黑名單…）排到清單最上面，同權再交回遊戲原本的排序。
/// </summary>
/// <remarks>
/// 🔴 <b>刻意只讀那四個「已離線證明」的 byte 欄位。</b>DR 的結構還宣告了 <c>DutyID@32</c> 與
/// <c>TimeLeftSeconds@68</c>，並用後者做同權時的次要排序——但這兩個 offset<b>沒有</b>被這支比較函式碰到、
/// 也就<b>沒有</b>在台服上得到離線佐證。與其賭一個對不上的 offset 去讀出亂掉的時間值來排序（靜默錯排），
/// 這裡同權時直接呼叫 <c>Original</c>（遊戲的原比較函式）——那是最保守也最不會錯的次要排序。
/// 🔴 找不到特徵碼時（版本不合）當成「這一版無法使用」印一行 <c>Information</c> 就退場，不硬掛 0 位址。
/// </remarks>
public sealed unsafe class BetterPartyFinderSort : TcModule
{
    public override string InternalName => "BetterPartyFinderSort";
    public override string DisplayName => "招募清單排序";

    public override string Description =>
        "把「可加入」的招募（副本已解鎖、未被列入黑名單等）排到招募清單最上面；同權時交回遊戲原本的排序。" +
        "hook 遊戲自己的排序比較函式，只讀離線驗證過的欄位、不改結構。";

    public override ModuleCategory Category => ModuleCategory.Combat;

    public override bool HasConfigUI => true;

    /// <summary>招募排序比較函式（台服 7.20 唯一命中 <c>0x14053B8F0</c>）。</summary>
    private const string SortCompareSignature =
        "40 53 48 83 EC 20 0F B6 82 ?? ?? ?? ?? 48 8B DA 38 81 ?? ?? ?? ??";

    /// <summary>
    /// 四個優先旗標欄位在招募記錄結構內的位移（byte），全部離線反組譯驗證過。
    /// </summary>
    /// <remarks>
    /// 順序就是比較優先序：先比 0x198、再 0x199、再 0x19A、再 0x19B；愈前面權重愈高。
    /// 每個都是「值小的排前面」（沿用 DR 的方向）。
    /// </remarks>
    private static readonly int[] PriorityOffsets = [0x198, 0x199, 0x19A, 0x19B];

    private delegate byte PartyFinderSortCompareDelegate(nint a1, nint a2);

    private Hook<PartyFinderSortCompareDelegate>? sortHook;

    protected override void OnEnable()
    {
        if (!Svc.SigScanner.TryScanText(SortCompareSignature, out var address) || address == nint.Zero)
        {
            Svc.Log.Information(
                $"[{InternalName}] 找不到招募排序比較函式的特徵碼，本模組這一版無法使用。");
            return;
        }

        sortHook = Svc.Hooks.HookFromAddress<PartyFinderSortCompareDelegate>(address, SortCompareDetour);
        sortHook.Enable();

        Svc.Log.Information($"[{InternalName}] 已掛載，排序比較函式位址 0x{address:X}。");
    }

    protected override void OnDisable()
    {
        sortHook?.Dispose();
        sortHook = null;
    }

    /// <summary>
    /// 比較兩則招募：回 <c>1</c>＝a1 排在 a1 前面（即 a1 較優先），回 <c>0</c>＝不。
    /// </summary>
    /// <remarks>
    /// 只讀四個離線驗證過的 byte 優先旗標；四個都同值時，把判斷完全交回遊戲的原比較函式。
    /// a1／a2 是遊戲排序當下傳進來的合法元素指標（在 hook 生命週期內有效），只讀結構內已證明存在的 byte，
    /// 不越界、不保存指標、不跨幀。
    /// </remarks>
    private byte SortCompareDetour(nint a1, nint a2)
    {
        // 理論上不會是 0（遊戲傳的是實體元素位址），但 0 位址交給 Original 也會崩，這裡直接短路成同權。
        if (a1 == 0 || a2 == 0) return 0;

        var pa = (byte*)a1;
        var pb = (byte*)a2;

        foreach (var off in PriorityOffsets)
        {
            var va = pa[off];
            var vb = pb[off];
            if (va != vb) return (byte)(va < vb ? 1 : 0);
        }

        return sortHook!.Original(a1, a2);
    }

    public override void DrawConfig()
    {
        ImGui.TextWrapped(
            "啟用後，招募清單會把「可加入」的招募（副本已解鎖、未被列入黑名單等優先旗標）排到最上面；" +
            "這些旗標相同的招募維持遊戲原本的排序。此模組沒有其他設定。");

        ImGui.Spacing();
        ImGui.TextDisabled(
            sortHook is { IsEnabled: true }
                ? "狀態：已掛載。"
                : "狀態：未掛載（找不到特徵碼／尚未啟用，詳見記錄）。");
    }
}

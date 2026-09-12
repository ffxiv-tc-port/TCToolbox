using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace TCToolbox.Core;

/// <summary>
/// 走遊戲自己的「雇員道具命令」在玩家背包與雇員之間搬道具（寄放／取回）。
/// </summary>
/// <remarks>
/// 🔴 <b>解析不到就明確不可用</b>（<see cref="IsAvailable"/> 回 <c>false</c>），呼叫端據此
/// 停手並告知使用者，絕不靜默走一條可能無效的路徑。特徵碼是「下次改版可能失效且靜默」的東西。
/// </remarks>
public static unsafe class RetainerItemTransfer
{
    private delegate void RetainerItemCommandDelegate(
        nint agentRetainerItemCommandModule, uint slot, InventoryType inventoryType,
        uint a4, RetainerItemCommand command);

    private const string RetainerItemCommandSig =
        "48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 48 83 EC 30 48 8B 5C 24 ?? 41 8B F0";

    public enum RetainerItemCommand : long
    {
        RetrieveFromRetainer = 0,
        EntrustToRetainer = 1,
    }

    private static RetainerItemCommandDelegate? command;
    private static bool scanAttempted;

    /// <summary>玩家背包四頁。</summary>
    public static readonly InventoryType[] PlayerBags =
        [InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4];

    /// <summary>雇員的道具頁（雇員的水晶／貨幣不在此列，本工具只搬一般道具）。</summary>
    public static readonly InventoryType[] RetainerBags =
    [
        InventoryType.RetainerPage1, InventoryType.RetainerPage2, InventoryType.RetainerPage3,
        InventoryType.RetainerPage4, InventoryType.RetainerPage5, InventoryType.RetainerPage6,
        InventoryType.RetainerPage7,
    ];

    /// <summary>特徵碼是否解析成功（只掃一次，之後回快取）。</summary>
    public static bool IsAvailable => TryResolve();

    private static bool TryResolve()
    {
        if (scanAttempted) return command != null;
        scanAttempted = true;

        if (Svc.SigScanner.TryScanText(RetainerItemCommandSig, out var addr))
        {
            command = Marshal.GetDelegateForFunctionPointer<RetainerItemCommandDelegate>(addr);
            Svc.Log.Information($"[RetainerItemTransfer] 雇員道具命令特徵碼解析成功：0x{addr:X}");
        }
        else
        {
            Svc.Log.Warning("[RetainerItemTransfer] 雇員道具命令特徵碼解析失敗，寄放／取回功能將停用。");
        }

        return command != null;
    }

    /// <summary>雇員道具命令模組。⚠️ <c>+ 40</c> 是未文件化偏移，沿用 AutoRetainer 的實測值。</summary>
    private static nint GetModule()
    {
        var agentModule = AgentModule.Instance();
        if (agentModule == null) return 0;
        var agent = agentModule->GetAgentByInternalId(AgentId.Retainer);
        return agent == null ? 0 : (nint)agent + 40;
    }

    /// <summary>雇員視窗是否處於可下命令的狀態。</summary>
    public static bool IsRetainerReady() => GetModule() != 0;

    /// <summary>
    /// 把 <paramref name="source"/> 容器第 <paramref name="slot"/> 格的道具寄放／取回。
    /// </summary>
    /// <param name="toRetainer"><c>true</c>＝從背包寄放到雇員；<c>false</c>＝從雇員取回背包。</param>
    /// <returns>成功送出命令為 <c>true</c>；特徵碼未解析或雇員視窗未就緒為 <c>false</c>。</returns>
    public static bool Move(InventoryType source, int slot, bool toRetainer)
    {
        if (!TryResolve()) return false;

        var module = GetModule();
        if (module == 0) return false;

        command!(module, (uint)slot, source, 0,
                 toRetainer ? RetainerItemCommand.EntrustToRetainer : RetainerItemCommand.RetrieveFromRetainer);
        return true;
    }
}

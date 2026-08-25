using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace TCToolbox.Core;

/// <summary>
/// 走遊戲自己的「雇員道具命令」在玩家背包與雇員之間搬道具（寄放／取回）。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 <b>為什麼不用 <c>InventoryManager.MoveItemSlot</c>：</b>對雇員頁呼叫 <c>MoveItemSlot</c>
/// 會「假成功」（回傳 0、本機容器同步更新），但伺服器隨後把它退回——即使帶 <c>a6: true</c>
/// 也<b>沒有台服實機證據</b>（見 <see cref="Modules.AutoInventoryTransfer"/> 類別註解裡
/// 2026-07-31／08-01 的實測）。<b>實機來回驗證過會動的是這條路徑</b>：遊戲自己的雇員道具命令，
/// 它一定送到伺服器，落點也由遊戲決定（取回進背包空位／寄放進雇員空位、自動疊堆）。
/// </para>
/// <para>
/// 📌 內容從 <see cref="Modules.AutoInventoryTransfer"/> 的私有實作抽出成共用：特徵碼、
/// <c>+ 40</c> 未文件化偏移、命令列舉值都<b>逐字沿用</b>那份已經實機驗證過的路徑，
/// 不新解一次。<see cref="Modules.AutoInventoryTransfer"/> 自己那份刻意<b>不動</b>
/// （它還有拖放 hook 等周邊邏輯綁在一起，重構它不在本次範圍內）。
/// </para>
/// <para>
/// 🔴 <b>解析不到就明確不可用</b>（<see cref="IsAvailable"/> 回 <c>false</c>），呼叫端據此
/// 停手並告知使用者，絕不靜默走一條可能無效的路徑。特徵碼是「下次改版可能失效且靜默」的東西。
/// </para>
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

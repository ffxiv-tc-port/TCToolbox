using System;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 稀有品繳交解鎖：打開「籌備任務」視窗時，把遊戲判定的軍銜解鎖旗標覆寫成「已解鎖」，
/// 讓軍銜不足時也能打開籌備稀有品分頁。開啟本模組後，每次清單打開（含每繳交一件後遊戲自己重開）都補寫一次；關掉即停。
/// </summary>
/// <remarks>
/// 契約：detour 先呼叫 Original 讓遊戲把 (rank&gt;=6) 的旗標寫完，再覆寫成 1（先寫會被 Original 蓋掉）。
/// 寫入前用指標閘門確認第一參數確實是 GrandCompanySupply agent（此函式另有一條 E9 尾呼叫入口，
/// 傳入參數未驗）；偏移必須由錨定樣式解出且落在 agent 欄位範圍內，否則不掛 hook、寫入點也拒寫。
/// AVE 在 .NET Core 攔不到，指標閘門與偏移閘門是唯一防線，排在任何解參考／寫入之前。
/// </remarks>
public sealed unsafe class ExpertDeliveryUnlock : TcModule
{
    public override string InternalName => "ExpertDeliveryUnlock";
    public override string DisplayName => "稀有品繳交解鎖";

    public override string Description =>
        "軍銜不足時也能打開籌備稀有品分頁：打開籌備任務視窗時把軍銜解鎖旗標覆寫成已解鎖" +
        "（每繳交一件、遊戲重開清單時也會再覆寫）。預設關，手動開啟，關掉即停；不自動化、不送封包。";

    public override ModuleCategory Category => ModuleCategory.Company;

    public override bool HasConfigUI => true;

    /// <summary>打開籌備任務視窗時初始化軍銜資訊的函式。</summary>
    private const string InitSignature =
        "40 55 56 41 54 41 56 48 8D AC 24 ?? ?? ?? ?? B8 ?? ?? ?? ?? E8";

    /// <summary>
    /// 旗標寫入指令的錨：<c>cmp dil,6; setae al; xor edx,edx; mov [rsi+disp32],al</c>，
    /// disp32 緊接在樣式之後。要求全 .text 恰好命中一次。
    /// </summary>
    private const string RankFlagStoreAnchor = "40 80 FF 06 0F 93 C0 33 D2 88 86";

    private const int AnchorLength = 11;

    /// <summary>錨點距初始化函式起點的上限；超過就不視為同一支函式。</summary>
    private const int MaxAnchorDistance = 0x200;

    /// <summary>AgentModule 為本 agent 配置的大小（與 CS 宣告一致）。</summary>
    private const int AgentAllocSize = 0x98;

    /// <summary><c>mov rsi, rcx</c>：函式把第一參數（agent）存進 rsi，錨點的 [rsi+disp32] 才是 agent 欄位。</summary>
    private static ReadOnlySpan<byte> MovRsiRcx => [0x48, 0x8B, 0xF1];

    /// <summary>nint f(AgentGrandCompanySupply* agent /*rcx*/, void* supplyData /*rdx*/, int a3 /*r8d*/)。第二參數不是 agent。</summary>
    private delegate nint InitDelegate(AgentGrandCompanySupply* agent, void* supplyData, int a3);

    private Hook<InitDelegate>? hook;

    /// <summary>軍銜解鎖旗標欄位偏移（CS 未宣告），從錨定樣式當場解出；-1＝解不出，此時絕不寫入。</summary>
    private int rankFlagOffset = -1;

    /// <summary>本功能停用的原因（空字串＝沒停用）；只在 OnEnable 寫，列提示讀。</summary>
    private string disabledReason = string.Empty;

    /// <summary>籌備任務的報酬視窗：每繳交一件都會先出現，之後遊戲才重開清單。</summary>
    private const string RewardAddonName = "GrandCompanySupplyReward";

    /// <summary>上次補寫之後出現過報酬視窗＝這次是繳交後遊戲自己重開清單。只在框架執行緒讀寫。</summary>
    /// <remarks>
    /// 刻意不用「清單視窗關閉」當同一次開窗的界線：每繳交一件，清單本身就會被關掉再由遊戲重開，
    /// 與使用者自己關窗分不出來。
    /// </remarks>
    private bool rewardShownSinceWrite;

    protected override void OnEnable()
    {
        rankFlagOffset = -1;
        disabledReason = string.Empty;
        rewardShownSinceWrite = false;

        if (!Svc.SigScanner.TryScanText(InitSignature, out var address) || address == nint.Zero)
        {
            disabledReason = "找不到籌備任務視窗初始化函式的特徵碼";
            Svc.Log.Information(
                $"[{InternalName}] {disabledReason}，本功能停用（不影響其他模組）。" +
                "這通常代表台服主程式改版、特徵碼需要更新。");
            return;
        }

        var (offset, anchorDistance, failure) = ResolveRankFlagOffset(address);
        if (offset < 0)
        {
            disabledReason = failure;
            Svc.Log.Information(
                $"[{InternalName}] {failure}，為了不寫錯位址，本功能停用、不掛 hook（不影響其他模組）。");
            return;
        }

        rankFlagOffset = offset;
        Svc.Log.Information(
            $"[{InternalName}] 已掛載於 0x{address:X}；解鎖旗標偏移 agent+0x{offset:X}" +
            $"（錨定樣式全 .text 唯一，位於函式 +0x{anchorDistance:X}）");

        hook = Svc.Hooks.HookFromAddress<InitDelegate>(address, Detour);
        hook.Enable();
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, RewardAddonName, OnRewardSetup);
    }

    protected override void OnDisable()
    {
        hook?.Dispose();
        hook = null;
        Svc.AddonLifecycle.UnregisterListener(OnRewardSetup);
        rewardShownSinceWrite = false;
    }

    private void OnRewardSetup(AddonEvent type, AddonArgs args) => rewardShownSinceWrite = true;

    /// <summary>偏移落在 AgentInterface 標頭之後、agent 配置範圍之內＝可寫的 agent 欄位。</summary>
    private static bool IsAgentFieldOffset(int offset)
        => offset >= sizeof(AgentInterface) && offset < AgentAllocSize;

    /// <summary>
    /// 從錨定樣式讀 disp32＝旗標偏移；任何前提不成立都回 Offset＝-1 與原因，呼叫端據此停用本功能。
    /// </summary>
    /// <remarks>
    /// 前提：錨點全 .text 唯一、落在已 hook 的函式內、函式在錨點前有 <c>mov rsi, rcx</c>、
    /// 解出的偏移是 agent 欄位。只讀主程式 .text 的位元組，不跟隨任何指標。
    /// </remarks>
    private static (int Offset, long AnchorDistance, string Failure) ResolveRankFlagOffset(nint funcStart)
    {
        var hits = Svc.SigScanner.ScanAllText(RankFlagStoreAnchor);
        if (hits.Length != 1)
            return (-1, 0, $"軍銜旗標寫入指令的錨定樣式命中 {hits.Length} 次（需要剛好 1 次）");

        var hit = hits[0];
        var distance = (long)hit - funcStart;
        if (distance <= 0 || distance > MaxAnchorDistance)
            return (-1, distance, $"軍銜旗標寫入指令不在初始化函式內（距函式起點 {distance} bytes）");

        var textStart = Svc.SigScanner.Module.BaseAddress + (nint)Svc.SigScanner.TextSectionOffset;
        var textEnd = textStart + Svc.SigScanner.TextSectionSize;
        if (funcStart < textStart || hit + AnchorLength + sizeof(int) > textEnd)
            return (-1, distance, "軍銜旗標寫入指令的位址超出主程式程式碼區段");

        if (new ReadOnlySpan<byte>((void*)funcStart, (int)distance).IndexOf(MovRsiRcx) < 0)
            return (-1, distance, "初始化函式在旗標寫入前沒有 mov rsi, rcx，無法確認寫入目標是 agent");

        var disp = *(int*)(hit + AnchorLength);
        if (!IsAgentFieldOffset(disp))
            return (-1, distance,
                    $"解出的旗標偏移 0x{disp:X} 不在 agent 欄位範圍內" +
                    $"（需 ≥ 0x{sizeof(AgentInterface):X} 且 < 0x{AgentAllocSize:X}）");

        return (disp, distance, string.Empty);
    }

    private nint Detour(AgentGrandCompanySupply* agent, void* supplyData, int a3)
    {
        // 快照到區域變數：OnDisable() 會把欄位設回 null，而 detour 可能還在執行中。
        var hook = this.hook;
        if (hook == null) return nint.Zero;

        // 契約：先放行讓遊戲把 (rank>=6) 的旗標寫完，之後才覆寫成 1（先寫會被蓋掉）。
        var ret = hook.OriginalDisposeSafe(agent, supplyData, a3);

        try
        {
            ProbeAndWrite(agent);
        }
        catch (Exception ex)
        {
            Svc.Log.Information($"[{InternalName}] 讀寫解鎖旗標失敗，本次略過：{ex.Message}");
        }

        return ret;
    }

    private void ProbeAndWrite(AgentGrandCompanySupply* agent)
    {
        // 🔴 指標閘門：只有第一參數確實是 GrandCompanySupply agent 時才解參考／寫入。
        // E9 尾呼叫入口可能傳非 agent 指標，讀寫壞指標＝AVE（try/catch 攔不到）。
        if (agent == null) return;
        var module = AgentModule.Instance();
        if (module == null) return;
        if ((AgentInterface*)agent != module->GetAgentByInternalId(AgentId.GrandCompanySupply)) return;

        // 🔴 寫入閘門：偏移必須是 agent 欄位（解不出時為 -1），否則絕不寫（寫錯＝踩壞記憶體、AVE 攔不到）。
        // 快照成區域變數，檢查與寫入用同一個值。
        var offset = rankFlagOffset;
        if (!IsAgentFieldOffset(offset))
        {
            Svc.Log.Information(
                $"[{InternalName}] 解鎖旗標偏移未通過檢查（0x{offset:X}），未寫入。");
            return;
        }

        var flagPtr = (byte*)((nint)agent + offset);
        var before = *flagPtr;
        // 遊戲每次重開清單（含每繳交一件後自己重開）都把旗標寫回 (rank>=6)，所以每次都要補寫。
        // 刻意不用時間節流：節流期內的重開會漏寫，籌備稀有品清單變空，自動繳交就判成交完。
        // 已非 0＝軍銜夠或這次已補寫過，不寫也不記。
        if (before != 0) return;
        *flagPtr = 1;

        // 使用者打開籌備任務視窗記 Information；同一次開窗裡繳交後的重開記 Verbose。
        var reopenedAfterDelivery = rewardShownSinceWrite;
        rewardShownSinceWrite = false;
        var detail = $"寫入前(agent+0x{offset:X})={before}、寫入後=1" +
                     $"、SelectedTab={agent->SelectedTab}、清單筆數(NumItems)={agent->NumItems}";
        if (reopenedAfterDelivery)
            Svc.Log.Verbose($"[{InternalName}] 繳交後清單重開，已重新覆寫解鎖旗標：{detail}");
        else
            Svc.Log.Information($"[{InternalName}] 籌備稀有品分頁已解鎖（軍銜不足，已覆寫解鎖旗標）：{detail}");
    }

    public override void DrawConfig()
    {
        ImGui.TextWrapped(
            "開啟後，打開「籌備任務」視窗即生效：軍銜不足時也能打開籌備稀有品分頁；關掉本模組即停。");
        ImGui.TextWrapped(
            "只在籌備任務視窗打開的那一刻覆寫解鎖旗標（每繳交一件，遊戲會自己重開清單，那時會再覆寫一次），" +
            "不自動化、不每幀輪詢、不送封包、不改設定檔。");
    }

    /// <summary>特徵碼或偏移對不上而停用時，在模組列上就要看得見。</summary>
    /// <remarks>Draw 路徑上讀，不得擲例外——只讀兩個欄位。</remarks>
    public override ModuleNotice? RowNotice =>
        IsEnabled && disabledReason.Length > 0
            ? new ModuleNotice(ModuleNoticeLevel.Warning, "功能停用",
                               $"{disabledReason}。台服改版後對不上時會是這個狀態；" +
                               "本模組不會寫入任何東西，也不會出錯。")
            : null;
}

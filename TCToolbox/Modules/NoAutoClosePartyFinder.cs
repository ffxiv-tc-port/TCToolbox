using System;
using Dalamud.Bindings.ImGui;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 招募視窗不自動關：正在看某一則招募的詳細內容時，有人加入／離開那支隊伍，遊戲原本會把詳細視窗關掉——
/// 這個模組改成關掉後立刻用<b>新資料</b>重新打開同一則，等於「不關、只刷新」。
/// </summary>
/// <remarks>
/// <para>
/// 機制＝hook <c>AgentLookingForGroup</c> 的隱藏函式。判定這次隱藏是「隊伍人數變動造成的刷新」時，
/// 關掉詳細視窗並排程重新開啟同一則；判定是「使用者自己要關」時，原封不動交回遊戲。
/// 參考 DailyRoutines <c>NoAutoClosePartyFinder</c>（作者 Nyy／YLCHEN）重寫，並修掉原版兩個問題（見下）。
/// </para>
/// <para>
/// <b>離線反組譯驗證（台服 7.20，imageBase 0x140000000）</b>：隱藏函式特徵碼<b>唯一命中</b>
/// <c>0x140527C10</c>，<c>.pdata</c> 確認為真函式起點；唯一引用來自 <c>.rdata</c> 的 vtable 槽
/// ——是虛函式、經 vtable 分派、<b>確實會被呼叫</b>（不是內聯死碼），hook 會觸發。rcx＝<c>AgentLookingForGroup*</c>。
/// </para>
/// <para>
/// 🔴 <b>修掉 DR 原版的跨幀原生指標。</b>DR 用 <c>RunOnTick(() =&gt; agent-&gt;OpenListing(...), 100ms)</c>
/// 把 <c>agent</c> 指標<b>帶過幀</b>——原生指標跨幀是紅線（延遲那 100ms 內 agent 可能已失效，
/// 裸解參考就是攔不到的 AccessViolationException）。這裡只把 <c>listingId</c>（值）帶過幀，
/// 重開的那一刻<b>重新解析</b> <see cref="AgentLookingForGroup.Instance"/>。
/// </para>
/// <para>
/// 🔴 <b>不攔截／不抑制任何遊戲訊息。</b>DR 原版靠攔一個 <c>LogMessage</c>（947，隊員變動）並把它
/// <c>isPrevented = true</c> 抑制掉來當時序訊號。這裡改成<b>唯讀輪詢</b>：每幀讀
/// <c>LastViewedListing.SlotsFilled</c>，人數變了才記一個時間戳——不 hook 訊息、不抑制任何東西，
/// 只讀遊戲已解析好的欄位。
/// </para>
/// <para>
/// ⚠️ <b>時序競態已設計成無害。</b>「人數變動」由每幀輪詢與 hook 當下<b>兩處</b>共同比對同一個基準值＋
/// 一個 1 秒的黏著時間戳，所以輪詢與 hook 在同一幀誰先跑都能抓到這次變動。若真的沒抓到（例如遊戲
/// 更新順序與假設不同），失敗形式是<b>視窗照舊關閉</b>（＝沒裝這個模組的行為），不會崩、不會卡。
/// </para>
/// </remarks>
public sealed unsafe class NoAutoClosePartyFinder : TcModule
{
    public override string InternalName => "NoAutoClosePartyFinder";
    public override string DisplayName => "招募視窗不自動關";

    public override string Description =>
        "正在看某則招募的詳細內容時，有人加入／離開那支隊伍，改成把視窗刷新（關掉立刻用新資料重開），而不是關掉。" +
        "使用者自己要關則照常關。hook 遊戲隱藏函式、不攔截任何訊息、不跨幀保存指標。";

    public override ModuleCategory Category => ModuleCategory.Combat;

    public override bool HasConfigUI => true;

    private const string DetailAddon = "LookingForGroupDetail";

    /// <summary>隱藏函式特徵碼（台服 7.20 唯一命中 <c>0x140527C10</c>）。</summary>
    private const string HideSignature = "48 89 5C 24 ?? 57 48 83 EC 20 83 A1 ?? ?? ?? ?? ??";

    /// <summary>人數變動後，這麼久以內的隱藏才判定為「刷新」而保持開啟（毫秒）。與 DR 一致 1 秒。</summary>
    private const int KeepOpenWindowMs = 1000;

    /// <summary>關掉之後隔多久重開（毫秒）。與 DR 一致。</summary>
    private const int ReopenDelayMs = 100;

    private delegate void HideDelegate(AgentLookingForGroup* agent);

    private Hook<HideDelegate>? hideHook;

    // ── 人數變動偵測用的基準（只存值，不存指標）。 ──
    private uint baseListingId;
    private byte baseSlotsFilled;
    private long lastMemberChangeTick;

    protected override void OnEnable()
    {
        if (!Svc.SigScanner.TryScanText(HideSignature, out var address) || address == nint.Zero)
        {
            Svc.Log.Information(
                $"[{InternalName}] 找不到招募隱藏函式的特徵碼，本模組這一版無法使用。");
            return;
        }

        hideHook = Svc.Hooks.HookFromAddress<HideDelegate>(address, HideDetour);
        hideHook.Enable();

        Svc.Framework.Update += OnUpdate;

        baseListingId = 0;
        baseSlotsFilled = 0;
        lastMemberChangeTick = 0;

        Svc.Log.Information($"[{InternalName}] 已掛載，隱藏函式位址 0x{address:X}。");
    }

    protected override void OnDisable()
    {
        Svc.Framework.Update -= OnUpdate;

        hideHook?.Dispose();
        hideHook = null;

        baseListingId = 0;
        baseSlotsFilled = 0;
        lastMemberChangeTick = 0;
    }

    /// <summary>每幀維護「目前正在看的那則招募」的人數基準；人數變了就記時間戳。</summary>
    private void OnUpdate(IFramework framework)
    {
        var agent = AgentLookingForGroup.Instance();
        if (agent == null || !UiHelper.IsAddonReady(DetailAddon))
        {
            baseListingId = 0;
            baseSlotsFilled = 0;
            return;
        }

        ObserveMemberChange(agent);
    }

    /// <summary>
    /// 比對目前的（listingId, SlotsFilled）與基準；同一則但人數變了就記時間戳並更新基準。
    /// </summary>
    /// <remarks>
    /// 輪詢與 hook 兩處都呼叫這支——所以誰先跑都會把這次變動抓進 <see cref="lastMemberChangeTick"/>。
    /// </remarks>
    private void ObserveMemberChange(AgentLookingForGroup* agent)
    {
        var id = agent->LastViewedListing.ListingId;
        var filled = agent->LastViewedListing.SlotsFilled;

        if (id != 0 && id == baseListingId && filled != baseSlotsFilled)
            lastMemberChangeTick = Environment.TickCount64;

        baseListingId = id;
        baseSlotsFilled = filled;
    }

    private void HideDetour(AgentLookingForGroup* agent)
    {
        try
        {
            if (agent != null && UiHelper.IsAddonReady(DetailAddon))
            {
                // hook 當下也比對一次，蓋掉「輪詢還沒跑到這一幀」的情形。
                ObserveMemberChange(agent);

                var listingId = agent->LastViewedListing.ListingId;
                var recentChange = Environment.TickCount64 - lastMemberChangeTick < KeepOpenWindowMs;

                if (listingId != 0 && recentChange)
                {
                    var detail = UiHelper.GetAddon(DetailAddon);
                    if (UiHelper.IsReady(detail))
                        detail->Close(true);

                    // 🔴 只帶 listingId（值）過幀；重開時重新解析 Instance()。
                    Svc.Framework.RunOnTick(() => ReopenListing(listingId),
                                            TimeSpan.FromMilliseconds(ReopenDelayMs));
                    return; // 吞掉這次的自動關閉
                }
            }
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, $"[{InternalName}] 判斷保持開啟時發生例外，交回遊戲原行為。");
        }

        hideHook!.Original(agent);
    }

    /// <summary>重新打開指定招募（在延遲的 tick 上執行，當場重新解析 agent）。</summary>
    private void ReopenListing(uint listingId)
    {
        try
        {
            if (listingId == 0) return;

            // 使用者可能在這 100ms 內自己又開了別的東西或關掉整個招募板——重新解析、狀態不對就放手。
            var agent = AgentLookingForGroup.Instance();
            if (agent == null) return;
            if (UiHelper.IsAddonReady(DetailAddon)) return; // 已經開著就不重複開

            // 🔴 標記這是「程式重開」，讓 AutoJoinPartyFinder 那側不要把這次刷新當成使用者主動開詳細而自動加入。
            PartyFinderCoordination.MarkProgrammaticReopen();
            agent->OpenListing(listingId);
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, $"[{InternalName}] 重新開啟招募詳細視窗失敗。");
        }
    }

    public override void DrawConfig()
    {
        ImGui.TextWrapped(
            "看某則招募的詳細內容時，有人加入／離開那支隊伍，會把視窗刷新（用新資料重開）而不是關掉；" +
            "你自己要關則照常關。此模組沒有其他設定。");

        ImGui.Spacing();
        ImGui.TextDisabled(
            hideHook is { IsEnabled: true }
                ? "狀態：已掛載。"
                : "狀態：未掛載（找不到特徵碼／尚未啟用，詳見記錄）。");
    }
}

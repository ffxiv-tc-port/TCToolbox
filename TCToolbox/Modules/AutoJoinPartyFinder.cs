using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 招募詳細視窗自動加入：點開一則招募的詳細內容之後，直接按下「加入」。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 <b>只有你自己點開某一則招募才會觸發</b>——模組不會去翻招募清單、不會自己開任何視窗、
/// 也不會定時做任何事。要停手就把詳細視窗關掉（或按住底下設定的「取消鍵」）。
/// </para>
/// <para>
/// ⚠️ 但它<b>不是</b>「手動觸發」模組：開著的時候，每一次你點開詳細內容遊戲行為就會不一樣，
/// 所以 <see cref="TcModule.IsManualTrigger"/> 維持 <c>false</c>。
/// </para>
/// <para>
/// 📌 <b>怎麼按下「加入」</b>：取 <c>AddonLookingForGroupDetail.JoinPartyButton</c>
/// （FFXIVClientStructs 具名欄位）再<b>重播那顆按鈕自己的事件</b>。
/// 上游 PandorasBox <c>AutoJoinPF</c> 走的是 <c>Callback.Fire(addon, false, 0)</c> ＋
/// <c>NodeList[111]</c>（鎖頭）／<c>NodeList[113]</c>（招募人名稱）這種<b>節點索引</b>，
/// 台服完全沒有可離線驗證的依據，而且差一格就是按到別的東西。
/// 這裡三個判斷全部改用具名欄位：
/// <list type="bullet">
/// <item>是不是自己的招募 → <c>AgentLookingForGroup.OwnListingId</c> 與
/// <c>LastViewedListing.ListingId</c> 相比，不去讀畫面上的名字。</item>
/// <item>是不是密碼招募 → <c>LastViewedListing.JoinConditionFlags</c>，不去看鎖頭圖示。</item>
/// <item>按不按得下去 → 按鈕自己的 <c>IsEnabled</c> 與可見性。</item>
/// </list>
/// </para>
/// <para>
/// 🔴 <b>確認框只在「我們剛剛按過加入」之後才代按。</b>判準是四個條件同時成立：
/// 我們按下加入 <see cref="ConfirmWindowMs"/> 毫秒以內、當時畫面上<b>沒有</b>確認框、
/// 詳細視窗還開著、<b>而且提示文字真的是「加入招募」那一句</b>。
/// </para>
/// <para>
/// 🔴 <b>為什麼光靠時間窗不夠</b>：前三個條件排掉的只有「按下加入<b>那一刻</b>已經存在的框」，
/// 蓋不住按下之後才冒出來的<b>外來</b>框（交易申請、別的外掛的確認框）——
/// 那是一個 5 秒的盲窗，而且失敗形式是靜默的（別人的對話框被按下「是」）。
/// → 按之前另外拿 <c>Addon</c> 表的加入確認句做比對（見 <see cref="JoinPromptRows"/>）。
/// </para>
/// </remarks>
public sealed unsafe class AutoJoinPartyFinder : TcModule
{
    public override string InternalName => "AutoJoinPartyFinder";

    public override string DisplayName => "招募詳細視窗自動加入";

    public override string Description =>
        "點開某一則招募的詳細內容之後，直接幫你按下「加入」（可設定延遲、取消鍵）。" +
        "自己的招募、密碼招募、按鈕是停用狀態時都不動作。只在你點開詳細視窗時觸發，不會自己翻招募清單。";

    public override ModuleCategory Category => ModuleCategory.Combat;

    public override bool HasConfigUI => true;

    private const string DetailAddon = "LookingForGroupDetail";

    /// <summary>按下加入之後，多久以內出現的確認框才算是我們造成的（毫秒）。</summary>
    private const int ConfirmWindowMs = 5_000;

    /// <summary>
    /// 判定「這個 SelectYesno 是不是加入招募的確認框」用的 <c>Addon</c> 列。
    /// </summary>
    /// <remarks>
    /// 🔑 台服 7.20 實查：<c>Addon#11115</c> ＝「確定要加入任務「＿UNKNOWN＿」的＿UNKNOWN＿嗎？\n招募人為＿UNKNOWN＿。」
    /// —— 全表 14850 列裡只有這一列含「招募人為」。
    /// 用列號查客戶端自己的字串，所以跟語言無關，也不會因為台服用全形標點而失效。
    /// ❌ <b>不能整句逐字比對</b>：句子裡的任務名、隨募類型、招募人名全都是 placeholder。
    /// 比對規則見 <see cref="AddonPrompt"/>（只留固定片段、全部依序出現才算命中）。
    /// </remarks>
    private static readonly uint[] JoinPromptRows = [11115];

    /// <summary>解析好的確認框樣板；<see cref="OnEnable"/> 時建一次。</summary>
    private readonly List<List<string>> joinPrompts = [];

    /// <summary>可選的取消鍵（0＝不使用）。</summary>
    private static readonly (int Code, string Label)[] SelectableKeys =
    [
        (0, "無（不使用）"),
        ((int)VirtualKey.SHIFT, "SHIFT"),
        ((int)VirtualKey.CONTROL, "CTRL"),
        ((int)VirtualKey.MENU, "ALT"),
    ];

    private AutoJoinPartyFinderConfig Config => Plugin.Instance.Config.AutoJoinPartyFinder;

    /// <summary>詳細視窗這一次開啟的時間；0＝沒開。</summary>
    private long detailOpenedTick;

    /// <summary>這一次開啟已經處理過了（不論是按了、還是判斷後決定不按）。</summary>
    private bool handledThisOpen;

    /// <summary>我們按下加入的時間；0＝沒有待確認的動作。</summary>
    private long joinClickTick;

    /// <summary>按下加入的那一刻，畫面上是不是已經有確認框了。</summary>
    private bool yesnoAlreadyOpenAtClick;

    private string lastAction = string.Empty;

    protected override void OnEnable()
    {
        joinPrompts.Clear();
        joinPrompts.AddRange(AddonPrompt.GetTemplates(JoinPromptRows));

        // 使用者回報用：樣板解不出來（台服沒有這列）的話，确認框就完全不會被代按，
        // 而那個征兆在畫面上跟「沒開這個功能」分不出來。
        Svc.Log.Information(
            $"[{InternalName}] 加入確認框判準 {joinPrompts.Count}/{JoinPromptRows.Length} 條：{AddonPrompt.Describe(joinPrompts)}");

        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, DetailAddon, OnDetailSetup);
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, DetailAddon, OnDetailFinalize);
        Svc.Framework.Update += OnFrameworkUpdate;

        // 模組是在詳細視窗已經開著的時候被啟用的話，這一次不接手（避免「一開啟就被加入」的驚嚇）。
        detailOpenedTick = 0;
        handledThisOpen = true;
        joinClickTick = 0;
    }

    protected override void OnDisable()
    {
        Svc.Framework.Update -= OnFrameworkUpdate;
        Svc.AddonLifecycle.UnregisterListener(OnDetailSetup);
        Svc.AddonLifecycle.UnregisterListener(OnDetailFinalize);

        detailOpenedTick = 0;
        handledThisOpen = false;
        joinClickTick = 0;
        lastAction = string.Empty;
        joinPrompts.Clear();
    }

    private void OnDetailSetup(AddonEvent type, AddonArgs args)
    {
        detailOpenedTick = Environment.TickCount64;

        // 🔴 若這次詳細視窗是 NoAutoClosePartyFinder 在刷新時「程式重開」的，就不自動加入
        //    （否則兩模組同開時，別人加入／離開造成的刷新會被誤當成使用者主動開詳細而自動加入）。
        handledThisOpen = PartyFinderCoordination.ConsumeProgrammaticReopen();
    }

    private void OnDetailFinalize(AddonEvent type, AddonArgs args)
    {
        detailOpenedTick = 0;
        handledThisOpen = true;
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        TryConfirm();

        if (handledThisOpen || detailOpenedTick == 0) return;
        if (Environment.TickCount64 - detailOpenedTick < Math.Max(0, Config.DelayMs)) return;

        handledThisOpen = true;
        TryJoin();
    }

    private void TryJoin()
    {
        // 取消鍵：按著的時候這一次完全不接手，讓使用者純粹看內容。
        if (Config.CancelKeyCode != 0 && Svc.Keys[(VirtualKey)Config.CancelKeyCode])
        {
            Svc.Log.Information($"[{InternalName}] 取消鍵按著，這一則不自動加入。");
            lastAction = "上一則：按著取消鍵，未加入";
            return;
        }

        var baseAddon = UiHelper.GetAddon(DetailAddon);
        if (!UiHelper.IsReady(baseAddon)) return;

        var agent = AgentLookingForGroup.Instance();
        if (agent == null)
        {
            Svc.Log.Information($"[{InternalName}] 取不到招募 agent，這一則不自動加入。");
            return;
        }

        // 🔴 自己的招募：按下去只會得到錯誤訊息，而且那是使用者最不想被亂點的一則。
        var own = agent->OwnListingId;
        var viewed = agent->LastViewedListing.ListingId;
        if (own != 0 && viewed == own)
        {
            Svc.Log.Information($"[{InternalName}] 這是自己的招募（listingId={viewed}），不自動加入。");
            lastAction = "上一則：自己的招募，未加入";
            return;
        }

        // 密碼招募：按下去會跳出輸入密碼的視窗，我們不會也不該去填它。
        // ⚠️ JoinCondition 是旗標列舉（Free=1、PrivateParty=3），「私人」那一位是 bit1（值 2）。
        //    這個讀法無法離線證明，所以兩個方向的失敗都設計成無害：
        //    多擋了＝這一則不自動加入（使用者自己按）、少擋了＝跳出密碼視窗（使用者自己關）。
        var flags = (byte)agent->LastViewedListing.JoinConditionFlags;
        if (Config.SkipPrivate && (flags & 2) != 0)
        {
            Svc.Log.Information($"[{InternalName}] 這是密碼招募（JoinConditionFlags=0x{flags:X2}），不自動加入。");
            lastAction = "上一則：密碼招募，未加入";
            return;
        }

        var detail = (AddonLookingForGroupDetail*)baseAddon;
        var button = detail->JoinPartyButton;

        // 🔴 先確認 OwnerNode 非 null 再問 IsEnabled——CS 的 IsEnabled 直接解參考 OwnerNode，沒有判空。
        if (button == null || button->AtkComponentBase.OwnerNode == null)
        {
            Svc.Log.Information(
                $"[{InternalName}] 取不到「加入」按鈕（JoinPartyButton），不自動加入" +
                $"（JoinConditionFlags=0x{flags:X2}、IsAlliance={agent->LastViewedListing.IsAlliance}）。" +
                "團隊招募要自己挑分隊，屬正常情況。");
            lastAction = "上一則：找不到加入鈕，未加入";
            return;
        }

        if (!button->IsEnabled || !button->AtkComponentBase.OwnerNode->AtkResNode.IsVisible())
        {
            Svc.Log.Information($"[{InternalName}] 「加入」按鈕目前不可按（滿員／不符條件），不自動加入。");
            lastAction = "上一則：加入鈕不可按，未加入";
            return;
        }

        // 按下去之前先記住畫面上有沒有確認框——之後代按「是」的因果判準靠它。
        yesnoAlreadyOpenAtClick = UiHelper.IsAddonReady("SelectYesno");

        switch (UiHelper.TryClickButton(baseAddon, button))
        {
            case UiHelper.ButtonPressResult.Guarded:
                // 同一扇詳細視窗的加入鈕剛按過、還沒觀察到它收掉：不再按（handledThisOpen 已鎖，這一次就此作罷）。
                Svc.Log.Information($"[{InternalName}] 「加入」按鈕剛按過、視窗還沒收掉，不重按。");
                lastAction = "上一則：加入鈕剛按過，未重按";
                return;
            case UiHelper.ButtonPressResult.Unavailable:
                Svc.Log.Information($"[{InternalName}] 「加入」按鈕按不動（取不到事件），不自動加入。");
                lastAction = "上一則：加入鈕按不動，未加入";
                return;
        }

        joinClickTick = Environment.TickCount64;
        lastAction = "上一則：已按下加入";

        Svc.Log.Information($"[{InternalName}] 已按下「加入」（listingId={viewed}、自己的={own}）。");

        if (Config.NotifyInChat)
            Svc.Chat.Print("[TC Toolbox] 已自動按下招募的「加入」。");
    }

    /// <summary>
    /// 代按加入之後的確認框。
    /// </summary>
    /// <remarks>
    /// 🔴 四個條件同時成立才按，缺一不可：
    /// <list type="bullet">
    /// <item>是我們按過加入之後 <see cref="ConfirmWindowMs"/> 毫秒以內；</item>
    /// <item>按下加入的那一刻畫面上<b>沒有</b>確認框（否則這個框跟我們無關）；</item>
    /// <item>招募詳細視窗還開著；</item>
    /// <item><b>提示文字命中 <see cref="JoinPromptRows"/> 的加入確認句。</b></item>
    /// </list>
    /// 前三個只能排掉「按下那一刻已經在的框」；第四個才排得掉「按下之後才冒出來的外來框」。
    /// 不命中一律不按，<b>也不把 <c>joinClickTick</c> 歸零</b>——外來的框留給使用者自己按，
    /// 真正的加入確認框如果隨後才出現，窗口還在就還接得到（fail-closed）。
    /// </remarks>
    private void TryConfirm()
    {
        if (joinClickTick == 0) return;

        if (Environment.TickCount64 - joinClickTick > ConfirmWindowMs)
        {
            joinClickTick = 0;
            return;
        }

        if (!Config.ConfirmYesNo || yesnoAlreadyOpenAtClick) return;
        if (!UiHelper.IsAddonReady(DetailAddon)) return;

        var yesno = UiHelper.GetAddon("SelectYesno");
        if (!UiHelper.IsReady(yesno)) return;

        var prompt = AddonPrompt.ReadSelectYesnoText(yesno);
        if (!AddonPrompt.MatchesAny(prompt, joinPrompts))
        {
            // ⚠️ 這行是「比對假設失效」與「真的有別的框冒出來」兩種情況唯一的征兆。
            if (Throttle.Pass($"{InternalName}-PromptMiss", 10_000))
            {
                Svc.Log.Information(
                    $"[{InternalName}] 加入窗口內出現未認得的確認框，不代按：「{prompt}」" +
                    $"（目前判準：{AddonPrompt.Describe(joinPrompts)}）");
            }

            return;
        }

        if (UiHelper.ClickSelectYesnoYes())
        {
            joinClickTick = 0;
            Svc.Log.Information($"[{InternalName}] 已代按加入的確認框：「{prompt}」");
        }
    }

    public override void DrawConfig()
    {
        ImGui.SetNextItemWidth(200f);
        var delay = Config.DelayMs;
        if (ImGui.SliderInt("詳細視窗開啟後的延遲（毫秒）##pfJoinDelay", ref delay, 0, 3_000))
        {
            Config.DelayMs = Math.Clamp(delay, 0, 3_000);
            Plugin.Instance.Config.Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("留一點時間讓視窗把內容填好；也是你反悔按下取消鍵的時間。");

        var currentIndex = 0;
        for (var i = 0; i < SelectableKeys.Length; i++)
        {
            if (SelectableKeys[i].Code == Config.CancelKeyCode) currentIndex = i;
        }

        ImGui.SetNextItemWidth(200f);
        if (ImGui.BeginCombo("取消鍵##pfJoinCancelKey", SelectableKeys[currentIndex].Label))
        {
            for (var i = 0; i < SelectableKeys.Length; i++)
            {
                if (!ImGui.Selectable(SelectableKeys[i].Label, i == currentIndex)) continue;
                Config.CancelKeyCode = SelectableKeys[i].Code;
                Plugin.Instance.Config.Save();
            }

            ImGui.EndCombo();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("按著這顆鍵點開招募詳細內容時，這一則不會自動加入——純粹看內容用。");

        var skipPrivate = Config.SkipPrivate;
        if (ImGui.Checkbox("跳過密碼招募##pfJoinSkipPrivate", ref skipPrivate))
        {
            Config.SkipPrivate = skipPrivate;
            Plugin.Instance.Config.Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("密碼招募按下去只會跳出輸入密碼的視窗，我們不會幫你填。");

        var confirm = Config.ConfirmYesNo;
        if (ImGui.Checkbox("順便按掉「確定要加入…」的確認框##pfJoinConfirm", ref confirm))
        {
            Config.ConfirmYesNo = confirm;
            Plugin.Instance.Config.Save();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "只在「我們剛按過加入的 5 秒內、而且按下去那一刻畫面上沒有其他確認框」時才會代按。\n" +
                "關掉的話確認框留給你自己按。");
        }

        var notify = Config.NotifyInChat;
        if (ImGui.Checkbox("按下加入時在聊天欄顯示##pfJoinNotify", ref notify))
        {
            Config.NotifyInChat = notify;
            Plugin.Instance.Config.Save();
        }

        if (lastAction.Length > 0)
            ImGui.TextDisabled(lastAction);

        ImGui.TextColored(new Vector4(1f, 0.8f, 0.35f, 1f),
                          "⚠ 開著的時候，每一則你點開的招募都會被自動加入——只想看內容請按著取消鍵，或先關掉本模組。");
    }
}

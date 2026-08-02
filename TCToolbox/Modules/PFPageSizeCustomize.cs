using System;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 招募板單頁筆數：把「隊員招募」一頁顯示的招募數量從遊戲預設的 50 筆改成自訂值（上限 100）。
/// 機制：寫入招募資訊代理物件的單頁筆數欄位。零 hook、不改控制流、不動封包。
/// 參考 DailyRoutines PFPageSizeCustomize 設計重寫（API13、無 OmenTools 相依）。
/// </summary>
/// <remarks>
/// <para><b>離線反組譯驗證紀錄（台服 7.20 主程式 ffxiv_dx11.exe，imageBase 0x140000000）</b></para>
/// <para>
/// DR 的作法是 hook 一支函式再對它的第一個參數 <c>+0x468</c> 寫值。那支函式在台服主程式
/// 唯一命中於 <c>0x140936DF0</c>，本體確實有 <c>mov eax, [rbx+0x468]</c>——但
/// <b>「rbx 是什麼物件」DR 沒有說，也不是 AgentLookingForGroup</b>。以下是實際追出來的結論：
/// </para>
/// <list type="number">
/// <item><c>0x140936DF0</c> 只會被四支 thunk 以 <c>jmp</c> 進入（<c>0x140936DD0</c>、
/// <c>0x1409370E0</c>、<c>0x140937100</c> 及其分支），rcx 全程不變。</item>
/// <item>三處呼叫端都長這樣：<c>mov rcx, [agent+0xE0]</c> 後才 <c>call</c> thunk
/// （<c>0x14052DF92</c>／<c>0x140535419</c>／<c>0x14053545F</c>）。這幾支的 agent 身分可由
/// <c>[agent+0x31F3]</c>(CategoryTab)、<c>[agent+0x3128]</c>(OwnListingId) 等 CS 已命名欄位交叉確認。</item>
/// <item><c>agent+0xE0</c> 由 <c>GetInfoProxyById(0x14)</c> 指派（<c>mov edx, 0x14</c> 就在
/// <c>call</c> 前一行，共 6 處），<c>0x14</c> ＝ <see cref="InfoProxyId.CrossRealmParty"/>，
/// 也就是 CS 的 <c>AgentLookingForGroup.InfoProxyCrossRealm</c>。</item>
/// <item>該類別的建構式在 <c>0x140935CE0</c>：vtable 寫 +0x00、UIModule 寫 +0x08、EntryCount 寫 +0x10
/// （＝<c>InfoProxyInterface</c> 版面），memset <c>+0x480</c> 長 <c>0x13B0</c>（＝6 × 0x348 ＝
/// <c>FixedSizeArray6&lt;CrossRealmGroup&gt;</c>），末端寫到 <c>+0x1A3C</c>（＝宣告 Size 0x1A40）。
/// 完全吻合 CS 的 <see cref="InfoProxyCrossRealm"/>。</item>
/// <item>建構式在 <c>0x140935D5B</c> 寫 <c>mov dword ptr [rcx+0x468], 0x32</c>——
/// <b>遊戲自己的預設值就是 50，而且是 dword</b>（所以 DR 只寫 Int16 是在賭高 16 位是 0；這裡寫滿 dword）。</item>
/// </list>
/// <para>
/// <b>為什麼不 hook：</b>該欄位是物件的常駐成員，寫進去就一直有效（只有建構式會覆寫成 50，
/// 而建構式一個 InfoModule 生命週期只跑一次）。既然不需要攔截時機，就沒有理由去改遊戲的控制流——
/// 直接定期比對並補寫即可，連 <c>Framework.Update</c> 的成本都只是一次指標讀取加一次整數比較。
/// </para>
/// <para>
/// <b>上限 100 的依據：</b>接收端 <c>0x140531BCA</c> 有 <c>cmp ebp, 0x64 / jae</c>——
/// 招募清單累積到 100 筆時客端<b>直接拒收</b>，不會寫進 <c>_listingIds</c>，
/// 與 <c>AgentLookingForGroup.ListingsSub</c> 宣告的 <c>Size = 0x320</c>（＝100 × 8）一致。
/// 也就是說即使把值調過頭也不會有緩衝區溢位，但超過 100 沒有意義。
/// </para>
/// <para>
/// <b>這個欄位還被誰讀：</b>全主程式只有三處讀 <c>[reg+0x468]</c> 屬於本物件——
/// <c>0x140936F08</c>（寫進送出的搜尋請求）、<c>0x14093621C</c> 與 <c>0x140529A60</c>
/// （乘上頁碼算出「第 N–M 筆」的提示字串）。沒有任何一處拿它做索引，所以調大不會造成越界。
/// </para>
/// </remarks>
public sealed unsafe class PFPageSizeCustomize : TcModule
{
    public override string InternalName => "PFPageSizeCustomize";
    public override string DisplayName => "招募板單頁筆數";

    public override string Description =>
        "把「隊員招募」一頁顯示的招募筆數從遊戲預設的 50 筆改成自訂值（上限 100，超過客端會直接拒收）。" +
        "只寫入遊戲自己的設定欄位，不掛 hook、不改控制流；停用模組時會還原成 50。";

    public override bool HasConfigUI => true;

    /// <summary>遊戲自己的預設值（建構式 <c>0x140935D5B</c> 寫入的常數）。</summary>
    public const int GameDefaultPageSize = 50;

    /// <summary>客端接收上限（<c>0x140531BCA</c> 的 <c>cmp ebp, 0x64</c>）。</summary>
    public const int MaxPageSize = 100;

    public const int MinPageSize = 1;

    /// <summary>
    /// 單頁筆數欄位在 <see cref="InfoProxyCrossRealm"/> 內的位移（dword）。
    /// CS 沒有替這個欄位命名（宣告裡 0x468 是空的），但物件本體與大小都已離線證明，見型別註解。
    /// </summary>
    private const int PageSizeOffset = 0x468;

    /// <summary>補寫檢查的間隔；只做一次指標讀取與整數比較，值相同就不寫。</summary>
    private const int ApplyIntervalMs = 1000;

    private const string ThrottleKey = "PFPageSizeCustomize.Apply";

    private PFPageSizeCustomizeConfig Config => Plugin.Instance.Config.PfPageSize;

    private int PageSize => Math.Clamp(Config.PageSize, MinPageSize, MaxPageSize);

    protected override void OnEnable()
    {
        Throttle.Reset(ThrottleKey);
        Svc.Framework.Update += OnUpdate;
        Apply(PageSize);
    }

    protected override void OnDisable()
    {
        Svc.Framework.Update -= OnUpdate;
        Throttle.Reset(ThrottleKey);

        // 不還原的話使用者停用模組後招募板還是停在自訂筆數，那等於模組關不掉
        Apply(GameDefaultPageSize);
    }

    private void OnUpdate(IFramework framework)
    {
        if (!Throttle.Pass(ThrottleKey, ApplyIntervalMs)) return;
        Apply(PageSize);
    }

    /// <summary>取得招募資訊代理物件；拿不到就回 null（登入前、切換角色時都可能是 null）。</summary>
    private static InfoProxyCrossRealm* GetProxy()
    {
        var infoModule = InfoModule.Instance();
        if (infoModule == null) return null;

        return (InfoProxyCrossRealm*)infoModule->GetInfoProxyById(InfoProxyId.CrossRealmParty);
    }

    private void Apply(int value)
    {
        try
        {
            var proxy = GetProxy();
            if (proxy == null) return;

            var field = (int*)((byte*)proxy + PageSizeOffset);
            if (*field == value) return;

            *field = value;
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, $"[{InternalName}] 寫入單頁筆數失敗");
        }
    }

    public override void DrawConfig()
    {
        ImGui.SetNextItemWidth(200f);
        var pageSize = PageSize;
        if (ImGui.SliderInt("單頁筆數", ref pageSize, MinPageSize, MaxPageSize))
        {
            Config.PageSize = Math.Clamp(pageSize, MinPageSize, MaxPageSize);
            Plugin.Instance.Config.Save();
            Apply(Config.PageSize);
        }

        using (ImRaii.PushIndent())
        {
            ImGui.TextDisabled($"遊戲預設 {GameDefaultPageSize}，拉到 {MaxPageSize} 可讓一頁塞滿。");
            ImGui.TextDisabled($"{MaxPageSize} 是客端硬上限——再多遊戲自己就不收了，設更大沒有意義。");
        }

        ImGui.Spacing();
        if (ImGui.Button($"還原遊戲預設（{GameDefaultPageSize}）"))
        {
            Config.PageSize = GameDefaultPageSize;
            Plugin.Instance.Config.Save();
            Apply(GameDefaultPageSize);
        }

        ImGui.Spacing();
        var proxyReady = GetProxy() != null;
        if (proxyReady)
        {
            var current = *(int*)((byte*)GetProxy() + PageSizeOffset);
            ImGui.TextDisabled($"目前遊戲內的值：{current}");
        }
        else
        {
            ImGui.TextDisabled("目前取不到招募資訊代理（尚未登入？），登入後會自動套用。");
        }
    }
}

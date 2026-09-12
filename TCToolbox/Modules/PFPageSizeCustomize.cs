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
/// <b>為什麼不 hook：</b>該欄位是物件的常駐成員，寫進去就一直有效（只有建構式會覆寫成 50，
/// 而建構式一個 InfoModule 生命週期只跑一次）。既然不需要攔截時機，就沒有理由去改遊戲的控制流——
/// 直接定期比對並補寫即可，連 <c>Framework.Update</c> 的成本都只是一次指標讀取加一次整數比較。
/// <b>上限 100 的依據：</b>接收端 <c>0x140531BCA</c> 有 <c>cmp ebp, 0x64 / jae</c>——
/// 招募清單累積到 100 筆時客端<b>直接拒收</b>，不會寫進 <c>_listingIds</c>，
/// 與 <c>AgentLookingForGroup.ListingsSub</c> 宣告的 <c>Size = 0x320</c>（＝100 × 8）一致。
/// 也就是說即使把值調過頭也不會有緩衝區溢位，但超過 100 沒有意義。
/// </remarks>
public sealed unsafe class PFPageSizeCustomize : TcModule
{
    public override string InternalName => "PFPageSizeCustomize";
    public override string DisplayName => "招募板單頁筆數";

    public override string Description =>
        "把「隊員招募」一頁顯示的招募筆數從遊戲預設的 50 筆改成自訂值（上限 100，超過客端會直接拒收）。" +
        "只寫入遊戲自己的設定欄位，不掛 hook、不改控制流；停用模組時會還原成 50。";

    public override ModuleCategory Category => ModuleCategory.Combat;

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

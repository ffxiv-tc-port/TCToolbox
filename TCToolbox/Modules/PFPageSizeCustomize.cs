using System;
using System.Numerics;
using System.Runtime.InteropServices;
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
/// <para>契約：欄位位移在啟用時由 <see cref="PfPageSizeLayoutDeriver"/> 從遊戲碼推導，推不出就不啟用；
/// 每次讀寫前要求遊戲的實例全域指標等於要寫的物件，不等就不碰。</para>
/// </remarks>
public sealed unsafe class PFPageSizeCustomize : TcModule
{
    public override string InternalName => "PFPageSizeCustomize";
    public override string DisplayName => "招募板單頁筆數";

    public override string Description =>
        "把「隊員招募」一頁顯示的招募筆數從遊戲預設的 50 筆改成自訂值（上限 100，超過客端會直接拒收）。" +
        "只寫入遊戲自己的設定欄位，不掛 hook、不改控制流；停用模組時會還原成 50。" +
        "遊戲改版後若推不出欄位位置，模組會自動停用並在聊天視窗說明。";

    public override ModuleCategory Category => ModuleCategory.Combat;

    public override bool HasConfigUI => true;

    /// <summary>遊戲自己的預設值（建構式寫入的常數）。</summary>
    public const int GameDefaultPageSize = 50;

    /// <summary>客端接收上限（客端自己有一道 <c>cmp ebp, 0x64</c>）。</summary>
    public const int MaxPageSize = 100;

    public const int MinPageSize = 1;

    /// <summary>補寫檢查的間隔；只做一次指標讀取與整數比較，值相同就不寫。</summary>
    private const int ApplyIntervalMs = 1000;

    /// <summary>實例指標連續幾次（每秒一次）對不上才判定指紋不符；之前只是跳過不寫。</summary>
    private const int OwnerMismatchLimit = 5;

    private const ulong MaxUserAddress = 0x00007FFFFFFFFFFF;

    /// <summary>整個行程只推導一次（執行檔在執行中不會變）；推導後兩者恰有一個非 null。</summary>
    private static PfPageSizeLayout? layout;
    private static string? layoutFailure;

    private string? runtimeFailure;
    private int ownerMismatchStreak;
    private long nextApplyTick;

    private PFPageSizeCustomizeConfig Config => Plugin.Instance.Config.PfPageSize;

    private int PageSize => Math.Clamp(Config.PageSize, MinPageSize, MaxPageSize);

    private string? Failure => layoutFailure ?? runtimeFailure;

    public override ModuleNotice? RowNotice => Failure is { } reason
        ? new ModuleNotice(ModuleNoticeLevel.Warning, "這個遊戲版本無法使用，已停用", reason)
        : null;

    protected override void OnEnable()
    {
        EnsureLayout();
        if (Failure is { } failure)
        {
            ModuleFailureNotice.Queue(InternalName, DisplayName, failure);
            throw new InvalidOperationException($"不啟用：{failure}");
        }

        ownerMismatchStreak = 0;
        nextApplyTick = 0;
        Svc.Framework.Update += OnUpdate;
    }

    protected override void OnDisable()
    {
        Svc.Framework.Update -= OnUpdate;

        // 不還原的話使用者停用模組後招募板還是停在自訂筆數，那等於模組關不掉
        Apply(GameDefaultPageSize);
    }

    private void OnUpdate(IFramework framework)
    {
        var now = Environment.TickCount64;
        if (now < nextApplyTick) return;

        nextApplyTick = now + ApplyIntervalMs;
        Apply(PageSize, countMismatch: true);
    }

    private void EnsureLayout()
    {
        if (layout != null || layoutFailure != null) return;

        string failure;
        try
        {
            var cs = PfPageSizeLayoutDeriver.Expectations.FromClientStructs(out failure);
            if (cs is { } expectations)
            {
                var scanner = Svc.SigScanner;
                var dataStart = scanner.Module.BaseAddress + (nint)scanner.DataSectionOffset;
                var derived = PfPageSizeLayoutDeriver.Derive(
                    ScanText, ReadCode, dataStart, dataStart + scanner.DataSectionSize, expectations, out failure);
                if (derived != null)
                {
                    layout = derived;
                    Svc.Log.Information(
                        $"[{InternalName}] 由遊戲碼推出單頁筆數欄位位移 0x{derived.PageSizeOffset:X}（建立端與讀取端一致、與結構定義相符）。");
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            failure = $"推導欄位位置時發生例外（{ex.GetType().Name}）";
        }

        layoutFailure = failure;
    }

    private static nint ScanText(string signature) =>
        Svc.SigScanner.TryScanText(signature, out var address) ? address : 0;

    /// <summary>讀 .text 的指令位元組；Dalamud 的掃描器是副本模式時讀副本（不受別人的 hook 影響）。</summary>
    private static byte[]? ReadCode(nint address, int length)
    {
        var scanner = Svc.SigScanner;
        var rva = (long)address - scanner.Module.BaseAddress;
        if (rva < scanner.TextSectionOffset || rva + length > scanner.TextSectionOffset + scanner.TextSectionSize)
            return null;

        var bytes = new byte[length];
        Marshal.Copy(scanner.SearchBase + (nint)rva, bytes, 0, length);
        return bytes;
    }

    /// <summary>取得招募資訊代理物件；拿不到就回 null（登入前、切換角色時都可能是 null）。</summary>
    private static InfoProxyCrossRealm* GetProxy()
    {
        if (InfoModule.MemberFunctionPointers.GetInfoProxyById == null) return null;

        var infoModule = InfoModule.Instance();
        if (infoModule == null) return null;

        return (InfoProxyCrossRealm*)infoModule->GetInfoProxyById(InfoProxyId.CrossRealmParty);
    }

    /// <summary>通過指紋閘門才給欄位指標；只有每秒那次檢查會累計「實例指標對不上」。</summary>
    private bool TryGetPageSizeField(bool countMismatch, out int* field)
    {
        field = null;
        if (layout is not { } l || runtimeFailure != null) return false;

        var proxy = GetProxy();
        if (proxy == null)
        {
            ownerMismatchStreak = 0;
            return false;
        }

        if ((ulong)proxy > MaxUserAddress) return false;

        if (*(nint*)l.InstanceSlot != (nint)proxy)
        {
            if (countMismatch && ++ownerMismatchStreak >= OwnerMismatchLimit)
                FailAtRuntime("遊戲記錄的招募資料物件與插件取得的不是同一個（指紋不符）");
            return false;
        }

        ownerMismatchStreak = 0;
        field = (int*)((byte*)proxy + l.PageSizeOffset);
        return true;
    }

    private void FailAtRuntime(string reason)
    {
        runtimeFailure = reason;
        Svc.Framework.Update -= OnUpdate;
        Svc.Log.Error($"[{InternalName}] 停止寫入：{reason}");
        ModuleFailureNotice.Queue(InternalName, DisplayName, reason);
    }

    private void Apply(int value, bool countMismatch = false)
    {
        try
        {
            if (!TryGetPageSizeField(countMismatch, out var field)) return;
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
        if (Failure is { } reason)
        {
            ImGui.TextColored(new Vector4(1f, 0.6f, 0.2f, 1f), $"這個遊戲版本無法使用：{reason}");
            return;
        }

        ImGui.SetNextItemWidth(200f);
        var pageSize = PageSize;
        if (ImGui.SliderInt("單頁筆數", ref pageSize, MinPageSize, MaxPageSize))
        {
            Config.PageSize = Math.Clamp(pageSize, MinPageSize, MaxPageSize);
            Plugin.Instance.Config.Save();
            if (IsEnabled) Apply(Config.PageSize);
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
            if (IsEnabled) Apply(GameDefaultPageSize);
        }

        ImGui.Spacing();
        if (!IsEnabled)
            ImGui.TextDisabled("模組開啟後才會套用。");
        else if (TryGetPageSizeField(false, out var field))
            ImGui.TextDisabled($"目前遊戲內的值：{*field}");
        else
            ImGui.TextDisabled("目前取不到招募資訊代理（尚未登入？），登入後會自動套用。");
    }
}

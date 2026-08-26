using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Hooking;
using Dalamud.Interface.Utility.Raii;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 自動隱藏橫幅：把選定的畫面中央大橫幅（理符任務、F.A.T.E.、部隊探索…）連同音效一起攔掉。
/// 機制：hook 遊戲的「橫幅設圖」函式，選中的橫幅 ID 直接不呼叫原函式——橫幅節點不會被建立、
/// 音效也不會播。不寫入任何遊戲記憶體、不做 code patch。
/// 參考 DailyRoutines AutoHideBanners 設計重寫（API13、無 OmenTools／KamiToolKit 相依）。
/// </summary>
public sealed unsafe class AutoHideBanners : TcModule
{
    public override string InternalName => "AutoHideBanners";
    public override string DisplayName => "自動隱藏橫幅";

    public override string Description =>
        "勾選要屏蔽的畫面橫幅後，該橫幅與其音效都不再彈出（理符任務、F.A.T.E.、尋寶、部隊探索、" +
        "友好部族、金碟 GATE、宇宙探索）。預設已勾好最常重複跳的幾張，可自行增減。";

    public override ModuleCategory Category => ModuleCategory.Misc;

    public override bool HasConfigUI => true;

    /// <summary>遊戲的橫幅設圖函式（sig 已對台服 7.20 主程式離線驗證，唯一命中）。</summary>
    private const string SetImageSignature = "48 89 5C 24 ?? 57 48 83 EC 30 48 8B D9 89 91";

    /// <summary>
    /// SimpleTweaks 裡做同一件事的 tweak 鍵（完整識別是 <c>UiAdjustments@HideUnwantedBanner</c>）。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>不是「功能相似」，是同一支遊戲函式</b>：SimpleTweaks
    /// <c>Tweaks/UiAdjustment/HideUnwantedBanner.cs</c> 用的特徵碼與上面的
    /// <see cref="SetImageSignature"/> <b>逐字元相同</b>——2026-08-07 直接對使用者機器上安裝的
    /// <c>SimpleTweaksPlugin.dll</c>（1.10.12.0）數字串命中數：UTF-8 恰好 1 次、UTF-16LE 0 次。
    /// 兩邊要屏蔽的橫幅 id 清單也高度重疊。
    /// <para>
    /// ⚠️ 這裡刻意<b>只顯示提示、不自動關掉任何一邊</b>：兩個都開不會壞，
    /// 而替使用者裁決「留哪一邊」不是這個模組該做的事。
    /// </para>
    /// </remarks>
    private const string SimpleTweaksBannerTweak = "HideUnwantedBanner";

    /// <summary>
    /// 與 SimpleTweaks 重疊時，在模組列上直接顯示（不是藏在 tooltip）。
    /// </summary>
    /// <remarks>
    /// 三種狀態各有各的顯示，其中「不知道」也一定看得見——把未知畫成「沒事」會直接誤導使用者。
    /// SimpleTweaks 沒裝、或那個 tweak 確認是關的，就什麼都不顯示（列面保持乾淨）。
    /// </remarks>
    public override ModuleNotice? RowNotice => SimpleTweaksProbe.Query(SimpleTweaksBannerTweak) switch
    {
        ConflictState.Active => new ModuleNotice(
            ModuleNoticeLevel.Warning,
            "! 與 SimpleTweaks 重複",
            "SimpleTweaks 也裝著，而且它的「隱藏不需要的橫幅」(UiAdjustments@HideUnwantedBanner) 是開著的。\n" +
            "兩邊掛的是遊戲同一支橫幅設圖函式（特徵碼逐字元相同），要屏蔽的橫幅清單也高度重疊。\n" +
            "\n" +
            "同時開著通常不會壞，但實際被擋掉的是「兩份清單的聯集」：\n" +
            "在這裡取消勾選某張橫幅，如果 SimpleTweaks 那邊還勾著，橫幅還是不會出現——\n" +
            "而且遊戲裡看不出來是誰擋的。\n" +
            "\n" +
            "建議只留一邊：到 SimpleTweaks 把那個 tweak 關掉，或把這個模組關掉。"),

        ConflictState.Unknown => new ModuleNotice(
            ModuleNoticeLevel.Unknown,
            "? SimpleTweaks 狀態未知",
            "SimpleTweaks 裝著，但讀不到它的設定檔，無法判斷它的「隱藏不需要的橫幅」是不是也開著。\n" +
            $"原因：{(string.IsNullOrEmpty(SimpleTweaksProbe.LastError) ? "（未記錄）" : SimpleTweaksProbe.LastError)}\n" +
            $"設定檔：{(string.IsNullOrEmpty(SimpleTweaksProbe.ConfigPath) ? "（路徑取不到）" : SimpleTweaksProbe.ConfigPath)}\n" +
            "\n" +
            "如果那個 tweak 其實是開著的，兩邊會擋到同一批橫幅，改設定會出現「取消勾選了卻還是不出現」。"),

        _ => null,
    };

    private delegate void SetImageDelegate(nint addonImage, int bannerId, int iconSubFolder, int soundEffectId);

    private Hook<SetImageDelegate>? setImageHook;

    private sealed record BannerInfo(uint Id, string Name, string Category);

    private const string CategoryLeve = "理符・籌備任務";
    private const string CategoryFate = "F.A.T.E.";
    private const string CategoryTreasure = "尋寶";
    private const string CategoryExpedition = "部隊探索・探索之旅";
    private const string CategoryTribe = "友好部族";
    private const string CategoryGate = "金碟遊樂場 GATE";
    private const string CategoryCosmic = "宇宙探索";

    /// <summary>
    /// 橫幅清單。
    /// <para>
    /// 名稱不是猜的，也不是任何 Excel 表提供的：ScreenImage 表只有 Image／Jingle／Type／Lang 四欄，
    /// 全 1138 張台服表裡沒有任何一張替這些圖示命名。作法是**把台服 sqpack 內的材質實際解出來看**——
    /// 以 Lumina 讀 <c>ui/icon/{folder}/tc/{id}_hr1.tex</c>（DXT5、2560x720）轉 PNG 後逐張辨識，
    /// 下列文字即橫幅上實際顯示的台服字樣。
    /// </para>
    /// <para>
    /// 分類沿用台服官方用語；Jingle 表（ScreenImage.Jingle → Jingle.Name，如 Que_Start／Fate_Clear／
    /// Gate_Enc）只拿來交叉驗證分類，不當顯示名用。
    /// </para>
    /// <para>
    /// ⚠️ DR 清單裡的 128525–128532（宇宙探索任務鏈橫幅）經 sqpack 索引比對，
    /// **台服 7.20 客端根本沒有這些材質**（該內容尚未實裝），因此整組移除——列出來也勾不到東西。
    /// </para>
    /// </summary>
    private static readonly BannerInfo[] Banners =
    [
        new(120031, "接受理符任務！", CategoryLeve),
        new(120032, "理符任務完成！", CategoryLeve),
        new(120055, "籌備任務完成！", CategoryLeve),

        new(120081, "F.A.T.E. 開始！", CategoryFate),
        new(120082, "F.A.T.E. 完成！", CategoryFate),
        new(120083, "F.A.T.E. 失敗……", CategoryFate),
        new(120084, "F.A.T.E. 開始！（額外獎勵）", CategoryFate),
        new(120085, "F.A.T.E. 完成！（額外獎勵）", CategoryFate),
        new(120086, "F.A.T.E. 失敗……（額外獎勵）", CategoryFate),

        new(120094, "發現寶箱！", CategoryTreasure),
        new(120093, "獲得寶箱！", CategoryTreasure),

        new(120095, "出發探險！", CategoryExpedition),
        new(120096, "平安歸還！", CategoryExpedition),
        new(120141, "探索之旅開始", CategoryExpedition),
        new(120142, "探索之旅結束", CategoryExpedition),

        new(121081, "友好部族　接受任務！", CategoryTribe),
        new(121082, "友好部族　任務完成！", CategoryTribe),

        new(121561, "GATE 開始！", CategoryGate),
        new(121562, "GATE 完成！", CategoryGate),
        new(121563, "GATE 失敗……", CategoryGate),

        new(128370, "執行探索任務", CategoryCosmic),
        new(128371, "放棄探索任務", CategoryCosmic),
        new(128372, "探索任務失敗……", CategoryCosmic),
        new(128373, "探索任務完成！", CategoryCosmic),
    ];

    private static readonly string[] CategoryOrder =
    [
        CategoryLeve, CategoryFate, CategoryTreasure, CategoryExpedition,
        CategoryTribe, CategoryGate, CategoryCosmic,
    ];

    /// <summary>首次啟用時預設勾選（跑日常時最常重複跳的那幾張）。</summary>
    private static readonly uint[] DefaultHiddenBanners =
        [120031, 120032, 120055, 120095, 120096, 120141, 120142];

    private static readonly HashSet<uint> KnownBannerIds = BuildKnownIds();

    private static HashSet<uint> BuildKnownIds()
    {
        var set = new HashSet<uint>(Banners.Length);
        foreach (var banner in Banners)
            set.Add(banner.Id);
        return set;
    }

    private string searchFilter = string.Empty;

    private AutoHideBannersConfig Config => Plugin.Instance.Config.HideBanners;

    protected override void OnEnable()
    {
        var configChanged = false;

        if (!Config.Initialized)
        {
            Config.Initialized = true;
            foreach (var id in DefaultHiddenBanners)
                Config.HiddenBanners.Add(id);
            configChanged = true;
        }

        // 清掉舊版留下、台服其實不存在的橫幅 ID（128525–128532）
        configChanged |= Config.HiddenBanners.RemoveWhere(id => !KnownBannerIds.Contains(id)) > 0;

        if (configChanged)
            Plugin.Instance.Config.Save();

        var address = Svc.SigScanner.ScanText(SetImageSignature);
        setImageHook = Svc.Hooks.HookFromAddress<SetImageDelegate>(address, SetImageDetour);
        setImageHook.Enable();
    }

    protected override void OnDisable()
    {
        setImageHook?.Dispose();
        setImageHook = null;
    }

    private void SetImageDetour(nint addonImage, int bannerId, int iconSubFolder, int soundEffectId)
    {
        // 🔴 OnDisable() 會把 hook 欄位設回 null，而 detour 可能還在執行中（in-flight 呼叫）。
        //    `!.` 只是叫編譯器閉嘴，執行期照樣是裸解參考 —— 欄位一為 null 就把
        //    NullReferenceException 擲回原生呼叫端，而且原始函式完全沒被呼叫。
        //    快照一次到區域變數，之後只用區域變數，不要對欄位做第二次讀取。
        var hook = setImageHook;

        try
        {
            if (bannerId > 0 && Config.HiddenBanners.Contains((uint)bannerId))
                return;
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, $"[{InternalName}] 橫幅過濾判定失敗，本次照常顯示");
        }

        if (hook == null)
        {
            Svc.Log.Information(
                $"[{InternalName}] 橫幅 hook 已在呼叫途中被卸載，略過本次原始呼叫。");
            return;
        }

        hook.OriginalDisposeSafe(addonImage, bannerId, iconSubFolder, soundEffectId);
    }

    public override void DrawConfig()
    {
        ImGui.TextDisabled("勾起來＝該橫幅與其音效都不再出現。");

        ImGui.SetNextItemWidth(220f);
        var filter = searchFilter;
        if (ImGui.InputTextWithHint("##bannerSearch", "搜尋名稱或編號…", ref filter, 64))
            searchFilter = filter;

        ImGui.SameLine();
        var showPreview = Config.ShowPreview;
        if (ImGui.Checkbox("顯示預覽圖", ref showPreview))
        {
            Config.ShowPreview = showPreview;
            Plugin.Instance.Config.Save();
        }

        ImGui.SameLine();
        if (ImGui.Button("還原預設"))
        {
            Config.HiddenBanners.Clear();
            foreach (var id in DefaultHiddenBanners)
                Config.HiddenBanners.Add(id);
            Plugin.Instance.Config.Save();
        }

        ImGui.SameLine();
        if (ImGui.Button("全部取消"))
        {
            Config.HiddenBanners.Clear();
            Plugin.Instance.Config.Save();
        }

        using var child = ImRaii.Child("TCToolboxBannerList",
                                       new Vector2(ImGui.GetContentRegionAvail().X, 420f), true);
        if (!child) return;

        var matched = 0;
        foreach (var category in CategoryOrder)
        {
            var headerDrawn = false;

            foreach (var banner in Banners)
            {
                if (banner.Category != category) continue;
                if (!Matches(banner)) continue;

                if (!headerDrawn)
                {
                    if (matched > 0) ImGui.Spacing();
                    ImGui.Separator();
                    ImGui.TextColored(new Vector4(1f, 0.85f, 0.35f, 1f), category);
                    headerDrawn = true;
                }

                matched++;
                DrawBannerEntry(banner);
            }
        }

        if (matched == 0)
            ImGui.TextDisabled("沒有符合的橫幅。");
    }

    private bool Matches(BannerInfo banner)
    {
        if (searchFilter.Length == 0) return true;
        return banner.Name.Contains(searchFilter, StringComparison.OrdinalIgnoreCase) ||
               banner.Category.Contains(searchFilter, StringComparison.OrdinalIgnoreCase) ||
               banner.Id.ToString().Contains(searchFilter, StringComparison.Ordinal);
    }

    private void DrawBannerEntry(BannerInfo banner)
    {
        using var id = ImRaii.PushId((int)banner.Id);

        var hidden = Config.HiddenBanners.Contains(banner.Id);
        if (ImGui.Checkbox(banner.Name, ref hidden))
        {
            if (hidden)
                Config.HiddenBanners.Add(banner.Id);
            else
                Config.HiddenBanners.Remove(banner.Id);
            Plugin.Instance.Config.Save();
        }

        ImGui.SameLine();
        ImGui.TextDisabled($"#{banner.Id}");

        if (Array.IndexOf(DefaultHiddenBanners, banner.Id) >= 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.55f, 0.85f, 1f, 1f), "[預設]");
        }

        if (!Config.ShowPreview) return;

        var wrap = GameIcons.TryGetLanguageIcon(banner.Id);
        if (wrap == null)
        {
            using (ImRaii.PushIndent())
                ImGui.TextDisabled("（找不到這張橫幅的材質，屏蔽功能不受影響）");
            return;
        }

        using (ImRaii.PushIndent())
        {
            // 橫幅原圖是 2560x720 的寬幅；縮到約 360px 寬就足以看清字樣
            var width = Math.Min(360f, ImGui.GetContentRegionAvail().X);
            var height = width * wrap.Height / Math.Max(1, wrap.Width);
            var tint = hidden ? new Vector4(1f, 0.5f, 0.5f, 0.75f) : new Vector4(1f, 1f, 1f, 1f);

            ImGui.Image(wrap.Handle, new Vector2(width, height), Vector2.Zero, Vector2.One, tint);
        }
    }
}

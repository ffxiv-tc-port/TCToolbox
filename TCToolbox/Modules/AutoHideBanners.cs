using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Hooking;
using Dalamud.Interface.Utility.Raii;
using FFXIVClientStructs.FFXIV.Component.GUI;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 自動隱藏橫幅：把選定的畫面中央大橫幅（升級、任務完成、開怪等）連同音效一起攔掉。
/// 機制：hook 遊戲的「橫幅設圖」函式，選中的橫幅 ID 直接不呼叫原函式——橫幅不會被建立、
/// 音效也不會播；另外針對宇宙探索的任務鏈橫幅 addon 額外做節點隱藏。
/// 不寫入任何遊戲記憶體、不做 code patch。
/// 參考 DailyRoutines AutoHideBanners 設計重寫（API13、無 OmenTools／KamiToolKit 相依）。
/// </summary>
public sealed unsafe class AutoHideBanners : TcModule
{
    public override string InternalName => "AutoHideBanners";
    public override string DisplayName => "自動隱藏橫幅";

    public override string Description =>
        "勾選要屏蔽的畫面橫幅後，該橫幅與其音效都不再彈出（升級、任務完成、副本開始／結束、開怪等）。" +
        "預設已勾好幾個最吵的，可自行增減。";

    public override bool HasConfigUI => true;

    /// <summary>遊戲的橫幅設圖函式（sig 已對台服 7.20 主程式離線驗證，唯一命中）。</summary>
    private const string SetImageSignature = "48 89 5C 24 ?? 57 48 83 EC 30 48 8B D9 89 91";

    private delegate void SetImageDelegate(nint addonImage, int bannerId, int iconSubFolder, int soundEffectId);

    private Hook<SetImageDelegate>? setImageHook;

    /// <summary>可屏蔽的橫幅圖示 ID（取自 DR 的清單，涵蓋 7.x 目前全部中央橫幅）。</summary>
    private static readonly uint[] BannerIds =
    [
        120031, 120032, 120055, 120081, 120082, 120083, 120084, 120085, 120086,
        120093, 120094, 120095, 120096, 120141, 120142, 121081, 121082, 121561,
        121562, 121563, 128370, 128371, 128372, 128373, 128525, 128526,
        128527, 128528, 128529, 128530, 128531, 128532,
    ];

    /// <summary>宇宙探索任務鏈橫幅：這幾個是獨立 addon，另外要隱藏節點。</summary>
    private static readonly HashSet<uint> MissionChainBannerIds =
        [128527, 128528, 128529, 128530, 128531, 128532];

    /// <summary>首次啟用時預設勾選（最吵的幾個）。</summary>
    private static readonly uint[] DefaultHiddenBanners =
        [120031, 120032, 120055, 120095, 120096, 120141, 120142];

    private AutoHideBannersConfig Config => Plugin.Instance.Config.HideBanners;

    protected override void OnEnable()
    {
        if (!Config.Initialized)
        {
            Config.Initialized = true;
            foreach (var id in DefaultHiddenBanners)
                Config.HiddenBanners.Add(id);
            Plugin.Instance.Config.Save();
        }

        var address = Svc.SigScanner.ScanText(SetImageSignature);
        setImageHook = Svc.Hooks.HookFromAddress<SetImageDelegate>(address, SetImageDetour);
        setImageHook.Enable();

        Svc.AddonLifecycle.RegisterListener(AddonEvent.PreDraw, "_WKSMissionChain", OnMissionChainPreDraw);
    }

    protected override void OnDisable()
    {
        Svc.AddonLifecycle.UnregisterListener(OnMissionChainPreDraw);

        setImageHook?.Dispose();
        setImageHook = null;

        // 還原可能被我們隱藏的任務鏈橫幅節點
        var addon = UiHelper.GetAddon("_WKSMissionChain");
        if (addon != null && addon->RootNode != null)
            addon->RootNode->ToggleVisibility(true);
    }

    private void SetImageDetour(nint addonImage, int bannerId, int iconSubFolder, int soundEffectId)
    {
        try
        {
            if (bannerId > 0 && Config.HiddenBanners.Contains((uint)bannerId))
                return;
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, $"[{InternalName}] 橫幅過濾判定失敗，本次照常顯示");
        }

        setImageHook!.Original(addonImage, bannerId, iconSubFolder, soundEffectId);
    }

    private void OnMissionChainPreDraw(AddonEvent type, AddonArgs args)
    {
        var addon = (AtkUnitBase*)args.Addon.Address;
        if (addon == null || addon->RootNode == null) return;

        var iconId = FindFirstImageIconId(addon);

        var shouldHide = iconId != 0 &&
                         MissionChainBannerIds.Contains(iconId) &&
                         Config.HiddenBanners.Contains(iconId);

        if (addon->RootNode->IsVisible() == !shouldHide) return;
        addon->RootNode->ToggleVisibility(!shouldHide);
    }

    /// <summary>取 addon 內第一個圖片節點目前載入的圖示 ID（走 PartsList → UldAsset → AtkTextureResource.IconId）。</summary>
    private static uint FindFirstImageIconId(AtkUnitBase* addon)
    {
        for (var i = 0; i < addon->UldManager.NodeListCount; i++)
        {
            var node = addon->UldManager.NodeList[i];
            if (node == null || node->Type != NodeType.Image) continue;

            var imageNode = (AtkImageNode*)node;
            if (imageNode->PartsList == null) continue;
            if (imageNode->PartId >= imageNode->PartsList->PartCount) continue;

            var asset = imageNode->PartsList->Parts[imageNode->PartId].UldAsset;
            if (asset == null || asset->AtkTexture.TextureType != TextureType.Resource) continue;

            var resource = asset->AtkTexture.Resource;
            if (resource == null || resource->IconId == 0) continue;

            return resource->IconId;
        }

        return 0;
    }

    public override void DrawConfig()
    {
        ImGui.TextDisabled("點圖片（或核取方塊）切換：勾起來＝該橫幅與其音效都不再出現。");

        var showPreview = Config.ShowPreview;
        if (ImGui.Checkbox("顯示橫幅預覽圖", ref showPreview))
        {
            Config.ShowPreview = showPreview;
            Plugin.Instance.Config.Save();
        }

        var tableSize = new Vector2(ImGui.GetContentRegionAvail().X, Config.ShowPreview ? 400f : 200f);
        using var table = ImRaii.Table("TCToolboxBannerList", 2,
                                       ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY,
                                       tableSize);
        if (!table) return;

        ImGui.TableSetupColumn("左", ImGuiTableColumnFlags.WidthStretch, 50f);
        ImGui.TableSetupColumn("右", ImGuiTableColumnFlags.WidthStretch, 50f);

        var perColumn = (BannerIds.Length + 1) / 2;
        for (var i = 0; i < perColumn; i++)
        {
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            DrawBannerEntry(BannerIds[i]);

            ImGui.TableNextColumn();
            var right = i + perColumn;
            if (right < BannerIds.Length)
                DrawBannerEntry(BannerIds[right]);
        }
    }

    private void DrawBannerEntry(uint bannerId)
    {
        using var id = ImRaii.PushId((int)bannerId);

        var hidden = Config.HiddenBanners.Contains(bannerId);
        if (ImGui.Checkbox($"#{bannerId}", ref hidden))
        {
            if (hidden)
                Config.HiddenBanners.Add(bannerId);
            else
                Config.HiddenBanners.Remove(bannerId);
            Plugin.Instance.Config.Save();
        }

        if (!Config.ShowPreview) return;

        var wrap = GameIcons.TryGetLanguageIcon(bannerId);
        if (wrap == null) return;

        var width = ImGui.GetContentRegionAvail().X;
        var height = width * wrap.Height / Math.Max(1, wrap.Width);
        var tint = hidden ? new Vector4(1f, 0.45f, 0.45f, 0.6f) : new Vector4(1f, 1f, 1f, 1f);

        ImGui.Image(wrap.Handle, new Vector2(width, height), Vector2.Zero, Vector2.One, tint);
    }
}

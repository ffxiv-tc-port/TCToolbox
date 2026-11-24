using System.Numerics;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace TCToolbox.Core;

/// <summary>每幀讀一次 _NaviMap 的數值（抄成值型別），給 <see cref="MinimapProjection"/> 用。</summary>
/// <remarks>
/// 🔴 不保存任何原生指標：每次呼叫都從 GameGui 重取，回傳的全是值。任一步不成立就回 false＝呼叫端這幀不畫。
/// 節點編號取自台服 ULD（ui/uld/NaviMap.uld）：4＝鎖定勾選框、8＝旋轉的方位環、18＝地圖元件（其中圖片 6 的 ScaleX＝縮放），
/// 與 MiniMappingway 的 Manager/NaviMapManager.cs 相同。
/// 節點用 NodeList 迴圈自己找（只讀欄位），不呼叫遊戲的 GetNodeById。
/// </remarks>
public static unsafe class NaviMapReader
{
    public const uint LockNodeId = 4;
    public const uint RotationNodeId = 8;
    public const uint MapComponentNodeId = 18;
    public const uint ZoomImageNodeId = 6;

    /// <param name="MapNodeCenter">地圖元件節點 18 的螢幕中心（ScreenX/Y＋寬高一半×縮放），與換算中心獨立，做對準檢查用。</param>
    public readonly record struct Reading(
        Vector2 AddonPos,
        float AddonScale,
        float Zoom,
        float Rotation,
        bool Locked,
        Vector2 AddonSize,
        Vector2 MapNodeCenter);

    public enum Failure
    {
        None = 0,
        NoAddon,
        Hidden,
        NotLoaded,
        NodeMismatch,
        MapComponentNotLoaded,
        NoZoomImage,
    }

    /// <summary>給 log 的原因說明（只在要寫 log 時才呼叫，每幀路徑不組字串）。</summary>
    public static string Describe(Failure f) => f switch
    {
        Failure.NoAddon => "找不到 _NaviMap",
        Failure.Hidden => "_NaviMap 存在但不可見（小地圖被關掉或介面隱藏）",
        Failure.NotLoaded => "_NaviMap 的節點尚未載入",
        Failure.NodeMismatch => $"_NaviMap 節點對不上（找不到根節點、節點 {RotationNodeId}、節點 {MapComponentNodeId} 的元件，或節點 {LockNodeId} 不是勾選框；可能改版換了版面）",
        Failure.MapComponentNotLoaded => "地圖元件尚未載入",
        Failure.NoZoomImage => $"地圖元件裡找不到圖片節點 {ZoomImageNodeId}（可能改版換了版面）",
        _ => "無",
    };

    public static bool TryRead(out Reading reading, out Failure failure)
    {
        reading = default;

        var addon = (AtkUnitBase*)Svc.GameGui.GetAddonByName("_NaviMap", 1).Address;
        if (addon == null)
        {
            failure = Failure.NoAddon;
            return false;
        }

        if (!addon->IsVisible || addon->VisibilityFlags != 0)
        {
            failure = Failure.Hidden;
            return false;
        }

        if (addon->UldManager.LoadedState != AtkLoadState.Loaded)
        {
            failure = Failure.NotLoaded;
            return false;
        }

        var root = addon->RootNode;
        var rotationNode = FindNode(&addon->UldManager, RotationNodeId);
        var mapNode = FindNode(&addon->UldManager, MapComponentNodeId);
        var mapComponent = ComponentOf(mapNode);
        var lockComponent = ComponentOf(FindNode(&addon->UldManager, LockNodeId));
        if (root == null || rotationNode == null || mapComponent == null || !IsButtonDerived(lockComponent))
        {
            failure = Failure.NodeMismatch;
            return false;
        }

        if (mapComponent->UldManager.LoadedState != AtkLoadState.Loaded)
        {
            failure = Failure.MapComponentNotLoaded;
            return false;
        }

        var zoomNode = FindNode(&mapComponent->UldManager, ZoomImageNodeId);
        if (zoomNode == null || zoomNode->Type != NodeType.Image)
        {
            failure = Failure.NoZoomImage;
            return false;
        }

        var scale = addon->Scale;
        var mapCenter = new Vector2(
            mapNode->ScreenX + (mapNode->Width * mapNode->ScaleX * scale / 2),
            mapNode->ScreenY + (mapNode->Height * mapNode->ScaleY * scale / 2));
        reading = new Reading(
            new Vector2(addon->X, addon->Y),
            scale,
            zoomNode->ScaleX,
            rotationNode->Rotation,
            ((AtkComponentCheckBox*)lockComponent)->IsChecked,
            new Vector2(root->Width, root->Height),
            mapCenter);
        failure = Failure.None;
        return true;
    }

    private static AtkResNode* FindNode(AtkUldManager* uld, uint nodeId)
    {
        if (uld->NodeList == null)
            return null;
        for (var i = 0; i < uld->NodeListCount; i++)
        {
            var node = uld->NodeList[i];
            if (node != null && node->NodeId == nodeId)
                return node;
        }

        return null;
    }

    // AtkResNode 只有 0xB0；Component 欄位在 0xB0，不先確認是元件節點（Type ≥ 1000）就讀＝越界。
    private static AtkComponentBase* ComponentOf(AtkResNode* node)
    {
        if (node == null || (int)node->Type < 1000)
            return null;
        return ((AtkComponentNode*)node)->Component;
    }

    // IsChecked 在 AtkComponentButton 的範圍（超出 AtkComponentBase），先從 ULD 元件資訊確認型別。
    private static bool IsButtonDerived(AtkComponentBase* component)
    {
        if (component == null || component->UldManager.BaseType != AtkUldManagerBaseType.Component)
            return false;
        var info = (AtkUldComponentInfo*)component->UldManager.Objects;
        return info != null && info->ComponentType is ComponentType.Button or ComponentType.CheckBox or ComponentType.RadioButton;
    }
}

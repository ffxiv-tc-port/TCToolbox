using System;
using System.Web;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Utility;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 道具右鍵選單直接開灰機 wiki。
/// 機制：純 <c>IContextMenu.OnMenuOpened</c>，開網址走 Dalamud 自己的
/// <see cref="Util.OpenLink"/>。零 hook、零特徵碼、不寫記憶體。
/// </summary>
/// <remarks>
/// 📌 <b>解決的是「兩步變一步」</b>：原本要先在 InventoryTools 的右鍵選單點「More Information」
/// 開 ItemWindow，再點視窗裡那顆灰機按鈕。這裡直接把那一步接到遊戲的道具右鍵選單上。
/// <para>
/// 網址格式與 InventoryTools 保留的那顆按鈕<b>逐字相同</b>
/// （<c>InventoryTools/Ui/Windows/ItemWindow.cs</c>：以道具名做站內搜尋、<c>ns220</c>
/// 是灰機的道具命名空間），所以兩邊點下去會落在同一頁，不會出現「按鈕開得到、選單開不到」。
/// </para>
/// <para>
/// ⚠️ 開瀏覽器一律走 <see cref="Util.OpenLink"/>，不自己 <c>Process.Start</c>——
/// Dalamud 那支會處理 <c>UseShellExecute</c> 與開完之後把視窗帶到前景，自己拼會少掉這些。
/// </para>
/// <para>
/// 🔴 與 <see cref="CopyItemNameContextMenu"/> 的<b>涵蓋範圍刻意不同</b>：那個模組只做
/// <see cref="ContextMenuType.Default"/>（因為背包本來就有「複製道具名」），而查 wiki
/// 最常用的地方就是背包，所以這裡<b>兩種選單都做</b>。背包走 Dalamud 給的
/// <see cref="MenuTargetInventory"/>（資訊最直接），其餘視窗共用
/// <see cref="ItemContextResolver"/>。
/// </para>
/// </remarks>
public sealed class HuijiWikiContextMenu : TcModule
{
    public override string InternalName => "HuijiWikiContextMenu";
    public override string DisplayName => "道具右鍵開灰機 wiki";

    public override string Description =>
        "在道具的右鍵選單加上「在灰機 wiki 查看」，點下去直接用預設瀏覽器開該道具的灰機 wiki 頁面。" +
        "背包與其他視窗（製作手帳、聊天欄道具連結、商店、市場等）都支援，不必再繞 ItemWindow。";

    public override bool HasConfigUI => true;

    /// <summary>
    /// 灰機 wiki 的站內搜尋網址。<c>ns220</c> 是灰機的「物品」命名空間，
    /// 限定它才不會搜到同名的任務／NPC 頁。與 InventoryTools 那顆按鈕同一份格式。
    /// </summary>
    private const string SearchUrlFormat = "https://ff14.huijiwiki.com/index.php?search={0}&ns220=1";

    /// <summary>選單文字。灰機 wiki 沒有對應的遊戲內用語可查，所以這是本外掛自己的字串。</summary>
    private const string MenuLabel = "在灰機 wiki 查看";

    private HuijiWikiContextMenuConfig Config => Plugin.Instance.Config.HuijiWiki;

    protected override void OnEnable() => Svc.ContextMenu.OnMenuOpened += OnMenuOpened;

    protected override void OnDisable() => Svc.ContextMenu.OnMenuOpened -= OnMenuOpened;

    private void OnMenuOpened(IMenuOpenedArgs args)
    {
        try
        {
            if (!TryResolveItem(args, out var itemId, out var itemName)) return;

            args.AddMenuItem(new MenuItem
            {
                Name = MenuLabel,
                PrefixChar = 'T',
                PrefixColor = 539,
                Priority = 0,
                OnClicked = _ => OpenWiki(itemId, itemName),
            });
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, $"[{InternalName}] 建立右鍵選單項目時發生例外（addon: {args.AddonName}）");
        }
    }

    /// <summary>
    /// 兩種選單各走各的來源。
    /// ⚠️ 背包那條不能改用 <see cref="ItemContextResolver"/>：那支對背包沒有具名欄位，
    /// 又不在 <c>HoveredItem</c> 白名單裡，會回 false。
    /// </summary>
    private static bool TryResolveItem(IMenuOpenedArgs args, out uint itemId, out string itemName)
    {
        itemId = 0;
        itemName = string.Empty;

        switch (args.MenuType)
        {
            case ContextMenuType.Inventory:
            {
                if (args.Target is not MenuTargetInventory { TargetItem: { } item }) return false;

                // GameInventoryItem.ItemId 帶 HQ／收藏品位移，正規化與查名共用同一支。
                return ItemContextResolver.TryGetItemName(item.ItemId, out itemId, out itemName);
            }

            case ContextMenuType.Default:
            {
                var addonName = args.AddonName;
                if (string.IsNullOrEmpty(addonName)) return false;

                // 對玩家／NPC 按右鍵時不該出現道具選項
                if (ItemContextResolver.IsTargetingCharacter(args)) return false;

                return ItemContextResolver.TryResolveItem(addonName, out itemId, out itemName);
            }

            default:
                return false;
        }
    }

    private void OpenWiki(uint itemId, string itemName)
    {
        var url = string.Format(SearchUrlFormat, HttpUtility.UrlEncode(itemName));

        // 使用者要回報「點了沒反應」時，這一行是唯一能分辨「網址沒組出來」與
        // 「瀏覽器沒起來」的證據，所以記 Information（使用者跑 LogLevel 2）。
        Svc.Log.Information($"[{InternalName}] 開啟灰機 wiki：{itemName}（#{itemId}）→ {url}");

        Util.OpenLink(url);

        if (Config.NotifyOnOpen)
            Svc.Chat.Print($"[TC Toolbox] 已在瀏覽器開啟灰機 wiki：{itemName}");
    }

    public override void DrawConfig()
    {
        var notify = Config.NotifyOnOpen;
        if (ImGui.Checkbox("開啟後在聊天欄顯示訊息", ref notify))
        {
            Config.NotifyOnOpen = notify;
            Plugin.Instance.Config.Save();
        }

        using (ImRaii.PushIndent())
        {
            ImGui.TextDisabled($"選單文字：「{MenuLabel}」。");
            ImGui.TextDisabled("以道具名做灰機 wiki 的站內搜尋（限「物品」命名空間），");
            ImGui.TextDisabled("與 InventoryTools 那顆灰機按鈕同一份網址格式。");
        }
    }
}

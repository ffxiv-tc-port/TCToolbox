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
/// ⚠️ 開瀏覽器一律走 <see cref="Util.OpenLink"/>，不自己 <c>Process.Start</c>——
/// Dalamud 那支會處理 <c>UseShellExecute</c> 與開完之後把視窗帶到前景，自己拼會少掉這些。
/// </remarks>
public sealed class HuijiWikiContextMenu : TcModule
{
    public override string InternalName => "HuijiWikiContextMenu";
    public override string DisplayName => "道具右鍵開灰機 wiki";

    public override string Description =>
        "在道具的右鍵選單加上「在灰機 wiki 查看」，點下去直接用預設瀏覽器開該道具的灰機 wiki 頁面。" +
        "背包與其他視窗（製作手帳、聊天欄道具連結、商店、市場等）都支援，不必再繞 ItemWindow。";

    public override ModuleCategory Category => ModuleCategory.Inventory;

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
            if (!ItemContextResolver.TryResolveFromMenu(args, out var itemId, out var itemName)) return;

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

    private void OpenWiki(uint itemId, string itemName)
    {
        var url = string.Format(SearchUrlFormat, HttpUtility.UrlEncode(itemName));

        // 使用者要回報「點了沒反應」時，這一行是唯一能分辨「網址沒組出來」與
        // 「瀏覽器沒起來」的證據，所以記 Information（使用者跑 LogLevel 1）。
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

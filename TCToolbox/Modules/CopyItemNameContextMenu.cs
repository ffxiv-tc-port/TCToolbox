using System;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Interface.Utility.Raii;
using Lumina.Excel.Sheets;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 在<b>背包以外</b>的道具右鍵選單補上「複製道具名」。
/// 機制：純 <c>IContextMenu.OnMenuOpened</c>；道具解析共用
/// <see cref="ItemContextResolver"/>（原本就寫在本檔，為了讓灰機 wiki 選單項共用而搬出去，
/// 行為未變）。零 hook、零特徵碼、不寫記憶體。
/// </summary>
/// <remarks>
/// 背包（<see cref="ContextMenuType.Inventory"/>）本來就有這個選項，所以完全不碰——
/// 這個模組只處理 <see cref="ContextMenuType.Default"/>。
/// <para>
/// 📌 台服 EXD 已驗證：<c>Addon</c> 159 ＝「複製道具名」（選單文字直接取這一列，跟遊戲用語一致）。
/// ⚠️ 台服對「尚未開放的道具」會保留列但把名稱留成空字串，所以判定不能用「查不查得到列」，
/// 必須檢查名稱內容——查得到列但名稱是空的一律當成不存在，不加選單項。
/// </para>
/// </remarks>
public sealed unsafe class CopyItemNameContextMenu : TcModule
{
    public override string InternalName => "CopyItemNameContextMenu";
    public override string DisplayName => "複製道具名（背包以外）";

    public override string Description =>
        "在製作手帳、聊天欄道具連結、任務報酬、成就、市場、商店等視窗的道具右鍵選單補上「複製道具名」。" +
        "背包本來就有這個選項，不重複加入；選單裡已經有同名項目時也會自動跳過。";

    public override ModuleCategory Category => ModuleCategory.Inventory;

    public override bool HasConfigUI => true;

    /// <summary>選單文字所在的 <c>Addon</c> 列（台服＝「複製道具名」）。</summary>
    private const uint CopyItemNameAddonRow = 159;

    private string menuLabel = "複製道具名";

    private CopyItemNameContextMenuConfig Config => Plugin.Instance.Config.CopyItemName;

    protected override void OnEnable()
    {
        menuLabel = ResolveMenuLabel();
        Svc.ContextMenu.OnMenuOpened += OnMenuOpened;
    }

    protected override void OnDisable() => Svc.ContextMenu.OnMenuOpened -= OnMenuOpened;

    /// <summary>選單文字取自遊戲自己的 Addon 表；台服未開放的列會是空字串，那就退回寫死的字面值。</summary>
    private static string ResolveMenuLabel()
    {
        var text = Svc.Data.GetExcelSheet<Addon>()
                      .GetRowOrDefault(CopyItemNameAddonRow)?.Text.ExtractText();

        return string.IsNullOrWhiteSpace(text) ? "複製道具名" : text;
    }

    private void OnMenuOpened(IMenuOpenedArgs args)
    {
        try
        {
            // 背包的右鍵選單遊戲本來就有這一項，不碰。
            if (args.MenuType != ContextMenuType.Default) return;

            var addonName = args.AddonName;
            if (string.IsNullOrEmpty(addonName)) return;

            // 對玩家／NPC 按右鍵時不該出現道具選項
            if (ItemContextResolver.IsTargetingCharacter(args)) return;

            if (!ItemContextResolver.TryResolveItem(addonName, out var itemId, out var itemName)) return;

            // 原生選單已經有同一項時不重複加入
            if (ItemContextResolver.NativeMenuContains(menuLabel)) return;

            args.AddMenuItem(new MenuItem
            {
                Name = menuLabel,
                PrefixChar = 'T',
                PrefixColor = 539,
                Priority = 0,
                OnClicked = _ => CopyToClipboard(itemId, itemName),
            });
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, $"[{InternalName}] 建立右鍵選單項目時發生例外（addon: {args.AddonName}）");
        }
    }

    private void CopyToClipboard(uint itemId, string itemName)
    {
        ImGui.SetClipboardText(itemName);

        if (Config.NotifyOnCopy)
            Svc.Chat.Print($"[TC Toolbox] 已複製道具名：{itemName}");

        if (Throttle.Pass("CopyItemName-Log", 5_000))
            Svc.Log.Information($"[{InternalName}] 已複製道具名：{itemName}（#{itemId}）");
    }

    public override void DrawConfig()
    {
        var notify = Config.NotifyOnCopy;
        if (ImGui.Checkbox("複製後在聊天欄顯示訊息", ref notify))
        {
            Config.NotifyOnCopy = notify;
            Plugin.Instance.Config.Save();
        }

        using (ImRaii.PushIndent())
            ImGui.TextDisabled($"選單文字取自遊戲介面用語：「{ResolveMenuLabel()}」。");
    }
}

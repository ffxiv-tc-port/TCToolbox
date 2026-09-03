using System;
using System.Collections.Generic;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Interface.Utility.Raii;
using Lumina.Excel.Sheets;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 在<b>原生沒有</b>「複製道具名」的道具右鍵選單補上這一項。
/// 機制：純 <c>IContextMenu.OnMenuOpened</c>；道具解析共用
/// <see cref="ItemContextResolver"/>。零 hook、零特徵碼、不寫記憶體。
/// </summary>
/// <remarks>
/// 🔴 <b>2026-08-08 修正：生效位置原本整個反了。</b>
/// 舊版寫死 <c>if (args.MenuType != ContextMenuType.Default) return;</c>，
/// 理由是註解裡那句「背包本來就有這個選項」——<b>台服實測不成立</b>
/// （使用者附截圖：背包右鍵完全沒有複製項，而聊天欄道具連結與製作筆記素材<b>原生就有</b>，
/// 反而被本模組加成兩份）。現在改成：
/// <list type="bullet">
/// <item><see cref="ContextMenuType.Inventory"/>（背包／雇員背包／陸行鳥鞍囊／裝備欄……
/// 全部共用 <c>AgentInventoryContext</c>，不必逐 addon 枚舉）＝<b>要加</b>，這才是本模組的本意。</item>
/// <item><see cref="ContextMenuType.Default"/> 中，<see cref="ExcludedAddons"/> 列到的視窗＝<b>不加</b>。</item>
/// </list>
/// <para>
/// 📌 台服 EXD 已驗證：<c>Addon</c> 159 ＝「複製道具名」（選單文字直接取這一列，跟遊戲用語一致）。
/// ⚠️ 台服對「尚未開放的道具」會保留列但把名稱留成空字串，所以判定不能用「查不查得到列」，
/// 必須檢查名稱內容——查得到列但名稱是空的一律當成不存在，不加選單項。
/// </para>
/// </remarks>
public sealed unsafe class CopyItemNameContextMenu : TcModule
{
    public override string InternalName => "CopyItemNameContextMenu";
    public override string DisplayName => "補上「複製道具名」";

    public override string Description =>
        "在原生沒有「複製道具名」的道具右鍵選單補上這一項：背包、雇員背包、陸行鳥鞍囊、裝備欄、" +
        "投影台、商店、市場、任務報酬、成就等。" +
        "聊天欄道具連結與製作筆記的素材原生就有這一項，所以不重複加入；" +
        "其餘視窗若原生選單裡已經出現同名項目，也會自動跳過。";

    public override ModuleCategory Category => ModuleCategory.Inventory;

    public override bool HasConfigUI => true;

    /// <summary>選單文字所在的 <c>Addon</c> 列（台服＝「複製道具名」）。</summary>
    private const uint CopyItemNameAddonRow = 159;

    /// <summary>
    /// 一律不加選單項的視窗（比對 <c>MenuArgs.AddonName</c>）。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>這份清單只放「有依據」的，不確定的一律不填</b>——
    /// 多出現在原生沒有的地方只是重複一項，把功能擋掉卻是靜默地少一個功能。
    /// <list type="bullet">
    /// <item><c>ChatLog</c>：聊天欄道具連結。<b>原生已有</b>複製道具名（2026-08-08 使用者截圖實證）。</item>
    /// <item><c>RecipeNote</c>：製作筆記的素材。<b>原生已有</b>複製道具名（同上）。</item>
    /// <item><c>RecipeProductList</c>：「會用到所選材料的配方」視窗（<c>Addon</c> 13440 就是這個標題；
    /// 對應 <c>AgentRecipeProductList.SearchForRecipesUsingItem</c>）。
    /// <b>使用者明示不做。</b>順帶一提現況本來就不會出現——它既不在
    /// <see cref="ItemContextResolver"/> 的具名欄位分支裡，也不在 HoveredItem 白名單裡；
    /// 寫在這裡是為了把「刻意不做」與「剛好沒做到」分開，免得日後有人把它加進白名單。</item>
    /// </list>
    /// </remarks>
    private static readonly HashSet<string> ExcludedAddons = new(StringComparer.Ordinal)
    {
        "ChatLog",
        "RecipeNote",
        "RecipeProductList",
    };

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
            // ⚠️ 先解析再判斷排除，順序是刻意的：這樣下面那些 Information 診斷只會在
            // 「真的對著一個道具按右鍵」時出現，對玩家／空白處按右鍵不會洗 log。
            if (!ItemContextResolver.TryResolveFromMenu(args, out var itemId, out var itemName)) return;

            var addonName = args.AddonName ?? string.Empty;

            // 原生已經有這一項（或使用者明示不做）的視窗，不碰。
            if (ExcludedAddons.Contains(addonName))
            {
                LogDecision(addonName, args.MenuType, $"略過（清單指定：原生已有／不做）（{itemName} #{itemId}）");
                return;
            }

            // 原生選單已經有同一項時不重複加入。
            // 🔴 這道只對 Default 有效——它讀的是 AgentContext，而背包選單的 agent 是
            // AgentInventoryContext；背包選單開著時 AgentContext 裡是上一次的殘值，
            // 拿來判斷會用「上一個視窗」的選單內容決定這個視窗要不要加，而且完全靜默。
            if (args.MenuType == ContextMenuType.Default && ItemContextResolver.NativeMenuContains(menuLabel))
            {
                LogDecision(addonName, args.MenuType, $"略過（原生選單裡已經有「{menuLabel}」）");
                return;
            }

            args.AddMenuItem(new MenuItem
            {
                Name = menuLabel,
                PrefixChar = 'T',
                PrefixColor = 539,
                Priority = 0,
                OnClicked = _ => CopyToClipboard(itemId, itemName),
            });

            LogDecision(addonName, args.MenuType, $"加入選單項（{itemName} #{itemId}）");
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, $"[{InternalName}] 建立右鍵選單項目時發生例外（addon: {args.AddonName}）");
        }
    }

    /// <summary>
    /// 「這個視窗到底有沒有被加上選單項、為什麼」的診斷。
    /// </summary>
    /// <remarks>
    /// 📌 寫 <c>Information</c>：使用者跑 LogLevel 1，盲區只有 Verbose,Debug 收得到但單檔數十萬行會淹沒，
    /// 而這個模組唯一會被回報的問題就是「該出現的沒出現／不該出現的出現了」。
    /// 節流鍵含 addon 名，所以每扇視窗各自有一行，不會被同一扇視窗洗掉。
    /// </remarks>
    private void LogDecision(string addonName, ContextMenuType menuType, string decision)
    {
        if (!Throttle.Pass($"CopyItemName-Menu-{menuType}-{addonName}", 60_000)) return;

        var native = menuType == ContextMenuType.Default
            ? ItemContextResolver.NativeMenuItemCount().ToString()
            : "n/a";

        Svc.Log.Information(
            $"[{InternalName}] {decision}｜addon={(addonName.Length == 0 ? "(無)" : addonName)}" +
            $" type={menuType} 原生項目數={native}");
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
        {
            ImGui.TextDisabled($"選單文字取自遊戲介面用語：「{ResolveMenuLabel()}」。");
            ImGui.TextDisabled("背包／雇員背包／陸行鳥鞍囊／裝備欄的右鍵選單都會加上。");
            ImGui.TextDisabled("聊天欄道具連結、製作筆記的素材原生就有這一項，所以不重複加入；");
            ImGui.TextDisabled("「會用到所選材料的配方」視窗刻意不加。");
        }
    }
}

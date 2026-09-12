using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using TCToolbox.Core;

namespace TCToolbox.Windows;

public sealed class MainWindow : Window
{
    private readonly Plugin plugin;

    public MainWindow(Plugin plugin) : base("TC Toolbox###TCToolboxMain")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(560, 320),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public override void Draw()
    {
        ImGui.TextDisabled("台服雜項 QoL 模組集。每個模組獨立開關（預設關閉），變更即時生效並自動存檔。");
        ImGui.Separator();
        ImGui.Spacing();

        using var tabs = ImRaii.TabBar("##TCToolboxCategories");
        if (!tabs) return;

        // 🔴 順序即優先度：「常用」放最前面，因為它的存在理由就是「不必在分頁間找」。
        //    「啟動中」緊接在後：它回答的是「我現在到底開著什麼」，那是掃視型的問題。
        //    「手動觸發」與「全部」是跨分類的篩選，放在四個分類分頁的右邊。
        DrawFavoritesTab();
        DrawActiveTab();

        foreach (var category in ModuleCategoryInfo.DisplayOrder)
            DrawCategoryTab(category);

        DrawManualTab();
        DrawAllTab();
    }

    /// <summary>
    /// 下一次要跳到哪一個分頁。<c>null</c>＝沒有人在要求跳頁。
    /// </summary>
    /// <remarks>
    /// 📌 存的是 <c>###</c> 後面那段<b>固定的英文 id</b>（例如 <c>tab-Inventory</c>），
    /// 不是分頁標題——標題帶著會變的數字。
    /// </remarks>
    private string? pendingTabId;

    /// <summary>這個分頁這一幀要不要被強制選取。</summary>
    /// <remarks>
    /// 🔴 <b>認領之後一定要清掉。</b>不清的話 <see cref="ImGuiTabItemFlags.SetSelected"/> 每幀都成立，
    /// 使用者會被黏在那一頁上、點別的分頁都跳不走，而且完全不會有錯誤訊息。
    /// </remarks>
    private ImGuiTabItemFlags FlagsFor(string tabId)
    {
        if (!string.Equals(pendingTabId, tabId, StringComparison.Ordinal))
            return ImGuiTabItemFlags.None;

        pendingTabId = null;
        return ImGuiTabItemFlags.SetSelected;
    }

    /// <summary>
    /// 「常用」分頁：使用者自己釘選的模組，不分分類。
    /// </summary>
    /// <remarks>
    /// 📌 <b>沒有釘選任何模組時這頁仍然存在</b>，裡面放一句怎麼釘的說明。
    /// 做成「有釘選才出現」看起來比較乾淨，但那樣這個功能就只剩星號按鈕一個入口——
    /// 使用者得先注意到那顆星、按下去、才知道有這一頁。空頁本身就是說明。
    /// </remarks>
    private void DrawFavoritesTab()
    {
        // 只數這一版真的存在的模組：設定檔裡可能留著舊版模組的名字，
        // 拿 Config 的集合大小當標題會出現「常用 (3)」但只畫得出 1 列。
        var count = 0;
        foreach (var module in plugin.Modules)
        {
            if (plugin.IsFavorite(module)) count++;
        }

        var title = count > 0 ? $"常用 ({count})" : "常用";

        using var tab = ImRaii.TabItem($"{title}###tab-Favorites", FlagsFor("tab-Favorites"));
        if (!tab) return;

        using var child = ImRaii.Child("##scroll-Favorites", Vector2.Zero, false);
        if (!child) return;

        if (count == 0)
        {
            ImGui.Spacing();
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
            ImGui.TextDisabled(
                "還沒有釘選任何模組。\n" +
                "在其他分頁上，點模組左邊那顆星號就會把它釘到這一頁，常用的功能就不必在分頁之間找。\n" +
                "釘選只影響這一頁的顯示，不會啟用或停用任何模組。");
            ImGui.PopTextWrapPos();
            return;
        }

        foreach (var module in plugin.Modules)
        {
            if (!plugin.IsFavorite(module)) continue;
            DrawModuleRow(module);
        }
    }

    /// <summary>
    /// 「啟動中」分頁：目前所有已啟用的模組，每一列標出它平常待在哪一頁。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>這是篩選不是分類</b>，與「常用」「手動觸發」同一種東西：模組照樣留在原本的分類分頁上。
    /// 🔴 <b>分類不在 <see cref="ModuleCategoryInfo.DisplayOrder"/> 裡的模組不能被跳過。</b>
    /// 那種模組在所有分類分頁上都看不到（只有「全部」找得到），正是最需要被指出來的——
    /// 它的標籤會寫「未分類」而不是消失。
    /// </remarks>
    private void DrawActiveTab()
    {
        var count = 0;
        foreach (var module in plugin.Modules)
        {
            if (module.IsEnabled) count++;
        }

        // ⚠️ 與「常用」同一個慣例：0 的時候不寫數字。「啟動中 (0)」看起來像壞掉，
        //    「啟動中」看起來像還沒開任何東西——後者才是事實。
        var title = count > 0 ? $"啟動中 ({count})" : "啟動中";

        using var tab = ImRaii.TabItem($"{title}###tab-Active", FlagsFor("tab-Active"));
        if (!tab) return;

        using var child = ImRaii.Child("##scroll-Active", Vector2.Zero, false);
        if (!child) return;

        if (count == 0)
        {
            ImGui.Spacing();
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
            ImGui.TextDisabled(
                "目前沒有任何模組啟用中。\n" +
                "所有模組預設都是關的；在別的分頁上勾起來之後，它就會出現在這一頁。\n" +
                "每一列右邊會標出它平常待在哪一個分頁，點那個標籤就跳過去。");
            ImGui.PopTextWrapPos();
            return;
        }

        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
        ImGui.TextDisabled("目前開著的全部模組。灰色標籤＝它平常待的分頁，點一下就跳過去。");
        ImGui.PopTextWrapPos();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var drawn = 0;

        foreach (var category in ModuleCategoryInfo.DisplayOrder)
        {
            foreach (var module in plugin.Modules)
            {
                if (!module.IsEnabled || module.Category != category) continue;
                DrawModuleRow(module, true);
                drawn++;
            }
        }

        // 🔴 收尾這一輪撿的是「分類沒被列進 DisplayOrder」的模組。
        //    不撿的話它們會從這一頁上<b>靜默消失</b>——而那正是最該被看見的一種模組。
        if (drawn >= count) return;

        foreach (var module in plugin.Modules)
        {
            if (!module.IsEnabled || IsOnACategoryTab(module.Category)) continue;
            DrawModuleRow(module, true);
        }
    }

    /// <summary>這個分類有沒有自己的分頁（＝有沒有被列進 <see cref="ModuleCategoryInfo.DisplayOrder"/>）。</summary>
    private static bool IsOnACategoryTab(ModuleCategory category)
    {
        foreach (var listed in ModuleCategoryInfo.DisplayOrder)
        {
            if (listed == category) return true;
        }

        return false;
    }

    /// <summary>
    /// 「手動觸發」分頁：核心行為是「按了才動一次」的模組。
    /// </summary>
    /// <remarks>
    /// 📌 這是<b>篩選</b>不是分類——這些模組在原本的分類分頁上照樣看得到
    /// （<see cref="TcModule.IsManualTrigger"/> 與 <see cref="TcModule.Category"/> 正交）。
    /// 這一頁回答的是另一個問題：「哪些東西是我開著也不會自己亂動的」。
    /// </remarks>
    private void DrawManualTab()
    {
        var total = 0;
        var enabled = 0;
        foreach (var module in plugin.Modules)
        {
            if (!module.IsManualTrigger) continue;
            total++;
            if (module.IsEnabled) enabled++;
        }

        using var tab = ImRaii.TabItem($"手動觸發 ({enabled}/{total})###tab-Manual", FlagsFor("tab-Manual"));
        if (!tab) return;

        using var child = ImRaii.Child("##scroll-Manual", Vector2.Zero, false);
        if (!child) return;

        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
        ImGui.TextDisabled("這些模組開著也不會自己動作，一律要你按下按鈕才會執行一次。");
        ImGui.PopTextWrapPos();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        foreach (var module in plugin.Modules)
        {
            if (!module.IsManualTrigger) continue;
            DrawModuleRow(module);
        }
    }

    /// <summary>畫一個分類分頁。</summary>
    /// <remarks>
    /// 🔴 分頁標題帶了「已啟用/總數」，那是<b>會變的字串</b>——所以 ImGui 的 id 必須靠
    /// <c>###</c> 後面那段固定的英文名撐住。用中文標題當 id 的話，使用者每勾一個模組
    /// 數字就變、ImGui 就當成一個全新的分頁，<b>畫面會跳回第一頁</b>。
    /// </remarks>
    private void DrawCategoryTab(ModuleCategory category)
    {
        // 計數要在建立分頁之前算完（標題需要它）。39 個模組的走訪成本可以忽略。
        var total = 0;
        var enabled = 0;
        foreach (var module in plugin.Modules)
        {
            if (module.Category != category) continue;
            total++;
            if (module.IsEnabled) enabled++;
        }

        var id = ModuleCategoryInfo.Id(category);

        using var tab = ImRaii.TabItem($"{ModuleCategoryInfo.Title(category)} ({enabled}/{total})###tab-{id}", FlagsFor($"tab-{id}"));
        if (!tab) return;

        // 分頁內容自己捲動，否則整條分頁列會跟著捲走。
        using var child = ImRaii.Child($"##scroll-{id}", Vector2.Zero, false);
        if (!child) return;

        foreach (var module in plugin.Modules)
        {
            if (module.Category != category) continue;
            DrawModuleRow(module);
        }
    }

    /// <summary>
    /// 「全部」分頁：維持改成分頁之前的那條長清單。
    /// </summary>
    /// <remarks>
    /// 📌 這頁是<b>安全網</b>，不是備援畫面：
    /// <list type="bullet">
    /// <item>萬一有模組的 <see cref="ModuleCategory"/> 沒被列進
    /// <see cref="ModuleCategoryInfo.DisplayOrder"/>，它在分類分頁上會完全消失而且不報錯——
    /// 這頁是它唯一還看得到的地方。</item>
    /// </list>
    /// </remarks>
    private void DrawAllTab()
    {
        var enabled = 0;
        foreach (var module in plugin.Modules)
        {
            if (module.IsEnabled) enabled++;
        }

        using var tab = ImRaii.TabItem($"全部 ({enabled}/{plugin.Modules.Count})###tab-All", FlagsFor("tab-All"));
        if (!tab) return;

        using var child = ImRaii.Child("##scroll-All", Vector2.Zero, false);
        if (!child) return;

        foreach (var module in plugin.Modules)
            DrawModuleRow(module);
    }

    /// <summary>畫一個模組列：釘選星號、勾選框、顯示名、列上提示、描述、設定。</summary>
    /// <remarks>
    /// 🔴 <b>同一頁裡不能把同一個模組畫兩次</b>（那才是真的 id 相撞）。
    /// 這就是「常用」做成獨立分頁、而不是每頁頂端插一塊置頂區的原因之一——
    /// 置頂區得在下面的清單裡把同一個模組跳過，多一條容易寫漏的規則。
    /// </remarks>
    /// <param name="module">要畫的模組。</param>
    /// <param name="showLocation">
    /// 列上要不要標出「這個模組平常待在哪一頁」。只有「啟動中」分頁需要——
    /// 在分類分頁上標自己所屬的分類是廢話。
    /// </param>
    private void DrawModuleRow(TcModule module, bool showLocation = false)
    {
        using var id = ImRaii.PushId(module.InternalName);

        DrawFavoriteToggle(module);
        ImGui.SameLine();

        var enabled = module.IsEnabled;
        if (ImGui.Checkbox($"##enable-{module.InternalName}", ref enabled))
            plugin.SetModuleEnabled(module, enabled);

        ImGui.SameLine();
        ImGui.TextUnformatted(module.DisplayName);

        if (showLocation) DrawLocationTags(module);

        DrawRowNotice(module);

        using (ImRaii.PushIndent())
        {
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
            ImGui.TextDisabled(module.Description);
            ImGui.PopTextWrapPos();

            if (module.IsEnabled && module.HasConfigUI)
            {
                if (ImGui.TreeNodeEx($"設定###cfg-{module.InternalName}", ImGuiTreeNodeFlags.None))
                {
                    DrawModuleConfig(module);
                    ImGui.TreePop();
                }
            }
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
    }

    /// <summary>分頁標籤的文字色（灰，壓得比描述再低一點：它是輔助資訊不是內容）。</summary>
    private static readonly Vector4 TagTextColor = new(0.58f, 0.58f, 0.58f, 1f);

    /// <summary>標籤滑過時的底色（很淡，只是讓人知道它可以點）。</summary>
    private static readonly Vector4 TagHoverColor = new(1f, 1f, 1f, 0.10f);

    /// <summary>標籤按下去的底色。</summary>
    private static readonly Vector4 TagActiveColor = new(1f, 1f, 1f, 0.18f);

    /// <summary>
    /// 「啟動中」分頁上那幾個灰色標籤：這個模組平常待在哪些分頁。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>分類不在 <see cref="ModuleCategoryInfo.DisplayOrder"/> 裡時標「未分類」，不是省略。</b>
    /// 那種模組在任何分類分頁上都找不到——省略標籤會讓它看起來跟別人一樣正常。
    /// 「未分類」沒有頁可跳，所以畫成純文字不是按鈕（按鈕按下去沒反應更糟）。
    /// </remarks>
    private void DrawLocationTags(TcModule module)
    {
        if (IsOnACategoryTab(module.Category))
        {
            var id = ModuleCategoryInfo.Id(module.Category);
            DrawTabTag(
                ModuleCategoryInfo.Title(module.Category),
                $"tab-{id}",
                $"這個模組平常在「{ModuleCategoryInfo.Title(module.Category)}」分頁上。點一下跳過去。");
        }
        else
        {
            // 🔴 「不知道」要在列上看得見：這個模組的分類沒有對應的分頁，
            //    所以除了「全部」與這一頁以外哪裡都找不到它。
            ImGui.SameLine();
            ImGui.TextColored(TagTextColor, "［未分類］");

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    "這個模組的分類沒有對應的分頁（沒被列進 ModuleCategoryInfo.DisplayOrder）。\n"
                    + "也就是說，除了這一頁與「全部」以外，其他分頁上都找不到它。");
            }
        }

        if (module.IsManualTrigger)
        {
            DrawTabTag(
                "手動觸發",
                "tab-Manual",
                "開著也不會自己動作，要按下按鈕才會執行一次。\n也列在「手動觸發」分頁上，點一下跳過去。");
        }

        if (plugin.IsFavorite(module))
            DrawTabTag("常用", "tab-Favorites", "已釘選，也列在「常用」分頁上。點一下跳過去。");
    }

    /// <summary>畫一個可以點的灰色小標籤；點下去就要求跳到 <paramref name="targetTabId"/> 那一頁。</summary>
    /// <remarks>
    /// ⚠️ <see cref="ImGuiCol.Button"/> 推成全透明是為了讓它看起來像標籤而不是按鈕——
    /// 但滑過與按下的底色<b>要留著</b>，不然使用者不會知道它能點。
    /// </remarks>
    private void DrawTabTag(string label, string targetTabId, string tooltip)
    {
        ImGui.SameLine();

        bool clicked;
        using (ImRaii.PushColor(ImGuiCol.Button, Vector4.Zero))
        using (ImRaii.PushColor(ImGuiCol.ButtonHovered, TagHoverColor))
        using (ImRaii.PushColor(ImGuiCol.ButtonActive, TagActiveColor))
        using (ImRaii.PushColor(ImGuiCol.Text, TagTextColor))
        {
            clicked = ImGui.SmallButton($"［{label}］###tag-{targetTabId}");
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(tooltip);

        if (clicked)
            pendingTabId = targetTabId;
    }

    /// <summary>模組設定畫面繪製失敗時，那一列顯示的紅字。</summary>
    private static readonly Vector4 ConfigErrorColor = new(1f, 0.35f, 0.35f, 1f);

    /// <summary>
    /// 畫單一模組的設定內容，並把它的例外隔離在這一列裡。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>這是最後一道，不是模組的免責條款。</b>所有模組的 <c>DrawConfig</c> 都在同一個
    /// <c>Window.Draw</c> 路徑上：任何一個擲例外（展開設定樹狀節點時會<b>每幀重擲</b>），
    /// Dalamud 的視窗錯誤閂鎖（10 秒內兩次）就會把主視窗<b>永久關閉到外掛重載為止</b>；
    /// 而模組的啟用／停用勾選框就在同一扇視窗裡 —— 使用者會連「關掉肇事模組」的入口
    /// 一起失去。這道 try 讓故障留在單一模組列，其餘模組與勾選框照常可用。
    /// </remarks>
    private static void DrawModuleConfig(TcModule module)
    {
        try
        {
            module.DrawConfig();
        }
        catch (Exception ex)
        {
            ImGui.TextColored(ConfigErrorColor, $"「{module.DisplayName}」的設定畫面繪製失敗，本次不顯示。");
            ImGui.TextDisabled(ex.Message);

            // 使用者回報用（LogLevel 1 收得到）。節流：展開節點時這裡每幀都會進來。
            if (Throttle.Pass($"MainWindow-DrawConfig-{module.InternalName}", 60_000))
                Svc.Log.Information($"[TCToolbox] 模組 {module.InternalName} 的設定畫面繪製失敗：{ex}");
        }
    }

    private static readonly Vector4 NoticeWarnColor = new(1f, 0.65f, 0.25f, 1f);
    private static readonly Vector4 NoticeUnknownColor = new(0.68f, 0.68f, 0.68f, 1f);

    /// <summary>已釘選的星號顏色（金）。</summary>
    private static readonly Vector4 FavoriteOnColor = new(1f, 0.80f, 0.30f, 1f);

    /// <summary>未釘選的星號顏色（暗灰）。刻意壓得很低，讓「有沒有釘」一眼掃得出來。</summary>
    private static readonly Vector4 FavoriteOffColor = new(0.42f, 0.42f, 0.42f, 1f);

    /// <summary>
    /// 模組列最左邊的釘選星號。金色＝已釘選、暗灰＝未釘選，點一下切換。
    /// </summary>
    /// <remarks>
    /// 📌 <b>刻意放在列上而不是收進「設定」或右鍵選單</b>：釘選是個一秒鐘的動作，
    /// 藏進第二層之後就沒有人會用了。兩個狀態靠顏色分辨（列上隨時掃視得到），
    /// 「這顆星是幹嘛的」放 tooltip（起疑才查）。
    /// </remarks>
    private void DrawFavoriteToggle(TcModule module)
    {
        var favorite = plugin.IsFavorite(module);

        bool clicked;
        using (ImRaii.PushColor(ImGuiCol.Text, favorite ? FavoriteOnColor : FavoriteOffColor))
        using (ImRaii.PushColor(ImGuiCol.Button, Vector4.Zero))
        {
            clicked = ImGuiComponents.IconButton($"fav-{module.InternalName}", FontAwesomeIcon.Star);
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(favorite
                ? "已加入「常用」分頁。點一下取消。\n（只影響顯示，不會停用這個模組。）"
                : "加入「常用」分頁，之後不必在分頁之間找它。\n（只影響顯示，不會啟用這個模組。）");
        }

        if (clicked)
            plugin.SetModuleFavorite(module, !favorite);
    }

    /// <summary>
    /// 畫模組列上的提示。
    /// </summary>
    /// <remarks>
    /// 🔴 <see cref="TcModule.RowNotice"/> 的實作可能要做 I/O（例如去讀別的外掛的設定檔），
    /// 而這裡是 ImGui 的 Draw 路徑：<b>擲一次例外，Dalamud 就把整個 <c>UiBuilder.Draw</c>
    /// 設成 null，介面到重開遊戲前都不會回來</b>。所以整段包 try，失敗就當作沒有提示。
    /// </remarks>
    private static void DrawRowNotice(TcModule module)
    {
        ModuleNotice? notice;
        try
        {
            notice = module.RowNotice;
        }
        catch (Exception ex)
        {
            if (Throttle.Pass($"MainWindow-RowNotice-{module.InternalName}", 60_000))
                Svc.Log.Information($"[TCToolbox] 模組 {module.InternalName} 的列上提示計算失敗，本次不顯示：{ex.Message}");
            return;
        }

        if (notice is not { } value || string.IsNullOrEmpty(value.Text)) return;

        ImGui.SameLine();
        ImGui.TextColored(
            value.Level == ModuleNoticeLevel.Warning ? NoticeWarnColor : NoticeUnknownColor,
            value.Text);

        if (!string.IsNullOrEmpty(value.Tooltip) && ImGui.IsItemHovered())
            ImGui.SetTooltip(value.Tooltip);
    }
}

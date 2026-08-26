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
        //    「手動觸發」與「全部」是跨分類的篩選，放在四個分類分頁的右邊。
        DrawFavoritesTab();

        foreach (var category in ModuleCategoryInfo.DisplayOrder)
            DrawCategoryTab(category);

        DrawManualTab();
        DrawAllTab();
    }

    /// <summary>
    /// 「常用」分頁：使用者自己釘選的模組，不分分類。
    /// </summary>
    /// <remarks>
    /// 📌 <b>沒有釘選任何模組時這頁仍然存在</b>，裡面放一句怎麼釘的說明。
    /// 做成「有釘選才出現」看起來比較乾淨，但那樣這個功能就只剩星號按鈕一個入口——
    /// 使用者得先注意到那顆星、按下去、才知道有這一頁。空頁本身就是說明。
    /// <para>
    /// ⚠️ 標題在沒有釘選時<b>不帶數字</b>：空的分頁寫「常用 (0)」像是壞掉，寫「常用」像是還沒用。
    /// id 照樣靠 <c>###</c> 撐住，所以數字有無不會讓 ImGui 當成另一個分頁。
    /// </para>
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

        using var tab = ImRaii.TabItem($"{title}###tab-Favorites");
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

        using var tab = ImRaii.TabItem($"手動觸發 ({enabled}/{total})###tab-Manual");
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

        using var tab = ImRaii.TabItem($"{ModuleCategoryInfo.Title(category)} ({enabled}/{total})###tab-{id}");
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
    /// <item>使用者原本就習慣一條長清單，想不起某個模組被分到哪一頁時不會卡住。</item>
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

        using var tab = ImRaii.TabItem($"全部 ({enabled}/{plugin.Modules.Count})###tab-All");
        if (!tab) return;

        using var child = ImRaii.Child("##scroll-All", Vector2.Zero, false);
        if (!child) return;

        foreach (var module in plugin.Modules)
            DrawModuleRow(module);
    }

    /// <summary>畫一個模組列：釘選星號、勾選框、顯示名、列上提示、描述、設定。</summary>
    /// <remarks>
    /// 所有分頁（常用／四個分類／手動觸發／全部）共用這一份。同一個模組同時出現在好幾頁不會撞 id——
    /// 一次只有一個分頁是展開的，而且每頁各自在不同的子視窗裡。
    /// ⚠️ 代價是「設定」TreeNode 的展開狀態各頁不共用（ImGui 的 id 含所在視窗）。
    /// <para>
    /// 🔴 <b>同一頁裡不能把同一個模組畫兩次</b>（那才是真的 id 相撞）。
    /// 這就是「常用」做成獨立分頁、而不是每頁頂端插一塊置頂區的原因之一——
    /// 置頂區得在下面的清單裡把同一個模組跳過，多一條容易寫漏的規則。
    /// </para>
    /// </remarks>
    private void DrawModuleRow(TcModule module)
    {
        using var id = ImRaii.PushId(module.InternalName);

        DrawFavoriteToggle(module);
        ImGui.SameLine();

        var enabled = module.IsEnabled;
        if (ImGui.Checkbox($"##enable-{module.InternalName}", ref enabled))
            plugin.SetModuleEnabled(module, enabled);

        ImGui.SameLine();
        ImGui.TextUnformatted(module.DisplayName);

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
    /// <para>
    /// 🔴 <c>TreePop()</c> 由呼叫端負責，所以這裡<b>絕對不能把例外往外丟</b>：
    /// <c>TreeNodeEx</c> 回 true 之後若跳過 <c>TreePop</c>，ImGui 的 ID 堆疊就會失衡。
    /// </para>
    /// <para>
    /// ⚠️ 這裡攔的是<b>一般例外</b>。AccessViolationException 在 .NET Core 屬 corrupted-state
    /// exception，<c>try/catch</c> 本來就攔不到 —— 原生指標的安全仍然只能靠事前判空。
    /// </para>
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

            // 使用者回報用（LogLevel 2 收得到）。節流：展開節點時這裡每幀都會進來。
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
    /// <para>
    /// ⚠️ <see cref="ImGuiComponents.IconButton"/> 的顏色參數控制的是<b>按鈕底色</b>不是圖示顏色；
    /// 圖示畫的時候讀的是 <see cref="ImGuiCol.Text"/>，所以要在外面推 Text 顏色才有用。
    /// </para>
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

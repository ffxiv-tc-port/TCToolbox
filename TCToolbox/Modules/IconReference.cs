using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 圖示對照表（開發輔助）：把 DTR／SeString 可用的圖示畫出來，並讓人一鍵複製對應的識別字。
/// </summary>
/// <remarks>
/// <para>
/// 📌 <b>純顯示模組</b>：開著但不去開視窗，遊戲行為完全不變（<see cref="IsManualTrigger"/>＝<c>true</c>）。
/// 不讀原生記憶體、不掛 hook、不送任何指令；唯一的副作用是使用者按了「DTR」按鈕之後，
/// 資訊列上會多出一格預覽用的欄位。
/// </para>
/// <para>
/// 🔑 <b>三個分頁對應三種完全不同的東西</b>，混在一起講會害人挑錯：
/// <list type="bullet">
/// <item><b>點陣圖示</b>（<see cref="BitmapFontIcon"/>）——遊戲自己的圖示表，
/// 只能透過 <see cref="IconPayload"/> 放進 SeString（DTR 欄位、聊天訊息）。
/// <b>它不是字元</b>，複製出來的識別字沒辦法直接貼進純文字欄位。</item>
/// <item><b>符號字元</b>（<see cref="SeIconChar"/>）——遊戲字型私有區（U+E020～U+E0E9）的<b>真字元</b>，
/// 可以直接塞進任何字串。</item>
/// <item><b>Unicode 常用符號</b>——與遊戲無關的標準字元，能不能顯示<b>完全看目前的字型</b>。</item>
/// </list>
/// </para>
/// <para>
/// ⚠️ <b>畫不出來（空白／豆腐方塊）本身就是有效結論</b>：那代表該圖示在目前這套字型／這個 Dalamud
/// 版本下不能用，不是這個模組壞了。這也正是要把它畫出來看的理由——光看列舉名字是猜不到的。
/// </para>
/// <para>
/// 🔴 這整個模組住在 ImGui 的 Draw 路徑上，<b>一律不得擲例外</b>
/// （Draw 擲一次例外，Dalamud 的視窗錯誤閂鎖就會把它永久關掉到外掛重載為止）。
/// 所以：清單只建一次並整段包 try、每一顆按鈕的動作各自包 try、
/// 表格與分頁一律用 <see cref="ImRaii"/> 收尾（就算中途擲例外，<c>EndTable</c>／<c>EndTabItem</c> 也一定會被呼叫）。
/// </para>
/// </remarks>
public sealed class IconReference : TcModule
{
    public override string InternalName => "IconReference";
    public override string DisplayName => "圖示對照表";

    public override string Description =>
        "開發輔助：把 DTR／SeString 能用的圖示全部畫出來對照，一鍵複製識別字。" +
        "分點陣圖示（BitmapFontIcon）、符號字元（SeIconChar）、Unicode 常用符號三個分頁；" +
        "每一列還能按「DTR」把該圖示丟到資訊列上，直接看它在 DTR 實際長什麼樣。指令 /tcicons。";

    public override ModuleCategory Category => ModuleCategory.Misc;

    /// <inheritdoc/>
    /// <remarks>開著但不開視窗、不按按鈕＝遊戲行為完全不變。</remarks>
    public override bool IsManualTrigger => true;

    public override bool HasConfigUI => true;

    private const string Command = "/tcicons";

    /// <summary>
    /// 預覽用的資訊列欄位標題。
    /// </summary>
    /// <remarks>
    /// 📌 前綴沿用本外掛既有的 DTR 欄位命名（<c>AutoCountPlayers</c> 用「TC Toolbox 周邊玩家」）——
    /// 使用者在 Dalamud 設定的資訊列排序清單裡，是靠這個前綴把本外掛的欄位認出來的。
    /// </remarks>
    private const string DtrTitle = "TC Toolbox 圖示預覽";

    private bool windowOpen;

    private string searchBitmap = string.Empty;
    private string searchSeIcon = string.Empty;
    private string searchUnicode = string.Empty;

    private IDtrBarEntry? dtrEntry;

    /// <summary>目前被丟到 DTR 上預覽的那一列的識別字（<c>null</c>＝沒有）。</summary>
    private string? dtrCurrent;

    /// <summary>Draw 路徑上的例外只報告一次，免得每幀洗爆記錄檔。</summary>
    private bool drawErrorReported;

    #region 資料

    /// <summary>一列要畫什麼、複製什麼。</summary>
    private enum IconKind
    {
        /// <summary>遊戲點陣圖示，只能經由 SeString 的 <see cref="IconPayload"/> 呈現。</summary>
        Bitmap = 0,

        /// <summary>遊戲字型私有區的真字元。</summary>
        SeIcon = 1,

        /// <summary>標準 Unicode 字元。</summary>
        Unicode = 2,
    }

    /// <summary>對照表的一列。</summary>
    /// <param name="Kind">決定「渲染」欄要怎麼畫。</param>
    /// <param name="Identifier">「複製」鈕會放進剪貼簿的完整識別字。</param>
    /// <param name="Note">備註欄（點陣圖示＝數值；字元＝碼點；Unicode＝碼點＋中文說明）。</param>
    /// <param name="Text">實際字元（點陣圖示沒有字元，為空字串）。</param>
    /// <param name="Icon">點陣圖示專用。</param>
    /// <param name="Encoded">
    /// 點陣圖示專用：SeString 編碼後的位元組。
    /// 📌 <b>建表時就算好</b>——這是要在每幀的 Draw 路徑上餵給渲染器的，
    /// 不能每幀重新配置 <see cref="SeString"/> 再 <c>Encode()</c>。
    /// </param>
    /// <param name="SearchKey">全小寫的搜尋鍵；同樣是為了不要每幀 <c>ToLowerInvariant</c>。</param>
    private readonly record struct IconEntry(
        IconKind Kind,
        string Identifier,
        string Note,
        string Text,
        BitmapFontIcon Icon,
        byte[] Encoded,
        string SearchKey);

    private static IReadOnlyList<IconEntry>? bitmapEntries;
    private static IReadOnlyList<IconEntry>? seIconEntries;
    private static IReadOnlyList<IconEntry>? unicodeEntries;

    /// <summary>
    /// Unicode 分頁的固定清單：（字元, 中文說明）。
    /// </summary>
    /// <remarks>
    /// 📌 中文說明是為了讓人「講得出是哪一個」——`U+25CF` 跟 `U+25CB` 光看碼點分不出誰是實心。
    /// ⚠️ 這裡有幾個字元（⚔ ⚠ ✔ ✖ ☑ ☒）在很多字型裡沒有字形，畫出來會是空白或豆腐方塊。
    /// <b>那是預期結果，不要「修好」它</b>——這張表存在的意義就是分辨哪些能用哪些不能用。
    /// </remarks>
    private static readonly (char Char, string Label)[] UnicodeSymbols =
    [
        ('♪', "音符"),
        ('♫', "雙音符"),
        ('♥', "實心心形"),
        ('♡', "空心心形"),
        ('★', "實心星"),
        ('☆', "空心星"),
        ('●', "實心圓"),
        ('○', "空心圓"),
        ('◆', "實心菱形"),
        ('◇', "空心菱形"),
        ('■', "實心方塊"),
        ('□', "空心方塊"),
        ('▲', "實心上三角"),
        ('△', "空心上三角"),
        ('▼', "實心下三角"),
        ('▽', "空心下三角"),
        ('→', "右箭頭"),
        ('←', "左箭頭"),
        ('↑', "上箭頭"),
        ('↓', "下箭頭"),
        ('※', "參考符（米字）"),
        ('✓', "細勾"),
        ('✕', "細叉"),
        ('☀', "太陽"),
        ('☁', "雲"),
        ('☂', "傘"),
        ('☃', "雪人"),
        ('♀', "女性符號"),
        ('♂', "男性符號"),
        ('⚔', "交叉刀劍"),
        ('⚠', "警告"),
        ('☑', "方框打勾"),
        ('☒', "方框打叉"),
        ('✔', "粗勾"),
        ('✖', "粗叉"),
        ('♦', "方塊（撲克）"),
        ('♣', "梅花（撲克）"),
        ('♠', "黑桃（撲克）"),
        ('◎', "雙圈"),
        ('◉', "實心雙圈"),
    ];

    /// <summary>
    /// 點陣圖示清單（第一次開視窗時才建，之後整個 session 重用）。
    /// </summary>
    /// <remarks>
    /// 🔑 <b>用 <see cref="Type.GetFields(BindingFlags)"/> 而不是 <c>Enum.GetValues</c></b>：
    /// 列舉若有同值別名，<c>Enum.GetValues</c> 會給重複的值、而 <c>Enum.GetName</c> 只回其中一個名字，
    /// 對照表就會出現兩列一模一樣的識別字（而且沒有任何錯誤）。逐欄位讀則是一個名字一列。
    /// （📌 本 pin 的 <see cref="BitmapFontIcon"/> 157 個成員實測無同值別名，
    /// 但這是上游隨時會變的前提，不值得賭。）
    /// <para>
    /// ⚠️ 迴圈變數叫 <c>member</c> 不叫 <c>field</c>：本專案是 C# 14，
    /// <c>field</c> 在<b>屬性存取子裡</b>是關鍵字（繫結到屬性的合成備份欄位），
    /// 在這裡宣告成區域變數會編譯失敗（CS9273）。同一段程式碼搬到方法裡就沒事——
    /// 所以這不是「照抄別處就會對」的東西。
    /// </para>
    /// </remarks>
    private static IReadOnlyList<IconEntry> BitmapEntries
    {
        get
        {
            if (bitmapEntries != null) return bitmapEntries;

            var list = new List<IconEntry>();
            try
            {
                foreach (var member in typeof(BitmapFontIcon).GetFields(BindingFlags.Public | BindingFlags.Static))
                {
                    if (member.GetValue(null) is not BitmapFontIcon icon) continue;
                    if (icon == BitmapFontIcon.None) continue;

                    var id = $"BitmapFontIcon.{member.Name}";
                    list.Add(new IconEntry(
                        IconKind.Bitmap,
                        id,
                        $"值 {(int)icon}",
                        string.Empty,
                        icon,
                        new SeString(new IconPayload(icon)).Encode(),
                        $"{id} {(int)icon}".ToLowerInvariant()));
                }

                list.Sort((a, b) => ((int)a.Icon).CompareTo((int)b.Icon));
            }
            catch (Exception ex)
            {
                // 建表失敗就是空表：視窗照開、其他分頁照常，不會把 Draw 拖下水。
                Svc.Log.Error(ex, "[IconReference] 建立 BitmapFontIcon 清單失敗，該分頁會是空的。");
            }

            bitmapEntries = list;
            return bitmapEntries;
        }
    }

    /// <summary>符號字元清單（同樣只建一次；理由見 <see cref="BitmapEntries"/>）。</summary>
    private static IReadOnlyList<IconEntry> SeIconEntries
    {
        get
        {
            if (seIconEntries != null) return seIconEntries;

            var list = new List<IconEntry>();
            try
            {
                foreach (var member in typeof(SeIconChar).GetFields(BindingFlags.Public | BindingFlags.Static))
                {
                    if (member.GetValue(null) is not SeIconChar icon) continue;

                    var id = $"SeIconChar.{member.Name}";
                    var code = $"U+{(int)icon:X4}";
                    list.Add(new IconEntry(
                        IconKind.SeIcon,
                        id,
                        code,
                        icon.ToIconString(),
                        BitmapFontIcon.None,
                        [],
                        $"{id} {code}".ToLowerInvariant()));
                }

                list.Sort((a, b) => string.CompareOrdinal(a.Note, b.Note));
            }
            catch (Exception ex)
            {
                Svc.Log.Error(ex, "[IconReference] 建立 SeIconChar 清單失敗，該分頁會是空的。");
            }

            seIconEntries = list;
            return seIconEntries;
        }
    }

    /// <summary>Unicode 常用符號清單（固定表，只轉一次）。</summary>
    private static IReadOnlyList<IconEntry> UnicodeEntries
    {
        get
        {
            if (unicodeEntries != null) return unicodeEntries;

            var list = new List<IconEntry>();
            foreach (var (ch, label) in UnicodeSymbols)
            {
                var id = $"U+{(int)ch:X4}";
                list.Add(new IconEntry(
                    IconKind.Unicode,
                    id,
                    label,
                    ch.ToString(),
                    BitmapFontIcon.None,
                    [],
                    $"{id} {label}".ToLowerInvariant()));
            }

            unicodeEntries = list;
            return unicodeEntries;
        }
    }

    #endregion

    protected override void OnEnable()
    {
        Svc.Commands.AddHandler(Command, new CommandInfo(OnCommand)
        {
            HelpMessage = "開啟圖示對照表（看 DTR／SeString 圖示長什麼樣並複製識別字）",
        });

        Svc.PluginInterface.UiBuilder.Draw += DrawWindow;

        Svc.Log.Information(
            $"[{InternalName}] 模組啟用：點陣圖示 {BitmapEntries.Count} 項、"
            + $"符號字元 {SeIconEntries.Count} 項、Unicode 符號 {UnicodeEntries.Count} 項。");
    }

    protected override void OnDisable()
    {
        Svc.Commands.RemoveHandler(Command);
        Svc.PluginInterface.UiBuilder.Draw -= DrawWindow;

        // 🔴 預覽用的 DTR 欄位一定要在這裡收掉：模組停用（含外掛卸載，Plugin.Dispose 會呼叫 Disable）
        //    之後那格若還留在資訊列上，就是一格永遠不會再更新、也點不掉的殭屍欄位。
        RemoveDtr();

        windowOpen = false;
        drawErrorReported = false;
    }

    private void OnCommand(string command, string arguments) => windowOpen = !windowOpen;

    #region DTR 預覽

    /// <summary>把某一列的圖示丟到資訊列上預覽（同時間只有一格，再按別列就換掉）。</summary>
    private void ShowInDtr(IconEntry entry)
    {
        try
        {
            dtrEntry ??= Svc.DtrBar.Get(DtrTitle);

            // 點陣圖示只能走 IconPayload；字元類直接放文字。
            dtrEntry.Text = entry.Kind == IconKind.Bitmap
                ? new SeString(new IconPayload(entry.Icon))
                : new SeString(new TextPayload(entry.Text));

            // 🔑 識別字放 tooltip：資訊列橫向空間很擠，而這一格的重點是「圖示本身長什麼樣」。
            dtrEntry.Tooltip =
                $"TC Toolbox — 圖示預覽\n{entry.Identifier}\n{entry.Note}\n\n左鍵：開關圖示對照表";
            dtrEntry.OnClick = _ => windowOpen = !windowOpen;
            dtrEntry.Shown = true;
            dtrCurrent = entry.Identifier;

            // 使用者要拿這個跟人對照，所以寫 Information（LogLevel 2 收得到）。
            Svc.Log.Information($"[{InternalName}] DTR 預覽 → {entry.Identifier}（{entry.Note}）");
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, $"[{InternalName}] 建立／更新 DTR 預覽欄位失敗。");
            RemoveDtr();
        }
    }

    /// <summary>收回預覽欄位。可重複呼叫。</summary>
    private void RemoveDtr()
    {
        if (dtrEntry != null)
        {
            try
            {
                dtrEntry.Remove();
            }
            catch (Exception ex)
            {
                Svc.Log.Error(ex, $"[{InternalName}] 移除 DTR 預覽欄位時發生例外（欄位參考仍會被丟棄）。");
            }

            dtrEntry = null;
        }

        dtrCurrent = null;
    }

    #endregion

    #region UI

    public override void DrawConfig()
    {
        if (ImGui.Button("開啟圖示對照表"))
            windowOpen = true;

        ImGui.SameLine();
        ImGui.TextDisabled($"或用指令 {Command}");

        ImGui.TextWrapped(
            "三個分頁：點陣圖示（BitmapFontIcon，只能放進 SeString，例如 DTR 欄位）、"
            + "符號字元（SeIconChar，遊戲字型私有區的真字元，可直接塞進任何字串）、"
            + "Unicode 常用符號（標準字元，能不能顯示看字型）。");

        ImGui.TextWrapped(
            "每一列可以複製識別字，也可以按「DTR」把該圖示放到資訊列上實地看效果——"
            + "同時間只會有一格，關掉視窗或停用本模組時會自動收回。");

        if (dtrCurrent != null)
        {
            ImGui.TextUnformatted($"目前 DTR 預覽：{dtrCurrent}");
            ImGui.SameLine();
            if (ImGui.Button("收回##dtrClear"))
                RemoveDtr();
        }
    }

    private void DrawWindow()
    {
        if (!windowOpen)
        {
            // 視窗一關就把預覽欄位收回：那格是「對照用」的，不該在看不到對照表的時候還留在資訊列上。
            if (dtrEntry != null) RemoveDtr();
            return;
        }

        ImGui.SetNextWindowSize(new Vector2(620, 520), ImGuiCond.FirstUseEver);

        // 標題引用 DisplayName；### 後面的 id 保持原字面值，視窗位置／大小的存檔才不會被重置。
        if (ImGui.Begin($"{DisplayName}###TCToolboxIconReference", ref windowOpen))
        {
            try
            {
                DrawBody();
            }
            catch (Exception ex)
            {
                // 🔴 Draw 擲例外會被 Dalamud 的錯誤閂鎖記一次，兩次就把視窗永久關掉。
                //    這裡自己攔下來並把視窗收掉，至少使用者還能重新開。
                if (!drawErrorReported)
                {
                    drawErrorReported = true;
                    Svc.Log.Error(ex, $"[{InternalName}] 繪製圖示對照表時發生例外，視窗已關閉。");
                }

                windowOpen = false;
            }
        }

        ImGui.End();
    }

    private void DrawBody()
    {
        using var tabs = ImRaii.TabBar("##iconTabs");
        if (!tabs) return;

        DrawTab("點陣圖示", "bitmap", BitmapEntries, ref searchBitmap,
                "遊戲的點陣圖示表。只能經由 SeString 的 IconPayload 使用（DTR 欄位、聊天訊息），"
                + "它不是字元，貼進純文字欄位不會有東西。程式碼寫法：new IconPayload(BitmapFontIcon.Xxx)。");

        DrawTab("符號字元", "seicon", SeIconEntries, ref searchSeIcon,
                "遊戲字型私有區（U+E020～U+E0E9）的真字元，可以直接串進任何字串。"
                + "程式碼寫法：SeIconChar.Xxx.ToIconString()。");

        DrawTab("Unicode 符號", "unicode", UnicodeEntries, ref searchUnicode,
                "標準 Unicode 字元，與遊戲無關。能不能顯示完全看目前的字型——"
                + "空白或方框代表這個字元在這裡不能用，那也是一個結論。");
    }

    private void DrawTab(
        string label,
        string id,
        IReadOnlyList<IconEntry> entries,
        ref string search,
        string hint)
    {
        // 分頁標籤不帶數量：標籤字串一變，ImGui 會當成另一個全新分頁而把選取重設回第一頁。
        using var tab = ImRaii.TabItem($"{label}###tab_{id}");
        if (!tab) return;

        ImGui.TextDisabled("(?)");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(hint);

        ImGui.SameLine();
        ImGui.SetNextItemWidth(-1f);
        ImGui.InputTextWithHint($"##search_{id}", "搜尋識別字／碼點／說明…", ref search, 64);

        var filter = search.Trim().ToLowerInvariant();
        var shown = 0;

        using (var table = ImRaii.Table(
                   $"##table_{id}", 4,
                   ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.ScrollY,
                   new Vector2(0f, -ImGui.GetTextLineHeightWithSpacing() - ImGui.GetStyle().ItemSpacing.Y)))
        {
            if (table)
            {
                ImGui.TableSetupScrollFreeze(0, 1);
                ImGui.TableSetupColumn("渲染", ImGuiTableColumnFlags.WidthFixed,
                                       Math.Max(ImGui.GetTextLineHeight() * 2f, ImGui.CalcTextSize("渲染").X));
                ImGui.TableSetupColumn("識別字", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("備註", ImGuiTableColumnFlags.WidthFixed, 130f);
                ImGui.TableSetupColumn("操作", ImGuiTableColumnFlags.WidthFixed, 170f);
                ImGui.TableHeadersRow();

                foreach (var entry in entries)
                {
                    if (filter.Length > 0 && !entry.SearchKey.Contains(filter, StringComparison.Ordinal))
                        continue;

                    shown++;
                    DrawRow(entry);
                }
            }
        }

        // 「顯示幾筆／共幾筆」放列上而不是 tooltip：搜尋打錯字時，0/156 是唯一看得出來的徵兆。
        ImGui.TextDisabled(filter.Length > 0
                               ? $"顯示 {shown} / 共 {entries.Count} 項（搜尋中）"
                               : $"共 {entries.Count} 項");
    }

    private void DrawRow(IconEntry entry)
    {
        using var rowId = ImRaii.PushId(entry.Identifier);

        ImGui.TableNextRow();

        ImGui.TableNextColumn();
        if (entry.Kind == IconKind.Bitmap)
        {
            // 點陣圖示走 Dalamud 的 SeString 渲染器——這是遊戲訊息／DTR 實際走的同一條繪製路徑。
            ImGuiHelpers.SeStringWrapped(entry.Encoded);
        }
        else
        {
            // 字元類直接畫字元；畫不出來（空白／方框）本身就是這張表要回答的問題。
            ImGui.TextUnformatted(entry.Text);
        }

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(entry.Identifier);

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(entry.Note);

        ImGui.TableNextColumn();
        if (ImGui.SmallButton("複製"))
            SetClipboard(entry.Identifier);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"複製識別字：{entry.Identifier}");

        if (entry.Text.Length > 0)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("字元"))
                SetClipboard(entry.Text);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("複製字元本身（可以直接貼進聊天欄或程式碼字串）");
        }

        ImGui.SameLine();
        var isCurrent = dtrCurrent == entry.Identifier;
        if (ImGui.SmallButton(isCurrent ? "收回" : "DTR"))
        {
            if (isCurrent)
                RemoveDtr();
            else
                ShowInDtr(entry);
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(isCurrent
                                 ? "把這個圖示從資訊列收回"
                                 : "把這個圖示放到資訊列上預覽（同時間只有一格；關閉視窗會自動收回）");
        }
    }

    /// <summary>寫剪貼簿。失敗只記錄不擲例外——這是在 Draw 路徑上被呼叫的。</summary>
    private void SetClipboard(string text)
    {
        try
        {
            ImGui.SetClipboardText(text);
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, $"[{InternalName}] 寫入剪貼簿失敗。");
        }
    }

    #endregion
}

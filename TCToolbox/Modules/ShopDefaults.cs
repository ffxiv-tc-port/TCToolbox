using System;
using System.Collections.Generic;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Interface.Utility.Raii;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 商店介面預設值。目前只有一項：<b>軍票商店開啟時自動切到指定分頁</b>。
/// </summary>
/// <remarks>
/// <para>
/// 機制與 <see cref="OptimizedFreeCompanyChest"/> 同一條路：找到視窗上原生的分頁圓鈕，
/// 複用它自己的事件按下去——跟使用者親手點分頁完全一樣。零 hook、零特徵碼、不寫遊戲記憶體。
/// </para>
/// <para>
/// 🔴 <b>與部隊置物櫃那個模組的關鍵差異：這裡不寫死節點 id。</b>
/// 軍票商店的分頁圓鈕節點 id 沒有任何離線來源可以確認，而寫死一個猜的節點 id
/// 失效時是<b>靜默的</b>（什麼都不發生，或更糟——按到別的元件）。
/// 改成每次開窗當場走一次 <c>NodeList</c> 把圓鈕撿出來，再依畫面 X 座標排成左右順序。
/// </para>
/// <para>
/// 🔑 <b>撿出來的數量本身就是自我檢查</b>：台服 <c>GCShopItemCategory</c> 有名字的列
/// 恰好三筆（軍用品／武器／防具，離線比對 <c>exd-tc/7.20/GCShopItemCategory.csv</c>，
/// 全表 5 列、列 0 與列 4 的 <c>Name</c> 是空字串）。撿到的圓鈕數量對不上這個數字，
/// 就代表「這個視窗長得跟我以為的不一樣」——這時候<b>什麼都不做</b>並寫一行記錄，
/// 而不是硬按下去。
/// </para>
/// <para>
/// ⚠️ 無法離線證明的兩件事，兩件的失敗形式都只是「切錯分頁或不切」，不會崩：
/// ①分頁圓鈕在畫面上是左右排列（依 <c>ScreenX</c> 排序才等於視覺順序）；
/// ②視覺順序與 <c>GCShopItemCategory</c> 的列順序一致。
/// 第一次實際開窗時會把撿到的每個圓鈕（節點 id／座標／目前是否選中）寫成 Information 記錄，
/// 對不上的話從記錄一眼就看得出來。
/// </para>
/// <para>
/// 🔴 <b>不跨幀保存原生指標。</b>PostSetup 只記一個 bool，真正動手在 PostDraw 當場重新取 addon。
/// 是否切成功靠圓鈕自己的 <c>IsSelected</c> 判定，不假設按了就成功。
/// </para>
/// <para>
/// 📌 <b>尚未實作</b>：來源清單裡的另一項「商店預設購買數量」。
/// 原因寫在 <see cref="DrawConfig"/> 的說明裡——承載購買數量的是哪一個 addon
/// 無法離線確認，而猜錯的方向（例如把數量框寫到市場委託或雇員介面上）代價是真金白銀。
/// </para>
/// </remarks>
public sealed unsafe class ShopDefaults : TcModule
{
    public override string InternalName => "ShopDefaults";
    public override string DisplayName => "商店介面預設值";

    public override string Description =>
        "開啟軍票商店（軍票交換）時自動切到你指定的分頁。走的是視窗上原生的分頁按鈕，" +
        "跟自己動手點一樣。預設「不切換」＝維持遊戲原本的行為。";

    public override ModuleCategory Category => ModuleCategory.Inventory;

    public override bool HasConfigUI => true;

    private const string GrandCompanyExchangeAddon = "GrandCompanyExchange";

    /// <summary>開窗後最多嘗試切換這麼久；到了還沒切成功就放棄並留下記錄。</summary>
    private const int AttemptWindowMs = 4_000;

    /// <summary>每次嘗試之間的最短間隔。</summary>
    private const int AttemptIntervalMs = 200;

    /// <summary>撿圓鈕的上限。分頁只有三個，開這麼大純粹是為了「撿到太多」也能被看見。</summary>
    private const int MaxTabButtons = 16;

    /// <summary>「不切換」＝維持遊戲原本的行為。</summary>
    public const int NoDefaultTab = -1;

    /// <summary>這次開窗還沒切完。⚠️ 純 bool，不存任何指標。</summary>
    private bool pending;

    private long deadlineTick;

    /// <summary>這次開窗是否已經把撿到的圓鈕寫進記錄（每次開窗只寫一次）。</summary>
    private bool reportedThisSession;

    /// <summary>分頁名稱，第一次用到才從遊戲的表建。</summary>
    private static IReadOnlyList<string>? tabNames;

    private ShopDefaultsConfig Config => Plugin.Instance.Config.ShopDefaults;

    protected override void OnEnable()
    {
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, GrandCompanyExchangeAddon, OnSetup);
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostDraw, GrandCompanyExchangeAddon, OnDraw);
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, GrandCompanyExchangeAddon, OnFinalize);
    }

    protected override void OnDisable()
    {
        Svc.AddonLifecycle.UnregisterListener(OnSetup);
        Svc.AddonLifecycle.UnregisterListener(OnDraw);
        Svc.AddonLifecycle.UnregisterListener(OnFinalize);
        pending = false;
    }

    /// <summary>
    /// 分頁名稱取自遊戲的 <c>GCShopItemCategory</c>，不自建對照表。
    /// 名稱為空的列（台服的列 0 與列 4）不是分頁，直接跳過。
    /// </summary>
    private static IReadOnlyList<string> TabNames
    {
        get
        {
            if (tabNames != null) return tabNames;

            var list = new List<string>();
            try
            {
                foreach (var row in Svc.Data.GetExcelSheet<GCShopItemCategory>())
                {
                    var name = row.Name.ExtractText();
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    list.Add(name.Trim());
                }
            }
            catch (Exception ex)
            {
                Svc.Log.Error(ex, "[ShopDefaults] 讀取 GCShopItemCategory 失敗，分頁清單會是空的。");
            }

            tabNames = list;
            return tabNames;
        }
    }

    private void OnSetup(AddonEvent type, AddonArgs args)
    {
        reportedThisSession = false;

        if (!IsSelectableTab(Config.GrandCompanyDefaultTab))
        {
            pending = false;
            return;
        }

        // PostSetup 這一刻元件不一定就緒，只記旗標，實際動手交給 PostDraw。
        pending = true;
        deadlineTick = Environment.TickCount64 + AttemptWindowMs;
        Throttle.Reset("ShopDefaults-GCAttempt");
    }

    private void OnFinalize(AddonEvent type, AddonArgs args) => pending = false;

    private void OnDraw(AddonEvent type, AddonArgs args)
    {
        if (!pending) return;

        try
        {
            if (!Throttle.Pass("ShopDefaults-GCAttempt", AttemptIntervalMs)) return;

            var target = Config.GrandCompanyDefaultTab;
            if (!IsSelectableTab(target))
            {
                pending = false;
                return;
            }

            // ⚠️ 當場重新取，不用任何跨幀保存的指標。
            var addon = (AtkUnitBase*)args.Addon.Address;
            if (!UiHelper.IsReady(addon)) return;

            var buttons = stackalloc AtkComponentRadioButton*[MaxTabButtons];
            var count = CollectTabButtons(addon, buttons, MaxTabButtons);

            if (!reportedThisSession)
            {
                reportedThisSession = true;
                ReportButtons(addon, buttons, count);
            }

            // 🔑 自我檢查：撿到的圓鈕數量必須等於遊戲自己的分頁數。
            // 對不上就代表對這個視窗的理解是錯的 —— 什麼都不做，不硬按。
            var expected = TabNames.Count;
            if (expected == 0 || count != expected)
            {
                pending = false;
                Svc.Log.Information(
                    $"[{InternalName}] 軍票商店撿到 {count} 個分頁圓鈕，與 GCShopItemCategory 的 {expected} 個分頁對不上，" +
                    "不切換分頁（詳細節點資訊見上一行記錄）。");
                return;
            }

            var radio = buttons[target];
            if (radio == null)
            {
                pending = false;
                Svc.Log.Information($"[{InternalName}] 第 {target + 1} 個分頁圓鈕是 null，不切換。");
                return;
            }

            // 已經在目標分頁（開窗預設就是它，或上一次點擊已生效）→ 收工。
            if (radio->IsSelected)
            {
                pending = false;
                Svc.Log.Information($"[{InternalName}] 軍票商店目前已在「{DescribeTab(target)}」，不需切換。");
                return;
            }

            if (Environment.TickCount64 >= deadlineTick)
            {
                pending = false;
                Svc.Log.Information(
                    $"[{InternalName}] 切換到「{DescribeTab(target)}」逾時未生效（{AttemptWindowMs}ms）——" +
                    "分頁圓鈕找得到但點下去沒有選上，請回報。");
                return;
            }

            // AtkComponentRadioButton 繼承 AtkComponentButton，版面相容；
            // ClickButton 會複用節點自己的事件，等同使用者親手點分頁。
            UiHelper.ClickButton(addon, (AtkComponentButton*)radio);
        }
        catch (Exception ex)
        {
            pending = false;
            Svc.Log.Error(ex, $"[{InternalName}] 切換軍票商店預設分頁時發生例外");
        }
    }

    /// <summary>
    /// 走一次 addon 的節點清單，把<b>確認是 RadioButton</b> 的元件依畫面 X 座標由左至右撿出來。
    /// </summary>
    /// <remarks>
    /// 🔴 三道型別閘門缺一不可，理由與 <see cref="OptimizedFreeCompanyChest"/> 相同：
    /// <c>AtkUldObjectInfo</c> 只有 0x10 位元組而 <c>ComponentType</c> 位在 +0x10，
    /// <c>BaseType</c> 不是 <c>Component</c> 卻照樣當成 <c>AtkUldComponentInfo</c> 讀，
    /// 是實實在在的越界讀取，而且讀到的值還會被當成型別判斷用。
    /// </remarks>
    private static int CollectTabButtons(AtkUnitBase* addon, AtkComponentRadioButton** buffer, int capacity)
    {
        var count = 0;

        var uld = &addon->UldManager;
        if (uld->LoadedState != AtkLoadState.Loaded) return 0;

        var nodeList = uld->NodeList;
        if (nodeList == null) return 0;

        var positions = stackalloc float[capacity];

        for (var i = 0; i < uld->NodeListCount && count < capacity; i++)
        {
            var node = nodeList[i];
            if (node == null) continue;

            // 元件節點的原始型別值是 >= 1000（CS 的 NodeType.Component 是 GetNodeType() 的回傳值 10000，
            // 不是節點自己那個欄位的值——不能拿它去比對）。
            if ((ushort)node->Type < 1000) continue;
            if (!node->IsVisible()) continue;

            var component = ((AtkComponentNode*)node)->Component;
            if (component == null) continue;
            if (component->UldManager.LoadedState != AtkLoadState.Loaded) continue;
            if (component->UldManager.BaseType != AtkUldManagerBaseType.Component) continue;

            var objectInfo = (AtkUldComponentInfo*)component->UldManager.Objects;
            if (objectInfo == null) continue;
            if (objectInfo->ComponentType != ComponentType.RadioButton) continue;

            // 依 ScreenX 插入排序（分頁最多幾個，不需要更聰明的做法）
            var x = node->ScreenX;
            var slot = count;
            while (slot > 0 && positions[slot - 1] > x)
            {
                positions[slot] = positions[slot - 1];
                buffer[slot] = buffer[slot - 1];
                slot--;
            }

            positions[slot] = x;
            buffer[slot] = (AtkComponentRadioButton*)component;
            count++;
        }

        return count;
    }

    /// <summary>
    /// 把撿到的圓鈕寫成一行 Information。
    /// </summary>
    /// <remarks>
    /// 🔑 這一行是「視覺順序假設對不對」唯一的離線後驗證據：節點 id 與座標都印出來，
    /// 使用者回報記錄時就能直接看出分頁是不是左右排、順序是不是與 GCShopItemCategory 一致。
    /// <b>一律 Information 級</b>——使用者跑 LogLevel 2，Debug／Verbose 完全收不到。
    /// </remarks>
    private void ReportButtons(AtkUnitBase* addon, AtkComponentRadioButton** buttons, int count)
    {
        var parts = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var radio = buttons[i];
            if (radio == null)
            {
                parts.Add($"[{i}] null");
                continue;
            }

            var node = radio->AtkComponentButton.AtkComponentBase.OwnerNode;
            var nodeId = node == null ? 0u : node->AtkResNode.NodeId;
            var screenX = node == null ? float.NaN : node->AtkResNode.ScreenX;
            parts.Add($"[{i}] 節點 {nodeId} x={screenX:0} {(radio->IsSelected ? "已選中" : "未選")}");
        }

        Svc.Log.Information(
            $"[{InternalName}] 軍票商店分頁圓鈕（由左至右）共 {count} 個：" +
            (parts.Count == 0 ? "（一個都沒撿到）" : string.Join("、", parts)) +
            $"；遊戲的分頁名稱為 {(TabNames.Count == 0 ? "（讀不到）" : string.Join("／", TabNames))}");
    }

    private static bool IsSelectableTab(int tab) => tab >= 0 && tab < TabNames.Count;

    private static string DescribeTab(int tab) =>
        IsSelectableTab(tab) ? TabNames[tab] : "不切換";

    public override void DrawConfig()
    {
        var names = TabNames;

        if (names.Count == 0)
        {
            ImGui.TextColored(new System.Numerics.Vector4(1f, 0.4f, 0.4f, 1f),
                              "讀不到 GCShopItemCategory（軍票商店分頁名稱），無法設定預設分頁。");
            return;
        }

        // 選單第一項固定是「不切換」，也就是維持遊戲原本的行為。
        var labels = new string[names.Count + 1];
        labels[0] = "不切換（維持遊戲原本行為）";
        for (var i = 0; i < names.Count; i++)
            labels[i + 1] = names[i];

        var current = Config.GrandCompanyDefaultTab;
        var index = IsSelectableTab(current) ? current + 1 : 0;

        ImGui.SetNextItemWidth(280f);
        if (ImGui.Combo("軍票商店預設分頁", ref index, labels, labels.Length))
        {
            Config.GrandCompanyDefaultTab = index <= 0 ? NoDefaultTab : index - 1;
            Plugin.Instance.Config.Save();
        }

        using (ImRaii.PushIndent())
        {
            ImGui.TextDisabled("開啟軍票交換視窗時自動切到這個分頁；分頁名稱取自遊戲自己的資料表。");
            ImGui.TextDisabled("切不過去（例如視窗改版）時什麼都不會發生，並在記錄裡留一行說明原因。");
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextDisabled("「商店預設購買數量」尚未實作");
        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "承載購買數量的是哪一個原生視窗，離線查不出來——\n" +
                "候選有 ShopExchangeCurrencyDialog、InputNumeric、ShopCardDialog 等，\n" +
                "而 InputNumeric 同時被市場委託、雇員、房屋等介面共用。\n" +
                "猜錯的方向是把數量填到不該填的地方，代價是真金白銀，所以先不做。");
    }
}

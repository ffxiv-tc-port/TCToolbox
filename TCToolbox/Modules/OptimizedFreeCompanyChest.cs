using System;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Interface.Utility.Raii;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 部隊置物櫃預設頁面：開啟公會／部隊儲物櫃時自動切到指定的那一頁。
/// 機制：找到那一頁的<b>原生分頁圓鈕</b>，複用它自己的事件按下去——
/// 跟使用者親手點分頁完全同一條路徑。零 hook、零特徵碼、不寫記憶體。
/// </summary>
/// <remarks>
/// 📌 做法沿用 DailyRoutines 的 <c>OptimizedFreeCompanyChest</c>：分頁圓鈕的節點 id
/// 是 <c>10 + 頁序</c>（第 1～5 頁 ＝ 10～14），水晶頁是 15。
/// 📌 使用者只點名「預設頁面」，DR 那個模組的「快捷存取」與「Gil 圖示」<b>刻意不做</b>。
/// <para>
/// 🔴 <b>節點 id 是寫死值，而且失效是靜默的</b>（什麼都不發生，或更糟——切到別頁）。
/// 所以這裡不是「取到就按」，而是三道閘門全過才按：
/// <list type="number">
/// <item>取得到 <c>GetComponentByNodeId</c> 的元件；</item>
/// <item>元件的 uld <see cref="ComponentType"/> 必須真的是 <see cref="ComponentType.RadioButton"/>
/// ——這是關鍵一道：節點 id 若改指到別種元件，直接把它當按鈕讀
/// （<c>IsEnabled</c> 走 <c>OwnerNode</c>）就是在對不是那個型別的記憶體解參考；</item>
/// <item><see cref="UiHelper.ClickButton"/> 自己還會驗按鈕啟用、節點可見、事件非 null。</item>
/// </list>
/// 任何一道沒過都<b>什麼都不做</b>，並寫一行 <c>Information</c>（使用者跑 LogLevel 1）。
/// </para>
/// <para>
/// 🔴 <b>不跨幀保存原生指標。</b>PostSetup 只把「這次開窗還沒切過」記成一個 bool，
/// 真正動手是在 PostDraw 當場重新取 addon。切換是否生效靠圓鈕自己的
/// <c>IsSelected</c> 判定，而不是假設按了就成功；<see cref="AttemptWindowMs"/> 到了還沒選上
/// 就放棄並留下記錄。
/// </para>
/// <para>
/// ⚠️ 預設是 <see cref="InventoryType.Invalid"/>＝不切換，也就是<b>維持遊戲原本的行為</b>。
/// 舊設定檔沒有這個欄位，反序列化不會覆寫欄位初始值，所以升級上來的使用者不會突然被換頁。
/// </para>
/// </remarks>
public sealed unsafe class OptimizedFreeCompanyChest : TcModule
{
    public override string InternalName => "OptimizedFreeCompanyChest";
    public override string DisplayName => "部隊置物櫃預設頁面";

    public override string Description =>
        "開啟部隊／公會置物櫃時自動切換到你指定的那一頁（第 1～5 頁或水晶頁）。" +
        "走的是視窗上原生的分頁按鈕，跟自己動手點一樣。預設「不切換」＝維持遊戲原本的行為。";

    public override ModuleCategory Category => ModuleCategory.Inventory;

    public override bool HasConfigUI => true;

    private const string AddonName = "FreeCompanyChest";

    /// <summary>第 1 頁分頁圓鈕的節點 id；第 N 頁 ＝ <c>PageRadioNodeIdBase + (N-1)</c>。</summary>
    private const uint PageRadioNodeIdBase = 10;

    /// <summary>水晶頁分頁圓鈕的節點 id。</summary>
    private const uint CrystalsRadioNodeId = 15;

    /// <summary>可選頁面。⚠️ 水晶頁是 22001，<b>不是</b> 20005——不能用「頁序 ＋ 20000」去算。</summary>
    private static readonly InventoryType[] SelectablePages =
    [
        InventoryType.FreeCompanyPage1,
        InventoryType.FreeCompanyPage2,
        InventoryType.FreeCompanyPage3,
        InventoryType.FreeCompanyPage4,
        InventoryType.FreeCompanyPage5,
        InventoryType.FreeCompanyCrystals,
    ];

    /// <summary>開窗後最多嘗試切換這麼久；到了還沒切成功就放棄並留下記錄。</summary>
    private const int AttemptWindowMs = 4_000;

    /// <summary>每次嘗試之間的最短間隔。</summary>
    private const int AttemptIntervalMs = 200;

    /// <summary>這次開窗還沒切完（PostSetup 設起、切成功或放棄時清掉）。⚠️ 純 bool，不存任何指標。</summary>
    private bool pending;

    private long deadlineTick;

    private OptimizedFreeCompanyChestConfig Config => Plugin.Instance.Config.FreeCompanyChest;

    protected override void OnEnable()
    {
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, AddonName, OnSetup);
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostDraw, AddonName, OnDraw);
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, AddonName, OnFinalize);
    }

    protected override void OnDisable()
    {
        Svc.AddonLifecycle.UnregisterListener(OnSetup);
        Svc.AddonLifecycle.UnregisterListener(OnDraw);
        Svc.AddonLifecycle.UnregisterListener(OnFinalize);
        pending = false;
    }

    private void OnSetup(AddonEvent type, AddonArgs args)
    {
        if (!IsSelectable(Config.DefaultPage))
        {
            pending = false;
            return;
        }

        // PostSetup 這一刻元件不一定就緒，所以只記旗標，實際動手交給 PostDraw。
        pending = true;
        deadlineTick = Environment.TickCount64 + AttemptWindowMs;
        Throttle.Reset("OptimizedFreeCompanyChest-Attempt");
    }

    private void OnFinalize(AddonEvent type, AddonArgs args) => pending = false;

    private void OnDraw(AddonEvent type, AddonArgs args)
    {
        if (!pending) return;

        try
        {
            if (!Throttle.Pass("OptimizedFreeCompanyChest-Attempt", AttemptIntervalMs)) return;

            var target = Config.DefaultPage;
            if (!IsSelectable(target))
            {
                pending = false;
                return;
            }

            // ⚠️ 當場重新取，不用任何跨幀保存的指標。
            var addon = (AtkUnitBase*)args.Addon.Address;
            if (!UiHelper.IsReady(addon)) return;

            var nodeId = ResolveNodeId(target);

            if (!TryGetRadioButton(addon, nodeId, out var radio, out var diagnosis))
            {
                pending = false;
                Svc.Log.Information(
                    $"[{InternalName}] 找不到「{DescribePage(target)}」的分頁圓鈕（節點 id {nodeId}），不切換：{diagnosis}");
                return;
            }

            // 已經在目標頁（開窗預設就是它，或上一次點擊已生效）→ 收工。
            if (radio->IsSelected)
            {
                pending = false;
                Svc.Log.Information($"[{InternalName}] 目前已在「{DescribePage(target)}」，不需切換。");
                return;
            }

            if (Environment.TickCount64 >= deadlineTick)
            {
                pending = false;
                Svc.Log.Information(
                    $"[{InternalName}] 切換到「{DescribePage(target)}」逾時未生效（{AttemptWindowMs}ms，節點 id {nodeId}）——" +
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
            Svc.Log.Error(ex, $"[{InternalName}] 切換預設頁面時發生例外");
        }
    }

    private static uint ResolveNodeId(InventoryType page)
        => page == InventoryType.FreeCompanyCrystals
            ? CrystalsRadioNodeId
            : PageRadioNodeIdBase + (uint)(page - InventoryType.FreeCompanyPage1);

    /// <summary>
    /// 取分頁圓鈕，並且<b>確認它真的是圓鈕</b>。
    /// 🔴 這道型別檢查是本模組唯一擋得住「節點 id 改掉了」的東西：少了它，
    /// 節點 id 指到別種元件時我們會直接把它當按鈕讀，那是自找的存取違規，
    /// 而且 <c>try/catch</c> 攔不到。
    /// </summary>
    private static bool TryGetRadioButton(
        AtkUnitBase* addon, uint nodeId, out AtkComponentRadioButton* radio, out string diagnosis)
    {
        radio = null;

        var component = addon->GetComponentByNodeId(nodeId);
        if (component == null)
        {
            diagnosis = "GetComponentByNodeId 回傳 null";
            return false;
        }

        if (component->UldManager.LoadedState != AtkLoadState.Loaded)
        {
            diagnosis = $"元件 uld 尚未載入（LoadedState={component->UldManager.LoadedState}）";
            return false;
        }

        // 🔴 這個檢查不是形式：AtkUldObjectInfo 只有 0x10 位元組，而 ComponentType 位在 +0x10，
        // 也就是**基底型別的結尾之外**。BaseType 不是 Component 時就把它當
        // AtkUldComponentInfo 讀，是實實在在的越界讀取（而且讀到的值還會被當成型別判斷用）。
        if (component->UldManager.BaseType != AtkUldManagerBaseType.Component)
        {
            diagnosis = $"元件的 uld BaseType 是 {component->UldManager.BaseType}，不是 Component";
            return false;
        }

        var objectInfo = (AtkUldComponentInfo*)component->UldManager.Objects;
        if (objectInfo == null)
        {
            diagnosis = "元件沒有 uld 物件資訊";
            return false;
        }

        var componentType = objectInfo->ComponentType;
        if (componentType != ComponentType.RadioButton)
        {
            diagnosis = $"元件型別是 {componentType}，不是 RadioButton —— 節點 id 對應可能已經改變";
            return false;
        }

        radio = (AtkComponentRadioButton*)component;
        diagnosis = string.Empty;
        return true;
    }

    private static bool IsSelectable(InventoryType page) => Array.IndexOf(SelectablePages, page) >= 0;

    /// <summary>頁面顯示名。水晶頁取遊戲自己的用語（<c>Addon</c> 2990 ＝「水晶」）。</summary>
    private static string DescribePage(InventoryType page)
    {
        if (page == InventoryType.FreeCompanyCrystals)
        {
            var text = Svc.Data.GetExcelSheet<Addon>().GetRowOrDefault(2990)?.Text.ExtractText();
            return string.IsNullOrWhiteSpace(text) ? "水晶" : text;
        }

        if (!IsSelectable(page)) return "不切換";

        return $"第 {page - InventoryType.FreeCompanyPage1 + 1} 頁";
    }

    public override void DrawConfig()
    {
        var current = Config.DefaultPage;
        var preview = IsSelectable(current) ? DescribePage(current) : "不切換（維持遊戲預設）";

        ImGui.SetNextItemWidth(220f);
        using (var combo = ImRaii.Combo("開啟時切換到", preview))
        {
            if (combo)
            {
                if (ImGui.Selectable("不切換（維持遊戲預設）", !IsSelectable(current)))
                {
                    Config.DefaultPage = InventoryType.Invalid;
                    Plugin.Instance.Config.Save();
                }

                foreach (var page in SelectablePages)
                {
                    if (ImGui.Selectable(DescribePage(page), current == page))
                    {
                        Config.DefaultPage = page;
                        Plugin.Instance.Config.Save();
                    }
                }
            }
        }

        using (ImRaii.PushIndent())
        {
            ImGui.TextDisabled("按的是視窗上原生的分頁按鈕，跟自己動手點一樣。");
            ImGui.TextDisabled("找不到分頁按鈕時什麼都不做，並在記錄檔留下一行說明。");
        }
    }
}

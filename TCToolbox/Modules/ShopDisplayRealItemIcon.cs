using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Memory;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using TCToolbox.Core;
using ValueType = FFXIVClientStructs.FFXIV.Component.GUI.ValueType;

namespace TCToolbox.Modules;

/// <summary>
/// 商店列表顯示真實道具圖示：在各種商店／兌換視窗裡，把清單縮圖換成該道具在遊戲裡的真正圖示，
/// 而不是遊戲原本畫的通用縮圖。純唯讀顯示，不 hook、不寫記憶體 patch、不送封包。
/// 參考 DailyRoutines <c>ShopDisplayRealItemIcon</c> 重寫（API13、無 OmenTools／KamiToolKit 相依）。
/// </summary>
/// <remarks>
/// 🔴 <b>再入防護</b>：<c>OnRefresh</c> 是 addon 的虛擬函式，直接呼叫理論上可能再度觸發
/// PostRefresh 監聽而形成遞迴。<see cref="reentryGuard"/> 讓任何巢狀進入立即返回，
/// 就算 Dalamud 的 hook 點與此重疊也不會堆疊溢位。
/// </remarks>
public sealed unsafe class ShopDisplayRealItemIcon : TcModule
{
    public override string InternalName => "ShopDisplayRealItemIcon";

    public override string DisplayName => "商店列表顯示真實道具圖示";

    public override string Description =>
        "在商店、兌換所、軍票交換、收藏品交易等視窗裡，把清單縮圖換成該道具真正的圖示。" +
        "純顯示，不影響購買行為。（圖示的版面位置取自國際服，台服若對不上只是圖示不會換，不會出錯。）";

    public override ModuleCategory Category => ModuleCategory.Inventory;

    /// <summary>PostDraw 每幀都會來，收藏品視窗的節流窗（毫秒）。</summary>
    private const int CollectablesDrawThrottleMs = 100;

    /// <summary>再入防護：直接呼叫 <c>OnRefresh</c> 時避免遞迴。</summary>
    private static bool reentryGuard;

    private static readonly string[] ShopExchangeAddons =
        ["ShopExchangeCurrency", "ShopExchangeItem", "ShopExchangeCoin"];

    protected override void OnEnable()
    {
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "Shop", OnShop);
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostRefresh, "Shop", OnShop);
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "InclusionShop", OnInclusionShop);
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostRefresh, "InclusionShop", OnInclusionShop);
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "GrandCompanyExchange", OnGrandCompanyExchange);
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostRefresh, "GrandCompanyExchange", OnGrandCompanyExchange);
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, ShopExchangeAddons, OnShopExchange);
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostRefresh, ShopExchangeAddons, OnShopExchange);
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostRefresh, "CollectablesShop", OnCollectablesShop);
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostDraw, "CollectablesShop", OnCollectablesShop);
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "FreeShop", OnFreeShop);
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostRefresh, "FreeShop", OnFreeShop);
    }

    protected override void OnDisable()
    {
        Svc.AddonLifecycle.UnregisterListener(OnShop);
        Svc.AddonLifecycle.UnregisterListener(OnInclusionShop);
        Svc.AddonLifecycle.UnregisterListener(OnGrandCompanyExchange);
        Svc.AddonLifecycle.UnregisterListener(OnShopExchange);
        Svc.AddonLifecycle.UnregisterListener(OnCollectablesShop);
        Svc.AddonLifecycle.UnregisterListener(OnFreeShop);
    }

    /// <summary>取回 addon 指標並確認 <c>AtkValues</c> 欄位非 null（見類別註解的 AVE 說明）。</summary>
    private static AtkUnitBase* Resolve(AddonArgs args)
    {
        var addon = (AtkUnitBase*)args.Addon.Address;
        if (addon == null || addon->AtkValues == null) return null;
        return addon;
    }

    /// <summary>讀第 <paramref name="index"/> 格的 UInt；越界或非數值型別一律回 0（不解參考）。</summary>
    private static uint ReadUInt(AtkUnitBase* addon, int index)
    {
        if (index < 0 || index >= addon->AtkValuesCount) return 0;
        var v = addon->AtkValues[index];
        return v.Type is ValueType.Int or ValueType.UInt ? v.UInt : 0;
    }

    /// <summary>
    /// 只把「本來就是數值」的槽換成道具圖示。字串／其他型別的槽一律不碰——見類別註解。
    /// </summary>
    private static void TrySetIcon(AtkUnitBase* addon, int writeIndex, uint itemId, uint iconOffset = 0)
    {
        if (writeIndex < 0 || writeIndex >= addon->AtkValuesCount) return;

        var slot = addon->AtkValues[writeIndex];
        if (slot.Type is not (ValueType.Int or ValueType.UInt)) return;

        if (!Svc.Data.GetExcelSheet<Item>().GetRowOrDefault(itemId).HasValue) return;
        var icon = Svc.Data.GetExcelSheet<Item>().GetRow(itemId).Icon;
        if (icon == 0) return;

        addon->AtkValues[writeIndex].SetUInt(icon + iconOffset);
    }

    /// <summary>改完值後叫 addon 依新值重畫；再入時立即返回。</summary>
    private static void SafeRefresh(AtkUnitBase* addon)
    {
        if (reentryGuard) return;
        reentryGuard = true;
        try
        {
            addon->OnRefresh(addon->AtkValuesCount, addon->AtkValues);
        }
        finally
        {
            reentryGuard = false;
        }
    }

    private static void OnFreeShop(AddonEvent type, AddonArgs args)
    {
        if (reentryGuard) return;
        var addon = Resolve(args);
        if (addon == null) return;

        var count = ReadUInt(addon, 3);
        if (count == 0) return;

        for (var i = 0; i < count; i++)
        {
            var itemId = ReadUInt(addon, 65 + i);
            if (itemId != 0) TrySetIcon(addon, 126 + i, itemId);
        }

        SafeRefresh(addon);
    }

    private static void OnShopExchange(AddonEvent type, AddonArgs args)
    {
        if (reentryGuard) return;
        var addon = Resolve(args);
        if (addon == null) return;

        var count = ReadUInt(addon, 4);
        if (count == 0) return;

        for (var i = 0; i < count; i++)
        {
            var itemId = ReadUInt(addon, 1064 + i);
            if (itemId != 0) TrySetIcon(addon, 210 + i, itemId);
        }

        SafeRefresh(addon);
    }

    private static void OnGrandCompanyExchange(AddonEvent type, AddonArgs args)
    {
        if (reentryGuard) return;
        var addon = Resolve(args);
        if (addon == null) return;

        var count = ReadUInt(addon, 1);
        if (count == 0) return;

        for (var i = 0; i < count; i++)
        {
            var itemId = ReadUInt(addon, 317 + i);
            if (itemId != 0) TrySetIcon(addon, 167 + i, itemId);
        }

        SafeRefresh(addon);
    }

    private static void OnInclusionShop(AddonEvent type, AddonArgs args)
    {
        if (reentryGuard) return;
        var addon = Resolve(args);
        if (addon == null) return;

        var count = ReadUInt(addon, 298);
        if (count == 0) return;

        for (var i = 0; i < count; i++)
        {
            var itemId = ReadUInt(addon, 300 + i * 18);
            if (itemId != 0) TrySetIcon(addon, 301 + i * 18, itemId);
        }

        SafeRefresh(addon);
    }

    private static void OnShop(AddonEvent type, AddonArgs args)
    {
        if (reentryGuard) return;
        var addon = Resolve(args);
        if (addon == null) return;

        // AtkValues[0]＝目前分頁（0＝販售清單、1＝買回清單）；AtkValues[2]＝清單筆數。
        var mode = ReadUInt(addon, 0);
        var count = ReadUInt(addon, 2);
        if (count == 0) return;

        for (var i = 0; i < count; i++)
        {
            uint itemId;
            uint iconOffset = 0;

            if (mode == 1)
            {
                // 買回清單的道具與 HQ 旗標存在 agent 的 Buyback 結構裡，不在 AtkValues。
                var proxy = ShopEventHandler.AgentProxy.Instance();
                if (proxy == null || proxy->Handler == null) break;

                var buyback = proxy->Handler->Buyback[i];
                itemId = buyback.ItemId;
                if ((buyback.Flags & InventoryItem.ItemFlags.HighQuality) != 0) iconOffset = 1_000_000;
            }
            else
            {
                itemId = ReadUInt(addon, 441 + i);
            }

            if (itemId != 0) TrySetIcon(addon, 197 + i, itemId, iconOffset);
        }

        SafeRefresh(addon);
    }

    /// <summary>
    /// 收藏品交易視窗。它<b>不</b>是改 AtkValues，而是走節點樹直接把每一格的圖示節點換圖。
    /// </summary>
    /// <remarks>
    /// 🔴 這條路徑最脆弱（寫死 node id 28／16+j／SearchNodeById 2、4，全部國際服版面）。
    /// 每一步都判空後才往下走；<c>LoadIconTexture</c> 只作用在真正的 <c>AtkImageNode</c> 上、
    /// 是純載圖操作，就算 node 對應錯了也只是顯示錯圖，不會崩。
    /// </remarks>
    private static void OnCollectablesShop(AddonEvent type, AddonArgs args)
    {
        if (type == AddonEvent.PostDraw &&
            !Throttle.Pass("ShopDisplayRealItemIcon-CollectablesShop", CollectablesDrawThrottleMs))
            return;

        var addon = Resolve(args);
        if (addon == null) return;

        if (type == AddonEvent.PostRefresh)
        {
            var count = ReadUInt(addon, 20);
            if (count == 0)
            {
                collectablesCache = [];
                return;
            }

            var list = new System.Collections.Generic.List<(uint IconId, string Name)>();
            for (var i = 0; i < count; i++)
            {
                var raw = ReadUInt(addon, 34 + 11 * i) % 500_000;
                if (raw == 0) continue;
                var row = Svc.Data.GetExcelSheet<Item>().GetRowOrDefault(raw);
                if (!row.HasValue) continue;
                var name = row.Value.Name.ExtractText();
                if (string.IsNullOrEmpty(name)) continue;
                list.Add((row.Value.Icon, name));
            }

            collectablesCache = list;
        }

        if (collectablesCache.Count == 0) return;

        var listComponent = (AtkComponentNode*)addon->GetNodeById(28u);
        if (listComponent == null || listComponent->Component == null) return;

        var nodeList = listComponent->Component->UldManager.NodeList;
        var nodeCount = listComponent->Component->UldManager.NodeListCount;

        for (var j = 0; j < 15; j++)
        {
            var slot = 16 + j;
            if (slot >= nodeCount) break;

            var rowNode = (AtkComponentNode*)nodeList[slot];
            if (rowNode == null || rowNode->Component == null) continue;

            var textNode = (AtkTextNode*)rowNode->Component->UldManager.SearchNodeById(4u);
            if (textNode == null) break;
            if (!textNode->NodeText.StringPtr.HasValue) continue;

            var shownName = SanitizeName(ReadNodeText(textNode));
            if (shownName.Length == 0) continue;

            var icon = FindIconByName(shownName);
            if (icon == 0) continue;

            var imageNode = (AtkImageNode*)rowNode->Component->UldManager.SearchNodeById(2u);
            if (imageNode == null) continue;

            imageNode->LoadIconTexture(icon, 0);
        }
    }

    private static System.Collections.Generic.List<(uint IconId, string Name)> collectablesCache = [];

    private static uint FindIconByName(string shownName)
    {
        foreach (var (iconId, name) in collectablesCache)
        {
            if (name.Contains(shownName, System.StringComparison.OrdinalIgnoreCase)) return iconId;
        }

        return 0;
    }

    /// <summary>讀節點上顯示的文字，剝掉 SeString payload 之後只留看得見的字。</summary>
    /// <remarks>
    /// 🔴 <b>一定要走 <c>MemoryHelper.ReadSeString(...).TextValue</c>，不可以用 <c>Utf8String.ToString()</c>。</b>
    /// ⚠️ 呼叫端的 <c>NodeText.StringPtr</c> 判空不能省：<c>MemoryHelper.ReadSeString</c> 只判
    /// <c>Utf8String*</c> 本身非 null，StringPtr 為 null 而 Length 還留著殘值時，
    /// <c>AsSpan()</c> 會建出一個長度非零、指向位址 0 的 Span，讀下去就是存取違規（try/catch 攔不到）。
    /// </remarks>
    private static string ReadNodeText(AtkTextNode* textNode) =>
        MemoryHelper.ReadSeString(&textNode->NodeText).TextValue;

    /// <summary>
    /// 剝完 payload 之後的收尾：去掉殘留的控制字元與前後空白。
    /// </summary>
    /// <remarks>
    /// 剝 payload 是 <see cref="ReadNodeText"/> 的責任，這裡只是最後一道保險——
    /// 換行之類的控制字元不影響顯示，但會讓字串比對落空。
    /// </remarks>
    private static string SanitizeName(string raw)
    {
        var sb = new System.Text.StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            if (ch >= ' ') sb.Append(ch);
        }

        return sb.ToString().Trim();
    }
}

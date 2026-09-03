using System;
using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 投影台：已擁有的幻影不要再收納一次。
/// </summary>
/// <remarks>
/// <para>
/// 與 <see cref="GlamourDuplicateCleanup"/> 互補：那個是<b>事後清</b>（把已經重複的取回背包），
/// 這個是<b>事前擋</b>（在「幻影化」的確認框上就攔下來），兩個可以同時開。
/// </para>
/// <para>
/// 📌 <b>遊戲自己不擋。</b>台服 7.20 的 <c>LogMessage</c> 表裡只有
/// 「投影台中所保存的幻影數量已達上限」(#4266) 這種容量錯誤，
/// <b>沒有</b>任何「這件外觀已經有了」的拒絕訊息（離線全表掃過）；
/// 道具說明上那句「投影台中有 N 個」(<c>LogMessage</c> #1452) 只是告知，不會阻止你再收一次。
/// 所以重複收納會白白吃掉一格投影台格數與一顆觸媒。
/// </para>
/// <para>
/// 🔴 <b>攔截點是確認框，不是任何原生節點。</b>流程是：
/// <list type="number">
/// <item>背包道具右鍵（Dalamud 的 <c>OnMenuOpened</c>）→ 記下「使用者現在瞄準的是哪一格」。
/// <b>只記數值，不留任何原生指標</b>。</item>
/// <item>那一格的外觀若已經在投影台裡，就把它記成候選（有效期 <see cref="CandidateTtlMs"/>）。</item>
/// <item>候選有效期內出現 <c>SelectYesno</c>，而且它的內容含有<b>台服自己的</b>
/// 「幻影化」確認字串（<c>Addon</c> 第 11994 列）時 → 提示，並視設定按下「否」。</item>
/// </list>
/// </para>
/// <para>
/// 🔴 <b>字串錨點是執行期從 <c>Addon</c> 表算出來的，不是寫死的中文。</b>
/// 取該列<b>最長的一段純文字 payload</b>（跳過所有參數與換行巨集）當比對錨，
/// 所以換語言、換版本都跟著走。算不出錨點時<b>整個攔截功能停用</b>並寫進記錄
/// —— 失效的方向是「遊戲照常運作」，不是「亂按對話框」。
/// </para>
/// <para>
/// 🔴 <b>按的是「否」按鈕本身（<c>AddonSelectYesno.NoButton</c>），不是寫死的 callback 序號。</b>
/// 用序號的話萬一 0/1 的意義相反，這個模組就會從「攔截重複收納」變成「自動確認收納」——
/// 那比什麼都不做還糟。改成重播那顆按鈕自己的事件，按不到就只提示不動作。
/// </para>
/// </remarks>
public sealed unsafe class GlamourStoreDuplicateGuard : TcModule
{
    public override string InternalName => "GlamourStoreDuplicateGuard";

    public override string DisplayName => "投影台：攔截重複收納";

    public override string Description =>
        "把已經在投影台裡的外觀再「幻影化」一次時，在確認框上攔下來並說明原因（遊戲自己不會擋）。" +
        "只在你自己右鍵那件裝備之後才會判斷，攔截時一定會在聊天欄說明。與「重複幻影取出」互補。";

    public override ModuleCategory Category => ModuleCategory.Inventory;

    public override bool HasConfigUI => true;

    /// <summary>「確定要使用…將所選道具化為幻影保存進投影台嗎？…」台服 7.20 離線核對過。</summary>
    private const uint AddonRowGlamourConfirm = 11994;

    /// <summary>字串錨點最多取幾個字。</summary>
    /// <remarks>
    /// ⚠️ 取太長會被對話框的自動換行／版面差異弄成不相符；太短又可能誤命中別的對話框。
    /// 這裡取一段「連續、無換行巨集」的文字前 20 個字，長度不足 <see cref="MinAnchorLength"/> 就放棄。
    /// </remarks>
    private const int MaxAnchorLength = 20;

    private const int MinAnchorLength = 6;

    /// <summary>右鍵之後這麼久以內出現的確認框才算是同一次操作（毫秒）。</summary>
    private const int CandidateTtlMs = 20_000;

    private GlamourStoreDuplicateGuardConfig Config => Plugin.Instance.Config.GlamourStoreGuard;

    /// <summary>比對用字串錨點；空字串＝算不出來，攔截功能停用。</summary>
    private string confirmAnchor = string.Empty;

    /// <summary>
    /// 使用者剛剛右鍵、而且外觀已經在投影台裡的那一件。
    /// 🔴 <b>只有數值</b>：跨幀保存原生指標是紅線。
    /// </summary>
    private readonly record struct Candidate(
        uint BaseItemId, string DisplayName, int ExistingCount, long Tick);

    private Candidate? candidate;

    /// <summary>上一次攔截的摘要，顯示在設定畫面上。</summary>
    private string lastAction = string.Empty;

    protected override void OnEnable()
    {
        confirmAnchor = BuildConfirmAnchor();

        // 🔑 「回 0」比「報錯」常見：錨點算不出來時整個模組會安靜地什麼都不做。
        // 一律 Information 級（使用者跑 LogLevel 1），讓「錨點是空的」看得出來。
        if (confirmAnchor.Length == 0)
        {
            Svc.Log.Information(
                $"[{InternalName}] 算不出 Addon#{AddonRowGlamourConfirm} 的比對錨點，" +
                "攔截功能停用（遊戲行為完全不受影響）。請回報。");
        }
        else
        {
            Svc.Log.Information($"[{InternalName}] 確認框比對錨點：「{confirmAnchor}」");
        }

        Svc.ContextMenu.OnMenuOpened += OnMenuOpened;
        Svc.Framework.Update += OnFrameworkUpdate;
    }

    protected override void OnDisable()
    {
        Svc.Framework.Update -= OnFrameworkUpdate;
        Svc.ContextMenu.OnMenuOpened -= OnMenuOpened;
        candidate = null;
        confirmAnchor = string.Empty;
        lastAction = string.Empty;
    }

    /// <summary>
    /// 從 <c>Addon</c> 表算出比對錨點：取該列<b>最長的一段純文字</b>。
    /// </summary>
    /// <remarks>
    /// 參數（觸媒名稱之類）與換行都是 macro payload，天然被跳過，
    /// 所以拿到的一定是一段「同一行、不含變數」的字，適合拿來 <c>Contains</c>。
    /// </remarks>
    private static string BuildConfirmAnchor()
    {
        var row = Svc.Data.GetExcelSheet<Addon>()?.GetRowOrDefault(AddonRowGlamourConfirm);
        if (row is not { } addon) return string.Empty;

        var best = string.Empty;
        foreach (var payload in addon.Text)
        {
            if (payload.Type != ReadOnlySePayloadType.Text) continue;

            var text = Encoding.UTF8.GetString(payload.Body.Span).Trim();
            if (text.Length > best.Length) best = text;
        }

        if (best.Length < MinAnchorLength) return string.Empty;

        return best.Length <= MaxAnchorLength ? best : best[..MaxAnchorLength];
    }

    private void OnMenuOpened(IMenuOpenedArgs args)
    {
        candidate = null;

        if (args.MenuType != ContextMenuType.Inventory) return;
        if (args.Target is not MenuTargetInventory inv || inv.TargetItem is not { } target) return;

        // 投影台資料沒載入時我們無從判斷。**明確地什麼都不做**，不要把「不知道」當成「沒有重複」。
        if (!PrismBox.TryReady(out var reason))
        {
            if (Throttle.Pass($"{InternalName}-NotReady", 30_000))
                Svc.Log.Information($"[{InternalName}] 略過判斷：{reason}");
            return;
        }

        // GameInventoryType 的數值與 InventoryType 完全一致（Inventory1=0 … FreeCompanyPage1=20000）。
        var container = (InventoryType)(ushort)target.ContainerType;
        var slot = (int)target.InventorySlot;

        var manager = InventoryManager.Instance();
        if (manager == null) return;

        var item = manager->GetInventorySlot(container, slot);
        if (item == null || item->ItemId == 0) return;

        var baseItemId = item->GetBaseItemId();
        if (baseItemId == 0) return;

        // 染色是 InventoryItem 自己的欄位（0x37 的 FixedSizeArray2<byte>），不是特徵碼函式。
        var stains = item->Stains;
        var stain0 = stains.Length > 0 ? stains[0] : (byte)0;
        var stain1 = stains.Length > 1 ? stains[1] : (byte)0;

        var existing = CountExisting(baseItemId, stain0, stain1);
        if (existing <= 0) return;

        candidate = new Candidate(
            baseItemId,
            ItemNames.Get(baseItemId, (item->Flags & InventoryItem.ItemFlags.HighQuality) != 0),
            existing,
            Environment.TickCount64);
    }

    /// <summary>
    /// 投影台裡已經有幾件同樣的外觀。
    /// </summary>
    /// <remarks>
    /// 📌 <b>刻意不比對優質／普通</b>：投影台裡的優質與普通品長得一模一樣，
    /// 兩件都留著就是純粹浪費一格。
    /// <para>
    /// ⚠️ 染色則<b>預設要一致</b>才算同一件（<see cref="GlamourStoreDuplicateGuardConfig.DistinguishByDye"/>）——
    /// 同一件裝備染成兩個顏色在投影台裡是兩種可用的外觀，一律當重複會擋掉正當的收納。
    /// 這個預設與 <see cref="GlamourDuplicateCleanup"/> 一致。
    /// </para>
    /// </remarks>
    private int CountExisting(uint baseItemId, byte stain0, byte stain1)
    {
        var count = 0;
        foreach (var entry in PrismBox.Snapshot())
        {
            if (entry.BaseItemId != baseItemId) continue;
            if (Config.DistinguishByDye && (entry.Stain0 != stain0 || entry.Stain1 != stain1)) continue;
            count++;
        }

        return count;
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (candidate is not { } pending) return;

        if (Environment.TickCount64 - pending.Tick > CandidateTtlMs)
        {
            candidate = null;
            return;
        }

        var baseAddon = UiHelper.GetAddon("SelectYesno");
        if (!UiHelper.IsReady(baseAddon)) return;

        // 錨點算不出來就不攔——寧可整個功能不能用，也不要對不認識的對話框按按鈕。
        if (confirmAnchor.Length == 0) return;

        var addon = (AddonSelectYesno*)baseAddon;
        var prompt = addon->PromptText;
        if (prompt == null) return;

        var text = prompt->NodeText.ToString();
        // 🔴 讀出 U+FFFD 替換字元＝窗的記憶體正在變動（多半是關閉中）：這一幀不判也不碰，candidate 留著下一幀重讀。
        if (AddonPrompt.LooksMidUpdate(text)) return;
        if (string.IsNullOrEmpty(text) || !text.Contains(confirmAnchor, StringComparison.Ordinal)) return;

        var blocked = false;
        if (Config.BlockConfirmation)
        {
            var press = ClickNo(baseAddon, addon);
            // 守衛擋下＝這一扇確認框剛被按過、還在關閉中：candidate 留著，等它收掉（或 TTL 到期），
            // 不對關閉中的窗再按第二次。
            if (press == UiHelper.ButtonPressResult.Guarded) return;
            blocked = press == UiHelper.ButtonPressResult.Pressed;
        }

        candidate = null;

        // 🔴 攔截一定要出聲。靜默地把使用者的操作取消掉是最糟的失敗形式。
        var message = blocked
            ? $"[TC Toolbox] 已攔截：「{pending.DisplayName}」的外觀投影台裡已經有 {pending.ExistingCount} 件，" +
              "收納只會多佔一格。要照樣收納請先關閉「投影台：攔截重複收納」。"
            : $"[TC Toolbox] 提醒：「{pending.DisplayName}」的外觀投影台裡已經有 {pending.ExistingCount} 件。";

        Svc.Chat.PrintError(message);

        lastAction = blocked
            ? $"已攔截「{pending.DisplayName}」（投影台裡已有 {pending.ExistingCount} 件）"
            : $"已提醒「{pending.DisplayName}」（投影台裡已有 {pending.ExistingCount} 件）";

        Svc.Log.Information(
            $"[{InternalName}] {(blocked ? "攔截" : "提醒")}重複收納：itemId={pending.BaseItemId}" +
            $"「{pending.DisplayName}」投影台既有 {pending.ExistingCount} 件" +
            $"（攔截設定={Config.BlockConfirmation}、按到否={blocked}）");
    }

    /// <summary>
    /// 按下確認框的「否」。
    /// </summary>
    /// <remarks>
    /// 🔴 一定要先確認 <c>OwnerNode</c> 非 null 再問 <c>IsEnabled</c>——
    /// CS 的 <c>AtkComponentButton.IsEnabled</c> 直接解參考 <c>OwnerNode</c>，沒有任何判空。
    /// <para>按不到就回 <see cref="UiHelper.ButtonPressResult.Unavailable"/>：那時只提示、不動遊戲，使用者照樣可以自己決定。
    /// <see cref="UiHelper.ButtonPressResult.Guarded"/>＝同一扇確認框剛按過、還在關閉中，呼叫端要等而不是提示。</para>
    /// </remarks>
    private static UiHelper.ButtonPressResult ClickNo(AtkUnitBase* baseAddon, AddonSelectYesno* addon)
    {
        var no = addon->NoButton;
        if (no == null || no->AtkComponentBase.OwnerNode == null) return UiHelper.ButtonPressResult.Unavailable;

        // 走 TryClickButton＝與 UiHelper.ClickSelectYesnoNo 共用同一把 SelectYesno 守衛鍵（併鍵：是／否都算按過）。
        return UiHelper.TryClickButton(baseAddon, no);
    }

    public override void DrawConfig()
    {
        var block = Config.BlockConfirmation;
        if (ImGui.Checkbox("直接按下「否」把收納擋掉", ref block))
        {
            Config.BlockConfirmation = block;
            Plugin.Instance.Config.Save();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "開啟（預設）：偵測到重複時直接幫你按「否」，並在聊天欄說明原因。\n" +
                "關閉：只在聊天欄提醒，確認框留給你自己決定。\n" +
                "\n" +
                "無論哪一種，攔截／提醒都一定會在聊天欄出現，不會靜默取消你的操作。");
        }

        var byDye = Config.DistinguishByDye;
        if (ImGui.Checkbox("染色不同的視為不同幻影（不算重複）", ref byDye))
        {
            Config.DistinguishByDye = byDye;
            Plugin.Instance.Config.Save();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "開啟（預設，與「重複幻影取出」一致）：同一件裝備染成不同顏色會各自視為一種外觀，不會被擋。\n" +
                "關閉：只看道具編號，同款一律視為重複。");
        }

        ImGui.Spacing();

        if (confirmAnchor.Length == 0)
        {
            ImGui.TextColored(new Vector4(1f, 0.45f, 0.4f, 1f),
                              "⚠ 讀不到遊戲的「幻影化」確認字串，攔截功能目前停用（遊戲行為不受影響）。");
        }
        else
        {
            ImGui.TextDisabled($"確認框比對字串：{confirmAnchor}");
        }

        if (lastAction.Length > 0)
            ImGui.TextDisabled($"上次動作：{lastAction}");

        ImGui.TextColored(new Vector4(1f, 0.8f, 0.35f, 1f),
                          "⚠ 只有在「投影台」視窗開著、資料已載入時才判斷得出來；判斷不出來時完全不動作。");
    }
}

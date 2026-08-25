using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 任務搜尋器旁的「快速登記」面板：一鍵把搜尋器開到目前選取的副本，或收藏起來的常用副本。
/// </summary>
/// <remarks>
/// 參考 DailyRoutines <c>FastContentsFinderRegister</c> 的意圖重寫，但<b>換了實作路線</b>：
/// <para>
/// 🔴 <b>不掃原生列表節點。</b>DR 原版靠寫死的節點編號（52／57／5／18／3）加一堆
/// 像素位移（<c>Y&gt;=300</c>、<c>ScreenY</c> 比較、<c>NodeList[3+i]</c>）去讀每一列的副本名，
/// 再用名稱反查 ID——那串偏移在台服沒驗過，而且列表節點索引正是 <c>CustomCS</c> 會讓它整代倒退的地方。
/// 這裡改讀 <c>AgentContentsFinder</c> 自己的結構欄位：<c>SelectedDuty</c>（含類型＋列號）
/// 直接就是「目前選取的那一項」，穩定且與版面繪製無關。
/// </para>
/// <para>
/// 🔴 <b>登記＝呼叫 <c>OpenRegularDuty</c> / <c>OpenRouletteDuty</c>（開啟並選取），不送報名封包。</b>
/// DR 原版送的是 <c>/pdrduty</c>→<c>RequestDuty*</c>（等於幫你按「參加」排隊）；這裡只把搜尋器
/// 開到那一項，使用者仍要自己按「參加」。跟 <see cref="ContentFinderCommand"/> 同一條安全路徑，
/// 也因此<b>不依賴</b>指令模組（DR 是硬相依 <c>ContentFinderCommand</c> 才發得出 <c>/pdrduty</c>）。
/// </para>
/// <para>
/// ⚠️ 與既有的 <see cref="OptimizedDutyFinderSetting"/> 同一個 addon，但兩者不重疊：
/// 那個是把「任務設定」開關攤在搜尋器<b>上方</b>；這個是在<b>左側</b>放常用副本的一鍵登記，
/// 兩排各管各的、位置也錯開。
/// </para>
/// </remarks>
public sealed unsafe class FastContentsFinderRegister : TcModule
{
    public override string InternalName => "FastContentsFinderRegister";
    public override string DisplayName => "任務搜尋器快速登記";

    public override string Description =>
        "在任務搜尋器旁顯示一個小面板：一鍵把搜尋器開到目前選取的副本，或你收藏的常用副本。" +
        "只會開啟並選取，不會替你按「參加」、不會自動排隊。";

    public override ModuleCategory Category => ModuleCategory.Combat;

    /// <inheritdoc/>
    /// <remarks>面板只是顯示；不去點它，遊戲行為完全不變。</remarks>
    public override bool IsManualTrigger => true;

    public override bool HasConfigUI => true;

    /// <summary>要疊面板的 addon（一般任務／討伐殲滅戰搜尋器），與 <see cref="OptimizedDutyFinderSetting"/> 相同。</summary>
    private static readonly string[] TargetAddons = ["ContentsFinder", "RaidFinder"];

    private FastContentsFinderRegisterConfig Config => Plugin.Instance.Config.FastContentsFinderRegister;

    protected override void OnEnable()
    {
        Svc.PluginInterface.UiBuilder.Draw += DrawOverlay;
    }

    protected override void OnDisable()
    {
        Svc.PluginInterface.UiBuilder.Draw -= DrawOverlay;
    }

    /// <summary>一張常用副本卡片解析出來的當幀資訊。</summary>
    private readonly record struct DutyRef(byte ContentType, uint Id, string Name)
    {
        public bool IsRoulette => ContentType == (byte)ContentsId.ContentsType.Roulette;
        public bool IsValid => ContentType != (byte)ContentsId.ContentsType.None && Id != 0;
    }

    private void DrawOverlay()
    {
        try
        {
            DrawOverlayCore();
        }
        catch (Exception ex)
        {
            // Draw 路徑擲例外會讓 Dalamud 把整個 UiBuilder.Draw 設 null，一定要在這裡兜住。
            if (Throttle.Pass($"{InternalName}-DrawError", 10_000))
                Svc.Log.Error(ex, $"[{InternalName}] 繪製快速登記面板時發生例外");
        }
    }

    private void DrawOverlayCore()
    {
        if (!Config.ShowOverlay) return;

        FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase* addon = null;
        foreach (var name in TargetAddons)
        {
            var candidate = UiHelper.GetAddon(name);
            if (!UiHelper.IsReady(candidate)) continue;
            addon = candidate;
            break;
        }

        if (addon == null) return;

        var agent = AgentContentsFinder.Instance();
        if (agent == null) return;

        var selection = ReadSelection(agent);
        var favorites = Config.Favorites;

        // 沒有選取、也沒有收藏＝面板沒有任何可操作的東西，就不要在搜尋器旁邊擺一塊空白。
        if (!selection.IsValid && favorites.Count == 0) return;

        const ImGuiWindowFlags flags = ImGuiWindowFlags.AlwaysAutoResize |
                                       ImGuiWindowFlags.NoTitleBar |
                                       ImGuiWindowFlags.NoCollapse |
                                       ImGuiWindowFlags.NoScrollbar |
                                       ImGuiWindowFlags.NoSavedSettings |
                                       ImGuiWindowFlags.NoFocusOnAppearing;

        if (ImGui.Begin("###TCToolboxFastContentsFinderRegister", flags))
        {
            // 放到搜尋器左側；算不出寬度時退到左緣，不要蓋在搜尋器上。
            var width = ImGui.GetWindowSize().X;
            var x = Math.Max(0f, addon->GetX() - width - 4f);
            ImGui.SetWindowPos(new Vector2(x, addon->GetY()));

            ImGui.TextDisabled("快速登記");
            ImGui.Separator();

            DrawSelectionRow(agent, selection);
            DrawFavorites(agent);
        }

        ImGui.End();
    }

    private void DrawSelectionRow(AgentContentsFinder* agent, DutyRef selection)
    {
        if (!selection.IsValid)
        {
            ImGui.TextDisabled("在列表選一個副本，即可加入常用。");
            ImGui.Spacing();
            return;
        }

        ImGui.TextUnformatted("目前選取：");
        ImGui.SameLine();
        // 「不知道」要看得見：名稱解不出來時畫灰字「?」而不是留白。
        if (selection.Name.Length > 0)
            ImGui.TextUnformatted(selection.Name);
        else
            ImGui.TextDisabled("?（名稱解析失敗）");

        using (ImRaii.Disabled(IsFavorite(selection)))
        {
            if (ImGui.SmallButton("加入常用##fcfr-add"))
                AddFavorite(selection);
        }

        if (IsFavorite(selection) && ImGui.IsItemHovered())
            ImGui.SetTooltip("這個副本已經在常用清單裡了。");

        ImGui.Spacing();
    }

    private void DrawFavorites(AgentContentsFinder* agent)
    {
        var favorites = Config.Favorites;
        if (favorites.Count == 0) return;

        ImGui.Separator();
        ImGui.TextDisabled("常用（點名稱開啟並選取）");

        FavoriteDuty? toRemove = null;
        for (var i = 0; i < favorites.Count; i++)
        {
            var fav = favorites[i];
            var duty = ResolveFavorite(fav);

            using var id = ImRaii.PushId(i);

            // 移除鈕。
            if (ImGui.SmallButton("✕"))
                toRemove = fav;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("從常用清單移除");

            ImGui.SameLine();

            if (!duty.IsValid)
            {
                // 收藏時的列號在這台機器上查不到（改版／資料不同）——畫灰字，別假裝能開。
                ImGui.TextDisabled($"{(string.IsNullOrEmpty(fav.Name) ? "?" : fav.Name)}（已失效）");
                continue;
            }

            var label = duty.Name.Length > 0 ? duty.Name : fav.Name;
            if (label.Length == 0) label = duty.IsRoulette ? $"隨機任務 #{duty.Id}" : $"副本 #{duty.Id}";

            if (ImGui.Button($"{label}##fcfr-open"))
                Open(agent, duty);

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(duty.IsRoulette
                                     ? $"開啟並選取隨機任務（ContentRoulette {duty.Id}）"
                                     : $"開啟並選取副本（ContentFinderCondition {duty.Id}）");
        }

        if (toRemove is { } remove)
            RemoveFavorite(remove);
    }

    private void Open(AgentContentsFinder* agent, DutyRef duty)
    {
        if (!duty.IsValid) return;

        if (duty.IsRoulette)
            agent->OpenRouletteDuty((byte)duty.Id);
        else
            agent->OpenRegularDuty(duty.Id);

        Svc.Log.Information(
            $"[{InternalName}] 快速登記 → {(duty.IsRoulette ? "隨機任務" : "副本")} {duty.Id}「{duty.Name}」");

        if (Config.NotifyOnOpen)
            Svc.Chat.Print($"[TC Toolbox] 已選取「{(duty.Name.Length > 0 ? duty.Name : duty.Id.ToString())}」，請自行按「參加」。");
    }

    /// <summary>讀「目前選取的那一項」。只讀結構欄位，不保存指標。</summary>
    private static DutyRef ReadSelection(AgentContentsFinder* agent)
    {
        var selected = agent->SelectedDuty;
        var type = (byte)selected.ContentType;
        var id = selected.Id;

        if (type == (byte)ContentsId.ContentsType.None || id == 0)
            return default;

        return new DutyRef(type, id, ResolveName(type, id));
    }

    private static DutyRef ResolveFavorite(FavoriteDuty fav) =>
        new(fav.ContentType, fav.Id, ResolveName(fav.ContentType, fav.Id));

    /// <summary>由（類型，列號）查副本／輪盤名；查不到回空字串（呼叫端負責畫成看得見的「不知道」）。</summary>
    private static string ResolveName(byte contentType, uint id)
    {
        try
        {
            if (contentType == (byte)ContentsId.ContentsType.Roulette)
            {
                var roulette = Svc.Data.GetExcelSheet<ContentRoulette>().GetRowOrDefault(id);
                return roulette?.Name.ExtractText() ?? string.Empty;
            }

            if (contentType == (byte)ContentsId.ContentsType.Regular)
            {
                var cfc = Svc.Data.GetExcelSheet<ContentFinderCondition>().GetRowOrDefault(id);
                return cfc?.Name.ExtractText() ?? string.Empty;
            }
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, $"[FastContentsFinderRegister] 解析副本名失敗（type={contentType} id={id}）");
        }

        return string.Empty;
    }

    private bool IsFavorite(DutyRef duty) =>
        Config.Favorites.Exists(f => f.ContentType == duty.ContentType && f.Id == duty.Id);

    private void AddFavorite(DutyRef duty)
    {
        if (!duty.IsValid || IsFavorite(duty)) return;

        Config.Favorites.Add(new FavoriteDuty
        {
            ContentType = duty.ContentType,
            Id = duty.Id,
            Name = duty.Name,
        });
        Plugin.Instance.Config.Save();

        Svc.Log.Information($"[{InternalName}] 加入常用：{(duty.IsRoulette ? "隨機任務" : "副本")} {duty.Id}「{duty.Name}」");
    }

    private void RemoveFavorite(FavoriteDuty fav)
    {
        Config.Favorites.RemoveAll(f => f.ContentType == fav.ContentType && f.Id == fav.Id);
        Plugin.Instance.Config.Save();
    }

    public override void DrawConfig()
    {
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
        ImGui.TextDisabled(
            "開著任務搜尋器時，左側會出現一個小面板：\n" +
            "· 在列表選一個副本後，按「加入常用」把它記起來。\n" +
            "· 常用清單裡的副本，點一下就把搜尋器開到那一項並選取。\n" +
            "只會開啟並選取，不會替你按「參加」，也不會自動排隊。");
        ImGui.PopTextWrapPos();

        ImGui.Spacing();

        var show = Config.ShowOverlay;
        if (ImGui.Checkbox("在任務搜尋器旁顯示快速登記面板", ref show))
        {
            Config.ShowOverlay = show;
            Plugin.Instance.Config.Save();
        }

        var notify = Config.NotifyOnOpen;
        if (ImGui.Checkbox("開啟後在聊天欄提示", ref notify))
        {
            Config.NotifyOnOpen = notify;
            Plugin.Instance.Config.Save();
        }

        ImGui.Separator();

        var favorites = Config.Favorites;
        if (favorites.Count == 0)
        {
            ImGui.TextDisabled("目前沒有收藏的常用副本。");
            return;
        }

        ImGui.TextDisabled($"常用副本（{favorites.Count}）：");
        FavoriteDuty? toRemove = null;
        for (var i = 0; i < favorites.Count; i++)
        {
            var fav = favorites[i];
            using var id = ImRaii.PushId(i);

            if (ImGui.SmallButton("移除"))
                toRemove = fav;

            ImGui.SameLine();

            var name = ResolveName(fav.ContentType, fav.Id);
            if (name.Length == 0) name = string.IsNullOrEmpty(fav.Name) ? $"#{fav.Id}" : $"{fav.Name}（已失效）";

            var isRoulette = fav.ContentType == (byte)ContentsId.ContentsType.Roulette;
            ImGui.TextUnformatted($"{(isRoulette ? "[隨機] " : string.Empty)}{name}");
        }

        if (toRemove is { } remove)
            RemoveFavorite(remove);
    }
}

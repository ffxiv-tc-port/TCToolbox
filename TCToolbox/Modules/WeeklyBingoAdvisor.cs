using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using TCToolbox.Core;
using TCToolbox.Windows;

namespace TCToolbox.Modules;

/// <summary>天書某一格的換格建議。</summary>
/// <param name="Slot">格子編號（0 起算）。</param>
/// <param name="Description">天書項目敘述。</param>
/// <param name="Target">這一格對應的副本／輪盤（可能對不到）。</param>
/// <param name="IsFreeSticker">副本已完成、印花還沒貼上——貼這一格不用再跑一趟。</param>
/// <param name="After">換完之後的 1／2／3 條線機率（<b>0~1 的比例</b>）。</param>
/// <param name="Delta">相對現況的變化量（同樣是比例，可能為負）。</param>
public readonly record struct BingoSuggestion(
    int Slot,
    string Description,
    BingoTarget Target,
    bool IsFreeSticker,
    double[] After,
    double[] Delta);

/// <summary>
/// 天書（<c>WeeklyBingo</c>）的換格建議：算「胡思亂想」花下去值不值得。
/// </summary>
/// <remarks>
/// 🔴 <b>預設純顯示。</b>絕對不會自己發動「再想想」／「重新貼」；「開啟」只把任務搜尋器的面板叫出來。
/// 唯一的例外是使用者自己打開的「用 AutoDuty 跑這格」（預設關，見 WeeklyBingoAdvisor.AutoDuty.cs）：按一次才跑一輪。
/// 🔴 機率全部由 <see cref="WondrousTailsSolverIpc"/> 向 WondrousTailsSolver 查，本外掛不自己算。
/// 對方不在就整個面板顯示「需要 WondrousTailsSolver」，不是顯示 0。
/// 📌 IPC 只在 <c>Framework.Update</c> 裡打：機率只在盤面變動時問，AutoDuty 只在那個選項開著時每秒問一次；
/// ImGui 的 Draw 路徑一律只讀算好的欄位。
/// </remarks>
public sealed unsafe partial class WeeklyBingoAdvisor : TcModule
{
    /// <summary>存檔用識別名。<see cref="Configuration"/> 的一次性預設啟用要引用它，所以是常數。</summary>
    public const string ModuleInternalName = "WeeklyBingoAdvisor";

    public override string InternalName => ModuleInternalName;

    public override string DisplayName => "天書奇談換格建議";

    public override string Description =>
        "打開天書（探險者筆記）時，算出「下一格貼哪裡最划算」，以及花奇想點用「再想想」換掉某一格、"
        + "或花 2 點「重新貼」全部重來，各自會讓連線機率變成多少。只顯示數字與開啟任務搜尋器，"
        + "不會替你發動胡思亂想；預設也不會替你報名任何副本。需要另外安裝 WondrousTailsSolver 才能算。"
        + "另有預設關的「用 AutoDuty 跑這格」：打開後，有劇情輔助器的副本多一顆「跑一輪」按鈕，"
        + "按一下就請 AutoDuty 用 NPC 隊友跑那個副本一輪（需要另外安裝 AutoDuty）。";

    public override ModuleCategory Category => ModuleCategory.Combat;

    public override bool HasConfigUI => true;

    private const string AddonName = "WeeklyBingo";

    /// <summary>天書固定 16 格。</summary>
    private const int SlotCount = 16;

    /// <summary>「再想想」1 點、「重新貼」2 點（Addon 5627／5629）。</summary>
    private const int RetryCost = 1;

    private const int ShuffleCost = 2;

    /// <summary>「重新貼」只在已貼 3～7 個印花時可用（Addon 5630 的條件文字）。</summary>
    private const int ShuffleMinStickers = 3;

    private const int ShuffleMaxStickers = 7;

    private WeeklyBingoAdvisorWindow? window;

    // ── 以下欄位只在遊戲主執行緒讀寫（Framework.Update 與 ImGui 的 Draw 都是主執行緒）──

    /// <summary>上一次算過的盤面；<c>-1</c>＝還沒算過／盤面讀不出來。</summary>
    private int cachedMask = -1;

    private bool cachedSolverOk;

    public bool SolverAvailable { get; private set; }

    public int SolverVersion { get; private set; }

    /// <summary>盤面讀得出來嗎。<see langword="false"/> 時 <see cref="BoardProblem"/> 說明原因。</summary>
    public bool BoardReadable { get; private set; }

    public string BoardProblem { get; private set; } = string.Empty;

    public int PlacedCount { get; private set; }

    public uint SecondChancePoints { get; private set; }

    /// <summary>現況的 1／2／3 條線機率；空陣列＝還沒算出來。</summary>
    public double[] Current { get; private set; } = [];

    /// <summary>下一個印花貼哪一格（空格）。</summary>
    public List<BingoSuggestion> NextCell { get; } = [];

    /// <summary>「再想想」拿掉哪一格（已貼的格子）。</summary>
    public List<BingoSuggestion> Retry { get; } = [];

    /// <summary>「重新貼」之後的期望機率；空陣列＝還沒算出來。</summary>
    public double[] Shuffle { get; private set; } = [];

    public bool CanAffordRetry => SecondChancePoints >= RetryCost;

    public bool CanAffordShuffle => SecondChancePoints >= ShuffleCost;

    public bool ShuffleInRange => PlacedCount is >= ShuffleMinStickers and <= ShuffleMaxStickers;

    private WeeklyBingoAdvisorConfig Config => Plugin.Instance.Config.WeeklyBingoAdvisor;

    protected override void OnEnable()
    {
        window = new WeeklyBingoAdvisorWindow(this);
        Plugin.Instance.WindowSystem.AddWindow(window);

        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, AddonName, OnAddonSetup);
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, AddonName, OnAddonFinalize);
        Svc.Framework.Update += OnUpdate;

        if (Config.FollowAddon && UiHelper.IsAddonReady(AddonName))
            window.IsOpen = true;
    }

    protected override void OnDisable()
    {
        Svc.Framework.Update -= OnUpdate;
        Svc.AddonLifecycle.UnregisterListener(OnAddonSetup);
        Svc.AddonLifecycle.UnregisterListener(OnAddonFinalize);

        if (window != null)
        {
            // 🔴 RemoveWindow 對「不在清單裡」的視窗會擲 ArgumentException，而外掛整個卸載時
            //    Plugin.Dispose 是先 RemoveAllWindows() 再 module.Disable()。
            if (Plugin.Instance.WindowSystem.Windows.Contains(window))
                Plugin.Instance.WindowSystem.RemoveWindow(window);

            window.IsOpen = false;
            window = null;
        }

        ResetState();
    }

    /// <summary>從設定畫面手動把面板叫出來。</summary>
    public void OpenPanel()
    {
        if (window == null) return;

        // 讓下一次 Update 重算，免得面板開起來是一片舊資料。
        cachedMask = -1;
        window.IsOpen = true;
    }

    private void OnAddonSetup(AddonEvent type, AddonArgs args)
    {
        if (!Config.FollowAddon || window == null) return;

        cachedMask = -1;
        window.IsOpen = true;
    }

    private void OnAddonFinalize(AddonEvent type, AddonArgs args)
    {
        if (Config.FollowAddon && window != null) window.IsOpen = false;
    }

    private void OnUpdate(IFramework framework)
    {
        // 「跑一輪」按下去的那一幀只記下目標，IPC 在這裡打（面板剛好被關掉也照樣處理掉，不留到下次）。
        RunPendingAutoDuty();

        // 面板沒開就不再做別的事。
        if (window is not { IsOpen: true }) return;

        try
        {
            Refresh();
            PollAutoDuty();
        }
        catch (Exception ex)
        {
            if (Throttle.Pass($"{InternalName}-Refresh", 60_000))
                Svc.Log.Information($"[{InternalName}] 更新盤面時發生例外（60 秒內只報一次）：{ex}");
        }
    }

    private void Refresh()
    {
        SolverAvailable = WondrousTailsSolverIpc.TryGetVersion(out var version)
                          && version >= WondrousTailsSolverIpc.SupportedVersion;
        SolverVersion = version;

        if (!TryReadBoard(out var mask))
        {
            cachedMask = -1;
            ClearResults();
            return;
        }

        // 奇想點只影響「買不買得起」的灰階，不影響機率，所以它變動不必重算。
        if (mask == cachedMask && SolverAvailable == cachedSolverOk) return;

        cachedMask = mask;
        cachedSolverOk = SolverAvailable;
        ClearResults();

        if (SolverAvailable) Compute(mask);
    }

    /// <summary>
    /// 讀出目前盤面。讀不出來時把原因寫進 <see cref="BoardProblem"/> 並回 <see langword="false"/>。
    /// </summary>
    /// <remarks>
    /// 🔑 <b>自我校準</b>：印花數有兩個獨立來源——逐格呼叫 <c>IsWeeklyBingoStickerPlaced</c>，
    /// 以及遊戲自己對原生位元欄位做的 PopCount。兩者對不上就代表格子索引的基準和這一版不同
    /// （CS 的散文註解寫「index starting at 1」，而實際在用的外掛是 0 起算），
    /// 這時<b>寧可說「不知道」也不要顯示錯的機率</b>。
    /// </remarks>
    private bool TryReadBoard(out int mask)
    {
        mask = 0;

        var playerState = PlayerState.Instance();
        if (playerState == null)
        {
            SetBoardProblem("還沒進遊戲。");
            return false;
        }

        if (!playerState->HasWeeklyBingoJournal)
        {
            SetBoardProblem("目前沒有天書（探險者筆記）。");
            return false;
        }

        // 🔴 對不上的遊戲函式要判指標是不是 null：直接呼叫會是 try/catch 攔不到的 AccessViolation。
        if (PlayerState.MemberFunctionPointers.IsWeeklyBingoStickerPlaced == null)
        {
            SetBoardProblem("讀不到印花狀態（遊戲函式在這一版對不上）。");
            return false;
        }

        for (var slot = 0; slot < SlotCount; slot++)
        {
            if (playerState->IsWeeklyBingoStickerPlaced(slot)) mask |= 1 << slot;
        }

        var counted = BitOperations.PopCount((uint)mask);
        PlacedCount = playerState->WeeklyBingoNumPlacedStickers;
        SecondChancePoints = playerState->WeeklyBingoNumSecondChancePoints;

        if (counted != PlacedCount)
        {
            SetBoardProblem($"印花數對不上（逐格讀到 {counted} 個，遊戲自己說 {PlacedCount} 個），不顯示機率。");

            if (Throttle.Pass($"{InternalName}-MaskMismatch", 600_000))
                Svc.Log.Information(
                    $"[{InternalName}] 印花數對不上：mask=0x{mask:X4} PopCount={counted} "
                    + $"WeeklyBingoNumPlacedStickers={PlacedCount}（格子索引基準可能已改變）。");

            return false;
        }

        BoardReadable = true;
        BoardProblem = string.Empty;
        return true;
    }

    private void SetBoardProblem(string reason)
    {
        BoardReadable = false;
        BoardProblem = reason;
    }

    private void Compute(int mask)
    {
        if (!WondrousTailsSolverIpc.TryGetLineChances(mask, out var current) || current[0] < 0) return;

        Current = current;

        var playerState = PlayerState.Instance();
        if (playerState == null) return;

        var empty = new List<int>();
        for (var slot = 0; slot < SlotCount; slot++)
        {
            if ((mask & (1 << slot)) == 0) empty.Add(slot);
        }

        foreach (var slot in empty)
        {
            if (!WondrousTailsSolverIpc.TryGetLineChances(mask | (1 << slot), out var after) || after[0] < 0)
                continue;

            NextCell.Add(MakeSuggestion(playerState, slot, after, current));
        }

        // 「再想想」＝指定一格已完成的變回未完成，同時隨機有一個未完成的變成已完成（Addon 5628）。
        // 落點的分布遊戲沒有公布，這裡假設在原本的空格裡等機率，所以取對所有空格的平均。
        for (var slot = 0; slot < SlotCount && empty.Count > 0; slot++)
        {
            if ((mask & (1 << slot)) == 0) continue;
            if (!TryMeanAfterRetry(mask, slot, empty, out var mean)) continue;

            Retry.Add(MakeSuggestion(playerState, slot, mean, current));
        }

        // 「重新貼」＝全部印花隨機重貼。對所有 C(16,k) 盤面取平均恆等於空盤的機率，
        // 所以直接問空盤就好，不必打一萬多次 IPC。
        if (WondrousTailsSolverIpc.TryGetLineChances(0, out var shuffle) && shuffle[0] >= 0)
            Shuffle = shuffle;

        SortSuggestions();
    }

    /// <summary>「再想想」拿掉 <paramref name="removed"/> 這一格之後，對所有落點取平均。</summary>
    private static bool TryMeanAfterRetry(int mask, int removed, List<int> empty, out double[] mean)
    {
        mean = [];
        var sum = new double[WondrousTailsSolverIpc.ChanceCount];

        foreach (var dest in empty)
        {
            var moved = (mask & ~(1 << removed)) | (1 << dest);

            // 🔴 -1＝對方算不出來。混進平均會得到一個看起來很合理的假數字，所以整格放棄。
            if (!WondrousTailsSolverIpc.TryGetLineChances(moved, out var after) || after[0] < 0) return false;

            for (var i = 0; i < sum.Length; i++) sum[i] += after[i];
        }

        for (var i = 0; i < sum.Length; i++) sum[i] /= empty.Count;

        mean = sum;
        return true;
    }

    private static BingoSuggestion MakeSuggestion(PlayerState* playerState, int slot, double[] after, double[] current)
    {
        var orderRowId = (uint)playerState->WeeklyBingoOrderData[slot];
        var description = orderRowId == 0 ? string.Empty : WeeklyBingoDutyResolver.GetDisplayName(orderRowId, slot);
        var target = orderRowId == 0
            ? BingoTarget.Fail("這一格沒有項目")
            : WeeklyBingoDutyResolver.Resolve(orderRowId);

        var free = playerState->GetWeeklyBingoTaskStatus(slot) == PlayerState.WeeklyBingoTaskStatus.Claimable;

        var delta = new double[after.Length];
        for (var i = 0; i < delta.Length; i++) delta[i] = after[i] - current[i];

        return new BingoSuggestion(slot, description, target, free, after, delta);
    }

    /// <summary>依目前選的排序依據重排。設定改變時要重叫，否則順序會停在舊的依據上。</summary>
    public void SortSuggestions()
    {
        var metric = Math.Clamp(Config.RankMetric, 0, WondrousTailsSolverIpc.ChanceCount - 1);

        NextCell.Sort((a, b) => b.Delta[metric].CompareTo(a.Delta[metric]));
        Retry.Sort((a, b) => b.Delta[metric].CompareTo(a.Delta[metric]));
    }

    private void ClearResults()
    {
        Current = [];
        Shuffle = [];
        NextCell.Clear();
        Retry.Clear();
    }

    private void ResetState()
    {
        cachedMask = -1;
        cachedSolverOk = false;
        SolverAvailable = false;
        BoardReadable = false;
        BoardProblem = string.Empty;
        PlacedCount = 0;
        SecondChancePoints = 0;
        ClearResults();
        ResetAutoDuty();
    }

    /// <summary>把任務搜尋器的面板開到這一格對應的項目。<b>只開面板，不報名。</b></summary>
    public void OpenDutyFinder(BingoTarget target)
    {
        if (!target.IsResolved) return;

        var agent = AgentContentsFinder.Instance();
        if (agent == null) return;

        if (target.Kind == BingoTargetKind.Roulette)
            agent->OpenRouletteDuty((byte)target.Id);
        else
            agent->OpenRegularDuty(target.Id);

        Svc.Log.Information($"[{InternalName}] 開啟任務搜尋器到「{target.Name}」（只開面板，沒有報名）。");
    }

    public override void DrawConfig()
    {
        var follow = Config.FollowAddon;
        if (ImGui.Checkbox("打開天書時自動開啟建議面板（關掉天書時一起收起來）", ref follow))
        {
            Config.FollowAddon = follow;
            Plugin.Instance.Config.Save();
        }

        var showFree = Config.HighlightFreeStickers;
        if (ImGui.Checkbox("標出「副本已完成、印花還沒貼」的格子", ref showFree))
        {
            Config.HighlightFreeStickers = showFree;
            Plugin.Instance.Config.Save();
        }

        using (ImRaii.PushIndent())
            ImGui.TextDisabled("那種格子貼上去不用再跑一趟，通常是第一順位。");

        var top = Config.TopCount;
        if (ImGui.SliderInt("每張表最多列幾個建議（拉到 0＝全部列出）", ref top, 0, SlotCount))
        {
            Config.TopCount = top;
            Plugin.Instance.Config.Save();
        }

        DrawMetricCombo();

        ImGui.Separator();
        DrawAutoDutyConfig();

        ImGui.Separator();

        if (ImGui.Button("開啟建議面板"))
            OpenPanel();

        ImGui.SameLine();
        ImGui.TextDisabled(IsEnabled ? string.Empty : "（模組還沒啟用，面板不會更新）");
    }

    private void DrawMetricCombo()
    {
        var metric = Math.Clamp(Config.RankMetric, 0, WondrousTailsSolverIpc.ChanceCount - 1);

        using var combo = ImRaii.Combo("建議的排序依據", MetricName(metric));
        if (!combo) return;

        for (var i = 0; i < WondrousTailsSolverIpc.ChanceCount; i++)
        {
            if (!ImGui.Selectable(MetricName(i), i == metric)) continue;

            Config.RankMetric = i;
            Plugin.Instance.Config.Save();
            SortSuggestions();
        }
    }

    private static string MetricName(int metric) => metric switch
    {
        0 => "貼出 1 條故事線的機率",
        1 => "貼出 2 條故事線的機率",
        2 => "貼出 3 條故事線的機率",
        _ => "（不明）",
    };

    /// <summary>面板本體。只讀 <c>Framework.Update</c> 算好的欄位，不做 IPC、不碰原生記憶體。</summary>
    public void DrawPanel()
    {
        if (!SolverAvailable)
        {
            ImGui.TextColored(new Vector4(1f, 0.8f, 0.35f, 1f), "需要 WondrousTailsSolver");
            ImGui.TextDisabled(SolverVersion > 0
                ? $"已裝的 WondrousTailsSolver 版本是 {SolverVersion}，這個面板需要 {WondrousTailsSolverIpc.SupportedVersion} 以上。"
                : "連線機率是向 WondrousTailsSolver 查的。裝好並啟用它之後，這裡就會出現數字。");
            return;
        }

        if (!BoardReadable)
        {
            ImGui.TextDisabled(BoardProblem.Length > 0 ? BoardProblem : "讀不到天書盤面。");
            return;
        }

        DrawSummary();
        ImGui.Separator();

        DrawSuggestionTable(
            "下一個印花貼哪一格",
            "##wt-next",
            NextCell,
            "把印花貼在這一格之後的機率。灰字「—」＝對不到可以直接開的副本。",
            enabled: true,
            blockedReason: string.Empty,
            withAutoDuty: Config.RunWithAutoDuty);

        ImGui.Spacing();

        DrawSuggestionTable(
            $"再想想（消耗 {RetryCost} 奇想點）",
            "##wt-retry",
            Retry,
            "拿掉這一格的印花，遊戲會隨機補一個到其他還沒完成的格子上；這裡顯示的是對所有落點取平均的結果。",
            CanAffordRetry,
            $"奇想點不夠（目前 {SecondChancePoints} 點，需要 {RetryCost} 點）。");

        ImGui.Spacing();
        DrawShuffle();

        ImGui.Spacing();
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
        ImGui.TextDisabled(Config.RunWithAutoDuty
            ? "這個面板只算給你看。「再想想」「重新貼」都要自己在遊戲的胡思亂想視窗裡發動；"
              + "「開啟」只把任務搜尋器開起來，「跑一輪」才會請 AutoDuty 用劇情輔助器（NPC 隊友）跑那個副本一輪。"
            : "這個面板只算給你看。「再想想」「重新貼」都要自己在遊戲的胡思亂想視窗裡發動，"
              + "按鈕也只是把任務搜尋器開起來，不會替你報名。");
        ImGui.PopTextWrapPos();
    }

    private void DrawSummary()
    {
        ImGui.Text($"已貼 {PlacedCount} / 9 個印花，奇想點 {SecondChancePoints} 點。");

        if (Current.Length != WondrousTailsSolverIpc.ChanceCount)
        {
            ImGui.TextDisabled("目前的連線機率：？（還沒算出來）");
            return;
        }

        ImGui.Text("目前的連線機率："
                   + $"1 條 {FormatChance(Current[0])}　"
                   + $"2 條 {FormatChance(Current[1])}　"
                   + $"3 條 {FormatChance(Current[2])}");
    }

    private void DrawShuffle()
    {
        var blocked = !CanAffordShuffle || !ShuffleInRange;

        ImGui.Text($"重新貼（消耗 {ShuffleCost} 奇想點）");

        if (Shuffle.Length != WondrousTailsSolverIpc.ChanceCount || Current.Length != WondrousTailsSolverIpc.ChanceCount)
        {
            ImGui.TextDisabled("？（還沒算出來）");
            return;
        }

        using (ImRaii.Disabled(blocked))
        {
            using (ImRaii.PushIndent())
            {
                ImGui.Text($"全部重貼之後：1 條 {FormatChance(Shuffle[0])}（{FormatDelta(Shuffle[0] - Current[0])}）　"
                           + $"2 條 {FormatChance(Shuffle[1])}（{FormatDelta(Shuffle[1] - Current[1])}）　"
                           + $"3 條 {FormatChance(Shuffle[2])}（{FormatDelta(Shuffle[2] - Current[2])}）");
            }
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("印花會全部取下來隨機重貼，所以結果與現在貼在哪裡無關，"
                             + "等於回到「隨機 9 格」的期望值。");

        if (!blocked) return;

        using (ImRaii.PushIndent())
        {
            ImGui.TextColored(
                new Vector4(1f, 0.8f, 0.35f, 1f),
                !ShuffleInRange
                    ? $"現在不能用：只有已貼上 {ShuffleMinStickers}～{ShuffleMaxStickers} 個印花時可以重新貼。"
                    : $"奇想點不夠（目前 {SecondChancePoints} 點，需要 {ShuffleCost} 點）。");
        }
    }

    private void DrawSuggestionTable(
        string title,
        string id,
        List<BingoSuggestion> rows,
        string hint,
        bool enabled,
        string blockedReason,
        bool withAutoDuty = false)
    {
        ImGui.Text(title);

        if (!enabled)
        {
            using (ImRaii.PushIndent())
                ImGui.TextColored(new Vector4(1f, 0.8f, 0.35f, 1f), blockedReason);
        }

        if (rows.Count == 0)
        {
            using (ImRaii.PushIndent())
                ImGui.TextDisabled("沒有可以建議的格子。");
            return;
        }

        using (ImRaii.PushIndent())
            ImGui.TextDisabled(hint);

        if (withAutoDuty) DrawAutoDutyStatus();

        // 多一欄時換一個表格 id：欄數不同的表不共用 ImGui 記住的欄寬。
        using var disabled = ImRaii.Disabled(!enabled);
        using var table = ImRaii.Table(withAutoDuty ? id + "-ad" : id, withAutoDuty ? 6 : 5,
            ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.SizingStretchProp);
        if (!table) return;

        ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed, 28f);
        ImGui.TableSetupColumn("天書項目", ImGuiTableColumnFlags.WidthStretch, 1.3f);
        ImGui.TableSetupColumn("會開啟", ImGuiTableColumnFlags.WidthStretch, 1.2f);
        ImGui.TableSetupColumn("1／2／3 條線", ImGuiTableColumnFlags.WidthStretch, 1.4f);
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 56f);
        if (withAutoDuty)
            ImGui.TableSetupColumn("AutoDuty", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableHeadersRow();

        var limit = Config.TopCount <= 0 ? rows.Count : Math.Min(Config.TopCount, rows.Count);
        for (var i = 0; i < limit; i++)
            DrawSuggestionRow(rows[i], id, withAutoDuty);
    }

    private void DrawSuggestionRow(BingoSuggestion row, string id, bool withAutoDuty)
    {
        ImGui.TableNextRow();

        ImGui.TableNextColumn();
        ImGui.Text((row.Slot + 1).ToString());

        ImGui.TableNextColumn();
        var description = row.Description.Length > 0 ? row.Description : "（無敘述）";
        if (Config.HighlightFreeStickers && row.IsFreeSticker)
        {
            ImGui.TextColored(new Vector4(0.45f, 0.9f, 0.45f, 1f), description);
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                SetTooltipOpaque("這一格的副本已經完成了，印花還沒貼上——貼它不用再跑一趟。");
        }
        else
        {
            ImGui.Text(description);
        }

        ImGui.TableNextColumn();
        if (row.Target.IsResolved)
        {
            ImGui.Text(row.Target.Name);
        }
        else
        {
            // ⚠️「不知道」本身要在列上看得見，不能留白。
            ImGui.TextDisabled("—");
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                SetTooltipOpaque(row.Target.Reason);
        }

        ImGui.TableNextColumn();
        ImGui.Text($"{FormatChance(row.After[0])}　{FormatChance(row.After[1])}　{FormatChance(row.After[2])}");
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            SetTooltipOpaque(
                $"相對現況：1 條 {FormatDelta(row.Delta[0])}、"
                + $"2 條 {FormatDelta(row.Delta[1])}、3 條 {FormatDelta(row.Delta[2])}");

        ImGui.TableNextColumn();
        using (ImRaii.Disabled(!row.Target.IsResolved))
        {
            if (ImGui.SmallButton($"開啟{id}-{row.Slot}"))
                OpenDutyFinder(row.Target);
        }

        if (!withAutoDuty) return;

        ImGui.TableNextColumn();
        DrawAutoDutyCell(row, id);
    }

    /// <summary>
    /// 表格列裡的說明框。「再想想」表整張可能在停用區段裡，而 ImGui 1.88 停用時把 <c>Style.Alpha</c>
    /// 乘上 DisabledAlpha，說明框會跟著變淡；這裡把 Alpha 推回 1 再畫。
    /// </summary>
    private static void SetTooltipOpaque(string text)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.Alpha, 1f);
        try
        {
            ImGui.SetTooltip(text);
        }
        finally
        {
            ImGui.PopStyleVar();
        }
    }

    /// <summary>機率 → 顯示字串。🔴 負數＝對方算不出來，畫成「？」不是 0%。</summary>
    private static string FormatChance(double value) => value < 0 ? "？" : $"{value * 100:F2}%";

    private static string FormatDelta(double value) =>
        $"{(value >= 0 ? "+" : string.Empty)}{value * 100:F2}%";
}

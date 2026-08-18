using System;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Fate;
using Lumina.Excel.Sheets;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// F.A.T.E. 自動等級同步：進入等級比自己低的 F.A.T.E. 時自動開啟等級同步。
/// </summary>
/// <remarks>
/// <para>
/// 🔴🔴 <b>絕對不送空參數的等級同步指令。</b>台服 <c>TextCommand</c> 第 270 列的說明逐字寫著
/// 「<b>無指令</b>：在開啟或解除狀態中切換」——也就是空參數是 <b>toggle</b>。
/// 已經同步的時候送空參數，結果是<b>反過來把同步解除掉</b>，而那正好是這個模組要做的事情的相反。
/// 上游 PandorasBox <c>AutoSyncFate</c> 送的就是空參數。
/// 本模組一律送 <c>on</c>，而且在送出之前先確認「現在確實沒有同步」。
/// </para>
/// <para>
/// 🔑 <b>「現在有沒有同步」是讀出來的，不是猜的</b>：
/// <c>FateManager.SyncedFateId</c>（偏移 0xA8）等於目前 F.A.T.E. 的編號就是已同步。
/// 這個偏移是離線從台服 7.20 主程式的 <c>FateManager::IsSyncedToFate</c> 反組譯確認的
/// （<c>movzx eax, word [rdx+0x18]</c> 取 <c>FateContext.FateId</c>、
/// <c>cmp word [rcx+0xA8], ax</c> 比對）。
/// ⚠️ 有了這道確認，就算指令的參數不被接受而必須退回用 toggle，也是安全的——
/// 因為我們剛剛才確認過「現在是關的」。
/// </para>
/// <para>
/// 📌 指令字串<b>從 <c>TextCommand</c> 表讀</b>，不寫死。
/// </para>
/// <para>
/// 📌 <b>台服遊戲本身沒有這個功能。</b>2026-08-19 離線確認：FFXIVClientStructs 的
/// <c>ConfigOption</c> 列舉（1041 個遊戲設定項）裡<b>沒有任何一項</b>與 fate／等級同步有關
/// （同一個查詢對已知存在的選項會命中，所以這個 0 不是查詢壞掉）；
/// 遊戲提供的是 <c>TextCommand</c> 270 這個手動指令，以及 F.A.T.E. 進度框上那顆要自己點的按鈕。
/// </para>
/// </remarks>
public sealed unsafe class FateLevelSync : TcModule
{
    public override string InternalName => "FateLevelSync";
    public override string DisplayName => "F.A.T.E. 自動等級同步";

    public override string Description =>
        "進入等級上限低於自己的 F.A.T.E. 時自動開啟等級同步（送出的是「開啟」不是「切換」，" +
        "而且送出前會先確認目前確實沒有同步，不會把已經同步的狀態反向解除）。離開 F.A.T.E. 不會自動解除同步。";

    public override ModuleCategory Category => ModuleCategory.Combat;

    public override bool HasConfigUI => true;

    /// <summary>台服 <c>TextCommand</c> 表裡「等級同步」那一列的列號。</summary>
    /// <remarks>
    /// ⚠️ 只是<b>起點</b>：實際使用的字串一律從表裡讀出來，而且讀不到就整個模組不動作
    /// （見 <see cref="ResolveCommand"/>）。列號本身也會先驗證讀到的那一列真的是等級同步指令。
    /// </remarks>
    private const uint LevelSyncTextCommandRow = 270;

    /// <summary>啟用時解析出來的指令字串（例如 <c>/lsync</c>）。<c>null</c>＝解析失敗，模組不動作。</summary>
    private string? syncCommand;

    private FateLevelSyncConfig Config => Plugin.Instance.Config.FateLevelSync;

    private readonly TaskQueue queue = new() { DefaultTimeoutMs = 15_000 };

    /// <summary>目前這一輪嘗試針對的 F.A.T.E.（0＝沒有在嘗試）。</summary>
    private ushort attemptFateId;

    /// <summary>同一個 F.A.T.E. 已經送出過幾次指令（上限 2：一次 on、一次退回 toggle）。</summary>
    private int attemptCount;

    protected override void OnEnable()
    {
        syncCommand = ResolveCommand();

        if (syncCommand == null)
        {
            Svc.Log.Information(
                $"[{InternalName}] 在 TextCommand 表裡找不到等級同步指令，模組不會做任何事。");
        }

        queue.OnTimeout = step => Svc.Log.Information($"[{InternalName}] 步驟逾時：{step}");

        Svc.Framework.Update += OnUpdate;
        Svc.Log.Information($"[{InternalName}] 模組啟用：指令＝{syncCommand ?? "（解析失敗）"}");
    }

    protected override void OnDisable()
    {
        Svc.Framework.Update -= OnUpdate;
        queue.Abort();
        attemptFateId = 0;
        attemptCount = 0;
    }

    /// <summary>
    /// 從 <c>TextCommand</c> 表解析出等級同步指令。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>會驗證那一列真的是等級同步指令</b>：只有列號對不上（改版時列號漂移）的話，
    /// 照樣讀出來的會是別的指令，而那是一個看起來很正常的錯答案——送出去會做完全不相干的事。
    /// 驗證方式＝該列的 <c>Command</c>／<c>ShortCommand</c> 至少有一個是等級同步的英文指令名。
    /// 驗證不過就<b>整表掃一次</b>找那個英文指令名；再找不到就回 <c>null</c>（模組不動作）。
    /// </remarks>
    private string? ResolveCommand()
    {
        try
        {
            var sheet = Svc.Data.GetExcelSheet<TextCommand>();

            var row = sheet.GetRowOrDefault(LevelSyncTextCommandRow);
            if (row != null && TryPick(row.Value, out var picked))
                return picked;

            Svc.Log.Information(
                $"[{InternalName}] TextCommand 第 {LevelSyncTextCommandRow} 列不是等級同步指令，改為整表搜尋。");

            foreach (var candidate in sheet)
            {
                if (TryPick(candidate, out var found))
                {
                    Svc.Log.Information($"[{InternalName}] 整表搜尋命中：TextCommand 第 {candidate.RowId} 列。");
                    return found;
                }
            }
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, $"[{InternalName}] 解析 TextCommand 失敗");
        }

        return null;

        static bool TryPick(TextCommand row, out string command)
        {
            command = string.Empty;

            var full = row.Command.ExtractText();
            var shortForm = row.ShortCommand.ExtractText();

            // 這兩個英文指令名是遊戲資料自己帶的，不是介面文字，不會因為語言而變。
            var isLevelSync =
                string.Equals(full, "/levelsync", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(shortForm, "/lsync", StringComparison.OrdinalIgnoreCase);

            if (!isLevelSync) return false;

            // 優先用短指令（與上游一致），讀不到才退回長指令、再退回中文別名。
            if (!string.IsNullOrWhiteSpace(shortForm)) command = shortForm;
            else if (!string.IsNullOrWhiteSpace(full)) command = full;
            else command = row.Alias.ExtractText();

            return !string.IsNullOrWhiteSpace(command);
        }
    }

    private void OnUpdate(IFramework framework)
    {
        queue.Tick();

        if (syncCommand == null) return;
        if (queue.IsBusy) return;
        if (!Throttle.Pass("FateLevelSync-Poll", 500)) return;

        if (!Svc.ClientState.IsLoggedIn) return;

        var player = Svc.Objects.LocalPlayer;
        if (player == null) return;

        // 🔴 每次重新取，不跨幀保存原生指標。
        var fateManager = FateManager.Instance();
        if (fateManager == null) return;

        var fateId = fateManager->GetCurrentFateId();
        if (fateId == 0)
        {
            // 離開 F.A.T.E.：只重設嘗試計數，刻意不解除同步。
            attemptFateId = 0;
            attemptCount = 0;
            return;
        }

        if (fateId != attemptFateId)
        {
            attemptFateId = fateId;
            attemptCount = 0;
        }

        // 🔴🔴 這一行就是不會反向解除同步的保證。
        if (fateManager->SyncedFateId == fateId) return;

        if (attemptCount >= 2) return;

        if (Config.SkipInCombat && Svc.Condition[ConditionFlag.InCombat]) return;

        if (!TryGetFateMaxLevel(fateId, out var maxLevel))
        {
            // 讀不到上限就不動作。猜一個值的話，等級同步會在不該同步的時候被打開。
            if (Throttle.Pass("FateLevelSync-NoMaxLevel", 60_000))
                Svc.Log.Information($"[{InternalName}] 在 IFateTable 裡找不到 F.A.T.E. #{fateId}，這一輪不動作。");
            return;
        }

        if (maxLevel == 0) return;
        if (player.Level <= maxLevel) return;

        StartSync(fateId, player.Level, maxLevel);
    }

    /// <summary>
    /// 從 Dalamud 受管理的 <see cref="IFateTable"/> 找出這個 F.A.T.E. 的等級上限。
    /// </summary>
    /// <remarks>
    /// 🔴 只取值，<b>不保存 <c>IFate</c> 參照</b>——那是原生指標的包裝，位址建構時就凍結。
    /// ⚠️ 列舉器可能吐出 <c>null</c>。
    /// </remarks>
    private static bool TryGetFateMaxLevel(ushort fateId, out byte maxLevel)
    {
        maxLevel = 0;

        foreach (var fate in Svc.Fates)
        {
            if (fate == null) continue;
            if (fate.FateId != fateId) continue;

            maxLevel = fate.MaxLevel;
            return true;
        }

        return false;
    }

    private void StartSync(ushort fateId, int playerLevel, byte maxLevel)
    {
        var isRetry = attemptCount > 0;

        // 第一次送「開啟」；萬一參數不被接受，第二次退回無參數的切換。
        // ⚠️ 退回切換是安全的，因為上面剛確認過 SyncedFateId != fateId（現在是關的）。
        var command = isRetry ? syncCommand! : $"{syncCommand} on";

        attemptCount++;

        queue.Enqueue(isRetry ? "重送等級同步指令（無參數切換）" : "送出等級同步指令", () =>
        {
            var fateManager = FateManager.Instance();
            if (fateManager == null) return null;

            // 送出前的最後一次確認：這幾幀之間狀態可能已經變了。
            if (fateManager->GetCurrentFateId() != fateId) return null;
            if (fateManager->SyncedFateId == fateId) return null;

            Svc.Log.Information(
                $"[{InternalName}] F.A.T.E. #{fateId} 上限 {maxLevel} 級、自己 {playerLevel} 級，送出：{command}");

            if (!ChatSender.ExecuteCommand(command))
            {
                Svc.Log.Information($"[{InternalName}] 指令沒有送出（ChatSender 拒絕）：{command}");
                return null;
            }

            return true;
        }, 5_000);

        queue.EnqueueDelay(Math.Max(500, Config.VerifyDelayMs), "等待伺服器回應");

        queue.Enqueue("確認同步結果", () =>
        {
            var fateManager = FateManager.Instance();
            if (fateManager == null) return null;

            var synced = fateManager->SyncedFateId == fateId;

            if (synced)
            {
                Svc.Log.Information($"[{InternalName}] 已同步到 F.A.T.E. #{fateId}。");
                if (Config.AnnounceInChat)
                    Svc.Chat.Print($"[TC Toolbox] 已開啟 F.A.T.E. 等級同步（上限 {maxLevel} 級）。");
                return true;
            }

            Svc.Log.Information(
                $"[{InternalName}] 送出 {command} 之後仍未同步"
                + (attemptCount >= 2 || !Config.RetryWithToggle
                    ? "，不再重試。"
                    : "，下一輪會改用無參數的切換再試一次。"));

            if (!Config.RetryWithToggle) attemptCount = 2;
            return true;
        }, 5_000);
    }

    public override void DrawConfig()
    {
        if (syncCommand == null)
        {
            ImGui.TextColored(new System.Numerics.Vector4(1f, 0.55f, 0.3f, 1f),
                              "在遊戲資料裡找不到等級同步指令，這個模組不會做任何事。");
        }
        else
        {
            ImGui.TextDisabled($"使用的指令：{syncCommand} on");
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    "指令字串是從遊戲的 TextCommand 表讀出來的，不是寫死的。\n" +
                    "刻意加上 on：無參數的等級同步指令是「切換」，已經同步時送出去會反過來解除同步。");
            }
        }

        var skipCombat = Config.SkipInCombat;
        if (ImGui.Checkbox("戰鬥中不動作", ref skipCombat))
        {
            Config.SkipInCombat = skipCombat;
            Plugin.Instance.Config.Save();
        }

        var retry = Config.RetryWithToggle;
        if (ImGui.Checkbox("「開啟」沒生效時改用無參數切換再試一次", ref retry))
        {
            Config.RetryWithToggle = retry;
            Plugin.Instance.Config.Save();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "只有在剛剛確認過「目前沒有同步」之後才會這麼做，所以不會反向解除同步。\n" +
                "每個 F.A.T.E. 最多送兩次指令。");
        }

        ImGui.SetNextItemWidth(160f);
        var delay = Config.VerifyDelayMs;
        if (ImGui.SliderInt("送出後等待確認（毫秒）", ref delay, 500, 8000))
        {
            Config.VerifyDelayMs = delay;
            Plugin.Instance.Config.Save();
        }

        var announce = Config.AnnounceInChat;
        if (ImGui.Checkbox("同步成功時在聊天視窗留一行", ref announce))
        {
            Config.AnnounceInChat = announce;
            Plugin.Instance.Config.Save();
        }
    }
}

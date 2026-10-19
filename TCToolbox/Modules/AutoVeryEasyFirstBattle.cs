using System;
using Dalamud.Bindings.ImGui;
using Dalamud.Hooking;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 單人任務戰鬥第一戰自動非常簡單：hook 遊戲的命令分派器，只在「開始單人任務戰鬥」命令上
/// 把難度參數改成「非常簡單」。第一戰沒有難度框、遊戲送預設難度 0；這裡攔到那一發改成 2。
/// </summary>
/// <remarks>
/// 🔴 本檔假設與契約（改版必重驗，寫死的命令碼/難度值下次改版必壞且靜默）：
/// <para>①命令碼 <see cref="CmdStartSoloQuestBattle"/>=823 在台服現行版＝開始單人任務戰鬥（離線逆向驗過）。</para>
/// <para>②難度值 0/1/2＝通常/簡單/非常簡單，<see cref="DifficultyVeryEasy"/>=2。</para>
/// <para>③分派器不解讀 param1（只塞進封包欄位），改值在 client 端不會 AVE；伺服器接受第一戰的
/// param1=2（實機確認有效；2 是難度框本來就會送的合法值）。</para>
/// <para>④sig 掃不到 ⇒ 不裝 hook、功能停用。detour 只在 command==823 碰 param1，其餘一律原樣轉呼叫。</para>
/// </remarks>
public sealed unsafe class AutoVeryEasyFirstBattle : TcModule
{
    public override string InternalName => "AutoVeryEasyFirstBattle";
    public override string DisplayName => "單人任務戰鬥第一戰自動非常簡單";

    public override string Description =>
        "單人任務戰鬥第一戰沒有難度框，攔截出戰命令把難度直接改成「非常簡單」。" +
        "遊戲改版後若找不到攔截點，本模組不會生效（照常難度開戰），不影響其他模組。";

    public override ModuleCategory Category => ModuleCategory.Combat;

    public override bool HasConfigUI => true;

    /// <summary>開始單人任務戰鬥的命令碼（見檔頭契約①）。</summary>
    private const uint CmdStartSoloQuestBattle = 823;

    /// <summary>難度＝非常簡單（見檔頭契約②）。</summary>
    private const uint DifficultyVeryEasy = 2;

    /// <summary>命令分派器特徵碼（DR 原值；開頭是 E8，ScanText 會自動解到被呼叫的那支函式）。</summary>
    private const string ExecuteCommandSignature =
        "E8 ?? ?? ?? ?? 48 8B 06 48 8B CE FF 50 ?? E9 ?? ?? ?? ?? 49 8B CC";

    private delegate nint ExecuteCommandDelegate(uint command, uint param1, uint param2, uint param3, uint param4);

    private Hook<ExecuteCommandDelegate>? hook;

    private const long LogIntervalMs = 5_000;

    /// <summary>下一次可以寫改寫記錄的時間（<see cref="Environment.TickCount64"/> 座標系）。</summary>
    private long nextLogTick;

    protected override void OnEnable()
    {
        if (!Svc.SigScanner.TryScanText(ExecuteCommandSignature, out var address) || address == nint.Zero)
        {
            Svc.Log.Information(
                $"[{InternalName}] 找不到命令分派器的特徵碼，本模組不會生效（不影響其他模組）。" +
                "這通常代表台服主程式改版、特徵碼需要更新。");
            return;
        }

        hook = Svc.Hooks.HookFromAddress<ExecuteCommandDelegate>(address, Detour);
        hook.Enable();
    }

    protected override void OnDisable()
    {
        hook?.Dispose();
        hook = null;
    }

    private nint Detour(uint command, uint param1, uint param2, uint param3, uint param4)
    {
        // 🔴 快照到區域變數：OnDisable() 會把欄位設回 null，而 detour 可能還在執行中。
        var hook = this.hook;
        if (hook == null) return nint.Zero;

        // 高頻中央分派器：非出戰命令連比對以外的事都不做，直接原樣轉呼叫。
        if (command == CmdStartSoloQuestBattle && param1 != DifficultyVeryEasy)
        {
            param1 = DifficultyVeryEasy;
            LogRewrite();
        }

        return hook.OriginalDisposeSafe(command, param1, param2, param3, param4);
    }

    /// <summary>記一行「已改寫」，<see cref="LogIntervalMs"/> 內最多一行。</summary>
    /// <remarks>
    /// 刻意不用 <see cref="Throttle"/>：那是主執行緒專用的共用字典，而分派器由哪條執行緒呼叫沒有證明。
    /// 記錄失敗不可擋住轉呼叫，所以整段包起來、失敗就略過。
    /// </remarks>
    private void LogRewrite()
    {
        try
        {
            var now = Environment.TickCount64;
            if (now < nextLogTick) return;

            nextLogTick = now + LogIntervalMs;
            Svc.Log.Information(
                $"[{InternalName}] 已把單人任務戰鬥難度參數改為非常簡單（命令 {CmdStartSoloQuestBattle}、param1→{DifficultyVeryEasy}）。");
        }
        catch
        {
            // 刻意吞掉：這裡只有寫記錄，失敗也要照常轉呼叫原函式。
        }
    }

    public override void DrawConfig()
    {
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
        ImGui.TextDisabled(
            "單人任務戰鬥第一戰沒有難度選擇框；本模組攔截出戰命令，把難度直接改成「非常簡單」。");
        ImGui.TextDisabled(
            "只改「開始單人任務戰鬥」這一個命令的難度，已經是非常簡單就不動。\n" +
            "遊戲改版後若找不到攔截點，本模組不會生效（照常難度開戰），記錄裡會有一行說明。");
        ImGui.PopTextWrapPos();
    }
}

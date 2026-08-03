using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 解除登入畫面的「請稍後再登錄」冷卻鎖。
/// 機制：在大廳畫面把 <see cref="AgentLobby"/> 的 <c>TemporaryLocked</c>（+0x1310）持續寫成 false。
/// 形狀與 <see cref="AutoBlockTitleMovie"/> 完全一樣——純資料寫入，不 hook、不 patch。
/// </summary>
/// <remarks>
/// 🔴 <b>這個模組有帳號風險，預設關閉，而且應該一直保持關閉。</b>
/// <c>TemporaryLocked</c> 是<b>客戶端自己的重登節流</b>：登出／被踢之後客戶端會擋你一小段時間，
/// 免得你連續狂送登入請求。把它拆掉之後，客戶端不再攔你，但<b>伺服器端看得到那些密集的登入嘗試</b>——
/// 這是外部可觀測的行為，不是本機的顯示問題。
/// <para>
/// ⚠️ 與 DailyRoutines 原版的差異：DR 用特徵碼 hook <c>AgentLobby::Update</c>，在原函式前後各寫一次。
/// 這裡不需要 hook——欄位就在那裡，從 <c>Framework.Update</c> 寫同一個位址效果相同，
/// 少一條台服沒驗過的特徵碼（特徵碼解錯位址是靜默的）。
/// </para>
/// </remarks>
public sealed unsafe class AutoIgnoreLoginLock : TcModule
{
    public override string InternalName => "AutoIgnoreLoginLock";
    public override string DisplayName => "解除重登冷卻鎖";

    public override string Description =>
        $"在角色選擇畫面持續解除客戶端的重登冷卻（就是會跳「{LockMessage}」的那一個），登出後可以立刻重登。" +
        "🔴 有帳號風險：這個冷卻是客戶端在替你節流，拆掉之後伺服器端會看到密集的登入嘗試。" +
        "預設關閉，請自行判斷是否要開。";

    public override bool HasConfigUI => true;

    /// <summary>冷卻提示訊息所在的 <c>LogMessage</c> 列（台服＝「請稍後再登錄。」）。</summary>
    private const uint LockMessageRow = 430;

    private static readonly string LockMessage = ResolveLockMessage();

    protected override void OnEnable() => Svc.Framework.Update += OnUpdate;

    protected override void OnDisable() => Svc.Framework.Update -= OnUpdate;

    /// <summary>提示訊息取自遊戲自己的 LogMessage 表；台服未開放的列會是空字串，那就退回寫死的字面值。</summary>
    private static string ResolveLockMessage()
    {
        var text = Svc.Data.GetExcelSheet<LogMessage>()
                      .GetRowOrDefault(LockMessageRow)?.Text.ExtractText();

        return string.IsNullOrWhiteSpace(text) ? "請稍後再登錄。" : text.Trim();
    }

    private void OnUpdate(IFramework framework)
    {
        // 進遊戲之後大廳 agent 不再管這件事，直接不做事
        if (Svc.ClientState.IsLoggedIn) return;
        if (!Throttle.Pass("AutoIgnoreLoginLock-Clear", 500)) return;

        var agent = AgentLobby.Instance();
        if (agent == null) return;
        if (!agent->TemporaryLocked) return;

        agent->TemporaryLocked = false;

        if (Throttle.Pass("AutoIgnoreLoginLock-Log", 10_000))
            Svc.Log.Information($"[{InternalName}] 已清除大廳的重登冷卻旗標。");
    }

    public override void DrawConfig()
    {
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);

        ImGui.TextColored(new Vector4(1f, 0.45f, 0.4f, 1f),
                          "這個功能沒有設定項，只有一句警告：");
        ImGui.TextColored(new Vector4(1f, 0.75f, 0.4f, 1f),
                          "「請稍後再登錄」是客戶端在替你節流重複登入。把它拆掉之後，" +
                          "你送出的登入嘗試會比正常玩家密集得多，而那是伺服器端看得到的行為。" +
                          "被判定異常的後果由帳號承擔——只有在你清楚自己在做什麼的時候才開。");

        ImGui.Spacing();
        ImGui.TextDisabled($"遊戲原文：{LockMessage}（LogMessage #{LockMessageRow}）");
        ImGui.TextDisabled("作用範圍只有角色選擇／標題畫面，登入後自動停止動作。");

        ImGui.PopTextWrapPos();
    }
}

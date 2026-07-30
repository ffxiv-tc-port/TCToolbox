using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 阻止標題畫面／角色選擇待機動畫。
/// 機制：每 tick 把 <see cref="AgentLobby"/> 的 <c>IdleTime</c> 歸零——大廳的閒置計時永遠到不了
/// 觸發門檻，待機影片與「閒置返回標題」都不會發生。純資料寫入，不做 code patch、不改繪製流程。
/// 與 AutoRetainer 的關係：AR 在 <c>Modules/Multi/MultiMode.cs</c> 有同一行寫入，但只在
/// MultiMode 啟用且正在等待登入畫面時執行；此模組是獨立的常時版本，不依賴也不修改 AR。
/// </summary>
public sealed unsafe class AutoBlockTitleMovie : TcModule
{
    public override string InternalName => "AutoBlockTitleMovie";
    public override string DisplayName => "阻止標題動畫";

    public override string Description =>
        "在標題畫面與角色選擇畫面持續把大廳閒置計時歸零，待機宣傳影片不會播放、也不會因閒置被踢回標題。" +
        "只寫入計時欄位，登入後自動停止動作。";

    protected override void OnEnable()
    {
        Svc.Framework.Update += OnUpdate;
    }

    protected override void OnDisable()
    {
        Svc.Framework.Update -= OnUpdate;
    }

    private void OnUpdate(IFramework framework)
    {
        // 進遊戲後大廳 agent 不再計時，直接不做事
        if (Svc.ClientState.IsLoggedIn) return;
        if (!Throttle.Pass("AutoBlockTitleMovie-Reset", 500)) return;

        var agent = AgentLobby.Instance();
        if (agent == null) return;

        agent->IdleTime = 0;
    }
}

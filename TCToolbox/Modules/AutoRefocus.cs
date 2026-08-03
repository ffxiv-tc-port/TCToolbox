using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 副本內自動恢復焦點目標：焦點目標消失後（王暫時離場、換場、目標被移除再生成），
/// 自動把焦點設回同一個對象。
/// 機制：每次輪詢讀 <c>ITargetManager.FocusTarget</c>，只<b>記下它的
/// <see cref="Dalamud.Game.ClientState.Objects.Types.IGameObject.GameObjectId"/></b>；
/// 要恢復時再用 <c>IObjectTable.SearchById</c> 重新查表拿當下有效的物件。
/// 零特徵碼、零 hook、不寫記憶體（設定焦點走 Dalamud 自己的 setter）。
/// </summary>
/// <remarks>
/// 🔴 <b>為什麼是存 ID 不是存 <c>IGameObject</c></b>：<c>IGameObject.Address</c> 在建構時就凍結，
/// 之後永遠不會重新解析；而 <c>IsValid()</c> 只檢查「玩家有沒有登入」，<b>完全不驗位址</b>。
/// 把 <c>IGameObject</c> 存進欄位＝把一個原生指標存進欄位，物件一被回收就是懸空指標，
/// 而懸空指標產生的 AccessViolationException 在 .NET Core 是 corrupted-state exception，
/// <b>try/catch 攔不到</b>。所以整個模組的欄位裡只有一個 <c>ulong</c>。
/// <para>
/// 與 DailyRoutines 原版的差異：DR 用特徵碼 hook <c>SetFocusTargetByObjectID</c> 來得知
/// 「使用者設了哪個焦點」。這裡改成輪詢既有的焦點目標——省掉一條台服未驗證的特徵碼
/// （特徵碼解錯位址是靜默的），代價只是「使用者設焦點後最多 200ms 才被記住」。
/// </para>
/// </remarks>
public sealed class AutoRefocus : TcModule
{
    public override string InternalName => "AutoRefocus";
    public override string DisplayName => "自動恢復焦點目標";

    public override string Description =>
        "在副本內，焦點目標消失時自動設回同一個對象（王離場再回來、目標被重新生成都算）。" +
        "只記住對象編號、每次都重新查表，不保存物件參考。";

    public override bool HasConfigUI => true;

    /// <summary>輪詢間隔。焦點消失到恢復之間最多就是這個延遲。</summary>
    private const int PollIntervalMs = 200;

    /// <summary>「沒有對象」的哨兵值（遊戲對空目標填的是 0xE0000000）。</summary>
    private const ulong InvalidObjectId = 0xE0000000;

    /// <summary>
    /// 上一次看到的焦點目標編號。<b>這裡只能放 ID，不能放 <c>IGameObject</c></b>（理由見型別註解）。
    /// </summary>
    private ulong rememberedObjectId;

    private AutoRefocusConfig Config => Plugin.Instance.Config.Refocus;

    protected override void OnEnable()
    {
        rememberedObjectId = 0;
        Svc.ClientState.TerritoryChanged += OnTerritoryChanged;
        Svc.Framework.Update += OnUpdate;
    }

    protected override void OnDisable()
    {
        Svc.Framework.Update -= OnUpdate;
        Svc.ClientState.TerritoryChanged -= OnTerritoryChanged;
        rememberedObjectId = 0;
    }

    /// <summary>換區＝上一個區域的對象編號全部作廢，絕不能跨區沿用。</summary>
    private void OnTerritoryChanged(ushort territory) => rememberedObjectId = 0;

    private void OnUpdate(IFramework framework)
    {
        if (!Throttle.Pass("AutoRefocus-Poll", PollIntervalMs)) return;

        // ITargetManager 的 getter/setter 直接解參考 TargetSystem，沒登入就不要碰
        if (!Svc.ClientState.IsLoggedIn || Svc.Objects.LocalPlayer == null)
        {
            rememberedObjectId = 0;
            return;
        }

        if (Config.OnlyInDuty && !Svc.Condition[ConditionFlag.BoundByDuty])
        {
            rememberedObjectId = 0;
            return;
        }

        // 讀取當下的焦點目標。注意這個 IGameObject 只在這一輪內使用，不存進任何欄位。
        var focus = Svc.Targets.FocusTarget;
        if (focus != null)
        {
            var id = focus.GameObjectId;
            if (id != 0 && id != InvalidObjectId)
                rememberedObjectId = id;
            return;
        }

        if (rememberedObjectId is 0 or InvalidObjectId) return;

        // 重新查表：查得到才是還活著的對象，查不到就什麼都不做（下一輪再試）。
        var restored = Svc.Objects.SearchById(rememberedObjectId);
        if (restored == null || !restored.IsTargetable) return;

        Svc.Targets.FocusTarget = restored;

        if (Config.NotifyOnRestore && Throttle.Pass("AutoRefocus-Notify", 5_000))
            Svc.Log.Information($"[{InternalName}] 焦點目標已恢復：{restored.Name}（{rememberedObjectId:X}）");
    }

    public override void DrawConfig()
    {
        var onlyInDuty = Config.OnlyInDuty;
        if (ImGui.Checkbox("只在副本內生效", ref onlyInDuty))
        {
            Config.OnlyInDuty = onlyInDuty;
            Plugin.Instance.Config.Save();
        }

        using (ImRaii.PushIndent())
            ImGui.TextDisabled("取消勾選後在野外也會恢復焦點；換區時記住的對象一律清空。");

        var notify = Config.NotifyOnRestore;
        if (ImGui.Checkbox("恢復時寫進插件記錄", ref notify))
        {
            Config.NotifyOnRestore = notify;
            Plugin.Instance.Config.Save();
        }

        using (ImRaii.PushIndent())
            ImGui.TextDisabled("寫入等級為 Information，且每 5 秒最多一則。");
    }
}

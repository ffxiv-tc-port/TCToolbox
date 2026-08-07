using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 防自動掛機登出。
/// 機制：每秒把 <see cref="InputTimerModule"/> 的閒置計時欄位歸零，讓遊戲自己的
/// 閒置判定永遠達不到上限，零 hook、零記憶體 code patch、不模擬任何按鍵輸入。
/// DR 原版閉源，依「重置閒置計時器」的標準做法自寫。
/// </summary>
public sealed unsafe class AutoAntiAfk : TcModule
{
    public override string InternalName => "AutoAntiAfk";
    public override string DisplayName => "防自動掛機登出";

    public override string Description =>
        "定期把遊戲的閒置計時器歸零，避免長時間未操作被自動登出（含副本／PvP／無人島／新手頻道各自的閒置上限）。" +
        "只寫入計時欄位，不模擬按鍵、不修改遊戲程式碼。";

    public override ModuleCategory Category => ModuleCategory.Misc;

    public override bool HasConfigUI => true;

    private AutoAntiAfkConfig Config => Plugin.Instance.Config.AntiAfk;

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
        // 計時器以秒為單位累加，每秒歸零一次即足夠（上限最短的情境也遠大於 1 秒）
        if (!Throttle.Pass("AutoAntiAfk-Reset", 1_000)) return;
        if (!Svc.ClientState.IsLoggedIn) return;

        var module = InputTimerModule.Instance();
        if (module == null) return;

        // AfkTimer：一般閒置登出判定（AutoAfk 啟用時累加，已進入 AFK 時為負值）
        module->AfkTimer = 0f;

        // ContentInputTimer：副本／PvP／無人島等「內容中」的無操作踢出判定
        module->ContentInputTimer = 0f;

        // InputTimer 同時驅動待機動作與鏡頭回正，預設不動，避免抑制待機動作
        if (Config.ResetIdleAnimationTimer)
            module->InputTimer = 0f;
    }

    public override void DrawConfig()
    {
        var resetIdle = Config.ResetIdleAnimationTimer;
        if (ImGui.Checkbox("同時重置一般閒置計時器", ref resetIdle))
        {
            Config.ResetIdleAnimationTimer = resetIdle;
            Plugin.Instance.Config.Save();
        }

        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("這個計時器同時控制待機動作與鏡頭回正，重置後角色不會再自動進入待機姿勢。\n只是要避免被登出的話不需要勾。");
    }
}

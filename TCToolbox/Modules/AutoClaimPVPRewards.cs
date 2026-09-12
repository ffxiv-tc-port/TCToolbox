using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 系列賽獎勵一鍵領取：開著「星里路標」的報酬視窗（<c>PvpReward</c>）並按下我們的開始鈕之後，
/// 把目前賽季所有待領取的系列賽獎勵一路領完。
/// ⚠️ 台服官方寫「星<b>里</b>路標」（<c>Addon</c> 第 14901 列），不是「星裡路標」——
/// 舊字串那個「裡」在台服資料表裡零命中。
/// 零 hook、零特徵碼、不寫記憶體，只重新觸發節點自己的事件（等同你親手點）。
/// </summary>
/// <remarks>
/// <para><b>防護（驗不出來就不點）：</b>目標節點一律自己走 <c>UldManager.NodeList</c> 用
/// <b>節點 ID</b> 找（不是索引、也不用 <c>GetComponentButtonById</c> 那種綁特徵碼的
/// <c>[MemberFunction]</c>），找到之後還要<b>比對節點型別等於該格在 ULD 裡的元件編號</b>
/// （第 1 格是 1007、其餘是 1006）。型別對不上就整輪停手並寫一行 Information —— 這條檢查
/// 正好就能擋掉舊版那種「點到別顆按鈕」的整類問題：關閉鈕（節點 124）的元件是 1001。</para>
/// </remarks>
public sealed unsafe class AutoClaimPVPRewards : TcModule
{
    /// <summary>
    /// 🔴 <b>不要跟著顯示名改這一行。</b>這個字串是設定檔裡
    /// <c>EnabledModules</c>／<c>FavoriteModules</c> 的鍵，改掉等於把既有使用者的
    /// 啟用狀態與釘選狀態靜默重設成「沒開過」。
    /// 名稱裡的 <c>Auto</c> 是歷史包袱（這個模組其實是手動按鈕），留著。
    /// </summary>
    public override string InternalName => "AutoClaimPVPRewards";

    /// <summary>
    /// ⚠️ 舊名是「自動領取戰利水晶」，但這個模組<b>不會自動做任何事</b>——
    /// 要在報酬視窗上按下開始鈕才跑。舊名讓人以為開著就會自己領，所以改掉。
    /// 「系列賽獎勵」是台服自己的字串（<c>Addon</c> 第 13757 列），不是意譯。
    /// </summary>
    public override string DisplayName => "系列賽獎勵一鍵領取";

    public override string Description =>
        "在「星里路標」報酬視窗上加一顆開始鈕，按下後把目前賽季所有待領取的系列賽獎勵一路領完。" +
        "只有你自己按下開始才會跑；戰利水晶快到上限時會自動停下。";

    public override ModuleCategory Category => ModuleCategory.Combat;

    /// <summary>報酬視窗上按了開始才跑；開著不按，什麼都不會被領走。</summary>
    public override bool IsManualTrigger => true;

    public override bool HasConfigUI => true;

    private const string AddonName = "PvpReward";
    private const string YesnoAddonName = "SelectYesno";

    /// <summary>本季已達成的系列賽等級所在的 AtkValue 索引。</summary>
    private const int LevelValueIndex = 7;

    /// <summary>本季已領取到第幾級所在的 AtkValue 索引。</summary>
    private const int ClaimedValueIndex = 8;

    /// <summary>視窗上實際存在的獎勵格數（第 31 格是最後一格，再上去沒有可點的格子）。</summary>
    private const int MaxTier = 31;

    /// <summary>第 1 格（節點 22）在 ULD 裡的元件編號。</summary>
    private const uint FirstTileComponentId = 1007;

    /// <summary>第 2–31 格（節點 23–54）在 ULD 裡的元件編號。</summary>
    private const uint TileComponentId = 1006;

    /// <summary>戰利水晶的 ItemId（台服 Item 表 36656＝戰利水晶）。</summary>
    private const uint TrophyCrystalItemId = 36656;

    /// <summary>領取後遊戲會重建視窗，這段時間內視窗不見不算中止。</summary>
    private const int AddonGraceMs = 3_000;

    /// <summary>按下獎勵格之後等確認框出現的上限。</summary>
    private const int ConfirmWaitMs = 5_000;

    /// <summary>按下「是」之後等 values 反映出新的已領取等級的上限。</summary>
    private const int ApplyWaitMs = 8_000;

    private readonly TaskQueue queue = new() { DefaultTimeoutMs = 15_000 };

    private int claimedCount;
    private string statusText = string.Empty;

    private DateTime lastAddonSeenAt;
    private DateTime? awaitingConfirmSince;
    private DateTime? awaitingApplySince;
    private int claimedValueBeforeConfirm;

    private AutoClaimPVPRewardsConfig Config => Plugin.Instance.Config.ClaimPvpRewards;

    protected override void OnEnable()
    {
        queue.OnTimeout = step => Stop($"領取流程逾時（{step}）");

        Svc.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, AddonName, OnAddonClosed);
        Svc.Framework.Update += OnUpdate;
        Svc.PluginInterface.UiBuilder.Draw += DrawOverlay;
    }

    protected override void OnDisable()
    {
        Svc.AddonLifecycle.UnregisterListener(OnAddonClosed);
        Svc.Framework.Update -= OnUpdate;
        Svc.PluginInterface.UiBuilder.Draw -= DrawOverlay;
        queue.Abort();
    }

    private void OnUpdate(IFramework framework) => queue.Tick();

    /// <summary>
    /// 視窗關閉只記時間戳，不直接中止。
    /// </summary>
    /// <remarks>
    /// 領取成功之後遊戲會把報酬視窗重建一次，那也會走 <c>PreFinalize</c>。
    /// 舊版在這裡直接 <c>Abort()</c>，等於「只要成功一次就自己把自己停掉」。
    /// 真正的中止條件改成「視窗消失超過 <see cref="AddonGraceMs"/> 還沒回來」，由主迴圈判。
    /// </remarks>
    private void OnAddonClosed(AddonEvent type, AddonArgs args)
    {
        if (!queue.IsBusy) return;
        statusText = "視窗重建中…";
    }

    private static int GetTrophyCrystalCount()
    {
        var manager = InventoryManager.Instance();
        return manager == null ? 0 : manager->GetInventoryItemCount(TrophyCrystalItemId, false, true, true);
    }

    /// <summary>讀一格 AtkValue 的無號整數；讀不到（含索引超界）就回 -1。</summary>
    /// <remarks>
    /// 🔴 <c>AtkUnitBase.AtkValuesSpan</c> 的實作是
    /// <c>new Span&lt;AtkValue&gt;(AtkValues, AtkValuesCount)</c>：
    /// <b>它自己不判 <c>AtkValues</c> 這個欄位</b>，而 <c>Span</c> 的建構子也不驗指標。
    /// ⇒ 只判 <c>addon == null</c> 和 <c>Length</c> 都擋不住這條，必須另外自判 <c>AtkValues</c> 欄位。
    /// </remarks>
    private static int ReadValue(AtkUnitBase* addon, int index)
    {
        if (addon == null) return -1;
        if (addon->AtkValues == null) return -1;

        var values = addon->AtkValuesSpan;
        if (index < 0 || index >= values.Length) return -1;

        return (int)values[index].UInt;
    }

    /// <summary>本季還沒領的等級數；讀不到就回 -1。</summary>
    private static int GetPendingCount(AtkUnitBase* addon)
    {
        var level = ReadValue(addon, LevelValueIndex);
        var claimed = ReadValue(addon, ClaimedValueIndex);
        if (level < 0 || claimed < 0) return -1;

        return Math.Max(0, Math.Min(level, MaxTier) - claimed);
    }

    /// <summary>等級 → 該格在視窗上的節點 ID。超出 1..31 回 0。</summary>
    /// <remarks>
    /// 取自 <c>AddonPvpReward::OnSetup</c>：三排各 10 格（節點 22-31／33-42／44-53），
    /// 第 31 格是節點 54。中間跳掉的 32／43 是那一排的容器 Res 節點。
    /// </remarks>
    private static uint TierToNodeId(int tier) => tier switch
    {
        >= 1 and <= 10 => (uint)(21 + tier),
        >= 11 and <= 20 => (uint)(22 + tier),
        >= 21 and <= MaxTier => (uint)(23 + tier),
        _ => 0,
    };

    /// <summary>
    /// 在 addon 的節點清單裡用<b>節點 ID</b> 找節點。
    /// </summary>
    /// <remarks>
    /// 刻意不用 <c>AtkUnitBase.GetComponentButtonById</c>：那是綁特徵碼的 <c>[MemberFunction]</c>，
    /// 解不出位址時是在原生層擲例外。這裡只讀 <c>NodeList</c>／<c>NodeId</c> 兩個純欄位、
    /// 邊界一律是 <c>NodeListCount</c>，假設不成立時最差就是回 null。
    /// </remarks>
    private static AtkResNode* FindNodeById(AtkUnitBase* addon, uint nodeId)
    {
        if (addon == null) return null;
        if (addon->UldManager.NodeList == null) return null;

        var count = addon->UldManager.NodeListCount;
        for (var i = 0; i < count; i++)
        {
            var node = addon->UldManager.NodeList[i];
            if (node != null && node->NodeId == nodeId) return node;
        }

        return null;
    }

    private void DrawOverlay()
    {
        var addon = UiHelper.GetAddon(AddonName);
        if (!UiHelper.IsReady(addon)) return;

        const ImGuiWindowFlags flags = ImGuiWindowFlags.AlwaysAutoResize |
                                       ImGuiWindowFlags.NoTitleBar |
                                       ImGuiWindowFlags.NoCollapse |
                                       ImGuiWindowFlags.NoScrollbar |
                                       ImGuiWindowFlags.NoSavedSettings;

        if (ImGui.Begin("###TCToolboxPvpReward", flags))
        {
            ImGui.SetWindowPos(new Vector2(addon->GetX() + 6, addon->GetY() - ImGui.GetWindowSize().Y - 4));

            ImGui.AlignTextToFramePadding();
            // 浮在報酬視窗上的標題直接引用 DisplayName，不再各寫一份字面值。
            ImGui.TextColored(new Vector4(1f, 0.85f, 0.35f, 1f), DisplayName);

            var pending = GetPendingCount(addon);
            ImGui.SameLine();
            if (pending < 0)
            {
                // 讀不到就不給按（fail-closed），而且要在 log 裡看得見為什麼。
                ImGui.TextColored(new Vector4(1f, 0.55f, 0.35f, 1f), "待領取：讀不到");
                if (Throttle.Pass("AutoClaimPVPRewards-ValueMissing", 60_000))
                {
                    // 「AtkValues 是 null」與「長度不夠」是兩種不同的故障，回報時要分得出來：
                    // 前者是視窗正在拆解／還沒填值（等一下就好），後者才代表 AtkValue 版面改了。
                    var detail = $"長度 {addon->AtkValuesCount}";
                    if (addon->AtkValues == null)
                        detail = "是 null（視窗正在拆解或還沒填值）";

                    Svc.Log.Information(
                        $"[AutoClaimPVPRewards] 讀不到 AtkValue[{LevelValueIndex}]／[{ClaimedValueIndex}]" +
                        $"（AtkValues {detail}），已停用開始鈕。");
                }
            }
            else
            {
                ImGui.TextDisabled($"待領取 {pending} 級");
            }

            ImGui.SameLine();
            using (ImRaii.Disabled(queue.IsBusy || pending <= 0))
            {
                if (ImGui.Button("開始領取##pvpreward"))
                    Start();
            }

            if (!queue.IsBusy && pending == 0)
            {
                ImGui.SameLine();
                ImGui.TextDisabled("（沒有待領取的等級）");
            }

            ImGui.SameLine();
            if (ImGui.Button("停止##pvpreward"))
                Stop("已手動停止");

            if (queue.IsBusy && statusText.Length > 0)
            {
                ImGui.SameLine();
                ImGui.TextDisabled(statusText);
            }
        }

        ImGui.End();
    }

    private void Start()
    {
        if (queue.IsBusy) return;

        claimedCount = 0;
        statusText = string.Empty;
        awaitingConfirmSince = null;
        awaitingApplySince = null;
        lastAddonSeenAt = DateTime.UtcNow;

        Svc.Log.Information("[AutoClaimPVPRewards] 開始領取。");

        // 單一自我驅動步驟：每一 tick 都從 addon 重新取狀態，不跨幀保存任何原生指標。
        // 佇列逾時給得很寬，真正的終止條件由 Pump() 自己判（每一種都會寫一行 Information）。
        queue.Enqueue("領取系列賽獎勵", Pump, 10 * 60 * 1000);
    }

    private void Stop(string reason)
    {
        var wasBusy = queue.IsBusy;
        queue.Abort();
        awaitingConfirmSince = null;
        awaitingApplySince = null;
        statusText = string.Empty;

        if (!wasBusy) return;

        Svc.Log.Information($"[AutoClaimPVPRewards] {reason}（本輪已領取 {claimedCount} 級）。");
        Svc.Chat.Print($"[TC Toolbox] {reason}（本輪已領取 {claimedCount} 級）。");
    }

    /// <summary>主迴圈。true=收工、false=下一 tick 再來、null=中止。</summary>
    private bool? Pump()
    {
        var addon = UiHelper.GetAddon(AddonName);
        if (!UiHelper.IsReady(addon))
        {
            // 領取成功後遊戲會重建視窗，短暫不見是正常的。
            if ((DateTime.UtcNow - lastAddonSeenAt).TotalMilliseconds <= AddonGraceMs) return false;
            Stop("報酬視窗已關閉，停止領取");
            return null;
        }

        lastAddonSeenAt = DateTime.UtcNow;

        // ── 階段三：按過「是」，等確認框收掉、等 values 反映出新的已領取等級 ──
        if (awaitingApplySince != null)
        {
            if ((DateTime.UtcNow - awaitingApplySince.Value).TotalMilliseconds > ApplyWaitMs)
            {
                Stop("領取後介面沒有更新，停止");
                return null;
            }

            // 確認框關閉要一兩幀，別在這段時間把它誤判成「別人開的確認框」。
            if (UiHelper.IsAddonReady(YesnoAddonName))
            {
                statusText = "關閉確認框…";
                return false;
            }

            var claimedNow = ReadValue(addon, ClaimedValueIndex);
            if (claimedNow > claimedValueBeforeConfirm)
            {
                claimedCount++;
                awaitingApplySince = null;
                statusText = $"已領取 {claimedCount} 級";
                return false;
            }

            statusText = "等待介面更新…";
            return false;
        }

        // ── 階段二：按過獎勵格，等自己造成的確認框 ──
        if (awaitingConfirmSince != null)
        {
            var yesno = UiHelper.GetAddon(YesnoAddonName);
            if (UiHelper.IsReady(yesno))
            {
                if (!Throttle.Pass("AutoClaimPVPRewards-Yesno", 300)) return false;

                // 先取基準值再送事件：之後就是靠「已領取等級有沒有變大」來確認真的領到了。
                claimedValueBeforeConfirm = ReadValue(addon, ClaimedValueIndex);
                if (claimedValueBeforeConfirm < 0)
                {
                    Stop("送出確認前讀不到已領取的系列賽等級，停止");
                    return null;
                }

                // 「確定要領取獎勵嗎？」的兩個選項是 0=確定 / 1=取消。
                // 🔴 守衛擋下（同一扇確認框剛按過、還在關閉中）＝這一輪沒按到：狀態不推進，下一輪再來。
                if (!UiHelper.TryFireCallback(yesno, true, 0)) return false;
                awaitingConfirmSince = null;
                awaitingApplySince = DateTime.UtcNow;
                statusText = "等待領取結果…";
                return false;
            }

            if ((DateTime.UtcNow - awaitingConfirmSince.Value).TotalMilliseconds > ConfirmWaitMs)
            {
                Stop("按下獎勵格後沒有出現確認框，停止（可能是背包空間不足）");
                return null;
            }

            statusText = "等待確認框…";
            return false;
        }

        // ── 階段一：決定下一格 ──
        // 只要別的視窗擋著就先不動作（避免我們的點擊被別的介面吃掉）。
        if (UiHelper.IsAddonReady(YesnoAddonName))
        {
            Stop("偵測到不是本模組造成的確認框，停止");
            return null;
        }

        if (GetTrophyCrystalCount() >= Config.StopAtTrophyCrystals)
        {
            Stop($"戰利水晶已達 {Config.StopAtTrophyCrystals}，停止領取");
            return null;
        }

        var level = ReadValue(addon, LevelValueIndex);
        var claimed = ReadValue(addon, ClaimedValueIndex);
        if (level < 0 || claimed < 0)
        {
            Stop($"讀不到系列賽等級／已領取到第幾級（AtkValue 版面可能因改版變動：level={level} claimed={claimed}），停止");
            return null;
        }

        var tier = claimed + 1;
        if (tier > level || tier > MaxTier)
        {
            Stop($"沒有待領取的等級了（已達成 {level} 級、已領取 {claimed} 級）");
            return null;
        }

        var nodeId = TierToNodeId(tier);
        if (nodeId == 0)
        {
            Stop($"第 {tier} 級沒有對應的節點，停止");
            return null;
        }

        var node = FindNodeById(addon, nodeId);
        if (node == null)
        {
            Stop($"找不到第 {tier} 級的節點 {nodeId}（節點編號可能因改版變動），停止");
            return null;
        }

        // 🔴 這條型別檢查就是舊版缺的那一條。節點 ID 若因改版漂移到別的元件上，
        //    這裡會擋下來 —— 例如關閉鈕（節點 124）的元件編號是 1001，對不上就不會被點到。
        var expected = nodeId == 22 ? FirstTileComponentId : TileComponentId;
        var actual = (uint)node->Type;
        if (actual != expected)
        {
            Stop($"第 {tier} 級的節點 {nodeId} 型別是 {actual}、預期 {expected}，不敢點，停止");
            return null;
        }

        var button = ((AtkComponentNode*)node)->Component;
        if (button == null)
        {
            Stop($"第 {tier} 級的節點 {nodeId} 沒有元件，停止");
            return null;
        }

        if (!Throttle.Pass("AutoClaimPVPRewards-Claim", 400)) return false;

        // TryClickButton 內部還會依序驗 OwnerNode／IsEnabled／可見性／事件非 null，全部通過才送事件。
        switch (UiHelper.TryClickButton(addon, (AtkComponentButton*)button))
        {
            case UiHelper.ButtonPressResult.Guarded:
                // 🔴 這一扇報酬視窗剛按過、遊戲還沒把它重建（PvpReward 在守衛裡是併鍵的單答窗）：
                //    等，不是停——對關閉中的視窗再點下一格就是這次要擋的崩潰形狀。
                statusText = "等待報酬視窗重建…";
                return false;
            case UiHelper.ButtonPressResult.Unavailable:
                Stop($"第 {tier} 級（節點 {nodeId}）現在不能按，停止");
                return null;
        }

        awaitingConfirmSince = DateTime.UtcNow;
        statusText = $"領取第 {tier} 級…";
        return false;
    }

    public override void DrawConfig()
    {
        ImGui.SetNextItemWidth(200f);
        var stopAt = Config.StopAtTrophyCrystals;
        if (ImGui.SliderInt("戰利水晶到達此數量就停止", ref stopAt, 1000, 20000))
        {
            Config.StopAtTrophyCrystals = Math.Clamp(stopAt, 1000, 20000);
            Plugin.Instance.Config.Save();
        }

        using (ImRaii.PushIndent())
            ImGui.TextDisabled("戰利水晶持有上限是 20000，超過的部分會直接消失，所以預設留一點餘裕。");
    }
}

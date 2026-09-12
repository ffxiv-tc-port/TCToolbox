using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Command;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using Lumina.Excel.Sheets;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 深宮寶箱鎖定：一顆熱鍵（或一個指令）把最近的寶箱設成目標；另可選「新寶箱出現就自動鎖定」。
/// </summary>
/// <remarks>
/// 🔴 <b>絕不跨幀保存原生指標。</b>掃描與設定目標在同一幀內完成；自動模式記的是
/// <c>GameObjectId</c>（受管理的 ulong），每次輪詢重新枚舉。
/// 📌 「在不在深宮」用 <c>EventFramework.GetInstanceContentDeepDungeon()</c> 判，
/// <b>不是</b>寫死一張 TerritoryType 清單——那種清單每出一座新深宮就會靜默失效。
/// </remarks>
public sealed unsafe class DeepDungeonChestTarget : TcModule
{
    public const string Command = "/tcchest";

    public override string InternalName => "DeepDungeonChestTarget";

    public override string DisplayName => "深宮寶箱鎖定";

    public override string Description =>
        $"深宮（死者宮殿／天之御柱／正統優雷卡／巡禮道）裡按一下就把最近的寶箱設成目標。" +
        $"指令 {Command}，也可以綁一顆熱鍵（預設未綁定）。" +
        "另可開啟「新寶箱出現就自動鎖定」（預設關閉，而且不會從敵人身上搶走目標）。";

    public override ModuleCategory Category => ModuleCategory.Combat;

    public override bool HasConfigUI => true;

    /// <summary>
    /// 自動鎖定關著時，這個模組開著也不會自己動。
    /// </summary>
    /// <remarks>
    /// 📌 刻意做成<b>跟著設定走</b>而不是固定值：「手動觸發」那一頁回答的問題是
    /// 「哪些東西是我開著也不會自己亂動的」，而這個模組的答案真的取決於那個勾選框。
    /// </remarks>
    public override bool IsManualTrigger => !Config.AutoTargetNewChests;

    private static DeepDungeonChestTargetConfig Config => Plugin.Instance.Config.DeepDungeonChestTarget;

    private readonly HotkeyWatcher hotkey = new();

    /// <summary>自動模式已經看過的寶箱（<c>GameObjectId</c>，不是指標）。</summary>
    private readonly HashSet<ulong> seenChests = [];

    /// <summary>每次輪詢重新用的暫存集合，避免每格都配置一個新的 HashSet。</summary>
    private readonly HashSet<ulong> visibleChests = [];

    /// <summary>上次輪詢時的區域，用來在換區時清掉「已看過」。</summary>
    private ushort lastTerritory;

    /// <summary>framework 幀計數（自動模式的節流用；刻意不用時間節流器）。</summary>
    private int tick;

    /// <summary>
    /// 只有在第二軸退化時才在列上說話。
    /// </summary>
    /// <remarks>
    /// 🔑 「不知道」要在列上看得見：退化之後找不到銀／金寶箱，而那個差別在遊戲裡的表現是
    /// 「按了熱鍵沒反應」，跟「附近真的沒有寶箱」長得一模一樣。
    /// </remarks>
    public override ModuleNotice? RowNotice
        => string.IsNullOrEmpty(ChestIdentity.DegradedReason)
            ? null
            : new ModuleNotice(ModuleNoticeLevel.Unknown, "? 只找得到銅寶箱", ChestIdentity.DegradedReason);

    protected override void OnEnable()
    {
        ChestIdentity.EnsureBuilt();

        Svc.Commands.AddHandler(Command, new CommandInfo(OnCommand)
        {
            HelpMessage = "把最近的寶箱設成目標（深宮）",
        });

        hotkey.Reset();
        seenChests.Clear();
        lastTerritory = Svc.ClientState.TerritoryType;
        Svc.Framework.Update += OnUpdate;
    }

    protected override void OnDisable()
    {
        Svc.Framework.Update -= OnUpdate;
        hotkey.Reset();
        seenChests.Clear();
        Svc.Commands.RemoveHandler(Command);
    }

    private void OnCommand(string command, string arguments) => TargetNearestChest(announce: true);

    private void OnUpdate(IFramework framework)
    {
        if (hotkey.Poll(Config.Hotkey))
            TargetNearestChest(announce: Config.AnnounceInChat);

        if (!Config.AutoTargetNewChests) return;

        // 🔑 節流用 framework 幀計數，不用時間節流器：這裡只是想「別每幀掃一次物件表」，
        //    而幀計數不會有時間節流器那種「第一次必放行、鍵全域持久」的語意。
        if (++tick < Math.Max(1, Config.AutoPollTicks)) return;
        tick = 0;

        AutoTargetNewChest();
    }

    /// <summary>手動觸發：鎖定最近的寶箱。</summary>
    private void TargetNearestChest(bool announce)
    {
        if (!IsChestAreaActive())
        {
            if (announce)
                Svc.Chat.Print("[TC Toolbox] 目前不在深宮，沒有尋找寶箱。（可在模組設定裡放寬到所有區域。）");
            return;
        }

        var player = Svc.Objects.LocalPlayer;
        if (player == null) return;

        var chest = FindNearestChest(player.Position, out var distance);
        if (chest == null)
        {
            if (announce)
                Svc.Chat.Print($"[TC Toolbox] {Config.MaxDistance:0} 公尺內沒有可鎖定的寶箱。");
            return;
        }

        // 🔴 同一幀內取得、同一幀內使用：不把 IGameObject 或它的位址留到下一幀。
        Svc.Targets.Target = chest;

        if (announce)
            Svc.Chat.Print($"[TC Toolbox] 已鎖定寶箱（{distance:0.0} 公尺）。");
    }

    /// <summary>自動模式：只在「出現了沒看過的寶箱」時動作。</summary>
    private void AutoTargetNewChest()
    {
        var territory = Svc.ClientState.TerritoryType;
        if (territory != lastTerritory)
        {
            lastTerritory = territory;
            seenChests.Clear();
        }

        if (!IsChestAreaActive())
        {
            seenChests.Clear();
            return;
        }

        if (Config.SkipInCombat && Svc.Condition[ConditionFlag.InCombat]) return;

        // 🔴 這一關要擋在掃描**之前**：擋在後面的話，這一批寶箱已經被登記成「已看過」，
        //    等目標空出來以後就再也不會觸發了。
        if (!CanStealTarget()) return;

        var player = Svc.Objects.LocalPlayer;
        if (player == null || player.IsDead) return;

        visibleChests.Clear();

        IGameObject? newest = null;
        var bestDistanceSquared = float.MaxValue;
        var maxDistanceSquared = Config.MaxDistance * Config.MaxDistance;

        foreach (var obj in Svc.Objects)
        {
            if (!IsChest(obj) || !obj.IsTargetable) continue;

            var distanceSquared = Vector3.DistanceSquared(player.Position, obj.Position);

            // 🔴 超出搜尋距離的**不可以**登記成「已看過」：登記了的話，走近之後它就再也不會觸發，
            //    表現成「自動鎖定只對剛好生成在腳邊的箱子有效」。
            if (distanceSquared > maxDistanceSquared) continue;

            var id = obj.GameObjectId;
            visibleChests.Add(id);

            if (seenChests.Contains(id)) continue;
            if (distanceSquared >= bestDistanceSquared) continue;

            bestDistanceSquared = distanceSquared;
            newest = obj;
        }

        // 這一輪「範圍內看得到的」就是新的已看過集合：離開範圍的會被丟掉，所以整趟深宮下來
        // 不會無限長大。副作用是「走遠再走回來」會被當成新的一個——在深宮那反而是想要的行為。
        seenChests.Clear();
        seenChests.UnionWith(visibleChests);

        if (newest == null) return;

        Svc.Targets.Target = newest;
        Svc.Log.Information(
            $"[DeepDungeonChestTarget] 自動鎖定新出現的寶箱（{Math.Sqrt(bestDistanceSquared):0.0} 公尺）。");
    }

    /// <summary>
    /// 現在可以把目標換成寶箱嗎。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>絕不從敵人身上搶走目標</b>——自動鎖定在戰鬥中把目標換成箱子，
    /// 使用者接下來按的每一個技能都會落空，而且他不會馬上意識到發生了什麼事。
    /// 只有「沒有目標」或「目前的目標本來就是個寶箱」才換。
    /// </remarks>
    private bool CanStealTarget()
    {
        var current = Svc.Targets.Target;
        if (current == null) return true;
        if (!current.IsTargetable) return true;
        return IsChest(current);
    }

    private IGameObject? FindNearestChest(Vector3 origin, out float distance)
    {
        IGameObject? best = null;
        var bestDistanceSquared = float.MaxValue;
        var maxDistanceSquared = Config.MaxDistance * Config.MaxDistance;

        foreach (var obj in Svc.Objects)
        {
            if (!IsChest(obj) || !obj.IsTargetable) continue;

            var distanceSquared = Vector3.DistanceSquared(origin, obj.Position);
            if (distanceSquared > maxDistanceSquared) continue;
            if (distanceSquared >= bestDistanceSquared) continue;

            bestDistanceSquared = distanceSquared;
            best = obj;
        }

        distance = best == null ? 0f : MathF.Sqrt(bestDistanceSquared);
        return best;
    }

    /// <summary>這個物件算不算「寶箱」。判準見 <see cref="ChestIdentity"/>。</summary>
    /// <remarks>
    /// 📌 <b>判準已經抽到 <see cref="ChestIdentity"/></b>，與
    /// <see cref="NearbyOnMinimap"/>（把寶箱畫到小地圖）共用同一份對照表。
    /// 那張表是一次 15000 列的整表走訪，而且參考列號是個魔術數字——兩個模組各留一份的話，
    /// 改一邊忘了另一邊的失敗形式是「其中一個功能少認得兩種箱子」，而那完全靜默。
    /// </remarks>
    private static bool IsChest(IGameObject obj) => ChestIdentity.IsChest(obj);

    /// <summary>現在這個區域要不要找寶箱。</summary>
    private static bool IsChestAreaActive() => Config.AnyArea || InDeepDungeon();

    /// <summary>
    /// 在不在深宮。
    /// </summary>
    /// <remarks>
    /// 📌 判準是「遊戲自己有沒有建立深宮的 director」，所以新出的深宮不必更新任何清單。
    /// 🔴 指標只在這個方法裡用完就丟，不跨幀保存。
    /// </remarks>
    private static bool InDeepDungeon()
    {
        var eventFramework = EventFramework.Instance();
        if (eventFramework == null) return false;

        InstanceContentDeepDungeon* deepDungeon = eventFramework->GetInstanceContentDeepDungeon();
        return deepDungeon != null;
    }

    public override void DrawConfig()
    {
        if (HotkeyUi.Draw("DeepDungeonChestTarget", Config.Hotkey))
            Plugin.Instance.Config.Save();

        ImGui.SetNextItemWidth(180f);
        var distance = Config.MaxDistance;
        if (ImGui.SliderFloat("搜尋距離（公尺）", ref distance, 5f, 100f, "%.0f"))
        {
            Config.MaxDistance = distance;
            Plugin.Instance.Config.Save();
        }

        var announce = Config.AnnounceInChat;
        if (ImGui.Checkbox("熱鍵鎖定時在聊天視窗回報", ref announce))
        {
            Config.AnnounceInChat = announce;
            Plugin.Instance.Config.Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("指令觸發一定會回報（打了指令沒有任何反應會讓人以為壞掉了）；這個開關只管熱鍵。");

        var anyArea = Config.AnyArea;
        if (ImGui.Checkbox("不限深宮，任何地方都能用", ref anyArea))
        {
            Config.AnyArea = anyArea;
            Plugin.Instance.Config.Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("打開之後在一般副本也會鎖定寶箱（一般副本的寶箱同樣是 ObjectKind.Treasure）。");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var auto = Config.AutoTargetNewChests;
        if (ImGui.Checkbox("新寶箱出現就自動鎖定", ref auto))
        {
            Config.AutoTargetNewChests = auto;
            Plugin.Instance.Config.Save();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "預設關閉。打開之後，看到沒見過的寶箱就會把它設成目標。\n" +
                "永遠不會從敵人身上搶走目標：只有「現在沒有目標」或「目前的目標本來就是寶箱」才換。");
        }

        if (Config.AutoTargetNewChests)
        {
            using (ImRaii.PushIndent())
            {
                var skipInCombat = Config.SkipInCombat;
                if (ImGui.Checkbox("戰鬥中不自動鎖定", ref skipInCombat))
                {
                    Config.SkipInCombat = skipInCombat;
                    Plugin.Instance.Config.Save();
                }

                ImGui.SetNextItemWidth(180f);
                var ticks = Config.AutoPollTicks;
                if (ImGui.SliderInt("檢查間隔（幀）", ref ticks, 1, 120))
                {
                    Config.AutoPollTicks = ticks;
                    Plugin.Instance.Config.Save();
                }

                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("每隔幾幀掃一次物件表。60 幀大約是一秒（實際取決於畫面更新率）。");
            }
        }

        ImGui.Spacing();
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);

        if (string.IsNullOrEmpty(ChestIdentity.DegradedReason))
        {
            ImGui.TextDisabled(
                $"辨識方式：ObjectKind.Treasure（銅寶箱與一般副本寶箱）"
                + $"＋ EObjName 表裡與第 {ChestIdentity.ReferenceChestEObjNameRow} 列同名的 "
                + $"{ChestIdentity.EventObjIds.Count} 個 EventObj"
                + "（銀／金寶箱、擬態怪的箱子）。名稱一律取自遊戲資料表，程式碼裡沒有寫死的物件名。");
        }
        else
        {
            ImGui.TextDisabled(ChestIdentity.DegradedReason);
        }

        ImGui.PopTextWrapPos();
    }
}

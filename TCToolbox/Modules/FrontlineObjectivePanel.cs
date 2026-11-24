using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Ipc;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 紛爭前線目標面板：把 BossModReborn 轉發的 DirectorUpdate 整理成目標清單（預告倒數、可契約、已占領、剩餘點數、冰 HP、即將失效），附距離、方向與俯視小圖。
/// </summary>
/// <remarks>
/// 🔴 PvP 只顯示、零自動化：不選目標、不移動、不施放。
/// 刻意不自己掛 ActorControl hook：委派參數個數跟 CS 世代綁定，全艦隊只留 BMR 一處，跳世代時才不會漏改一份而傳錯參數。
/// 狀態語意全在 <see cref="FrontlineObjectiveTracker"/>（純類別，離線探針直接重放錄影驗）。
/// </remarks>
public sealed class FrontlineObjectivePanel : TcModule
{
    public const string ModuleInternalName = "FrontlineObjectivePanel";

    private const string BmrInternalName = "BossModReborn";
    private const string IpcName = "BossMod.WorldState.DirectorUpdate";

    public override string InternalName => ModuleInternalName;
    public override string DisplayName => "前線目標面板";

    public override string Description =>
        "紛爭前線（塵封秘岩、榮譽野、昂薩哈凱爾、周邊遺跡群）的目標清單：快要可用的倒數、可契約、已占領與即將失效、石文剩餘點數、冰的 HP，附距離、方向與俯視小圖。"
        + "資料由 BossModReborn 轉發（需要它載入）。只顯示，不選目標、不移動、不施放。";

    public override ModuleCategory Category => ModuleCategory.Combat;

    /// <summary>只畫一扇唯讀面板，開著不按也不會改變任何遊戲行為。</summary>
    public override bool IsManualTrigger => true;

    public override bool HasConfigUI => true;

    private static readonly Vector4 UnknownColor = new(0.68f, 0.68f, 0.68f, 1f);
    private static readonly Vector4 UpcomingColor = new(1f, 0.92f, 0.55f, 1f);
    private static readonly Vector4 AvailableColor = new(0.55f, 0.95f, 0.55f, 1f);
    private static readonly Vector4 ActiveColor = new(1f, 0.65f, 0.25f, 1f);
    private static readonly Vector4 EndedColor = new(0.55f, 0.55f, 0.55f, 1f);

    // 前線三隊的慣用色：黑渦團紅、雙蛇黨黃、不滅隊藍。
    private static readonly Vector4[] TeamColors =
    [
        new(0.95f, 0.35f, 0.35f, 1f),
        new(0.95f, 0.85f, 0.30f, 1f),
        new(0.40f, 0.65f, 1f, 1f),
    ];

    /// <summary>淡色（可能有／預告）的透明度；實色＝1。</summary>
    private const float PossibleAlpha = 0.5f;

    // 🔒 tracker 只在持有 gate 時碰；IPC 回呼與 Draw 目前都在主執行緒，鎖是防日後 BMR 改從別的執行緒送。鎖內不呼叫 ImGui。
    private readonly object gate = new();
    private readonly FrontlineObjectiveTracker tracker = new();
    private ICallGateSubscriber<uint, uint, uint, uint, uint, uint, object>? subscriber;

    private bool windowOpen = true;
    private bool showMap = true;
    private bool showIdle;
    private bool previewOutside;
    private uint lastSeenTerritory = uint.MaxValue;

    private bool? bmrLoaded;
    private long bmrCheckedAt = long.MinValue;
    private bool loggedFirstEvent;
    private bool loggedForeign;

    private static double Now => Environment.TickCount64 / 1000.0;

    protected override void OnEnable()
    {
        lock (gate)
            tracker.Reset(Svc.ClientState.TerritoryType);
        loggedFirstEvent = false;
        loggedForeign = false;
        lastSeenTerritory = uint.MaxValue;
        subscriber = Svc.PluginInterface.GetIpcSubscriber<uint, uint, uint, uint, uint, uint, object>(IpcName);
        subscriber.Subscribe(OnDirectorUpdate);
        Svc.PluginInterface.UiBuilder.Draw += DrawWindow;
    }

    protected override void OnDisable()
    {
        Svc.PluginInterface.UiBuilder.Draw -= DrawWindow;
        subscriber?.Unsubscribe(OnDirectorUpdate);
        subscriber = null;
        lock (gate)
            tracker.Reset(0);
        previewOutside = false;
    }

    /// <summary>BMR 在執行它自己那一幀的 ops 時同步呼叫進來。</summary>
    /// <remarks>🔴 不得擲例外：Dalamud 的 SendMessage 會把例外丟回 BMR 並跳過其他訂閱者。</remarks>
    private void OnDirectorUpdate(uint directorId, uint updateId, uint p1, uint p2, uint p3, uint p4)
    {
        try
        {
            var territory = (uint)Svc.ClientState.TerritoryType;
            bool first;
            lock (gate)
            {
                if (tracker.Territory != territory)
                    tracker.Reset(territory);
                first = tracker.Feed(Now, directorId, updateId, p1, p2, p3, p4) && tracker.EventCount == 1;
            }

            if (first && !loggedFirstEvent)
            {
                loggedFirstEvent = true;
                Svc.Log.Information($"[{ModuleInternalName}] 收到本區（{territory}）第一筆前線目標事件，面板開始有資料。");
            }
        }
        catch (Exception ex)
        {
            if (Throttle.Pass($"{ModuleInternalName}-Feed", 60_000))
                Svc.Log.Information($"[{ModuleInternalName}] 處理 DirectorUpdate 時發生例外，已略過：{ex}");
        }
    }

    public override void DrawConfig()
    {
        if (ImGui.Button(previewOutside ? "關閉面板預覽" : "在前線外也打開面板（預覽）"))
            previewOutside = !previewOutside;
        ImGui.TextDisabled("進入紛爭前線時面板會自己打開；關掉後下次進場再打開。");
        ImGui.TextDisabled("需要 BossModReborn 載入；只顯示，不選目標、不移動、不施放。");
    }

    private bool? BmrLoaded()
    {
        var now = Environment.TickCount64;
        if (now - bmrCheckedAt >= 1000)
        {
            bmrCheckedAt = now;
            bmrLoaded = PluginPresence.IsLoaded(BmrInternalName);
        }

        return bmrLoaded;
    }

    private void DrawWindow()
    {
        if (!Svc.ClientState.IsLoggedIn)
            return;

        var territory = (uint)Svc.ClientState.TerritoryType;
        if (territory != lastSeenTerritory)
        {
            lastSeenTerritory = territory;
            loggedFirstEvent = false;
            loggedForeign = false;
            if (FrontlineObjectiveTracker.MapOf(territory) != FrontlineMap.None)
                windowOpen = true;
        }

        var inFrontline = FrontlineObjectiveTracker.MapOf(territory) != FrontlineMap.None;
        if (!(inFrontline && windowOpen) && !previewOutside)
            return;

        // 先在鎖內抄完要畫的東西，鎖外才碰 ImGui。
        List<FrontlineObjectiveView> rows;
        FrontlineMap map;
        int events, foreign;
        uint lastForeign;
        bool sawStart;
        double? measuredExpire;
        (Vector2 Min, Vector2 Max)? bounds;
        var now = Now;
        lock (gate)
        {
            if (tracker.Territory != territory)
                tracker.Reset(territory);
            rows = tracker.Snapshot(now);
            map = tracker.Map;
            events = tracker.EventCount;
            foreign = tracker.ForeignDirectorCount;
            lastForeign = tracker.LastForeignDirector;
            sawStart = tracker.SawMatchStart;
            measuredExpire = tracker.MeasuredOvooExpireSeconds;
            bounds = tracker.MapBounds();
        }

        if (map != FrontlineMap.None && events == 0 && foreign >= 20 && !loggedForeign)
        {
            loggedForeign = true;
            Svc.Log.Information($"[{ModuleInternalName}] 本區（{territory}）收到 {foreign} 筆 director 0x{lastForeign:X8} 的事件但沒有一筆對得上本圖，面板無資料（可能改版換了 director id）。");
        }

        // 玩家位置每幀現取、只抄值。
        var player = Svc.Objects.LocalPlayer;
        Vector2? playerPos = player is null ? null : new Vector2(player.Position.X, player.Position.Z);
        var playerRot = player?.Rotation ?? 0f;

        ImGui.SetNextWindowSize(UiScale.Px(460, 360), ImGuiCond.FirstUseEver);
        var open = true;
        if (ImGui.Begin($"{DisplayName}###TCToolboxFrontlineObjectives", ref open))
        {
            DrawHeader(map, events, foreign, sawStart, measuredExpire);
            // 刻意不列：連一筆事件都沒有時列出的全是「?」，只會淹掉「無資料」那句。
            if (map != FrontlineMap.None && events > 0)
            {
                DrawRows(rows, playerPos);
                if (bounds is { } b)
                {
                    ImGui.Checkbox("俯視小圖", ref showMap);
                    if (showMap)
                        DrawMiniMap(rows, b, playerPos, playerRot);
                }
            }
        }

        ImGui.End();
        if (!open)
        {
            windowOpen = false;
            previewOutside = false;
        }
    }

    private void DrawHeader(FrontlineMap map, int events, int foreign, bool sawStart, double? measuredExpire)
    {
        var mapName = map switch
        {
            FrontlineMap.SealRock => "塵封秘岩（爭奪戰）",
            FrontlineMap.FieldsOfGlory => "榮譽野（碎冰戰）",
            FrontlineMap.OnsalHakair => "昂薩哈凱爾（競爭戰）",
            FrontlineMap.Secure => "周邊遺跡群（陣地戰）",
            _ => "不在紛爭前線",
        };
        ImGui.TextUnformatted(mapName);

        if (map == FrontlineMap.None)
        {
            ImGui.TextColored(UnknownColor, "無資料");
            UiHelperTooltip("目前不在支援的紛爭前線地圖；進場後才會有資料。");
            return;
        }

        if (events == 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(UnknownColor, "無資料");
            var bmr = BmrLoaded();
            var why = bmr switch
            {
                false => "BossModReborn 沒有載入；本面板的資料由它轉發。",
                null => "問不到插件清單，不確定 BossModReborn 有沒有載入。",
                _ => "還沒收到這場的目標事件：剛進場還沒開打、BossModReborn 版本太舊還沒有這個介面，或它攔遊戲事件的 hook 沒裝成（看它的降級通知）。",
            };
            if (foreign > 0)
                why += $"\n另外收到 {foreign} 筆對不上本圖的事件（可能改版換了編號）。";
            UiHelperTooltip(why);
            return;
        }

        if (!sawStart)
        {
            ImGui.SameLine();
            ImGui.TextColored(UnknownColor, "（中途載入）");
            UiHelperTooltip("比賽開始後才開始收資料：載入前發生的事看不到，那些目標在列上顯示「?」，等它們下一次有動靜就會補上。");
        }

        if (map == FrontlineMap.OnsalHakair)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(measuredExpire is { } m ? $"失效 {m:0.0} 秒（實測）" : "失效約 30 秒（未實測）");
            UiHelperTooltip("敖包占領後多久失效，資料裡沒有；先用觀察值 30 秒，本場量到一次「占領→失效」就改用實測值。");
        }
        else if (map == FrontlineMap.Secure)
        {
            ImGui.SameLine();
            ImGui.TextColored(UnknownColor, "只有據點擁有者");
            UiHelperTooltip("周邊遺跡群目前只解出據點占領與失去；占領進度、無人機、截擊系統尚未支援。");
        }

        if (map == FrontlineMap.FieldsOfGlory)
        {
            ImGui.SameLine();
            ImGui.Checkbox("顯示待機中的冰", ref showIdle);
        }
    }

    private void DrawRows(List<FrontlineObjectiveView> rows, Vector2? playerPos)
    {
        var idle = 0;
        using (var table = ImRaii.Table("##frontlineObjectives", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit))
        {
            if (table)
            {
                ImGui.TableSetupColumn("目標", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("狀態");
                ImGui.TableSetupColumn("進度");
                ImGui.TableSetupColumn("距離");
                ImGui.TableHeadersRow();
                foreach (var v in rows)
                {
                    if (v.Phase == FrontlineObjectivePhase.Idle && !showIdle)
                    {
                        idle++;
                        continue;
                    }

                    DrawRow(v, playerPos);
                }
            }
        }

        if (idle > 0)
            ImGui.TextDisabled($"另有 {idle} 個待機中（未顯示）");
    }

    private void DrawRow(FrontlineObjectiveView v, Vector2? playerPos)
    {
        var color = WithAlpha(PhaseColor(v), v.Confirmed ? 1f : PossibleAlpha);
        ImGui.TableNextRow();

        ImGui.TableNextColumn();
        var rank = v.Rank >= 0 ? $"［{FrontlineObjectiveTracker.RankName(v.Rank)}］" : string.Empty;
        ImGui.TextColored(v.Phase == FrontlineObjectivePhase.Unknown ? UnknownColor : WithAlpha(Vector4.One, v.Confirmed ? 1f : PossibleAlpha), v.Name + rank);
        UiHelperTooltip(v.Detail + (v.Estimated ? "\n倒數是推估值。" : string.Empty));

        ImGui.TableNextColumn();
        ImGui.TextColored(color, v.Status);

        ImGui.TableNextColumn();
        var barWidth = UiScale.Px(120);
        if (v.ProgressUnknown)
        {
            ImGui.TextColored(UnknownColor, "?");
        }
        else if (v.Fraction is { } f)
        {
            var overlay = v.SecondsLeft is { } s ? (v.Estimated ? $"約 {s:0} 秒" : $"{s:0} 秒") : $"{f * 100:0}%";
            using var c = ImRaii.PushColor(ImGuiCol.PlotHistogram, color);
            ImGui.ProgressBar(Math.Clamp(f, 0f, 1f), new Vector2(barWidth, 0), overlay);
        }
        else if (v.SecondsLeft is { } s)
        {
            ImGui.TextColored(color, $"{s:0} 秒");
        }
        else
        {
            ImGui.TextDisabled("—");
        }

        ImGui.TableNextColumn();
        if (v.Position is { } pos && playerPos is { } me)
        {
            var delta = pos - me;
            DrawArrow(delta, UiScale.Px(7));
            ImGui.SameLine();
            ImGui.TextUnformatted($"{delta.Length():0} 米");
        }
        else
        {
            ImGui.TextColored(UnknownColor, "?");
            UiHelperTooltip(v.Position == null ? "這個目標的座標還沒驗過，不顯示距離。" : "讀不到自己的位置。");
        }
    }

    /// <summary>在目前游標處畫一個北朝上的方向箭頭（世界 X＝右、Z＝下，與遊戲地圖同向）。</summary>
    private static void DrawArrow(Vector2 worldDelta, float radius)
    {
        var start = ImGui.GetCursorScreenPos();
        var center = start + new Vector2(radius, ImGui.GetTextLineHeight() * 0.5f);
        var dl = ImGui.GetWindowDrawList();
        var outline = ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.85f));
        var body = ImGui.GetColorU32(Vector4.One);
        if (worldDelta.LengthSquared() < 1f)
        {
            dl.AddCircleFilled(center, radius * 0.4f, body);
        }
        else
        {
            var d = Vector2.Normalize(worldDelta);
            var n = new Vector2(-d.Y, d.X);
            var tip = center + (d * radius);
            var a = center - (d * radius * 0.6f) + (n * radius * 0.6f);
            var b = center - (d * radius * 0.6f) - (n * radius * 0.6f);
            dl.AddTriangleFilled(tip, a, b, body);
            dl.AddTriangle(tip, a, b, outline, 1f);
        }

        ImGui.Dummy(new Vector2(radius * 2, ImGui.GetTextLineHeight()));
    }

    private void DrawMiniMap(List<FrontlineObjectiveView> rows, (Vector2 Min, Vector2 Max) bounds, Vector2? playerPos, float playerRot)
    {
        var size = UiScale.Px(220);
        var origin = ImGui.GetCursorScreenPos();
        ImGui.Dummy(new Vector2(size, size));
        var dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(origin, origin + new Vector2(size, size), ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.35f)));
        dl.AddRect(origin, origin + new Vector2(size, size), ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.25f)));

        var center = (bounds.Min + bounds.Max) * 0.5f;
        var extent = Math.Max(bounds.Max.X - bounds.Min.X, bounds.Max.Y - bounds.Min.Y) * 1.2f + 20f;
        var scale = size / extent;
        Vector2 ToScreen(Vector2 w) => origin + new Vector2(size * 0.5f) + ((w - center) * scale);

        var mouse = ImGui.GetMousePos();
        var hover = ImGui.IsItemHovered();
        FrontlineObjectiveView? hovered = null;
        var radius = UiScale.Px(6);
        var outline = ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.85f));
        foreach (var v in rows)
        {
            if (v.Position is not { } p)
                continue;
            var s = ToScreen(p);
            var alpha = v.Confirmed ? 1f : PossibleAlpha;
            if (v.Phase is FrontlineObjectivePhase.Idle)
            {
                dl.AddCircleFilled(s, radius * 0.35f, ImGui.GetColorU32(WithAlpha(EndedColor, 0.6f)));
                continue;
            }

            var col = ImGui.GetColorU32(WithAlpha(PhaseColor(v), alpha));
            dl.AddCircle(s, radius + 1f, outline, 0, 3f);
            if (v.Phase == FrontlineObjectivePhase.Unknown)
            {
                dl.AddCircle(s, radius, ImGui.GetColorU32(UnknownColor), 0, 1.5f);
            }
            else
            {
                dl.AddCircle(s, radius, ImGui.GetColorU32(WithAlpha(EndedColor, 0.5f)), 0, 2f);
                // 進度環：從正上方順時針，長度＝剩餘比例。
                var f = v.Fraction ?? 1f;
                if (f > 0.01f)
                {
                    dl.PathArcTo(s, radius, -MathF.PI / 2, -MathF.PI / 2 + (MathF.Tau * Math.Clamp(f, 0f, 1f)), 24);
                    dl.PathStroke(col, ImDrawFlags.None, 2.5f);
                }

                dl.AddCircleFilled(s, radius * 0.45f, col);
            }

            if (hover && Vector2.DistanceSquared(mouse, s) <= (radius + 3) * (radius + 3))
                hovered = v;
        }

        if (playerPos is { } me)
        {
            var s = ToScreen(me);
            var d = new Vector2(MathF.Sin(playerRot), MathF.Cos(playerRot));
            var n = new Vector2(-d.Y, d.X);
            var r = UiScale.Px(7);
            var tip = s + (d * r);
            var a = s - (d * r * 0.6f) + (n * r * 0.55f);
            var b = s - (d * r * 0.6f) - (n * r * 0.55f);
            dl.AddTriangleFilled(tip, a, b, ImGui.GetColorU32(Vector4.One));
            dl.AddTriangle(tip, a, b, outline, 1.5f);
        }

        if (hovered != null)
            ImGui.SetTooltip($"{hovered.Name}：{hovered.Status}\n{hovered.Detail}");
    }

    private static Vector4 PhaseColor(FrontlineObjectiveView v)
    {
        if (v.Owner is >= 0 and <= 2)
            return TeamColors[v.Owner];
        return v.Phase switch
        {
            FrontlineObjectivePhase.Upcoming => UpcomingColor,
            FrontlineObjectivePhase.Available => AvailableColor,
            FrontlineObjectivePhase.Active => ActiveColor,
            FrontlineObjectivePhase.Ended => EndedColor,
            FrontlineObjectivePhase.Idle => EndedColor,
            _ => UnknownColor,
        };
    }

    private static Vector4 WithAlpha(Vector4 c, float a) => new(c.X, c.Y, c.Z, c.W * a);

    private static void UiHelperTooltip(string text)
    {
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(text);
    }
}

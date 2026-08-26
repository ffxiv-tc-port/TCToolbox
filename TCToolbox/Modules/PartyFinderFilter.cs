using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text.RegularExpressions;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Gui.PartyFinder.Types;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 招募清單過濾器：招募板上不想看的招募直接隱藏（重複洗版、關鍵字、以及打高難度時「我這職／我這職能已滿」的隊伍）。
/// </summary>
/// <remarks>
/// <para>
/// <b>機制＝消費 Dalamud 官方已解析好的招募資料</b>：訂閱 <see cref="IPartyFinderGui.ReceiveListing"/>，
/// 對每一則招募設定 <c>args.Visible</c>。Dalamud 這個事件<b>只能隱藏、不能竄改</b>招募內容，
/// 也不送任何封包——資料是遊戲自己收下並解析完的，我們只是選擇要不要顯示。
/// 這正是紅線「不做封包偽造／攔截」括號裡的例外（讀取遊戲已解析好的資料不算）。
/// 參考 DailyRoutines <c>PartyFinderFilter</c>（作者 status102）設計重寫，去掉 OmenTools 相依、
/// 補上職能欄位的載入時自我校準。
/// </para>
/// <para><b>四道過濾（各自可關）</b>：</para>
/// <list type="number">
/// <item><b>重複招募</b>：同一批次裡「同一副本＋同一段說明文字」的第二則之後全部隱藏
/// （RMT／代刷常一次貼十幾則一模一樣的）。</item>
/// <item><b>關鍵字</b>：對招募人名稱與說明做正規表示式比對，黑名單（命中就藏）或白名單（只留命中的）。</item>
/// <item><b>高難度：我這職已在隊</b>：打高難度副本時，隊裡已經有一個跟我同職業的就藏
/// （同職通常搶同一件裝，留著也進不去）。</item>
/// <item><b>高難度：我這職能已滿</b>（預設關）：我的職能（坦／純治／盾治／近戰／遠物／遠魔）
/// 在該隊已達設定上限、或該隊沒有一個空位收我這職，就藏。</item>
/// </list>
/// <para>
/// 🔴 <b>職能欄位（<c>ClassJob.Unknown11</c>）在台服 Lumina pin 的對齊未經離線證明。</b>
/// 台服的六分類值是 1坦／2純治／6盾治／3近戰／4遠物／5遠魔，但這個欄位是自動命名的，
/// pin 一動就可能對到別的欄位。所以<b>啟用時自我校準</b>：讀騎士／戰士／暗黑／絕槍（應同為坦）、
/// 白魔／占星（同為純治）、學者／賢者（同為盾治）以及各職能代表職，驗「同組相等、跨組相異、坦≠0」。
/// 校準不過就<b>停用兩道高難度過濾並印一行 <c>Information</c></b>——寧可少過濾，不要靠一個對錯的欄位
/// 把使用者的招募清單默默砍掉。
/// </para>
/// <para>
/// ⚠️ 職業→<see cref="JobFlags"/> 的比對用 <c>NameEnglish</c> 去空白後 <c>Enum.TryParse</c>
/// （White Mage→WhiteMage、Blue Mage→BlueMage…），已逐一核對過台服 <c>ClassJob</c> 表的英文名
/// 與 <see cref="JobFlags"/> 列舉名對得上；對不上的職業會在「空位是否收我」那一步被當成不收（多藏，不會少藏成誤入）。
/// </para>
/// </remarks>
public sealed class PartyFinderFilter : TcModule
{
    public override string InternalName => "PartyFinderFilter";
    public override string DisplayName => "招募清單過濾器";

    public override string Description =>
        "招募板上把不想看的招募直接隱藏：重複洗版、關鍵字（黑／白名單）、以及打高難度時「同職已在隊」「我職能已滿」的隊伍。" +
        "只消費 Dalamud 已解析好的招募資料設定顯示與否，不掛 hook、不送封包。";

    public override ModuleCategory Category => ModuleCategory.Combat;

    public override bool HasConfigUI => true;

    private const string PartyFinderAddon = "LookingForGroup";

    // 六分類的正規順序（設定陣列與 UI 都照這個順序），與 DR 的索引對應一致。
    private const int RoleTank = 0;
    private const int RolePureHealer = 1;
    private const int RoleShieldHealer = 2;
    private const int RoleMelee = 3;
    private const int RolePhysRanged = 4;
    private const int RoleMagicRanged = 5;

    private static readonly string[] RoleLabels =
        ["坦克", "純治療", "盾職治療", "近戰", "遠程物理", "遠程魔法"];

    private PartyFinderFilterConfig Config => Plugin.Instance.Config.PartyFinderFilter;

    // ── 載入時自我校準出來的職能欄位值（canonical index → 該職能在台服 Unknown11 的實際值）。
    private bool roleCalibrated;
    private readonly byte[] calibratedRoleValue = new byte[6];

    // ── 每批次的上下文（DR 的作法：從該批第一則招募判定，整批共用）。
    private int currentBatch = -1;
    private bool batchIsSecret;
    private bool batchIsHighEnd;
    private readonly HashSet<(ushort Duty, string Text)> batchDescriptions = [];

    // ── 已編譯好的正規表示式（設定改動時重建；編譯失敗的規則跳過）。
    private readonly List<Regex> compiledRegex = [];

    private int lastHiddenCount;

    protected override void OnEnable()
    {
        NormalizeConfig();
        CalibrateRoles();
        RebuildRegex();

        Svc.PartyFinder.ReceiveListing += OnReceiveListing;
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PreSetup, PartyFinderAddon, OnAddonSetup);

        currentBatch = -1;
        batchDescriptions.Clear();
    }

    protected override void OnDisable()
    {
        Svc.PartyFinder.ReceiveListing -= OnReceiveListing;
        Svc.AddonLifecycle.UnregisterListener(OnAddonSetup);

        batchDescriptions.Clear();
        compiledRegex.Clear();
        currentBatch = -1;
    }

    private void OnAddonSetup(AddonEvent type, AddonArgs args)
    {
        // 招募板重開時把批次上下文清掉，下一批重新判定。
        currentBatch = -1;
        batchDescriptions.Clear();
    }

    /// <summary>設定陣列長度／值域正規化（舊設定檔或手動亂改都可能給出壞陣列）。</summary>
    private void NormalizeConfig()
    {
        if (Config.RoleCaps is not { Length: 6 })
            Config.RoleCaps = [2, 1, 1, 2, 1, 2];
    }

    /// <summary>
    /// 載入時校準 <c>ClassJob.Unknown11</c> 到六職能。
    /// </summary>
    /// <remarks>
    /// 讀一組已知職業，驗「同組相等、跨組相異、坦≠0」。任何一項不成立即判為欄位對齊失敗，
    /// <see cref="roleCalibrated"/> 保持 <c>false</c>，高難度職能過濾整組停用。
    /// </remarks>
    private void CalibrateRoles()
    {
        roleCalibrated = false;
        try
        {
            var sheet = Svc.Data.GetExcelSheet<ClassJob>();
            if (sheet == null) return;

            byte? Role(uint id) => sheet.GetRowOrDefault(id)?.Unknown11;

            // 每個職能取兩個代表職，兩者必須相等。
            var pld = Role(19); var war = Role(21); var drk = Role(32); var gnb = Role(37); // 坦
            var whm = Role(24); var ast = Role(33);                                          // 純治
            var sch = Role(28); var sge = Role(40);                                          // 盾治
            var drg = Role(22);                                                              // 近戰
            var brd = Role(23);                                                              // 遠物
            var blm = Role(25);                                                              // 遠魔

            if (pld is null || whm is null || sch is null || drg is null || brd is null || blm is null)
            {
                Svc.Log.Information($"[{InternalName}] 職能欄位校準失敗：讀不到代表職的 ClassJob 列，停用高難度職能過濾。");
                return;
            }

            bool Same(byte? a, byte? b) => a is not null && b is not null && a.Value == b.Value;
            if (!Same(pld, war) || !Same(pld, drk) || !Same(pld, gnb) ||
                !Same(whm, ast) || !Same(sch, sge))
            {
                Svc.Log.Information($"[{InternalName}] 職能欄位校準失敗：同職能的代表職值不一致（Unknown11 疑似對到別的欄位），停用高難度職能過濾。");
                return;
            }

            Span<byte> vals = [pld.Value, whm.Value, sch.Value, drg.Value, brd.Value, blm.Value];
            for (var i = 0; i < 6; i++)
            {
                for (var j = i + 1; j < 6; j++)
                {
                    if (vals[i] == vals[j])
                    {
                        Svc.Log.Information($"[{InternalName}] 職能欄位校準失敗：六職能的值不互異，停用高難度職能過濾。");
                        return;
                    }
                }
            }

            if (pld.Value == 0)
            {
                Svc.Log.Information($"[{InternalName}] 職能欄位校準失敗：坦克職能值為 0（0 代表非戰鬥職），停用高難度職能過濾。");
                return;
            }

            calibratedRoleValue[RoleTank] = pld.Value;
            calibratedRoleValue[RolePureHealer] = whm.Value;
            calibratedRoleValue[RoleShieldHealer] = sch.Value;
            calibratedRoleValue[RoleMelee] = drg.Value;
            calibratedRoleValue[RolePhysRanged] = brd.Value;
            calibratedRoleValue[RoleMagicRanged] = blm.Value;
            roleCalibrated = true;

            Svc.Log.Information(
                $"[{InternalName}] 職能欄位校準成功：坦={pld.Value} 純治={whm.Value} 盾治={sch.Value} " +
                $"近戰={drg.Value} 遠物={brd.Value} 遠魔={blm.Value}。");
        }
        catch (Exception ex)
        {
            Svc.Log.Information($"[{InternalName}] 職能欄位校準時發生例外，停用高難度職能過濾：{ex.Message}");
            roleCalibrated = false;
        }
    }

    private void RebuildRegex()
    {
        compiledRegex.Clear();
        foreach (var rule in Config.RegexRules)
        {
            if (!rule.Enabled || string.IsNullOrEmpty(rule.Pattern)) continue;
            try
            {
                compiledRegex.Add(new Regex(rule.Pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant));
            }
            catch (ArgumentException)
            {
                // 壞的規則跳過（設定畫面編輯時已即時驗過，這裡是最後一道）。
            }
        }
    }

    /// <summary>canonical index → 該職能上限（-1＝忽略）。</summary>
    private int CapOf(int canonicalRole) =>
        canonicalRole is >= 0 and < 6 ? Config.RoleCaps[canonicalRole] : -1;

    /// <summary>Unknown11 的實際值 → canonical index（校準過才有意義；找不到回 -1）。</summary>
    private int CanonicalOf(byte roleValue)
    {
        for (var i = 0; i < 6; i++)
            if (calibratedRoleValue[i] == roleValue) return i;
        return -1;
    }

    private void OnReceiveListing(IPartyFinderListing listing, IPartyFinderListingEventArgs args)
    {
        try
        {
            // 每一「批次」是招募板一次刷新收到的一整組招募。批次換了就重算上下文。
            if (args.BatchNumber != currentBatch)
            {
                currentBatch = args.BatchNumber;
                batchIsSecret = listing.SearchArea.HasFlag(SearchAreaFlags.Private);
                batchIsHighEnd = listing.Category == DutyCategory.HighEndDuty;
                batchDescriptions.Clear();
            }

            // 私人／密碼搜尋區整批不過濾（那是使用者自己指定要看的一小撮）。
            if (batchIsSecret) return;

            var visible = args.Visible;
            visible &= FilterSameDescription(listing);
            visible &= FilterRegex(listing);
            visible &= FilterHighEndSameJob(listing);
            visible &= FilterHighEndRoleCount(listing);

            if (!visible && args.Visible) lastHiddenCount++;
            args.Visible = visible;
        }
        catch (Exception ex)
        {
            // 這是在 Dalamud 事件路徑上被呼叫，擲例外會擾亂整條招募解析——一律吞掉並放行該則。
            Svc.Log.Error(ex, $"[{InternalName}] 過濾招募時發生例外，放行該則。");
        }
    }

    /// <summary>回 <c>true</c>＝這則要顯示。</summary>
    private bool FilterSameDescription(IPartyFinderListing listing)
    {
        if (!Config.FilterSameDescription) return true;

        var text = listing.Description.TextValue;
        if (string.IsNullOrWhiteSpace(text)) return true;

        // Add 回 false ＝這批已經有一模一樣的（同副本＋同說明），隱藏第二則之後。
        return batchDescriptions.Add((listing.RawDuty, text));
    }

    private bool FilterRegex(IPartyFinderListing listing)
    {
        if (compiledRegex.Count == 0) return true;

        var name = listing.Name.TextValue;
        var desc = listing.Description.TextValue;

        var matched = false;
        foreach (var rx in compiledRegex)
        {
            if (rx.IsMatch(name) || rx.IsMatch(desc)) { matched = true; break; }
        }

        // 白名單：命中才留；黑名單：命中就藏。
        return Config.RegexIsWhitelist ? matched : !matched;
    }

    private bool FilterHighEndSameJob(IPartyFinderListing listing)
    {
        if (!Config.HighEndFilterSameJob || !batchIsHighEnd) return true;

        var me = Svc.Objects.LocalPlayer;
        if (me == null) return true;

        var myJobId = me.ClassJob.RowId;
        if (myJobId == 0) return true;

        foreach (var jp in listing.JobsPresent)
        {
            if (jp.RowId == myJobId) return false; // 同職已在隊 → 藏
        }

        return true;
    }

    /// <summary>
    /// 高難度職能上限過濾。
    /// </summary>
    /// <remarks>
    /// 忠實移植 DR 的判斷：對「我的職能」數該隊已填的同職能人數，若已達上限、或該隊沒有一個空位收我這職，
    /// 就藏。校準未通過時整段跳過（回 true）。
    /// </remarks>
    private bool FilterHighEndRoleCount(IPartyFinderListing listing)
    {
        if (!Config.HighEndFilterRoleCount || !batchIsHighEnd || !roleCalibrated) return true;

        var me = Svc.Objects.LocalPlayer;
        if (me == null) return true;

        var myJob = me.ClassJob.ValueNullable;
        if (myJob == null) return true;

        var myCanonical = CanonicalOf(myJob.Value.Unknown11);
        if (myCanonical < 0) return true; // 非戰鬥職／未知職能 → 不過濾

        var cap = CapOf(myCanonical);
        if (cap < 0) return true; // 這個職能設為忽略

        var myFlag = JobToFlag(myJob.Value);

        var slots = new List<PartyFinderSlot>(listing.Slots);
        var present = new List<Lumina.Excel.RowRef<ClassJob>>(listing.JobsPresent);

        var count = 0;
        var hasOpenSlotForMe = false;

        var n = Math.Min(slots.Count, present.Count);
        for (var i = 0; i < n && i < 8; i++)
        {
            if (count >= cap) break;

            var jp = present[i].ValueNullable;
            if (jp is { RowId: not 0 })
            {
                if (jp.Value.Unknown11 == calibratedRoleValue[myCanonical]) count++;
            }
            else if (!hasOpenSlotForMe && myFlag is { } f && slots[i][f])
            {
                hasOpenSlotForMe = true;
            }
        }

        // DR 語意：有空位收我這職、而且我這職能還沒滿 → 顯示；否則藏。
        return count < cap && hasOpenSlotForMe;
    }

    /// <summary>職業 → <see cref="JobFlags"/>（對不上回 null，呼叫端會當成「這個空位不收我」）。</summary>
    private static JobFlags? JobToFlag(ClassJob job)
    {
        var name = job.NameEnglish.ExtractText().Replace(" ", string.Empty);
        return Enum.TryParse<JobFlags>(name, out var flag) ? flag : null;
    }

    public override void DrawConfig()
    {
        var dedup = Config.FilterSameDescription;
        if (ImGui.Checkbox("隱藏重複招募（同副本＋同說明）", ref dedup))
        {
            Config.FilterSameDescription = dedup;
            Plugin.Instance.Config.Save();
        }

        using (ImRaii.PushIndent())
            ImGui.TextDisabled("一次貼十幾則一模一樣的（常見於代刷／RMT）只留第一則。");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextColored(new Vector4(0.6f, 0.8f, 1f, 1f), "高難度副本專屬");

        var sameJob = Config.HighEndFilterSameJob;
        if (ImGui.Checkbox("隱藏「已經有跟我同職業」的隊伍", ref sameJob))
        {
            Config.HighEndFilterSameJob = sameJob;
            Plugin.Instance.Config.Save();
        }

        var roleCount = Config.HighEndFilterRoleCount;
        using (ImRaii.Disabled(!roleCalibrated))
        {
            if (ImGui.Checkbox("隱藏「我這職能已滿／沒有空位收我」的隊伍", ref roleCount))
            {
                Config.HighEndFilterRoleCount = roleCount;
                Plugin.Instance.Config.Save();
            }
        }

        if (!roleCalibrated)
        {
            using (ImRaii.PushIndent())
                ImGui.TextColored(new Vector4(1f, 0.8f, 0.35f, 1f),
                                  "職能欄位在這一版對不上（已寫入記錄），此過濾停用。");
        }
        else if (Config.HighEndFilterRoleCount)
        {
            using (ImRaii.PushIndent())
            {
                ImGui.TextDisabled("各職能上限（-1＝不看這個職能）：");
                for (var i = 0; i < 6; i++)
                {
                    ImGui.SetNextItemWidth(80f);
                    var v = Config.RoleCaps[i];
                    if (ImGui.InputInt($"{RoleLabels[i]}##roleCap{i}", ref v))
                    {
                        Config.RoleCaps[i] = Math.Clamp(v, -1, 8);
                    }

                    if (ImGui.IsItemDeactivatedAfterEdit())
                        Plugin.Instance.Config.Save();
                }
            }
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextColored(new Vector4(0.6f, 0.8f, 1f, 1f), "關鍵字過濾（正規表示式，比對招募人名稱＋說明）");

        var isWhite = Config.RegexIsWhitelist;
        if (ImGui.RadioButton("黑名單（命中就藏）", !isWhite)) { Config.RegexIsWhitelist = false; RebuildRegex(); Plugin.Instance.Config.Save(); }
        ImGui.SameLine();
        if (ImGui.RadioButton("白名單（只留命中的）", isWhite)) { Config.RegexIsWhitelist = true; RebuildRegex(); Plugin.Instance.Config.Save(); }

        if (ImGui.Button("新增一條"))
        {
            Config.RegexRules.Add(new PartyFinderRegexRule());
            Plugin.Instance.Config.Save();
        }

        var toRemove = -1;
        for (var i = 0; i < Config.RegexRules.Count; i++)
        {
            var rule = Config.RegexRules[i];

            var enabled = rule.Enabled;
            if (ImGui.Checkbox($"##rxOn{i}", ref enabled))
            {
                rule.Enabled = enabled;
                RebuildRegex();
                Plugin.Instance.Config.Save();
            }

            ImGui.SameLine();
            ImGui.SetNextItemWidth(280f);
            var pattern = rule.Pattern;
            if (ImGui.InputText($"##rx{i}", ref pattern, 500))
                rule.Pattern = pattern;

            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                if (IsValidRegex(rule.Pattern))
                {
                    RebuildRegex();
                    Plugin.Instance.Config.Save();
                }
                else
                {
                    ImGui.SameLine();
                    ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), "格式錯誤");
                }
            }

            ImGui.SameLine();
            if (ImGui.Button($"刪除##rxDel{i}")) toRemove = i;
        }

        if (toRemove >= 0)
        {
            Config.RegexRules.RemoveAt(toRemove);
            RebuildRegex();
            Plugin.Instance.Config.Save();
        }

        if (lastHiddenCount > 0)
        {
            ImGui.Spacing();
            ImGui.TextDisabled($"自啟用以來已隱藏 {lastHiddenCount} 則。");
        }
    }

    private static bool IsValidRegex(string pattern)
    {
        if (string.IsNullOrEmpty(pattern)) return true;
        try { _ = new Regex(pattern); return true; }
        catch (ArgumentException) { return false; }
    }
}

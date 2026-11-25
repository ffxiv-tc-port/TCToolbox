using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Command;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;
using TCToolbox.Core;
using SafeMemory = Dalamud.SafeMemory;
using ValueType = FFXIVClientStructs.FFXIV.Component.GUI.ValueType;

namespace TCToolbox.Modules;

// 本檔假設與契約：
// - hook 的是紛爭前線結算處理函式 (director, 結算資料) → void；只讀、原函式每次都照常呼叫且只呼叫一次。
// - 讀原生記憶體只有兩處：detour 裡依人數有界複製結算緩衝區、FrontlineRecord 的 PostSetup 讀 AtkValues。兩處都在框架執行緒、當場抄成 managed 資料，不留指標。
// - detour 裡的原生讀取一律 SafeMemory（讀不到回 false）、不直接解參；讀不到＝這場不記，原函式照常呼叫。
// - 解析、寫檔、載入報表都在背景；失敗各記一次。

/// <summary>前線戰績：每場紛爭前線結算時記一份 JSON＋一行摘要，並提供報表視窗。只讀、零自動化。</summary>
public sealed unsafe partial class PvpFrontlineRecords : TcModule
{
    public override string InternalName => "PvpFrontlineRecords";
    public override string DisplayName => "前線戰績";

    public override string Description =>
        "紛爭前線每場結算時，把全部玩家的計分（職業、隊伍、擊倒／死亡／協助、傷害、治療、占領）與三隊分數存成一份紀錄，" +
        "並在 Dalamud 記錄檔寫一行不含角色名的摘要。報表視窗可看最近一場的計分板、依地圖與職業的彙總，也能匯出 CSV。" +
        "只記錄與顯示，不會替你做任何操作。";

    public override ModuleCategory Category => ModuleCategory.Combat;

    public override bool HasConfigUI => true;

    public const string Command = "/tcpvprecords";

    /// <summary>台服 7.30 唯一命中；長版是為了改版後寧可不中也不要誤中別的函式。</summary>
    private const string ResultHandlerSignature =
        "4C 8B DC 55 56 57 41 56 49 8D AB ?? ?? ?? ?? 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 85 ?? ?? ?? ?? " +
        "48 8B 3D ?? ?? ?? ?? 4C 8B F2 48 89 7D ?? 48 8B F1 48 85 FF 0F 84 ?? ?? ?? ?? 0F B6 42";

    private const string ResultAddonName = "FrontlineRecord";
    private const uint FrontlineIntendedUse = 18;
    private const int MaxAddonValues = 1024;
    private const ulong MinUserAddress = 0x10000;
    private const ulong MaxUserAddress = 0x00007FFFFFFFFFFF;
    private static readonly TimeSpan PairWindow = TimeSpan.FromMinutes(5);

    private delegate void ResultHandlerDelegate(nint director, nint data);

    /// <summary>建立後就不再設回 null：detour 要靠它呼叫原函式（停用後走 <c>OriginalDisposeSafe</c>）。</summary>
    private Hook<ResultHandlerDelegate>? hook;

    private volatile bool capturing;
    private bool sigResolved;
    private bool sigChecked;

    private uint cachedTerritory = uint.MaxValue;
    private bool cachedIsFrontline;
    private uint cachedCfc;
    private string cachedMapName = string.Empty;

    private int badCountLogged;
    private int captureFailLogged;
    private int writeFailLogged;
    private int addonFailLogged;
    private DateTime lastReadFailLogUtc = DateTime.MinValue;

    // 只在框架執行緒讀寫：同場紀錄與結算視窗快照互相指認。
    private string? lastRecordStem;
    private DateTime lastRecordUtc;
    private uint lastRecordTerritory;
    private string? pendingAddonFile;
    private DateTime pendingAddonUtc;
    private uint pendingAddonTerritory;

    private IReadOnlyDictionary<int, string> jobAbbr = new Dictionary<int, string>();
    private string recordsDir = string.Empty;
    private string pluginVersion = string.Empty;
    private PvpFrontlineRecordsWindow? window;

    public override ModuleNotice? RowNotice
    {
        get
        {
            if (!IsEnabled) return null;
            if (sigChecked && !sigResolved)
                return new ModuleNotice(ModuleNoticeLevel.Warning, "這版遊戲找不到結算函式，不會記新場次",
                    "結算處理函式的特徵碼沒有命中，多半是遊戲更新過了。\n已經存下的紀錄照樣能在報表裡看；新場次要等插件更新。");
            return null;
        }
    }

    protected override void OnEnable()
    {
        recordsDir = Path.Combine(Svc.PluginInterface.GetPluginConfigDirectory(), "PvpRecords");
        pluginVersion = typeof(PvpFrontlineRecords).Assembly.GetName().Version?.ToString() ?? string.Empty;
        jobAbbr = BuildJobAbbreviations();
        ResetState();

        window = new PvpFrontlineRecordsWindow(this);
        Plugin.Instance.WindowSystem.AddWindow(window);
        Svc.Commands.AddHandler(Command, new CommandInfo(OnCommand) { HelpMessage = "開啟「前線戰績」報表" });
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, ResultAddonName, OnResultAddonSetup);

        sigChecked = true;
        if (!Svc.SigScanner.TryScanText(ResultHandlerSignature, out var address) || address == nint.Zero)
        {
            sigResolved = false;
            Svc.Log.Information($"[{InternalName}] 這版遊戲找不到前線結算處理函式（特徵碼未命中），不會記錄新場次；報表照常可看。");
            return;
        }

        sigResolved = true;
        hook = Svc.Hooks.HookFromAddress<ResultHandlerDelegate>(address, ResultHandlerDetour);
        capturing = true;
        hook.Enable();
        Svc.Log.Information($"[{InternalName}] 已掛上前線結算處理函式（ffxiv_dx11.exe+0x{address - Svc.SigScanner.Module.BaseAddress:X}）。");
    }

    protected override void OnDisable()
    {
        capturing = false;
        hook?.Dispose();

        Svc.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, ResultAddonName, OnResultAddonSetup);
        Svc.Commands.RemoveHandler(Command);

        if (window != null)
        {
            // 插件卸載時 Plugin.Dispose 先 RemoveAllWindows 才停用模組；對不在清單裡的視窗 RemoveWindow 會擲例外。
            if (Plugin.Instance.WindowSystem.Windows.Contains(window))
                Plugin.Instance.WindowSystem.RemoveWindow(window);
            window.IsOpen = false;
            window = null;
        }

        sigChecked = false;
        ResetState();
    }

    private void ResetState()
    {
        cachedTerritory = uint.MaxValue;
        cachedIsFrontline = false;
        lastRecordStem = null;
        pendingAddonFile = null;
    }

    private void OnCommand(string command, string args) => OpenReport();

    public void OpenReport()
    {
        if (window != null) window.IsOpen = true;
    }

    private void ResultHandlerDetour(nint director, nint data)
    {
        try
        {
            if (capturing) TryCapture(data);
        }
        catch (Exception ex)
        {
            if (Interlocked.Exchange(ref captureFailLogged, 1) == 0)
                Svc.Log.Warning(ex, $"[{InternalName}] 擷取結算資料失敗（本次載入只記這一次），這場不記錄。");
        }

        // 原函式不動緩衝區，所以先複製再呼叫：複製的就是原函式看到的那一份。
        hook!.OriginalDisposeSafe(director, data);
    }

    private void TryCapture(nint data)
    {
        if (!IsUserAddress(data)) return;

        var territory = (uint)Svc.ClientState.TerritoryType;
        if (!IsFrontline(territory)) return;

        // 版面長度是反組譯推的、沒實機量過：讀不到的頁在 detour 裡直接解參＝AVE（try/catch 攔不到），所以一律走 ReadProcessMemory。
        if (!SafeMemory.ReadBytes(data + FrontlineResultLayout.PlayerCountOffset, 1, out var countByte))
        {
            LogReadFailure("人數欄位讀不到", null, 1);
            return;
        }

        int count = countByte[0];
        if (!FrontlineResultLayout.IsPlausibleCount(count))
        {
            if (Interlocked.Exchange(ref badCountLogged, 1) == 0)
                Svc.Log.Information($"[{InternalName}] 結算資料的人數是 {count}（應為 1～{FrontlineResultLayout.MaxPlayers}），不記錄；本次載入只記這一次。");
            return;
        }

        var length = FrontlineResultLayout.BufferLength(count);
        if (!SafeMemory.ReadBytes(data, length, out var buffer))
        {
            LogReadFailure("結算資料比推定的長度短（有一段讀不到）", count, length);
            return;
        }

        var now = DateTimeOffset.Now;
        var stem = FrontlineRecordJson.Stem(now, territory);
        var ctx = new FrontlineCaptureContext(now, territory, cachedCfc, cachedMapName,
            Svc.PlayerState.ContentId, Svc.PlayerState.CharacterName ?? string.Empty, Svc.PlayerState.HomeWorld.RowId, pluginVersion);

        string? addonFile = null;
        if (pendingAddonFile != null && DateTime.UtcNow - pendingAddonUtc < PairWindow && pendingAddonTerritory == territory)
            addonFile = pendingAddonFile;
        pendingAddonFile = null;

        lastRecordStem = stem;
        lastRecordUtc = DateTime.UtcNow;
        lastRecordTerritory = territory;

        var jobs = jobAbbr;
        var dir = recordsDir;
        _ = Task.Run(() => WriteRecord(buffer, ctx, stem, addonFile, jobs, dir));
    }

    /// <summary>只在框架執行緒呼叫；隔 <see cref="PairWindow"/> 才再記一次＝每場最多一行（一場前線遠長於它）。</summary>
    private void LogReadFailure(string reason, int? count, int length)
    {
        var now = DateTime.UtcNow;
        if (now - lastReadFailLogUtc < PairWindow) return;
        lastReadFailLogUtc = now;
        Svc.Log.Information($"[{InternalName}] 這場不記錄：{reason}（人數 {count?.ToString() ?? "?"}、要讀 0x{length:X} 位元組）。");
    }

    private bool IsFrontline(uint territory)
    {
        if (territory == cachedTerritory) return cachedIsFrontline;

        cachedTerritory = territory;
        var row = Svc.Data.GetExcelSheet<TerritoryType>().GetRowOrDefault(territory);
        cachedIsFrontline = row?.TerritoryIntendedUse.RowId == FrontlineIntendedUse;
        cachedCfc = row?.ContentFinderCondition.RowId ?? 0;
        cachedMapName = cachedCfc != 0
            ? Svc.Data.GetExcelSheet<ContentFinderCondition>().GetRowOrDefault(cachedCfc)?.Name.ExtractText() ?? string.Empty
            : string.Empty;
        return cachedIsFrontline;
    }

    private void WriteRecord(byte[] buffer, FrontlineCaptureContext ctx, string stem, string? addonFile,
        IReadOnlyDictionary<int, string> jobs, string dir)
    {
        try
        {
            var record = FrontlineResultParser.Parse(buffer, ctx);
            record.AddonFile = addonFile;
            var path = WriteAtomic(dir, ctx.CapturedLocal, stem, ".json", FrontlineRecordJson.Serialize(record));
            Svc.Log.Information($"[{InternalName}] {FrontlineSummary.Line(record, id => JobAbbr(jobs, id), Path.GetFileName(path))}");
            window?.MarkDirty();
        }
        catch (Exception ex)
        {
            if (Interlocked.Exchange(ref writeFailLogged, 1) == 0)
                Svc.Log.Warning(ex, $"[{InternalName}] 解析或寫入前線紀錄失敗（本次載入只記這一次）。");
        }
    }

    /// <summary>先寫 <c>.tmp</c> 再改名；同名已存在就加 <c>-2</c>、<c>-3</c>…，不覆蓋。</summary>
    internal static string WriteAtomic(string root, DateTimeOffset local, string stem, string suffix, byte[] bytes)
    {
        var dir = Path.Combine(root, local.Year.ToString("0000"));
        Directory.CreateDirectory(dir);

        var final = Path.Combine(dir, stem + suffix);
        for (var n = 2; File.Exists(final); n++)
            final = Path.Combine(dir, $"{stem}-{n}{suffix}");

        var tmp = final + ".tmp";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes);
                fs.Flush(true);
            }

            File.Move(tmp, final, false);
            return final;
        }
        catch
        {
            try { File.Delete(tmp); } catch { /* 刪不掉的 .tmp 會被備份排除，留著無害。 */ }
            throw;
        }
    }

    private void OnResultAddonSetup(AddonEvent type, AddonArgs args)
    {
        try
        {
            if (args is not AddonSetupArgs setup) return;

            var territory = (uint)Svc.ClientState.TerritoryType;
            if (!IsFrontline(territory)) return;

            var snapshot = new FrontlineAddonSnapshot
            {
                CapturedLocal = DateTimeOffset.Now,
                TerritoryId = territory,
                AddonName = args.AddonName,
                DeclaredCount = setup.AtkValueCount,
                Values = SnapshotValues(setup.AtkValues, setup.AtkValueCount),
            };

            var stem = FrontlineRecordJson.Stem(snapshot.CapturedLocal, territory);
            if (lastRecordStem != null && DateTime.UtcNow - lastRecordUtc < PairWindow && lastRecordTerritory == territory)
            {
                snapshot.RecordFile = lastRecordStem + ".json";
            }
            else
            {
                pendingAddonFile = stem + FrontlineRecordJson.AddonSuffix;
                pendingAddonUtc = DateTime.UtcNow;
                pendingAddonTerritory = territory;
            }

            var dir = recordsDir;
            _ = Task.Run(() => WriteAddonSnapshot(snapshot, stem, dir));
        }
        catch (Exception ex)
        {
            if (Interlocked.Exchange(ref addonFailLogged, 1) == 0)
                Svc.Log.Warning(ex, $"[{InternalName}] 讀取結算視窗的數值失敗（本次載入只記這一次）。");
        }
    }

    private void WriteAddonSnapshot(FrontlineAddonSnapshot snapshot, string stem, string dir)
    {
        try
        {
            WriteAtomic(dir, snapshot.CapturedLocal, stem, FrontlineRecordJson.AddonSuffix, FrontlineRecordJson.Serialize(snapshot));
        }
        catch (Exception ex)
        {
            if (Interlocked.Exchange(ref addonFailLogged, 1) == 0)
                Svc.Log.Warning(ex, $"[{InternalName}] 寫入結算視窗數值旁檔失敗（本次載入只記這一次）。");
        }
    }

    private static List<FrontlineAddonValue> SnapshotValues(nint pointer, uint declared)
    {
        var list = new List<FrontlineAddonValue>();
        if (declared == 0 || !IsUserAddress(pointer)) return list;

        var values = (AtkValue*)pointer;
        var count = (int)Math.Min(declared, MaxAddonValues);
        for (var i = 0; i < count; i++)
        {
            var v = values + i;
            var type = v->Type;
            if (type == ValueType.Undefined) continue;
            list.Add(new FrontlineAddonValue { Index = i, Type = (int)type, Value = FormatValue(v) });
        }

        return list;
    }

    private static string FormatValue(AtkValue* v) => (v->Type & ValueType.TypeMask) switch
    {
        ValueType.Bool => (v->Byte != 0).ToString(),
        ValueType.Int => v->Int.ToString(),
        ValueType.Int64 => v->Int64.ToString(),
        ValueType.UInt => v->UInt.ToString(),
        ValueType.UInt64 => v->UInt64.ToString(),
        ValueType.Float => v->Float.ToString("R"),
        ValueType.String or ValueType.String8 => ReadUtf8(v->String.Value),
        ValueType.WideString => v->WideString == null || !IsUserAddress((nint)v->WideString) ? "(null)" : new string(v->WideString),
        ValueType.Undefined or ValueType.Null => string.Empty,
        _ => $"0x{(nint)v->Pointer:X}",
    };

    private static string ReadUtf8(byte* text)
    {
        if (text == null || !IsUserAddress((nint)text)) return "(null)";
        return new ReadOnlySeStringSpan(MemoryMarshal.CreateReadOnlySpanFromNullTerminated(text)).ExtractText();
    }

    private static bool IsUserAddress(nint address) => (ulong)address is >= MinUserAddress and <= MaxUserAddress;

    private static IReadOnlyDictionary<int, string> BuildJobAbbreviations()
    {
        var map = new Dictionary<int, string>();
        foreach (var job in Svc.Data.GetExcelSheet<ClassJob>())
            map[(int)job.RowId] = job.Abbreviation.ExtractText();
        return map;
    }

    internal static string JobAbbr(IReadOnlyDictionary<int, string> jobs, int id) =>
        jobs.TryGetValue(id, out var s) && s.Length > 0 ? s : $"?{id}";

    internal string JobAbbr(int id) => JobAbbr(jobAbbr, id);

    internal string RecordsDirectory => recordsDir;

    internal bool HookResolved => sigChecked && sigResolved;

    public override void DrawConfig()
    {
        if (sigChecked && !sigResolved)
        {
            ImGui.TextColored(new System.Numerics.Vector4(1f, 0.5f, 0.4f, 1f), "這版遊戲找不到結算函式。");
            ImGui.TextDisabled("新場次不會被記錄（沒有掛任何 hook）；已存下的紀錄照樣能在報表裡看。");
        }
        else
        {
            ImGui.TextDisabled("每場紛爭前線結算時自動記錄；不必開著報表。");
        }

        if (ImGui.Button("開啟前線戰績報表")) OpenReport();
        ImGui.SameLine();
        ImGui.TextDisabled($"指令 {Command}");
    }
}

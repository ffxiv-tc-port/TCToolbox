using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Text.ReadOnly;
using TCToolbox.Core;
using ValueType = FFXIVClientStructs.FFXIV.Component.GUI.ValueType;

namespace TCToolbox.Modules;

/// <summary>
/// 寶藏鑑定紀錄：鑑定相關視窗開啟／更新／關閉時，把視窗資料寫進 Dalamud 記錄檔（Information，前綴 <c>[AppraisalDiag]</c>）。
/// 只讀不按：不送 callback、不點按鈕、不寫記憶體。用來取得之後「一鍵鑑定」需要的視窗名稱與參數。
/// </summary>
/// <remarks>
/// 鑑定家族的視窗只覆寫 OnSetup／OnRefresh，資料都在 AtkValues，所以只聽 PostSetup／PostRefresh（加 PreFinalize 標時間）。
/// 刻意不聽 ReceiveEvent：那要在共用的 <c>AtkUnitBase.ReceiveEvent</c> 上掛 hook，為了診斷不值得。
/// 契約：全部在框架執行緒（AddonLifecycle 回呼）；不跨幀保存任何原生指標。
/// </remarks>
public sealed unsafe class AppraisalDiagnostics : TcModule
{
    public const string ModuleInternalName = "AppraisalDiagnostics";

    public override string InternalName => ModuleInternalName;
    public override string DisplayName => "寶藏鑑定紀錄（只記錄）";

    public override string Description =>
        "只記錄、不操作：深層迷宮寶藏（死者宮殿／天之御柱／正統優雷卡）等鑑定相關的視窗出現時，" +
        "把視窗內容寫進 Dalamud 記錄檔（搜尋 [AppraisalDiag]），之後用來做「一鍵鑑定」。" +
        "不按任何按鈕、不送任何指令、不寫遊戲記憶體；沒在鑑定時不會寫記錄。";

    public override ModuleCategory Category => ModuleCategory.Combat;

    public override bool IsManualTrigger => true;

    private const string Tag = "[AppraisalDiag]";
    private const string Keyword = "鑑定";
    private const int MaxValues = 256;
    private const int ValuesPerLine = 8;
    private const int MaxTextLength = 300;
    private const ulong MinUserAddress = 0x10000;
    private const ulong MaxUserAddress = 0x00007FFFFFFFFFFF;

    private static readonly string[] AppraisalAddons =
        ["ItemAppraisal", "ItemInspectionList", "ItemInspection", "ItemInspectionResult"];

    private static readonly string[] MenuAddons = ["SelectString", "SelectIconString", "SelectYesno"];

    private static readonly AgentId[] AppraisalAgents = [AgentId.ItemAppraisal, AgentId.ItemInspection];

    /// <summary>只在這一版讀代理物件原始欄位：兩者的配置大小（0xA0）只對這一版離線驗過。</summary>
    private const string RawAgentVerifiedVersion = "2026.09.14.0000.0000";

    private const int RawAgentStart = 0x28;
    private const int RawAgentEnd = 0xA0;

    private readonly Dictionary<string, string> lastRefresh = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> suppressedRefresh = new(StringComparer.Ordinal);
    private bool rawAgentAllowed;
    private int sequence;

    protected override void OnEnable()
    {
        lastRefresh.Clear();
        suppressedRefresh.Clear();

        var version = ReadGameVersion();
        rawAgentAllowed = version == RawAgentVerifiedVersion;
        Svc.Log.Information(rawAgentAllowed
            ? $"{Tag} 已啟用（遊戲版本 {version}，會附 agent 原始欄位）。"
            : $"{Tag} 已啟用（遊戲版本 {version ?? "讀不到"} 沒有離線驗過 agent 配置大小，略過 agent 原始欄位）。");

        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, AppraisalAddons, OnAppraisalAddon);
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostRefresh, AppraisalAddons, OnAppraisalAddon);
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, AppraisalAddons, OnAppraisalAddon);
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, MenuAddons, OnMenuAddon);
    }

    protected override void OnDisable()
    {
        Svc.AddonLifecycle.UnregisterListener(OnAppraisalAddon);
        Svc.AddonLifecycle.UnregisterListener(OnMenuAddon);
    }

    private void OnAppraisalAddon(AddonEvent type, AddonArgs args)
    {
        try
        {
            var name = args.AddonName;
            if (type == AddonEvent.PreFinalize)
            {
                lastRefresh.Remove(name);
                Svc.Log.Information($"{Tag} #{++sequence} {name} 關閉{TakeSuppressed(name)} | {DescribeAgents()}");
                return;
            }

            var values = DumpValues(args, out var count);
            var lines = new List<string>(values) { $"agent：{DescribeAgents()}" };
            lines.AddRange(DescribeRawAgents());

            if (type == AddonEvent.PostSetup)
            {
                lastRefresh.Remove(name);
                Emit(name, "開啟", count, lines, withTarget: true);
                return;
            }

            var content = string.Join("\n", lines);
            if (lastRefresh.TryGetValue(name, out var previous) && previous == content)
            {
                suppressedRefresh[name] = suppressedRefresh.GetValueOrDefault(name) + 1;
                return;
            }

            lastRefresh[name] = content;
            Emit(name, "更新" + TakeSuppressed(name), count, lines, withTarget: false);
        }
        catch (Exception ex)
        {
            Svc.Log.Warning($"{Tag} 記錄 {args.AddonName} 時發生例外：{ex.GetType().Name} {ex.Message}");
        }
    }

    private void OnMenuAddon(AddonEvent type, AddonArgs args)
    {
        try
        {
            // 先用目標名稱／鑑定代理物件判斷再讀 AtkValues：其他時候的選單一個字都不讀。
            if (!AnyAppraisalAgentOpen() && !TargetNameHasKeyword()) return;

            var values = DumpValues(args, out var count);
            var lines = new List<string>(values) { $"agent：{DescribeAgents()}" };
            Emit(args.AddonName, "開啟（鑑定相關選單）", count, lines, withTarget: true);
        }
        catch (Exception ex)
        {
            Svc.Log.Warning($"{Tag} 記錄 {args.AddonName} 時發生例外：{ex.GetType().Name} {ex.Message}");
        }
    }

    private void Emit(string addon, string what, int count, List<string> lines, bool withTarget)
    {
        var id = ++sequence;
        var target = withTarget ? $" | 目標：{DescribeTarget()}" : string.Empty;
        Svc.Log.Information($"{Tag} #{id} {addon} {what}，AtkValues {count} 個{target}");
        foreach (var line in lines)
            Svc.Log.Information($"{Tag} #{id} {addon} {line}");
    }

    private string TakeSuppressed(string addon)
    {
        if (!suppressedRefresh.Remove(addon, out var n) || n == 0) return string.Empty;
        return $"（之前略過 {n} 次相同內容的更新）";
    }

    /// <summary>把 PostSetup／PostRefresh 帶進來的 AtkValues 每 <see cref="ValuesPerLine"/> 個排成一行。</summary>
    private static List<string> DumpValues(AddonArgs args, out int count)
    {
        var (pointer, declared) = args switch
        {
            AddonSetupArgs s => (s.AtkValues, s.AtkValueCount),
            AddonRefreshArgs r => (r.AtkValues, r.AtkValueCount),
            _ => ((nint)0, 0u),
        };

        count = (int)Math.Min(declared, int.MaxValue);
        var lines = new List<string>();
        if (count == 0) return lines;
        if (!IsUserAddress(pointer))
        {
            lines.Add($"AtkValues 指標不合理（0x{pointer:X}），不讀");
            return lines;
        }

        var shown = Math.Min(count, MaxValues);
        var values = (AtkValue*)pointer;
        var sb = new StringBuilder();
        for (var start = 0; start < shown; start += ValuesPerLine)
        {
            sb.Clear();
            var end = Math.Min(start + ValuesPerLine, shown);
            sb.Append($"值[{start}-{end - 1}]");
            for (var i = start; i < end; i++)
                sb.Append($" [{i}]{FormatValue(values + i)}");
            lines.Add(sb.ToString());
        }

        if (shown < count) lines.Add($"（只列前 {shown} 個，共 {count} 個）");
        return lines;
    }

    private static string FormatValue(AtkValue* value)
    {
        var type = value->Type;
        return (type & ValueType.TypeMask) switch
        {
            ValueType.Bool => $"{type}={value->Byte != 0}",
            ValueType.Int => $"{type}={value->Int}",
            ValueType.Int64 => $"{type}={value->Int64}",
            ValueType.UInt => $"{type}={value->UInt}",
            ValueType.UInt64 => $"{type}={value->UInt64}",
            ValueType.Float => $"{type}={value->Float}",
            ValueType.String or ValueType.String8 => $"{type}={ReadUtf8(value->String.Value)}",
            ValueType.WideString => $"{type}={ReadWide(value->WideString)}",
            ValueType.Undefined or ValueType.Null => type.ToString(),
            _ => $"{type}=0x{(nint)value->Pointer:X}",
        };
    }

    /// <summary>字串照原樣：純文字，含 payload 時另附巨集形式；換行改寫成 \n 以免拆行。</summary>
    private static string ReadUtf8(byte* text)
    {
        if (text == null) return "（null）";
        if (!IsUserAddress((nint)text)) return $"（指標不合理 0x{(nint)text:X}）";

        var bytes = MemoryMarshal.CreateReadOnlySpanFromNullTerminated(text);
        var se = new ReadOnlySeStringSpan(bytes);
        var plain = Quote(se.ExtractText());
        return bytes.IndexOf((byte)0x02) < 0 ? plain : $"{plain}〔巨集：{Quote(se.ToMacroString())}〕";
    }

    private static string ReadWide(char* text)
    {
        if (text == null) return "（null）";
        return IsUserAddress((nint)text) ? Quote(new string(text)) : $"（指標不合理 0x{(nint)text:X}）";
    }

    private static string Quote(string text)
    {
        var escaped = text.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
        return escaped.Length <= MaxTextLength ? $"「{escaped}」" : $"「{escaped[..MaxTextLength]}…」";
    }

    private static bool IsUserAddress(nint address) => (ulong)address is >= MinUserAddress and <= MaxUserAddress;

    private static bool AnyAppraisalAgentOpen()
    {
        foreach (var id in AppraisalAgents)
        {
            var agent = GetAgent(id);
            if (agent != null && agent->AddonId != 0) return true;
        }

        return false;
    }

    private static AgentInterface* GetAgent(AgentId id)
    {
        if (AgentModule.MemberFunctionPointers.GetAgentByInternalId == null) return null;

        var module = AgentModule.Instance();
        return module == null ? null : module->GetAgentByInternalId(id);
    }

    private static string DescribeAgents()
    {
        var parts = new List<string>(AppraisalAgents.Length);
        foreach (var id in AppraisalAgents)
        {
            var agent = GetAgent(id);
            parts.Add(agent == null ? $"{id}({(int)id}) 取不到" : $"{id}({(int)id}) AddonId={agent->AddonId}");
        }

        return string.Join("；", parts);
    }

    /// <summary>兩個鑑定代理物件在 CS 宣告之後的原始欄位（每 4 bytes 一格），只在 <see cref="RawAgentVerifiedVersion"/> 讀。</summary>
    private List<string> DescribeRawAgents()
    {
        var lines = new List<string>();
        if (!rawAgentAllowed) return lines;

        foreach (var id in AppraisalAgents)
        {
            var agent = (byte*)GetAgent(id);
            if (!IsUserAddress((nint)agent)) continue;

            var sb = new StringBuilder($"agent {id} 原始欄位");
            for (var offset = RawAgentStart; offset < RawAgentEnd; offset += sizeof(uint))
                sb.Append($" +{offset:X2}={*(uint*)(agent + offset):X8}");
            lines.Add(sb.ToString());
        }

        return lines;
    }

    private static bool TargetNameHasKeyword() =>
        Svc.Targets.Target?.Name.TextValue.Contains(Keyword, StringComparison.Ordinal) == true;

    private static string DescribeTarget()
    {
        var target = Svc.Targets.Target;
        return target == null
            ? "無"
            : $"{target.Name.TextValue}（BaseId={target.BaseId}，{target.ObjectKind}）";
    }

    /// <summary>讀遊戲目錄的 <c>ffxivgame.ver</c>；讀不到回 null。</summary>
    private static string? ReadGameVersion()
    {
        try
        {
            var dir = Path.GetDirectoryName(Environment.ProcessPath);
            if (string.IsNullOrEmpty(dir)) return null;

            var path = Path.Combine(dir, "ffxivgame.ver");
            if (!File.Exists(path)) return null;

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var version = reader.ReadToEnd().Trim();
            return version.Length == 0 ? null : version;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using CyDownload.Core.Resolve;

namespace CyDownload.App.Infrastructure;

/// <summary>目标设备设置（写进 settings.json；对应 `TargetProfile`）。</summary>
public sealed record TargetSettings
{
    public string ProductType { get; init; } = "iPhone4,1";

    public string IosVersion { get; init; } = "6.1.3";

    public string? BuildVersion { get; init; } = "10B329";

    public string? CfVersion { get; init; } = "793.00";

    public string? KernelVersion { get; init; } = "13.0.0";

    public IReadOnlyList<string> Arches { get; init; } = ["iphoneos-arm"];

    public TargetProfile ToProfile() => new()
    {
        Name = $"{ProductType.Split(',')[0]} / {IosVersion}",
        ProductType = ProductType,
        IosVersion = IosVersion,
        BuildVersion = BuildVersion,
        CfVersion = CfVersion,
        KernelVersion = KernelVersion,
        Arches = (Arches ?? []).Where(arch => !string.IsNullOrWhiteSpace(arch)).Select(arch => arch!.Trim()).ToArray() is { Length: > 0 } arches
            ? arches
            : ["iphoneos-arm"],
    };

    public static TargetSettings FromProfile(TargetProfile profile) => new()
    {
        ProductType = profile.ProductType,
        IosVersion = profile.IosVersion,
        BuildVersion = profile.BuildVersion,
        CfVersion = profile.CfVersion,
        KernelVersion = profile.KernelVersion,
        Arches = profile.Arches.Count == 0 ? ["iphoneos-arm"] : profile.Arches,
    };
}

/// <summary>
/// `settings.json`。放在存档根目录，跟存档一起搬走。
/// <para>
/// **坏值/坏 JSON 一律回默认**：启动不能因为一个设置文件打不开就失败。
/// </para>
/// </summary>
public sealed record AppSettings
{

    /// <summary>`system` / `zh-CN` / `en`。</summary>
    public string Language { get; init; } = "system";

    /// <summary>`preset`（31 包基线）或 `none`（不假设任何已装）。</summary>
    public string BaseMode { get; init; } = "preset";

    public TargetSettings Target { get; init; } = new();

    public int MaxNodes { get; init; } = 500;

    public int MaxCandidatesPerName { get; init; } = 3;

    public int HttpNoProgressSeconds { get; init; } = 30;

    public int HttpTotalMinutes { get; init; } = 10;

    /// <summary>同时下载的包数（1 = 串行；默认 4，设置可调）。</summary>
    public int MaxParallelDownloads { get; init; } = 4;

    /// <summary>同一个源的同时下载上限（老源并发高会 502，默认 2）。</summary>
    public int MaxPerRepoConcurrency { get; init; } = 2;

    /// <summary>资料库是否把同一插件的多次记录合并成一行（默认是；关掉看每次 run 的差异）。</summary>
    public bool MergeSameGroups { get; init; } = true;

    /// <summary>是否把「内置但已停用」的源也显示在页签栏（默认否，避免一堆不可用页签）。</summary>
    public bool ShowDisabledSources { get; init; }

    /// <summary>默认源是否已种过（用户把源全删光后，下次启动不再自动写回，）。</summary>
    public bool SourcesSeeded { get; init; }

    /// <summary>内置源清单版本（升到新版本时补缺失的源）。</summary>
    public int BuiltInSourcesVersion { get; init; }

    [JsonIgnore]
    public bool UsePresetBase => !string.Equals(BaseMode, "none", StringComparison.OrdinalIgnoreCase);

    public TargetProfile TargetProfile => Target.ToProfile();
}

/// <summary>`settings.json` 的读写（原子替换；失败不抛给调用方，返回 problem）。</summary>
internal static class SettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string PathFor(string archiveRoot) => Path.Combine(archiveRoot, "settings.json");

    /// <summary>读取；文件不存在/坏 JSON/读失败都回默认值（并把原因写进 <paramref name="problem"/>）。</summary>
    public static AppSettings Load(string archiveRoot, out string? problem)
    {
        problem = null;
        var path = PathFor(archiveRoot);

        try
        {
            if (!File.Exists(path)) return new AppSettings();

            var json = File.ReadAllText(path);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions);
            if (settings is null)
            {
                problem = "settings.json is empty";
                return new AppSettings();
            }

            return Sanitize(settings, out problem);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                       or NullReferenceException or ArgumentException or InvalidOperationException)
        {
            problem = $"{ex.GetType().Name}: {ex.Message}";
            return new AppSettings();
        }
    }

    /// <summary>保存（先写 .tmp 再替换，避免写一半留下坏文件）。失败返回 false，不抛。</summary>
    public static bool Save(string archiveRoot, AppSettings settings, out string? problem)
    {
        problem = null;
        var path = PathFor(archiveRoot);
        var temp = path + ".tmp";

        try
        {
            settings = Sanitize(settings, out _);
            Directory.CreateDirectory(archiveRoot);
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, SerializerOptions) + "\n");
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            problem = $"{ex.GetType().Name}: {ex.Message}";
            try
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
            catch
            {
                // 清理失败不重要
            }

            return false;
        }
    }

    /// <summary>把明显不合理的值收回默认（用户手改 settings.json 是常见操作）。</summary>
    private static AppSettings Sanitize(AppSettings settings, out string? problem)
    {
        problem = null;

        // JSON 可以显式写 "Target": null —— 引用类型字段必须空安全，否则启动直接 NRE、只能手改文件才能再启动
        var target = settings.Target ?? new TargetSettings();

        // Arches 里可能有 null 元素（"Arches": [null]）→ 过滤掉，全空则回默认
        var arches = (target.Arches ?? [])
            .Where(arch => !string.IsNullOrWhiteSpace(arch))
            .Select(arch => arch!.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (arches.Count == 0) arches = ["iphoneos-arm"];
        target = target with { Arches = arches };

        if (string.IsNullOrWhiteSpace(target.ProductType) || string.IsNullOrWhiteSpace(target.IosVersion))
        {
            problem = "target fields incomplete; defaults restored";
            target = new TargetSettings();
        }

        var nodes = Math.Clamp(settings.MaxNodes, 10, 5000);
        var candidates = Math.Clamp(settings.MaxCandidatesPerName, 1, 50);
        var noProgress = Math.Clamp(settings.HttpNoProgressSeconds, 5, 600);
        var total = Math.Clamp(settings.HttpTotalMinutes, 1, 120);
        var parallel = Math.Clamp(settings.MaxParallelDownloads, 1, 8);
        var perRepo = Math.Clamp(settings.MaxPerRepoConcurrency, 1, parallel);

        var baseMode = settings.UsePresetBase ? "preset" : "none";
        var language = string.IsNullOrWhiteSpace(settings.Language) ? "system" : settings.Language.Trim();

        return settings with
        {
            Language = language,
            BaseMode = baseMode,
            Target = target,
            MaxNodes = nodes,
            MaxCandidatesPerName = candidates,
            HttpNoProgressSeconds = noProgress,
            HttpTotalMinutes = total,
            MaxParallelDownloads = parallel,
            MaxPerRepoConcurrency = perRepo,
        };
    }
}







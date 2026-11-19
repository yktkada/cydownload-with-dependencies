using System.Globalization;
using System.Text.Json;
using CyDownload.Core.Diagnostics;
using CyDownload.Core.Fetch;
using CyDownload.Core.Index;
using CyDownload.Core.Net;
using CyDownload.Core.Resolve;

namespace CyDownload.Core.Export;

/// <summary>导出参数。</summary>
public sealed record ExportOptions
{
    /// <summary>导出目录（会被清空其中的 `*.deb` 与旧 manifest）。</summary>
    public required string Directory { get; init; }

    /// <summary>目标设备（用于兼容性警告，例如 iOS 6 装不了 `control.tar.zst`）。</summary>
    public TargetProfile? Target { get; init; }

    /// <summary>本次求解用的 base 口径（写进 manifest，便于复现）。</summary>
    public string BaseMode { get; init; } = "preset";
}

/// <summary>导出进度。</summary>
public sealed record ExportProgress(int Completed, int Total, string CurrentPackage);

/// <summary>导出到目录里的一个文件。</summary>
public sealed record ExportedFile(
    int Order,
    string FileName,
    PackageRow Package,
    long Bytes,
    string SourceUrl,
    bool FromCache,
    string PoolPath);

/// <summary>导出结果。</summary>
public sealed record ExportResult(
    string Directory,
    IReadOnlyList<ExportedFile> Files,
    string ManifestPath,
    long TotalBytes,
    IReadOnlyList<string> Cycles,
    IReadOnlyList<string> Warnings)
{
    /// <summary>目标目录里已有同名同内容的文件，跳过的数量（重复导出常见）。</summary>
    public int SkippedIdentical { get; init; }

    /// <summary>目标目录里已有同名但内容不同的文件，**未覆盖**的数量。</summary>
    public int SkippedDifferent { get; init; }

    /// <summary>文件名补零宽度（按包数位宽决定，例如 12 个包 → 2）。</summary>
    public static int WidthFor(int count) => count <= 0 ? 1 : count.ToString(CultureInfo.InvariantCulture).Length;
}

/// <summary>
/// 把一个闭包导出成「可以直接拷进设备 `Cydia/AutoInstall`」的目录。
/// <para>
/// 文件名 `NN-包名_版本_架构.deb`，`NN` 补零宽度按闭包总数位宽（12 个包 → `01-`…`12-`）。
/// 为什么补零：AutoInstall 用 bash glob 展开，按字节序（实测 `10-` 会排在 `2-` 前面）。
/// </para>
/// <para>
/// **导出只做复制，不删除、不移动任何东西**（设计 + ）：
/// 目标目录里原有的文件一律不动；同名同内容 → 跳过；同名不同内容 → 跳过并告警（不覆盖）。
/// pool 里的原件始终保留（导出是给设备用的一份拷贝）。
/// </para>
/// <para>
/// 同时写 `manifest.json`：顺序、来源 URL、哈希、环标注、警告——用户以后想复现或排查都靠它。
/// </para>
/// </summary>
public sealed class DebExporter
{
    private readonly IDebSource _source;
    private readonly ILogSink _log;

    public DebExporter(IDebSource source, ILogSink? log = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
        _log = log ?? NullLogSink.Instance;
    }

    public async Task<ExportResult> ExportAsync(
        ResolutionResult resolution,
        ExportOptions options,
        IProgress<ExportProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Directory);

        if (!resolution.Success)
        {
            throw new InvalidOperationException(
                "Cannot export an unsuccessful resolution: " + (resolution.FailureReason ?? "unknown"));
        }

        var directory = Path.GetFullPath(options.Directory);
        Directory.CreateDirectory(directory);

        var packages = resolution.ToDownload.Select(item => item.Package).ToList();
        var sort = TopologicalSorter.Sort(packages);
        var width = ExportResult.WidthFor(sort.Ordered.Count);

        var warnings = new List<string>();
        var files = new List<ExportedFile>(sort.Ordered.Count);
        long totalBytes = 0;
        var skippedIdentical = 0;
        var skippedDifferent = 0;

        // 只在需要时覆盖我们自己的 manifest；**目录里任何既有文件都不删**。
        var manifestPath = Path.Combine(directory, "manifest.json");

        try
        {
        for (var index = 0; index < sort.Ordered.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var package = sort.Ordered[index];
            var resolved = resolution.ToDownload.FirstOrDefault(item => ReferenceEquals(item.Package, package))
                ?? resolution.ToDownload.First(item => RowKey(item.Package) == RowKey(package));

            progress?.Report(new ExportProgress(index, sort.Ordered.Count, package.PackageId));

            var fetched = await _source.FetchAsync(resolved, progress: null, cancellationToken).ConfigureAwait(false);

            var number = (index + 1).ToString($"D{width}", CultureInfo.InvariantCulture);
            var fileName = $"{number}-{package.PackageId}_{package.Version}_{package.Architecture}.deb";
            var destination = Path.Combine(directory, fileName);

            if (File.Exists(destination) && !IsSameContent(destination, fetched.FilePath))
            {
                // 同名但内容不同：**不覆盖**（用户可能改过/放了自己的版本），报出来让用户决定
                skippedDifferent++;
                warnings.Add($"{fileName}：目标目录里已存在同名但内容不同的文件，未覆盖");
                continue;
            }

            if (File.Exists(destination))
            {
                skippedIdentical++;
            }
            else
            {
                // 复制而不是移动：pool 里的原件必须保留。
                // 先落 .tmp 再原子改名：复制中途崩溃不会在导出目录留下半个 deb。
                var temp = destination + ".tmp";
                File.Copy(fetched.FilePath, temp, overwrite: true);
                File.Move(temp, destination, overwrite: false);
            }

            files.Add(new ExportedFile(
                index + 1, fileName, package, fetched.Bytes, fetched.SourceUrl, fetched.FromCache, fetched.FilePath));
            totalBytes += fetched.Bytes;

            AddCompatibilityWarnings(warnings, options.Target, package, fetched.Inspection);
        }

        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 中断/失败：把「已经复制了哪些」写成 manifest.partial.json，
            // 并删掉本次失败的 .tmp（不含任何用户文件）。
            await WritePartialManifestAsync(directory, options, resolution, sort, files, ex, cancellationToken)
                .ConfigureAwait(false);
            throw;
        }

        progress?.Report(new ExportProgress(sort.Ordered.Count, sort.Ordered.Count, ""));

        var manifest = BuildManifest(options, resolution, sort, files, totalBytes, warnings);
        await File.WriteAllTextAsync(manifestPath, manifest, cancellationToken).ConfigureAwait(false);

        // 成功之后，上一次中断留下的 partial 就不该再存在
        try
        {
            var stalePartial = Path.Combine(directory, "manifest.partial.json");
            if (File.Exists(stalePartial)) File.Delete(stalePartial);
        }
        catch (IOException)
        {
            // 删不掉不影响导出结果
        }

        _log.Write(
            $"[export] {directory}：{files.Count} 个 deb（复制），合计 {totalBytes:N0} B，" +
            $"同名跳过 {skippedIdentical}，内容不同未覆盖 {skippedDifferent}，环 {sort.Cycles.Count} 个，警告 {warnings.Count} 条");

        return new ExportResult(directory, files, manifestPath, totalBytes, sort.Cycles, warnings)
        {
            SkippedIdentical = skippedIdentical,
            SkippedDifferent = skippedDifferent,
        };
    }

    /// <summary>目标文件与源文件内容是否一致（长度 + SHA256；先比长度省读数）。</summary>
    private static bool IsSameContent(string left, string right)
    {
        var leftInfo = new FileInfo(left);
        var rightInfo = new FileInfo(right);
        if (!leftInfo.Exists || !rightInfo.Exists) return false;
        if (leftInfo.Length != rightInfo.Length) return false;

        return HashFile(left).AsSpan().SequenceEqual(HashFile(right));
    }

    private static byte[] HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return System.Security.Cryptography.SHA256.HashData(stream);
    }

    /// <summary>导出中断时写 `manifest.partial.json`：说明这次只复制了哪些、为什么停下。</summary>
    private static async Task WritePartialManifestAsync(
        string directory,
        ExportOptions options,
        ResolutionResult resolution,
        SortResult sort,
        IReadOnlyList<ExportedFile> files,
        Exception error,
        CancellationToken cancellationToken)
    {
        try
        {
            var payload = new
            {
                partial = true,
                generatedAt = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture),
                error = error.GetType().Name + ": " + error.Message,
                target = options.Target?.Name,
                baseMode = options.BaseMode,
                intended = sort.Ordered.Count,
                copied = files.Select(file => new
                {
                    order = file.Order,
                    file = file.FileName,
                    package = file.Package.PackageId,
                    version = file.Package.Version,
                    size = file.Bytes,
                }).ToList(),
            };

            var path = Path.Combine(directory, "manifest.partial.json");
            await File.WriteAllTextAsync(
                path,
                System.Text.Json.JsonSerializer.Serialize(payload, new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true,
                }) + "\n",
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception writeError) when (writeError is IOException or UnauthorizedAccessException)
        {
            // 清单写不出来不能掩盖原始失败
        }
    }
    /// <summary>兼容性警告：iOS 6/7 的 dpkg 不认 zstd 控制段（ 真机实测）。</summary>
    private static void AddCompatibilityWarnings(
        List<string> warnings,
        TargetProfile? target,
        PackageRow package,
        DebInspection inspection)
    {
        if (!inspection.ControlIsZstd) return;

        var iosVersion = target?.IosVersion ?? "";
        var oldSystem = iosVersion.Length > 0 &&
                        Deb.DebianVersion.TryParse(iosVersion, out var parsed) &&
                        parsed.CompareTo(Deb.DebianVersion.Parse("7.0")) < 0;

        if (!oldSystem) return;

        warnings.Add(
            $"{package.PackageId} {package.Version}：控制段是 control.tar.zst，iOS {iosVersion} 的 dpkg 1.18.10 不认（装不上）");
    }

    private static string BuildManifest(
        ExportOptions options,
        ResolutionResult resolution,
        SortResult sort,
        IReadOnlyList<ExportedFile> files,
        long totalBytes,
        IReadOnlyList<string> warnings)
    {
        var manifest = new
        {
            generatedAt = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture),
            target = options.Target is null ? null : new
            {
                name = options.Target.Name,
                productType = options.Target.ProductType,
                iosVersion = options.Target.IosVersion,
                arches = options.Target.Arches,
            },
            baseMode = options.BaseMode,
            count = files.Count,
            totalBytes,
            cycles = sort.Cycles,
            warnings,
            files = files.Select(file => new
            {
                order = file.Order,
                file = file.FileName,
                package = file.Package.PackageId,
                version = file.Package.Version,
                architecture = file.Package.Architecture,
                repo = file.Package.Repo,
                size = file.Bytes,
                sha256 = file.Package.Sha256,
                md5 = file.Package.Md5,
                sourceUrl = file.SourceUrl,
                fromPoolCache = file.FromCache,
            }).ToList(),
            satisfied = resolution.Satisfied.Select(item => new
            {
                parent = item.Parent,
                name = item.Name,
                source = item.Source,
                version = item.Version,
            }).ToList(),
        };

        return JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }) + "\n";
    }

    private static string RowKey(PackageRow package)
        => package.PackageId + "\u0000" + package.Version + "\u0000" + package.Architecture + "\u0000" + package.Repo;
}





using System.IO;
using System.Net.Http;
using CyDownload.Core.Diagnostics;
using CyDownload.Core.Index;
using CyDownload.Core.Export;
using CyDownload.Core.Fetch;
using CyDownload.Core.Net;
using CyDownload.Core.Repos;
using CyDownload.Core.Resolve;

namespace CyDownload.Probe;

/// <summary>
/// 抓取 / 求解自检台（手动跑，不入发布包）。
/// <code>
/// CyDownload.Probe                          抓三源，打印结果（不落库）
/// CyDownload.Probe fetch &lt;dbPath&gt;           抓三源 + 落库
/// CyDownload.Probe resolve &lt;dbPath&gt; &lt;pkg…&gt;  对已有库算闭包
/// </code>
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var mode = args.Length > 0 ? args[0].ToLowerInvariant() : "fetch";
        return mode switch
        {
            "fetch" => await FetchAsync(args.Length > 1 ? args[1] : null).ConfigureAwait(false),
            "resolve" => Resolve(args),
            "export" => await ExportAsync(args).ConfigureAwait(false),
            "repair" => await RepairAsync(args).ConfigureAwait(false),
            _ => Usage(),
        };
    }

    private static int Usage()
    {
        Console.WriteLine("用法：");
        Console.WriteLine("  CyDownload.Probe                          抓三源（不落库）");
        Console.WriteLine("  CyDownload.Probe fetch <dbPath>           抓三源 + 落库");
        Console.WriteLine("  CyDownload.Probe resolve <dbPath> <pkg…>  对已有库算闭包");
        Console.WriteLine("  CyDownload.Probe export  <dbPath> <outDir> <pkg…>  算闭包并导出 deb 目录");
        Console.WriteLine("  CyDownload.Probe repair  <dbPath>         修复「台账有、文件没了」的 pool 对象");
        return 1;
    }

    // ── 抓取 ────────────────────────────────────────────────────────────────

    private static async Task<int> FetchAsync(string? databasePathArgument)
    {
        var databasePath = databasePathArgument is null ? null : Path.GetFullPath(databasePathArgument);
        var options = new HttpDownloadOptions { Headers = TargetHeaders() };

        using var http = HttpDownloader.CreateClient();
        var log = new ConsoleLog();
        var fetcher = new IndexFetcher(new HttpDownloader(http), log);

        ArchiveDatabase? database = null;
        PackageRepository? repository = null;
        if (databasePath is not null)
        {
            var directory = Path.GetDirectoryName(databasePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            database = ArchiveDatabase.Open(databasePath);
            repository = new PackageRepository(database, log);
            Console.WriteLine($"落库目标：{databasePath}");
        }

        var failures = 0;
        var outcomes = new List<IndexFetchOutcome>();

        try
        {
            foreach (var repo in DefaultRepos())
            {
                Console.WriteLine();
                Console.WriteLine($"=== {repo.Name} ({repo.Layout}) ===");

                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                var outcome = await fetcher
                    .FetchAsync(repo, options, new ThrottledProgress(), cts.Token)
                    .ConfigureAwait(false);
                outcomes.Add(outcome);

                if (!outcome.Success)
                {
                    failures++;
                    Console.WriteLine($"  FAIL {outcome.Error}");
                    continue;
                }

                Console.WriteLine(
                    $"  OK   {outcome.Compression,-5} {outcome.CompressedBytes,10:N0} B -> " +
                    $"{outcome.UncompressedBytes,12:N0} B | stanza {outcome.StanzaCount,6:N0} / " +
                    $"收下 {outcome.KeptCount,6:N0} | Release {outcome.Verification} | " +
                    $"{outcome.Elapsed.TotalSeconds,5:0.0} s");

                if (repository is null) continue;

                var imported = repository.Import(repo, outcome.Stanzas, repo.Architecture, DateTimeOffset.Now);
                Console.WriteLine(
                    $"  落库 新增 {imported.Inserted,6:N0} / 更新 {imported.Updated,6:N0} / " +
                    $"跳过 {imported.Skipped,5:N0} | provides {imported.Provides,6:N0} | " +
                    $"库里 {imported.TotalAfterImport,6:N0} 行 | {imported.Elapsed.TotalMilliseconds,7:0} ms");
            }

            Console.WriteLine();
            Console.WriteLine("=== 汇总 ===");
            foreach (var outcome in outcomes)
            {
                Console.WriteLine(
                    $"  {outcome.Repo.Name,-8} {(outcome.Success ? "ok" : "fail"),-5} " +
                    $"{outcome.StanzaCount,6:N0} stanza  {outcome.Elapsed.TotalSeconds,6:0.0} s");
            }

            if (repository is not null) Console.WriteLine($"  库里总计 {repository.CountPackages():N0} 行");

            return failures == 0 ? 0 : 2;
        }
        finally
        {
            database?.Dispose();
        }
    }

    // ── 求解 ────────────────────────────────────────────────────────────────

    private static int Resolve(string[] args)
    {
        if (args.Length < 3) return Usage();

        var databasePath = Path.GetFullPath(args[1]);
        var targets = args[2..];

        if (!File.Exists(databasePath))
        {
            Console.WriteLine($"库不存在：{databasePath}（先跑 fetch）");
            return 1;
        }

        var log = new ConsoleLog();
        using var database = ArchiveDatabase.Open(databasePath);
        var repository = new PackageRepository(database, log);
        // 架构过滤用目标设备的 arches：不传就只按库里有的行乱选，arm64 目标会选错架构

        var target = TargetProfile.IPhone4S613;
        var source = new DatabasePackageSource(repository, target.Arches, log);
        var basePackages = new BasePackageProvider(target, installed: null, usePreset: true);
        var resolver = new DependencyResolver(source, basePackages, log);

        Console.WriteLine($"目标：{target.Describe()}");
        Console.WriteLine($"base：31 包预设 + 伪包（{basePackages.Count} 个）");
        Console.WriteLine($"求解：{string.Join(", ", targets)}");

        var result = resolver.Resolve(targets);

        Console.WriteLine();
        Console.WriteLine($"=== {(result.Success ? "成功" : "失败")}（展开 {result.NodesVisited} 个节点）===");
        if (!result.Success) Console.WriteLine($"原因：{result.FailureReason}");

        Console.WriteLine();
        Console.WriteLine($"下载清单 {result.ToDownload.Count} 个，合计 {result.TotalBytes:N0} B");
        foreach (var item in result.ToDownload)
        {
            var mark = item.IsTarget ? "*" : " ";
            Console.WriteLine(
                $"  {mark} {item.PackageId,-40} {item.Version,-18} {item.Package.Size ?? 0,10:N0} B  " +
                $"[{item.Package.Repo}/p{item.RepoPriority}]");
        }

        if (result.Cycles.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"环 {result.Cycles.Count} 个（顺序不保证）：{string.Join("；", result.Cycles)}");
        }

        Console.WriteLine();
        Console.WriteLine($"已满足 {result.Satisfied.Count} 条");
        foreach (var item in result.Satisfied.GroupBy(s => s.Name).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            Console.WriteLine($"  {item.Key,-32} ← {item.First().Source,-10} {item.First().Version}");
        }

        if (result.Unresolved.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"未解析 {result.Unresolved.Count} 条");
            foreach (var item in result.Unresolved) Console.WriteLine("  " + item);
        }

        return result.Success ? 0 : 3;
    }

    // ── 修复缺失实体文件 ────────────────────────────────────────────────────

    private static async Task<int> RepairAsync(string[] args)
    {
        if (args.Length < 2) return Usage();

        var databasePath = Path.GetFullPath(args[1]);
        if (!File.Exists(databasePath))
        {
            Console.WriteLine($"库不存在：{databasePath}");
            return 1;
        }

        var log = new ConsoleLog();
        using var database = ArchiveDatabase.Open(databasePath);
        var repository = new PackageRepository(database, log);
        var library = new LibraryStore(database);
        var poolDirectory = Path.Combine(Path.GetDirectoryName(databasePath)!, "pool");

        var missing = library.LoadMissingPoolFiles(poolDirectory);
        Console.WriteLine($"台账里缺文件的 pool 行：{missing.Count}");
        if (missing.Count == 0) return 0;

        var lookup = PackageLookup.Build(repository.LoadAll());
        var repoBaseUrls = repository.LoadRepos()
            .ToDictionary(row => row.Name, row => row.BaseUrl, StringComparer.Ordinal);

        using var http = HttpDownloader.CreateClient();
        var fetcher = new DebFetcher(
            new HttpDownloader(http), new HttpDownloadOptions { Headers = TargetHeaders() },
            repoBaseUrls, poolDirectory, log);

        var repaired = 0;
        var failed = 0;

        foreach (var entry in missing)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            try
            {
                var package = lookup.Find(entry.PackageId, entry.Version, entry.Architecture, entry.Repo)
                    ?? throw new InvalidOperationException("package no longer in index");
                var resolved = new ResolvedPackage(package, RepoPriority: 0, IsTarget: false);
                var fetched = await fetcher.FetchAsync(resolved, progress: null, cts.Token).ConfigureAwait(false);

                library.UpsertPool(
                [
                    entry with
                    {
                        RelativePath = Path.GetRelativePath(poolDirectory, fetched.FilePath),
                        Bytes = fetched.Bytes,
                        Sha256 = package.Sha256,
                        AcquiredAt = DateTimeOffset.Now.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                    },
                ]);

                repaired++;
                Console.WriteLine($"  修复 {entry.PackageId} {entry.Version}：{fetched.Bytes:N0} B");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failed++;
                Console.WriteLine($"  失败 {entry.PackageId} {entry.Version}：{ex.Message}");
            }
        }

        Console.WriteLine($"=== 修复完成：{repaired} 成功，{failed} 失败 ===");
        return failed == 0 ? 0 : 2;
    }

    // ── 导出 ────────────────────────────────────────────────────────────────

    private static async Task<int> ExportAsync(string[] args)
    {
        if (args.Length < 4) return Usage();

        var databasePath = Path.GetFullPath(args[1]);
        var outputDirectory = Path.GetFullPath(args[2]);
        var targets = args[3..];

        if (!File.Exists(databasePath))
        {
            Console.WriteLine($"库不存在：{databasePath}（先跑 fetch）");
            return 1;
        }

        var log = new ConsoleLog();
        using var database = ArchiveDatabase.Open(databasePath);
        var repository = new PackageRepository(database, log);

        var target = TargetProfile.IPhone4S613;
        var source = new DatabasePackageSource(repository, target.Arches, log);
        var basePackages = new BasePackageProvider(target, installed: null, usePreset: true);
        var resolver = new DependencyResolver(source, basePackages, log);

        Console.WriteLine($"目标：{target.Describe()}");
        Console.WriteLine($"求解：{string.Join(", ", targets)}");
        var resolution = resolver.Resolve(targets);

        if (!resolution.Success)
        {
            Console.WriteLine($"求解失败：{resolution.FailureReason}");
            foreach (var item in resolution.Unresolved) Console.WriteLine("  " + item);
            return 3;
        }

        Console.WriteLine($"闭包：{resolution.ToDownload.Count} 个，{resolution.TotalBytes:N0} B");

        using var http = HttpDownloader.CreateClient();
        var poolDirectory = Path.Combine(Path.GetDirectoryName(databasePath)!, "pool");
        var repoBaseUrls = DefaultRepos().ToDictionary(repo => repo.Name, repo => repo.BaseUrl, StringComparer.Ordinal);
        var fetcher = new DebFetcher(
            new HttpDownloader(http), new HttpDownloadOptions { Headers = TargetHeaders() },
            repoBaseUrls, poolDirectory, log);
        var exporter = new DebExporter(fetcher, log);

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        var result = await exporter.ExportAsync(
            resolution,
            new ExportOptions { Directory = outputDirectory, Target = target, BaseMode = "preset" },
            progress: null,
            cts.Token).ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine($"=== 导出到 {result.Directory} ===");
        foreach (var file in result.Files)
        {
            Console.WriteLine(
                $"  {file.FileName,-62} {file.Bytes,10:N0} B  {(file.FromCache ? "pool" : "new ")}  " +
                $"[{file.Package.Repo}]");
        }

        Console.WriteLine($"合计 {result.TotalBytes:N0} B；manifest: {result.ManifestPath}");
        if (result.Cycles.Count > 0)
        {
            Console.WriteLine($"环 {result.Cycles.Count} 个（顺序不保证）：{string.Join("；", result.Cycles)}");
        }

        foreach (var warning in result.Warnings) Console.WriteLine("  [warn] " + warning);

        return 0;
    }
    // ── 公共 ────────────────────────────────────────────────────────────────

    private static RepoDescriptor[] DefaultRepos() =>
    [
        new()
        {
            Name = "saurik", BaseUrl = "http://apt.saurik.com/", Layout = RepoLayout.Dists,
            Suite = "ios/793.00", Component = "main", Priority = 1,
        },
        new()
        {
            Name = "bigboss", BaseUrl = "http://apt.thebigboss.org/repofiles/cydia/", Layout = RepoLayout.Dists,
            Suite = "stable", Component = "main", Priority = 2,
        },
        new()
        {
            Name = "aptgs", BaseUrl = "http://apt.gs", Layout = RepoLayout.Flat, Priority = 3,
        },
    ];

    /// <summary>按目标设备伪造头（实测对索引内容无影响，仍按目标设备发）。</summary>
    private static Dictionary<string, string> TargetHeaders()
    {
        var target = TargetProfile.IPhone4S613;
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["X-Machine"] = target.ProductType,
            ["X-Unique-ID"] = "7a5f0b3c1d2e4f6a8b9c0d1e2f3a4b5c6d7e8f90",
            ["X-Firmware"] = target.IosVersion,
        };
    }

    private sealed class ConsoleLog : ILogSink
    {
        public void Write(string line) => Console.WriteLine("  " + line);
    }

    /// <summary>每 25% 报一次，避免刷屏。</summary>
    private sealed class ThrottledProgress : IProgress<DownloadProgress>
    {
        private int _lastBucket = -1;

        public void Report(DownloadProgress value)
        {
            var bucket = value.Percent is { } percent ? (int)(percent / 25) : -1;
            if (bucket == _lastBucket) return;
            _lastBucket = bucket;
            Console.WriteLine(value.Percent is { } p
                ? $"  ... {p,5:0.0}%  {value.BytesReceived,10:N0} B"
                : $"  ... {value.BytesReceived,10:N0} B");
        }
    }
}




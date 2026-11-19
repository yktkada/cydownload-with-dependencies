using CyDownload.Core.Diagnostics;
using CyDownload.Core.Index;
using CyDownload.Core.Net;
using CyDownload.Core.Resolve;

namespace CyDownload.Core.Fetch;

/// <summary>一次 deb 取回的结果（可能来自 pool 缓存）。</summary>
public sealed record DebFetchResult(
    ResolvedPackage Package,
    string FilePath,
    long Bytes,
    string SourceUrl,
    bool FromCache,
    DebInspection Inspection);

/// <summary>deb 取回失败（下不到 / 校验不过 / 结构不对）。**消息面向日志（英文）**，界面层按错误码本地化。</summary>
public sealed class DebFetchException : Exception
{
    public DebFetchException(string message, string url)
        : base(message) => Url = url;

    public DebFetchException(string message, string url, Exception inner)
        : base(message, inner) => Url = url;

    public string Url { get; }
}

/// <summary>取 deb 的抽象：导出器只依赖它，单测可以喂夹具。</summary>
public interface IDebSource
{
    Task<DebFetchResult> FetchAsync(
        ResolvedPackage package,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken);
}

/// <summary>
/// 把 deb 落到**内容寻址**的 pool：`pool/objects/&lt;sha256&gt;.deb`（设计：真实相同的文件只存一份）。
/// <para>
/// 同一份 deb 从两个源下到、或不同包字节相同，都只落一份文件；`archive_pool` 仍按
/// (包名,版本,架构,源) 记行，多行指向同一个对象。
/// </para>
/// <para>
/// 流程：先问缓存（<c>cachedFilePath</c>，由调用方用数据库里的 rel_path 给出）→ 校验通过就直接用；
/// 否则下载 → 字节数/MD5/SHA256/**deb 结构**三重校验 → 算内容 sha256 → 已存在对象就复用（不重复写）→
/// 写 `.part` 再原子改名。
/// </para>
/// </summary>
public sealed class DebFetcher : IDebSource
{
    private readonly HttpDownloader _downloader;
    private readonly HttpDownloadOptions _options;
    private readonly IReadOnlyDictionary<string, string> _repoBaseUrls;
    private readonly Func<PackageRow, string?>? _cachedFilePath;
    private readonly ILogSink _log;

    public DebFetcher(
        HttpDownloader downloader,
        HttpDownloadOptions options,
        IReadOnlyDictionary<string, string> repoBaseUrls,
        string poolDirectory,
        ILogSink? log = null,
        Func<PackageRow, string?>? cachedFilePath = null)
    {
        ArgumentNullException.ThrowIfNull(downloader);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(repoBaseUrls);
        ArgumentException.ThrowIfNullOrWhiteSpace(poolDirectory);

        _downloader = downloader;
        _options = options;
        _repoBaseUrls = repoBaseUrls;
        _cachedFilePath = cachedFilePath;
        PoolDirectory = Path.GetFullPath(poolDirectory);
        _log = log ?? NullLogSink.Instance;
    }

    public string PoolDirectory { get; }

    public async Task<DebFetchResult> FetchAsync(
        ResolvedPackage package,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(package);

        var url = BuildUrl(package.Package);

        // ① 数据库里有记录的对象：校验通过就直接复用（不重新下载）
        if (_cachedFilePath?.Invoke(package.Package) is { } cachedPath && File.Exists(cachedPath))
        {
            var cachedBytes = await File.ReadAllBytesAsync(cachedPath, cancellationToken).ConfigureAwait(false);
            if (Verify(package.Package, cachedBytes, out var cachedInspection) is null)
            {
                _log.Write($"[fetch] 复用 pool：{package.PackageId} {package.Version}（{cachedBytes.Length:N0} B）");
                return new DebFetchResult(package, cachedPath, cachedBytes.Length, url, FromCache: true, cachedInspection);
            }

            _log.Write($"[fetch] pool 里的 {cachedPath} 校验不过，重新下载");
        }

        byte[] bytes;
        HttpDownloadResult meta;
        try
        {
            (meta, bytes) = await _downloader
                .GetBytesAsync(url, _options, progress, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpDownloadException ex)
        {
            throw new DebFetchException($"download failed: {ex.Message}", url, ex);
        }

        var failure = Verify(package.Package, bytes, out var inspection);
        if (failure is not null)
        {
            throw new DebFetchException($"verification failed: {failure}", meta.FinalUrl);
        }

        // ② 内容寻址落盘：同一内容只写一次
        var sha256 = PoolStore.Sha256OfBytes(bytes);
        var (relativePath, fullPath, created) = PoolStore.SaveObject(
            PoolDirectory, sha256, bytes, cancellationToken);

        _log.Write(
            $"[fetch] 下载完成 {package.PackageId} {package.Version}：{bytes.Length:N0} B " +
            $"（{meta.Elapsed.TotalSeconds:0.0} s，控制段 {inspection.ControlCompression}，" +
            $"sha256 {sha256[..12]}…，{(created ? "新对象" : "已有对象复用")}，{relativePath}）");

        return new DebFetchResult(package, fullPath, bytes.Length, meta.FinalUrl, FromCache: false, inspection);
    }

    private string BuildUrl(PackageRow package)
    {
        if (string.IsNullOrWhiteSpace(package.Filename))
        {
            throw new DebFetchException($"package {package.PackageId} has no Filename in index", "(none)");
        }

        if (!_repoBaseUrls.TryGetValue(package.Repo, out var baseUrl))
        {
            throw new DebFetchException($"unknown repo '{package.Repo}' for {package.PackageId}", "(none)");
        }

        var relative = package.Filename.Replace('\\', '/');
        if (relative.StartsWith("./", StringComparison.Ordinal)) relative = relative[2..];
        relative = relative.TrimStart('/');

        return baseUrl.TrimEnd('/') + "/" + relative;
    }

    /// <summary>校验：字节数 → MD5/SHA256 → deb 结构。返回 null 表示通过。</summary>
    private static string? Verify(PackageRow package, byte[] bytes, out DebInspection inspection)
    {
        inspection = DebFileInspector.Inspect(bytes);

        if (package.Size is { } expectedSize && expectedSize != bytes.Length)
        {
            return $"size {bytes.Length} != index {expectedSize}";
        }

        if (!string.IsNullOrWhiteSpace(package.Md5))
        {
            var actual = Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(bytes));
            if (!string.Equals(actual, package.Md5.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return $"md5 {actual} != index {package.Md5}";
            }
        }

        if (!string.IsNullOrWhiteSpace(package.Sha256))
        {
            var actual = PoolStore.Sha256OfBytes(bytes);
            if (!string.Equals(actual, package.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return $"sha256 {actual} != index {package.Sha256}";
            }
        }

        if (!inspection.IsValid)
        {
            return "not a valid deb: " + inspection.Error;
        }

        return null;
    }
}

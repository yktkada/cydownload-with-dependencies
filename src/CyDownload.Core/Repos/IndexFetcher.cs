using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using CyDownload.Core.Deb;
using CyDownload.Core.Diagnostics;
using CyDownload.Core.Fetch;
using CyDownload.Core.Net;

namespace CyDownload.Core.Repos;

/// <summary>索引与 Release 校验表对得上吗。</summary>
public enum ReleaseVerification
{
    /// <summary>源没给 Release，或 Release 里没有校验表。</summary>
    NotAvailable,

    /// <summary>Release 有校验表，但没列这个文件。</summary>
    NotListed,

    Matched,

    Mismatched,
}

/// <summary>一次抓索引的结果。失败也返回对象（错误进 <see cref="Error"/>），不抛异常给 UI。</summary>
public sealed record IndexFetchOutcome(
    RepoDescriptor Repo,
    bool Success,
    string? IndexUrl,
    string? FinalUrl,
    CompressionKind Compression,
    long CompressedBytes,
    long UncompressedBytes,
    int StanzaCount,
    int KeptCount,
    ReleaseVerification Verification,
    TimeSpan Elapsed,
    string? Error,
    IReadOnlyList<string> Attempts)
{
    /// <summary>解析出的全部 stanza（**未按架构过滤**，落库时由 <c>PackageRepository</c> 过滤并统计）。</summary>
    public IReadOnlyList<ControlStanza> Stanzas { get; init; } = [];

    /// <summary>给界面「最近抓取结果」那一行用（文案由 App 层本地化，这里只给数据）。</summary>
    public bool IsSuccess => Success;
}

/// <summary>
/// 抓一个源的索引：**探测降级链 + 魔数判定 + 解压 + 解析 + Release 校验**。
/// <para>
/// 降级顺序 `.bz2` → `.gz` → 明文；每一跳都按魔数判定，不看扩展名、也不看状态码
/// （实测 bigboss 的明文 `Packages` 会 302→301 跳到营销页并最终返回 **200 HTML**）。
/// </para>
/// <para>
/// 失败**不抛**：把原因和「试过哪些地址」放进结果，让 UI 显示、让 app.log 留档。
/// </para>
/// </summary>
public sealed class IndexFetcher
{
    private readonly HttpDownloader _downloader;
    private readonly ILogSink _log;

    public IndexFetcher(HttpDownloader downloader, ILogSink? log = null)
    {
        ArgumentNullException.ThrowIfNull(downloader);
        _downloader = downloader;
        _log = log ?? NullLogSink.Instance;
    }

    public async Task<IndexFetchOutcome> FetchAsync(
        RepoDescriptor repo,
        HttpDownloadOptions options,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(options);

        // 「失败不抛」要覆盖到**所有**失败：
        // 总时长超时抛的是 OperationCanceledException、缺 Suite/Component 抛的是 InvalidOperationException，
        // 它们都不能冒泡到 UI。用户主动取消仍然照常抛（那是控制流，不是失败）。
        try
        {
            return await FetchCoreAsync(repo, options, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var message = $"超时：总时长超过 {options.TotalTimeout.TotalMinutes:0} 分钟";
            Record(repo, $"索引失败：{message}");
            return Failure(repo, message, [], TimeSpan.Zero);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var message = $"配置/环境错误：{ex.GetType().Name}: {ex.Message}";
            Record(repo, $"索引失败：{message}");
            return Failure(repo, message, [], TimeSpan.Zero);
        }
    }

    private static IndexFetchOutcome Failure(
        RepoDescriptor repo,
        string error,
        IReadOnlyList<string> attempts,
        TimeSpan elapsed)
        => new(
            repo, Success: false, IndexUrl: null, FinalUrl: null, CompressionKind.Unknown,
            CompressedBytes: 0, UncompressedBytes: 0, StanzaCount: 0, KeptCount: 0,
            ReleaseVerification.NotAvailable, elapsed, error, attempts);

    private async Task<IndexFetchOutcome> FetchCoreAsync(
        RepoDescriptor repo,
        HttpDownloadOptions options,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var attempts = new List<string>();
        var failures = new List<string>();
        var effective = WithRepoHeaders(options, repo.HeaderOverrides);

        var release = await TryFetchReleaseAsync(repo, effective, cancellationToken).ConfigureAwait(false);

        foreach (var candidate in repo.BuildIndexCandidates())
        {
            attempts.Add(candidate.Url);

            byte[] raw;
            HttpDownloadResult meta;
            try
            {
                (meta, raw) = await _downloader
                    .GetBytesAsync(candidate.Url, effective, progress, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (HttpDownloadException ex)
            {
                failures.Add($"{candidate.Url}: {ex.Message}");
                Record(repo, $"索引候选失败 {candidate.Url}：{ex.Message}");
                continue;
            }

            var head = raw.AsSpan(0, Math.Min(CompressionSniffer.SniffBytes, raw.Length));
            var kind = CompressionSniffer.Detect(head);
            if (kind is CompressionKind.Markup or CompressionKind.Unknown)
            {
                failures.Add($"{candidate.Url}: 内容是 {kind}（不是索引）");
                Record(repo, $"索引候选被跳过 {candidate.Url}：内容是 {kind}（不是索引）");
                continue;
            }

            byte[] plain;
            try
            {
                plain = Decompress(raw, kind);
            }
            catch (Exception ex)
            {
                failures.Add($"{candidate.Url}: 解压失败 {ex.GetType().Name}: {ex.Message}");
                Record(repo, $"索引解压失败 {candidate.Url}：{ex.GetType().Name}: {ex.Message}");
                continue;
            }

            var text = DecodeText(plain);
            var stanzas = ControlFileParser.Parse(text);
            var kept = CountArch(stanzas, repo.Architecture);

            if (stanzas.Count == 0)
            {
                // JSON / HTML 错误页也会「解压/解码成功」，靠 stanza 数为 0 兜住
                failures.Add($"{candidate.Url}: 解析出 0 个 stanza");
                Record(repo, $"索引解析出 0 个 stanza，跳过 {candidate.Url}");
                continue;
            }

            var verification = Verify(release, candidate.RelativePath, raw, out var verifyProblem);
            if (verification == ReleaseVerification.Mismatched)
            {
                failures.Add($"{candidate.Url}: Release 校验失败 {verifyProblem}");
                Record(repo, $"索引校验失败 {candidate.Url}：{verifyProblem}");
                continue;
            }

            Record(repo,
                $"索引成功 {candidate.Url} → {(meta.FinalUrl == candidate.Url ? "直取" : meta.FinalUrl)}；" +
                $"{raw.Length:N0} B {kind} → {plain.Length:N0} B；stanza {stanzas.Count}，收下 {kept}（{repo.Architecture}）；" +
                $"{stopwatch.Elapsed.TotalSeconds:0.0} s；Release 校验 {verification}");

            return new IndexFetchOutcome(
                repo, Success: true, candidate.Url, meta.FinalUrl, kind,
                raw.Length, plain.Length, stanzas.Count, kept, verification,
                stopwatch.Elapsed, Error: null, attempts)
            {
                Stanzas = stanzas,
            };
        }

        var error = attempts.Count == 0
            ? "没有可用的索引候选"
            : $"全部候选失败（试过 {attempts.Count} 个地址）：{string.Join("；", failures)}";

        Record(repo, $"索引失败：{error}");
        return new IndexFetchOutcome(
            repo, Success: false, IndexUrl: null, FinalUrl: null, CompressionKind.Unknown,
            CompressedBytes: 0, UncompressedBytes: 0, StanzaCount: 0, KeptCount: 0,
            ReleaseVerification.NotAvailable, stopwatch.Elapsed, error, attempts);
    }

    private async Task<ReleaseInfo?> TryFetchReleaseAsync(
        RepoDescriptor repo,
        HttpDownloadOptions options,
        CancellationToken cancellationToken)
    {
        var url = repo.BuildReleaseUrl();
        try
        {
            var (_, bytes) = await _downloader
                .GetBytesAsync(url, options, progress: null, cancellationToken)
                .ConfigureAwait(false);

            var info = ReleaseInfo.Parse(
                ControlFileParser.Parse(DecodeText(bytes)).FirstOrDefault() ?? throw new InvalidDataException("empty Release"));

            Record(repo, $"Release 读取成功 {url}：校验表 {info.FileCount} 项");
            return info;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Release 是「有则更好」：老源经常没有，没它照样能抓索引
            Record(repo, $"Release 不可用 {url}：{ex.Message}");
            return null;
        }
    }

    private static ReleaseVerification Verify(
        ReleaseInfo? release,
        string relativePath,
        byte[] raw,
        out string? problem)
    {
        problem = null;
        if (release is null || release.FileCount == 0) return ReleaseVerification.NotAvailable;
        if (!release.TryGetFileHash(relativePath, out var expected)) return ReleaseVerification.NotListed;

        if (expected.Size != raw.Length)
        {
            problem = $"长度 {raw.Length} != Release 的 {expected.Size}";
            return ReleaseVerification.Mismatched;
        }

        if (expected.Sha256 is { Length: > 0 } sha)
        {
            var actual = Convert.ToHexStringLower(SHA256.HashData(raw));
            if (!string.Equals(actual, sha, StringComparison.OrdinalIgnoreCase))
            {
                problem = $"SHA256 {actual} != {sha}";
                return ReleaseVerification.Mismatched;
            }

            return ReleaseVerification.Matched;
        }

        if (expected.Md5 is { Length: > 0 } md5)
        {
            var actual = Convert.ToHexStringLower(MD5.HashData(raw));
            if (!string.Equals(actual, md5, StringComparison.OrdinalIgnoreCase))
            {
                problem = $"MD5 {actual} != {md5}";
                return ReleaseVerification.Mismatched;
            }

            return ReleaseVerification.Matched;
        }

        return ReleaseVerification.NotListed;
    }

    private static byte[] Decompress(byte[] raw, CompressionKind kind)
    {
        if (kind == CompressionKind.None) return raw;

        using var input = new MemoryStream(raw, writable: false);
        using var decompressed = CompressionSniffer.OpenDecompressor(input, kind);
        using var output = new MemoryStream();
        decompressed.CopyTo(output);
        return output.ToArray();
    }

    private static string DecodeText(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes);
        return text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;
    }

    private static int CountArch(IReadOnlyList<ControlStanza> stanzas, string architecture)
        => stanzas.Count(stanza =>
            string.Equals(stanza.Get("Architecture"), architecture, StringComparison.OrdinalIgnoreCase));

    private static HttpDownloadOptions WithRepoHeaders(
        HttpDownloadOptions options,
        IReadOnlyDictionary<string, string>? overrides)
    {
        if (overrides is null || overrides.Count == 0) return options;

        var merged = new Dictionary<string, string>(options.Headers, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in overrides) merged[name] = value;
        return options with { Headers = merged };
    }

    private void Record(RepoDescriptor repo, string message) => _log.Write($"[{repo.Name}] {message}");
}




using System.Diagnostics;
using System.Net;
using System.Net.Http;

namespace CyDownload.Core.Net;

/// <summary>下载进度快照。</summary>
public sealed record DownloadProgress(long BytesReceived, long? TotalBytes)
{
    /// <summary>总长未知时为 null（有些老服务器不给 Content-Length）。</summary>
    public double? Percent => TotalBytes is > 0 and var total ? BytesReceived * 100.0 / total : null;
}

/// <summary>一次下载的参数。默认值按实测调（bigboss 索引 1.27 MB 下载耗时 95 s）。</summary>
public sealed record HttpDownloadOptions
{
    /// <summary>「多久没有收到任何数据」判卡死。**不是总时长**——老源索引可以下 95 秒。</summary>
    public TimeSpan NoProgressTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>总时长上限（兜底，防止慢速持续传输永不结束）。</summary>
    public TimeSpan TotalTimeout { get; init; } = TimeSpan.FromMinutes(10);

    public int MaxRedirects { get; init; } = 5;

    /// <summary>Cydia 时代的 UA；实测对索引内容无影响（saurik/bigboss 两组头返回完全一致），保留是为了兼容。</summary>
    public string UserAgent { get; init; } = "Telesphoreo APT-HTTP/1.0.592";

    /// <summary>附加请求头（自定义伪造头走这里，例如 X-Machine / X-Firmware）。</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();

    public int BufferSize { get; init; } = 80 * 1024;
}

/// <summary>下载结果（含**跟随重定向后的最终 URL** —— saurik 的 deb 会 302 到 cache.saurik.com）。</summary>
public sealed record HttpDownloadResult(
    string RequestedUrl,
    string FinalUrl,
    int StatusCode,
    long Bytes,
    IReadOnlyList<string> RedirectChain,
    TimeSpan Elapsed,
    string? ContentType,
    string? ETag,
    DateTimeOffset? LastModified);

/// <summary>下载失败。**消息面向开发者/日志（英文）**；界面层按错误码本地化。</summary>
public sealed class HttpDownloadException : Exception
{
    public HttpDownloadException(string message, string url)
        : base(message) => Url = url;

    public HttpDownloadException(string message, string url, Exception inner)
        : base(message, inner) => Url = url;

    public HttpDownloadException(string message, string url, int statusCode)
        : base(message) => (Url, StatusCode) = (url, statusCode);

    public string Url { get; }

    public int? StatusCode { get; }
}

/// <summary>
/// 极简 HTTP 下载器：跟随重定向（≤5 跳）、Content-Length 进度、**按「无进度」判卡死**。
/// <para>
/// 不做断点续传（非目标）——实测 apt.gs 的 Range 实现不可靠（`bytes=0-63` 会返回尾部区间）。
/// </para>
/// </summary>
public sealed class HttpDownloader
{
    private readonly HttpClient _client;

    public HttpDownloader(HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
    }

    /// <summary>
    /// 生产用 handler：**必须关闭自动重定向**。
    /// <para>
    /// `HttpClientHandler.AllowAutoRedirect` 默认 `true`，handler 会自己跟完重定向，
    /// 于是 <see cref="HttpDownloadResult.FinalUrl"/> 只会等于请求 URL、`RedirectChain` 只有 1 条、
    /// <see cref="HttpDownloadOptions.MaxRedirects"/> 形同虚设（实际是 handler 默认的 50 跳）。
    /// 测试里用假 handler 不自动跟随，所以测试与生产会分叉——这里把默认值定死。
    /// </para>
    /// </summary>
    public static HttpClientHandler CreateHandler() => new() { AllowAutoRedirect = false };

    /// <summary>生产用 <see cref="HttpClient"/>：不自动重定向 + 总超时交给 <see cref="HttpDownloadOptions"/> 管。</summary>
    public static HttpClient CreateClient() => new(CreateHandler()) { Timeout = Timeout.InfiniteTimeSpan };
    /// <summary>把 URL 的内容写进 <paramref name="destination"/>（调用方负责流的生命周期与落盘原子性）。</summary>
    public async Task<HttpDownloadResult> DownloadToStreamAsync(
        string url,
        Stream destination,
        HttpDownloadOptions options,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(options);

        if (!Uri.TryCreate(url, UriKind.Absolute, out var current) ||
            (current.Scheme != Uri.UriSchemeHttp && current.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException($"Not an absolute http(s) URL: '{url}'.", nameof(url));
        }

        var stopwatch = Stopwatch.StartNew();
        var chain = new List<string>();

        using var totalCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        totalCts.CancelAfter(options.TotalTimeout);

        for (var hop = 0; hop <= options.MaxRedirects; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.TryAddWithoutValidation("User-Agent", options.UserAgent);
            request.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
            request.Headers.TryAddWithoutValidation("Connection", "close");
            foreach (var (name, value) in options.Headers)
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }

            using var response = await _client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, totalCts.Token)
                .ConfigureAwait(false);

            chain.Add($"{(int)response.StatusCode} {current}");
            var status = (int)response.StatusCode;

            if (IsRedirect(response.StatusCode))
            {
                var location = response.Headers.Location
                    ?? throw new HttpDownloadException(
                        $"Redirect {status} without Location header: {current}", current.ToString(), status);

                current = location.IsAbsoluteUri ? location : new Uri(current, location);
                continue;
            }

            if (status != (int)HttpStatusCode.OK)
            {
                throw new HttpDownloadException($"HTTP {status} for {current}", current.ToString(), status);
            }

            var totalBytes = response.Content.Headers.ContentLength;
            var contentType = response.Content.Headers.ContentType?.ToString();
            var etag = response.Headers.ETag?.Tag;
            var lastModified = response.Content.Headers.LastModified;

            await using var stream = await response.Content.ReadAsStreamAsync(totalCts.Token).ConfigureAwait(false);
            using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(totalCts.Token);
            idleCts.CancelAfter(options.NoProgressTimeout);

            var buffer = new byte[options.BufferSize];
            long received = 0;

            while (true)
            {
                int read;
                try
                {
                    read = await stream.ReadAsync(buffer, idleCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested &&
                                                         !totalCts.IsCancellationRequested)
                {
                    throw new HttpDownloadException(
                        $"No data received for {options.NoProgressTimeout.TotalSeconds:0} s: {current}",
                        current.ToString());
                }

                if (read <= 0) break;

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                received += read;

                idleCts.CancelAfter(options.NoProgressTimeout);      // 每个数据块重置看门狗
                progress?.Report(new DownloadProgress(received, totalBytes));
            }

            return new HttpDownloadResult(
                url, current.ToString(), status, received, chain, stopwatch.Elapsed,
                contentType, etag, lastModified);
        }

        throw new HttpDownloadException($"Too many redirects (>{options.MaxRedirects}): {url}", url);
    }

    /// <summary>取整块内容（索引这种几十 KB ~ 几 MB 的对象用）。</summary>
    public async Task<(HttpDownloadResult Result, byte[] Bytes)> GetBytesAsync(
        string url,
        HttpDownloadOptions options,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var result = await DownloadToStreamAsync(url, buffer, options, progress, cancellationToken).ConfigureAwait(false);
        return (result, buffer.ToArray());
    }

    private static bool IsRedirect(HttpStatusCode status) => status is
        HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or
        HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;
}


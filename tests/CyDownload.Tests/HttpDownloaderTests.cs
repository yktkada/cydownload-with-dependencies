using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using CyDownload.Core.Net;
using Xunit;

namespace CyDownload.Tests;

/// <summary>
/// HTTP 下载器：重定向、状态码、**按无进度判卡死**。
/// 全部用假 <see cref="HttpMessageHandler"/>，不碰真实网络。
/// </summary>
public sealed class HttpDownloaderTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.ToString());
            return Task.FromResult(_responder(request));
        }
    }

    private static HttpDownloader Downloader(StubHandler handler)
        => new(new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan });

    private static HttpResponseMessage Ok(byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private static HttpResponseMessage Redirect(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }

    private static readonly HttpDownloadOptions FastOptions = new()
    {
        NoProgressTimeout = TimeSpan.FromSeconds(5),
        TotalTimeout = TimeSpan.FromSeconds(30),
        MaxRedirects = 3,
    };

    [Fact]
    public async Task GetBytes_FollowsRedirect_AndReportsFinalUrl()
    {
        var handler = new StubHandler(request =>
            request.RequestUri!.Host == "apt.example.com"
                ? Redirect("http://cache.example.com/debs/x.deb")
                : Ok([1, 2, 3, 4]));

        var (result, bytes) = await Downloader(handler).GetBytesAsync(
            "http://apt.example.com/debs/x.deb", FastOptions, progress: null, CancellationToken.None);

        Assert.Equal([1, 2, 3, 4], bytes);
        Assert.Equal("http://apt.example.com/debs/x.deb", result.RequestedUrl);
        Assert.Equal("http://cache.example.com/debs/x.deb", result.FinalUrl);
        Assert.Equal(2, result.RedirectChain.Count);
        Assert.StartsWith("302 ", result.RedirectChain[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task NonSuccessStatus_ThrowsWithStatusCode()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var exception = await Assert.ThrowsAsync<HttpDownloadException>(() =>
            Downloader(handler).GetBytesAsync("http://x.example.com/a", FastOptions, null, CancellationToken.None));

        Assert.Equal(404, exception.StatusCode);
        Assert.Contains("HTTP 404", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TooManyRedirects_Throws()
    {
        var handler = new StubHandler(_ => Redirect("http://loop.example.com/a"));

        var exception = await Assert.ThrowsAsync<HttpDownloadException>(() =>
            Downloader(handler).GetBytesAsync("http://loop.example.com/a", FastOptions, null, CancellationToken.None));

        Assert.Contains("redirect", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RedirectWithoutLocation_Throws()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Found));

        await Assert.ThrowsAsync<HttpDownloadException>(() =>
            Downloader(handler).GetBytesAsync("http://x.example.com/a", FastOptions, null, CancellationToken.None));
    }

    [Fact]
    public async Task NonHttpUrl_IsRejected()
    {
        var handler = new StubHandler(_ => Ok([]));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Downloader(handler).GetBytesAsync("ftp://x.example.com/a", FastOptions, null, CancellationToken.None));
    }

    [Fact]
    public async Task StalledStream_ThrowsByNoProgressTimeout()
    {
        // 只吐 1 个字节然后永久挂住：必须按「无进度」判卡死，而不是等总超时
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new StallingStream()),
        });

        var options = FastOptions with { NoProgressTimeout = TimeSpan.FromMilliseconds(300) };

        var exception = await Assert.ThrowsAsync<HttpDownloadException>(() =>
            Downloader(handler).GetBytesAsync("http://slow.example.com/a", options, null, CancellationToken.None));

        Assert.Contains("No data received", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Progress_ReportsGrowingByteCounts()
    {
        var handler = new StubHandler(_ => Ok(Enumerable.Range(0, 200_000).Select(i => (byte)i).ToArray()));
        var reports = new List<DownloadProgress>();

        await Downloader(handler).GetBytesAsync(
            "http://x.example.com/big", FastOptions,
            new CollectingProgress(reports), CancellationToken.None);

        Assert.NotEmpty(reports);
        Assert.Equal(200_000, reports[^1].BytesReceived);
        Assert.True(reports[^1].Percent is > 99 and <= 100);
    }

    [Fact]
    public async Task UserAgentAndCustomHeaders_AreSent()
    {
        HttpRequestMessage? captured = null;
        var handler = new StubHandler(request =>
        {
            captured = request;
            return Ok([1]);
        });

        var options = FastOptions with
        {
            Headers = new Dictionary<string, string> { ["X-Machine"] = "iPhone4,1", ["X-Firmware"] = "6.1.3" },
        };

        await Downloader(handler).GetBytesAsync("http://x.example.com/a", options, null, CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal("iPhone4,1", captured!.Headers.GetValues("X-Machine").Single());
        Assert.Equal("6.1.3", captured.Headers.GetValues("X-Firmware").Single());
        Assert.Contains("Telesphoreo", captured.Headers.UserAgent.ToString(), StringComparison.Ordinal);
    }

    private sealed class CollectingProgress(List<DownloadProgress> sink) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => sink.Add(value);
    }
    /// <summary>先给 1 字节，然后永远不返回（模拟网络卡死）。</summary>
    private sealed class StallingStream : Stream
    {
        private bool _delivered;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, CancellationToken.None).GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (!_delivered)
            {
                _delivered = true;
                buffer.Span[0] = (byte)'!';
                return 1;
            }

            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

}


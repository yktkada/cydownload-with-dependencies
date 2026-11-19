using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using CyDownload.Core.Diagnostics;
using CyDownload.Core.Fetch;
using CyDownload.Core.Net;
using CyDownload.Core.Repos;
using Xunit;

namespace CyDownload.Tests;

/// <summary>
/// 抓取链：**降级顺序 + 魔数判定 + Release 校验 + 失败不抛**。
/// 用真实 saurik 索引夹具（`saurik-Packages.bz2`，364 包）+ 假 handler，不碰网络。
/// </summary>
public sealed class IndexFetcherTests
{
    private static string FixturePath(params string[] parts)
        => Path.Combine([AppContext.BaseDirectory, "Fixtures", .. parts]);

    private static byte[] SaurikIndex() => File.ReadAllBytes(FixturePath("saurik-Packages.bz2"));

    private static RepoDescriptor Saurik() => new()
    {
        Name = "saurik",
        BaseUrl = "http://apt.example.com/",
        Layout = RepoLayout.Dists,
        Suite = "ios/793.00",
        Component = "main",
        Priority = 1,
    };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<string, HttpResponseMessage> _route;

        public StubHandler(Func<string, HttpResponseMessage> route) => _route = route;

        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add(url);
            return Task.FromResult(_route(url));
        }
    }

    private static HttpResponseMessage Ok(byte[] bytes, string contentType = "application/octet-stream")
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private static HttpResponseMessage NotFound() => new(HttpStatusCode.NotFound);

    private static readonly HttpDownloadOptions Fast = new()
    {
        NoProgressTimeout = TimeSpan.FromSeconds(5),
        TotalTimeout = TimeSpan.FromSeconds(30),
    };

    private static IndexFetcher Fetcher(StubHandler handler, List<string>? log = null)
        => new(
            new HttpDownloader(new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan }),
            log is null ? NullLogSink.Instance : new CollectingLog(log));

    private sealed class CollectingLog(List<string> sink) : ILogSink
    {
        public void Write(string line) => sink.Add(line);
    }

    private static string ReleaseWithSha256(byte[] bytes, string relativePath)
    {
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        return $"""
            Origin: Test
            Suite: ios/793.00
            Components: main
            SHA256:
             {sha} {bytes.Length} {relativePath}
            """;
    }

    private static string ReleaseUrl() 
        => "http://apt.example.com/dists/ios/793.00/Release";

    private static string Bz2Url()
        => "http://apt.example.com/dists/ios/793.00/main/binary-iphoneos-arm/Packages.bz2";

    [Fact]
    public async Task Success_WithMatchingReleaseHash()
    {
        var index = SaurikIndex();
        var log = new List<string>();
        var handler = new StubHandler(url => url switch
        {
            _ when url == ReleaseUrl() => Ok(Encoding.UTF8.GetBytes(
                ReleaseWithSha256(index, "main/binary-iphoneos-arm/Packages.bz2"))),
            _ when url == Bz2Url() => Ok(index),
            _ => NotFound(),
        });

        var outcome = await Fetcher(handler, log).FetchAsync(Saurik(), Fast, null, CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.Equal(CompressionKind.Bzip2, outcome.Compression);
        Assert.Equal(364, outcome.StanzaCount);
        Assert.Equal(364, outcome.KeptCount);
        Assert.Equal(ReleaseVerification.Matched, outcome.Verification);
        Assert.Equal(index.Length, outcome.CompressedBytes);
        Assert.True(outcome.UncompressedBytes > index.Length);
        Assert.Null(outcome.Error);
        Assert.NotEmpty(log);
    }

    [Fact]
    public async Task HashMismatch_FailsAllCandidates()
    {
        var index = SaurikIndex();
        var badRelease = ReleaseWithSha256(index, "main/binary-iphoneos-arm/Packages.bz2")
            .Replace(Convert.ToHexStringLower(SHA256.HashData(index)), new string('0', 64), StringComparison.Ordinal);

        var handler = new StubHandler(url => url switch
        {
            _ when url == ReleaseUrl() => Ok(Encoding.UTF8.GetBytes(badRelease)),
            _ when url == Bz2Url() => Ok(index),
            _ => NotFound(),
        });

        var outcome = await Fetcher(handler).FetchAsync(Saurik(), Fast, null, CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Equal(3, outcome.Attempts.Count);       // bz2 / gz / plain 全试过
        Assert.Contains("校验", outcome.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HtmlPage_IsSkipped_AndPlainTextFallbackWorks()
    {
        const string plainIndex = """
            Package: bash
            Version: 4.0.44-16
            Architecture: iphoneos-arm

            Package: sed
            Version: 4.1.5-8
            Architecture: iphoneos-arm
            """;

        var handler = new StubHandler(url => url switch
        {
            _ when url.EndsWith("Packages.bz2", StringComparison.Ordinal) =>
                Ok(Encoding.UTF8.GetBytes("<!DOCTYPE html><html><body>hosting</body></html>"), "text/html"),
            _ when url.EndsWith("/Packages", StringComparison.Ordinal) => Ok(Encoding.UTF8.GetBytes(plainIndex)),
            _ => NotFound(),
        });

        var outcome = await Fetcher(handler).FetchAsync(Saurik(), Fast, null, CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.Equal(CompressionKind.None, outcome.Compression);   // 明文候选
        Assert.Equal(2, outcome.StanzaCount);
    }

    [Fact]
    public async Task AllCandidatesFail_ReturnsFailureWithoutThrowing()
    {
        var handler = new StubHandler(_ => NotFound());

        var outcome = await Fetcher(handler).FetchAsync(Saurik(), Fast, null, CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Equal(0, outcome.StanzaCount);
        Assert.Equal(3, outcome.Attempts.Count);
        Assert.Contains("全部候选失败", outcome.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ArchFilter_CountsOnlyTargetArchitecture()
    {
        const string mixed = """
            Package: old
            Version: 1.0
            Architecture: iphoneos-arm

            Package: modern
            Version: 1.0
            Architecture: iphoneos-arm64

            Package: old2
            Version: 2.0
            Architecture: iphoneos-arm
            """;

        var handler = new StubHandler(url => url switch
        {
            _ when url.EndsWith("/Packages", StringComparison.Ordinal) => Ok(Encoding.UTF8.GetBytes(mixed)),
            _ => NotFound(),
        });

        var outcome = await Fetcher(handler).FetchAsync(Saurik(), Fast, null, CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.Equal(3, outcome.StanzaCount);
        Assert.Equal(2, outcome.KeptCount);           // arm64 被过滤
    }

    [Fact]
    public async Task MissingRelease_IsTolerated_AndReportedAsNotAvailable()
    {
        var handler = new StubHandler(url => url switch
        {
            _ when url == Bz2Url() => Ok(SaurikIndex()),
            _ => NotFound(),
        });

        var outcome = await Fetcher(handler).FetchAsync(Saurik(), Fast, null, CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.Equal(ReleaseVerification.NotAvailable, outcome.Verification);
    }

    [Fact]
    public async Task RepoHeaderOverrides_AreApplied()
    {
        HttpRequestMessage? captured = null;
        var handler = new StubHandler(url =>
        {
            if (url == Bz2Url()) return Ok(SaurikIndex());
            return NotFound();
        });

        var withCapture = new HttpMessageCapture(handler, request => captured = request);
        var fetcher = new IndexFetcher(
            new HttpDownloader(new HttpClient(withCapture) { Timeout = Timeout.InfiniteTimeSpan }),
            NullLogSink.Instance);

        var repo = Saurik() with
        {
            HeaderOverrides = new Dictionary<string, string> { ["X-Machine"] = "iPhone4,1", ["X-Firmware"] = "6.1.3" },
        };

        await fetcher.FetchAsync(repo, Fast, null, CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal("iPhone4,1", captured!.Headers.GetValues("X-Machine").Single());
        Assert.Equal("6.1.3", captured.Headers.GetValues("X-Firmware").Single());
    }

    /// <summary>把每个请求先交给回调再转发（用来断言请求头）。</summary>
    private sealed class HttpMessageCapture(HttpMessageHandler inner, Action<HttpRequestMessage> onSend) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            onSend(request);
            return base.SendAsync(request, cancellationToken);
        }
    }
}


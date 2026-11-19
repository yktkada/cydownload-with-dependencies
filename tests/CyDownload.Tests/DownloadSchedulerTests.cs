using System.Net.Http;
using CyDownload.App.Infrastructure;
using Xunit;

namespace CyDownload.Tests;

/// <summary>并行下载调度器：并发上限、单源上限、重试、失败不连坐、取消。</summary>
public sealed class DownloadSchedulerTests
{
    private static DownloadJob Job(int index, string repo, Func<CancellationToken, Task<bool>> execute)
        => new(index, "pkg" + index, repo, execute);

    [Fact]
    public async Task RunAsync_RespectsGlobalParallelLimit()
    {
        var current = 0;
        var maxSeen = 0;
        var counterLock = new object();
        var jobs = Enumerable.Range(0, 12).Select(index => Job(index, "repo" + index, async token =>
        {
            lock (counterLock) { current++; maxSeen = Math.Max(maxSeen, current); }
            await Task.Delay(60, token);
            lock (counterLock) { current--; }
            return false;
        })).ToList();

        var outcomes = await DownloadScheduler.RunAsync(jobs, maxParallel: 3, maxPerRepo: 3,
            TestContext.Current.CancellationToken);

        Assert.All(outcomes, outcome => Assert.True(outcome.Ok));
        Assert.InRange(maxSeen, 1, 3);
        Assert.Equal(3, maxSeen);       // 有足够的任务，应该吃满 3 路
    }

    [Fact]
    public async Task RunAsync_RespectsPerRepoLimit()
    {
        var current = new Dictionary<string, int>(StringComparer.Ordinal);
        var maxSeen = new Dictionary<string, int>(StringComparer.Ordinal);
        var gate = new object();

        async Task<bool> Run(string repo, CancellationToken token)
        {
            lock (gate)
            {
                current[repo] = current.GetValueOrDefault(repo) + 1;
                maxSeen[repo] = Math.Max(maxSeen.GetValueOrDefault(repo), current[repo]);
            }

            await Task.Delay(50, token);

            lock (gate) current[repo] = current[repo] - 1;
            return false;
        }

        // 同一个源 6 个任务；单源上限 2 → 该源并发不超过 2
        var jobs = Enumerable.Range(0, 6).Select(index => Job(index, "aptgs", token => Run("aptgs", token)))
            .ToList();

        var outcomes = await DownloadScheduler.RunAsync(jobs, maxParallel: 6, maxPerRepo: 2,
            TestContext.Current.CancellationToken);

        Assert.All(outcomes, outcome => Assert.True(outcome.Ok));
        Assert.Equal(2, maxSeen["aptgs"]);
    }

    [Fact]
    public async Task RunAsync_RetriesFailedJob()
    {
        var attempts = 0;
        var jobs = new List<DownloadJob>
        {
            Job(0, "saurik", _ =>
            {
                attempts++;
                return attempts < 3
                    ? Task.FromException<bool>(new HttpRequestException("boom"))
                    : Task.FromResult(false);
            }),
        };

        var outcomes = await DownloadScheduler.RunAsync(jobs, 2, 2, TestContext.Current.CancellationToken);

        Assert.True(outcomes[0].Ok);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task RunAsync_FailureDoesNotStopOtherJobs()
    {
        var jobs = new List<DownloadJob>
        {
            Job(0, "a", _ => Task.FromException<bool>(new InvalidOperationException("always"))),
            Job(1, "b", _ => Task.FromResult(true)),
            Job(2, "c", _ => Task.FromResult(false)),
        };

        var outcomes = await DownloadScheduler.RunAsync(jobs, 3, 3, TestContext.Current.CancellationToken);

        Assert.False(outcomes[0].Ok);
        Assert.True(outcomes[1].Ok);
        Assert.True(outcomes[1].FromCache);
        Assert.True(outcomes[2].Ok);
    }

    [Fact]
    public async Task RunAsync_Cancel_StopsScheduling_AndReportsCanceled()
    {
        using var cts = new CancellationTokenSource();
        var started = 0;
        var jobs = Enumerable.Range(0, 20).Select(index => Job(index, "r" + index, async token =>
        {
            Interlocked.Increment(ref started);
            await Task.Delay(TimeSpan.FromSeconds(5), token);
            return false;
        })).ToList();

        var run = DownloadScheduler.RunAsync(jobs, 2, 2, cts.Token);
        await Task.Delay(150, TestContext.Current.CancellationToken);
        cts.Cancel();

        var outcomes = await run;

        Assert.Contains(outcomes, outcome => outcome.Canceled);
        Assert.True(started < jobs.Count);          // 取消后不再派发剩余任务
    }
}

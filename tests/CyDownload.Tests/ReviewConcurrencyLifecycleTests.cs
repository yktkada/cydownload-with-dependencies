using System.IO;
using System.Text.Json;
using CyDownload.App.Infrastructure;
using CyDownload.App.ViewModels;
using CyDownload.Core.Index;
using CyDownload.Core.Repos;
using Xunit;

namespace CyDownload.Tests;

/// <summary>
/// 并发/生命周期回归用例：
/// settings.json 空值、导入 0 行不清历史、调度器同源取消不越界释放、队列边界。
/// </summary>
public sealed class ReviewConcurrencyLifecycleTests
{
    // ── settings.json 里 "Target": null 不能阻断启动 ─────────────────────

    [Fact]
    public void Settings_WithNullTarget_LoadsDefaultsWithoutThrowing()
    {
        using var workspace = new TestWorkspace();
        var path = Path.Combine(workspace.Root, "settings.json");

        File.WriteAllText(path, """
            {
              "Language": "system",
              "Target": null,
              "MaxNodes": 500,
              "HttpNoProgressSeconds": 30
            }
            """);

        var settings = SettingsStore.Load(workspace.Root, out _);

        Assert.NotNull(settings.Target);
        Assert.Equal("iPhone4,1", settings.Target.ProductType);
        Assert.NotEmpty(settings.Target.Arches);
    }

    [Fact]
    public void Settings_WithNullArchesElement_FiltersItOut()
    {
        using var workspace = new TestWorkspace();
        var path = Path.Combine(workspace.Root, "settings.json");

        File.WriteAllText(path, """
            {
              "Target": { "ProductType": "iPhone4,1", "IosVersion": "6.1.3", "Arches": [null, "iphoneos-arm"] }
            }
            """);

        var settings = SettingsStore.Load(workspace.Root, out _);

        Assert.Single(settings.Target.Arches);
        Assert.Equal("iphoneos-arm", settings.Target.Arches[0]);
    }

    [Fact]
    public void Settings_ClampsTimeoutsAndParallelism()
    {
        using var workspace = new TestWorkspace();
        var path = Path.Combine(workspace.Root, "settings.json");

        File.WriteAllText(path, """
            {
              "HttpNoProgressSeconds": -5,
              "HttpTotalMinutes": 0,
              "MaxParallelDownloads": 99,
              "MaxPerRepoConcurrency": 99
            }
            """);

        var settings = SettingsStore.Load(workspace.Root, out _);

        Assert.Equal(5, settings.HttpNoProgressSeconds);
        Assert.Equal(1, settings.HttpTotalMinutes);
        Assert.Equal(8, settings.MaxParallelDownloads);
        Assert.InRange(settings.MaxPerRepoConcurrency, 1, settings.MaxParallelDownloads);
    }

    // ── 导入本轮 0 行不能清空该源该架构的历史 ───────────────────────

    [Fact]
    public void Import_WithZeroRows_KeepsExistingRows()
    {
        using var workspace = new TestWorkspace();
        using var database = ArchiveDatabase.Open(workspace.Full("archive.db"));
        var repository = new PackageRepository(database);
        var repo = new RepoDescriptor { Name = "r", BaseUrl = "http://example.invalid", Layout = RepoLayout.Flat };

        var stanzas = CyDownload.Core.Deb.ControlFileParser.Parse("""
            Package: keepme
            Version: 1.0
            Architecture: iphoneos-arm
            """);
        repository.Import(repo, stanzas, "iphoneos-arm", DateTimeOffset.Now);
        Assert.Equal(1, repository.CountPackages("r"));

        // 索引整体缺行（架构值变了/上游重定向）→ 不能把历史行删光
        repository.Import(repo, [], "iphoneos-arm", DateTimeOffset.Now);

        Assert.Equal(1, repository.CountPackages("r"));
    }

    // ── 同一源排队取消，不能抛 SemaphoreFullException ─────────────────

    [Fact]
    public async Task Scheduler_CancelWhileWaitingOnRepoGate_DoesNotThrow()
    {
        using var cts = new CancellationTokenSource();
        var started = 0;

        var jobs = Enumerable.Range(0, 4).Select(index => new DownloadJob(index, "pkg" + index, "same-repo",
            async token =>
            {
                Interlocked.Increment(ref started);
                await Task.Delay(TimeSpan.FromSeconds(5), token);
                return false;
            })).ToList();

        var run = DownloadScheduler.RunAsync(jobs, maxParallel: 4, maxPerRepo: 1, cts.Token);
        await Task.Delay(150, TestContext.Current.CancellationToken);
        cts.Cancel();

        var outcomes = await run;       // 修复前：finally 无条件 Release → SemaphoreFullException 穿出

        Assert.Contains(outcomes, outcome => outcome.Canceled);
        Assert.Equal(1, started);       // 单源上限 1：取消前只应有一个真正开始
    }
}

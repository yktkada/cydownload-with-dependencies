namespace CyDownload.App.Infrastructure;

/// <summary>一个待下载的任务：包 id + 所属源 + 实际执行体（返回是否命中缓存）。</summary>
public sealed record DownloadJob(
    int Index,
    string PackageId,
    string Repo,
    Func<CancellationToken, Task<bool>> Execute);

/// <summary>单个任务的结局。</summary>
public sealed record DownloadOutcome(
    int Index,
    string PackageId,
    bool Ok,
    bool FromCache,
    bool Canceled,
    string? Error);

/// <summary>
/// 并行下载调度器（：默认 4 路、设置可调）。
/// <para>
/// 两条硬约束：
/// <list type="bullet">
/// <item>**全局并发上限**（老源承受力有限，默认 4）；</item>
/// <item>**单源并发上限**（默认 2）：bigboss / apt.gs 这些老服务器并发一高就 502。</item>
/// </list>
/// 单个任务失败会**重试**（默认 3 次尝试、指数退避），仍失败只记录、不拖垮其它任务；
/// 取消时停止派发新任务并等待在跑的任务收尾（不抛给调用方）。
/// </para>
/// </summary>
public static class DownloadScheduler
{
    /// <summary>默认重试次数（含首次）。</summary>
    public const int DefaultMaxAttempts = 3;

    /// <summary>退避基数（第 n 次重试等 n × 300ms）。</summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(300);

    public static async Task<IReadOnlyList<DownloadOutcome>> RunAsync(
        IReadOnlyList<DownloadJob> jobs,
        int maxParallel,
        int maxPerRepo,
        CancellationToken cancellationToken,
        Action<int>? onStarted = null,
        Action<DownloadOutcome>? onFinished = null)
    {
        ArgumentNullException.ThrowIfNull(jobs);

        var outcomes = new DownloadOutcome[jobs.Count];
        if (jobs.Count == 0) return outcomes;

        var parallel = Math.Clamp(maxParallel, 1, 16);
        var perRepo = Math.Clamp(maxPerRepo, 1, parallel);

        using var globalGate = new SemaphoreSlim(parallel, parallel);
        var repoGates = new Dictionary<string, SemaphoreSlim>(StringComparer.Ordinal);

        try
        {
            await Parallel.ForEachAsync(
                jobs,
                new ParallelOptions { MaxDegreeOfParallelism = parallel, CancellationToken = cancellationToken },
                async (job, token) =>
                {
                    await globalGate.WaitAsync(token).ConfigureAwait(false);

                    SemaphoreSlim? repoGate = null;
                    var repoAcquired = false;
                    try
                    {
                        lock (repoGates)
                        {
                            if (!repoGates.TryGetValue(job.Repo, out repoGate))
                            {
                                repoGate = new SemaphoreSlim(perRepo, perRepo);
                                repoGates[job.Repo] = repoGate;
                            }
                        }

                        // 取消可能发生在等单源许可时：没拿到就绝不能 Release（否则 SemaphoreFullException，
                        // 且会顶替原始 OCE —— ）
                        await repoGate.WaitAsync(token).ConfigureAwait(false);
                        repoAcquired = true;

                        onStarted?.Invoke(job.Index);

                        var outcome = await RunOneAsync(job, token).ConfigureAwait(false);
                        outcomes[job.Index] = outcome;
                        onFinished?.Invoke(outcome);
                    }
                    finally
                    {
                        if (repoAcquired) repoGate?.Release();
                        globalGate.Release();
                    }
                }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 取消是正常收尾：把没跑完的标成取消（已跑完的结果保留）
            for (var index = 0; index < outcomes.Length; index++)
            {
                outcomes[index] ??= new DownloadOutcome(
                    index, jobs[index].PackageId, Ok: false, FromCache: false, Canceled: true, Error: "canceled");
            }
        }

        return outcomes;
    }

    private static async Task<DownloadOutcome> RunOneAsync(DownloadJob job, CancellationToken cancellationToken)
    {
        Exception? lastError = null;

        for (var attempt = 1; attempt <= DefaultMaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var fromCache = await job.Execute(cancellationToken).ConfigureAwait(false);
                return new DownloadOutcome(job.Index, job.PackageId, Ok: true, FromCache: fromCache,
                    Canceled: false, Error: null);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (attempt < DefaultMaxAttempts)
            {
                lastError = ex;
                await Task.Delay(RetryDelay * attempt, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        return new DownloadOutcome(job.Index, job.PackageId, Ok: false, FromCache: false, Canceled: false,
            Error: lastError?.GetType().Name + ": " + lastError?.Message);
    }

    /// <summary>单源并发上限的默认值（老源 502 较多，先保守）。</summary>
    public const int DefaultMaxPerRepo = 2;
}

using CyDownload.Core.Diagnostics;
using CyDownload.Core.Index;

namespace CyDownload.Core.Resolve;

/// <summary>
/// 候选来源。抽成接口是为了让求解器能在没有数据库的情况下单测（`InMemoryPackageSource`）。
/// <para>
/// **两个实现必须语义一致**：真实包与「提供同名虚拟包」的候选要合并返回，
/// 不能因为真实包命中就跳过 provides。
/// </para>
/// </summary>
public interface IPackageSource
{
    /// <summary>
    /// 找能满足这个名字的候选：既有**真实包**（`package_id` 命中），也有**提供该名字的虚拟包**（`provides` 命中）。
    /// 返回顺序无所谓——排序由求解器负责。
    /// </summary>
    IReadOnlyList<PackageCandidate> Find(string packageName);
}

/// <summary>
/// 把 <see cref="PackageRepository"/> 包成候选来源（查 `packages` + `provides`，并带上源优先级）。
/// <para>
/// <b>架构过滤在这里做</b>：只返回 <c>architectures</c> 里的行。
/// 不传（null）表示不过滤——只给调试/探针用，产品必须传目标的 arches。
/// </para>
/// </summary>
public sealed class DatabasePackageSource : IPackageSource
{
    private readonly PackageRepository _repository;
    private readonly IReadOnlyCollection<string>? _architectures;
    private readonly ILogSink _log;

    public DatabasePackageSource(
        PackageRepository repository,
        IReadOnlyCollection<string>? architectures = null,
        ILogSink? log = null)
    {
        ArgumentNullException.ThrowIfNull(repository);
        _repository = repository;
        _architectures = architectures is { Count: > 0 } ? architectures : null;
        _log = log ?? NullLogSink.Instance;
    }

    public IReadOnlyList<PackageCandidate> Find(string packageName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageName);

        var direct = _repository.CandidatesByName(packageName, _architectures);
        var provided = _repository.CandidatesByProvidedName(packageName, _architectures);

        if (provided.Count == 0) return direct;

        // 去重（同一行既是真实包又提供同名的情况很少，但不能出现两份）
        var seen = new HashSet<long>(direct.Select(candidate => candidate.Package.Id));
        var merged = new List<PackageCandidate>(direct.Count + provided.Count);
        merged.AddRange(direct);
        var added = 0;
        foreach (var candidate in provided)
        {
            if (seen.Add(candidate.Package.Id)) { merged.Add(candidate); added++; }
        }

        if (added > 0)
        {
            _log.Write($"[resolve] {packageName}：真实包 {direct.Count} 个 + 虚拟包 {added} 个");
        }

        return merged;
    }
}

/// <summary>内存候选源（单测 / 原型用）。语义与 <see cref="DatabasePackageSource"/> 一致：真实包 + provides 合并。</summary>
public sealed class InMemoryPackageSource : IPackageSource
{
    private readonly Dictionary<string, List<PackageCandidate>> _byPackageId = new(StringComparer.Ordinal);
    private readonly List<PackageCandidate> _all = [];
    private readonly IReadOnlyCollection<string>? _architectures;

    public InMemoryPackageSource(IReadOnlyCollection<string>? architectures = null)
        => _architectures = architectures is { Count: > 0 } ? architectures : null;

    public int Count => _all.Count;

    public InMemoryPackageSource Add(PackageRow package, int repoPriority = 100)
    {
        ArgumentNullException.ThrowIfNull(package);

        var candidate = new PackageCandidate(package, repoPriority);
        _all.Add(candidate);

        if (!_byPackageId.TryGetValue(package.PackageId, out var list))
        {
            list = [];
            _byPackageId[package.PackageId] = list;
        }

        list.Add(candidate);
        return this;
    }

    public IReadOnlyList<PackageCandidate> Find(string packageName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageName);

        var result = new List<PackageCandidate>();

        if (_byPackageId.TryGetValue(packageName, out var direct)) result.AddRange(direct);

        // 与 DB 实现一致：**真实包命中也要继续看 provides**
        foreach (var candidate in _all)
        {
            if (string.Equals(candidate.Package.PackageId, packageName, StringComparison.Ordinal)) continue;
            if (!candidate.Package.ProvidesName(packageName)) continue;
            result.Add(candidate);
        }

        if (_architectures is null) return result;

        return result
            .Where(candidate => _architectures.Contains(candidate.Package.Architecture, StringComparer.OrdinalIgnoreCase))
            .ToList();
    }
}

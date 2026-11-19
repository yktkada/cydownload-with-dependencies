using CyDownload.Core.Deb;
using CyDownload.Core.Diagnostics;
using CyDownload.Core.Index;

namespace CyDownload.Core.Resolve;

/// <summary>
/// 依赖闭包求解。
/// <para>
/// 规则：只解 `Depends` + `Pre-Depends`；`a | b` 取「已满足 / 已选中 / 能装上的第一个」；
/// 支持 `Provides` 虚拟包；**候选按 Debian 版本降序、同版本按源优先级升序**（实测同名同版本会跨源出现）。
/// </para>
/// <para>
/// 求解是**带回溯的 DFS**：选了某个高版本导致下游无解时会退回来换低版本——
/// 只做贪心的话会出现「明明有解却报未解析」。
/// 节点数与「每个名字最多试几个候选」都有上限，超限直接判失败而不是卡死。
/// </para>
/// <para>环不会死循环：依赖一个已选中且满足的包时直接视为满足（真实环 `mobilesubstrate ↔ safemode`）。</para>
/// </summary>
public sealed class DependencyResolver
{
    private readonly IPackageSource _source;
    private readonly BasePackageProvider _base;
    private readonly ILogSink _log;

    public DependencyResolver(IPackageSource source, BasePackageProvider basePackages, ILogSink? log = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(basePackages);

        _source = source;
        _base = basePackages;
        _log = log ?? NullLogSink.Instance;
    }

    /// <summary>算一组目标的闭包（多选 = 一次求解求并集，共享依赖只算一次）。</summary>
    public ResolutionResult Resolve(IEnumerable<string> targets, ResolveOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(targets);

        var targetNames = targets
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (targetNames.Count == 0) return ResolutionResult.Failed("no targets");

        var settings = options ?? new ResolveOptions();
        var state = new State(targetNames);
        var pending = new List<DependencyClause>(targetNames.Count);

        // 每个目标就是一个「无约束的单项 clause」——复用同一套求解逻辑
        foreach (var target in targetNames)
        {
            pending.Add(new DependencyClause([new Dependency(target, Constraint: null)]));
        }

        var solved = TrySolve(pending, 0, state, settings);

        if (!solved)
        {
            var reason = state.LimitExceeded
                ? $"节点数超过上限 {settings.MaxNodes}（源数据可能异常，或依赖链过深）"
                : "存在无法满足的依赖";
            var diagnosis = Diagnose(targetNames, settings);
            _log.Write($"[resolve] 失败：{reason}；未解析 {diagnosis.Count} 条");
            return new ResolutionResult(false, [], [], diagnosis, [], reason, state.Nodes);
        }

        var result = BuildResult(state, targetNames);
        _log.Write(
            $"[resolve] 成功：下载 {result.ToDownload.Count} 个（{result.TotalBytes:N0} B），" +
            $"已满足 {result.Satisfied.Count} 条，环 {result.Cycles.Count} 个，展开 {state.Nodes} 个节点");
        return result;
    }

    // ── DFS ─────────────────────────────────────────────────────────────────

    private bool TrySolve(List<DependencyClause> pending, int position, State state, ResolveOptions options)
    {
        while (position < pending.Count)
        {
            var clause = pending[position];

            if (ClauseSatisfied(clause, state)) { position++; continue; }

            foreach (var alternative in clause.Alternatives)
            {
                // 这个名字已经选过但不满足约束 → 这个备选走不通，换下一个备选
                if (state.Selected.ContainsKey(alternative.Name)) continue;

                foreach (var candidate in EnumerateCandidates(alternative, options))
                {
                    if (++state.Nodes > options.MaxNodes)
                    {
                        state.LimitExceeded = true;
                        return false;
                    }

                    state.Selected[alternative.Name] = candidate;

                    var appended = AppendDependencies(pending, candidate.Package);
                    if (TrySolve(pending, position + 1, state, options)) return true;

                    pending.RemoveRange(pending.Count - appended, appended);
                    state.Selected.Remove(alternative.Name);

                    if (state.LimitExceeded) return false;
                }
            }

            return false;      // 这一条 clause 怎么都满足不了 → 让上层回溯
        }

        return true;
    }

    private static int AppendDependencies(List<DependencyClause> pending, PackageRow package)
    {
        var dependencies = package.Dependencies();
        foreach (var clause in dependencies.Clauses) pending.Add(clause);
        return dependencies.Clauses.Count;
    }

    /// <summary>clause 是否已被 base 或「已选中的包」满足（含虚拟包与环）。</summary>
    private bool ClauseSatisfied(DependencyClause clause, State state)
    {
        foreach (var alternative in clause.Alternatives)
        {
            if (_base.IsSatisfied(alternative.Name, alternative.Constraint, out _)) return true;

            foreach (var selected in state.Selected.Values)
            {
                if (selected.Package.Satisfies(alternative.Name, alternative.Constraint)) return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 候选：满足约束 → 版本降序 → 源优先级升序 → 源名（稳定），截断到每名上限。
    /// <para>
    /// **依赖表达式解析不出来的包直接不可选**——我们没法确认它还要什么，选它等于赌。
    /// </para>
    /// <para>
    /// **`firmware` 永远不从源里取**：它是伪包，只由 base 判定；
    /// 约束不满足就是「这个候选不可用」，不能去源里找同名真实包。
    /// </para>
    /// </summary>
    private IEnumerable<PackageCandidate> EnumerateCandidates(Dependency dependency, ResolveOptions options)
    {
        if (string.Equals(dependency.Name, "firmware", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var candidates = new List<PackageCandidate>();
        foreach (var candidate in _source.Find(dependency.Name))
        {
            // 先验版本串：真实库里存在 `1.0_biuld1008` 这类脏版本，Satisfies 之前必须能安全跳过
            if (!DebianVersion.TryParse(candidate.Package.Version, out _)) continue;
            if (!candidate.Package.Satisfies(dependency.Name, dependency.Constraint)) continue;
            if (candidate.Package.Dependencies().Unparsed.Count > 0) continue;         // 依赖写坏了 → 不可选
            candidates.Add(candidate);
        }

        // 用户指定的版本（浏览器里选了 1.5，就不能自动挑 1.6）
        if (options.PinnedTargets is { Count: > 0 } pinned &&
            pinned.TryGetValue(dependency.Name, out var pinnedVersion))
        {
            candidates = candidates
                .Where(candidate => string.Equals(candidate.Package.Version, pinnedVersion, StringComparison.Ordinal))
                .ToList();
        }

        return candidates
            .OrderByDescending(candidate => candidate.ParsedVersion, DebianVersion.Comparer)
            .ThenBy(candidate => candidate.RepoPriority)
            .ThenBy(candidate => candidate.Package.Repo, StringComparer.Ordinal)
            .Take(options.MaxCandidatesPerName);
    }

    // ── 结果组装 ────────────────────────────────────────────────────────────

    private ResolutionResult BuildResult(State state, IReadOnlyList<string> targetNames)
    {
        var targets = new HashSet<string>(targetNames, StringComparer.Ordinal);

        // 去重：同一行可能被两个**虚拟包名**各选中一次（`Provides: v1, v2` 而目标同时依赖 v1、v2），
        // 不去重会在下载清单里出现两条同一份 deb。
        var downloads = state.Selected.Values
            .GroupBy(candidate => RowKey(candidate.Package), StringComparer.Ordinal)
            .Select(group => group.First())
            .Select(candidate => new ResolvedPackage(
                candidate.Package,
                candidate.RepoPriority,
                targets.Contains(candidate.Package.PackageId)))
            .OrderBy(item => item.PackageId, StringComparer.Ordinal)
            .ThenByDescending(item => item.Package.Version, StringComparer.Ordinal)
            .ToList();

        var satisfied = new List<SatisfiedDependency>();
        var unresolved = new List<UnresolvedDependency>();

        foreach (var candidate in state.Selected.Values.OrderBy(item => item.Package.PackageId, StringComparer.Ordinal))
        {
            var parent = candidate.Package.PackageId;
            var dependencies = candidate.Package.Dependencies();

            foreach (var text in dependencies.Unparsed)
            {
                unresolved.Add(new UnresolvedDependency(parent, text, "依赖表达式无法解析"));
            }

            foreach (var clause in dependencies.Clauses)
            {
                var handled = false;

                foreach (var alternative in clause.Alternatives)
                {
                    if (_base.IsSatisfied(alternative.Name, alternative.Constraint, out var source))
                    {
                        satisfied.Add(new SatisfiedDependency(
                            parent, alternative.Name, source,
                            _base.TryGetBaseVersion(alternative.Name, out var baseVersion) ? baseVersion : null));
                        handled = true;
                        break;
                    }

                    if (state.Selected.Values.Any(selected =>
                            selected.Package.Satisfies(alternative.Name, alternative.Constraint)))
                    {
                        handled = true;      // 在下载清单里
                        break;
                    }
                }

                if (!handled)
                {
                    unresolved.Add(new UnresolvedDependency(parent, clause.ToString(), "源里没有可用候选"));
                }
            }
        }

        return new ResolutionResult(
            true, downloads, satisfied, unresolved, FindCycles(state), FailureReason: null, NodesVisited: state.Nodes);
    }

    private static string RowKey(PackageRow package)
        => package.PackageId + "\u0000" + package.Version + "\u0000" + package.Architecture + "\u0000" + package.Repo;
    /// <summary>诊断用：为什么这个名字没有可用候选（区分「没有」「版本不满足」「依赖写坏」）。</summary>
    private string ExplainMissing(string name, VersionConstraint? constraint)
    {
        var all = _source.Find(name);
        if (all.Count == 0) return "源里没有这个包";

        var matchingConstraint = all
            .Where(candidate => candidate.Package.Satisfies(name, constraint))
            .ToList();

        if (matchingConstraint.Count == 0) return "源里没有满足该版本约束的候选";

        if (matchingConstraint.All(candidate => candidate.Package.Dependencies().Unparsed.Count > 0))
        {
            return "候选包的依赖表达式无法解析（不敢选）";
        }

        if (matchingConstraint.All(candidate => !DebianVersion.TryParse(candidate.Package.Version, out _)))
        {
            return "候选的版本号无法解析";
        }

        return "没有可用候选";
    }

    /// <summary>找互相依赖的成对环（只报长度 2 的，够用且不会误报）。</summary>
    private static IReadOnlyList<string> FindCycles(State state)
    {
        var dependsOn = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var (name, candidate) in state.Selected)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var clause in candidate.Package.Dependencies().Clauses)
            {
                foreach (var alternative in clause.Alternatives)
                {
                    if (state.Selected.ContainsKey(alternative.Name)) set.Add(alternative.Name);
                }
            }

            dependsOn[name] = set;
        }

        var cycles = new List<string>();
        foreach (var (name, deps) in dependsOn.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            foreach (var other in deps.OrderBy(item => item, StringComparer.Ordinal))
            {
                if (!dependsOn.TryGetValue(other, out var back) || !back.Contains(name)) continue;

                var label = string.CompareOrdinal(name, other) < 0 ? $"{name} ↔ {other}" : $"{other} ↔ {name}";
                if (!cycles.Contains(label, StringComparer.Ordinal)) cycles.Add(label);
            }
        }

        return cycles;
    }

    // ── 失败诊断（贪心，只为了给用户一个原因）────────────────────────────────

    private List<UnresolvedDependency> Diagnose(IReadOnlyList<string> targetNames, ResolveOptions options)
    {
        var problems = new List<UnresolvedDependency>();
        var chosen = new Dictionary<string, PackageCandidate>(StringComparer.Ordinal);
        var queue = new Queue<(string? Parent, string Name, VersionConstraint? Constraint)>();

        foreach (var target in targetNames) queue.Enqueue((null, target, null));

        var guard = 0;
        while (queue.Count > 0 && guard++ < options.MaxNodes)
        {
            var (parent, name, constraint) = queue.Dequeue();

            if (chosen.TryGetValue(name, out var already) && already.Package.Satisfies(name, constraint)) continue;
            if (_base.IsSatisfied(name, constraint, out _)) continue;

            var candidate = EnumerateCandidates(new Dependency(name, constraint), options).FirstOrDefault();
            if (candidate is null)
            {
                problems.Add(new UnresolvedDependency(
                    parent,
                    constraint is null ? name : $"{name} ({constraint})",
                    ExplainMissing(name, constraint)));
                continue;
            }

            chosen[name] = candidate;
            foreach (var clause in candidate.Package.Dependencies().Clauses)
            {
                // 只追第一个备选，够说明问题
                var alternative = clause.First;
                queue.Enqueue((candidate.Package.PackageId, alternative.Name, alternative.Constraint));
            }
        }

        return problems;
    }

    private sealed class State
    {
        public State(IReadOnlyList<string> targets) => Targets = targets;

        public IReadOnlyList<string> Targets { get; }

        public Dictionary<string, PackageCandidate> Selected { get; } = new(StringComparer.Ordinal);

        public int Nodes { get; set; }

        public bool LimitExceeded { get; set; }
    }
}



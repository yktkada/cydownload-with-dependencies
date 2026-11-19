using CyDownload.Core.Deb;
using CyDownload.Core.Index;

namespace CyDownload.Core.Export;

/// <summary>排序结果：依赖在前；环内节点追加在末尾并单独报出。</summary>
public sealed record SortResult(IReadOnlyList<PackageRow> Ordered, IReadOnlyList<string> Cycles);

/// <summary>
/// 拓扑排序（Kahn）：**被依赖者在前**，同层按包名升序（结果可复现）。
/// <para>
/// 环必须显式处理：真实环 `mobilesubstrate ↔ com.saurik.substrate.safemode` 的两个节点
/// 入度永远不归零。这里把剩下的一律按包名追加到末尾并报给调用方，由 manifest 标注「顺序不保证」。
/// </para>
/// <para>
/// 依赖匹配用 <see cref="PackageRow.Satisfies"/>(名字, 约束)，所以**虚拟包（Provides）也能连边**。
/// </para>
/// </summary>
public static class TopologicalSorter
{
    public static SortResult Sort(IReadOnlyList<PackageRow> packages)
    {
        ArgumentNullException.ThrowIfNull(packages);

        var ordered = new List<PackageRow>(packages.Count);
        if (packages.Count == 0) return new SortResult(ordered, []);

        // 同一行可能重复出现（多目标共享依赖被重复加入等情况）——按「包名+版本+架构+源」去重
        var unique = new Dictionary<string, PackageRow>(StringComparer.Ordinal);
        foreach (var package in packages)
        {
            unique.TryAdd(RowKey(package), package);
        }

        var rows = unique.Values.OrderBy(package => package.PackageId, StringComparer.Ordinal).ToList();

        // 依赖边：package → 它依赖的（在集合内的）包
        var dependencies = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var package in rows) dependencies[RowKey(package)] = [];

        foreach (var package in rows)
        {
            var key = RowKey(package);
            foreach (var clause in package.Dependencies().Clauses)
            {
                foreach (var alternative in clause.Alternatives)
                {
                    var provider = rows.FirstOrDefault(candidate =>
                        !ReferenceEquals(candidate, package) &&
                        candidate.Satisfies(alternative.Name, alternative.Constraint));

                    if (provider is null) continue;

                    dependencies[key].Add(RowKey(provider));
                    break;
                }
            }
        }

        // 反图：被依赖者 → 依赖它的包
        var users = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var key in dependencies.Keys) users[key] = [];

        foreach (var (key, deps) in dependencies)
        {
            foreach (var dep in deps) users[dep].Add(key);
        }

        var pending = dependencies.ToDictionary(pair => pair.Key, pair => new HashSet<string>(pair.Value, StringComparer.Ordinal));
        var byKey = rows.ToDictionary(RowKey, StringComparer.Ordinal);

        var ready = pending.Where(pair => pair.Value.Count == 0)
            .Select(pair => pair.Key)
            .OrderBy(key => byKey[key].PackageId, StringComparer.Ordinal)
            .ThenBy(key => byKey[key].Version, StringComparer.Ordinal)
            .ToList();

        var done = new HashSet<string>(StringComparer.Ordinal);

        while (ready.Count > 0)
        {
            var key = ready[0];
            ready.RemoveAt(0);
            if (!done.Add(key)) continue;

            ordered.Add(byKey[key]);

            foreach (var user in users[key])
            {
                if (!pending.TryGetValue(user, out var remaining)) continue;
                remaining.Remove(key);
                if (remaining.Count == 0 && !done.Contains(user) && !ready.Contains(user))
                {
                    ready.Add(user);
                }
            }

            ready.Sort(CompareKeys);

            int CompareKeys(string left, string right)
            {
                var order = string.CompareOrdinal(byKey[left].PackageId, byKey[right].PackageId);
                return order != 0 ? order : string.CompareOrdinal(byKey[left].Version, byKey[right].Version);
            }
        }

        // Kahn 之后剩下的节点 = 环上的 + 环下游的。**只把真正在环里的报成环**，
        // 下游节点照常排进输出（它们本身没环，只是没法先于环排出）。
        var unresolvedNodes = rows.Where(row => !done.Contains(RowKey(row))).ToList();
        var cycleKeys = FindNodesInCycles(unresolvedNodes, dependencies);

        var cycles = unresolvedNodes
            .Where(row => cycleKeys.Contains(RowKey(row)))
            .OrderBy(row => row.PackageId, StringComparer.Ordinal)
            .ThenBy(row => row.Version, StringComparer.Ordinal)
            .ToList();

        foreach (var row in unresolvedNodes
                     .OrderBy(row => cycleKeys.Contains(RowKey(row)) ? 0 : 1)
                     .ThenBy(row => row.PackageId, StringComparer.Ordinal))
        {
            ordered.Add(row);
        }

        return new SortResult(ordered, cycles.Select(CycleLabel).ToList());
    }

    /// <summary>Tarjan 强连通分量：返回「分量大小 &gt; 1 或自环」的节点键。</summary>
    private static HashSet<string> FindNodesInCycles(
        IReadOnlyList<PackageRow> nodes,
        IReadOnlyDictionary<string, HashSet<string>> edges)
    {
        var index = 0;
        var indexes = new Dictionary<string, int>(StringComparer.Ordinal);
        var lowLinks = new Dictionary<string, int>(StringComparer.Ordinal);
        var onStack = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        var inCycle = new HashSet<string>(StringComparer.Ordinal);

        void Visit(string node)
        {
            indexes[node] = index;
            lowLinks[node] = index;
            index++;
            stack.Push(node);
            onStack.Add(node);

            if (edges.TryGetValue(node, out var targets))
            {
                foreach (var target in targets)
                {
                    if (!indexes.ContainsKey(target))
                    {
                        Visit(target);
                        lowLinks[node] = Math.Min(lowLinks[node], lowLinks[target]);
                    }
                    else if (onStack.Contains(target))
                    {
                        lowLinks[node] = Math.Min(lowLinks[node], indexes[target]);
                    }
                }
            }

            if (lowLinks[node] != indexes[node]) return;

            var component = new List<string>();
            string member;
            do
            {
                member = stack.Pop();
                onStack.Remove(member);
                component.Add(member);
            }
            while (!string.Equals(member, node, StringComparison.Ordinal));

            var selfLoop = component.Count == 1 && targetsOf(component[0]).Contains(component[0]);
            if (component.Count > 1 || selfLoop)
            {
                foreach (var item in component) inCycle.Add(item);
            }

            HashSet<string> targetsOf(string key) => edges.TryGetValue(key, out var set) ? set : [];
        }

        foreach (var node in nodes)
        {
            var key = RowKey(node);
            if (!indexes.ContainsKey(key)) Visit(key);
        }

        return inCycle;
    }

    private static string RowKey(PackageRow package)
        => package.PackageId + "\u0000" + package.Version + "\u0000" + package.Architecture + "\u0000" + package.Repo;

    private static string CycleLabel(PackageRow package) => $"{package.PackageId} {package.Version}";
}



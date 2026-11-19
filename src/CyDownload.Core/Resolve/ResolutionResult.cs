using CyDownload.Core.Index;

namespace CyDownload.Core.Resolve;

/// <summary>求解参数（默认值照，超限报错而非卡死」）。</summary>
public sealed record ResolveOptions
{
    /// <summary>最多展开多少个依赖节点；超过就判失败（防止源数据把内存/时间拖爆）。</summary>
    public int MaxNodes { get; init; } = 500;

    /// <summary>同一个名字最多试几个候选版本（回溯成本上限）。</summary>
    public int MaxCandidatesPerName { get; init; } = 3;

    /// <summary>
    /// 指定版本的「钉死目标」：包名 → 版本。用于「用户选中了 1.5 就不要下 1.6」。
    /// 只对目标包名生效（依赖里同名包也会被钉到该版本）。
    /// </summary>
    public IReadOnlyDictionary<string, string>? PinnedTargets { get; init; }
}

/// <summary>要下载的一个包。</summary>
public sealed record ResolvedPackage(PackageRow Package, int RepoPriority, bool IsTarget)
{
    public string PackageId => Package.PackageId;

    public string Version => Package.Version;

    public override string ToString() => $"{PackageId} {Version} [{Package.Repo}]";
}

/// <summary>被 base（预设 / 设备已装 / 伪包）满足的依赖。</summary>
public sealed record SatisfiedDependency(string? Parent, string Name, string Source, string? Version)
{
    public override string ToString() => $"{Name} ← {Source}" + (Parent is null ? "" : $"（需要者 {Parent}）");
}

/// <summary>解析不出来的依赖（源里没包 / 版本约束不满足 / 表达式坏掉）。</summary>
public sealed record UnresolvedDependency(string? Parent, string Text, string Reason)
{
    public override string ToString()
        => (Parent is null ? Text : $"{Text}（需要者 {Parent}）") + "：" + Reason;
}

/// <summary>
/// 闭包求解结果。三分区 + 环 + 失败原因。
/// </summary>
public sealed record ResolutionResult(
    bool Success,
    IReadOnlyList<ResolvedPackage> ToDownload,
    IReadOnlyList<SatisfiedDependency> Satisfied,
    IReadOnlyList<UnresolvedDependency> Unresolved,
    IReadOnlyList<string> Cycles,
    string? FailureReason,
    int NodesVisited)
{
    /// <summary>下载清单里的总字节数（缺 Size 的按 0 计）。</summary>
    public long TotalBytes => ToDownload.Sum(item => item.Package.Size ?? 0);

    public static ResolutionResult Failed(string reason) => new(
        false, [], [], [], [], reason, 0);
}

using CyDownload.Core.Deb;

namespace CyDownload.Core.Resolve;

/// <summary>
/// base 基线：哪些依赖算「已经装好」，不用下载。
/// <para>
/// 三层来源，优先级从高到低判：**伪包**（按 firmware.sh 规则生成）→ **用户导入的 status** → **31 包预设**。
/// 伪包与 `gsc.*` 永远视为满足；base 默认**可以是空的**（不假设任何已装 → 多下几个包但不会漏）。
/// </para>
/// </summary>
public sealed class BasePackageProvider
{
    /// <summary>5.x–6.x 越狱引导包的 31 个包（`freeze.tar.gz` 实测基线）。</summary>
    public static IReadOnlyDictionary<string, string> Preset31 { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["apt7-key"] = "0.7.25.3-3",
        ["apt7-lib"] = "0.7.25.3-14",
        ["base"] = "1-4",
        ["bash"] = "4.0.44-16",
        ["bzip2"] = "1.0.5-8",
        ["coreutils-bin"] = "8.12-9",
        ["cydia-lproj"] = "1.1.12",
        ["darwintools"] = "1-5",
        ["debianutils"] = "3.3.3ubuntu1-1p",
        ["diffutils"] = "2.8.1-6",
        ["dpkg"] = "1.18.10-12",
        ["findutils"] = "4.2.33-7",
        ["firmware-sbin"] = "0-1",
        ["gnupg"] = "1.4.8-4",
        ["grep"] = "2.5.4-3",
        ["gzip"] = "1.6-7",
        ["lzma"] = "4.32.7-5",
        ["ncurses"] = "5.7-15",
        ["org.thebigboss.repo.icons"] = "1.0",
        ["pam"] = "32.1-4",
        ["pam-modules"] = "36.1-5",
        ["profile.d"] = "0-2",
        ["readline"] = "6.0-8",
        ["sed"] = "4.1.5-8",
        ["shell-cmds"] = "118-7",
        ["system-cmds"] = "433.8-14",
        ["tar"] = "1.29-10",
        ["com.saurik.patcyh"] = "1.2.0",
        ["uikittools"] = "1.1.12",
        ["ldid"] = "1:1.2.1",
        ["cydia"] = "1.1.30",
    };

    private readonly Dictionary<string, string> _installed;
    private readonly Dictionary<string, string> _pseudo;

    /// <param name="target">目标设备（决定伪包）。</param>
    /// <param name="installed">用户导入的 `dpkg/status`（可为 null）。</param>
    /// <param name="usePreset">是否叠加 31 包预设（「刚刷完机」场景用 true；「已用一段时间」建议 false）。</param>
    public BasePackageProvider(
        TargetProfile target,
        IReadOnlyDictionary<string, string>? installed = null,
        bool usePreset = false)
    {
        ArgumentNullException.ThrowIfNull(target);
        Target = target;
        _installed = installed is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(installed, StringComparer.OrdinalIgnoreCase);
        _pseudo = BuildPseudoPackages(target);

        if (usePreset)
        {
            foreach (var (name, version) in Preset31)
            {
                _installed.TryAdd(name, version);
            }
        }
    }

    public TargetProfile Target { get; }

    /// <summary>生成出来的伪包（调试/界面展示用）。</summary>
    public IReadOnlyDictionary<string, string> PseudoPackages => _pseudo;

    /// <summary>base 里已知的包数（含伪包）。</summary>
    public int Count => _installed.Count + _pseudo.Count;

    /// <summary>
    /// 这个名字+约束是否已被 base 满足。<paramref name="source"/> 给出来源，便于报告区分
    /// 「31 包预设」/「设备已装」/「伪包」。
    /// </summary>
    public bool IsSatisfied(string name, VersionConstraint? constraint, out string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        // gsc.* 是设备能力伪包，一律放行（清单随设备变，无法穷举；）
        if (name.StartsWith("gsc.", StringComparison.OrdinalIgnoreCase))
        {
            source = "capability";
            return true;
        }

        if (_pseudo.TryGetValue(name, out var pseudoVersion))
        {
            source = "pseudo";
            return Matches(pseudoVersion, constraint);
        }

        if (_installed.TryGetValue(name, out var installedVersion))
        {
            source = "base";
            return Matches(installedVersion, constraint);
        }

        source = "";
        return false;
    }

    /// <summary>取 base 里该名字的版本（伪包 / 已装 / 预设；没有则 false）。</summary>
    public bool TryGetBaseVersion(string name, out string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (_pseudo.TryGetValue(name, out version!)) return true;
        return _installed.TryGetValue(name, out version!);
    }

    /// <summary>某版本是否满足约束（无约束恒真；版本串非法按不满足处理，**不抛**）。</summary>
    public static bool Matches(string actualVersion, VersionConstraint? constraint)
    {
        if (constraint is not { } required) return true;
        return DebianVersion.TryParse(actualVersion, out var actual) && required.IsSatisfiedBy(actual);
    }

    /// <summary>
    /// 按 `/usr/libexec/cydia/firmware.sh` 的规则生成伪包：
    /// `firmware`、`cy+os.*`、`cy+cpu.*`（版本恒 0）、`cy+model.*`（机型名小写 + 版本 `,`→`.`）、
    /// `cy+kernel.*`、`cy+lib.corefoundation`。
    /// </summary>
    public static Dictionary<string, string> BuildPseudoPackages(TargetProfile target)
    {
        ArgumentNullException.ThrowIfNull(target);

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["firmware"] = target.IosVersion,
            ["cy+os.ios"] = target.IosVersion,
            ["cy+cpu." + target.CpuName] = "0",
            ["cy+model." + target.ModelName.ToLowerInvariant()] = target.ModelVersion,
        };

        if (!string.IsNullOrWhiteSpace(target.KernelVersion))
        {
            map["cy+kernel.darwin"] = target.KernelVersion;
        }

        if (!string.IsNullOrWhiteSpace(target.CfVersion))
        {
            map["cy+lib.corefoundation"] = target.CfVersion;
        }

        return map;
    }
}

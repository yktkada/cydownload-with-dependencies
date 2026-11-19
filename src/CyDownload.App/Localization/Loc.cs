using System.Globalization;
using System.Windows;

namespace CyDownload.App.Localization;

/// <summary>
/// 运行时语言切换。**唯一事实源是两份 XAML 资源字典**，不是 `.resx`。
/// <para>
/// XAML 侧用 <c>{DynamicResource Key}</c>：切换语言 = 替换
/// <see cref="Application.Resources"/> 里合并字典的那一份，所有 `DynamicResource` 立即更新。
/// 代码侧用 <see cref="Get"/> / <see cref="Format"/>。
/// </para>
/// <para>
/// **必须在任何界面提示之前 <see cref="Initialize(string?, out string?)"/>**：
/// 未初始化时 <see cref="Get"/> 只能返回 <c>[键名]</c>（这是有意的可见失败，不是兜底文案）。
/// </para>
/// <para>
/// **不缓存成品显示串**：业务层返回枚举或 <c>(Key, Args)</c>，渲染时才拼。
/// </para>
/// </summary>
internal static class Loc
{
    private const string AssemblyName = "CyDownload";

    private static ResourceDictionary? _active;
    private static CultureInfo _language = CultureInfo.GetCultureInfo(LanguageRules.Chinese);

    /// <summary>语言切换完成（资源字典已替换）。订阅者负责让「非 DynamicResource 的文案」失效重算。</summary>
    public static event EventHandler? LanguageChanged;

    public static CultureInfo Language => _language;

    /// <summary>资源字典是否已加载。未加载时所有文案都会退化成 <c>[键名]</c>。</summary>
    public static bool IsInitialized => _active is not null;

    /// <summary>当前语言是否英文（只影响文案，不影响数字格式）。</summary>
    public static bool IsEnglish => !string.Equals(_language.Name, LanguageRules.Chinese, StringComparison.OrdinalIgnoreCase);

    /// <summary>启动时调用一次（不触发 <see cref="LanguageChanged"/>）。</summary>
    public static void Initialize(string? setting) => Initialize(setting, out _);

    /// <summary>
    /// 启动时调用一次；加载失败**不抛**（否则启动序列会被一条资源错误整条带走）。
    /// 失败时先退回另一种语言，两种都失败才留 <paramref name="problem"/>（调用方写日志并提示）。
    /// </summary>
    public static void Initialize(string? setting, out string? problem)
    {
        problem = null;

        var resolved = LanguageRules.Resolve(setting, CultureInfo.CurrentUICulture.Name);
        var fallback = string.Equals(resolved, LanguageRules.Chinese, StringComparison.OrdinalIgnoreCase)
            ? LanguageRules.English
            : LanguageRules.Chinese;

        foreach (var candidate in (string[])[resolved, fallback])
        {
            try
            {
                Apply(CultureInfo.GetCultureInfo(candidate), raiseEvent: false);
                return;
            }
            catch (Exception ex)
            {
                problem = $"语言资源加载失败（{candidate}）：{ex.GetType().Name}: {ex.Message}";
            }
        }

        _active = null;
    }

    /// <summary>运行时切换语言：替换字典并向所有绑定广播。</summary>
    public static void SetLanguage(string culture)
    {
        var target = CultureInfo.GetCultureInfo(
            LanguageRules.Resolve(culture, CultureInfo.CurrentUICulture.Name));

        if (string.Equals(target.Name, _language.Name, StringComparison.OrdinalIgnoreCase) && _active is not null)
        {
            return;
        }

        Apply(target, raiseEvent: true);
    }

    private static void Apply(CultureInfo culture, bool raiseEvent)
    {
        var dictionary = Load(culture);
        var previous = _active;
        _active = dictionary;
        _language = culture;

        // 先摘旧字典再挂新字典：不摘会累积，旧语言的值仍能被 DynamicResource 命中。
        var application = Application.Current;
        if (application is not null)
        {
            var merged = application.Resources.MergedDictionaries;
            if (previous is not null) merged.Remove(previous);
            merged.Add(dictionary);
        }

        if (raiseEvent) LanguageChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>
    /// 仅测试用：清空已加载的字典，用来验证「未初始化时退化行为」。
    /// 生产代码里没有任何调用点。
    /// </summary>
    internal static void ResetForTests()
    {
        var application = Application.Current;
        if (application is not null && _active is not null)
        {
            application.Resources.MergedDictionaries.Remove(_active);
        }

        _active = null;
    }

    private static ResourceDictionary Load(CultureInfo culture)
    {
        var name = culture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            ? LanguageRules.Chinese
            : LanguageRules.English;

        var uri = new Uri(
            $"pack://application:,,,/{AssemblyName};component/Localization/Strings.{name}.xaml",
            UriKind.Absolute);

        var dictionary = new ResourceDictionary { Source = uri };
        if (dictionary.Count == 0)
        {
            // 空字典几乎一定是 BAML 没打进包：明确报出来，别让界面静默显示 [键名]
            throw new InvalidOperationException($"资源字典为空：{uri}");
        }

        return dictionary;
    }

    /// <summary>
    /// 取一条文案。**缺键（或未初始化）返回 <c>[key]</c>** —— 让漏翻在界面上立刻可见，不要静默返回空串。
    /// </summary>
    public static string Get(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (_active is not null && _active[key] is string text) return text;
        return "[" + key + "]";
    }

    /// <summary>
    /// 取模板并格式化。<b>数字一律 <see cref="CultureInfo.InvariantCulture"/> 格式化</b>。
    /// 缺键时把参数一起带出来，便于排查（否则只看到 <c>[key]</c>，不知道实际值是什么）。
    /// </summary>
    public static string Format(string key, params object?[] args)
    {
        var template = Get(key);

        // 只有"未初始化 / 缺键"这一种情况才把参数带出来（那是排查线索）。
        // 已初始化时一律走模板：模板写错（漏占位符）属于资源缺陷，不该在运行时悄悄拼参数。
        if (!IsInitialized || string.Equals(template, "[" + key + "]", StringComparison.Ordinal))
        {
            if (args.Length == 0) return template;
            var rendered = string.Join("; ", args.Select(a => a?.ToString() ?? "null"));
            return $"{template} ({rendered})";
        }

        return string.Format(CultureInfo.InvariantCulture, template, args);
    }
}


namespace CyDownload.App.Localization;

/// <summary>
/// 语言解析。**纯函数**，不碰 UI、不读环境，便于单测。
/// <para>
/// 口径：显式设置优先；`system` / 空 / 未知值 → 看 <see cref="System.Globalization.CultureInfo.CurrentUICulture"/>；
/// 只有 `zh*` 判中文，其余一律英文。
/// </para>
/// </summary>
internal static class LanguageRules
{
    /// <summary>中文（默认语言；启动先加载这份，避免闪英文）。</summary>
    public const string Chinese = "zh-CN";

    /// <summary>英文。</summary>
    public const string English = "en";

    public const string System = "system";

    /// <summary>解析出实际要用哪种语言：返回 <see cref="Chinese"/> 或 <see cref="English"/>。</summary>
    public static string Resolve(string? setting, string? currentUiCultureName)
    {
        var value = (setting ?? "").Trim();

        if (value.Length > 0 && !string.Equals(value, System, StringComparison.OrdinalIgnoreCase))
        {
            if (value.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) return Chinese;
            if (value.StartsWith("en", StringComparison.OrdinalIgnoreCase)) return English;

            // 未知的显式设置：按 system 处理，避免界面卡在没人认识的语言值上
        }

        return FromCulture(currentUiCultureName);
    }

    private static string FromCulture(string? cultureName)
        => (cultureName ?? "").StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? Chinese : English;

    /// <summary>
    /// 语言显示名（下拉用）。**这里是「各语言的自称」（endonym），故意不随界面语言变化**——
    /// 中文永远显示「简体中文」、英文永远显示 English，否则用户切错语言后找不到切回来的选项。
    /// 它也是本仓库里唯一允许出现中文/英文之外的「双语字面量」的位置。
    /// </summary>
    public static string DisplayName(string culture) => culture switch
    {
        Chinese => "简体中文",
        English => "English",
        _ => culture,
    };
}

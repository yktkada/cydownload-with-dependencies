using System.Globalization;
using CyDownload.Core.Index;

namespace CyDownload.App.ViewModels;

/// <summary>源浏览器里的一行（包装 `PackageRow`，只加显示用属性）。</summary>
internal sealed class PackageItemViewModel
{
    public PackageItemViewModel(PackageRow row)
    {
        Row = row;
        SearchText = string.Join('\n', new[] { row.PackageId, row.Name ?? "", row.Description ?? "" })
            .ToLowerInvariant();
    }

    public PackageRow Row { get; }

    public string PackageId => Row.PackageId;

    public string? DisplayName => Row.Name;

    public string NameOrId => string.IsNullOrWhiteSpace(Row.Name) ? Row.PackageId : Row.Name!;

    public string Version => Row.Version;

    public string Architecture => Row.Architecture;

    public string Section => Row.Section ?? "";

    public string Repo => Row.Repo;

    /// <summary>体积（KB/MB）；索引没给 Size 时返回空串。</summary>
    public string SizeText => Row.Size is { } bytes ? FormatBytes(bytes) : "";

    public string? Depends => Row.Depends;

    /// <summary>最低 iOS（来自 `Depends` 的 firmware 约束；空 = 不限制）。</summary>
    public string? MinIos => Row.MinIos;

    /// <summary>数值体积（列头排序用）。</summary>
    public long SizeBytes => Row.Size ?? -1;

    /// <summary>搜索用：包名 + 显示名 + 描述（小写，只算一次）。</summary>
    public string SearchText { get; }

    /// <summary>双击详情窗口用的一行摘要。</summary>
    public string Description => Row.Description ?? "";

    internal static string FormatBytes(long bytes)
    {
        const double kb = 1024;
        if (bytes < kb) return bytes.ToString(CultureInfo.InvariantCulture) + " B";
        if (bytes < kb * 1024) return (bytes / kb).ToString("0.0", CultureInfo.InvariantCulture) + " KB";
        if (bytes < kb * 1024 * 1024) return (bytes / (kb * 1024)).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
        return (bytes / (kb * 1024 * 1024)).ToString("0.00", CultureInfo.InvariantCulture) + " GB";
    }

    public override string ToString() => $"{PackageId} {Version}";
}


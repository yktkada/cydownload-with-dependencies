using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using CyDownload.App.Localization;
using CyDownload.Core.Repos;

namespace CyDownload.App.Views;

/// <summary>
/// 源的新增/编辑窗口。
/// <para>
/// 设计：**名称可以留空，自动从地址的主机名生成；地址自动补协议前缀**
/// （粘 `apt.saurik.com` → 保存为 `http://apt.saurik.com`）。规范化与名称推导都在
/// <see cref="SourceUrl"/>（Core，有单测），这里只做交互与预览。
/// </para>
/// </summary>
public partial class SourceEditWindow : Window
{
    private readonly string? _originalName;
    private readonly IReadOnlyList<string> _existingNames;

    internal SourceEditWindow(RepoDescriptor? existing, IReadOnlyList<string>? existingNames = null)
    {
        _originalName = existing?.Name;
        _existingNames = existingNames ?? [];

        InitializeComponent();

        LayoutCombo.Items.Add(new ComboBoxItem { Content = Loc.Get("SourceEdit.LayoutDists"), Tag = "dists" });
        LayoutCombo.Items.Add(new ComboBoxItem { Content = Loc.Get("SourceEdit.LayoutFlat"), Tag = "flat" });

        if (existing is null)
        {
            LayoutCombo.SelectedIndex = 0;
            PriorityBox.Text = "100";
            ArchitectureBox.Text = "iphoneos-arm";
        }
        else
        {
            NameBox.Text = existing.Name;
            BaseUrlBox.Text = existing.BaseUrl;
            LayoutCombo.SelectedIndex = existing.Layout == RepoLayout.Flat ? 1 : 0;
            SuiteBox.Text = existing.Suite ?? "";
            ComponentBox.Text = existing.Component ?? "";
            PriorityBox.Text = existing.Priority.ToString(CultureInfo.InvariantCulture);
            EnabledBox.IsChecked = existing.Enabled;
            ArchitectureBox.Text = existing.Architecture;
        }

        UpdatePreview();
    }

    /// <summary>保存后的源定义（对话框返回 true 时有效）。</summary>
    public RepoDescriptor Result { get; private set; } = new() { Name = "", BaseUrl = "" };

    /// <summary>改名前的名字（null = 新增）。</summary>
    public string? OriginalName => _originalName;

    /// <summary>实时预览：最终会保存成什么（地址已补前缀、名称自动生成/去重）。</summary>
    private void OnInputChanged(object sender, TextChangedEventArgs e) => UpdatePreview();

    private void UpdatePreview()
    {
        if (PreviewText is null) return;      // 构造初期 TextChanged 可能先触发

        if (TryBuildResult(out var preview, out var error))
        {
            PreviewText.Text = Loc.Format("SourceEdit.Preview", preview.Name, preview.BaseUrl);
            return;
        }

        PreviewText.Text = error is null ? "" : Loc.Format("SourceEdit.BadAddress", error);
    }

    /// <summary>把当前输入变成合法的 <see cref="RepoDescriptor"/>（预览与保存共用同一条路径）。</summary>
    private bool TryBuildResult(out RepoDescriptor descriptor, out string? error)
    {
        descriptor = Result;

        if (!SourceUrl.TryNormalize(BaseUrlBox.Text, out var normalized, out error))
        {
            return false;
        }

        var typed = SourceUrl.SanitizeName(NameBox.Text);
        var auto = typed.Length == 0;

        var name = auto ? SourceUrl.DeriveName(normalized) : typed;
        if (string.IsNullOrWhiteSpace(name))
        {
            error = "cannot derive a name from this address";
            return false;
        }

        // 只有「名称留空自动生成」时才自动去重；用户自己写的重名要报错
        if (auto)
        {
            var taken = _existingNames
                .Where(item => !string.Equals(item, _originalName, StringComparison.Ordinal))
                .ToList();

            name = SourceUrl.MakeUniqueName(name, taken);
        }

        var layout = (LayoutCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "dists";

        descriptor = new RepoDescriptor
        {
            Name = name,
            BaseUrl = normalized,
            Layout = string.Equals(layout, "flat", StringComparison.Ordinal) ? RepoLayout.Flat : RepoLayout.Dists,
            Suite = NullIfEmpty(SuiteBox.Text),
            Component = NullIfEmpty(ComponentBox.Text),
            Priority = ParseInt(PriorityBox.Text, 100),
            Enabled = EnabledBox.IsChecked != false,
            Architecture = string.IsNullOrWhiteSpace(ArchitectureBox.Text)
                ? "iphoneos-arm"
                : ArchitectureBox.Text.Trim(),
        };

        error = null;
        return true;
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (!TryBuildResult(out var descriptor, out var error))
        {
            MessageBox.Show(this, Loc.Format("SourceEdit.BadAddress", error ?? "invalid"),
                Loc.Get("SourceEdit.Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Result = descriptor;
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private static string? NullIfEmpty(string? text)
        => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static int ParseInt(string text, int fallback)
        => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;
}


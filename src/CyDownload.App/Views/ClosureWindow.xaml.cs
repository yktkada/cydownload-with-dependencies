using System.Windows;
using CyDownload.App.Infrastructure;
using CyDownload.App.Localization;
using CyDownload.App.ViewModels;
using CyDownload.Core.Resolve;
using Microsoft.Win32;

namespace CyDownload.App.Views;

/// <summary>闭包窗口：三分区（下载 / 已满足 / 未解析）+ 环标注 + 导出。</summary>
public partial class ClosureWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly ResolutionResult _resolution;

    internal ClosureWindow(MainViewModel viewModel, ResolutionResult resolution)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(resolution);

        _viewModel = viewModel;
        _resolution = resolution;

        InitializeComponent();

        TargetText.Text = Loc.Format("Closure.Target", viewModel.Settings.TargetProfile.Describe());
        SummaryText.Text = Loc.Format(
            "Closure.Summary",
            resolution.ToDownload.Count,
            PackageItemViewModel.FormatBytes(resolution.TotalBytes),
            resolution.Satisfied.Count,
            resolution.Unresolved.Count);

        CyclesText.Text = resolution.Cycles.Count == 0
            ? ""
            : Loc.Format("Closure.Cycles", string.Join("；", resolution.Cycles));

        DownloadGrid.ItemsSource = resolution.ToDownload
            .OrderBy(item => item.PackageId, StringComparer.Ordinal)
            .Select(item => new DownloadRow(item))
            .ToList();

        SatisfiedGrid.ItemsSource = resolution.Satisfied
            .OrderBy(item => item.Name, StringComparer.Ordinal)
            .Select(item => new SatisfiedRow(item))
            .ToList();

        UnresolvedGrid.ItemsSource = resolution.Unresolved
            .Select(item => new UnresolvedRow(item))
            .ToList();

        _languageHandler = (_, _) => ApplyColumnHeaders();
        Loc.LanguageChanged += _languageHandler;
        ApplyColumnHeaders();
        Closed += (_, _) => Loc.LanguageChanged -= _languageHandler;
    }

    private readonly EventHandler _languageHandler;

    /// <summary>列头在代码里设（`DataGridColumn` 不在可视树，`DynamicResource` 不生效）。</summary>
    private void ApplyColumnHeaders()
    {
        CdPackageId.Header = Loc.Get("Detail.PackageId");
        CdVersion.Header = Loc.Get("Column.Version");
        CdSize.Header = Loc.Get("Column.Size");
        CdRepo.Header = Loc.Get("Column.Repo");

        CsName.Header = Loc.Get("Detail.PackageId");
        CsVersion.Header = Loc.Get("Column.Version");
        CsSource.Header = Loc.Get("Column.Repo");
        CsParent.Header = Loc.Get("Closure.Satisfied");

        CuText.Header = Loc.Get("Closure.Unresolved");
        CuReason.Header = Loc.Get("Detail.Description");
        CuParent.Header = Loc.Get("Closure.Satisfied");
    }

    private async void OnExportClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = Loc.Get("Closure.Export"),
            // 默认给「文档」：桌面常被 UAC/重定向保护
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };

        if (dialog.ShowDialog(this) != true) return;

        if (!CyDownload.App.Infrastructure.WriteProbe.CanWrite(dialog.FolderName))
        {
            MessageBox.Show(this, Loc.Format("Err.DirNotWritable", dialog.FolderName), Loc.Get("Closure.Title"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SafeFireAndForget.RunHandler(
            async () =>
            {
                var result = await _viewModel.ExportAsync(dialog.FolderName);
                if (result is null) return;

                if (!IsLoaded) return;      // 用户已经关掉窗口：结果由主窗口状态栏/日志体现

                MessageBox.Show(this,
                    Loc.Format("Export.Done", result.Directory, result.Files.Count,
                        PackageItemViewModel.FormatBytes(result.TotalBytes),
                        result.Files.Count - result.SkippedIdentical - result.SkippedDifferent,
                        result.SkippedIdentical, result.SkippedDifferent),
                    Loc.Get("Closure.Title"), MessageBoxButton.OK, MessageBoxImage.Information);
            },
            "Export");
    }

    /// <summary>
    /// 后台下载：**不 await、不锁窗口**——设计是「下载时可以关掉窗口继续浏览」，
    /// 下载本身在 MainViewModel 里跑，完成由主窗口的事件弹提示。
    /// </summary>
    private void OnDownloadLibraryClick(object sender, RoutedEventArgs e)
        => SafeFireAndForget.RunHandler(
            () => _viewModel.DownloadResolutionToLibraryAsync(_resolution),
            "DownloadToLibrary");

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private sealed record DownloadRow(ResolvedPackage Item)
    {
        public string PackageId => Item.PackageId;

        public string Version => Item.Version;

        public string SizeText => PackageItemViewModel.FormatBytes(Item.Package.Size ?? 0);

        public string Repo => Item.Package.Repo;
    }

    private sealed record SatisfiedRow(SatisfiedDependency Item)
    {
        public string Name => Item.Name;

        public string Version => Item.Version ?? "";

        public string Source => Item.Source;

        public string Parent => Item.Parent ?? "";
    }

    private sealed record UnresolvedRow(UnresolvedDependency Item)
    {
        public string Text => Item.Text;

        public string Reason => Item.Reason;

        public string Parent => Item.Parent ?? "";
    }
}







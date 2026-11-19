using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using CyDownload.App.Infrastructure;
using CyDownload.App.Localization;
using CyDownload.App.ViewModels;
using CyDownload.App.ViewModels.Library;
using CyDownload.Core.Export;
using CyDownload.Core.Resolve;
using Microsoft.Win32;

namespace CyDownload.App.Views;

/// <summary>
/// 主窗口。code-behind 只做视图层的事：文件/文件夹对话框、打开子窗口、DataGrid 视图过滤与选中同步、
/// 以及把后台失败显示到状态栏。
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly ICollectionView _packageView;
    private readonly EventHandler _languageHandler;
    private readonly Action<string, Exception> _unhandledHandler;

    private string _filterKeyword = "";

    internal MainWindow(MainViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;

        _packageView = CollectionViewSource.GetDefaultView(viewModel.Packages);
        _packageView.Filter = MatchesFilter;

        _viewModel.PackageFilterChanged += keyword =>
        {
            _filterKeyword = keyword;
            _packageView.Refresh();
            UpdateEmptyHints();
        };

        _viewModel.ClosureReady += OnClosureReady;
        _viewModel.LibraryDownloadCompleted += OnLibraryDownloadCompleted;
        _viewModel.PackageDetailRequested += OnPackageDetailRequested;
        _viewModel.ExportRequested += OnExportRequested;

        // 后台失败必须界面上看得见（：此前 UnhandledErrorReported 没有订阅者）
        _unhandledHandler = (message, _) => _viewModel.ReportError(message);
        App.UnhandledErrorReported += _unhandledHandler;

        // 语言切换：列头与右键菜单是代码设的，要自己重设（DataGridColumn 不在可视树）
        _languageHandler = (_, _) => ApplyLocalizedChrome();
        Loc.LanguageChanged += _languageHandler;

        BuildLanguageCombo();
        ApplyLocalizedChrome();
        UpdateEmptyHints();

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await _viewModel.InitializeAsync();
            UpdateEmptyHints();
            SyncGroupCombo();          // 分组下拉显示当前分组（「全部」），不做绑定以免递归
        }
        catch (Exception ex)
        {
            SafeFireAndForget.Report("Initialize", ex);
        }
    }

    /// <summary>关窗时把所有静态事件退订（否则窗口被静态事件钉住）。</summary>
    private void OnClosed(object? sender, EventArgs e)
    {
        Loc.LanguageChanged -= _languageHandler;
        App.UnhandledErrorReported -= _unhandledHandler;
        _viewModel.Dispose();
    }

    // ── 本地化 chrome（列头 / 菜单 / 语言下拉）──────────────────────────────

    private void ApplyLocalizedChrome()
    {
        LibName.Header = Loc.Get("Column.Name");
        LibGroup.Header = Loc.Get("Column.Count");
        LibSize.Header = Loc.Get("Column.Size");

        ColPackageId.Header = Loc.Get("Detail.PackageId");
        ColName.Header = Loc.Get("Column.Name");
        ColVersion.Header = Loc.Get("Column.Version");
        ColSection.Header = Loc.Get("Column.Section");
        ColSize.Header = Loc.Get("Column.Size");
        ColRepo.Header = Loc.Get("Column.Repo");

        AddSourceMenu.Header = Loc.Get("Source.Menu.Add");
        EditSourceMenu.Header = Loc.Get("Source.Menu.Edit");
        DeleteSourceMenu.Header = Loc.Get("Source.Menu.Delete");

        // 右键菜单文案：ContextMenu 在弹出树里，{DynamicResource} 解析不到 → 代码设、语言切换时重设
        LibMenuExportGroup.Header = Loc.Get("Menu.Library.ExportGroup");
        LibMenuExportFile.Header = Loc.Get("Menu.Library.ExportFile");
        LibMenuOpenFolder.Header = Loc.Get("Menu.Library.OpenFolder");
        LibMenuExpand.Header = Loc.Get("Menu.Library.Expand");
        LibMenuCollapse.Header = Loc.Get("Menu.Library.Collapse");
        LibMenuCopyPackageId.Header = Loc.Get("Menu.CopyPackageId");
        LibMenuCopyVersion.Header = Loc.Get("Menu.CopyVersion");
        LibMenuCopyFileName.Header = Loc.Get("Menu.CopyFileName");
        LibMenuCopyRow.Header = Loc.Get("Menu.CopyRow");
        LibMenuDeleteGroup.Header = Loc.Get("Menu.Library.DeleteGroup");
        LibName.Header = Loc.Get("Main.Library.NameHeader");
        LibVersion.Header = Loc.Get("Main.Library.VersionHeader");
        LibPkgId.Header = Loc.Get("Main.Library.PackageHeader");
        // 右键菜单里按 D 的两条通道：① InputGestureText 显示提示；② Header 里的 (_D) 是 WPF 原生访问键
        PkgMenuDownloadLibrary.InputGestureText = "D";
        PkgMenuDownloadLibrary.Header = Loc.Get("Menu.Package.DownloadLibrary") + "(_D)";
        LibMenuAddToGroup.Header = Loc.Get("Main.Library.AddToGroup");
        LibMenuRemoveFromGroup.Header = Loc.Get("Main.Library.RemoveFromGroup");

        SourceMenuButton.Content = Loc.Get("Main.Toolbar.Source");
        BtnAddSource.Header = Loc.Get("Source.Menu.Add");
        BtnEditSource.Header = Loc.Get("Source.Menu.Edit");
        BtnDeleteSource.Header = Loc.Get("Source.Menu.Delete");

        PkgMenuResolve.Header = Loc.Get("Menu.Package.Resolve");
        PkgMenuExport.Header = Loc.Get("Menu.Package.Export");
        PkgMenuDownloadLibrary.Header = Loc.Get("Menu.Package.DownloadLibrary");
        PkgMenuDetail.Header = Loc.Get("Menu.Package.Detail");
        PkgMenuCopyPackageId.Header = Loc.Get("Menu.CopyPackageId");
        PkgMenuCopyVersion.Header = Loc.Get("Menu.CopyVersion");
        PkgMenuCopyRow.Header = Loc.Get("Menu.CopyRow");
    }

    private void BuildLanguageCombo()
    {
        LanguageCombo.Items.Clear();
        LanguageCombo.Items.Add(new ComboBoxItem { Content = "system", Tag = "system" });
        LanguageCombo.Items.Add(new ComboBoxItem
        {
            Content = LanguageRules.DisplayName(LanguageRules.Chinese), Tag = LanguageRules.Chinese,
        });
        LanguageCombo.Items.Add(new ComboBoxItem
        {
            Content = LanguageRules.DisplayName(LanguageRules.English), Tag = LanguageRules.English,
        });

        var current = _viewModel.Settings.Language;
        foreach (ComboBoxItem item in LanguageCombo.Items)
        {
            if (string.Equals((string?)item.Tag, current, StringComparison.OrdinalIgnoreCase))
            {
                LanguageCombo.SelectedItem = item;
                return;
            }
        }

        LanguageCombo.SelectedIndex = 0;
    }

    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LanguageCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string culture) return;
        _viewModel.ApplyLanguage(culture);
    }

    // ── 闭包与导出 ──────────────────────────────────────────────────────────

    private void OnLibraryDownloadCompleted(int downloaded, int cached)
    {
        MessageBox.Show(
            this,
            Loc.Format("Download.Library.Done", downloaded, cached),
            Loc.Get("Main.Toolbar.DownloadLibrary"),
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void OnClosureReady(ResolutionResult resolution)
    {
        // 非模态：下载/导出在后台跑，用户可以关掉它继续浏览
        var window = new ClosureWindow(_viewModel, resolution) { Owner = this };
        window.Show();
    }

    /// <summary>命令只发「要导出」，选目录与确认在视图层（导出**只复制**，不删不移）。</summary>
    private async void OnExportRequested()
    {
        var directory = PickFolder();
        if (directory.Length == 0) return;
        if (!WriteProbe.CanWrite(directory))
        {
            MessageBox.Show(this, Loc.Format("Err.DirNotWritable", directory), Loc.Get("Main.Toolbar.Export"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var result = await _viewModel.ExportAsync(directory);
            if (result is null) return;

            // 分列「新增 / 已存在跳过 / 内容不同未覆盖」（：N 个里可能一个新文件都没有）
            var text = Loc.Format(
                "Export.Done",
                result.Directory,
                result.Files.Count,
                PackageItemViewModel.FormatBytes(result.TotalBytes),
                result.Files.Count - result.SkippedIdentical,
                result.SkippedIdentical,
                result.SkippedDifferent);

            MessageBox.Show(this, text, Loc.Get("Main.Toolbar.Export"),
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            SafeFireAndForget.Report("Export", ex);
        }
    }

    private void OnClearQueueClick(object sender, RoutedEventArgs e) => _viewModel.ClearDownloadQueue();

    private void OnCancelOperationClick(object sender, RoutedEventArgs e) => _viewModel.CancelCurrentOperation();

    // ── 源管理（右键菜单；）────────────────────────────────────────

    /// <summary>右键菜单作用于「鼠标下那个页签」，而不是碰巧选中的那个。</summary>
    private void OnSourceTabRightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var item = ItemsControl.ContainerFromElement(SourceTabs, (DependencyObject)e.OriginalSource) as TabItem;
        if (item?.DataContext is SourceTabViewModel tab) _viewModel.SelectedSource = tab;
    }

    /// <summary>工具条「源」按钮：把同一套菜单弹在按钮下方（零源时这是唯一的添加入口）。</summary>
    private void OnSourceMenuClick(object sender, RoutedEventArgs e)
    {
        if (SourceMenuButton.ContextMenu is not { } menu) return;

        var merged = _viewModel.SelectedSource?.IsMerged == true;
        BtnEditSource.IsEnabled = !merged;
        BtnDeleteSource.IsEnabled = !merged;

        menu.PlacementTarget = SourceMenuButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>「全部」页签不是一个源：编辑/删除项禁用。</summary>
    private void OnSourceTabsContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var merged = _viewModel.SelectedSource?.IsMerged == true;
        EditSourceMenu.IsEnabled = !merged;
        DeleteSourceMenu.IsEnabled = !merged;
    }

    private void OnAddSourceClick(object sender, RoutedEventArgs e) => EditSource(null);

    private void OnEditSourceClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedSource is null) return;
        EditSource(_viewModel.SelectedSource.Repo);
    }

    private void EditSource(CyDownload.Core.Repos.RepoDescriptor? existing)
    {
        var window = new SourceEditWindow(
            existing,
            _viewModel.Sources.Where(source => !source.IsMerged).Select(source => source.Name).ToList())
        {
            Owner = this,
        };
        if (window.ShowDialog() != true) return;

        SafeFireAndForget.RunHandler(
            async () =>
            {
                var saved = await _viewModel.SaveSourceAsync(window.Result, window.OriginalName);
                if (saved) return;

                MessageBox.Show(this, Loc.Format("Err.SourceExists", window.Result.Name),
                    Loc.Get("SourceEdit.Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
            },
            "SaveSource");
    }

    private void OnDeleteSourceClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedSource is not { } source) return;

        SafeFireAndForget.RunHandler(
            async () =>
            {
                // 先给出「会删多少行」的清单再让用户确认（删除先给清单的口径）
                var rows = await _viewModel.CountPackagesAsync(source.Name);

                var confirm = MessageBox.Show(
                    this, Loc.Format("Source.DeleteConfirm", source.Name, rows),
                    Loc.Get("Source.Menu.Delete"), MessageBoxButton.OKCancel, MessageBoxImage.Warning);

                if (confirm != MessageBoxResult.OK) return;

                await _viewModel.DeleteSourceAsync(source.Name);
            },
            "DeleteSource");
    }

    // ── 右键菜单（资料库 / 源浏览器共用同一套机制）──────────────────────────
    // 做法照搬 iDeviceGui：PreviewMouseRightButtonDown 先把**指针下那行**选中再弹菜单，
    // 菜单项按行类型显示/隐藏；复制类 handler 两边共用。

    private LibraryRowItem? _contextLibraryRow;

    /// <summary>当前打开的右键菜单（给「右键后按 D」用）。</summary>
    private ContextMenu? _openContextMenu;

    private void OnLibrarySelectionChanged(object sender, SelectionChangedEventArgs e)
        => _viewModel.NotifyLibrarySelectionChanged(
            LibraryGrid.SelectedItems.OfType<LibraryRowItem>().ToList());

    /// <summary>
    /// 右键菜单打开后按 <c>D</c> = 点「下载到资料库（含依赖）」。
    /// 只在浏览器右键菜单里生效；Esc 关闭菜单交给 WPF 自己处理。
    /// </summary>
    /// <summary>右键菜单内按 D = 「下载到资料库（含依赖）」（菜单是独立 Popup，键要挂在菜单上）。</summary>
    private void OnContextMenuPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.D) return;
        if (!ReferenceEquals(sender, PackageGrid.ContextMenu)) return;

        e.Handled = true;
        PackageGrid.ContextMenu.IsOpen = false;
        OnDownloadPackageToLibraryClick(sender, new RoutedEventArgs());
    }

    private void OnWindowPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.D || _openContextMenu is not { IsOpen: true }) return;
        if (!ReferenceEquals(_openContextMenu, PackageGrid.ContextMenu)) return;

        _openContextMenu.IsOpen = false;
        e.Handled = true;
        OnDownloadPackageToLibraryClick(sender, new RoutedEventArgs());
    }
    private PackageItemViewModel? _contextPackage;

    private void OnLibraryGridRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _contextLibraryRow = RowUnderMouse<LibraryRowItem>(LibraryGrid, e);
        _openContextMenu = LibraryGrid.ContextMenu;
        if (_contextLibraryRow is not null && !LibraryGrid.SelectedItems.Contains(_contextLibraryRow))
        {
            LibraryGrid.SelectedItem = _contextLibraryRow;
        }
    }

    private void OnPackageGridRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _contextPackage = RowUnderMouse<PackageItemViewModel>(PackageGrid, e);
        _openContextMenu = PackageGrid.ContextMenu;
        if (_contextPackage is not null) PackageGrid.SelectedItem = _contextPackage;
    }

    private static T? RowUnderMouse<T>(DataGrid grid, System.Windows.Input.MouseButtonEventArgs e)
        where T : class
        => ItemsControl.ContainerFromElement(grid, (DependencyObject)e.OriginalSource) is DataGridRow row
            ? row.Item as T
            : null;

    /// <summary>组行只给组操作，成员/独立行只给单文件操作。</summary>
    private void OnLibraryContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var row = _contextLibraryRow ?? LibraryGrid.SelectedItem as LibraryRowItem;
        if (row is null)
        {
            e.Handled = true;
            return;
        }

        var isGroup = row.IsGroup;
        var hasFile = !string.IsNullOrWhiteSpace(row.RelativePath);

        LibMenuExportGroup.Visibility = isGroup ? Visibility.Visible : Visibility.Collapsed;
        LibMenuExpand.Visibility = isGroup ? Visibility.Visible : Visibility.Collapsed;
        LibMenuCollapse.Visibility = isGroup ? Visibility.Visible : Visibility.Collapsed;
        LibMenuDeleteGroup.Visibility = isGroup ? Visibility.Visible : Visibility.Collapsed;
        LibMenuExportFile.Visibility = isGroup ? Visibility.Collapsed : Visibility.Visible;
        LibMenuOpenFolder.Visibility = hasFile ? Visibility.Visible : Visibility.Collapsed;
        LibMenuCopyFileName.Visibility = hasFile ? Visibility.Visible : Visibility.Collapsed;
        LibMenuOpenFolder.IsEnabled = hasFile;
    }

    // —— 资料库：导出 / 打开目录 / 展开收起 / 删除组 ——

    private void OnExportLibraryGroupClick(object sender, RoutedEventArgs e)
    {
        var rows = SelectedLibraryGroupRows();
        if (rows.Count == 0) return;

        var ids = rows
            .SelectMany(row => row.GroupIds.Count > 0 ? row.GroupIds : [row.GroupId])
            .Distinct()
            .OrderBy(id => id)
            .ToList();

        var directory = PickFolder();
        if (directory.Length == 0) return;      // 用户取消选目录（：不要误报"不可写"）
        if (!WriteProbe.CanWrite(directory))
        {
            MessageBox.Show(this, Loc.Format("Err.DirNotWritable", directory), Loc.Get("Main.Pane.Library"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ExportLibraryAsync(() => _viewModel.ExportLibraryGroupsAsync(ids, directory));
    }

    private void OnExportLibraryFileClick(object sender, RoutedEventArgs e)
    {
        var rows = LibraryGrid.SelectedItems.OfType<LibraryRowItem>()
            .Where(row => !row.IsGroup && !string.IsNullOrWhiteSpace(row.RelativePath))
            .ToList();

        if (rows.Count == 0 && _contextLibraryRow is { IsGroup: false } single &&
            !string.IsNullOrWhiteSpace(single.RelativePath))
        {
            rows = [single];
        }

        if (rows.Count == 0) return;

        var directory = PickFolder();
        if (directory.Length == 0) return;      // 用户取消选目录（：不要误报"不可写"）
        if (!WriteProbe.CanWrite(directory))
        {
            MessageBox.Show(this, Loc.Format("Err.DirNotWritable", directory), Loc.Get("Main.Pane.Library"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ExportLibraryAsync(() => rows.Count == 1
            ? _viewModel.ExportLibraryFileAsync(rows[0], directory)
            : _viewModel.ExportLibraryFilesAsync(rows, directory));
    }

    /// <summary>选目录 → 复制（只复制、不删除）→ 报结果。</summary>
    private async void ExportLibraryAsync(Func<Task<CyDownload.Core.Export.LibraryExportResult>> action)
    {
        try
        {
            var result = await action();
            MessageBox.Show(
                this,
                Loc.Format("Export.LibraryDone", result.Copied.Count - result.SkippedIdentical - result.SkippedDifferent,
                    result.Directory, result.Missing.Count, result.SkippedIdentical, result.SkippedDifferent),
                Loc.Get("Main.Pane.Library"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (UnauthorizedAccessException ex)
        {
            // 目标目录没写权限（UAC / 只读共享）：给可读提示，不当未处理异常
            MessageBox.Show(this, Loc.Format("Err.DirNotWritable", ex.Message), Loc.Get("Main.Pane.Library"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            SafeFireAndForget.Report("ExportLibrary", ex);
        }
    }

    /// <summary>选中的组行（没选中时退回右键那一行）。</summary>
    private List<LibraryRowItem> SelectedLibraryGroupRows()
    {
        var rows = LibraryGrid.SelectedItems.OfType<LibraryRowItem>().Where(row => row.IsGroup).ToList();
        if (rows.Count == 0 && _contextLibraryRow is { IsGroup: true } context) rows.Add(context);
        return rows;
    }

    /// <summary>选中行对应的插件根包名（去重；分批加/移出用户分组用）。</summary>
    private List<string> SelectedLibraryPackageIds()
    {
        var rows = LibraryGrid.SelectedItems.OfType<LibraryRowItem>().ToList();
        if (rows.Count == 0 && _contextLibraryRow is { } context) rows.Add(context);

        return rows
            .Select(row => row.IsGroup ? row.RootPackage : row.PackageId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private string _preferredExportDirectory =
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    private string PickFolder()
    {
        // 默认给「文档」而不是「桌面」：桌面常被 UAC/重定向保护，普通权限写不进去
        var initial = Directory.Exists(_preferredExportDirectory)
            ? _preferredExportDirectory
            : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        var dialog = new OpenFolderDialog
        {
            Title = Loc.Get("Menu.Library.ExportGroup"),
            InitialDirectory = initial,
        };

        if (dialog.ShowDialog(this) != true) return "";

        _preferredExportDirectory = dialog.FolderName;
        return dialog.FolderName;
    }

    private void OnOpenLibraryFolderClick(object sender, RoutedEventArgs e)
    {
        var row = _contextLibraryRow;
        if (row is null || string.IsNullOrWhiteSpace(row.RelativePath)) return;

        var path = Path.Combine(AppPaths.ArchiveDirectory, "pool", row.RelativePath);
        if (!File.Exists(path)) return;

        SafeFireAndForget.Run(
            () => System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + path + "\"")?.Dispose(),
            "OpenFolder");
    }

    private void OnExpandLibraryRowClick(object sender, RoutedEventArgs e)
    {
        if (_contextLibraryRow is not { IsGroup: true } row) return;
        if (_viewModel.IsLibraryGroupCollapsed(row.GroupId)) _viewModel.ToggleLibraryGroup(row.GroupId);
    }

    private void OnCollapseLibraryRowClick(object sender, RoutedEventArgs e)
    {
        if (_contextLibraryRow is not { IsGroup: true } row) return;
        if (!_viewModel.IsLibraryGroupCollapsed(row.GroupId)) _viewModel.ToggleLibraryGroup(row.GroupId);
    }

    private void OnDeleteLibraryGroupClick(object sender, RoutedEventArgs e)
    {
        var rows = SelectedLibraryGroupRows();
        if (rows.Count == 0) return;

        var ids = rows
            .SelectMany(row => row.GroupIds.Count > 0 ? row.GroupIds : [row.GroupId])
            .Distinct()
            .OrderBy(id => id)
            .ToList();

        var confirm = MessageBox.Show(
            this, Loc.Format("Menu.Library.DeleteGroupConfirm",
                rows.Count == 1 ? rows[0].PackageId : Loc.Format("Menu.Library.SelectedCount", rows.Count),
                rows.Count == 1 ? rows[0].VersionText : ""),
            Loc.Get("Menu.Library.DeleteGroup"), MessageBoxButton.OKCancel, MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.OK) return;

        SafeFireAndForget.RunHandler(() => _viewModel.DeleteLibraryGroupsAsync(ids), "DeleteLibraryGroup");
    }

    // —— 复制类（两个菜单共用）——

    private static void CopyText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // 剪贴板被别的进程占用：忽略，不弹错
        }
    }

    private void OnCopyLibraryPackageIdClick(object sender, RoutedEventArgs e)
        => CopyText(_contextLibraryRow?.PackageId ?? "");

    private void OnCopyLibraryVersionClick(object sender, RoutedEventArgs e)
        => CopyText(_contextLibraryRow?.VersionText ?? "");

    private void OnCopyLibraryFileNameClick(object sender, RoutedEventArgs e)
        => CopyText(Path.GetFileName(_contextLibraryRow?.RelativePath ?? ""));

    private void OnCopyLibraryRowClick(object sender, RoutedEventArgs e)
    {
        if (_contextLibraryRow is not { } row) return;
        CopyText(string.Join('\t', row.PackageId, row.VersionText, row.RepoText, row.SizeText));
    }

    private void OnCopyPackageIdClick(object sender, RoutedEventArgs e)
        => CopyText(_contextPackage?.PackageId ?? "");

    private void OnCopyPackageVersionClick(object sender, RoutedEventArgs e)
        => CopyText(_contextPackage?.Version ?? "");

    private void OnCopyPackageRowClick(object sender, RoutedEventArgs e)
    {
        if (_contextPackage is not { } package) return;
        CopyText(string.Join('\t', package.PackageId, package.Version, package.Section, package.SizeText, package.Repo));
    }

    // —— 源浏览器：算闭包 / 算闭包并导出 / 详情 ——

    private void OnResolvePackageClick(object sender, RoutedEventArgs e)
    {
        if (_contextPackage is not { } package) return;
        SafeFireAndForget.RunHandler(() => _viewModel.ResolveSingleAsync(package.PackageId, package.Version), "ResolveSingle");
    }

    private void OnExportPackageClick(object sender, RoutedEventArgs e)
    {
        if (_contextPackage is not { } package) return;

        var directory = PickFolder();
        if (directory.Length == 0) return;

        SafeFireAndForget.RunHandler(
            async () =>
            {
                if (!await _viewModel.ResolveSingleAsync(package.PackageId, package.Version)) return;

                if (!WriteProbe.CanWrite(directory))
        {
            MessageBox.Show(this, Loc.Format("Err.DirNotWritable", directory), Loc.Get("Main.Toolbar.Export"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var result = await _viewModel.ExportAsync(directory);
        if (result is null) return;
                MessageBox.Show(
                    this,
                    Loc.Format("Export.Done", result.Directory, result.Files.Count,
                        PackageItemViewModel.FormatBytes(result.TotalBytes),
                        result.Files.Count - result.SkippedIdentical,
                        result.SkippedIdentical, result.SkippedDifferent),
                    Loc.Get("Main.Toolbar.Export"), MessageBoxButton.OK, MessageBoxImage.Information);
            },
            "ExportPackage");
    }

    private void OnDownloadPackageToLibraryClick(object sender, RoutedEventArgs e)
    {
        if (_contextPackage is not { } package) return;
        SafeFireAndForget.RunHandler(
            async () =>
            {
                if (!await _viewModel.ResolveSingleAsync(package.PackageId, package.Version)) return;
                if (_viewModel.LastResolution is { } resolution)
                {
                    await _viewModel.DownloadResolutionToLibraryAsync(resolution);
                }
            },
            "DownloadToLibrary");
    }

    private void OnPackageDetailMenuClick(object sender, RoutedEventArgs e)
    {
        if (_contextPackage is not { } package) return;
        _viewModel.RequestPackageDetail(package);
    }
    // ── 资料库交互 ──────────────────────────────────────────────────────────

    /// <summary>点组头 = 展开/收起（与 iDeviceInstaller 的资料库一致；成员行点了不动）。</summary>
    private void OnLibraryRowClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var container = ItemsControl.ContainerFromElement(LibraryGrid, (DependencyObject)e.OriginalSource)
            as DataGridRow;

        if (container?.Item is LibraryRowItem { IsGroup: true } row)
        {
            _viewModel.ToggleLibraryGroup(row.GroupId);
        }
    }

    private void OnExpandAllLibraryClick(object sender, RoutedEventArgs e) => _viewModel.ExpandAllLibraryGroups();

    // ── 用户分组（抄 IdeviceGui GroupStore + GroupNameWindow） ─────────────────────────

    private bool _syncingGroupCombo;

    private void OnLibraryGroupChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingGroupCombo) return;
        if (LibraryGroupCombo.SelectedItem is not string name) return;

        SafeFireAndForget.RunHandler(
            async () =>
            {
                await _viewModel.SetActiveLibraryGroupAsync(name);
                SyncGroupCombo();
            },
            "LibraryGroupChanged");
    }

    private void OnNewLibraryGroupClick(object sender, RoutedEventArgs e)
    {
        var dialog = new GroupNameWindow(
            Loc.Get("Group.Title"), Loc.Get("Group.NewPrompt")) { Owner = this };

        if (dialog.ShowDialog() != true) return;

        SafeFireAndForget.RunHandler(
            async () =>
            {
                if (!await _viewModel.CreateLibraryGroupAsync(dialog.ResultName))
                {
                    MessageBox.Show(this, Loc.Get("Group.NameExists"), Loc.Get("Group.Title"),
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                SyncGroupCombo();
            },
            "NewLibraryGroup");
    }

    private void OnRenameLibraryGroupClick(object sender, RoutedEventArgs e)
    {
        var current = _viewModel.ActiveLibraryGroup;
        if (string.Equals(current, Loc.Get("Main.Library.AllGroups"), StringComparison.Ordinal))
        {
            MessageBox.Show(this, Loc.Get("Group.SelectFirst"), Loc.Get("Group.Title"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new GroupNameWindow(
            Loc.Get("Group.Title"), Loc.Get("Group.RenamePrompt"), current) { Owner = this };

        if (dialog.ShowDialog() != true) return;

        SafeFireAndForget.RunHandler(
            async () =>
            {
                if (!await _viewModel.RenameLibraryGroupAsync(current, dialog.ResultName))
                {
                    MessageBox.Show(this, Loc.Get("Group.NameExists"), Loc.Get("Group.Title"),
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                SyncGroupCombo();
            },
            "RenameLibraryGroup");
    }

    private void OnDeleteUserGroupClick(object sender, RoutedEventArgs e)
    {
        var current = _viewModel.ActiveLibraryGroup;
        if (string.Equals(current, Loc.Get("Main.Library.AllGroups"), StringComparison.Ordinal)) return;

        var confirm = MessageBox.Show(this, Loc.Format("Group.DeleteConfirm", current),
            Loc.Get("Group.Title"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        SafeFireAndForget.RunHandler(
            async () =>
            {
                await _viewModel.DeleteLibraryGroupAsync(current);
                SyncGroupCombo();
            },
            "DeleteLibraryGroup");
    }

    /// <summary>分组下拉与 VM 的当前分组保持同步（VM 操作后调用）。</summary>
    private void SyncGroupCombo()
    {
        _syncingGroupCombo = true;
        try
        {
            LibraryGroupCombo.SelectedItem = _viewModel.ActiveLibraryGroup;
        }
        finally
        {
            _syncingGroupCombo = false;
        }
    }

    // ── 列头排序 ────────────────────────────────────────────────────────────

    private void OnLibrarySorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        if (e.Column is null || string.IsNullOrEmpty(e.Column.SortMemberPath)) return;

        var descending = e.Column.SortDirection != ListSortDirection.Descending;
        _viewModel.SortLibrary(e.Column.SortMemberPath, descending);

        foreach (var column in LibraryGrid.Columns) column.SortDirection = null;
        e.Column.SortDirection = descending ? ListSortDirection.Descending : ListSortDirection.Ascending;
    }

    private void OnPackageSorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        if (e.Column is null || string.IsNullOrEmpty(e.Column.SortMemberPath)) return;

        var descending = e.Column.SortDirection != ListSortDirection.Descending;
        _packageView.SortDescriptions.Clear();
        _packageView.SortDescriptions.Add(new SortDescription(e.Column.SortMemberPath,
            descending ? ListSortDirection.Descending : ListSortDirection.Ascending));

        foreach (var column in PackageGrid.Columns) column.SortDirection = null;
        e.Column.SortDirection = descending ? ListSortDirection.Descending : ListSortDirection.Ascending;
    }


    /// <summary>
    /// 「清理已移除」：删掉已不在任何资料库分组里的 deb 实体文件与台账行（先给数字再确认）。
    /// </summary>
    /// <summary>把右键那一行的插件加入分组（下拉已选分组直接用，否则让用户输入/选择名字）。</summary>
    private void OnAddToGroupClick(object sender, RoutedEventArgs e)
    {
        var packageIds = SelectedLibraryPackageIds();
        if (packageIds.Count == 0) return;

        string? target = null;
        var active = _viewModel.ActiveLibraryGroup;
        if (!string.Equals(active, Loc.Get("Main.Library.AllGroups"), StringComparison.Ordinal))
        {
            target = active;
        }
        else
        {
            var dialog = new GroupNameWindow(
                Loc.Get("Group.Title"), Loc.Get("Group.NewPrompt")) { Owner = this };
            if (dialog.ShowDialog() == true) target = dialog.ResultName;
        }

        if (string.IsNullOrWhiteSpace(target)) return;

        SafeFireAndForget.RunHandler(
            async () =>
            {
                foreach (var packageId in packageIds)
                {
                    await _viewModel.AddPackageToGroupAsync(packageId, target);
                }

                SyncGroupCombo();
            },
            "AddToGroup");
    }

    private void OnRemoveFromGroupClick(object sender, RoutedEventArgs e)
    {
        var packageIds = SelectedLibraryPackageIds();
        if (packageIds.Count == 0) return;

        var active = _viewModel.ActiveLibraryGroup;
        if (string.Equals(active, Loc.Get("Main.Library.AllGroups"), StringComparison.Ordinal)) return;

        SafeFireAndForget.RunHandler(
            async () =>
            {
                foreach (var packageId in packageIds)
                {
                    await _viewModel.RemovePackageFromActiveGroupAsync(packageId);
                }
            },
            "RemoveFromGroup");
    }

    /// <summary>
    /// 「修复缺失」：台账里有、磁盘上没了的包，按身份四元重新下载并回写登记。
    /// </summary>
    private void OnRepairMissingClick(object sender, RoutedEventArgs e)
        => SafeFireAndForget.RunHandler(
            async () =>
            {
                var missing = await _viewModel.CountMissingPoolFilesAsync();

                if (missing == 0)
                {
                    MessageBox.Show(this, Loc.Get("Repair.None"), Loc.Get("Main.Pane.Library"),
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var confirm = MessageBox.Show(
                    this, Loc.Format("Repair.Confirm", missing), Loc.Get("Main.Pane.Library"),
                    MessageBoxButton.YesNo, MessageBoxImage.Question);

                if (confirm != MessageBoxResult.Yes) return;

                var (repaired, failed) = await _viewModel.RepairMissingPoolAsync();

                MessageBox.Show(
                    this, Loc.Format("Repair.Done", repaired, failed), Loc.Get("Main.Pane.Library"),
                    MessageBoxButton.OK, MessageBoxImage.Information);
            },
            "RepairMissing");

    private void OnCleanupLibraryClick(object sender, RoutedEventArgs e)
        => SafeFireAndForget.RunHandler(
            async () =>
            {
                var stats = await _viewModel.GetPoolCleanupStatsAsync();

                if (stats.Files == 0 && stats.Entries == 0)
                {
                    MessageBox.Show(this, Loc.Get("Cleanup.None"), Loc.Get("Main.Pane.Library"),
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var confirm = MessageBox.Show(
                    this,
                    Loc.Format("Cleanup.Confirm", stats.Files, PackageItemViewModel.FormatBytes(stats.Bytes)),
                    Loc.Get("Main.Pane.Library"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (confirm != MessageBoxResult.Yes) return;

                var result = await _viewModel.CleanupUnreferencedPoolAsync();

                MessageBox.Show(
                    this,
                    Loc.Format("Cleanup.Done", result.DeletedFiles,
                        PackageItemViewModel.FormatBytes(result.DeletedBytes), result.DeletedRows),
                    Loc.Get("Main.Pane.Library"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            },
            "CleanupPool");

    private void OnCollapseAllLibraryClick(object sender, RoutedEventArgs e) => _viewModel.CollapseAllLibraryGroups();

    // ── 包列表交互 ──────────────────────────────────────────────────────────

    private void OnPackageSelectionChanged(object sender, SelectionChangedEventArgs e)
        => _viewModel.NotifySelectionChanged(PackageGrid.SelectedItems.OfType<PackageItemViewModel>());

    private void OnPackageDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (PackageGrid.SelectedItem is PackageItemViewModel item) _viewModel.RequestPackageDetail(item);
    }

    private void OnPackageDetailRequested(PackageItemViewModel package)
    {
        var window = new PackageDetailWindow(package) { Owner = this };
        window.ShowDialog();
    }

    private bool MatchesFilter(object item)
    {
        if (item is not PackageItemViewModel package) return false;

        // iOS 版本筛选：只留「最低 iOS ≤ 选中版本」的包（没有 firmware 约束的视为兼容）
        var ios = _viewModel.IosFilterVersion;
        if (ios.Length > 0 && !CyDownload.Core.Deb.MinIosParser.IsCompatible(package.Row.MinIos, ios)) return false;

        if (_filterKeyword.Length == 0) return true;
        return package.SearchText.Contains(_filterKeyword, StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateEmptyHints()
    {
        // 用**视图**的行数：搜索过滤到 0 条时也要显示「没有匹配」
        var shown = PackageGrid.Items.Count;
        EmptyBrowserHint.Visibility = shown > 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    // ── 设置 ────────────────────────────────────────────────────────────────

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        var window = new SettingsWindow(_viewModel.Settings) { Owner = this };
        if (window.ShowDialog() != true) return;

        // 应用设置并刷新源页签（「显示更多源」会改变页签列表）；写文件在 VM 里走后台
        SafeFireAndForget.RunHandler(
            () => _viewModel.ApplySettingsAsync(window.Result),
            "SaveSettings");
    }
}















using System.Globalization;
using CyDownload.App.Mvvm;
using System.Collections.ObjectModel;
using System.IO;
using CyDownload.App.Infrastructure;
using CyDownload.App.Localization;
using CyDownload.App.ViewModels.Library;
using CyDownload.Core.Deb;
using CyDownload.Core.Diagnostics;
using CyDownload.Core.Export;
using CyDownload.Core.Fetch;
using CyDownload.Core.Index;
using CyDownload.Core.Net;
using CyDownload.Core.Repos;
using CyDownload.Core.Resolve;

namespace CyDownload.App.ViewModels;

/// <summary>
/// 主窗口 ViewModel：源页签、包浏览、闭包求解、导出、日志与状态。
/// <para>
/// 线程约定：命令在 UI 线程执行；耗时调用都 `await`（Core 内部已经是异步 + 后台线程）；
/// 日志与进度用 <see cref="Progress{T}"/>（在 UI 线程创建）回到界面，不用 `Dispatcher.Invoke`。
/// </para>
/// </summary>
internal sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly ArchiveDatabaseHandle _database;
    private readonly PackageRepository _repository;
    private readonly LibraryStore _libraryStore;
    private readonly ArchiveDatabase _archiveDatabase;
    private readonly LibraryView _libraryView = new();
    private readonly UserGroupsFile _userGroups = new();
    private IReadOnlySet<string>? _activeGroupMembers;
    private string _iosFilterVersion = "";
    private readonly UiLogSink _log;

    private readonly AsyncRelayCommand _fetchCommand;
    private readonly AsyncRelayCommand _resolveCommand;
    private readonly AsyncRelayCommand _refreshLibraryCommand;

    private readonly AsyncRelayCommand _exportCommand;
    private readonly AsyncRelayCommand _downloadToLibraryCommand;

    private AppSettings _settings;
    private SourceTabViewModel? _selectedSource;
    private int _sourceGeneration;
    private ResolutionResult? _lastResolution;
    private string _searchText = "";
    private string _statusText;
    private bool _isBusy;

    public MainViewModel(
        ArchiveDatabaseHandle database,
        ArchiveDatabase store,
        PackageRepository repository,
        AppSettings settings,
        UiLogSink log)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(log);

        _database = database;
        _archiveDatabase = store;
        _repository = repository;
        _libraryStore = new LibraryStore(store, log);
        _settings = settings;
        _log = log;
        _statusText = Loc.Get("Main.Status.Idle");


        _fetchCommand = new AsyncRelayCommand(FetchAsync, () => !IsBusy && SelectedSource is not null);
        _resolveCommand = new AsyncRelayCommand(ResolveAsync, () => !IsBusy && SelectedPackages.Count > 0);
        _refreshLibraryCommand = new AsyncRelayCommand(RefreshArchiveAsync);
        _downloadToLibraryCommand = new AsyncRelayCommand(
            DownloadToLibraryAsync,
            // 允许「下载中继续加包」：IsBusy 不再参与判断（任务会排队）
            () => SelectedPackages.Count > 0);
        _exportCommand = new AsyncRelayCommand(
            () =>
            {
                // 命令只负责「请求对话框」：真正选目录在视图层（MVVM 不碰对话框）
                ExportRequested?.Invoke();
                return Task.CompletedTask;
            },
            () => !IsBusy && _lastResolution is { Success: true });

        Loc.LanguageChanged += OnLanguageChanged;
    }

    // ── 集合 ────────────────────────────────────────────────────────────────

    public ObservableCollection<SourceTabViewModel> Sources { get; } = [];

    public ObservableCollection<PackageItemViewModel> Packages { get; } = [];

    public ObservableCollection<PackageItemViewModel> SelectedPackages { get; } = [];

    /// <summary>资料库当前选中的行（复用源浏览器的 Extended 多选语义）。</summary>
    public ObservableCollection<LibraryRowItem> SelectedLibraryRows { get; } = [];

    public void NotifyLibrarySelectionChanged(IEnumerable<LibraryRowItem> selected)
    {
        SelectedLibraryRows.Clear();
        foreach (var row in selected) SelectedLibraryRows.Add(row);
    }

    /// <summary>资料库行（组头 + 展开后的成员 + 独立 deb），由 <see cref="LibraryView"/> 生成。</summary>
    public ObservableCollection<LibraryRowItem> LibraryRows { get; } = [];


    /// <summary>右栏下载队列（一次导出一个包一项）。</summary>
    public ObservableCollection<DownloadItemViewModel> DownloadItems { get; } = [];

    public AppSettings Settings => _settings;

    public ResolutionResult? LastResolution => _lastResolution;

    /// <summary>闭包算好了（主窗口据此打开闭包窗口）。</summary>
    public event Action<ResolutionResult>? ClosureReady;

    /// <summary>导出的目录选择交给视图（选完再调 <see cref="ExportAsync"/>）。</summary>
    public event Action? ExportRequested;

    /// <summary>双击包（主窗口据此打开详情窗口）。</summary>
    public event Action<PackageItemViewModel>? PackageDetailRequested;

    /// <summary>筛选关键字变化（视图侧用 CollectionView 过滤）。</summary>
    public event Action<string>? PackageFilterChanged;

    // ── 状态 ────────────────────────────────────────────────────────────────

    public SourceTabViewModel? SelectedSource
    {
        get => _selectedSource;
        set
        {
            if (!SetProperty(ref _selectedSource, value)) return;
            OnPropertyChanged(nameof(SourceInfoText));
            _fetchCommand.RaiseCanExecuteChanged();

            // 代际计数：快速切 Tab 时，先完成但已过期的加载结果必须丢掉
            var generation = ++_sourceGeneration;
            var merged = value?.IsMerged == true;
            var name = value?.Name;
            SafeFireAndForget.Run(
                async () =>
                {
                    var rows = name is null
                        ? new List<PackageRow>()
                        : await Task.Run(() => merged ? _repository.LoadAll() : _repository.LoadAll(name))
                            .ConfigureAwait(true);

                    if (generation != _sourceGeneration) return;

                    Packages.Clear();
                    SelectedPackages.Clear();
                    foreach (var row in rows) Packages.Add(new PackageItemViewModel(row));
                    ApplyPackageFilter();
                },
                "SwitchSourceTab");
        }
    }

    public string SourceInfoText
    {
        get
        {
            if (SelectedSource is null) return Loc.Get("Main.Browser.NoSource");
            var parts = new List<string> { SelectedSource.LastFetchText };
            if (!string.IsNullOrWhiteSpace(SelectedSource.LastResult)) parts.Add(SelectedSource.LastResult);
            return string.Join("  ·  ", parts);
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value ?? "")) return;
            ApplyPackageFilter();
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            _fetchCommand.RaiseCanExecuteChanged();
            _resolveCommand.RaiseCanExecuteChanged();
            if (value) StatusText = Loc.Get("Main.Status.Busy");
        }
    }

    public string PackageCountText
    {
        get
        {
            var suffix = string.IsNullOrWhiteSpace(_searchText) ? "" : Loc.Get("Main.Browser.Filtered");
            return Loc.Format("Main.Browser.Count", Packages.Count, suffix);
        }
    }

    public string SelectedCountText => Loc.Format("Main.Browser.SelectedCount", SelectedPackages.Count);

    public string LibraryCountText
    {
        get
        {
            var counts = _libraryView.LastCounts;
            return Loc.Format("Library.Counts", counts.Groups, counts.Files);
        }
    }

    public bool HasSources => Sources.Count > 0;

    public bool HasPackages => Packages.Count > 0;

    public bool HasLibrary => LibraryRows.Count > 0;

    // ── 命令 ────────────────────────────────────────────────────────────────

    public AsyncRelayCommand FetchCommand => _fetchCommand;

    public AsyncRelayCommand ResolveCommand => _resolveCommand;

    public AsyncRelayCommand RefreshLibraryCommand => _refreshLibraryCommand;

    public AsyncRelayCommand ExportCommand => _exportCommand;

    /// <summary>把选中包的闭包**下载进资料库**（不导出）。</summary>
    public AsyncRelayCommand DownloadToLibraryCommand => _downloadToLibraryCommand;

    // ── 初始化 ──────────────────────────────────────────────────────────────

    public async Task InitializeAsync()
    {
        // 资料库内容寻址：旧布局（<包名>/<版本>_<架构>_<源>.deb）一次性搬进 objects/ 并回写 DB
        await Task.Run(() => PoolStore.Migrate(_archiveDatabase, _database.PoolDirectory, _log)).ConfigureAwait(true);

        // 用户分组（groups.json 外挂，抄 IdeviceGui GroupStore）与 iOS 兼容信息回填
        await Task.Run(() =>
        {
            var loaded = UserGroupsStore.Load(_database.ArchiveRoot, out var warning);
            if (warning is not null) _log.Write("[groups] " + warning);
            _userGroups.Version = loaded.Version;
            _userGroups.Groups.Clear();
            _userGroups.Groups.AddRange(loaded.Groups);
        }).ConfigureAwait(true);
        RebuildLibraryGroupNames();
        await Task.Run(() => _repository.BackfillMinIos(_log)).ConfigureAwait(true);

        await ReloadSourcesAsync().ConfigureAwait(true);
        await RefreshArchiveAsync().ConfigureAwait(true);
    }

    /// <summary>重新读源列表（启动 / 源管理后）。读库在后台线程。</summary>
    public async Task ReloadSourcesAsync(string? selectName = null)
    {
        var rows = await Task.Run(() => _repository.LoadRepos()).ConfigureAwait(true);

        var previous = selectName ?? SelectedSource?.Name;
        Sources.Clear();

        // 「全部」合并页签：所有源的包合成一个列表
        if (rows.Count > 0)
        {
            var totalPackages = await Task.Run(() => _repository.CountPackages()).ConfigureAwait(true);
            Sources.Add(SourceTabViewModel.CreateMerged(rows.Count, totalPackages));
        }

        // 默认只显示可用源；内置但已停用的源要用户在设置里勾「显示更多源」才列出。
        // 用户自己加的源即使停用也照常显示（他只可能主动停用它）。
        var legacyDisabled = DefaultSources.All()
            .Where(source => !source.Enabled)
            .Select(source => source.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            if (!_settings.ShowDisabledSources && !row.Enabled && legacyDisabled.Contains(row.Name)) continue;

            Sources.Add(new SourceTabViewModel(row.ToDescriptor(), FormatTimestamp(row.LastFetch), row.PackageCount ?? 0));
        }

        OnPropertyChanged(nameof(HasSources));
        OnPropertyChanged(nameof(HasNoSources));

        SelectedSource = Sources.FirstOrDefault(source =>
                             !source.IsMerged && string.Equals(source.Name, previous, StringComparison.Ordinal))
                         ?? Sources.FirstOrDefault();
    }

    /// <summary>没有源时界面给引导（不是空白 Tab）。</summary>
    public bool HasNoSources => Sources.Count == 0;

    /// <summary>新增/修改源（<paramref name="originalName"/> 非空表示改名）。</summary>
    public async Task<bool> SaveSourceAsync(RepoDescriptor source, string? originalName = null)
    {
        ArgumentNullException.ThrowIfNull(source);

        var existing = await Task.Run(() => _repository.LoadRepos()).ConfigureAwait(true);
        var duplicated = existing.Any(row =>
            !string.Equals(row.Name, originalName, StringComparison.Ordinal) &&
            string.Equals(row.Name, source.Name, StringComparison.Ordinal));

        if (duplicated) return false;

        await Task.Run(() => _repository.SaveRepo(source, originalName)).ConfigureAwait(true);
        await ReloadSourcesAsync(source.Name).ConfigureAwait(true);
        return true;
    }

    /// <summary>某个源已入库多少行（删源前给用户看清单；全量）。</summary>
    public Task<int> CountPackagesAsync(string repoName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoName);
        return Task.Run(() => _repository.CountPackages(repoName));
    }

    /// <summary>删除源（连同该源已入库的包行；界面调用前已让用户确认行数）。</summary>
    public async Task DeleteSourceAsync(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        await Task.Run(() => _repository.DeleteRepo(name)).ConfigureAwait(true);
        await ReloadSourcesAsync().ConfigureAwait(true);
    }

    private void ApplyPackageFilter()
    {
        OnPropertyChanged(nameof(PackageCountText));
        OnPropertyChanged(nameof(HasPackages));
        OnPropertyChanged(nameof(SelectedCountText));
        PackageFilterChanged?.Invoke(_searchText.Trim());
    }

    public void NotifySelectionChanged(IEnumerable<PackageItemViewModel> selected)
    {
        SelectedPackages.Clear();
        foreach (var item in selected) SelectedPackages.Add(item);
        OnPropertyChanged(nameof(SelectedCountText));
        _resolveCommand.RaiseCanExecuteChanged();
    }

    public void RequestPackageDetail(PackageItemViewModel package)
    {
        ArgumentNullException.ThrowIfNull(package);
        PackageDetailRequested?.Invoke(package);
    }

    // ── 抓取 ────────────────────────────────────────────────────────────────

    private async Task FetchAsync()
    {
        if (SelectedSource is null) return;

        // 「全部」页签 = 依次抓取所有源（用户 ：所有源合并在一个标签）
        if (SelectedSource.IsMerged)
        {
            await FetchAllSourcesAsync().ConfigureAwait(true);
            return;
        }

        var tab = SelectedSource;
        IsBusy = true;
        StatusText = Loc.Format("Main.Status.Fetching", tab.Name);
        tab.LastResult = "";

        try
        {
            var options = new HttpDownloadOptions
            {
                NoProgressTimeout = TimeSpan.FromSeconds(_settings.HttpNoProgressSeconds),
                TotalTimeout = TimeSpan.FromMinutes(_settings.HttpTotalMinutes),
                Headers = BuildHeaders(),
            };

            using var http = HttpDownloader.CreateClient();
            var fetcher = new IndexFetcher(new HttpDownloader(http), _log);
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(_settings.HttpTotalMinutes + 1));
            _operationCts = cts;

            // 抓取进度：bigboss 索引实测 ~106 s，不能只有一行"抓取中"
            var fetchProgress = new Progress<DownloadProgress>(progress =>
            {
                ExportProgressValue = progress.Percent ?? 0;
                StatusText = Loc.Format(
                    "Main.Status.FetchingPercent",
                    tab.Name,
                    progress.Percent is { } percent ? percent.ToString("0") : "?",
                    PackageItemViewModel.FormatBytes(progress.BytesReceived));
            });

            var outcome = await fetcher
                .FetchAsync(tab.Repo, options, fetchProgress, cts.Token)
                .ConfigureAwait(true);

            ExportProgressValue = 0;
            if (!outcome.Success)
            {
                // 界面只给本地化短句，原始英文原因进日志
                _log.Write("[fetch] " + (outcome.Error ?? "failed"));
                tab.LastResult = Loc.Get("Err.FetchFailed");
                StatusText = tab.LastResult;
                return;
            }

            var imported = await Task.Run(() => _repository.Import(
                tab.Repo, outcome.Stanzas, tab.Repo.Architecture, DateTimeOffset.Now)).ConfigureAwait(true);

            tab.PackageCount = imported.TotalAfterImport;
            tab.LastFetch = FormatTimestamp(DateTimeOffset.Now.ToString("O"));
            tab.LastResult = Loc.Format(
                "Main.Browser.Count", imported.TotalAfterImport, "") +
                $"  ·  {outcome.Compression} {outcome.CompressedBytes:N0} B  ·  {outcome.Elapsed.TotalSeconds:0.0} s";
            StatusText = Loc.Get("Main.Status.Idle");
            OnPropertyChanged(nameof(SourceInfoText));

            var rows = await Task.Run(() => _repository.LoadAll(tab.Name)).ConfigureAwait(true);
            Packages.Clear();
            SelectedPackages.Clear();
            foreach (var row in rows) Packages.Add(new PackageItemViewModel(row));
            ApplyPackageFilter();
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>「全部」页签的抓取：逐个源抓取并入 库，最后刷新合并列表。</summary>
    private async Task FetchAllSourcesAsync()
    {
        var tabs = Sources.Where(source => !source.IsMerged && source.Repo.Enabled).ToList();
        if (tabs.Count == 0) return;

        IsBusy = true;

        try
        {
            var options = new HttpDownloadOptions
            {
                NoProgressTimeout = TimeSpan.FromSeconds(_settings.HttpNoProgressSeconds),
                TotalTimeout = TimeSpan.FromMinutes(_settings.HttpTotalMinutes),
                Headers = BuildHeaders(),
            };

            using var http = HttpDownloader.CreateClient();
            var fetcher = new IndexFetcher(new HttpDownloader(http), _log);
            using var cts = new CancellationTokenSource(
                TimeSpan.FromMinutes(_settings.HttpTotalMinutes * (tabs.Count + 1)));
            _operationCts = cts;

            var failures = 0;

            for (var index = 0; index < tabs.Count; index++)
            {
                var tab = tabs[index];
                StatusText = Loc.Format("Main.Status.FetchingAll", tab.Name, index + 1, tabs.Count);
                tab.LastResult = "";

                var progress = new Progress<DownloadProgress>(item =>
                {
                    ExportProgressValue = item.Percent ?? 0;
                    StatusText = Loc.Format(
                        "Main.Status.FetchingPercent",
                        tab.Name,
                        item.Percent is { } percent ? percent.ToString("0") : "?",
                        PackageItemViewModel.FormatBytes(item.BytesReceived));
                });

                var outcome = await fetcher
                    .FetchAsync(tab.Repo, options, progress, cts.Token)
                    .ConfigureAwait(true);

                ExportProgressValue = 0;

                if (!outcome.Success)
                {
                    failures++;
                    _log.Write($"[fetch] {tab.Name}: " + (outcome.Error ?? "failed"));
                    tab.LastResult = Loc.Get("Err.FetchFailed");
                    continue;
                }

                var repo = tab.Repo;
                var imported = await Task.Run(() => _repository.Import(
                    repo, outcome.Stanzas, repo.Architecture, DateTimeOffset.Now)).ConfigureAwait(true);

                tab.PackageCount = imported.TotalAfterImport;
                tab.LastFetch = FormatTimestamp(DateTimeOffset.Now.ToString("O"));
                tab.LastResult = Loc.Format("Main.Browser.Count", imported.TotalAfterImport, "");
            }

            StatusText = failures == 0 ? Loc.Get("Main.Status.Idle") : Loc.Get("Err.FetchFailed");

            // 合并视图重算（包数与行）
            var totalPackages = await Task.Run(() => _repository.CountPackages()).ConfigureAwait(true);
            var mergedTab = Sources.FirstOrDefault(source => source.IsMerged);
            if (mergedTab is not null) mergedTab.PackageCount = totalPackages;

            var rows = await Task.Run(() => _repository.LoadAll()).ConfigureAwait(true);
            Packages.Clear();
            SelectedPackages.Clear();
            foreach (var row in rows) Packages.Add(new PackageItemViewModel(row));
            ApplyPackageFilter();
            OnPropertyChanged(nameof(SourceInfoText));
        }
        finally
        {
            _operationCts = null;
            IsBusy = false;
        }
    }
    private Dictionary<string, string> BuildHeaders()
    {
        var target = _settings.TargetProfile;
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["X-Machine"] = target.ProductType,
            ["X-Firmware"] = target.IosVersion,
        };
    }

    // ── 求解 ────────────────────────────────────────────────────────────────

    private async Task ResolveAsync()
    {
        var result = await ResolveCoreAsync().ConfigureAwait(true);
        if (result is { Success: true }) ClosureReady?.Invoke(result);
    }

    /// <summary>求解选中包（不弹窗）；失败时把本地化原因写状态栏。</summary>
    private async Task<ResolutionResult?> ResolveCoreAsync()
    {
        var targets = SelectedPackages
            .Select(item => item.PackageId)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (targets.Count == 0)
        {
            StatusText = Loc.Get("Err.NoSelection");
            return null;
        }

        IsBusy = true;
        StatusText = Loc.Format("Main.Status.Resolving", string.Join(", ", targets));

        try
        {
            var target = _settings.TargetProfile;
            var baseProvider = new BasePackageProvider(target, installed: null, usePreset: _settings.UsePresetBase);
            var source = new DatabasePackageSource(_repository, target.Arches, _log);
            var resolver = new DependencyResolver(source, baseProvider, _log);

            // 钉住用户选中的版本（否则求解会挑最高版本：选 CoolBooter 1.5 却下 1.6）
            var pinned = SelectedPackages
                .GroupBy(item => item.PackageId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First().Version, StringComparer.Ordinal);

            var options = new ResolveOptions
            {
                MaxNodes = _settings.MaxNodes,
                MaxCandidatesPerName = _settings.MaxCandidatesPerName,
                PinnedTargets = pinned.Count > 0 ? pinned : null,
            };

            var result = await Task.Run(() => resolver.Resolve(targets, options)).ConfigureAwait(true);
            _lastResolution = result;

            if (!result.Success)
            {
                StatusText = Loc.Format("Err.ResolveFailed", result.FailureReason ?? "");
                return result;
            }

            StatusText = Loc.Format(
                "Closure.Summary",
                result.ToDownload.Count,
                PackageItemViewModel.FormatBytes(result.TotalBytes),
                result.Satisfied.Count,
                result.Unresolved.Count);

            return result;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// pool 缓存查询：给 <see cref="DebFetcher"/> 用（按 包名+版本+架构+源 找到对象文件）。
    /// 内容寻址后同一文件可能被多行引用，查一次数据库即可。
    /// </summary>
    private async Task<Func<PackageRow, string?>> BuildPoolLookupAsync()
    {
        var poolDirectory = _database.PoolDirectory;
        var entries = await Task.Run(() => _libraryStore.LoadPool()).ConfigureAwait(true);

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.RelativePath)) continue;
            map[PoolKey(entry.PackageId, entry.Version, entry.Architecture, entry.Repo)] =
                Path.Combine(poolDirectory, entry.RelativePath);
        }

        return package => map.TryGetValue(
            PoolKey(package.PackageId, package.Version, package.Architecture, package.Repo), out var path)
            ? path
            : null;
    }

    // ── 导出 ────────────────────────────────────────────────────────────────

    /// <summary>导出上一次求解结果（目录由调用方用文件夹对话框选）。</summary>
    private readonly SemaphoreSlim _exportGate = new(1, 1);

    /// <summary>当前长任务的取消源（抓取 / 导出共用；界面「取消」按钮指向它）。</summary>
    private CancellationTokenSource? _operationCts;

    /// <summary>取消正在进行的抓取/导出（硬约束 7 的可取消一侧；）。</summary>
    public void CancelCurrentOperation()
    {
        try
        {
            _operationCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 刚好已结束
        }
    }

    public bool CanCancel => IsBusy;

    /// <summary>导出进度（0–100），绑定到右栏进度条。</summary>
    public double ExportProgressValue
    {
        get => _exportProgressValue;
        private set => SetProperty(ref _exportProgressValue, value);
    }

    private double _exportProgressValue;

    /// <summary>清空下载队列（界面按钮）。</summary>
    public void ClearDownloadQueue()
    {
        DownloadItems.Clear();
        ExportProgressValue = 0;
    }

    public async Task<ExportResult?> ExportAsync(string directory, IProgress<ExportProgress>? progress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        // 防御性检查放在取闸之前：否则抛出时闸门永不释放
        var resolution = _lastResolution;
        if (resolution is null)
        {
            StatusText = Loc.Get("Main.Status.NoResolution");
            return null;
        }

        // 并发导出不再抛异常（用户可见的"忙"不是 bug）：提示后直接返回
        if (!await _exportGate.WaitAsync(0).ConfigureAwait(true))
        {
            StatusText = Loc.Format("Busy.Another", Loc.Get("Main.Status.Exporting"));
            return null;
        }

        try
        {
            IsBusy = true;
            DownloadItems.Clear();
            ExportProgressValue = 0;
            var repos = (await Task.Run(() => _repository.LoadRepos()).ConfigureAwait(true))
                .ToDictionary(row => row.Name, row => row.BaseUrl, StringComparer.Ordinal);

            var options = new HttpDownloadOptions
            {
                NoProgressTimeout = TimeSpan.FromSeconds(_settings.HttpNoProgressSeconds),
                TotalTimeout = TimeSpan.FromMinutes(_settings.HttpTotalMinutes),
                Headers = BuildHeaders(),
            };

            using var http = HttpDownloader.CreateClient();
            var fetcher = new DebFetcher(
                new HttpDownloader(http), options, repos, _database.PoolDirectory, _log,
                cachedFilePath: await BuildPoolLookupAsync().ConfigureAwait(true));
            var exporter = new DebExporter(fetcher, _log);

            var report = new Progress<ExportProgress>(item => OnExportProgress(item));

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(60));
            var result = await exporter.ExportAsync(
                resolution,
                new ExportOptions
                {
                    Directory = directory,
                    Target = _settings.TargetProfile,
                    BaseMode = _settings.BaseMode,
                },
                progress ?? report,
                cts.Token).ConfigureAwait(true);

            foreach (var warning in result.Warnings) _log.Write("[export] " + warning);
            MarkQueueFinished(result);

            StatusText = Loc.Get("Main.Status.Idle");

            // 导出成功 = 资料库的事实：把 pool 台账 + 「每个插件一组」的闭包一起落库
            await PersistLibraryRunAsync(resolution, result).ConfigureAwait(true);
            await RefreshArchiveAsync().ConfigureAwait(true);
            return result;
        }
        finally
        {
            IsBusy = false;
            _exportGate.Release();
        }
    }

    /// <summary>
    /// 导出前按**拓扑顺序**预建下载队列：文件名与体积都是最终值（：
    /// 之前用包名当文件名、体积恒 0，用户看不出自己会拿到什么）。
    /// </summary>
    private List<DownloadItemViewModel> BuildDownloadQueue(ResolutionResult resolution)
    {
        DownloadItems.Clear();
        var created = new List<DownloadItemViewModel>();

        var sorted = TopologicalSorter.Sort(resolution.ToDownload.Select(item => item.Package).ToList());
        var width = ExportResult.WidthFor(sorted.Ordered.Count);

        for (var index = 0; index < sorted.Ordered.Count; index++)
        {
            var package = sorted.Ordered[index];
            var resolved = resolution.ToDownload.FirstOrDefault(item =>
                string.Equals(item.PackageId, package.PackageId, StringComparison.Ordinal) &&
                string.Equals(item.Version, package.Version, StringComparison.Ordinal));

            var number = (index + 1).ToString($"D{width}", CultureInfo.InvariantCulture);
            var fileName = $"{number}-{package.PackageId}_{package.Version}_{package.Architecture}.deb";

            var item = new DownloadItemViewModel(
                fileName, package.PackageId, package.Version, resolved?.Package.Size ?? 0);
            DownloadItems.Add(item);
            created.Add(item);
        }

        return created;
    }

    /// <summary>进度回调：按索引推进（第 i 项「下载中」，之前的「完成」）。</summary>
    private void OnExportProgress(ExportProgress progress)
    {
        ExportProgressValue = progress.Total <= 0 ? 0 : progress.Completed * 100.0 / progress.Total;

        for (var index = 0; index < DownloadItems.Count; index++)
        {
            DownloadItems[index].Status = index < progress.Completed
                ? Loc.Get("Download.Done")
                : index == progress.Completed
                    ? Loc.Get("Download.Running")
                    : Loc.Get("Download.Waiting");
        }

        if (string.IsNullOrEmpty(progress.CurrentPackage))
        {
            ExportProgressValue = 100;
            foreach (var item in DownloadItems) item.Status = Loc.Get("Download.Done");
            return;
        }

        StatusText = Loc.Format("Main.Status.Exporting", progress.CurrentPackage, progress.Completed, progress.Total);
    }

    /// <summary>导出结束：按结果把队列项改成 完成 / 已缓存 / 已存在。</summary>
    private void MarkQueueFinished(ExportResult result)
    {
        // 同包多版本/多源时 PackageId 会重复 → 用文件名做键（，同类重复键崩溃）
        var byFileName = DownloadItems.ToDictionary(item => item.FileName, StringComparer.Ordinal);

        foreach (var file in result.Files)
        {
            if (!byFileName.TryGetValue(file.FileName, out var item)) continue;
            item.Status = file.FromCache ? Loc.Get("Download.Cached") : Loc.Get("Download.Done");
        }

        foreach (var warning in result.Warnings)
        {
            foreach (var item in DownloadItems)
            {
                if (warning.StartsWith(item.FileName + "：", StringComparison.Ordinal) ||
                    warning.StartsWith(item.FileName + ":", StringComparison.Ordinal))
                {
                    item.Status = Loc.Get("Download.Skipped");
                }
            }
        }

        ExportProgressValue = 100;
    }

    /// <summary>
    /// 把一次导出写进资料库（分组行为与 iDeviceInstaller 一致：一个插件 = 一组，组内是整个依赖闭包）。
    /// 同一份 deb 会同时出现在多个插件的组里——这是「按插件的闭包」而不是「按文件去重」。
    /// </summary>
    // ── 下载到资料库（不导出）────────────────────────────────────────────────

    private readonly SemaphoreSlim _libraryGate = new(1, 1);

    /// <summary>工具栏/右键「下载到资料库」：先求解选中包，再把闭包 deb 下进 pool 并记分组。</summary>
    private async Task DownloadToLibraryAsync()
    {
        if (SelectedPackages.Count == 0)
        {
            StatusText = Loc.Get("Err.NoSelection");
            return;
        }

        var resolution = await ResolveCoreAsync().ConfigureAwait(true);
        if (resolution is null) return;

        if (!resolution.Success)
        {
            StatusText = Loc.Format("Err.ResolveFailed", resolution.FailureReason ?? "");
            return;
        }

        await DownloadResolutionToLibraryAsync(resolution).ConfigureAwait(true);
    }

    /// <summary>
    /// 把闭包**下载进资料库**（pool + 分组），不复制到任何导出目录。
    /// <para>
    /// 「不一定全部导出」：先攒进资料库，想给设备用时再右键组/成员导出。
    /// 已经在 pool 里的包会走缓存校验，不重复下载。
    /// </para>
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentQueue<ResolutionResult> _downloadQueue = new();

    /// <summary>排队中的闭包签名（去重，见 DownloadResolutionToLibraryAsync）。</summary>
    private readonly HashSet<string> _queuedSignatures = [];

    /// <summary>正在处理的闭包签名。</summary>
    private string? _activeDownloadSignature;

    /// <summary>下载 worker 闸（0 超时：拿到就自己当 worker，拿不到就把任务排进队列）。</summary>
    private readonly SemaphoreSlim _downloadWorkerGate = new(1, 1);

    /// <summary>
    /// 下载一个闭包到资料库。**如果已经有下载在跑，就排队**（用户 ：
    /// 一个包+依赖下载中时，要能继续加别的包；旧实现直接抛 `A library download is already running`）。
    /// </summary>
    public async Task DownloadResolutionToLibraryAsync(ResolutionResult resolution)
    {
        ArgumentNullException.ThrowIfNull(resolution);

        if (!resolution.Success || resolution.ToDownload.Count == 0)
        {
            StatusText = Loc.Get("Main.Status.Idle");
            return;
        }

        // 去重：同一闭包（目标集合 + 包集合相同）已经在跑或排队时，不再重复入队
        var signature = DownloadSignature(resolution);
        if (signature == _activeDownloadSignature || _queuedSignatures.Contains(signature))
        {
            _log.Write("[library] duplicate download request ignored");
            return;
        }

        _queuedSignatures.Add(signature);
        _downloadQueue.Enqueue(resolution);

        if (!await _downloadWorkerGate.WaitAsync(0).ConfigureAwait(true))
        {
            // 已有 worker：排进去即可，不再抛异常
            StatusText = Loc.Format("Download.Queued", _downloadQueue.Count);
            _log.Write($"[library] queued download ({_downloadQueue.Count} waiting)");
            return;
        }

        var downloaded = 0;
        var cached = 0;
        IsBusy = true;

        try
        {
            // 丢失唤醒：worker 释放闸后必须再查一次队列 —— 否则新请求在
            // "TryDequeue 返回 false" 与 "Release" 之间入队时，会永远没人处理。
            while (true)
            {
                try
                {
                    while (_downloadQueue.TryDequeue(out var next))
                    {
                        var activeSignature = DownloadSignature(next);
                        lock (_queuedSignatures) _queuedSignatures.Remove(activeSignature);
                        _activeDownloadSignature = activeSignature;
                        try
                        {
                            var (debs, fromCache) = await DownloadOneResolutionAsync(next).ConfigureAwait(true);
                            downloaded += debs;
                            cached += fromCache;
                        }
                        catch (OperationCanceledException)
                        {
                            StatusText = Loc.Get("Main.Status.Canceled");
                            DropQueuedDownloads();
                            return;
                        }
                        catch (Exception ex)
                        {
                            // 单个闭包失败不拖垮队列：记日志、继续下一个
                            _log.Write("[library] download failed: " + ex.Message);
                            StatusText = Loc.Format("Err.Operation", ex.GetType().Name + ": " + ex.Message);
                        }
                    }
                }
                finally
                {
                    _operationCts = null;
                    _activeDownloadSignature = null;
                    _downloadWorkerGate.Release();
                }

                if (_downloadQueue.IsEmpty) break;
                if (!await _downloadWorkerGate.WaitAsync(0).ConfigureAwait(true)) break;
            }
        }
        finally
        {
            IsBusy = false;
        }

        if (downloaded > 0)
        {
            _log.Write($"[library] queue done: {downloaded} debs ({cached} cached)");
            StatusText = Loc.Get("Main.Status.Idle");
            LibraryDownloadCompleted?.Invoke(downloaded, cached);
        }
    }

    /// <summary>取消/退出时丢掉排队中的闭包与去重签名（否则同请求再来会被静默忽略）。</summary>
    private void DropQueuedDownloads()
    {
        _downloadQueue.Clear();
        lock (_queuedSignatures) _queuedSignatures.Clear();
        _activeDownloadSignature = null;
    }

    /// <summary>闭包签名：目标包 + 全部待下载包（用于队列去重）。</summary>
    private static string DownloadSignature(ResolutionResult resolution)
        => string.Join("|", resolution.ToDownload
            .Select(item => item.PackageId + "@" + item.Version)
            .OrderBy(text => text, StringComparer.Ordinal));

    /// <summary>下载单个闭包（队列 worker 内调用）：返回 (deb 数, 缓存命中数)。</summary>
    private async Task<(int Downloaded, int Cached)> DownloadOneResolutionAsync(ResolutionResult resolution)
    {
        BuildDownloadQueue(resolution);
        ExportProgressValue = 0;

        var repos = (await Task.Run(() => _repository.LoadRepos()).ConfigureAwait(true))
            .ToDictionary(row => row.Name, row => row.BaseUrl, StringComparer.Ordinal);

        var options = new HttpDownloadOptions
        {
            NoProgressTimeout = TimeSpan.FromSeconds(_settings.HttpNoProgressSeconds),
            TotalTimeout = TimeSpan.FromMinutes(_settings.HttpTotalMinutes),
            Headers = BuildHeaders(),
        };

        using var http = HttpDownloader.CreateClient();
        var fetcher = new DebFetcher(
            new HttpDownloader(http), options, repos, _database.PoolDirectory, _log,
            cachedFilePath: await BuildPoolLookupAsync().ConfigureAwait(true));

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(60));
        _operationCts = cts;

        var pool = new List<LibraryPoolEntry>();
        var poolLock = new object();
        var cached = 0;
        var stamp = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture);

        var ordered = resolution.ToDownload
            .OrderBy(item => item.PackageId, StringComparer.Ordinal)
            .ToList();

        var totalBytes = ordered.Sum(item => item.Package.Size ?? 0);
        long doneBytes = 0;
        var received = new long[ordered.Count];

        // 进度：字节加权（内容寻址缓存命中也算完成），全部经 Progress 回 UI 线程
        var progressGate = new object();
        void ReportProgress()
        {
            lock (progressGate)
            {
                var done = Interlocked.Read(ref doneBytes) + received.Sum();
                if (totalBytes > 0)
                {
                    ExportProgressValue = Math.Min(99.0, done * 100.0 / totalBytes);
                }
            }
        }

        var started = new Progress<int>(index =>
        {
            if (index < 0 || index >= ordered.Count) return;
            var item = ordered[index];
            var vm = FindDownloadItem(item);
            if (vm is not null) vm.Status = Loc.Get("Download.Running");
            StatusText = Loc.Format("Download.ParallelStatus", ordered.Count, _settings.MaxParallelDownloads);
        });

        var finished = new Progress<(int Index, DownloadOutcome Outcome)>(pair =>
        {
            var item = ordered[pair.Index];
            var vm = FindDownloadItem(item);
            var outcome = pair.Outcome;

            if (vm is not null)
            {
                vm.Status = outcome.Canceled
                    ? Loc.Get("Main.Status.Canceled")
                    : outcome.Ok
                        ? (outcome.FromCache ? Loc.Get("Download.Cached") : Loc.Get("Download.Done"))
                        : Loc.Get("Download.Failed");
            }

            if (outcome.Ok)
            {
                if (outcome.FromCache) cached++;
                ExportProgressValue = totalBytes > 0
                    ? Math.Min(99.0, Interlocked.Read(ref doneBytes) * 100.0 / totalBytes)
                    : ExportProgressValue;
            }
        });

        var jobs = new List<DownloadJob>(ordered.Count);
        for (var index = 0; index < ordered.Count; index++)
        {
            var item = ordered[index];
            var jobIndex = index;

            jobs.Add(new DownloadJob(jobIndex, item.PackageId, item.Package.Repo, async token =>
            {
                var perItem = new Progress<DownloadProgress>(progress =>
                {
                    Interlocked.Exchange(ref received[jobIndex], progress.BytesReceived);
                    ReportProgress();
                });

                var fetched = await fetcher.FetchAsync(item, perItem, token).ConfigureAwait(false);
                Interlocked.Exchange(ref received[jobIndex], 0);
                Interlocked.Add(ref doneBytes, fetched.Bytes);

                lock (poolLock)
                {
                    pool.Add(new LibraryPoolEntry(
                        item.PackageId,
                        item.Version,
                        item.Package.Architecture,
                        item.Package.Repo,
                        Path.GetRelativePath(_database.PoolDirectory, fetched.FilePath),
                        fetched.Bytes,
                        item.Package.Sha256,
                        stamp,
                        item.Package.Name));
                }

                return fetched.FromCache;
            }));
        }

        var outcomes = await DownloadScheduler.RunAsync(
            jobs,
            _settings.MaxParallelDownloads,
            _settings.MaxPerRepoConcurrency,
            cts.Token,
            onStarted: index => ((IProgress<int>)started).Report(index),
            onFinished: outcome => ((IProgress<(int, DownloadOutcome)>)finished).Report((outcome.Index, outcome)))
            .ConfigureAwait(true);

        if (cts.IsCancellationRequested)
        {
            throw new OperationCanceledException(cts.Token);
        }

        ExportProgressValue = 100;

        var failures = outcomes.Where(outcome => !outcome.Ok).ToList();
        if (failures.Count > 0)
        {
            foreach (var failure in failures)
            {
                _log.Write($"[library] failed {failure.PackageId}: {failure.Error}");
            }

            throw new InvalidOperationException(
                $"{failures.Count} package(s) failed: " + string.Join(", ", failures.Select(failure => failure.PackageId)));
        }

        var items = resolution.ToDownload
            .Select((item, index) => new LibraryGroupItem(
                item.PackageId, item.Version, item.Package.Architecture, item.Package.Repo, item.IsTarget, index))
            .ToList();

        var groups = resolution.ToDownload
            .Where(item => item.IsTarget)
            .Select(item => new LibraryGroupDraft(item.PackageId, item.Version, items, item.Package.Name))
            .ToList();

        if (groups.Count == 0)
        {
            groups.Add(new LibraryGroupDraft(items[0].PackageId, items[0].Version, items,
                resolution.ToDownload[0].Package.Name));
        }

        await Task.Run(() => _libraryStore.SaveRun(
            _settings.TargetProfile.Describe(), exportDirectory: null, groups, pool, DateTimeOffset.Now))
            .ConfigureAwait(true);

        await RefreshArchiveAsync().ConfigureAwait(true);
        return (pool.Count, cached);
    }

    /// <summary>按包 id+版本找右栏队列行（并行时多行同时在跑）。</summary>
    private DownloadItemViewModel? FindDownloadItem(ResolvedPackage item)
        => DownloadItems.FirstOrDefault(row =>
               string.Equals(row.PackageId, item.PackageId, StringComparison.Ordinal) &&
               string.Equals(row.Version, item.Version, StringComparison.Ordinal))
           ?? DownloadItems.FirstOrDefault(row =>
               string.Equals(row.PackageId, item.PackageId, StringComparison.Ordinal));

    /// <summary>下载到资料库完成（界面弹提示）。</summary>
    public event Action<int, int>? LibraryDownloadCompleted;

    private async Task PersistLibraryRunAsync(ResolutionResult resolution, ExportResult result)
    {
        var now = DateTimeOffset.Now;
        var stamp = now.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        var rootDirectory = _database.PoolDirectory;

        var pool = result.Files
            .Select(file => new LibraryPoolEntry(
                PackageId: file.Package.PackageId,
                Version: file.Package.Version,
                Architecture: file.Package.Architecture,
                Repo: file.Package.Repo,
                RelativePath: Path.GetRelativePath(rootDirectory, file.PoolPath),
                Bytes: file.Bytes,
                Sha256: file.Package.Sha256,
                AcquiredAt: stamp,
                DisplayName: file.Package.Name))
            .ToList();

        var items = resolution.ToDownload
            .Select((item, index) => new LibraryGroupItem(
                item.PackageId,
                item.Version,
                item.Package.Architecture,
                item.Package.Repo,
                item.IsTarget,
                index))
            .ToList();

        // 闭包可能为空（目标全被 base 满足）：此时没有可记的分组，直接返回
        if (items.Count == 0) return;

        var groups = resolution.ToDownload
            .Where(item => item.IsTarget)
            .Select(item => new LibraryGroupDraft(item.PackageId, item.Version, items, item.Package.Name))
            .ToList();

        if (groups.Count == 0)
        {
            groups.Add(new LibraryGroupDraft(items[0].PackageId, items[0].Version, items,
                resolution.ToDownload[0].Package.Name));
        }

        try
        {
            await Task.Run(() => _libraryStore.SaveRun(
                _settings.TargetProfile.Describe(), result.Directory, groups, pool, now)).ConfigureAwait(true);
        }
        catch (InvalidOperationException ex)
        {
            _log.Write("[library] save groups failed (exported files are fine): " + ex.Message);
        }
    }
    // ── 资料库 ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 刷新资料库：pool 台账 + 分组 → <see cref="LibraryView"/> → 行序列。
    /// <para>
    /// 磁盘上已有、但台账里没有的 deb 会**补登**（老版本下载的、或手动放进 pool 的），
    /// 这样「下载过的都算资料库」对历史文件也成立。
    /// </para>
    /// </summary>
    public async Task RefreshArchiveAsync()
    {
        var poolDirectory = _database.PoolDirectory;

        var (groups, pool) = await Task.Run(() =>
        {
            var existing = _libraryStore.LoadPool();
            var known = existing
                .Select(entry => entry.PackageId + "|" + entry.Version + "|" + entry.Architecture + "|" + entry.Repo)
                .ToHashSet(StringComparer.Ordinal);

            // 只补登台账里没有的
            var backfill = DiscoverPoolFiles(poolDirectory)
                .Where(entry => !known.Contains(
                    entry.PackageId + "|" + entry.Version + "|" + entry.Architecture + "|" + entry.Repo))
                .ToList();

            if (backfill.Count > 0)
            {
                try
                {
                    _libraryStore.UpsertPool(backfill);
                    existing = _libraryStore.LoadPool();
                }
                catch (InvalidOperationException ex)
                {
                    _log.Write("[library] backfill pool failed: " + ex.Message);
                }
            }

            return (_libraryStore.LoadGroups(), existing);
        }).ConfigureAwait(true);

        _libraryView.MergeSameGroups = _settings.MergeSameGroups;
        _libraryView.AllowedRoots = _activeGroupMembers;
        _libraryView.ReplaceAll(pool, groups);
        RefreshLibraryRows();
    }

    /// <summary>扫 pool 目录，把磁盘上有但台账里没有的文件列出来（文件名 `包名/版本_源.deb`）。</summary>
    private static List<LibraryPoolEntry> DiscoverPoolFiles(string poolDirectory)
    {
        if (!Directory.Exists(poolDirectory)) return [];

        var found = new List<LibraryPoolEntry>();
        var objectsPrefix = PoolStore.ObjectsFolder + Path.DirectorySeparatorChar;
        foreach (var path in Directory.EnumerateFiles(poolDirectory, "*.deb", SearchOption.AllDirectories))
        {
            // 内容寻址的对象目录由数据库台账描述（文件名是 sha256，解析不出包名）→ 跳过
            var relative = Path.GetRelativePath(poolDirectory, path);
            if (relative.StartsWith(objectsPrefix, StringComparison.OrdinalIgnoreCase)) continue;

            // 文件名：版本_架构_源.deb（v3 起）；版本里不会出现下划线，所以从右往左拆
            var fileName = Path.GetFileNameWithoutExtension(path);
            var parts = fileName.Split('_');
            if (parts.Length < 3) continue;

            var repo = parts[^1];
            var architecture = parts[^2];
            var version = string.Join('_', parts[..^2]);
            if (version.Length == 0) continue;

            var packageId = Path.GetFileName(Path.GetDirectoryName(path) ?? "");
            if (packageId.Length == 0) continue;

            var info = new FileInfo(path);
            found.Add(new LibraryPoolEntry(
                packageId, version, architecture, repo,
                Path.GetRelativePath(poolDirectory, path), info.Length, null,
                info.LastWriteTimeUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture)));
        }

        return found;
    }

    /// <summary>重算资料库行并刷新统计（组头展开/收起、搜索、导出后都要调）。</summary>
    private void RefreshLibraryRows()
    {
        var rows = _libraryView.Build();
        LibraryRows.Clear();
        foreach (var row in rows) LibraryRows.Add(row);
        OnPropertyChanged(nameof(LibraryCountText));
        OnPropertyChanged(nameof(HasLibrary));
    }

    /// <summary>资料库搜索（匹配插件名 / 包名 / 版本）。</summary>
    public string LibrarySearchText
    {
        get => _libraryView.SearchText;
        set
        {
            if (string.Equals(_libraryView.SearchText, value, StringComparison.Ordinal)) return;
            _libraryView.SearchText = value ?? "";
            OnPropertyChanged();
            RefreshLibraryRows();
        }
    }

    /// <summary>某个组当前是不是收起状态（右键「展开/收起」用）。</summary>
    public bool IsLibraryGroupCollapsed(long groupId) => _libraryView.IsCollapsed(groupId);

    /// <summary>点组头展开/收起。</summary>
    public void ToggleLibraryGroup(long groupId)
    {
        _libraryView.ToggleGroup(groupId);
        RefreshLibraryRows();
    }

    public void ExpandAllLibraryGroups()
    {
        _libraryView.ExpandAll();
        RefreshLibraryRows();
    }

    public void CollapseAllLibraryGroups()
    {
        _libraryView.CollapseAll();
        RefreshLibraryRows();
    }

    // ── 日志 ────────────────────────────────────────────────────────────────


    /// <summary>后台失败要落到状态栏（：此前只有日志文件里有）。</summary>
    public void ReportError(string message)
    {
        StatusText = message;
        _log.Write("[ui] " + message);
    }
    // ── 资料库右键：导出 / 删除组 / 浏览器右键：算闭包 ──────────────────────

    /// <summary>把资料库里**某一组**（一个插件的整个闭包）复制到目录（右键「导出这一组」）。</summary>
    public async Task<LibraryExportResult> ExportLibraryGroupAsync(long groupId, string directory)
        => await ExportLibraryGroupsAsync([groupId], directory).ConfigureAwait(true);

    /// <summary>
    /// 导出**一组（或合并显示的多次记录）**：按 group id 并集取成员（：
    /// 合并行显示的是并集，导出若只取最早那次 run 会缺依赖）。
    /// </summary>
    public async Task<LibraryExportResult> ExportLibraryGroupsAsync(IReadOnlyList<long> groupIds, string directory)
    {
        ArgumentNullException.ThrowIfNull(groupIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (groupIds.Count == 0) throw new InvalidOperationException("No library group to export.");

        var wanted = groupIds.ToHashSet();
        var files = await Task.Run(() =>
        {
            var groups = _libraryStore.LoadGroups().Where(item => wanted.Contains(item.Id)).ToList();
            if (groups.Count == 0)
            {
                throw new InvalidOperationException($"Library group {string.Join(',', groupIds)} was not found.");
            }

            return LibraryExportPlanner.Build(
                groups.SelectMany(group => group.Items).ToList(),
                _libraryStore.LoadPool(),
                CyDownload.Core.Index.PackageLookup.Build(_repository.LoadAll()),
                _database.PoolDirectory,
                _log);
        }).ConfigureAwait(true);

        return await Task.Run(() => LibraryExporter.Copy(files, directory, _log)).ConfigureAwait(true);
    }

    /// <summary>把资料库里选中的多个单文件行一次复制到目录（多选右键「导出这个 deb」）。</summary>
    public async Task<LibraryExportResult> ExportLibraryFilesAsync(IReadOnlyList<LibraryRowItem> rows, string directory)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var files = await Task.Run(() =>
        {
            var list = new List<LibrarySourceFile>();
            var order = 0;
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var row in rows.Where(row => !string.IsNullOrWhiteSpace(row.RelativePath)))
            {
                if (!seen.Add(row.RelativePath)) continue;

                list.Add(new LibrarySourceFile(
                    Path.Combine(_database.PoolDirectory, row.RelativePath),
                    row.PackageId, row.VersionText, "", order++));
            }

            return list;
        }).ConfigureAwait(true);

        return await Task.Run(() => LibraryExporter.Copy(files, directory, _log)).ConfigureAwait(true);
    }

    /// <summary>把资料库里**单个 deb**（成员行/独立行）复制到目录。</summary>
    public async Task<LibraryExportResult> ExportLibraryFileAsync(LibraryRowItem row, string directory)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        if (string.IsNullOrWhiteSpace(row.RelativePath))
        {
            throw new InvalidOperationException("This library row has no file on disk.");
        }

        var file = new LibrarySourceFile(
            Path.Combine(_database.PoolDirectory, row.RelativePath),
            row.PackageId,
            row.VersionText,
            "iphoneos-arm",
            0);

        return await Task.Run(() => LibraryExporter.Copy([file], directory, _log)).ConfigureAwait(true);
    }

    /// <summary>删除一个资料库分组记录（只删记录，不动磁盘文件）。</summary>
    public async Task DeleteLibraryGroupAsync(long groupId)
        => await DeleteLibraryGroupsAsync([groupId]).ConfigureAwait(true);

    /// <summary>删除一组（合并显示时删掉它代表的**全部** run，）。</summary>
    public async Task DeleteLibraryGroupsAsync(IReadOnlyList<long> groupIds)
    {
        ArgumentNullException.ThrowIfNull(groupIds);
        if (groupIds.Count == 0) return;

        await Task.Run(() =>
        {
            foreach (var groupId in groupIds) _libraryStore.DeleteGroup(groupId);
        }).ConfigureAwait(true);
        await RefreshArchiveAsync().ConfigureAwait(true);
    }

    /// <summary>浏览器右键「算闭包」：选中这个包并求解（返回是否成功打开闭包窗口）。</summary>
    public async Task<bool> ResolveSingleAsync(string packageId, string? version = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);

        var item = Packages.FirstOrDefault(package =>
            string.Equals(package.PackageId, packageId, StringComparison.Ordinal) &&
            (string.IsNullOrEmpty(version) || string.Equals(package.Version, version, StringComparison.Ordinal)));

        if (item is null) return false;

        SelectedPackages.Clear();
        SelectedPackages.Add(item);
        OnPropertyChanged(nameof(SelectedCountText));
        _resolveCommand.RaiseCanExecuteChanged();

        await ResolveAsync().ConfigureAwait(true);
        return _lastResolution is { Success: true };
    }

    /// <summary>包身份四元键（与 archive_pool / packages 的主键口径一致）。</summary>
    private static string LibraryPackageKey(string packageId, string version, string architecture, string repo)
        => PoolKey(packageId, version, architecture, repo);

    private static string PoolKey(string packageId, string version, string architecture, string repo)
        => packageId + "\u0000" + version + "\u0000" + architecture + "\u0000" + repo;
    // ── 设置与语言 ──────────────────────────────────────────────────────────

    public void ApplySettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        OnPropertyChanged(nameof(Settings));
        _resolveCommand.RaiseCanExecuteChanged();
    }

    /// <summary>有多少 pool 记录的文件在磁盘上不存在（修复按钮确认框用）。</summary>
    public Task<int> CountMissingPoolFilesAsync()
        => Task.Run(() => _libraryStore.LoadMissingPoolFiles(_database.PoolDirectory).Count);

    /// <summary>
    /// 修复「台账有、文件没了」的包：按身份四元在索引里找回包行并重新下载，回写 rel_path/sha。
    /// 返回 (修复数, 失败数)。索引里已经不存在的包只记日志（无法凭空恢复）。
    /// </summary>
    public async Task<(int Repaired, int Failed)> RepairMissingPoolAsync()
    {
        var poolDirectory = _database.PoolDirectory;
        var missing = await Task.Run(() => _libraryStore.LoadMissingPoolFiles(poolDirectory)).ConfigureAwait(true);
        if (missing.Count == 0) return (0, 0);

        var lookup = await Task.Run(() => CyDownload.Core.Index.PackageLookup.Build(_repository.LoadAll()))
            .ConfigureAwait(true);
        var repos = (await Task.Run(() => _repository.LoadRepos()).ConfigureAwait(true))
            .ToDictionary(row => row.Name, row => row.BaseUrl, StringComparer.Ordinal);

        var options = new HttpDownloadOptions
        {
            NoProgressTimeout = TimeSpan.FromSeconds(_settings.HttpNoProgressSeconds),
            TotalTimeout = TimeSpan.FromMinutes(_settings.HttpTotalMinutes),
            Headers = BuildHeaders(),
        };

        using var http = HttpDownloader.CreateClient();
        var fetcher = new DebFetcher(new HttpDownloader(http), options, repos, poolDirectory, _log);

        var repaired = 0;
        var failed = 0;

        foreach (var entry in missing)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            try
            {
                var package = lookup.Find(entry.PackageId, entry.Version, entry.Architecture, entry.Repo)
                    ?? throw new InvalidOperationException("package no longer in index");

                var resolved = new ResolvedPackage(package, RepoPriority: 0, IsTarget: false);
                var fetched = await fetcher.FetchAsync(resolved, progress: null, cts.Token).ConfigureAwait(true);

                _libraryStore.UpsertPool(
                [
                    entry with
                    {
                        RelativePath = Path.GetRelativePath(poolDirectory, fetched.FilePath),
                        Bytes = fetched.Bytes,
                        Sha256 = package.Sha256,
                        AcquiredAt = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture),
                    },
                ]);

                repaired++;
            }
            catch (Exception ex)
            {
                failed++;
                _log.Write($"[library] repair failed {entry.PackageId} {entry.Version}: {ex.Message}");
            }
        }

        await RefreshArchiveAsync().ConfigureAwait(true);
        return (repaired, failed);
    }

    /// <summary>预览「已从资料库移除」的 pool 记录与文件数（清理按钮确认框用）。</summary>
    public Task<PoolCleanupStats> GetPoolCleanupStatsAsync()
        => Task.Run(() => _libraryStore.CountUnreferencedPool(ProtectedPackageIds()));

    /// <summary>用户分组里的插件（清理时不能被当成"已从资料库移除"，）。</summary>
    private IReadOnlyCollection<string> ProtectedPackageIds()
        => _userGroups.Groups
            .SelectMany(group => group.Members)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// 删除「已从资料库移除的插件」的实体文件与台账行（内容寻址共享的文件会保留）。
    /// 删完刷新资料库，返回实际删掉的统计。
    /// </summary>
    public async Task<PoolCleanupResult> CleanupUnreferencedPoolAsync()
    {
        var poolDirectory = _database.PoolDirectory;
        var result = await Task.Run(() => _libraryStore.DeleteUnreferencedPool(
                poolDirectory, _log, ProtectedPackageIds()))
            .ConfigureAwait(true);

        await RefreshArchiveAsync().ConfigureAwait(true);
        return result;
    }

    // ── 用户分组（抄 IdeviceGui GroupStore：groups.json 外挂，不改主库） ─────────────────

    /// <summary>资料库分组下拉的选项：全部 + 用户分组名。</summary>
    public ObservableCollection<string> LibraryGroupNames { get; } = [];

    /// <summary>当前生效的分组（「全部」= 不过滤）。</summary>
    public string ActiveLibraryGroup => _activeGroupMembers is null ? Loc.Get("Main.Library.AllGroups") : _activeGroupName;

    private string _activeGroupName = "";

    /// <summary>资料库排序（列头点击）。</summary>
    public void SortLibrary(string memberPath, bool descending)
    {
        var field = memberPath switch
        {
            "Bytes" => LibrarySortField.Size,
            "NameText" or "PackageId" => LibrarySortField.Name,
            _ => LibrarySortField.Time,
        };

        _libraryView.Sort = new LibrarySort(field, descending);
        RefreshLibraryRows();
    }

    /// <summary>当前资料库排序方向（列头箭头由视图自己画）。</summary>
    public bool LibrarySortDescending => _libraryView.Sort.Descending;

    public string LibrarySortMemberPath => _libraryView.Sort.Field switch
    {
        LibrarySortField.Size => "Bytes",
        LibrarySortField.Name => "PackageId",
        _ => "CreatedAt",
    };

    private void RebuildLibraryGroupNames()
    {
        LibraryGroupNames.Clear();
        LibraryGroupNames.Add(Loc.Get("Main.Library.AllGroups"));
        foreach (var group in _userGroups.Groups.OrderBy(group => group.Name, StringComparer.Ordinal))
        {
            LibraryGroupNames.Add(group.Name);
        }

        if (string.IsNullOrWhiteSpace(_activeGroupName) ||
            !_userGroups.Groups.Any(group => string.Equals(group.Name, _activeGroupName, StringComparison.OrdinalIgnoreCase)))
        {
            _activeGroupName = "";
        }

        _activeGroupMembers = string.IsNullOrWhiteSpace(_activeGroupName)
            ? null
            : _userGroups.Groups
                .First(group => string.Equals(group.Name, _activeGroupName, StringComparison.OrdinalIgnoreCase))
                .Members
                .ToHashSet(StringComparer.Ordinal);

        OnPropertyChanged(nameof(ActiveLibraryGroup));
    }

    /// <summary>切换资料库分组（「全部」= 显示所有插件）。</summary>
    public async Task SetActiveLibraryGroupAsync(string? name)
    {
        var requested = (name ?? "").Trim();
        _activeGroupName = requested.Length == 0 ||
                           string.Equals(requested, Loc.Get("Main.Library.AllGroups"), StringComparison.Ordinal)
            ? ""
            : requested;

        RebuildLibraryGroupNames();
        await RefreshArchiveAsync().ConfigureAwait(true);
    }

    /// <summary>把插件加进指定分组（分组不存在就先建）。</summary>
    public async Task<bool> AddPackageToGroupAsync(string packageId, string groupName)
    {
        // 分组已存在 = 往里加成员（不是"重建同名分组"，不能按重名校验拦掉）
        var group = UserGroupsStore.Find(_userGroups, groupName);
        if (group is null)
        {
            if (UserGroupsStore.ValidateName(_userGroups, groupName) is not null) return false;
            group = UserGroupsStore.GetOrAdd(_userGroups, groupName);
        }

        if (!group.Members.Contains(packageId, StringComparer.Ordinal)) group.Members.Add(packageId);

        var saved = await SaveUserGroupsAsync().ConfigureAwait(true);
        RebuildLibraryGroupNames();
        await RefreshArchiveAsync().ConfigureAwait(true);
        return saved;
    }

    /// <summary>把插件从当前分组移出。</summary>
    public async Task RemovePackageFromActiveGroupAsync(string packageId)
    {
        if (string.IsNullOrWhiteSpace(_activeGroupName)) return;

        var group = UserGroupsStore.Find(_userGroups, _activeGroupName);
        group?.Members.RemoveAll(member => string.Equals(member, packageId, StringComparison.Ordinal));
        await SaveUserGroupsAsync().ConfigureAwait(true);
        RebuildLibraryGroupNames();
        await RefreshArchiveAsync().ConfigureAwait(true);
    }

    /// <summary>新建分组并切到它。</summary>
    public async Task<bool> CreateLibraryGroupAsync(string name)
    {
        if (UserGroupsStore.ValidateName(_userGroups, name) is not null) return false;

        UserGroupsStore.GetOrAdd(_userGroups, name);
        await SaveUserGroupsAsync().ConfigureAwait(true);
        _activeGroupName = name.Trim();
        RebuildLibraryGroupNames();
        await RefreshArchiveAsync().ConfigureAwait(true);
        return true;
    }

    /// <summary>重命名分组（成员保留）。</summary>
    public async Task<bool> RenameLibraryGroupAsync(string oldName, string newName)
    {
        if (UserGroupsStore.ValidateName(_userGroups, newName, exceptName: oldName) is not null) return false;

        var group = UserGroupsStore.Find(_userGroups, oldName);
        if (group is null) return false;

        group.Name = newName.Trim();
        await SaveUserGroupsAsync().ConfigureAwait(true);
        _activeGroupName = group.Name;
        RebuildLibraryGroupNames();
        await RefreshArchiveAsync().ConfigureAwait(true);
        return true;
    }

    /// <summary>删除分组（只删分组，不动资料库与文件）。</summary>
    public async Task<bool> DeleteLibraryGroupAsync(string name)
    {
        var removed = _userGroups.Groups.RemoveAll(group =>
            string.Equals(group.Name, name, StringComparison.OrdinalIgnoreCase));
        if (removed == 0) return false;

        await SaveUserGroupsAsync().ConfigureAwait(true);
        _activeGroupName = "";
        RebuildLibraryGroupNames();
        await RefreshArchiveAsync().ConfigureAwait(true);
        return true;
    }

    private Task<bool> SaveUserGroupsAsync()
        => Task.Run(() =>
        {
            if (UserGroupsStore.Save(_userGroups, _database.ArchiveRoot, out var problem)) return true;

            _log.Write("[groups] failed to save groups.json: " + problem);
            return false;
        });

    // ── iOS 版本筛选（浏览器） ───────────────────────────────────────────────

    /// <summary>iOS 筛选项：首项是「全部版本」（显示文案走资源），其余是版本号。</summary>
    public IReadOnlyList<string> IosFilterOptions { get; } =
        [Loc.Get("Main.Library.IosAll"), "5.0", "6.0", "6.1", "7.0", "7.1", "8.0", "8.1", "8.4", "9.0"];

    /// <summary>下拉当前项（显示字符串；「全部版本」映射成不过滤）。</summary>
    public string IosFilterSelection
    {
        get => _iosFilterVersion.Length == 0 ? IosFilterOptions[0] : _iosFilterVersion;
        set
        {
            _iosFilterVersion = string.Equals(value, IosFilterOptions[0], StringComparison.Ordinal)
                ? ""
                : (value ?? "");
            OnPropertyChanged(nameof(IosFilterSelection));
            ApplyPackageFilter();
        }
    }

    /// <summary>实际生效的 iOS 版本（空 = 不过滤）。</summary>
    public string IosFilterVersion => _iosFilterVersion;

    /// <summary>应用设置并刷新源页签（「显示更多源」会改变页签列表）。</summary>
    public async Task ApplySettingsAsync(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ApplySettings(settings);
        await ReloadSourcesAsync().ConfigureAwait(true);
        await RefreshArchiveAsync().ConfigureAwait(true);

        var snapshot = settings;
        await SafeFireAndForget.Run(
            () => Task.Run(() =>
            {
                SettingsStore.Save(_database.ArchiveRoot, snapshot, out var problem);
                if (problem is not null) _log.Write("[settings] save failed: " + problem);
            }),
            "SaveSettings").ConfigureAwait(true);
    }

    /// <summary>运行时切换语言（**立即落盘**；：此前只在设置窗口保存时才写）。</summary>
    public void ApplyLanguage(string culture)
    {
        Loc.SetLanguage(culture);
        _settings = _settings with { Language = culture };
        var snapshot = _settings;
        var root = _database.ArchiveRoot;
        SafeFireAndForget.Run(
            () => Task.Run(() =>
            {
                SettingsStore.Save(root, snapshot, out var problem);
                if (problem is not null) _log.Write("[settings] save language failed: " + problem);
            }),
            "SaveLanguage");
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(string.Empty);            // 空名字 = 所有绑定重读
        foreach (var source in Sources) source.RefreshLocalizedText();
        StatusText = Loc.Get("Main.Status.Idle");
    }

    public void Dispose()
    {
        // 关窗/退出时先取消长任务：否则后台续体会在 DB 已 Dispose 后继续写
        try { CancelCurrentOperation(); }
        catch (ObjectDisposedException) { }

        DropQueuedDownloads();
        Loc.LanguageChanged -= OnLanguageChanged;
    }

    private static string FormatTimestamp(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        return DateTimeOffset.TryParse(raw, out var parsed)
            ? parsed.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
            : raw;
    }
}

/// <summary>存档库句柄：ViewModel 只拿得到路径，不直接摸数据库连接。</summary>
internal sealed record ArchiveDatabaseHandle(string ArchiveRoot)
{
    public string PoolDirectory => Path.Combine(ArchiveRoot, "pool");
}





























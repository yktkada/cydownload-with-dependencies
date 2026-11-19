using System.IO;
using System.Threading;
using System.Windows;
using CyDownload.App.Infrastructure;
using CyDownload.App.Localization;
using CyDownload.App.ViewModels;
using CyDownload.App.Views;
using CyDownload.Core.Diagnostics;
using CyDownload.Core.Index;

namespace CyDownload.App;

/// <summary>
/// 应用入口。启动顺序是硬要求：
/// ⓪ 语言 → ① 单实例 → ② 程序目录可写自检 → ③ 临时目录 → ④ 设置 → ⑤ 开库 → ⑥ 建主窗口。
/// <para>
/// 整个启动序列包在 try/catch 里：catch → 崩溃日志 + 提示 + 显式 Shutdown。
/// 不能只靠全局异常兜底（窗口构造失败时进程会变成「无窗口的 Dispatcher」还占着单实例 Mutex）。
/// </para>
/// </summary>
public partial class App : Application
{
    private const string SingleInstanceMutexName = "CyDownload.SingleInstance.v1";

    private Mutex? _singleInstance;
    private FileLog? _fileLog;
    private UiLogSink? _uiLog;
    private ArchiveDatabase? _database;
    private PackageRepository? _repository;
    private MainViewModel? _mainViewModel;

    internal static event Action<string, Exception>? UnhandledErrorReported;

    /// <summary>给 Core 层用的日志出口（抓取/落库/求解/导出都写这里）。启动前为 null。</summary>
    internal static ILogSink? LogSink { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        RegisterGlobalExceptionHandlers();

        try
        {
            // ⓪ 语言必须先初始化：下面任何 MessageBox 都要用资源文案
            Loc.Initialize(setting: null, out var languageProblem);

            if (!RunStartupChecks())
            {
                Shutdown(1);
                return;
            }

            Directory.CreateDirectory(AppPaths.LogsDirectory);
            Directory.CreateDirectory(AppPaths.ArchiveDirectory);

            _fileLog = new FileLog(AppPaths.LogsDirectory, enabled: true);
            _uiLog = new UiLogSink(_fileLog);
            LogSink = _uiLog;
            _fileLog.Write("启动完成：程序目录 " + AppPaths.ProgramDirectory);

            // ④ 设置（坏 JSON/坏值一律回默认，不让启动失败）
            var settings = SettingsStore.Load(AppPaths.ArchiveDirectory, out var settingsProblem);
            if (settingsProblem is not null)
            {
                _fileLog.Write("settings.json 读取有问题，已用默认值：" + settingsProblem);
            }

            Loc.SetLanguage(settings.Language);

            // ⑤ 存档库
            var database = ArchiveDatabase.Open(AppPaths.DatabasePath);
            _database = database;
            _repository = new PackageRepository(database, _uiLog);
            _fileLog.Write($"存档库已打开：{database.DatabasePath}（schema v{database.UserVersion}）");

            // 空库写内置源：否则干净机器上没有页签，抓取永远不可用。
            // 只种一次；之后内置清单升级时只补「缺失的名字」，不覆盖用户改过的源。
            if (!settings.SourcesSeeded)
            {
                if (!_repository.HasAnyRepo())
                {
                    foreach (var source in Core.Repos.DefaultSources.All())
                    {
                        _repository.SaveRepo(source);
                    }

                    _fileLog.Write(
                        $"首次启动：已写入内置源 {Core.Repos.DefaultSources.All().Count} 个" +
                        "（启用的：saurik / bigboss / apt.gs / julioverne / cokepokes）");
                }

                settings = settings with { SourcesSeeded = true };
                SettingsStore.Save(AppPaths.ArchiveDirectory, settings, out var seedProblem);
                if (seedProblem is not null) _fileLog.Write("settings.json 写入种子标记失败：" + seedProblem);
            }

            // 内置源清单升级：补缺失项（已存在的不动，避免覆盖用户改过的地址/优先级）
            if (settings.BuiltInSourcesVersion < Core.Repos.DefaultSources.Version)
            {
                var existing = _repository.LoadRepos().Select(row => row.Name).ToHashSet(StringComparer.Ordinal);
                var added = 0;
                foreach (var source in Core.Repos.DefaultSources.All())
                {
                    if (existing.Contains(source.Name)) continue;
                    _repository.SaveRepo(source);
                    added++;
                }

                settings = settings with { BuiltInSourcesVersion = Core.Repos.DefaultSources.Version };
                SettingsStore.Save(AppPaths.ArchiveDirectory, settings, out var versionProblem);
                if (versionProblem is not null) _fileLog.Write("settings.json 写入源清单版本失败：" + versionProblem);
                _fileLog.Write($"内置源清单升级到 v{Core.Repos.DefaultSources.Version}：补 {added} 个");
            }

            // ⑥ 主窗口
            var handle = new ArchiveDatabaseHandle(AppPaths.ArchiveDirectory);
            _mainViewModel = new MainViewModel(handle, database, _repository, settings, _uiLog);
            var window = new MainWindow(_mainViewModel);
            MainWindow = window;
            window.Show();
            _fileLog.Write("主窗口已显示");
        }
        catch (Exception ex)
        {
            ReportStartupFailure(ex);
            Shutdown(1);
        }
    }

    private static bool RunStartupChecks()
    {
        if (!TryAcquireSingleInstance(out var mutex))
        {
            ShowMessage(
                Loc.Get("App.SingleInstance.Message"),
                Loc.Get("App.SingleInstance.Title"),
                MessageBoxImage.Information);
            return false;
        }

        SingleInstanceMutex = mutex;

        if (!AppPaths.EnsureWritable(out var problem))
        {
            ShowMessage(problem, Loc.Get("App.NotWritable.Title"), MessageBoxImage.Error);
            return false;
        }

        if (!AppPaths.UseProgramTempDirectory(out var tempProblem))
        {
            ShowMessage(tempProblem, Loc.Get("App.TempNotWritable.Title"), MessageBoxImage.Error);
            return false;
        }

        return true;
    }

    private static Mutex? SingleInstanceMutex
    {
        get => ((App)Current)._singleInstance;
        set => ((App)Current)._singleInstance = value;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _mainViewModel?.Dispose(); } catch { /* 退出时不能再抛 */ }
        try { _database?.Checkpoint(); } catch { /* 关库前尽力合并 WAL */ }
        try { _database?.Dispose(); } catch { /* 退出时不能再抛 */ }
        try { _fileLog?.Write("退出"); } catch { /* 退出时不能再抛 */ }
        try { _fileLog?.Dispose(); } catch { /* 退出时不能再抛 */ }
        try { _singleInstance?.ReleaseMutex(); } catch { /* 未持有则忽略 */ }
        _singleInstance?.Dispose();
        _singleInstance = null;
        base.OnExit(e);
    }

    /// <summary>把异常送到日志 + 状态栏两处（<c>SafeFireAndForget</c> 与全局异常都走这里）。</summary>
    internal static void ReportUnhandled(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var message = Loc.Format("Status.UnhandledError", exception.Message);
        try { CrashReporter.Write("Unhandled", exception); } catch { /* 记录崩溃时不能再抛 */ }
        try { LogSink?.Write("未处理异常：" + exception.GetType().Name + ": " + exception.Message); } catch { }

        UnhandledErrorReported?.Invoke(message, exception);
    }

    private static void ReportStartupFailure(Exception exception)
    {
        try { CrashReporter.Write("Startup", exception); } catch { }

        var detail = exception.GetType().Name + ": " + exception.Message;
        var text = Loc.IsInitialized ? Loc.Format("App.StartupFailed.Message", detail) : detail;
        ShowMessage(text, Loc.Get("App.StartupFailed.Title"), MessageBoxImage.Error);
    }

    private static void ShowMessage(string text, string title, MessageBoxImage image)
    {
        try
        {
            MessageBox.Show(text, title, MessageBoxButton.OK, image);
        }
        catch
        {
            // 提示失败只能放弃提示；调用方仍会走 Shutdown
        }
    }

    private static bool TryAcquireSingleInstance(out Mutex? mutex)
    {
        mutex = null;
        try
        {
            mutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
            return createdNew;
        }
        catch
        {
            // 宁可可能双开，也不要打不开
            return true;
        }
    }

    private void RegisterGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            ReportUnhandled(args.Exception);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception) CrashReporter.Write("AppDomain", exception);
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            args.SetObserved();
            ReportUnhandled(args.Exception);
        };
    }
}





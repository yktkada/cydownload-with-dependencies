// carried from IdeviceGui ViewModels/AsyncRelayCommand.cs (main)
using System.Windows.Input;

namespace CyDownload.App.Mvvm;

/// <summary>
/// 异步命令：执行期间自动禁用自己，异常不会逃逸到 UI 线程。
/// ICommand.Execute 是同步签名，因此这里用 async void —— 但内部的 await 不会阻塞 UI 线程。
/// <para>
/// <b>CanExecuteChanged 直接挂到 WPF 的 <see cref="CommandManager.RequerySuggested"/>（add/remove 转发）。</b>
/// 不要改成构造函数里 `CommandManager.RequerySuggested += …` 且从不退订：那是<b>静态事件持有 VM</b>，
/// 每个命令对象都会活到进程结束（内存泄漏）。
/// </para>
/// </summary>
internal sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool>? _canExecute;
    private bool _running;

    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    /// <summary>转发到 WPF 的重查询机制，不持有任何实例引用。</summary>
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public event Action<Exception>? Failed;

    public bool CanExecute(object? parameter) => !_running && (_canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (_running) return;
        _running = true;
        RaiseCanExecuteChanged();
        try
        {
            await _execute();
        }
        catch (OperationCanceledException)
        {
            // 用户取消不是错误：不写 crash.log、不弹「未处理异常」，状态由 VM 自己收尾
        }
        catch (Exception ex)
        {
            // 先给订阅者，再无条件上报（App.ReportUnhandled = 崩溃日志 + 状态栏）：
            // 只在 `Failed?.Invoke` 里转一圈的话，没人订阅就静默消失了。
            Failed?.Invoke(ex);
            App.ReportUnhandled(ex);
        }
        finally
        {
            _running = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CommandManager.InvalidateRequerySuggested();
}

// carried from IdeviceGui Services/SafeFireAndForget.cs (main)
using CyDownload.App.Localization;

namespace CyDownload.App.Infrastructure;

/// <summary>
/// 无主异步任务的统一收口（硬约束：禁止裸 `_ =`）。
/// <para>
/// 理由：① **异常不许静默消失**——裸 `_ = XxxAsync` 的异常只会进
/// <c>TaskScheduler.UnobservedTaskException</c>（进程退出才可能触发）；
/// ② 失败要进**日志 + 状态栏两处可见**；③ 操作名写在这里，靠读代码就知道"谁在后台跑"。
/// </para>
/// <para>
/// WPF 事件处理器的 `async void` 用 <see cref="RunHandler"/> 包一层，把 async void 缩到只剩一个入口；
/// <see cref="OperationCanceledException"/> 视为正常收尾（关窗/取消），不记错。
/// </para>
/// </summary>
internal static class SafeFireAndForget
{
    /// <summary>跑一个无主异步任务；成功静默，失败进日志 + 状态栏（不吞）。</summary>
    internal static Task Run(Func<Task> work, string operation, Action<string, Exception>? onError = null)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(operation);
        return RunCore(work, operation, onError ?? ReportToApp);
    }

    /// <summary>同步委托版本（内部包成已完成的 Task）。</summary>
    internal static Task Run(Action work, string operation, Action<string, Exception>? onError = null)
    {
        ArgumentNullException.ThrowIfNull(work);
        return Run(
            () =>
            {
                work();
                return Task.CompletedTask;
            },
            operation,
            onError);
    }

    /// <summary>WPF 事件处理器专用：把 `async void` 收敛到一个入口（异常同样不吞）。</summary>
    /// <remarks>
    /// 这里的 `_ =` 是**全仓唯一允许的裸 `_ =`**：它就是「收口」本身——
    /// <see cref="Run"/> 内部已经把异常接住并送日志 + 状态栏，返回的 Task 不会再抛。
    /// 其它任何地方都不许直接写 `_ = XxxAsync`。
    /// </remarks>
    internal static void RunHandler(Func<Task> work, string operation, Action<string, Exception>? onError = null)
        => _ = Run(work, operation, onError);

    /// <summary>把异常写成「操作名 未完成：类型: 消息」进日志 + 状态栏（可直接用于已有 try/catch 的事件）。</summary>
    internal static void Report(string operation, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(exception);

        var message = Loc.Format(
            "Status.OperationFailed", operation, exception.GetType().Name, exception.Message);

        try { CrashReporter.Write("FireAndForget." + operation, exception); }
        catch { /* 崩溃日志失败不能掩盖原异常 */ }

        try { App.ReportUnhandled(new InvalidOperationException(message, exception)); }
        catch { /* 上报失败同样不能递归 */ }
    }

    private static void ReportToApp(string message, Exception exception)
    {
        try { App.ReportUnhandled(new InvalidOperationException(message, exception)); }
        catch { /* 上报失败不能递归 */ }
    }

    private static async Task RunCore(Func<Task> work, string operation, Action<string, Exception> onError)
    {
        try
        {
            await work().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 关窗 / 取消：正常收尾，不记错
        }
        catch (Exception exception)
        {
            var message = Loc.Format(
                "Status.OperationFailed", operation, exception.GetType().Name, exception.Message);

            try { CrashReporter.Write("FireAndForget." + operation, exception); }
            catch { /* 同上 */ }

            try { onError(message, exception); }
            catch { /* 上报失败不能递归 */ }
        }
    }
}

using CyDownload.Core.Diagnostics;

namespace CyDownload.App.Infrastructure;

/// <summary>
/// 日志出口：**同时**写文件日志（如果启用）并抛出事件给界面日志面板。
/// <para>
/// 为什么不让界面直接读文件：Core 的抓取/落库/求解都往 <see cref="ILogSink"/> 写，
/// 界面需要实时看到同一批行；文件那份负责事后复盘。
/// </para>
/// <para>写日志失败不能影响主流程——这里所有异常都吞掉（与 <see cref="FileLog"/> 的口径一致）。</para>
/// </summary>
internal sealed class UiLogSink : ILogSink
{
    private readonly ILogSink? _file;

    public UiLogSink(ILogSink? file = null) => _file = file;

    /// <summary>新的一行（**可能在后台线程触发**；订阅者要自己切回 UI 线程）。</summary>
    public event Action<string>? Line;

    public void Write(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        try
        {
            _file?.Write(line);
        }
        catch
        {
            // 文件日志失败不影响界面
        }

        try
        {
            Line?.Invoke(line);
        }
        catch
        {
            // 订阅者异常不能反过来打断 Core
        }
    }
}

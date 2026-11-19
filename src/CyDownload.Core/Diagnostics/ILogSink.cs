namespace CyDownload.Core.Diagnostics;

/// <summary>
/// 日志出口（Core 层用）。实现放在 App（文件日志），Core 只依赖这个接口。
/// <para>
/// 为什么需要它：，
/// 而抓取与下载都在 Core 里 —— 不能让 Core 反过来引用 App 的 <c>FileLog</c>。
/// </para>
/// <para>
/// 实现必须是**线程安全、不抛异常**的（写日志失败不能影响主流程）；文案保持中文原文、不随界面语言变。
/// </para>
/// </summary>
public interface ILogSink
{
    void Write(string line);
}

/// <summary>什么都不做的日志出口（单元测试 / 未接线时用）。</summary>
public sealed class NullLogSink : ILogSink
{
    public static NullLogSink Instance { get; } = new();

    private NullLogSink()
    {
    }

    public void Write(string line)
    {
        // 故意为空
    }
}

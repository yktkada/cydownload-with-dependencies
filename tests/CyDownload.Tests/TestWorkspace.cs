using System.IO;
using Xunit;

namespace CyDownload.Tests;

/// <summary>
/// 临时工作目录夹具：建在**测试程序目录下的 _test-tmp**，不用 <c>Path.GetTempPath</c> ——
/// 沙箱禁止测试进程写用户 TEMP，而程序目录是可写的（与「只写程序目录」的硬约束一致）。
/// </summary>
internal sealed class TestWorkspace : IDisposable
{
    private static readonly string RootBase = Path.Combine(AppContext.BaseDirectory, "_test-tmp");

    public string Root { get; }

    public TestWorkspace(string tag = "cyd-test")
    {
        Root = Path.Combine(RootBase, $"{tag}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Root);
    }

    public string Full(string relativeName) => Path.Combine(Root, relativeName);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
        catch
        {
            // 临时目录清理失败不影响测试结论
        }
    }
}

using System.IO;

namespace CyDownload.App.Infrastructure;

/// <summary>
/// 目标目录可写探测：导出前先建一个探针文件再删掉。
/// <para>
/// 有些目录（Program Files、部分 UAC 保护的桌面重定向目录、只读共享）不提升权限就写不进去；
/// 提前探测能给出**可读的提示**，而不是导出到一半抛 UnauthorizedAccessException 写 crash.log。
/// </para>
/// </summary>
public static class WriteProbe
{
    /// <summary>目录可写返回 true；不存在、无权限、只读都返回 false。</summary>
    public static bool CanWrite(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return false;

        try
        {
            if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);

            var probe = Path.Combine(directory, ".cydownload-write-test-" + Guid.NewGuid().ToString("N")[..8]);
            using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.WriteByte(0);
            }

            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }
}

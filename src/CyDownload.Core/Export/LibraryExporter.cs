using System.Globalization;
using CyDownload.Core.Diagnostics;

namespace CyDownload.Core.Export;

/// <summary>资料库里要导出的一个 deb（pool 里的路径 + 显示用的字段）。</summary>
public sealed record LibrarySourceFile(
    string PoolPath,
    string PackageId,
    string Version,
    string Architecture,
    int Order);

/// <summary>资料库导出结果。</summary>
public sealed record LibraryExportResult(
    string Directory,
    IReadOnlyList<string> Copied,
    IReadOnlyList<string> Missing,
    int SkippedIdentical,
    int SkippedDifferent,
    long TotalBytes,
    IReadOnlyList<string>? Failed = null);

/// <summary>
/// 把**资料库里已有的 deb** 复制到一个目录（右键「导出」用）。
/// <para>
/// 与 <see cref="DebExporter"/> 的关系：那个是「从闭包下载/导出」，这个是「pool 里已经有文件，只复制」。
/// 语义完全一致——**只复制，不删除、不覆盖**：同名同内容跳过、同名不同内容不覆盖并计入 skipped。
/// </para>
/// <para>文件命名沿用闭包的补零规则（`NN-包名_版本_架构.deb`），因为目标同样是 `Cydia/AutoInstall`。</para>
/// </summary>
public static class LibraryExporter
{
    public static LibraryExportResult Copy(
        IReadOnlyList<LibrarySourceFile> files,
        string directory,
        ILogSink? log = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var sink = log ?? NullLogSink.Instance;
        var target = Path.GetFullPath(directory);
        Directory.CreateDirectory(target);

        var ordered = files.OrderBy(file => file.Order).ToList();
        var width = ordered.Count <= 0 ? 1 : ordered.Count.ToString(CultureInfo.InvariantCulture).Length;

        var copied = new List<string>();
        var missing = new List<string>();
        var failed = new List<string>();
        var skippedIdentical = 0;
        var skippedDifferent = 0;
        long totalBytes = 0;

        for (var index = 0; index < ordered.Count; index++)
        {
            var file = ordered[index];

            try
            {
                if (!File.Exists(file.PoolPath))
                {
                    missing.Add(file.PackageId + " " + file.Version);
                    continue;
                }

                var number = (index + 1).ToString($"D{width}", CultureInfo.InvariantCulture);
                var fileName = $"{number}-{file.PackageId}_{file.Version}_{file.Architecture}.deb";
                var destination = Path.Combine(target, fileName);

                if (File.Exists(destination))
                {
                    if (IsSameContent(destination, file.PoolPath)) skippedIdentical++;
                    else skippedDifferent++;
                    copied.Add(fileName);
                    // 合计口径与 DebExporter 一致：已存在的文件也算进总字节
                    totalBytes += new FileInfo(destination).Length;
                    continue;
                }

                // 原子复制：先 .tmp 再改名（与 DebExporter 同口径）
                var temp = destination + ".tmp";
                try
                {
                    File.Copy(file.PoolPath, temp, overwrite: true);
                    File.Move(temp, destination, overwrite: false);
                }
                finally
                {
                    // 成功/失败都不留 .tmp
                    if (File.Exists(temp)) File.Delete(temp);
                }

                copied.Add(fileName);
                totalBytes += new FileInfo(destination).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 单文件失败（被占用/权限/磁盘满）不拖垮整次导出，也不再变成"未处理异常"
                failed.Add(file.PackageId + " " + file.Version);
                sink.Write($"[library-export] 跳过 {file.PackageId} {file.Version}：{ex.GetType().Name}: {ex.Message}");
            }
        }

        sink.Write(
            $"[library-export] {target}：复制 {copied.Count - skippedIdentical - skippedDifferent} 个，" +
            $"同名跳过 {skippedIdentical}，内容不同未覆盖 {skippedDifferent}，缺文件 {missing.Count}，失败 {failed.Count}");

        return new LibraryExportResult(target, copied, missing, skippedIdentical, skippedDifferent, totalBytes, failed);
    }

    private static bool IsSameContent(string left, string right)
    {
        var leftInfo = new FileInfo(left);
        var rightInfo = new FileInfo(right);
        if (!leftInfo.Exists || !rightInfo.Exists) return false;
        if (leftInfo.Length != rightInfo.Length) return false;

        using var a = new FileStream(left, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var b = new FileStream(right, FileMode.Open, FileAccess.Read, FileShare.Read);
        return System.Security.Cryptography.SHA256.HashData(a)
            .AsSpan()
            .SequenceEqual(System.Security.Cryptography.SHA256.HashData(b));
    }
}

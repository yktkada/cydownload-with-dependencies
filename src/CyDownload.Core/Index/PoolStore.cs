using System.Globalization;
using System.Security.Cryptography;
using CyDownload.Core.Diagnostics;
using Microsoft.Data.Sqlite;

namespace CyDownload.Core.Index;

/// <summary>把旧布局 pool 迁到内容寻址布局的结果。</summary>
public sealed record PoolMigrationResult(int Moved, int Deduped, int RowsUpdated, int RowsInserted, int Ignored);

/// <summary>
/// 资料库的**内容寻址存储**（设计：真实相同的文件只存一份）。
/// <para>
/// 布局：`pool/objects/&lt;sha256&gt;.deb`。同一个包从两个源下到、或不同包恰好字节相同，
/// 都只落一份文件；`archive_pool` 仍然按 (包名,版本,架构,源) 记行，多行可以指向同一个对象。
/// </para>
/// <para>
/// 旧布局（`pool/&lt;包名&gt;/&lt;版本&gt;_&lt;架构&gt;_&lt;源&gt;.deb`，以及更早的 `&lt;版本&gt;_&lt;源&gt;.deb`）由
/// <see cref="Migrate"/> 一次性搬进对象目录并回写数据库。
/// </para>
/// </summary>
public static class PoolStore
{
    public const string ObjectsFolder = "objects";

    /// <summary>对象相对路径：`objects/&lt;sha256&gt;.deb`。</summary>
    public static string RelativePathFor(string sha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256);
        return Path.Combine(ObjectsFolder, sha256.Trim().ToLowerInvariant() + ".deb");
    }

    public static string FullPath(string poolDirectory, string sha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(poolDirectory);
        return Path.Combine(poolDirectory, RelativePathFor(sha256));
    }

    public static string Sha256OfBytes(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    public static string Sha256OfFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>
    /// 写入一个对象：同 sha 已存在就不重复写（返回 Created=false）。
    /// 先写 `.part` 再改名，避免中途崩溃留下半个对象。
    /// </summary>
    public static (string RelativePath, string FullPath, bool Created) SaveObject(
        string poolDirectory,
        string sha256,
        byte[] bytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(poolDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256);
        ArgumentNullException.ThrowIfNull(bytes);

        var relative = RelativePathFor(sha256);
        var full = Path.Combine(poolDirectory, relative);

        // 对象名是内容哈希，但"文件存在"不等于"内容正确"：长度不符说明被截断/损坏 → 重写
        if (File.Exists(full) && new FileInfo(full).Length == bytes.Length) return (relative, full, false);

        var directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var part = full + ".part";
        try
        {
            File.WriteAllBytes(part, bytes);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(part, full, overwrite: true);
        }
        catch
        {
            try
            {
                if (File.Exists(part)) File.Delete(part);
            }
            catch (IOException)
            {
                // 清不掉就留着，下次同名写入会覆盖
            }

            throw;
        }

        return (relative, full, true);
    }

    /// <summary>
    /// 把旧布局 pool 迁移到 `objects/`：**相同内容只留一份**，并回写 `archive_pool.rel_path`。
    /// <para>
    /// 顺序很重要：先按旧 rel_path 更新已有行；没有对应行的文件（老版本留下的）再按文件名解析补登。
    /// 无法解析且库里也没有的文件保持原样并计入 <see cref="PoolMigrationResult.Ignored"/>（不删用户文件）。
    /// </para>
    /// </summary>
    public static PoolMigrationResult Migrate(
        ArchiveDatabase database,
        string poolDirectory,
        ILogSink? log = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentException.ThrowIfNullOrWhiteSpace(poolDirectory);

        var sink = log ?? NullLogSink.Instance;
        if (!Directory.Exists(poolDirectory)) return new PoolMigrationResult(0, 0, 0, 0, 0);

        var objectsDirectory = Path.Combine(poolDirectory, ObjectsFolder);
        Directory.CreateDirectory(objectsDirectory);

        var legacyFiles = Directory.EnumerateFiles(poolDirectory, "*.deb", SearchOption.AllDirectories)
            .Where(path => !path.StartsWith(objectsDirectory, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (legacyFiles.Count == 0) return new PoolMigrationResult(0, 0, 0, 0, 0);

        var moved = 0;
        var deduped = 0;
        var rowsUpdated = 0;
        var rowsInserted = 0;
        var ignored = 0;

        var entries = new List<(string OldRelative, string Sha, long Bytes, string FullPath, string AcquiredAt)>();

        foreach (var path in legacyFiles)
        {
            var oldRelative = Path.GetRelativePath(poolDirectory, path);
            var sha = Sha256OfFile(path);
            var target = FullPath(poolDirectory, sha);

            if (File.Exists(target))
            {
                // 对象已存在：旧文件是重复副本 → 删掉（设计：真实相同的只存一份）
                File.Delete(path);
                deduped++;
            }
            else
            {
                File.Move(path, target);
                moved++;
            }

            entries.Add((
                oldRelative,
                sha,
                new FileInfo(target).Length,
                target,
                File.GetLastWriteTimeUtc(target).ToString("O", CultureInfo.InvariantCulture)));
        }

        // 清理空目录（旧布局的 <包名>/ 目录）
        foreach (var directory in Directory.EnumerateDirectories(poolDirectory))
        {
            if (string.Equals(directory, objectsDirectory, StringComparison.OrdinalIgnoreCase)) continue;
            if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        }

        database.Write(conn =>
        {
            using var transaction = conn.BeginTransaction();

            foreach (var entry in entries)
            {
                using var update = conn.CreateCommand();
                update.Transaction = transaction;

                // 旧台账的 rel_path 可能是 `/` 也可能是 `\`（DB 里存的是写入时的 Path 分隔符）
                update.CommandText =
                    "UPDATE archive_pool SET rel_path = $new, sha256 = $sha WHERE rel_path = $old OR rel_path = $oldAlt;";
                update.Parameters.AddWithValue("$new", Path.Combine(ObjectsFolder, entry.Sha + ".deb"));
                update.Parameters.AddWithValue("$sha", entry.Sha);
                update.Parameters.AddWithValue("$old", entry.OldRelative);
                update.Parameters.AddWithValue("$oldAlt", entry.OldRelative.Replace('\\', '/'));
                var updated = update.ExecuteNonQuery();
                if (updated > 0)
                {
                    rowsUpdated += updated;
                    continue;
                }

                if (!TryParseLegacyName(entry.OldRelative, out var packageId, out var version, out var architecture, out var repo))
                {
                    ignored++;
                    continue;
                }

                using var insert = conn.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO archive_pool(package_id, version, architecture, repo, rel_path, bytes, sha256, acquired_at)
                    VALUES($pkg, $ver, $arch, $repo, $rel, $bytes, $sha, $at)
                    ON CONFLICT(package_id, version, architecture, repo) DO UPDATE SET
                        rel_path = excluded.rel_path,
                        bytes = excluded.bytes,
                        sha256 = excluded.sha256,
                        acquired_at = excluded.acquired_at;
                    """;
                insert.Parameters.AddWithValue("$pkg", packageId);
                insert.Parameters.AddWithValue("$ver", version);
                insert.Parameters.AddWithValue("$arch", architecture);
                insert.Parameters.AddWithValue("$repo", repo);
                insert.Parameters.AddWithValue("$rel", Path.Combine(ObjectsFolder, entry.Sha + ".deb"));
                insert.Parameters.AddWithValue("$bytes", entry.Bytes);
                insert.Parameters.AddWithValue("$sha", entry.Sha);
                insert.Parameters.AddWithValue("$at", entry.AcquiredAt);
                insert.ExecuteNonQuery();
                rowsInserted++;
            }

            transaction.Commit();
            return 0;
        });

        sink.Write(
            $"[pool] 内容寻址迁移：移动 {moved}，去重删除 {deduped}，更新行 {rowsUpdated}，补登 {rowsInserted}，忽略 {ignored}");

        return new PoolMigrationResult(moved, deduped, rowsUpdated, rowsInserted, ignored);
    }

    /// <summary>旧布局的"架构"段判断（：`1:1.2.1` 的 sanitize 产物会被误当成架构）。</summary>
    private static bool LooksLikeArchitecture(string segment)
        => segment.Equals("all", StringComparison.OrdinalIgnoreCase) ||
           segment.Contains("arm", StringComparison.OrdinalIgnoreCase) ||
           segment.Contains("amd64", StringComparison.OrdinalIgnoreCase) ||
           segment.Contains("i386", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 解析旧文件名：`&lt;包名&gt;/&lt;版本&gt;_&lt;架构&gt;_&lt;源&gt;.deb`（v3）或
    /// `&lt;包名&gt;/&lt;版本&gt;_&lt;源&gt;.deb`（v2，架构按 iphoneos-arm 处理）。
    /// </summary>
    internal static bool TryParseLegacyName(
        string relativePath,
        out string packageId,
        out string version,
        out string architecture,
        out string repo)
    {
        packageId = "";
        version = "";
        architecture = "";
        repo = "";

        var parts = relativePath.Replace('\\', '/').Split('/');
        if (parts.Length < 2) return false;

        var fileName = Path.GetFileNameWithoutExtension(parts[^1]);
        var segments = fileName.Split('_');
        if (segments.Length < 2) return false;

        packageId = parts[^2];
        if (packageId.Length == 0) return false;

        if (segments.Length >= 3 && LooksLikeArchitecture(segments[^2]))
        {
            repo = segments[^1];
            architecture = segments[^2];
            version = string.Join('_', segments[..^2]);
        }
        else
        {
            // v2：没有架构，文件名里只有 版本_源
            repo = segments[^1];
            architecture = "iphoneos-arm";
            version = segments[0];
        }

        return version.Length > 0 && repo.Length > 0;
    }
}


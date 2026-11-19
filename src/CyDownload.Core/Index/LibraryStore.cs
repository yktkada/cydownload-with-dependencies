using System.Globalization;
using CyDownload.Core.Diagnostics;
using Microsoft.Data.Sqlite;

namespace CyDownload.Core.Index;

/// <summary>`archive_pool` 的一行：pool 里的一个 deb（含它在磁盘上的相对路径）。</summary>
public sealed record LibraryPoolEntry(
    string PackageId,
    string Version,
    string Architecture,
    string Repo,
    string RelativePath,
    long Bytes,
    string? Sha256,
    string? AcquiredAt,
    string? DisplayName = null);

/// <summary>分组里的一个成员（含它是不是根）。</summary>
public sealed record LibraryGroupItem(
    string PackageId,
    string Version,
    string Architecture,
    string Repo,
    bool IsRoot,
    int Position);

/// <summary>一次导出里的一个根插件：组头 + 它的整个依赖闭包。</summary>
public sealed record LibraryGroup(
    long Id,
    long RunId,
    string RootPackage,
    string RootVersion,
    string Target,
    string CreatedAt,
    string? ExportDir,
    IReadOnlyList<LibraryGroupItem> Items,
    string? DisplayName = null,
    IReadOnlyList<long>? GroupIds = null)
{
    public override string ToString() => $"{RootPackage} {RootVersion}（{Items.Count} 个）";
}

/// <summary>落库用的分组草稿（还没拿到 group id）。</summary>
public sealed record LibraryGroupDraft(
    string RootPackage,
    string RootVersion,
    IReadOnlyList<LibraryGroupItem> Items,
    string? DisplayName = null,
    IReadOnlyList<long>? GroupIds = null);

/// <summary>「已移除插件」的统计（清理前预览）。</summary>
public sealed record PoolCleanupStats(int Entries, int Files, long Bytes);

/// <summary>清理结果：删了多少台账行 / 文件 / 字节，多少个文件删失败。</summary>
public sealed record PoolCleanupResult(int DeletedRows, int DeletedFiles, long DeletedBytes, int FailedFiles);

/// <summary>
/// 资料库分组与 pool 台账（schema v2，）。
/// <para>
/// 行为对齐 `iDeviceInstaller` 的资料库：**一次导出 = 一个 run；每个用户选中的插件 = 一个组；
/// 组里是它这一趟的整个依赖闭包**。同一份 deb 可以出现在多个组里（插件各自的闭包有交集），
/// 这是「按插件的闭包」而不是「按文件去重」。
/// </para>
/// <para>故意不做「求解期写入」：求解可能失败/被放弃，只有**导出成功那一刻**的事实才写进来。</para>
/// </summary>
public sealed class LibraryStore
{
    private readonly ArchiveDatabase _database;
    private readonly ILogSink _log;

    public LibraryStore(ArchiveDatabase database, ILogSink? log = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
        _log = log ?? NullLogSink.Instance;
    }

    /// <summary>
    /// 一次导出落库：写完 pool 台账 + 每个根插件一组（同一事务）。
    /// <returns>本次的 run_id（同一次导出的多个组共用一个，便于将来整批删除）。</returns>
    /// </summary>
    public long SaveRun(
        string target,
        string? exportDirectory,
        IReadOnlyList<LibraryGroupDraft> groups,
        IReadOnlyList<LibraryPoolEntry> pool,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(pool);

        var stamp = now.ToString("O", CultureInfo.InvariantCulture);

        return _database.Write(conn =>
        {
            using var transaction = conn.BeginTransaction();

            var runId = NextRunId(conn, transaction);

            foreach (var entry in pool) UpsertPool(conn, transaction, entry);

            foreach (var group in groups)
            {
                var groupId = InsertGroup(conn, transaction, runId, target, exportDirectory, stamp, group);
                var position = 0;
                foreach (var item in group.Items.OrderBy(item => item.Position))
                {
                    InsertItem(conn, transaction, groupId, item, position++);
                }
            }

            transaction.Commit();
            _log.Write($"[library] 写入 run {runId}：{groups.Count} 组，pool {pool.Count} 条");
            return runId;
        });
    }

    /// <summary>只写 pool 台账（启动时把磁盘上已下载但没登记过的 deb 补进来）。返回写入条数。</summary>
    public int UpsertPool(IReadOnlyList<LibraryPoolEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0) return 0;

        return _database.Write(conn =>
        {
            using var transaction = conn.BeginTransaction();
            foreach (var entry in entries) UpsertPool(conn, transaction, entry);
            transaction.Commit();
            return entries.Count;
        });
    }
    /// <summary>按时间倒序读所有分组（含成员）。</summary>
    public IReadOnlyList<LibraryGroup> LoadGroups() => _database.Read(conn =>
    {
        var groups = new List<LibraryGroup>();

        using (var command = conn.CreateCommand())
        {
            command.CommandText = """
                SELECT id, run_id, root_package, root_version, target, created_at, export_dir, display_name
                FROM library_groups ORDER BY id DESC;
                """;

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                groups.Add(new LibraryGroup(
                    Id: reader.GetInt64(0),
                    RunId: reader.GetInt64(1),
                    RootPackage: reader.GetString(2),
                    RootVersion: reader.GetString(3),
                    Target: reader.GetString(4),
                    CreatedAt: reader.GetString(5),
                    ExportDir: reader.IsDBNull(6) ? null : reader.GetString(6),
                    Items: [],
                    DisplayName: reader.IsDBNull(7) ? null : reader.GetString(7)));
            }
        }

        if (groups.Count == 0) return groups;

        var byId = groups.ToDictionary(group => group.Id);
        using (var command = conn.CreateCommand())
        {
            command.CommandText = """
                SELECT group_id, package_id, version, architecture, repo, is_root, position
                FROM library_group_items ORDER BY group_id, position;
                """;

            using var reader = command.ExecuteReader();
            var items = new Dictionary<long, List<LibraryGroupItem>>();
            while (reader.Read())
            {
                var groupId = reader.GetInt64(0);
                if (!items.TryGetValue(groupId, out var list))
                {
                    list = [];
                    items[groupId] = list;
                }

                list.Add(new LibraryGroupItem(
                    PackageId: reader.GetString(1),
                    Version: reader.GetString(2),
                    Architecture: reader.GetString(3),
                    Repo: reader.GetString(4),
                    IsRoot: reader.GetInt64(5) != 0,
                    Position: reader.GetInt32(6)));
            }

            return groups
                .Select(group => byId[group.Id] with
                {
                    Items = items.TryGetValue(group.Id, out var list) ? list : [],
                })
                .ToList();
        }
    });

    /// <summary>读 pool 台账（按加入时间倒序）。</summary>
    public IReadOnlyList<LibraryPoolEntry> LoadPool() => _database.Read(conn =>
    {
        using var command = conn.CreateCommand();
        command.CommandText = """
            SELECT package_id, version, architecture, COALESCE(repo, '') , rel_path, bytes, sha256, acquired_at,
                   display_name
            FROM archive_pool ORDER BY acquired_at DESC, package_id;
            """;

        using var reader = command.ExecuteReader();
        var rows = new List<LibraryPoolEntry>();
        while (reader.Read())
        {
            rows.Add(new LibraryPoolEntry(
                PackageId: reader.GetString(0),
                Version: reader.GetString(1),
                Architecture: reader.GetString(2),
                Repo: reader.GetString(3),
                RelativePath: reader.GetString(4),
                Bytes: reader.GetInt64(5),
                Sha256: reader.IsDBNull(6) ? null : reader.GetString(6),
                AcquiredAt: reader.IsDBNull(7) ? null : reader.GetString(7),
                DisplayName: reader.IsDBNull(8) ? null : reader.GetString(8)));
        }

        return rows;
    });

    public int CountGroups() => _database.Read(conn =>
    {
        using var command = conn.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM library_groups;";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    });

    /// <summary>整组删除（级联删成员）；返回是否删到了。</summary>
    public bool DeleteGroup(long groupId) => _database.Write(conn =>
    {
        using var command = conn.CreateCommand();
        command.CommandText = "DELETE FROM library_groups WHERE id = $id;";
        command.Parameters.AddWithValue("$id", groupId);
        return command.ExecuteNonQuery() > 0;
    });

    /// <summary>
    /// 「已从资料库移除的插件」统计：`archive_pool` 里没有任何分组引用的记录。
    /// 文件数按**内容寻址**去重：多个包指向同一个 objects 文件时只算一次。
    /// </summary>
    public PoolCleanupStats CountUnreferencedPool(IReadOnlyCollection<string>? protectedPackageIds = null)
        => _database.Read(conn =>
        {
            var (rows, files) = LoadUnreferenced(conn, protectedPackageIds, out var bytes);
            return new PoolCleanupStats(rows.Count, files.Count, bytes);
        });

    /// <summary>
    /// 删除「已从资料库移除的插件」的实体文件与台账行。
    /// <para>
    /// 只删**没有被任何分组引用**的 pool 记录；文件按内容寻址共享——同一 objects 文件还被别的记录引用时保留。
    /// 文件删不掉不算失败（记在 <see cref="PoolCleanupResult.FailedFiles"/>），台账照删，下次还能再清。
    /// </para>
    /// </summary>
    public PoolCleanupResult DeleteUnreferencedPool(
        string poolDirectory,
        Diagnostics.ILogSink? log = null,
        IReadOnlyCollection<string>? protectedPackageIds = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(poolDirectory);
        var sink = log ?? Diagnostics.NullLogSink.Instance;

        var deletedRows = 0;
        var deletedFiles = 0;
        var failedFiles = 0;
        var deletedBytes = 0L;

        _database.Write(conn =>
        {
            var (rows, files) = LoadUnreferenced(conn, protectedPackageIds, out var bytes);
            if (rows.Count == 0) return 0;

            using var transaction = conn.BeginTransaction();

            foreach (var row in rows)
            {
                using var delete = conn.CreateCommand();
                delete.Transaction = transaction;
                delete.CommandText = """
                    DELETE FROM archive_pool
                    WHERE package_id = $pkg AND version = $ver AND architecture = $arch AND repo = $repo;
                    """;
                delete.Parameters.AddWithValue("$pkg", row.PackageId);
                delete.Parameters.AddWithValue("$ver", row.Version);
                delete.Parameters.AddWithValue("$arch", row.Architecture);
                delete.Parameters.AddWithValue("$repo", row.Repo);
                deletedRows += delete.ExecuteNonQuery();
            }

            transaction.Commit();

            foreach (var file in files)
            {
                var full = Path.Combine(poolDirectory, file);
                try
                {
                    if (File.Exists(full))
                    {
                        var length = new FileInfo(full).Length;
                        File.Delete(full);
                        deletedBytes += length;
                    }

                    deletedFiles++;
                }
                catch (IOException ex)
                {
                    failedFiles++;
                    sink.Write($"[pool] 删除 {full} 失败：{ex.Message}");
                }
                catch (UnauthorizedAccessException ex)
                {
                    failedFiles++;
                    sink.Write($"[pool] 删除 {full} 被拒绝：{ex.Message}");
                }
            }

            return 0;
        });

        sink.Write($"[pool] 清理已移除插件：删台账 {deletedRows} 行、文件 {deletedFiles} 个（{deletedBytes:N0} B），失败 {failedFiles}");
        return new PoolCleanupResult(deletedRows, deletedFiles, deletedBytes, failedFiles);
    }

    /// <summary>找出没被任何分组引用的 pool 记录；同时算出要删的文件（按内容去重、排除仍被引用的路径）。</summary>
    private static (List<LibraryPoolEntry> Rows, List<string> Files) LoadUnreferenced(
        SqliteConnection conn,
        IReadOnlyCollection<string>? protectedPackageIds,
        out long bytes)
    {
        using var command = conn.CreateCommand();
        command.CommandText = """
            SELECT p.package_id, p.version, p.architecture, COALESCE(p.repo, ''), p.rel_path, p.bytes,
                   p.sha256, p.acquired_at, p.display_name
            FROM archive_pool p
            WHERE NOT EXISTS (
                SELECT 1 FROM library_group_items i
                WHERE i.package_id = p.package_id
                  AND i.version = p.version
                  AND i.architecture = p.architecture
                  AND COALESCE(i.repo, '') = COALESCE(p.repo, ''))
              AND ($guard = 0 OR p.package_id NOT IN (SELECT value FROM json_each($protected)))
            ORDER BY p.package_id;
            """;

        var guard = protectedPackageIds is { Count: > 0 } ? 1 : 0;
        command.Parameters.AddWithValue("$guard", guard);
        command.Parameters.AddWithValue("$protected", guard == 0
            ? "[]"
            : System.Text.Json.JsonSerializer.Serialize(protectedPackageIds));

        var rows = new List<LibraryPoolEntry>();
        var orphanPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var entry = new LibraryPoolEntry(
                    PackageId: reader.GetString(0),
                    Version: reader.GetString(1),
                    Architecture: reader.GetString(2),
                    Repo: reader.GetString(3),
                    RelativePath: reader.GetString(4),
                    Bytes: reader.GetInt64(5),
                    Sha256: reader.IsDBNull(6) ? null : reader.GetString(6),
                    AcquiredAt: reader.IsDBNull(7) ? null : reader.GetString(7),
                    DisplayName: reader.IsDBNull(8) ? null : reader.GetString(8));
                rows.Add(entry);
                orphanPaths.Add(entry.RelativePath);
            }
        }

        // 仍被任何「非孤儿」记录引用的路径要保留（内容寻址：一个对象可能被多行共享）
        var keepPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (orphanPaths.Count > 0)
        {
            using var keep = conn.CreateCommand();
            keep.CommandText = """
                SELECT DISTINCT p.rel_path
                FROM archive_pool p
                WHERE EXISTS (
                    SELECT 1 FROM library_group_items i
                    WHERE i.package_id = p.package_id
                      AND i.version = p.version
                      AND i.architecture = p.architecture
                      AND COALESCE(i.repo, '') = COALESCE(p.repo, ''));
                """;
            using var reader = keep.ExecuteReader();
            while (reader.Read()) keepPaths.Add(reader.GetString(0));
        }

        var files = orphanPaths.Where(path => !keepPaths.Contains(path)).ToList();
        bytes = rows.Sum(row => row.Bytes);
        return (rows, files);
    }

    /// <summary>找出台账里有、磁盘上却找不到文件的 pool 行（历史迁移/误删后修复用）。</summary>
    public IReadOnlyList<LibraryPoolEntry> LoadMissingPoolFiles(string poolDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(poolDirectory);

        return LoadPool()
            .Where(entry => !File.Exists(Path.Combine(poolDirectory, entry.RelativePath)))
            .ToList();
    }

    // ── SQL 细节 ────────────────────────────────────────────────────────────

    private static long NextRunId(SqliteConnection conn, SqliteTransaction transaction)
    {
        using var command = conn.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COALESCE(MAX(run_id), 0) + 1 FROM library_groups;";
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void UpsertPool(SqliteConnection conn, SqliteTransaction transaction, LibraryPoolEntry entry)
    {
        using var command = conn.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO archive_pool(package_id, version, architecture, repo, rel_path, bytes, sha256, acquired_at, display_name)
            VALUES($pkg, $ver, $arch, $repo, $path, $bytes, $sha, $at, $display)
            ON CONFLICT(package_id, version, architecture, repo) DO UPDATE SET
                repo = excluded.repo,
                rel_path = excluded.rel_path,
                bytes = excluded.bytes,
                sha256 = COALESCE(excluded.sha256, archive_pool.sha256),
                acquired_at = excluded.acquired_at,
                display_name = COALESCE(excluded.display_name, archive_pool.display_name);
            """;
        command.Parameters.AddWithValue("$pkg", entry.PackageId);
        command.Parameters.AddWithValue("$ver", entry.Version);
        command.Parameters.AddWithValue("$arch", entry.Architecture);
        command.Parameters.AddWithValue("$repo", entry.Repo);
        command.Parameters.AddWithValue("$path", entry.RelativePath);
        command.Parameters.AddWithValue("$bytes", entry.Bytes);
        command.Parameters.AddWithValue("$sha", (object?)entry.Sha256 ?? DBNull.Value);
        command.Parameters.AddWithValue("$at", (object?)entry.AcquiredAt ?? DBNull.Value);
        command.Parameters.AddWithValue("$display", (object?)entry.DisplayName ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static long InsertGroup(
        SqliteConnection conn,
        SqliteTransaction transaction,
        long runId,
        string target,
        string? exportDirectory,
        string stamp,
        LibraryGroupDraft draft)
    {
        using var command = conn.CreateCommand();
        command.Transaction = transaction;
        // RETURNING id 才能区分「刚插入」与「冲突后什么也没做」；
        // last_insert_rowid 在 DO NOTHING 时不会归零，会返回本连接上一次的 rowid。
        command.CommandText = """
            INSERT INTO library_groups(run_id, root_package, root_version, target, created_at, export_dir, display_name)
            VALUES($run, $root, $rootVer, $target, $created, $dir, $display)
            ON CONFLICT(run_id, root_package, root_version) DO NOTHING
            RETURNING id;
            """;
        command.Parameters.AddWithValue("$run", runId);
        command.Parameters.AddWithValue("$root", draft.RootPackage);
        command.Parameters.AddWithValue("$rootVer", draft.RootVersion);
        command.Parameters.AddWithValue("$target", target);
        command.Parameters.AddWithValue("$created", stamp);
        command.Parameters.AddWithValue("$dir", (object?)exportDirectory ?? DBNull.Value);
        command.Parameters.AddWithValue("$display", (object?)draft.DisplayName ?? DBNull.Value);

        var scalar = command.ExecuteScalar();
        if (scalar is not null and not DBNull)
        {
            return Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
        }

        // 冲突（同一 run 里重复的根）：取回既有 id
        using var select = conn.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = """
            SELECT id FROM library_groups
            WHERE run_id = $run AND root_package = $root AND root_version = $rootVer;
            """;
        select.Parameters.AddWithValue("$run", runId);
        select.Parameters.AddWithValue("$root", draft.RootPackage);
        select.Parameters.AddWithValue("$rootVer", draft.RootVersion);
        return Convert.ToInt64(select.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void InsertItem(
        SqliteConnection conn,
        SqliteTransaction transaction,
        long groupId,
        LibraryGroupItem item,
        int position)
    {
        using var command = conn.CreateCommand();
        command.Transaction = transaction;
        // 组是新建的，成员不会重复；REPLACE 只为防御（同包同版本同架构只有一行）
        command.CommandText = """
            INSERT OR REPLACE INTO library_group_items
                (group_id, package_id, version, architecture, repo, is_root, position)
            VALUES($group, $pkg, $ver, $arch, $repo, $root, $pos);
            """;
        command.Parameters.AddWithValue("$group", groupId);
        command.Parameters.AddWithValue("$pkg", item.PackageId);
        command.Parameters.AddWithValue("$ver", item.Version);
        command.Parameters.AddWithValue("$arch", item.Architecture);
        command.Parameters.AddWithValue("$repo", item.Repo);
        command.Parameters.AddWithValue("$root", item.IsRoot ? 1 : 0);
        command.Parameters.AddWithValue("$pos", position);
        command.ExecuteNonQuery();
    }
}







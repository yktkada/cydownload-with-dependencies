using System.Text.Json;
using System.Diagnostics;
using System.Globalization;
using CyDownload.Core.Deb;
using CyDownload.Core.Diagnostics;
using CyDownload.Core.Repos;
using Microsoft.Data.Sqlite;

namespace CyDownload.Core.Index;

/// <summary>一次导入的结果。</summary>
public sealed record ImportResult(
    string Repo,
    int Inserted,
    int Updated,
    int Removed,
    int Skipped,
    int Provides,
    int TotalAfterImport,
    TimeSpan Elapsed)
{
    public int Considered => Inserted + Updated;
}

/// <summary>
/// `packages` / `provides` / `repos` 的写入与查询。
/// <para>
/// **一次导入 = 一个事务**：写一半崩溃不会留下半个源的索引；重跑同一次导入是幂等的
/// （`UNIQUE(package_id, version, architecture, repo)` + `INSERT OR REPLACE`）。
/// </para>
/// </summary>
public sealed class PackageRepository
{
    /// <summary>导入互斥的等锁上限。同一库同时只允许一轮导入（否则两轮互相看不见对方刚插的行）。</summary>
    private const int ImportGateWaitMs = 60_000;

    /// <summary>源没有登记 priority 时的兜底（数值越大越靠后）。</summary>
    private const int DefaultRepoPriority = 100;

    private readonly ArchiveDatabase _database;
    private readonly ILogSink _log;

    public PackageRepository(ArchiveDatabase database, ILogSink? log = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
        _log = log ?? NullLogSink.Instance;
    }

    /// <summary>
    /// 把一个源的索引 stanza 批量导入。只收 <paramref name="architecture"/> 的行（其它架构计入 skipped）。
    /// </summary>
    public ImportResult Import(
        RepoDescriptor repo,
        IReadOnlyList<ControlStanza> stanzas,
        string architecture,
        DateTimeOffset fetchedAt)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(stanzas);
        ArgumentException.ThrowIfNullOrWhiteSpace(architecture);

        if (!_database.ImportGate.Wait(ImportGateWaitMs))
        {
            throw new TimeoutException($"Waited {ImportGateWaitMs} ms for the import gate of {_database.DatabasePath}.");
        }

        try
        {
            var stopwatch = Stopwatch.StartNew();
            var result = _database.Write(conn =>
            {
                using var transaction = conn.BeginTransaction();

                UpsertRepo(conn, transaction, repo, fetchedAt);

                // 已有行的键（用于区分 inserted / updated）
                var existing = LoadKeys(conn, transaction, repo.Name);

                var rows = new List<PackageRow>(stanzas.Count);
                var badRows = 0;
                var otherArch = 0;

                foreach (var stanza in stanzas)
                {
                    if (!PackageRow.TryFromStanza(stanza, repo.Name, out var row))
                    {
                        badRows++;
                        continue;
                    }

                    if (!string.Equals(row.Architecture, architecture, StringComparison.OrdinalIgnoreCase))
                    {
                        otherArch++;
                        continue;
                    }

                    rows.Add(row);
                }

                var (inserted, updated) = UpsertPackages(conn, transaction, rows, existing);

                // 上游下架的包必须清掉：bigboss 每日更新，老版本下架是常态；
                // 留着会让求解器选中一条下载必 404 的行。**只清本次导入的那个架构**，
                // 免得将来同一源导入多个架构时把别的架构删了。
                // 本轮 0 行时**不能**清空该源该架构的历史行（索引整体缺 Architecture/架构值变了会误删，
                // 源已关站时不可恢复）——
                var removed = rows.Count == 0
                    ? 0
                    : DeleteMissing(
                        conn, transaction, repo.Name, architecture,
                        rows.Select(row => Key(row.PackageId, row.Version, row.Architecture)).ToHashSet(StringComparer.Ordinal));

                var provides = RebuildProvides(conn, transaction, repo.Name, rows);
                var total = CountPackages(conn, transaction, repo.Name);

                UpdateRepoStats(conn, transaction, repo.Name, total, fetchedAt);

                transaction.Commit();
                return new ImportResult(
                    repo.Name, inserted, updated, removed, badRows + otherArch, provides, total, stopwatch.Elapsed);
            });

            _log.Write(
                $"[{repo.Name}] 落库完成：新增 {result.Inserted}，更新 {result.Updated}，清理下架 {result.Removed}，" +
                $"跳过 {result.Skipped}（坏行/异架构），provides {result.Provides} 条，" +
                $"现有 {result.TotalAfterImport} 行，{result.Elapsed.TotalMilliseconds:0} ms");

            return result;
        }
        finally
        {
            _database.ImportGate.Release();
        }
    }

    /// <summary>库里的包行数（可按源过滤）。</summary>
    public int CountPackages(string? repo = null) => _database.Read(conn =>
    {
        using var command = conn.CreateCommand();
        command.CommandText = repo is null
            ? "SELECT COUNT(*) FROM packages;"
            : "SELECT COUNT(*) FROM packages WHERE repo = $repo;";
        if (repo is not null) command.Parameters.AddWithValue("$repo", repo);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    });

    /// <summary>读**所有源**的包（合并页签用），按包名 + 源排序，保证结果可复现。</summary>
    public IReadOnlyList<PackageRow> LoadAll() => _database.Read(conn =>
    {
        using var command = conn.CreateCommand();
        command.CommandText = SelectColumns + " FROM packages p ORDER BY p.package_id, p.version, p.repo;";

        using var reader = command.ExecuteReader();
        var rows = new List<PackageRow>();
        while (reader.Read()) rows.Add(ReadRow(reader));
        return rows;
    });
    /// <summary>按唯一键取一行（不存在返回 null）。</summary>
    public PackageRow? Find(string packageId, string version, string architecture, string repo)
        => _database.Read(conn =>
        {
            using var command = conn.CreateCommand();
            command.CommandText = SelectColumns + """
                 FROM packages p
                 WHERE p.package_id = $pid AND p.version = $ver AND p.architecture = $arch AND p.repo = $repo;
                """;
            command.Parameters.AddWithValue("$pid", packageId);
            command.Parameters.AddWithValue("$ver", version);
            command.Parameters.AddWithValue("$arch", architecture);
            command.Parameters.AddWithValue("$repo", repo);

            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadRow(reader) : null;
        });

    /// <summary>读某个源的全部行（调试 / 测试 / 后续求解器用）。</summary>
    public IReadOnlyList<PackageRow> LoadAll(string repo)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repo);

        return _database.Read(conn =>
        {
            using var command = conn.CreateCommand();
            command.CommandText = SelectColumns + " FROM packages p WHERE p.repo = $repo ORDER BY p.package_id, p.version;";
            command.Parameters.AddWithValue("$repo", repo);

            using var reader = command.ExecuteReader();
            var rows = new List<PackageRow>();
            while (reader.Read()) rows.Add(ReadRow(reader));
            return rows;
        });
    }

    /// <summary>`provides` 反查：哪些行提供了这个名字（可为空的列表）。</summary>
    public IReadOnlyList<PackageRow> FindByProvidedName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return _database.Read(conn =>
        {
            using var command = conn.CreateCommand();
            command.CommandText = SelectColumns + """
                 FROM packages p
                 JOIN provides v ON v.package_row = p.id
                 WHERE v.name = $name
                 ORDER BY p.repo, p.package_id, p.version;
                """;
            command.Parameters.AddWithValue("$name", name);

            using var reader = command.ExecuteReader();
            var rows = new List<PackageRow>();
            while (reader.Read()) rows.Add(ReadRow(reader));
            return rows;
        });
    }

    /// <summary>
    /// `package_id` 命中的候选（带源优先级；同名同版本在多源时靠它排序）。
    /// <paramref name="architectures"/> 非空时按架构过滤（：目标 arches 必须接入）。
    /// </summary>
    public IReadOnlyList<PackageCandidate> CandidatesByName(
        string packageId,
        IReadOnlyCollection<string>? architectures = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);

        return _database.Read(conn => ReadCandidates(conn, packageId, architectures, providedName: null));
    }

    /// <summary>由 `provides` 命中该名字的候选（虚拟包）。</summary>
    public IReadOnlyList<PackageCandidate> CandidatesByProvidedName(
        string name,
        IReadOnlyCollection<string>? architectures = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return _database.Read(conn => ReadCandidates(conn, name, architectures, providedName: name));
    }

    private static List<PackageCandidate> ReadCandidates(
        SqliteConnection conn,
        string name,
        IReadOnlyCollection<string>? architectures,
        string? providedName)
    {
        using var command = conn.CreateCommand();

        var where = providedName is null
            ? "WHERE p.package_id = $name"
            : "JOIN provides v ON v.package_row = p.id WHERE v.name = $name";

        var archClause = "";
        if (architectures is { Count: > 0 })
        {
            var names = new List<string>(architectures.Count);
            var index = 0;
            foreach (var architecture in architectures)
            {
                var parameter = "$arch" + index++;
                command.Parameters.AddWithValue(parameter, architecture);
                names.Add(parameter);
            }

            archClause = " AND p.architecture IN (" + string.Join(", ", names) + ")";
        }

        command.CommandText = SelectColumns + $"""
            , COALESCE(r.priority, {DefaultRepoPriority}) AS repo_priority
            FROM packages p
            LEFT JOIN repos r ON r.name = p.repo
            {where}{archClause};
            """;
        command.Parameters.AddWithValue("$name", name);

        using var reader = command.ExecuteReader();
        var candidates = new List<PackageCandidate>();
        while (reader.Read())
        {
            candidates.Add(new PackageCandidate(ReadRow(reader), reader.GetInt32(22)));
        }

        return candidates;
    }
    /// <summary>
    /// 新增/修改一个源（源管理用）。<paramref name="originalName"/> 非空且不同名 = 改名。
    /// <para>
    /// 改名**不删包行**：`packages.repo` 是 `repos(name)` 的外键（`foreign_keys=ON`），
    /// 直接删旧名会 `FOREIGN KEY constraint failed`（全量）。
    /// 做法是在同一事务里：插/更新新名字的源 → `UPDATE packages SET repo = 新名 WHERE repo = 旧名` → 删旧名字的源。
    /// </para>
    /// <para>只改地址/优先级时不动包行（重新抓取即可）。**不写 `last_fetch`**。</para>
    /// </summary>
    public void SaveRepo(RepoDescriptor repo, string? originalName = null)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentException.ThrowIfNullOrWhiteSpace(repo.Name);

        var rename = !string.IsNullOrWhiteSpace(originalName) &&
                     !string.Equals(originalName, repo.Name, StringComparison.Ordinal);

        _database.Write(conn =>
        {
            using var transaction = conn.BeginTransaction();

            UpsertRepo(conn, transaction, repo, fetchedAt: null);

            if (rename)
            {
                using var move = conn.CreateCommand();
                move.Transaction = transaction;
                move.CommandText = "UPDATE packages SET repo = $new WHERE repo = $old;";
                move.Parameters.AddWithValue("$new", repo.Name);
                move.Parameters.AddWithValue("$old", originalName);
                move.ExecuteNonQuery();

                // 台账里的 repo 不是外键，但必须一起改，否则改名后 pool 缓存命中不了
                using var movePool = conn.CreateCommand();
                movePool.Transaction = transaction;
                movePool.CommandText = "UPDATE archive_pool SET repo = $new WHERE repo = $old;";
                movePool.Parameters.AddWithValue("$new", repo.Name);
                movePool.Parameters.AddWithValue("$old", originalName);
                movePool.ExecuteNonQuery();

                using var moveItems = conn.CreateCommand();
                moveItems.Transaction = transaction;
                moveItems.CommandText = "UPDATE library_group_items SET repo = $new WHERE repo = $old;";
                moveItems.Parameters.AddWithValue("$new", repo.Name);
                moveItems.Parameters.AddWithValue("$old", originalName);
                moveItems.ExecuteNonQuery();

                using var drop = conn.CreateCommand();
                drop.Transaction = transaction;
                drop.CommandText = "DELETE FROM repos WHERE name = $old;";
                drop.Parameters.AddWithValue("$old", originalName);
                drop.ExecuteNonQuery();
            }

            transaction.Commit();
            return 0;
        });
    }

    /// <summary>
    /// 删除一个源：**连同它已入库的包行一起删**（`provides` 靠外键级联），返回删掉的包行数。
    /// <para>
    /// 为什么不"只删源定义、留包行"：`packages.repo` 是外键，不先删包会直接
    /// `FOREIGN KEY constraint failed`（全量）。界面在调用前会提示将删多少行
    /// （<see cref="CountPackages(string)"/>），用户确认后才到这里——符合"删除先给清单"的口径。
    /// </para>
    /// </summary>
    public int DeleteRepo(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return _database.Write(conn =>
        {
            using var transaction = conn.BeginTransaction();

            int removed;
            using (var packages = conn.CreateCommand())
            {
                packages.Transaction = transaction;
                packages.CommandText = "DELETE FROM packages WHERE repo = $name;";
                packages.Parameters.AddWithValue("$name", name);
                removed = packages.ExecuteNonQuery();
            }

            using (var repo = conn.CreateCommand())
            {
                repo.Transaction = transaction;
                repo.CommandText = "DELETE FROM repos WHERE name = $name;";
                repo.Parameters.AddWithValue("$name", name);
                repo.ExecuteNonQuery();
            }

            transaction.Commit();
            return removed;
        });
    }

    /// <summary>库里有没有源（首次启动据此决定是否写默认三源；只在"从没初始化过"时用）。</summary>
    public bool HasAnyRepo() => _database.Read(conn =>
    {
        using var command = conn.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM repos);";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
    });
    /// <summary>读所有源（按 priority 升序，界面页签顺序就用它）。</summary>
    public IReadOnlyList<RepoRow> LoadRepos() => _database.Read(conn =>
    {
        using var command = conn.CreateCommand();
        command.CommandText = """
            SELECT name, base_url, layout, suite, component, priority, headers_json, last_fetch, pkg_count, enabled
            FROM repos ORDER BY priority, name;
            """;

        using var reader = command.ExecuteReader();
        var rows = new List<RepoRow>();
        while (reader.Read())
        {
            rows.Add(new RepoRow(
                Name: reader.GetString(0),
                BaseUrl: reader.GetString(1),
                Layout: reader.GetString(2),
                Suite: reader.IsDBNull(3) ? null : reader.GetString(3),
                Component: reader.IsDBNull(4) ? null : reader.GetString(4),
                Priority: reader.GetInt32(5),
                HeadersJson: reader.IsDBNull(6) ? null : reader.GetString(6),
                LastFetch: reader.IsDBNull(7) ? null : reader.GetString(7),
                PackageCount: reader.IsDBNull(8) ? null : reader.GetInt32(8),
                Enabled: reader.IsDBNull(9) || reader.GetInt32(9) != 0));
        }

        return rows;
    });
    /// <summary>
    /// 回填 `min_ios`（v6 迁移前的老行是 NULL）。只读一次 depends、只写有变化的行。
    /// 返回更新的行数。
    /// </summary>
    public int BackfillMinIos(ILogSink? log = null)
    {
        var sink = log ?? NullLogSink.Instance;

        var pending = _database.Read(conn =>
        {
            using var command = conn.CreateCommand();
            command.CommandText = "SELECT id, depends FROM packages WHERE min_ios IS NULL AND depends IS NOT NULL;";
            using var reader = command.ExecuteReader();

            var rows = new List<(long Id, string? MinIos)>();
            while (reader.Read())
            {
                // 解析不出也要写 ''（表示"已尝试、无约束"），否则每次启动都会重扫一遍
                rows.Add((reader.GetInt64(0), MinIosParser.Parse(reader.IsDBNull(1) ? null : reader.GetString(1)) ?? ""));
            }

            return rows;
        });

        if (pending.Count == 0) return 0;

        var updated = _database.Write(conn =>
        {
            using var transaction = conn.BeginTransaction();
            using var command = conn.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE packages SET min_ios = $ios WHERE id = $id;";
            var ios = command.Parameters.Add("$ios", SqliteType.Text);
            var id = command.Parameters.Add("$id", SqliteType.Integer);

            var count = 0;
            foreach (var (rowId, minIos) in pending)
            {
                ios.Value = minIos;
                id.Value = rowId;
                count += command.ExecuteNonQuery();
            }

            transaction.Commit();
            return count;
        });

        if (updated > 0) sink.Write($"[index] 回填 min_ios {updated} 行");
        return updated;
    }

    // ── SQL 细节 ────────────────────────────────────────────────────────────

    // 列名一律带 p. 前缀：FindByProvidedName 要 JOIN provides（那张表也有 name 列，不限定会报 ambiguous）
    private const string SelectColumns = """
        SELECT p.id, p.package_id, p.name, p.version, p.architecture, p.section, p.maintainer,
               p.author, p.website, p.depiction, p.description, p.depends, p.predepends, p.provides,
               p.tags, p.filename, p.size, p.installed_size, p.md5, p.sha256, p.repo, p.min_ios
        """;

    /// <param name="fetchedAt">null = 不更新 `last_fetch`（编辑源不该显示「刚抓过」，）。</param>
    private static void UpsertRepo(
        SqliteConnection conn,
        SqliteTransaction transaction,
        RepoDescriptor repo,
        DateTimeOffset? fetchedAt)
    {
        using var command = conn.CreateCommand();
        command.Transaction = transaction;
        // 每源伪造头也落库：F7 源设置页要显示"当前生效的头"
        var headersJson = repo.HeaderOverrides is { Count: > 0 }
            ? JsonSerializer.Serialize(repo.HeaderOverrides)
            : null;

        command.CommandText = """
            INSERT INTO repos(name, base_url, layout, suite, component, priority, headers_json, last_fetch, enabled)
            VALUES($name, $base, $layout, $suite, $component, $priority, $headersJson, $lastFetch, $enabled)
            ON CONFLICT(name) DO UPDATE SET
                base_url = excluded.base_url,
                layout = excluded.layout,
                suite = excluded.suite,
                component = excluded.component,
                priority = excluded.priority,
                headers_json = excluded.headers_json,
                last_fetch = COALESCE(excluded.last_fetch, repos.last_fetch),
                enabled = excluded.enabled;
            """;
        command.Parameters.AddWithValue("$name", repo.Name);
        command.Parameters.AddWithValue("$headersJson", (object?)headersJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$base", repo.BaseUrl.TrimEnd('/'));
        command.Parameters.AddWithValue("$layout", repo.Layout == RepoLayout.Dists ? "dists" : "flat");
        command.Parameters.AddWithValue("$suite", (object?)repo.Suite ?? DBNull.Value);
        command.Parameters.AddWithValue("$component", (object?)repo.Component ?? DBNull.Value);
        command.Parameters.AddWithValue("$priority", repo.Priority);
        command.Parameters.AddWithValue("$enabled", repo.Enabled ? 1 : 0);
        command.Parameters.AddWithValue(
            "$lastFetch",
            fetchedAt is { } stamp
                ? stamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
                : (object)DBNull.Value);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// 删掉本轮索引里**已不存在**的行。只清 `repo + architecture` 这一格，
    /// 不影响同一源其它架构的行，也不会动别的源。`provides` 靠外键级联跟着删。
    /// </summary>
    private static int DeleteMissing(
        SqliteConnection conn,
        SqliteTransaction transaction,
        string repo,
        string architecture,
        HashSet<string> seenKeys)
    {
        var stale = new List<long>();

        using (var select = conn.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText =
                "SELECT id, package_id, version FROM packages WHERE repo = $repo AND architecture = $arch;";
            select.Parameters.AddWithValue("$repo", repo);
            select.Parameters.AddWithValue("$arch", architecture);

            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                if (!seenKeys.Contains(Key(reader.GetString(1), reader.GetString(2), architecture)))
                {
                    stale.Add(reader.GetInt64(0));
                }
            }
        }

        if (stale.Count == 0) return 0;

        var removed = 0;
        foreach (var chunk in stale.Chunk(400))
        {
            using var delete = conn.CreateCommand();
            delete.Transaction = transaction;

            var names = new string[chunk.Length];
            for (var i = 0; i < chunk.Length; i++) names[i] = "$id" + i;

            delete.CommandText = "DELETE FROM packages WHERE id IN (" + string.Join(", ", names) + ");";
            for (var i = 0; i < chunk.Length; i++) delete.Parameters.AddWithValue(names[i], chunk[i]);
            removed += delete.ExecuteNonQuery();
        }

        return removed;
    }
    private static HashSet<string> LoadKeys(SqliteConnection conn, SqliteTransaction transaction, string repo)
    {
        using var command = conn.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT package_id, version, architecture FROM packages WHERE repo = $repo;";
        command.Parameters.AddWithValue("$repo", repo);

        using var reader = command.ExecuteReader();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read()) keys.Add(Key(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return keys;
    }

    private static (int Inserted, int Updated) UpsertPackages(
        SqliteConnection conn,
        SqliteTransaction transaction,
        IReadOnlyList<PackageRow> rows,
        HashSet<string> existing)
    {
        using var command = conn.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR REPLACE INTO packages(
                package_id, name, version, architecture, section, maintainer, author, website,
                depiction, description, depends, predepends, provides, tags, filename, size,
                installed_size, md5, sha256, repo, min_ios)
            VALUES($pid, $name, $ver, $arch, $section, $maintainer, $author, $website,
                   $depiction, $description, $depends, $predepends, $provides, $tags, $filename, $size,
                   $installedSize, $md5, $sha256, $repo, $minIos);
            """;

        var parameters = new (string Name, SqliteType Type)[]
        {
            ("$pid", SqliteType.Text), ("$name", SqliteType.Text), ("$ver", SqliteType.Text),
            ("$arch", SqliteType.Text), ("$section", SqliteType.Text), ("$maintainer", SqliteType.Text),
            ("$author", SqliteType.Text), ("$website", SqliteType.Text), ("$depiction", SqliteType.Text),
            ("$description", SqliteType.Text), ("$depends", SqliteType.Text), ("$predepends", SqliteType.Text),
            ("$provides", SqliteType.Text), ("$tags", SqliteType.Text), ("$filename", SqliteType.Text),
            ("$size", SqliteType.Integer), ("$installedSize", SqliteType.Integer), ("$md5", SqliteType.Text),
            ("$sha256", SqliteType.Text), ("$repo", SqliteType.Text), ("$minIos", SqliteType.Text),
        };

        var bindings = new SqliteParameter[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
        {
            bindings[i] = command.Parameters.Add(parameters[i].Name, parameters[i].Type);
        }

        var inserted = 0;
        var updated = 0;

        foreach (var row in rows)
        {
            bindings[0].Value = row.PackageId;
            bindings[1].Value = (object?)row.Name ?? DBNull.Value;
            bindings[2].Value = row.Version;
            bindings[3].Value = row.Architecture;
            bindings[4].Value = (object?)row.Section ?? DBNull.Value;
            bindings[5].Value = (object?)row.Maintainer ?? DBNull.Value;
            bindings[6].Value = (object?)row.Author ?? DBNull.Value;
            bindings[7].Value = (object?)row.Website ?? DBNull.Value;
            bindings[8].Value = (object?)row.Depiction ?? DBNull.Value;
            bindings[9].Value = (object?)row.Description ?? DBNull.Value;
            bindings[10].Value = (object?)row.Depends ?? DBNull.Value;
            bindings[11].Value = (object?)row.PreDepends ?? DBNull.Value;
            bindings[12].Value = (object?)row.Provides ?? DBNull.Value;
            bindings[13].Value = (object?)row.Tags ?? DBNull.Value;
            bindings[14].Value = (object?)row.Filename ?? DBNull.Value;
            bindings[15].Value = (object?)row.Size ?? DBNull.Value;
            bindings[16].Value = (object?)row.InstalledSize ?? DBNull.Value;
            bindings[17].Value = (object?)row.Md5 ?? DBNull.Value;
            bindings[18].Value = (object?)row.Sha256 ?? DBNull.Value;
            bindings[19].Value = row.Repo;
            bindings[20].Value = (object?)row.MinIos ?? DBNull.Value;

            command.ExecuteNonQuery();

            if (existing.Contains(Key(row.PackageId, row.Version, row.Architecture))) updated++;
            else inserted++;
        }

        return (inserted, updated);
    }

    /// <summary>
    /// 重建该源的 `provides` 表：先按源删干净再插。
    /// 之所以整体重建而不是增量：`INSERT OR REPLACE` 会换掉 rowid，增量维护会留下悬空行。
    /// </summary>
    private static int RebuildProvides(
        SqliteConnection conn,
        SqliteTransaction transaction,
        string repo,
        IReadOnlyList<PackageRow> rows)
    {
        using (var delete = conn.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText =
                "DELETE FROM provides WHERE package_row IN (SELECT id FROM packages WHERE repo = $repo);";
            delete.Parameters.AddWithValue("$repo", repo);
            delete.ExecuteNonQuery();
        }

        // package_id/version/architecture → 行 id
        var ids = new Dictionary<string, long>(StringComparer.Ordinal);
        using (var select = conn.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT id, package_id, version, architecture FROM packages WHERE repo = $repo;";
            select.Parameters.AddWithValue("$repo", repo);
            using var reader = select.ExecuteReader();
            while (reader.Read()) ids[Key(reader.GetString(1), reader.GetString(2), reader.GetString(3))] = reader.GetInt64(0);
        }

        using var insert = conn.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT OR IGNORE INTO provides(name, package_row, version) VALUES($name, $row, $version);";
        var nameParameter = insert.Parameters.Add("$name", SqliteType.Text);
        var rowParameter = insert.Parameters.Add("$row", SqliteType.Integer);
        var versionParameter = insert.Parameters.Add("$version", SqliteType.Text);

        var count = 0;
        foreach (var row in rows)
        {
            var provided = row.ProvidedNames();
            if (provided.Count == 0) continue;
            if (!ids.TryGetValue(Key(row.PackageId, row.Version, row.Architecture), out var id)) continue;

            foreach (var item in provided)
            {
                nameParameter.Value = item.Name;
                rowParameter.Value = id;
                versionParameter.Value = item.Version?.ToString() ?? (object)DBNull.Value;
                count += insert.ExecuteNonQuery();
            }
        }

        return count;
    }

    private static int CountPackages(SqliteConnection conn, SqliteTransaction transaction, string repo)
    {
        using var command = conn.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM packages WHERE repo = $repo;";
        command.Parameters.AddWithValue("$repo", repo);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void UpdateRepoStats(
        SqliteConnection conn,
        SqliteTransaction transaction,
        string repo,
        int total,
        DateTimeOffset fetchedAt)
    {
        using var command = conn.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE repos SET pkg_count = $count, last_fetch = $at WHERE name = $repo;";
        command.Parameters.AddWithValue("$count", total);
        command.Parameters.AddWithValue("$at", fetchedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$repo", repo);
        command.ExecuteNonQuery();
    }

    private static string Key(string packageId, string version, string architecture)
        => packageId + "\u0000" + version + "\u0000" + architecture;

    private static PackageRow ReadRow(SqliteDataReader reader) => new(
        Id: reader.GetInt64(0),
        PackageId: reader.GetString(1),
        Name: reader.IsDBNull(2) ? null : reader.GetString(2),
        Version: reader.GetString(3),
        Architecture: reader.GetString(4),
        Section: reader.IsDBNull(5) ? null : reader.GetString(5),
        Maintainer: reader.IsDBNull(6) ? null : reader.GetString(6),
        Author: reader.IsDBNull(7) ? null : reader.GetString(7),
        Website: reader.IsDBNull(8) ? null : reader.GetString(8),
        Depiction: reader.IsDBNull(9) ? null : reader.GetString(9),
        Description: reader.IsDBNull(10) ? null : reader.GetString(10),
        Depends: reader.IsDBNull(11) ? null : reader.GetString(11),
        PreDepends: reader.IsDBNull(12) ? null : reader.GetString(12),
        Provides: reader.IsDBNull(13) ? null : reader.GetString(13),
        Tags: reader.IsDBNull(14) ? null : reader.GetString(14),
        Filename: reader.IsDBNull(15) ? null : reader.GetString(15),
        Size: reader.IsDBNull(16) ? null : reader.GetInt64(16),
        InstalledSize: reader.IsDBNull(17) ? null : reader.GetInt64(17),
        Md5: reader.IsDBNull(18) ? null : reader.GetString(18),
        Sha256: reader.IsDBNull(19) ? null : reader.GetString(19),
        Repo: reader.GetString(20),
        MinIos: reader.IsDBNull(21) ? null : reader.GetString(21));
}












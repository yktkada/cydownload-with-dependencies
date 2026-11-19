using Microsoft.Data.Sqlite;

namespace CyDownload.Core.Index;

/// <summary>
/// 存档库 schema。v1 建全 7 张表；**v2 加资料库分组**（`library_groups` / `library_group_items`，
/// 一次导出 = 一个 run，每个根插件一组，组内是它的整个依赖闭包——行为抄 `iDeviceInstaller` 的资料库分组）。
/// <para>
/// 与 `iDeviceInstaller` 的 <c>SchemaSql</c> 同一形态：只在 <c>user_version == 0</c> 时整份执行。
/// </para>
/// </summary>
internal static class SchemaSql
{
    internal const int CurrentVersion = 6;

    internal static void CreateV1Schema(SqliteConnection connection)
    {
        // 建表必须在一个事务里：中途崩溃会留下「部分表 + user_version=0」，
        // 下次 Open 会重新 CREATE TABLE 直接失败，只能手删库。
        using var transaction = connection.BeginTransaction();

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = V1;
        command.ExecuteNonQuery();

        command.CommandText = "PRAGMA user_version=1;";   // v1 建完只标 1，迁移由 Open 逐级做
        command.ExecuteNonQuery();

        transaction.Commit();
    }

    /// <summary>`packages` 20 列（原项目 16 字段 + predepends / provides / repo / sha256）。</summary>
    private const string V1 = """
        CREATE TABLE repos (
          name          TEXT PRIMARY KEY,
          base_url      TEXT NOT NULL,
          layout        TEXT NOT NULL,
          suite         TEXT,
          component     TEXT,
          priority      INTEGER NOT NULL DEFAULT 0,
          headers_json  TEXT,
          last_fetch    TEXT,
          pkg_count     INTEGER
        );

        CREATE TABLE packages (
          id             INTEGER PRIMARY KEY,
          package_id     TEXT NOT NULL,
          name           TEXT,
          version        TEXT NOT NULL,
          architecture   TEXT NOT NULL,
          section        TEXT,
          maintainer     TEXT,
          author         TEXT,
          website        TEXT,
          depiction      TEXT,
          description    TEXT,
          depends        TEXT,
          predepends     TEXT,
          provides       TEXT,
          tags           TEXT,
          filename       TEXT,
          size           INTEGER,
          installed_size INTEGER,
          md5            TEXT,
          sha256         TEXT,
          repo           TEXT NOT NULL REFERENCES repos(name),
          UNIQUE(package_id, version, architecture, repo)
        );

        -- UNIQUE(package_id, …) 的最左前缀已覆盖「按包名查」，不再单独建 IX_packages_pkgid
        CREATE INDEX IX_packages_name  ON packages(name);

        CREATE TABLE provides (
          name        TEXT NOT NULL,
          package_row INTEGER NOT NULL REFERENCES packages(id) ON DELETE CASCADE,
          version     TEXT,
          PRIMARY KEY(name, package_row)
        );
        -- PRIMARY KEY(name, …) 的最左前缀已覆盖「按名字反查」，不再单独建 IX_provides_name

        CREATE TABLE targets (
          name           TEXT PRIMARY KEY,
          product_type   TEXT NOT NULL,
          ios_version    TEXT NOT NULL,
          build_version  TEXT,
          cf_version     TEXT,
          cpu_model      TEXT,
          kernel_version TEXT,
          arches         TEXT NOT NULL
        );

        CREATE TABLE base_packages (
          target   TEXT NOT NULL REFERENCES targets(name),
          name     TEXT NOT NULL,
          version  TEXT NOT NULL,
          kind     TEXT NOT NULL,
          provides TEXT,
          PRIMARY KEY(target, name)
        );

        CREATE TABLE archive_pool (
          package_id   TEXT NOT NULL,
          version      TEXT NOT NULL,
          architecture TEXT NOT NULL,
          rel_path     TEXT NOT NULL,
          bytes        INTEGER NOT NULL,
          sha256       TEXT,
          acquired_at  TEXT,
          PRIMARY KEY(package_id, version, architecture)
        );

        CREATE TABLE closure_runs (
          id         INTEGER PRIMARY KEY,
          target     TEXT NOT NULL,
          package_id TEXT NOT NULL,
          created_at TEXT NOT NULL,
          summary    TEXT
        );
        """;

    /// <summary>
    /// v1 → v2：**只加表**，不动既有数据（幂等，可重复执行）。
    /// <para>
    /// 为什么把分组落库而不是每次从依赖图重算：一次导出时同时知道「用户选的是哪几个插件」和
    /// 「实际下到了什么」；重算需要重新求解（源更新后可能得出不同结果），库里的记录才是当时的事实。
    /// </para>
    /// </summary>
    internal static void MigrateToV2(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS library_groups (
                  id           INTEGER PRIMARY KEY,
                  run_id       INTEGER NOT NULL,
                  root_package TEXT NOT NULL,
                  root_version TEXT NOT NULL,
                  target       TEXT NOT NULL,
                  created_at   TEXT NOT NULL,
                  export_dir   TEXT,
                  UNIQUE(run_id, root_package, root_version)
                );

                CREATE TABLE IF NOT EXISTS library_group_items (
                  group_id     INTEGER NOT NULL REFERENCES library_groups(id) ON DELETE CASCADE,
                  package_id   TEXT NOT NULL,
                  version      TEXT NOT NULL,
                  architecture TEXT NOT NULL,
                  repo         TEXT NOT NULL,
                  is_root      INTEGER NOT NULL,
                  position     INTEGER NOT NULL,
                  PRIMARY KEY(group_id, package_id, version, architecture)
                );

                CREATE INDEX IF NOT EXISTS IX_library_group_items_pkg
                  ON library_group_items(package_id, version);

                CREATE INDEX IF NOT EXISTS IX_library_groups_run ON library_groups(run_id);
                """;
            command.ExecuteNonQuery();
        }

        using (var version = connection.CreateCommand())
        {
            version.Transaction = transaction;
            version.CommandText = "PRAGMA user_version = 2;";
            version.ExecuteNonQuery();
        }

        // v1 的 archive_pool 没有 repo 列（源名只编码在文件名里）；分组要按 (包名, 版本, 源) 关联，补上。
        if (!HasColumn(connection, transaction, "archive_pool", "repo"))
        {
            using var alter = connection.CreateCommand();
            alter.Transaction = transaction;
            alter.CommandText = "ALTER TABLE archive_pool ADD COLUMN repo TEXT;";
            alter.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>
    /// v2 → v3：`archive_pool` 的主键从 `(包名, 版本, 架构)` 改成 `(包名, 版本, 架构, 源)`。
    /// <para>
    /// 为什么：pool 文件名带源名（实测同名同版本跨源内容不同），台账却不带源 → 跨源导出会互相覆盖，
    /// 分组回查时成员会静默消失。SQLite 不能改主键，只能重建表再搬数据。
    /// </para>
    /// </summary>
    internal static void MigrateToV3(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                CREATE TABLE archive_pool_v3 (
                  package_id   TEXT NOT NULL,
                  version      TEXT NOT NULL,
                  architecture TEXT NOT NULL,
                  repo         TEXT NOT NULL DEFAULT '',
                  rel_path     TEXT NOT NULL,
                  bytes        INTEGER NOT NULL,
                  sha256       TEXT,
                  acquired_at  TEXT,
                  PRIMARY KEY(package_id, version, architecture, repo)
                );

                INSERT INTO archive_pool_v3(package_id, version, architecture, repo, rel_path, bytes, sha256, acquired_at)
                SELECT package_id, version, architecture, COALESCE(repo, ''), rel_path, bytes, sha256, acquired_at
                FROM archive_pool;

                DROP TABLE archive_pool;
                ALTER TABLE archive_pool_v3 RENAME TO archive_pool;
                """;
            command.ExecuteNonQuery();
        }

        using (var version = connection.CreateCommand())
        {
            version.Transaction = transaction;
            version.CommandText = "PRAGMA user_version = 3;";
            version.ExecuteNonQuery();
        }

        transaction.Commit();
    }
    /// <summary>
    /// v3 → v4：`repos.enabled`（内置源可以「禁用」：保留在列表里、但「全部」抓取时跳过）。
    /// 老库默认 1（保持原有行为）。
    /// </summary>
    internal static void MigrateToV4(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();

        if (!HasColumn(connection, transaction, "repos", "enabled"))
        {
            using var alter = connection.CreateCommand();
            alter.Transaction = transaction;
            alter.CommandText = "ALTER TABLE repos ADD COLUMN enabled INTEGER NOT NULL DEFAULT 1;";
            alter.ExecuteNonQuery();
        }

        using (var version = connection.CreateCommand())
        {
            version.Transaction = transaction;
            version.CommandText = "PRAGMA user_version = 4;";
            version.ExecuteNonQuery();
        }

        transaction.Commit();
    }
    /// <summary>
    /// v4 → v5：给 `library_groups` / `archive_pool` 加 `display_name`（插件显示名，来自索引的 `Name:` 字段），
    /// 并从 `packages` 回填历史行。设计：资料库要显示插件名称，不只是包名。
    /// </summary>
    internal static void MigrateToV5(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();

        if (!HasColumn(connection, transaction, "library_groups", "display_name"))
        {
            using var alter = connection.CreateCommand();
            alter.Transaction = transaction;
            alter.CommandText = "ALTER TABLE library_groups ADD COLUMN display_name TEXT;";
            alter.ExecuteNonQuery();
        }

        if (!HasColumn(connection, transaction, "archive_pool", "display_name"))
        {
            using var alter = connection.CreateCommand();
            alter.Transaction = transaction;
            alter.CommandText = "ALTER TABLE archive_pool ADD COLUMN display_name TEXT;";
            alter.ExecuteNonQuery();
        }

        // 回填：按包名从 packages 取一个非空显示名（同名多版本时取任意一个）
        using (var backfill = connection.CreateCommand())
        {
            backfill.Transaction = transaction;
            backfill.CommandText = """
                UPDATE library_groups SET display_name = (
                    SELECT p.name FROM packages p
                    WHERE p.package_id = library_groups.root_package AND p.name IS NOT NULL AND p.name <> ''
                    LIMIT 1)
                WHERE display_name IS NULL;

                UPDATE archive_pool SET display_name = (
                    SELECT p.name FROM packages p
                    WHERE p.package_id = archive_pool.package_id AND p.name IS NOT NULL AND p.name <> ''
                    LIMIT 1)
                WHERE display_name IS NULL;
                """;
            backfill.ExecuteNonQuery();
        }

        using (var version = connection.CreateCommand())
        {
            version.Transaction = transaction;
            version.CommandText = "PRAGMA user_version = 5;";
            version.ExecuteNonQuery();
        }

        transaction.Commit();
    }
    /// <summary>
    /// v5 → v6：`packages.min_ios`（从 `Depends` 里的 `firmware (&gt;= X)` 抽出的最低 iOS）。
    /// 老行留 NULL（视为不限制），由后台回填补上。
    /// </summary>
    internal static void MigrateToV6(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();

        if (!HasColumn(connection, transaction, "packages", "min_ios"))
        {
            using var alter = connection.CreateCommand();
            alter.Transaction = transaction;
            alter.CommandText = "ALTER TABLE packages ADD COLUMN min_ios TEXT;";
            alter.ExecuteNonQuery();
        }

        using (var version = connection.CreateCommand())
        {
            version.Transaction = transaction;
            version.CommandText = "PRAGMA user_version = 6;";
            version.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private static bool HasColumn(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string column)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA table_info(" + table + ");";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }
}







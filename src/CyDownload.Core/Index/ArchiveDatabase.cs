using System.IO;
using Microsoft.Data.Sqlite;

namespace CyDownload.Core.Index;

/// <summary>
/// 存档库入口：`&lt;存档根目录&gt;\archive.db`，WAL + `busy_timeout=15000`；
/// **单写连接**用 <see cref="SemaphoreSlim"/> 串行化，读用独立连接。
/// <para>
/// 搬自 `iDeviceGui/Services/Data/AppDatabase.cs`，只换 schema 与类名。
/// 原文件把「WAL / 单写连接 / 写锁不可重入 / 短 busy_timeout / Checkpoint」这些坑全踩过一遍，不要重写。
/// </para>
/// <para>
/// 所有方法都是同步的（SQLite 是本地文件 IO）。调用方在后台线程里用它们，
/// **不要**在 UI 线程直接调用（硬约束：UI 线程零阻塞）。
/// </para>
/// </summary>
public sealed class ArchiveDatabase : IDisposable
{
    /// <summary>SQLite 忙等。</summary>
    private const int BusyTimeoutMs = 15000;

    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private SqliteConnection? _writer;
    private bool _disposed;

    /// <summary>
    /// 导入互斥：同一库同时只允许一轮抓取落库。两条并发导入各自"读一遍现有行再写"，
    /// 会互相看不见对方刚插入的行，从而撞唯一索引。
    /// </summary>
    public SemaphoreSlim ImportGate { get; } = new(1, 1);

    public string DatabasePath { get; }
    public int UserVersion { get; }
    public string JournalMode { get; }
    public int BusyTimeout { get; }

    private ArchiveDatabase(string databasePath, int userVersion, string journalMode, int busyTimeout)
    {
        DatabasePath = databasePath;
        UserVersion = userVersion;
        JournalMode = journalMode;
        BusyTimeout = busyTimeout;
    }

    /// <summary>打开（必要时建库）并校验 schema 版本。目录不存在会先创建（只写程序目录）。</summary>
    public static ArchiveDatabase Open(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        var full = Path.GetFullPath(databasePath);
        var directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        using var conn = NewConnection(full, SqliteOpenMode.ReadWriteCreate);
        var journalMode = (Convert.ToString(Scalar(conn, "PRAGMA journal_mode=WAL;")) ?? "").ToLowerInvariant();
        var busyTimeout = Convert.ToInt32(Scalar(conn, "PRAGMA busy_timeout=" + BusyTimeoutMs + ";"));
        var userVersion = Convert.ToInt32(Scalar(conn, "PRAGMA user_version;"));

        if (userVersion == 0)
        {
            SchemaSql.CreateV1Schema(conn);
            userVersion = Convert.ToInt32(Scalar(conn, "PRAGMA user_version;"));
        }

        // 逐级迁移：每次升一级并回读 user_version（幂等；中断在中间也不会留下半级）
        while (userVersion < SchemaSql.CurrentVersion)
        {
            switch (userVersion)
            {
                case 1:
                    SchemaSql.MigrateToV2(conn);
                    break;
                case 2:
                    SchemaSql.MigrateToV3(conn);
                    break;
                case 3:
                    SchemaSql.MigrateToV4(conn);
                    break;
                case 4:
                    SchemaSql.MigrateToV5(conn);
                    break;
                case 5:
                    SchemaSql.MigrateToV6(conn);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"No migration path from archive schema v{userVersion} to v{SchemaSql.CurrentVersion}: {full}");
            }

            userVersion = Convert.ToInt32(Scalar(conn, "PRAGMA user_version;"));
        }

        if (userVersion != SchemaSql.CurrentVersion)
        {
            // 面向开发者/日志的英文消息；界面在 App 层按错误码本地化。
            throw new InvalidOperationException(
                $"Archive schema version {userVersion} does not match expected {SchemaSql.CurrentVersion}: {full}");
        }

        return new ArchiveDatabase(full, userVersion, journalMode, busyTimeout);
    }

    /// <summary>
    /// 只读操作：独立连接，**以 <see cref="SqliteOpenMode.ReadOnly"/> 打开**，不占写锁、也没有写能力。
    /// <para>
    /// WAL 库的只读连接需要 -shm 已存在（由写连接创建）；本类在写连接建立前就有可能被读，
    /// 因此调用方应保证先 <see cref="Open"/> 之后再读 —— 这里对打开失败不做静默降级。
    /// </para>
    /// </summary>
    public T Read<T>(Func<SqliteConnection, T> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var conn = NewConnection(DatabasePath, SqliteOpenMode.ReadOnly);
        return work(conn);
    }

    /// <summary>
    /// 写操作：单写连接 + <see cref="SemaphoreSlim"/> 串行化（不阻塞 UI 线程，因为调用方在后台）。
    /// <para>
    /// **不可重入**：同一次调用栈里再次 `Write` / `Checkpoint` / `Dispose` 会**立即**抛
    /// （不是静默死锁）；跨线程等待超过 <see cref="WriteWaitMs"/> 抛 <see cref="TimeoutException"/>。
    /// </para>
    /// </summary>
    public T Write<T>(Func<SqliteConnection, T> work) => Write(work, WriteWaitMs);

    /// <summary>
    /// 写操作，自定义等锁上限（<paramref name="waitMs"/>）；<paramref name="busyTimeoutMs"/> &gt; 0 时
    /// 临时把 SQLite 的 `busy_timeout` 调小（用完恢复）——「写不进去就算了」的动作不该等满 15 s。
    /// </summary>
    public T Write<T>(Func<SqliteConnection, T> work, int waitMs, int busyTimeoutMs = 0)
    {
        ArgumentNullException.ThrowIfNull(work);
        ObjectDisposedException.ThrowIf(_disposed, this);

        EnterWrite(waitMs);
        try
        {
            _writer ??= NewConnection(DatabasePath, SqliteOpenMode.ReadWrite);
            if (busyTimeoutMs <= 0) return work(_writer);

            // 注意：**只改 `PRAGMA busy_timeout` 不够**——Microsoft.Data.Sqlite 会按
            // `DefaultTimeout`（默认 30 s）自己重试 BUSY。想"很快就放弃"必须两个一起调。
            var previousDefaultTimeout = _writer.DefaultTimeout;
            _writer.DefaultTimeout = Math.Max(1, (busyTimeoutMs + 999) / 1000);
            SetBusyTimeout(_writer, busyTimeoutMs);
            try
            {
                return work(_writer);
            }
            finally
            {
                try
                {
                    _writer.DefaultTimeout = previousDefaultTimeout;
                    SetBusyTimeout(_writer, BusyTimeout);
                }
                catch
                {
                    // 恢复失败不该盖掉原始结果
                }
            }
        }
        finally
        {
            ExitWrite();
        }
    }

    private static void SetBusyTimeout(SqliteConnection conn, int milliseconds)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout=" + milliseconds + ";";
        cmd.ExecuteNonQuery();
    }

    /// <summary>写锁等待上限；测试可临时改小以验证超时路径。</summary>
    internal static int WriteWaitMs = 15_000;

    /// <summary>当前持有写锁的托管线程号；-1 = 无人持有。</summary>
    private int _writeOwnerThreadId = -1;

    private void EnterWrite(int waitMs)
    {
        var tid = Environment.CurrentManagedThreadId;
        if (Volatile.Read(ref _writeOwnerThreadId) == tid)
        {
            throw new InvalidOperationException(
                "Archive write lock is not reentrant: nested Write/Checkpoint/Dispose would deadlock.");
        }

        if (!_writeGate.Wait(waitMs))
        {
            throw new TimeoutException(
                $"Waited more than {waitMs} ms for the archive write lock.");
        }

        Volatile.Write(ref _writeOwnerThreadId, tid);
    }

    private void ExitWrite()
    {
        Volatile.Write(ref _writeOwnerThreadId, -1);
        _writeGate.Release();
    }

    /// <summary>把 WAL 合并回主库（关窗 / 测试里需要看 <c>-wal</c> 是否清掉时用）。</summary>
    public void Checkpoint()
    {
        Write(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            return cmd.ExecuteScalar();
        });
    }

    private static SqliteConnection NewConnection(string path, SqliteOpenMode mode)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Pooling = false,
        };

        var conn = new SqliteConnection(builder.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout=" + BusyTimeoutMs + "; PRAGMA foreign_keys=ON;";
        cmd.ExecuteNonQuery();
        return conn;
    }

    internal static object? Scalar(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    public void Dispose()
    {
        if (_disposed) return;

        // 先拿写锁，成功后才标记 disposed —— 反过来（先标记再 EnterWrite）时若 EnterWrite 抛
        // （在 Write 回调里 Dispose 会立即抛），对象已标记释放但连接与 gate 都没释放。
        EnterWrite(WriteWaitMs);
        try
        {
            _writer?.Dispose();
            _writer = null;
            _disposed = true;
        }
        finally
        {
            ExitWrite();
            _writeGate.Dispose();
        }
    }
}





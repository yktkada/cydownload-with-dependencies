// carried from IdeviceGui Services/AppPaths.cs (main)
using System.IO;
using CyDownload.App.Localization;

namespace CyDownload.App.Infrastructure;

/// <summary>
/// 程序目录 / 日志 / 数据 / 存档目录的唯一出口（只写程序目录，严禁写用户目录）。
/// <para>
/// 发布版：`data`、`logs`、`archive` 都在 exe 同级。开发期可用环境变量
/// <c>CYDOWNLOAD_DATA</c> 把**数据**目录指到别处（避免 <c>dotnet clean</c> 删库）；
/// <b>日志目录始终跟着程序目录</b>，不跟随该变量——崩溃日志要能在"刚部署到新机器"时也找到。
/// </para>
/// </summary>
internal static class AppPaths
{
    public const string DataDirectoryEnvVar = "CYDOWNLOAD_DATA";

    /// <summary>exe 所在目录（带结尾分隔符，与 <see cref="AppContext.BaseDirectory"/> 一致）。</summary>
    public static string ProgramDirectory => AppContext.BaseDirectory;

    /// <summary><c>&lt;程序目录&gt;\logs</c></summary>
    public static string LogsDirectory => Path.Combine(ProgramDirectory, "logs");

    /// <summary>崩溃日志：<c>&lt;程序目录&gt;\logs\crash.log</c>（覆盖写，只留最后一次）。</summary>
    public static string CrashLogPath => Path.Combine(LogsDirectory, "crash.log");

    /// <summary>
    /// 数据目录：开发期读 <c>CYDOWNLOAD_DATA</c>，否则 <c>&lt;程序目录&gt;\data</c>。
    /// <b>不回退用户目录</b>；覆盖值一律 <see cref="Path.GetFullPath(string)"/> ——
    /// 相对路径会落到「当前工作目录」（快捷方式的 Start in 可变），等于写出程序目录，违反硬约束。
    /// </summary>
    public static string DataDirectory
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable(DataDirectoryEnvVar);
            var path = string.IsNullOrWhiteSpace(overridden)
                ? Path.Combine(ProgramDirectory, "data")
                : overridden;

            return Path.GetFullPath(path);
        }
    }

    /// <summary>存档根目录（deb 池 + archive.db）。首次启动自动创建。</summary>
    public static string ArchiveDirectory => Path.Combine(ProgramDirectory, "archive");

    /// <summary>deb 池：<c>&lt;存档根&gt;\pool\&lt;包名&gt;\&lt;版本&gt;.deb</c>。</summary>
    public static string PoolDirectory => Path.Combine(ArchiveDirectory, "pool");

    /// <summary>存档库：<c>&lt;存档根&gt;\archive.db</c>。</summary>
    public static string DatabasePath => Path.Combine(ArchiveDirectory, "archive.db");

    /// <summary>设置文件：<c>&lt;存档根&gt;\settings.json</c>。</summary>
    public static string SettingsPath => Path.Combine(ArchiveDirectory, "settings.json");

    /// <summary>
    /// SQLite 的临时文件目录：<c>&lt;data&gt;\tmp</c>。
    /// <para>
    /// **不能留给系统 TEMP**（那是用户目录，违反硬约束）：SQLite 在大事务 / 大结果集时会建临时文件，
    /// 默认落在 <c>%TEMP%</c>；在严格沙箱下会直接抛 <c>SQLITE_CANTOPEN(14) unable to open database file</c>。
    /// 启动时把它指到程序目录，并同时设 <c>SQLITE_TMPDIR</c> / <c>TMP</c> / <c>TEMP</c>
    /// （SQLite 的 Windows 后端用 <c>GetTempPathW</c>，只认 TMP/TEMP；只设 <c>SQLITE_TMPDIR</c> 实测无效）。
    /// </para>
    /// </summary>
    public static string TempDirectory => Path.Combine(DataDirectory, "tmp");

    /// <summary>把本进程的临时目录指到程序目录。调用时机：打开数据库之前。</summary>
    public static bool UseProgramTempDirectory(out string problem)
    {
        problem = "";
        try
        {
            Directory.CreateDirectory(TempDirectory);
        }
        catch (Exception ex)
        {
            problem = Loc.Format("App.TempNotWritable.Message", TempDirectory, Describe(ex));
            return false;
        }

        if (!TryWriteProbe(TempDirectory, out var tempError))
        {
            problem = Loc.Format("App.TempNotWritable.Message", TempDirectory, tempError);
            return false;
        }

        Environment.SetEnvironmentVariable("SQLITE_TMPDIR", TempDirectory);
        Environment.SetEnvironmentVariable("TMP", TempDirectory);
        Environment.SetEnvironmentVariable("TEMP", TempDirectory);
        return true;
    }

    /// <summary>
    /// 启动自检：程序目录必须可写，否则提示用户把程序放到可写目录，**不静默回退**。
    /// 返回 false 时 <paramref name="problem"/> 是给用户看的多行说明。
    /// </summary>
    public static bool EnsureWritable(out string problem)
    {
        problem = "";

        if (!TryWriteProbe(ProgramDirectory, out var programError))
        {
            problem = Loc.Format("App.NotWritable.Message", ProgramDirectory, programError);
            return false;
        }

        try
        {
            Directory.CreateDirectory(LogsDirectory);
        }
        catch (Exception ex)
        {
            problem = Loc.Format("App.LogsNotWritable.Message", LogsDirectory, Describe(ex));
            return false;
        }

        if (!TryWriteProbe(LogsDirectory, out var logError))
        {
            problem = Loc.Format("App.LogsNotWritable.Message", LogsDirectory, logError);
            return false;
        }

        return true;
    }

    /// <summary>在目标目录里创建再删除一个探针文件，用真实 IO 判断可写（比读 ACL 可靠）。</summary>
    private static bool TryWriteProbe(string directory, out string error)
    {
        error = "";
        var probe = Path.Combine(directory, ".write-probe-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.WriteByte(0x42);
            }

            File.Delete(probe);
            return true;
        }
        catch (Exception ex)
        {
            error = Describe(ex);
            try
            {
                if (File.Exists(probe)) File.Delete(probe);
            }
            catch
            {
                // 探针清理失败不重要
            }

            return false;
        }
    }

    private static string Describe(Exception exception) => exception.GetType().Name + ": " + exception.Message;
}

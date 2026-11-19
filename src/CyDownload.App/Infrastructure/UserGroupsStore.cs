using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CyDownload.App.Infrastructure;

/// <summary>用户分组文件（对齐 IdeviceGui `Services/Groups/GroupStore.cs`：外挂 JSON，不改主库）。</summary>
public sealed class UserGroupsFile
{
    public int Version { get; set; } = 1;

    public List<UserGroup> Groups { get; set; } = [];
}

/// <summary>一个用户分组：名字 + 成员（资料库里插件的根包名）。</summary>
public sealed class UserGroup
{
    public string Name { get; set; } = "";

    public List<string> Members { get; set; } = [];
}

/// <summary>
/// 用户自定义分组的读写（`archive/groups.json`，原子写、损坏自动备份）。
/// <para>
/// 成员是**插件根包名**（资料库按插件分组，所以分组也是插件集合）。主库 schema 不参与。
/// </para>
/// </summary>
public static class UserGroupsStore
{
    public const string FileName = "groups.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string PathFor(string archiveDirectory) => Path.Combine(archiveDirectory, FileName);

    public static UserGroupsFile Load(string archiveDirectory, out string? warning)
    {
        warning = null;
        var path = PathFor(archiveDirectory);
        if (!File.Exists(path)) return new UserGroupsFile();

        try
        {
            var json = File.ReadAllText(path);
            var file = JsonSerializer.Deserialize<UserGroupsFile>(json, JsonOptions) ?? new UserGroupsFile();
            file.Groups ??= [];

            foreach (var group in file.Groups)
            {
                group.Name = (group.Name ?? "").Trim();
                group.Members ??= [];
                group.Members = group.Members
                    .Where(member => !string.IsNullOrWhiteSpace(member))
                    .Select(member => member.Trim())
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
            }

            // 同名组只留第一个，避免下拉框出现重名
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            file.Groups.RemoveAll(group => group.Name.Length == 0 || !seen.Add(group.Name));
            return file;
        }
        catch (Exception ex)
        {
            var backup = "groups.bad-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".json";
            warning = "分组文件损坏，已备份为 " + backup + "：" + ex.GetType().Name + ": " + ex.Message;
            try
            {
                File.Copy(path, Path.Combine(archiveDirectory, backup), overwrite: false);
            }
            catch (IOException)
            {
                // 备份失败不影响主流程（原文件仍在）
            }

            return new UserGroupsFile();
        }
    }

    public static bool Save(UserGroupsFile file, string archiveDirectory, out string? error)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentException.ThrowIfNullOrWhiteSpace(archiveDirectory);

        error = null;
        try
        {
            Directory.CreateDirectory(archiveDirectory);
            var path = PathFor(archiveDirectory);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(file, JsonOptions));
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    /// <summary>校验分组名：空 → 报错；重名（忽略大小写）→ 报错。返回 null 表示可用。</summary>
    public static string? ValidateName(UserGroupsFile file, string name, string? exceptName = null)
    {
        ArgumentNullException.ThrowIfNull(file);

        var trimmed = (name ?? "").Trim();
        if (trimmed.Length == 0) return "name-empty";

        return file.Groups.Any(group =>
            !string.Equals(group.Name, exceptName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(group.Name, trimmed, StringComparison.OrdinalIgnoreCase))
            ? "name-exists"
            : null;
    }

    public static UserGroup? Find(UserGroupsFile file, string name)
    {
        ArgumentNullException.ThrowIfNull(file);
        return file.Groups.FirstOrDefault(group =>
            string.Equals(group.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    public static UserGroup GetOrAdd(UserGroupsFile file, string name)
    {
        var existing = Find(file, name);
        if (existing is not null) return existing;

        var created = new UserGroup { Name = (name ?? "").Trim() };
        file.Groups.Add(created);
        return created;
    }

    public static void RemoveMember(UserGroupsFile file, string packageId)
    {
        ArgumentNullException.ThrowIfNull(file);
        foreach (var group in file.Groups)
        {
            group.Members.RemoveAll(member => string.Equals(member, packageId, StringComparison.Ordinal));
        }
    }
}

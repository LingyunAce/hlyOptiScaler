using System.IO.Abstractions;
using System.Text.Json;
using Deployer.Core.Domain;
using Deployer.Core.Serialization;

namespace Deployer.Core.Profiles;

public sealed class JsonProfileStore : IProfileStore
{
    /// <summary>坏掉的用户配置会被另存成 &lt;原路径&gt;.corrupt，免得下一次 Save 把它覆盖销毁。</summary>
    public const string CorruptSuffix = ".corrupt";

    private readonly IFileSystem _fs;
    private readonly string _path;
    private List<UpscalerProfile> _userProfiles;
    private bool _preserveOriginal;

    public JsonProfileStore(IFileSystem fs, string path)
    {
        _fs = fs;
        _path = path;
        _userProfiles = Load();
    }

    /// <summary>默认位置：%APPDATA%\OptiScalerDeployer\profiles.json</summary>
    public static string DefaultPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "OptiScalerDeployer", "profiles.json");

    public string? LoadError { get; private set; }

    public IReadOnlyList<UpscalerProfile> All()
    {
        var userIds = new HashSet<string>(_userProfiles.Select(p => p.Id), StringComparer.OrdinalIgnoreCase);
        var result = new List<UpscalerProfile>(_userProfiles);
        result.AddRange(BuiltinProfiles.All.Where(p => !userIds.Contains(p.Id)));
        return result;
    }

    public UpscalerProfile? Find(string id) =>
        All().FirstOrDefault(p => SameId(p.Id, id));

    public void Save(UpscalerProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        EnsureCanPersist();
        var problem = Problem(profile);
        if (problem is not null)
        {
            throw new ArgumentException(problem, nameof(profile));
        }

        // 先落盘、成功后才换内存：写失败时界面不会显示一个文件里并不存在的档。
        var next = _userProfiles.Where(p => !SameId(p.Id, profile.Id)).ToList();
        next.Add(profile with { IsBuiltin = false });
        Persist(next);
        _userProfiles = next;
    }

    public bool Delete(string id)
    {
        var next = _userProfiles.Where(p => !SameId(p.Id, id)).ToList();
        if (next.Count == _userProfiles.Count)
        {
            // 没删到东西就不写文件：%APPDATA% 只读时 Delete 一个不存在的 Id 不该抛异常。
            return false;
        }

        EnsureCanPersist();
        Persist(next);
        _userProfiles = next;
        return true;
    }

    private static bool SameId(string? left, string? right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private List<UpscalerProfile> Load()
    {
        if (!_fs.File.Exists(_path))
        {
            return new List<UpscalerProfile>();
        }

        string json;
        try
        {
            json = _fs.File.ReadAllText(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 连读都读不了，也就没法另存副本。原文件保持不动。
            _preserveOriginal = true;
            LoadError = $"读取 {_path} 失败：{ex.Message}。本次只用内置档，原文件没有被改动。";
            return new List<UpscalerProfile>();
        }

        List<UpscalerProfile> parsed;
        try
        {
            parsed = DeployerJson.Deserialize<List<UpscalerProfile>>(json);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // 只吞「这文件不是我们要的东西」。别的异常（包括编程错误）一律往外抛。
            var copy = Quarantine(json);
            LoadError = $"{_path} 不是有效的 profile 列表：{ex.Message}。"
                + (copy is null
                    ? "原内容无法另存，后续保存将被阻止以免覆盖原文件。"
                    : $"原内容已另存为 {copy}，本次只用内置档。");
            return new List<UpscalerProfile>();
        }

        var usable = new List<UpscalerProfile>();
        var rejected = new List<string>();
        foreach (var profile in parsed)
        {
            var problem = profile is null ? "记录是 null" : Problem(profile);
            if (problem is not null)
            {
                rejected.Add(problem);
                continue;
            }

            // 用户手写的 JSON 里 isBuiltin 想填什么就填什么，一律拉回 false：
            // 否则一个冒充内置档的用户档既删不掉（Delete 只动用户档）、又在 UI 上显示成不可编辑。
            usable.Add(profile! with { IsBuiltin = false });
        }

        if (rejected.Count > 0)
        {
            var copy = Quarantine(json);
            LoadError = $"{_path} 里有 {rejected.Count} 条记录不可用（例如：{rejected[0]}），已忽略。"
                + (copy is null
                    ? "原内容无法另存，后续保存将被阻止以免覆盖原文件。"
                    : $"原内容已另存为 {copy}。");
        }

        return usable;
    }

    /// <summary>把原始内容保存到一个未占用的 .corrupt 路径，保留此前启动留下的恢复副本。</summary>
    private string? Quarantine(string json)
    {
        var target = _path + CorruptSuffix;
        for (var suffix = 1; _fs.File.Exists(target); suffix++)
        {
            target = _path + CorruptSuffix + "." + suffix;
        }

        try
        {
            _fs.File.WriteAllText(target, json);
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 连副本都写不出来也不能让构造函数抛：那样程序就起不来了。如实说明就好。
            _preserveOriginal = true;
            return null;
        }
    }

    private void EnsureCanPersist()
    {
        if (_preserveOriginal)
        {
            throw new IOException($"无法安全保存 {_path}：原用户配置无法读取或另存，已保留原文件。");
        }
    }

    private void Persist(List<UpscalerProfile> profiles)
    {
        var dir = _fs.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
        {
            _fs.Directory.CreateDirectory(dir);
        }

        _fs.File.WriteAllText(_path, DeployerJson.Serialize(profiles));
    }

    /// <summary>返回 null 表示可用，否则返回人话说明哪里不行。Save 与 Load 共用同一把尺，保证存得进的一定读得回。</summary>
    private static string? Problem(UpscalerProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Id))
        {
            return "id 为空";
        }

        if (string.IsNullOrWhiteSpace(profile.DisplayName))
        {
            return $"{profile.Id}：displayName 为空";
        }

        if (profile.Description is null)
        {
            return $"{profile.Id}：description 是 null";
        }

        if (profile.Edits is null)
        {
            return $"{profile.Id}：edits 是 null";
        }

        foreach (var edit in profile.Edits)
        {
            if (edit is null)
            {
                return $"{profile.Id}：有一条 edit 是 null";
            }

            if (string.IsNullOrWhiteSpace(edit.Section) || string.IsNullOrWhiteSpace(edit.Key)
                || string.IsNullOrWhiteSpace(edit.NewValue))
            {
                return $"{profile.Id}：有一条 edit 的 section/key/newValue 为空";
            }
        }

        return null;
    }
}

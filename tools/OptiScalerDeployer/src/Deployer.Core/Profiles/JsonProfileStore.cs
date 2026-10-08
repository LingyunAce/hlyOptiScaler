using System.IO.Abstractions;
using Deployer.Core.Domain;
using Deployer.Core.Serialization;

namespace Deployer.Core.Profiles;

public sealed class JsonProfileStore : IProfileStore
{
    private readonly IFileSystem _fs;
    private readonly string _path;
    private List<UpscalerProfile> _userProfiles;

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

    public IReadOnlyList<UpscalerProfile> All()
    {
        var userIds = new HashSet<string>(_userProfiles.Select(p => p.Id), StringComparer.OrdinalIgnoreCase);
        var result = new List<UpscalerProfile>(_userProfiles);
        result.AddRange(BuiltinProfiles.All.Where(p => !userIds.Contains(p.Id)));
        return result;
    }

    public UpscalerProfile? Find(string id) =>
        All().FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    public void Save(UpscalerProfile profile)
    {
        _userProfiles.RemoveAll(p => string.Equals(p.Id, profile.Id, StringComparison.OrdinalIgnoreCase));
        _userProfiles.Add(profile with { IsBuiltin = false });
        Persist();
    }

    public bool Delete(string id)
    {
        var removed = _userProfiles.RemoveAll(
            p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
        if (removed > 0)
        {
            Persist();
        }

        return removed > 0;
    }

    private List<UpscalerProfile> Load()
    {
        try
        {
            if (!_fs.File.Exists(_path))
            {
                return new List<UpscalerProfile>();
            }

            var json = _fs.File.ReadAllText(_path);
            return DeployerJson.Deserialize<List<UpscalerProfile>>(json)
                .Where(p => !string.IsNullOrWhiteSpace(p.Id))
                .ToList();
        }
        catch (Exception)
        {
            // 损坏的用户配置不能阻止程序启动，退回内置档
            return new List<UpscalerProfile>();
        }
    }

    private void Persist()
    {
        var dir = _fs.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
        {
            _fs.Directory.CreateDirectory(dir);
        }

        _fs.File.WriteAllText(_path, DeployerJson.Serialize(_userProfiles));
    }
}

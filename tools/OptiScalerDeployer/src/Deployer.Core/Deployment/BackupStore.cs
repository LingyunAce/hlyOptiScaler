using System.IO.Abstractions;
using Deployer.Core.Domain;
using Deployer.Core.Errors;
using Deployer.Core.Serialization;
using Deployer.Core.Util;

namespace Deployer.Core.Deployment;

public sealed class BackupStore : IBackupStore
{
    private const string ManifestFileName = "manifest.json";
    private const string ProgressFileName = "progress.json";
    private readonly IFileSystem _fs;
    private readonly string _root;
    private readonly AtomicFileWriter _writer;

    public BackupStore(IFileSystem fs, string root)
    {
        _fs = fs;
        _root = FullPath(root);
        _writer = new AtomicFileWriter(fs);
    }

    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OptiScalerDeployer", "backups");

    public string DirectoryFor(string fingerprint, Guid planId) =>
        _fs.Path.Combine(_root, ValidateFingerprint(fingerprint), planId.ToString("D"));

    public string ManifestPathFor(string fingerprint, Guid planId) =>
        _fs.Path.Combine(DirectoryFor(fingerprint, planId), ManifestFileName);

    public string ProgressPathFor(string fingerprint, Guid planId) =>
        _fs.Path.Combine(DirectoryFor(fingerprint, planId), ProgressFileName);

    public BackupEntry Backup(BackupEntry entry)
    {
        EnsureWithinRoot(entry.BackupPath);
        if (!IsWithinPlanDirectory(entry.BackupPath))
        {
            throw new InvalidDataException($"备份目标必须位于 fingerprint/planId 目录中：{entry.BackupPath}");
        }

        if (!_fs.File.Exists(entry.OriginalPath))
        {
            throw new FileNotFoundException($"待备份的原件不存在：{entry.OriginalPath}", entry.OriginalPath);
        }

        if (_fs.File.Exists(entry.BackupPath))
        {
            throw new IOException($"备份目标已存在，拒绝覆盖：{entry.BackupPath}");
        }

        var dir = _fs.Path.GetDirectoryName(entry.BackupPath);
        if (!string.IsNullOrEmpty(dir))
        {
            _fs.Directory.CreateDirectory(dir);
        }

        var sourceHash = Hashing.Sha256OfFile(_fs, entry.OriginalPath);
        _fs.File.Copy(entry.OriginalPath, entry.BackupPath);
        var backupHash = Hashing.Sha256OfFile(_fs, entry.BackupPath);
        if (!string.Equals(sourceHash, backupHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new ChecksumMismatchException(entry.BackupPath, sourceHash, backupHash);
        }

        return entry.WithSha256(sourceHash);
    }

    public void WriteManifest(DeploymentManifest manifest)
    {
        ValidateFingerprint(manifest.Fingerprint);
        WriteJson(ManifestPathFor(manifest.Fingerprint, manifest.PlanId), manifest);
    }

    public DeploymentManifest? ReadManifest(string manifestPath) => ReadJson<DeploymentManifest>(manifestPath);

    public void WriteProgress(DeploymentProgress progress)
    {
        EnsureWithinRoot(progress.ManifestPath);
        var dir = _fs.Path.GetDirectoryName(progress.ManifestPath);
        if (dir is not null)
        {
            WriteJson(_fs.Path.Combine(dir, ProgressFileName), progress);
        }
    }

    public DeploymentProgress? ReadProgress(string progressPath) => ReadJson<DeploymentProgress>(progressPath);

    public IReadOnlyList<Guid> ListPlanIds(string fingerprint)
    {
        var gameRoot = _fs.Path.Combine(_root, ValidateFingerprint(fingerprint));
        EnsureWithinRoot(gameRoot);
        if (!_fs.Directory.Exists(gameRoot))
        {
            return Array.Empty<Guid>();
        }

        var plans = new List<(Guid Id, DateTime Created)>();
        foreach (var dir in _fs.Directory.GetDirectories(gameRoot))
        {
            EnsureWithinRoot(dir);
            if (!Guid.TryParse(_fs.Path.GetFileName(dir), out var id)
                || !string.Equals(_fs.Path.GetFileName(dir), id.ToString("D"), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var created = DateTime.MinValue;
            try
            {
                created = ReadJson<DeploymentManifest>(_fs.Path.Combine(dir, ManifestFileName))?.CreatedUtc
                          ?? DateTime.MinValue;
            }
            catch (InvalidDataException)
            {
                // A corrupt plan remains listed, last, so a healthy plan is still discoverable.
            }

            plans.Add((id, created));
        }

        return plans.OrderByDescending(x => x.Created).Select(x => x.Id).ToList();
    }

    public IReadOnlyList<Guid> Prune(string fingerprint, int keep)
    {
        if (keep < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(keep));
        }

        var doomed = ListPlanIds(fingerprint).Skip(keep).ToList();
        foreach (var id in doomed)
        {
            var dir = DirectoryFor(fingerprint, id);
            EnsureWithinRoot(dir);
            if (_fs.Directory.Exists(dir))
            {
                _fs.Directory.Delete(dir, recursive: true);
            }
        }

        return doomed;
    }

    private void WriteJson<T>(string path, T value)
    {
        EnsureWithinRoot(path);
        var content = DeployerJson.Serialize(value);
        _writer.Write(new FileWrite(path, path, System.Text.Encoding.UTF8.GetByteCount(content),
            Hashing.Sha256OfString(content), content));
    }

    private T? ReadJson<T>(string path) where T : class
    {
        EnsureWithinRoot(path);
        if (!_fs.File.Exists(path))
        {
            return null;
        }

        try
        {
            return DeployerJson.Deserialize<T>(_fs.File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"读不出来：{path}", ex);
        }
    }

    private string FullPath(string path) => _fs.Path.GetFullPath(path);

    private void EnsureWithinRoot(string path)
    {
        var full = FullPath(path);
        if (!IsWithin(full, _root))
        {
            throw new InvalidDataException($"路径超出备份根目录：{path}");
        }
    }

    private bool IsWithinPlanDirectory(string path)
    {
        var relative = _fs.Path.GetRelativePath(_root, FullPath(path));
        var pieces = relative.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        return pieces.Length >= 3
               && RegexFingerprint(pieces[0])
               && Guid.TryParse(pieces[1], out var id)
               && string.Equals(pieces[1], id.ToString("D"), StringComparison.OrdinalIgnoreCase);
    }

    private static bool RegexFingerprint(string? value) =>
        value is { Length: 8 } && value.All(Uri.IsHexDigit);

    private static string ValidateFingerprint(string fingerprint)
    {
        if (!RegexFingerprint(fingerprint))
        {
            throw new ArgumentException("Fingerprint 必须是 8 位十六进制值。", nameof(fingerprint));
        }

        return fingerprint;
    }

    private bool IsWithin(string path, string root)
    {
        var relative = _fs.Path.GetRelativePath(root, path);
        return relative == "." || (!relative.Equals("..", StringComparison.Ordinal)
            && !relative.StartsWith(".." + _fs.Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !relative.StartsWith(".." + _fs.Path.AltDirectorySeparatorChar, StringComparison.Ordinal)
            && !_fs.Path.IsPathRooted(relative));
    }
}

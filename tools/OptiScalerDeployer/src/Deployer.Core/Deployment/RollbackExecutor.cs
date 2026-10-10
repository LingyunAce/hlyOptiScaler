using System.IO.Abstractions;
using Deployer.Core.Domain;
using Deployer.Core.Errors;
using Deployer.Core.Util;

namespace Deployer.Core.Deployment;

public sealed class RollbackExecutor : IRollbackExecutor
{
    private readonly IFileSystem _fs;
    private readonly AtomicFileWriter _writer;
    private readonly IBackupStore _backups;
    private readonly Action<DeploymentManifest>? _validateRecovery;

    public RollbackExecutor(IFileSystem fs, AtomicFileWriter writer, IBackupStore backups, Action<DeploymentManifest>? validateRecovery = null)
    {
        _fs = fs;
        _writer = writer;
        _backups = backups;
        _validateRecovery = validateRecovery;
    }

    public RollbackResult Rollback(string manifestPath)
    {
        DeploymentManifest manifest;
        try
        {
            manifest = _backups.ReadManifest(manifestPath)
                ?? throw new FileNotFoundException($"找不到备份清单：{manifestPath}", manifestPath);
            ValidateManifest(manifestPath, manifest);
            _validateRecovery?.Invoke(manifest);
        }
        catch (Exception ex)
        {
            var cause = ex.InnerException is null ? ex.Message : $"{ex.Message}（{ex.InnerException.Message}）";
            return Failure($"无法读取安全的备份清单：{manifestPath}。需手工处理。原因：{cause}");
        }

        var restored = new List<string>();
        var deleted = new List<string>();
        var errors = new List<string>();
        var protectedOriginals = new HashSet<string>(manifest.Backups.Select(b => FullPath(b.OriginalPath)),
            StringComparer.OrdinalIgnoreCase);

        // Validate each backup before touching its destination. A failed restore must never
        // make that same path eligible for the "new file" deletion pass below.
        foreach (var entry in manifest.Backups.Reverse())
        {
            try
            {
                _validateRecovery?.Invoke(manifest);
                Restore(entry);
                restored.Add(entry.OriginalPath);
            }
            catch (Exception ex)
            {
                errors.Add($"还原失败：{entry.OriginalPath}（{ex.Message}）");
            }
        }

        foreach (var written in manifest.WrittenPaths.Reverse())
        {
            try { _validateRecovery?.Invoke(manifest); }
            catch (Exception ex) { errors.Add($"恢复边界校验失败：{ex.Message}"); break; }
            if (!protectedOriginals.Contains(FullPath(written)))
            {
                TryDelete(written, deleted, errors);
            }

            // Clean only paths named by this manifest; never sweep a game tree for a suffix.
            TryDelete(written + ".optideploy.tmp", deleted, errors);
            TryDelete(written + ".optideploy.bak", deleted, errors);
        }

        foreach (var dir in manifest.CreatedDirectories
                     .OrderByDescending(d => d.Count(c => c is '\\' or '/')))
        {
            try
            {
                _validateRecovery?.Invoke(manifest);
                if (_fs.Directory.Exists(dir) && !_fs.Directory.EnumerateFileSystemEntries(dir).Any())
                {
                    _fs.Directory.Delete(dir, recursive: false);
                    deleted.Add(dir);
                }
            }
            catch (Exception ex)
            {
                errors.Add($"删除空目录失败：{dir}（{ex.Message}）");
            }
        }

        return new RollbackResult(errors.Count == 0, restored, deleted,
            errors.Count == 0 ? null : string.Join(Environment.NewLine, errors));
    }

    private void Restore(BackupEntry entry)
    {
        if (!_fs.File.Exists(entry.BackupPath))
        {
            throw new FileNotFoundException("备份文件丢失", entry.BackupPath);
        }

        // Old manifests have no saved hash. Hash the backup now so the atomic copy still
        // verifies transfer integrity, while reporting the historical limitation is unnecessary
        // for the existing RollbackResult contract.
        var expected = entry.Sha256 ?? Hashing.Sha256OfFile(_fs, entry.BackupPath);
        var actual = Hashing.Sha256OfFile(_fs, entry.BackupPath);
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
        {
            throw new ChecksumMismatchException(entry.BackupPath, expected, actual);
        }

        var length = _fs.FileInfo.New(entry.BackupPath).Length;
        _writer.Write(new FileWrite(entry.BackupPath, entry.OriginalPath, length, expected));
    }

    private void ValidateManifest(string manifestPath, DeploymentManifest manifest)
    {
        var expectedManifest = _backups.ManifestPathFor(manifest.Fingerprint, manifest.PlanId);
        if (!FullPath(manifestPath).Equals(FullPath(expectedManifest), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("备份清单路径与清单中的游戏/PlanId 不匹配。");
        }

        var deploy = FullPath(manifest.DeployDirectory);
        var backupRoot = FullPath(_backups.DirectoryFor(manifest.Fingerprint, manifest.PlanId));
        var written = new HashSet<string>(manifest.WrittenPaths.Select(FullPath), StringComparer.OrdinalIgnoreCase);
        foreach (var path in manifest.WrittenPaths)
        {
            EnsureChildOf(path, deploy);
        }

        foreach (var entry in manifest.Backups)
        {
            EnsureChildOf(entry.OriginalPath, deploy);
            EnsureChildOf(entry.BackupPath, backupRoot);
            if (!written.Contains(FullPath(entry.OriginalPath)))
            {
                throw new InvalidDataException($"备份原件路径不在本次写入清单中：{entry.OriginalPath}");
            }
        }

        foreach (var dir in manifest.CreatedDirectories)
        {
            EnsureChildOf(dir, deploy);
        }
    }

    private void EnsureChildOf(string path, string root)
    {
        var full = FullPath(path);
        var relative = _fs.Path.GetRelativePath(root, full);
        if (relative == "." || relative == ".."
            || relative.StartsWith(".." + _fs.Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || relative.StartsWith(".." + _fs.Path.AltDirectorySeparatorChar, StringComparison.Ordinal)
            || _fs.Path.IsPathRooted(relative))
        {
            throw new InvalidDataException($"manifest 路径超出部署目录：{path}");
        }
    }

    private string FullPath(string path) => _fs.Path.GetFullPath(path);

    private void TryDelete(string path, List<string> deleted, List<string> errors)
    {
        try
        {
            if (_fs.File.Exists(path))
            {
                _fs.File.Delete(path);
                deleted.Add(path);
            }
        }
        catch (Exception ex)
        {
            errors.Add($"删除失败：{path}（{ex.Message}）");
        }
    }

    private static RollbackResult Failure(string message) =>
        new(false, Array.Empty<string>(), Array.Empty<string>(), message);
}

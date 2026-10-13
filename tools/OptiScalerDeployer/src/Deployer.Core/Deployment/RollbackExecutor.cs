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
        IReadOnlyList<string> completedWrittenPaths;
        IReadOnlyList<string> completedCreatedDirectories;
        string? pendingRecoveryError;
        try
        {
            RollbackPrivilegePolicy.EnsureAllowedForCurrentProcess();
            RejectReparseAncestors(manifestPath);
            manifest = _backups.ReadManifest(manifestPath)
                ?? throw new FileNotFoundException($"找不到备份清单：{manifestPath}", manifestPath);
            ValidateManifest(manifestPath, manifest);
            _validateRecovery?.Invoke(manifest);
            completedWrittenPaths = ResolveCompletedWrittenPaths(manifest, out pendingRecoveryError);
            completedCreatedDirectories = ResolveCompletedCreatedDirectories(manifest, completedWrittenPaths);
        }
        catch (Exception ex)
        {
            var cause = ex.InnerException is null ? ex.Message : $"{ex.Message}（{ex.InnerException.Message}）";
            return Failure($"无法读取安全的备份清单：{manifestPath}。需手工处理。原因：{cause}");
        }

        var restored = new List<string>();
        var deleted = new List<string>();
        var errors = new List<string>();
        if (pendingRecoveryError is not null) errors.Add(pendingRecoveryError);
        var protectedOriginals = new HashSet<string>(manifest.Backups.Select(b => FullPath(b.OriginalPath)),
            StringComparer.OrdinalIgnoreCase);
        var ownedWrites = completedWrittenPaths.Select(FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Validate each backup before touching its destination. A failed restore must never
        // make that same path eligible for the "new file" deletion pass below.
        foreach (var entry in manifest.Backups.Reverse())
        {
            if (!ownedWrites.Contains(FullPath(entry.OriginalPath))) continue;
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

        foreach (var written in completedWrittenPaths.Reverse())
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

        foreach (var dir in completedCreatedDirectories
                     .OrderByDescending(d => d.Count(c => c is '\\' or '/')))
        {
            try
            {
                _validateRecovery?.Invoke(manifest);
                RejectReparseTree(dir);
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
        // Re-check immediately before touching either side. A junction can be
        // introduced after manifest validation, so lexical containment alone is
        // insufficient for an elevated rollback.
        RejectReparseAncestors(entry.OriginalPath);
        RejectReparseAncestors(entry.BackupPath);
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

        if (manifest.RecoveryStateVersion is < 0 or > 1)
            throw new InvalidDataException("不支持的恢复清单版本。");
        if (manifest.PendingWrite is { } pending)
        {
            EnsureChildOf(pending.Path, deploy);
            if (!System.Text.RegularExpressions.Regex.IsMatch(pending.Sha256, "^[0-9a-fA-F]{64}$"))
                throw new InvalidDataException("待写入文件的哈希无效。");
            if (written.Contains(FullPath(pending.Path)))
                throw new InvalidDataException("待写入路径已在完成清单中。");
            written.Add(FullPath(pending.Path));
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

    private IReadOnlyList<string> ResolveCompletedWrittenPaths(DeploymentManifest manifest, out string? pendingRecoveryError)
    {
        pendingRecoveryError = null;
        var progressPath = _backups.ProgressPathFor(manifest.Fingerprint, manifest.PlanId);
        RejectReparseAncestors(progressPath);
        if (manifest.RecoveryStateVersion == 1)
        {
            var owned = manifest.WrittenPaths.ToList();
            if (manifest.PendingWrite is { } pending)
            {
                var backup = manifest.Backups.FirstOrDefault(entry => FullPath(entry.OriginalPath)
                    .Equals(FullPath(pending.Path), StringComparison.OrdinalIgnoreCase));
                if (!_fs.File.Exists(pending.Path))
                {
                    if (_fs.File.Exists(pending.Path + ".optideploy.tmp") || _fs.File.Exists(pending.Path + ".optideploy.bak"))
                        pendingRecoveryError = $"待写入目标缺失但存在未决临时文件：{pending.Path}。已保留现场供手工检查。";
                    else if (backup is not null)
                        owned.Add(pending.Path); // Restore the original if it disappeared during the attempted write.
                }
                else
                {
                    var actual = Hashing.Sha256OfFile(_fs, pending.Path);
                    if (actual.Equals(pending.Sha256, StringComparison.OrdinalIgnoreCase))
                        owned.Add(pending.Path);
                    else if (backup is not null && _fs.File.Exists(backup.BackupPath))
                    {
                        var originalHash = backup.Sha256 ?? Hashing.Sha256OfFile(_fs, backup.BackupPath);
                        if (!actual.Equals(originalHash, StringComparison.OrdinalIgnoreCase))
                            pendingRecoveryError = $"待写入目标内容既非本次 payload，也非备份原件：{pending.Path}。已保留现场供手工检查。";
                    }
                    else
                        pendingRecoveryError = $"待写入目标内容与预期不符：{pending.Path}。已保留现场供手工检查。";
                }
            }
            return owned;
        }

        // Legacy manifests used progress.json as completion state. New manifests never
        // consult this mirror because it cannot be atomically committed with manifest.json.
        var progress = _backups.ReadProgress(progressPath);
        if (progress is null) return manifest.WrittenPaths; // compatibility with pre-progress manifests
        if (progress.PlanId != manifest.PlanId || !FullPath(progress.ManifestPath).Equals(
                FullPath(_backups.ManifestPathFor(manifest.Fingerprint, manifest.PlanId)), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("部署进度与备份清单不匹配。");
        var allowed = manifest.WrittenPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var completed = progress.CompletedSteps.ToArray();
        if (completed.Any(path => !allowed.Contains(path)))
            throw new InvalidDataException("部署进度包含不在清单中的写入路径。");
        return completed;
    }

    private IReadOnlyList<string> ResolveCompletedCreatedDirectories(DeploymentManifest manifest, IReadOnlyList<string> completedWrites)
    {
        return manifest.CreatedDirectories.Where(directory => completedWrites.Any(path =>
        {
            var relative = _fs.Path.GetRelativePath(FullPath(directory), FullPath(path));
            return relative != "." && !relative.Equals("..", StringComparison.Ordinal)
                && !relative.StartsWith(".." + _fs.Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !relative.StartsWith(".." + _fs.Path.AltDirectorySeparatorChar, StringComparison.Ordinal)
                && !_fs.Path.IsPathRooted(relative);
        })).ToArray();
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
        RejectReparseAncestors(full);
    }

    private string FullPath(string path) => _fs.Path.GetFullPath(path);

    private void TryDelete(string path, List<string> deleted, List<string> errors)
    {
        try
        {
            RejectReparseAncestors(path);
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

    private void RejectReparseAncestors(string path)
    {
        var current = FullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                if (_fs.File.Exists(current) || _fs.Directory.Exists(current))
                {
                    if ((_fs.File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidDataException($"回滚路径包含 reparse point：{path}");
                }
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            current = _fs.Path.GetDirectoryName(current);
        }
    }

    private void RejectReparseTree(string root)
    {
        RejectReparseAncestors(root);
        if (!_fs.Directory.Exists(root)) return;
        foreach (var child in _fs.Directory.EnumerateFileSystemEntries(root))
        {
            RejectReparseAncestors(child);
            if (_fs.Directory.Exists(child)) RejectReparseTree(child);
        }
    }

    private static RollbackResult Failure(string message) =>
        new(false, Array.Empty<string>(), Array.Empty<string>(), message);
}

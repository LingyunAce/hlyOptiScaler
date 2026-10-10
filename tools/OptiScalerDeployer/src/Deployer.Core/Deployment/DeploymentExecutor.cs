using System.IO.Abstractions;
using Deployer.Core.Domain;
using Deployer.Core.Errors;
using Deployer.Core.Util;

namespace Deployer.Core.Deployment;

public sealed class DeploymentExecutor : IDeploymentExecutor
{
    private readonly IFileSystem _fs;
    private readonly AtomicFileWriter _writer;
    private readonly IBackupStore _backups;
    private readonly IRollbackExecutor? _rollback;
    private readonly Action? _validateExecution;

    public DeploymentExecutor(IFileSystem fs, AtomicFileWriter writer, IBackupStore backups,
        IRollbackExecutor? rollback = null, Action? validateExecution = null)
    {
        _fs = fs;
        _writer = writer;
        _backups = backups;
        _rollback = rollback;
        _validateExecution = validateExecution;
    }

    public DeploymentResult Execute(DeploymentPlan plan, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var completed = new List<string>();
        var manifestPath = string.Empty;
        var manifestPublished = false;
        var gameWriteStarted = false;

        try
        {
            var rejection = RejectReason(plan);
            if (rejection is not null)
            {
                return Failed(string.Empty, completed, rejection, false);
            }

            var fingerprint = plan.Game.Fingerprint;
            ValidatePlanPaths(plan);
            _validateExecution?.Invoke();
            manifestPath = _backups.ManifestPathFor(fingerprint, plan.PlanId);
            if (_fs.File.Exists(manifestPath))
            {
                return Failed(manifestPath, completed, "此 PlanId 已执行或留下部署记录；请重新生成计划后再执行。", false);
            }

            EnsureDestinationsStillMatchPlan(plan);
            var backups = new List<BackupEntry>();

            // Publish a safe empty manifest first. It cannot authorize deleting or replacing any game file.
            WriteManifest(plan, manifestPath, backups, Array.Empty<string>(), Array.Empty<string>());
            manifestPublished = true;

            foreach (var entry in plan.Backups)
            {
                ct.ThrowIfCancellationRequested();
                _validateExecution?.Invoke();
                var verified = _backups.Backup(entry);
                backups.Add(verified);
            }

            // All overwrite originals now have verified shadow copies. The manifest becomes
            // rollback-authoritative only after this point.
            EnsureDestinationsStillMatchPlan(plan, backups);
            var createdDirs = plan.Writes.SelectMany(w => _writer.MissingDirectories(w.DestinationPath))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var written = plan.Writes.Select(w => w.DestinationPath).ToList();
            WriteManifest(plan, manifestPath, backups, written, createdDirs);
            _backups.WriteProgress(DeploymentProgress.Empty(plan.PlanId, manifestPath));

            foreach (var write in plan.Writes)
            {
                ct.ThrowIfCancellationRequested();
                _validateExecution?.Invoke();
                EnsureDestinationStillMatchesPlan(write.DestinationPath, backups);
                gameWriteStarted = true;
                _writer.Write(write);
                completed.Add(write.DestinationPath);
                _validateExecution?.Invoke();
                _backups.WriteProgress(new DeploymentProgress
                {
                    PlanId = plan.PlanId,
                    ManifestPath = manifestPath,
                    CompletedSteps = completed.ToList(),
                });
            }

            return new DeploymentResult(true, manifestPath, completed, null, false);
        }
        catch (OperationCanceledException)
        {
            if (manifestPublished && gameWriteStarted)
            {
                TryRollback(manifestPath);
            }

            throw;
        }
        catch (Exception ex)
        {
            var rolledBack = manifestPublished && gameWriteStarted && TryRollback(manifestPath);
            var message = ex is DeployerException ? ex.Message : $"部署失败：{ex.Message}";
            return Failed(manifestPath, completed, message, rolledBack);
        }
    }

    private static string? RejectReason(DeploymentPlan plan)
    {
        if (plan.HasBlockingWarning)
        {
            return "计划包含阻塞级警告，不能执行。请先处理警告。";
        }

        if (plan.Identity.Status != IdentityStatus.Resolved)
        {
            return "DLL 身份尚未解决，不能执行部署。";
        }

        if (plan.Elevation is ElevationRequirement.Blocked or ElevationRequirement.Declined
            || plan.Writability == DirectoryWritability.Blocked)
        {
            return "部署目录当前不可写，不能执行部署。";
        }

        if (plan.Writes.Count == 0)
        {
            return "部署计划没有任何文件写入。";
        }

        return null;
    }

    private void ValidatePlanPaths(DeploymentPlan plan)
    {
        var root = FullPath(plan.Game.DeployDirectory);
        var backupRoot = FullPath(_backups.DirectoryFor(plan.Game.Fingerprint, plan.PlanId));
        var backupPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var writePaths = new HashSet<string>(plan.Writes.Select(w => FullPath(w.DestinationPath)),
            StringComparer.OrdinalIgnoreCase);
        foreach (var backup in plan.Backups)
        {
            EnsureChildOf(backup.OriginalPath, root);
            EnsureChildOf(backup.BackupPath, backupRoot);
            if (!writePaths.Contains(FullPath(backup.OriginalPath)))
            {
                throw new InvalidDataException($"备份目标不属于本次写入：{backup.OriginalPath}");
            }

            if (!backupPaths.Add(FullPath(backup.OriginalPath)))
            {
                throw new InvalidDataException($"重复的备份目标：{backup.OriginalPath}");
            }
        }

        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var write in plan.Writes)
        {
            EnsureChildOf(write.DestinationPath, root);
            var destination = FullPath(write.DestinationPath);
            if (!destinations.Add(destination))
            {
                throw new InvalidDataException($"重复的写入目标：{write.DestinationPath}");
            }

            if (_fs.File.Exists(write.DestinationPath) && !backupPaths.Contains(destination))
            {
                throw new IOException($"目标文件已存在但计划没有备份它，拒绝覆盖：{write.DestinationPath}");
            }

            if (_fs.File.Exists(write.DestinationPath + ".optideploy.tmp")
                || _fs.File.Exists(write.DestinationPath + ".optideploy.bak"))
            {
                throw new IOException($"部署临时文件已存在，拒绝覆盖：{write.DestinationPath}");
            }
        }

        var identityWrite = plan.Writes[^1];
        var identityName = plan.Identity.FileName;
        var expectedDestination = FullPath(_fs.Path.Combine(root, identityName));
        var expectedSource = FullPath(_fs.Path.Combine(plan.Payload.RootDirectory, "OptiScaler.dll"));
        if (identityName != _fs.Path.GetFileName(identityName)
            || !FullPath(identityWrite.DestinationPath).Equals(expectedDestination, StringComparison.OrdinalIgnoreCase)
            || identityWrite.InlineContent is not null
            || !FullPath(identityWrite.SourcePath).Equals(expectedSource, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("计划必须以 OptiScaler.dll 的身份副本作为最后一步写入。");
        }
    }

    private void EnsureDestinationsStillMatchPlan(DeploymentPlan plan, IReadOnlyList<BackupEntry>? backups = null)
    {
        foreach (var write in plan.Writes)
        {
            EnsureDestinationStillMatchesPlan(write.DestinationPath, backups ?? plan.Backups);
        }
    }

    private void EnsureDestinationStillMatchesPlan(string path, IReadOnlyList<BackupEntry> backups)
    {
        var backup = backups.FirstOrDefault(b => FullPath(b.OriginalPath)
            .Equals(FullPath(path), StringComparison.OrdinalIgnoreCase));
        var exists = _fs.File.Exists(path);
        if (backup is null && exists)
        {
            throw new IOException($"执行期间目标文件新出现，拒绝覆盖：{path}");
        }

        if (backup is not null)
        {
            if (!exists)
            {
                throw new IOException($"待覆盖原件在执行期间消失：{path}");
            }

            if (backup.Sha256 is null)
            {
                return;
            }

            var expected = backup.Sha256;
            var actual = Hashing.Sha256OfFile(_fs, path);
            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException($"目标文件在计划/备份后发生变化，拒绝覆盖：{path}");
            }
        }
    }

    private void WriteManifest(DeploymentPlan plan, string path, IReadOnlyList<BackupEntry> backups,
        IReadOnlyList<string> written, IReadOnlyList<string> createdDirectories) =>
        WriteValidatedManifest(new DeploymentManifest
        {
            PlanId = plan.PlanId,
            CreatedUtc = DateTime.UtcNow,
            Fingerprint = plan.Game.Fingerprint,
            GameTitle = plan.Game.Title,
            DeployDirectory = plan.Game.DeployDirectory,
            Payload = plan.Payload,
            Identity = plan.Identity,
            Profile = plan.Profile,
            Backups = backups.ToList(),
            WrittenPaths = written.ToList(),
            CreatedDirectories = createdDirectories.ToList(),
        });

    private void WriteValidatedManifest(DeploymentManifest manifest)
    {
        _validateExecution?.Invoke();
        _backups.WriteManifest(manifest);
    }

    private bool TryRollback(string manifestPath)
    {
        if (_rollback is null)
        {
            return false;
        }

        try
        {
            _validateExecution?.Invoke();
            return _rollback.Rollback(manifestPath).Success;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private string FullPath(string path) => _fs.Path.GetFullPath(path);

    private void EnsureChildOf(string path, string root)
    {
        var relative = _fs.Path.GetRelativePath(root, FullPath(path));
        if (relative == "." || relative == ".."
            || relative.StartsWith(".." + _fs.Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || relative.StartsWith(".." + _fs.Path.AltDirectorySeparatorChar, StringComparison.Ordinal)
            || _fs.Path.IsPathRooted(relative))
        {
            throw new InvalidDataException($"计划路径超出部署目录：{path}");
        }
    }

    private static DeploymentResult Failed(string path, IReadOnlyList<string> completed, string message, bool rolledBack) =>
        new(false, path, completed.ToList(), message, rolledBack);
}

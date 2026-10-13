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
            WriteState(plan, manifestPath, backups, Array.Empty<string>(), Array.Empty<string>(), null);
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
            // Keep the recovery manifest limited to writes that have actually completed.
            // Publishing the complete plan here would allow rollback to delete a file that
            // was never written by this run (for example after a mid-deployment crash).
            WriteState(plan, manifestPath, backups, Array.Empty<string>(), createdDirs, null);

            foreach (var write in plan.Writes)
            {
                ct.ThrowIfCancellationRequested();
                _validateExecution?.Invoke();
                EnsureDestinationStillMatchesPlan(write.DestinationPath, backups);
                var pending = new PendingDeploymentWrite(write.DestinationPath, write.Sha256);
                // Write-ahead intent closes the crash window between the atomic game-file
                // replacement and publishing the completed prefix. Recovery deletes/restores
                // this target only when its bytes match this expected hash.
                WriteState(plan, manifestPath, backups, completed, createdDirs, pending);
                gameWriteStarted = true;
                _writer.Write(write);
                completed.Add(write.DestinationPath);
                WriteState(plan, manifestPath, backups, completed, createdDirs, null);
                _validateExecution?.Invoke();
            }

            return new DeploymentResult(true, manifestPath, completed, null, false);
        }
        catch (OperationCanceledException ex)
        {
            if (manifestPublished && gameWriteStarted)
            {
                if (!TryRollback(manifestPath, out var recoveryError))
                    throw new OperationCanceledException(
                        $"部署已取消，但自动恢复未完成。恢复清单已保留，请关闭管理员权限并在普通用户会话中恢复。{recoveryError}", ex, ct);
            }

            throw;
        }
        catch (Exception ex)
        {
            var rolledBack = false;
            string? recoveryError = null;
            if (manifestPublished && gameWriteStarted)
                rolledBack = TryRollback(manifestPath, out recoveryError);
            var message = ex is DeployerException ? ex.Message : $"部署失败：{ex.Message}";
            if (manifestPublished && gameWriteStarted && !rolledBack)
                message += $"\n自动恢复未完成，恢复清单已保留，请关闭管理员权限并在普通用户会话中恢复。{recoveryError}";
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

    private void WriteState(DeploymentPlan plan, string path, IReadOnlyList<BackupEntry> backups,
        IReadOnlyList<string> written, IReadOnlyList<string> createdDirectories, PendingDeploymentWrite? pending)
    {
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
            Backups = backups.Where(backup => written.Concat(pending is null
                    ? Array.Empty<string>()
                    : new[] { pending.Path })
                .Any(path => FullPath(path).Equals(FullPath(backup.OriginalPath), StringComparison.OrdinalIgnoreCase)))
                .ToList(),
            WrittenPaths = written.ToList(),
            RecoveryStateVersion = 1,
            PendingWrite = pending,
            CreatedDirectories = createdDirectories.ToList(),
        });
        _backups.WriteProgress(new DeploymentProgress
        {
            PlanId = plan.PlanId,
            ManifestPath = path,
            CompletedSteps = written.ToList(),
            PendingWrite = pending,
        });
    }

    private void WriteValidatedManifest(DeploymentManifest manifest)
    {
        _validateExecution?.Invoke();
        _backups.WriteManifest(manifest);
    }

    private bool TryRollback(string manifestPath, out string? recoveryError)
    {
        recoveryError = null;
        if (_rollback is null)
        {
            recoveryError = "当前没有可用的回滚器。";
            return false;
        }

        try
        {
            _validateExecution?.Invoke();
            var result = _rollback.Rollback(manifestPath);
            recoveryError = result.ErrorMessage;
            return result.Success;
        }
        catch (Exception ex)
        {
            recoveryError = ex.Message;
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

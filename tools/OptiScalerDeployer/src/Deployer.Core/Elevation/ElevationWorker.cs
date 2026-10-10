using System.IO.Abstractions;
using System.Security.Cryptography;
using System.Text;
using Deployer.Core.Deployment;
using Deployer.Core.Domain;
using Deployer.Core.Serialization;
using Deployer.Core.Util;
namespace Deployer.Core.Elevation;
public sealed record ElevationRequest(DeploymentPlan Plan, string BackupRoot);
public static class ElevationJobStore
{
    public static ElevationJob Create(DeploymentPlan plan, string backupRoot, string jobsRoot)
    {
        var directory = Path.GetFullPath(Path.Combine(jobsRoot, plan.PlanId.ToString("D")));
        ElevationPolicy.RejectReparseAncestors(directory);
        if (Directory.Exists(directory)) throw new IOException("Job already exists; generate a new plan.");
        Directory.CreateDirectory(directory);
        var bytes = Encoding.UTF8.GetBytes(DeployerJson.Serialize(new ElevationRequest(plan, Path.GetFullPath(backupRoot))));
        var path = Path.Combine(directory, "plan.json");
        using (var file = new FileStream(path, FileMode.CreateNew)) file.Write(bytes);
        return new(plan.PlanId, path, Convert.ToHexString(SHA256.HashData(bytes)), directory);
    }

    public static ElevationRequest Read(ElevationJob job)
    {
        ElevationPolicy.ValidateJob(job);
        using var stream = new FileStream(job.PlanPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 16 * 1024 * 1024) throw new InvalidDataException("Job exceeds size limit.");
        using var buffer = new MemoryStream(); stream.CopyTo(buffer); var bytes = buffer.ToArray();
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), Convert.FromHexString(job.PlanSha256))) throw new InvalidDataException("Plan checksum mismatch.");
        return DeployerJson.Deserialize<ElevationRequest>(Encoding.UTF8.GetString(bytes));
    }

    public static void Cleanup(ElevationJob job)
    {
        ElevationPolicy.RejectReparseAncestors(job.JobDirectory);
        foreach (var name in new[] { "plan.json", "result.json", "cancel" })
        { var path = Path.Combine(job.JobDirectory, name); ElevationPolicy.RejectReparseAncestors(path); if (File.Exists(path)) File.Delete(path); }
        if (!Directory.EnumerateFileSystemEntries(job.JobDirectory).Any()) Directory.Delete(job.JobDirectory);
    }
}
public static class ElevationWorker
{
    public static Action<DeploymentManifest> CreateRecoveryValidator(ElevationRequest request)
    {
        var plan = request.Plan; var fs = new FileSystem(); var writer = new AtomicFileWriter(fs);
        var writes = plan.Writes.Select(w => Path.GetFullPath(w.DestinationPath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var directories = plan.Writes.SelectMany(w => writer.MissingDirectories(w.DestinationPath)).Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var originals = plan.Backups.ToDictionary(b => Path.GetFullPath(b.OriginalPath),
            b => (Path: Path.GetFullPath(b.BackupPath), Hash: Hashing.Sha256OfFile(fs, b.OriginalPath)), StringComparer.OrdinalIgnoreCase);
        var backupDir = new BackupStore(fs, request.BackupRoot).DirectoryFor(plan.Game.Fingerprint, plan.PlanId);
        return manifest =>
        {
            if (manifest.PlanId != plan.PlanId || manifest.Fingerprint != plan.Game.Fingerprint
                || !Path.GetFullPath(manifest.DeployDirectory).Equals(Path.GetFullPath(plan.Game.DeployDirectory), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Recovery manifest does not match the reviewed plan.");
            var paths = manifest.WrittenPaths.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (paths.Count != manifest.WrittenPaths.Count || !paths.SetEquals(writes) || manifest.Backups.Count != originals.Count
                || manifest.Backups.Select(b => Path.GetFullPath(b.OriginalPath)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != originals.Count)
                throw new InvalidDataException("Recovery manifest has missing or unapproved operations.");
            foreach (var path in paths)
            {
                ElevationPolicy.ValidateTarget(path, plan.Game.DeployDirectory);
                ElevationPolicy.ValidateTarget(path + ".optideploy.tmp", plan.Game.DeployDirectory);
                ElevationPolicy.ValidateTarget(path + ".optideploy.bak", plan.Game.DeployDirectory);
            }
            foreach (var backup in manifest.Backups)
            {
                ElevationPolicy.ValidateTarget(backup.BackupPath, backupDir);
                if (!originals.TryGetValue(Path.GetFullPath(backup.OriginalPath), out var original)
                    || !original.Path.Equals(Path.GetFullPath(backup.BackupPath), StringComparison.OrdinalIgnoreCase)
                    || !original.Hash.Equals(backup.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Recovery backup does not match the trusted original.");
            }
            foreach (var dir in manifest.CreatedDirectories)
            {
                ElevationPolicy.ValidateTarget(dir, plan.Game.DeployDirectory);
                if (!directories.Contains(Path.GetFullPath(dir))) throw new InvalidDataException("Recovery directory was not created by this plan.");
            }
        };
    }
    public static async Task<ElevationResult> ExecuteAsync(ElevationJob job, CancellationToken ct = default)
    {
        string? manifestPath = null; var completed = new List<string>();
        ElevationResult result;
        var outputSafe = false;
        try
        {
            ElevationPolicy.ValidateJobDirectory(job);
            var resultPath = Path.Combine(job.JobDirectory, "result.json");
            if (File.Exists(resultPath)) return new(job.JobId, false, "Rejected", null, completed, "Job already has a result.", true);
            outputSafe = true;
            var request = ElevationJobStore.Read(job);
            ValidateRequest(job, request);
            var recoveryValidator = CreateRecoveryValidator(request);
            var fs = new FileSystem(); var store = new BackupStore(fs, request.BackupRoot); var writer = new AtomicFileWriter(fs);
            manifestPath = store.ManifestPathFor(request.Plan.Game.Fingerprint, request.Plan.PlanId);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            using var monitorStop = new CancellationTokenSource();
            void CheckCancel()
            {
                var marker = Path.Combine(job.JobDirectory, "cancel"); ElevationPolicy.RejectReparseAncestors(marker);
                if (File.Exists(marker)) cancellation.Cancel();
            }
            CheckCancel();
            var monitor = Task.Run(async () =>
            {
                try { while (!monitorStop.IsCancellationRequested) { CheckCancel(); await Task.Delay(100, monitorStop.Token); } }
                catch (OperationCanceledException) { }
                catch { cancellation.Cancel(); }
            });
            try
            {
                var executor = new DeploymentExecutor(fs, writer, store, new RollbackExecutor(fs, writer, store, recoveryValidator), () => ValidateRequest(job, request));
                var deployed = await Task.Run(() => executor.Execute(request.Plan, cancellation.Token), CancellationToken.None);
                completed.AddRange(deployed.CompletedSteps);
                result = new(job.JobId, deployed.Success, deployed.Success ? "Completed" : "Failed", deployed.ManifestPath, completed, deployed.ErrorMessage, !deployed.Success && File.Exists(manifestPath));
            }
            finally { monitorStop.Cancel(); await monitor; }
        }
        catch (OperationCanceledException)
        { result = new(job.JobId, false, "Cancelled", manifestPath, completed, "Deployment cancelled; inspect the manifest before recovery.", File.Exists(manifestPath)); }
        catch (Exception ex)
        { result = new(job.JobId, false, "Rejected", manifestPath, completed, ex.Message, File.Exists(manifestPath)); }
        if (manifestPath is not null && File.Exists(Path.Combine(Path.GetDirectoryName(manifestPath)!, "progress.json")))
        {
            try { result = result with { CompletedSteps = DeployerJson.Deserialize<DeploymentProgress>(File.ReadAllText(Path.Combine(Path.GetDirectoryName(manifestPath)!, "progress.json"))).CompletedSteps }; } catch { }
        }
        if (outputSafe)
        {
            ElevationPolicy.ValidateJobDirectory(job);
            var temp = Path.Combine(job.JobDirectory, "result.tmp");
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { var bytes = Encoding.UTF8.GetBytes(DeployerJson.Serialize(result)); stream.Write(bytes); stream.Flush(true); }
            File.Move(temp, Path.Combine(job.JobDirectory, "result.json"));
        }
        return result;
    }

    public static void ValidateRequest(ElevationJob job, ElevationRequest request)
    {
        var plan = request.Plan; var game = plan.Game;
        if (plan.PlanId != job.JobId || plan.HasBlockingWarning || plan.Identity.Status != IdentityStatus.Resolved
            || plan.Writability is not (DirectoryWritability.Writable or DirectoryWritability.NeedsElevation)
            || plan.Elevation is not (ElevationRequirement.NotRequired or ElevationRequirement.Required)) throw new InvalidDataException("Unapproved or blocked deployment plan.");
        ElevationPolicy.ValidateTarget(game.DeployDirectory, game.InstallRoot);
        ElevationPolicy.ValidateTarget(request.BackupRoot, request.BackupRoot);
        ElevationPolicy.ValidateTarget(plan.Payload.RootDirectory, plan.Payload.RootDirectory);
        if (!Directory.Exists(game.DeployDirectory) || !Directory.Exists(plan.Payload.RootDirectory)) throw new InvalidDataException("Game or payload directory is missing.");
        if (string.IsNullOrEmpty(game.ExecutableName) || game.ExecutableName != Path.GetFileName(game.ExecutableName)
            || !game.ExecutableName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(Path.Combine(game.DeployDirectory, game.ExecutableName))) throw new InvalidDataException("Invalid game executable.");
        var identity = plan.Identity.Chosen == DllIdentity.Asi ? "OptiScaler.asi" : plan.Identity.Chosen.ToString().ToLowerInvariant() + ".dll";
        if (!Enum.IsDefined(plan.Identity.Chosen) || !plan.Identity.FileName.Equals(identity, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Invalid identity.");
        var fs = new FileSystem(); var store = new BackupStore(fs, request.BackupRoot);
        var backupDir = store.DirectoryFor(game.Fingerprint, plan.PlanId);
        foreach (var write in plan.Writes)
        {
            ElevationPolicy.ValidateTarget(write.DestinationPath, game.DeployDirectory);
            ElevationPolicy.ValidateTarget(write.DestinationPath + ".optideploy.tmp", game.DeployDirectory);
            ElevationPolicy.ValidateTarget(write.DestinationPath + ".optideploy.bak", game.DeployDirectory);
            ElevationPolicy.ValidateTarget(write.SourcePath, plan.Payload.RootDirectory);
            var relative = Path.GetRelativePath(plan.Payload.RootDirectory, write.SourcePath);
            var expected = Path.Combine(game.DeployDirectory, relative.Equals("OptiScaler.dll", StringComparison.OrdinalIgnoreCase) ? identity : relative);
            if (!Path.GetFullPath(write.DestinationPath).Equals(Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase)
                || (write.IsInline && !relative.Equals("OptiScaler.ini", StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("Write is not a payload deployment operation.");
            if (Path.GetFullPath(write.DestinationPath).Equals(Path.GetFullPath(Path.Combine(game.DeployDirectory, game.ExecutableName)), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Cannot replace game executable.");
        }
        foreach (var backup in plan.Backups)
        {
            ElevationPolicy.ValidateTarget(backup.OriginalPath, game.DeployDirectory);
            ElevationPolicy.ValidateTarget(backup.BackupPath, backupDir);
            var expected = Path.Combine(backupDir, Path.GetRelativePath(game.DeployDirectory, backup.OriginalPath));
            if (!Path.GetFullPath(backup.BackupPath).Equals(Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Backup path does not match the game file.");
        }
        ElevationPolicy.ValidateTarget(store.ManifestPathFor(game.Fingerprint, plan.PlanId), backupDir);
        ElevationPolicy.ValidateTarget(store.ProgressPathFor(game.Fingerprint, plan.PlanId), backupDir);
        foreach (var name in new[] { "manifest.json", "progress.json" })
        {
            ElevationPolicy.ValidateTarget(Path.Combine(backupDir, name + ".optideploy.tmp"), backupDir);
            ElevationPolicy.ValidateTarget(Path.Combine(backupDir, name + ".optideploy.bak"), backupDir);
        }
        if (Overlaps(request.BackupRoot, game.InstallRoot) || Overlaps(request.BackupRoot, plan.Payload.RootDirectory)
            || Overlaps(job.JobDirectory, game.InstallRoot) || Overlaps(job.JobDirectory, request.BackupRoot)
            || Overlaps(game.DeployDirectory, plan.Payload.RootDirectory)) throw new InvalidDataException("Deployment roots must not overlap.");
    }
    private static bool Overlaps(string left, string right)
    {
        var a = Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)); var b = Path.TrimEndingDirectorySeparator(Path.GetFullPath(right));
        return a.Equals(b, StringComparison.OrdinalIgnoreCase) || a.StartsWith(b + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || b.StartsWith(a + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}

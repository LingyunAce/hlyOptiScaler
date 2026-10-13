using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Deployer.Core.Serialization;

namespace Deployer.Core.Elevation;

public sealed record ElevationJob(Guid JobId, string PlanPath, string PlanSha256, string JobDirectory);
public sealed record ElevationResult(Guid JobId, bool Success, string Status, string? ManifestPath, IReadOnlyList<string> CompletedSteps, string? Error, bool Recoverable);
public interface IJobTransport { Task<ElevationResult> ExecuteAsync(ElevationJob job, CancellationToken cancellationToken); }
public sealed class FakeJobTransport(Func<ElevationJob, CancellationToken, Task<ElevationResult>> handler) : IJobTransport
{ public Task<ElevationResult> ExecuteAsync(ElevationJob job, CancellationToken ct) => handler(job, ct); }

public sealed class RunAsJobTransport(string workerPath, Func<ProcessStartInfo, Process?>? launch = null) : IJobTransport
{
    public async Task<ElevationResult> ExecuteAsync(ElevationJob job, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ElevationPolicy.ValidateJob(job);
        using var workerTrust = WorkerTrust.Acquire(workerPath);
        var request = ElevationJobStore.Read(job);
        ElevationWorker.ValidateRequest(job, request);
        var manifest = Path.Combine(request.BackupRoot, request.Plan.Game.Fingerprint, request.Plan.PlanId.ToString("D"), "manifest.json");
        var fullWorkerPath = Path.GetFullPath(workerPath);
        var psi = new ProcessStartInfo(fullWorkerPath, $"--job-dir \"{job.JobDirectory}\" --sha256 {job.PlanSha256}") { UseShellExecute = true, Verb = "runas", WorkingDirectory = Path.GetDirectoryName(fullWorkerPath)! };
        try
        {
            using var process = (launch ?? Process.Start)(psi) ?? throw new InvalidOperationException("Worker could not start.");
            using var cancel = ct.Register(() =>
            {
                try { var path = Path.Combine(job.JobDirectory, "cancel"); ElevationPolicy.RejectReparseAncestors(path); using var marker = new FileStream(path, FileMode.CreateNew); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // Always collect the writer's final outcome.
            });
            // Do not abandon a live elevated writer. It observes the cancel marker and reports recovery.
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            var resultPath = Path.Combine(job.JobDirectory, "result.json");
            ElevationPolicy.RejectReparseAncestors(resultPath);
            if (File.Exists(resultPath) && new FileInfo(resultPath).Length <= 1024 * 1024)
            {
                var result = DeployerJson.Deserialize<ElevationResult>(await File.ReadAllTextAsync(resultPath, CancellationToken.None).ConfigureAwait(false));
                if (result.JobId != job.JobId || (result.ManifestPath is not null && !string.IsNullOrEmpty(result.ManifestPath) && !Path.GetFullPath(result.ManifestPath).Equals(Path.GetFullPath(manifest), StringComparison.OrdinalIgnoreCase))
                    || result.CompletedSteps.Any(p => !request.Plan.Writes.Any(w => w.DestinationPath.Equals(p, StringComparison.OrdinalIgnoreCase)))) throw new InvalidDataException("Worker result does not match this job.");
                if (result.Success && (result.Status != "Completed" || !File.Exists(manifest))) throw new InvalidDataException("Worker success has no recovery manifest.");
                return result;
            }
            IReadOnlyList<string> steps = Array.Empty<string>();
            var usedLegacyProgress = false;
            var foundV1Manifest = false;
            try
            {
                ElevationPolicy.RejectReparseAncestors(manifest);
                if (File.Exists(manifest) && new FileInfo(manifest).Length <= 1024 * 1024)
                {
                    var saved = DeployerJson.Deserialize<Deployer.Core.Domain.DeploymentManifest>(File.ReadAllText(manifest));
                    if (saved.RecoveryStateVersion == 1)
                    {
                        foundV1Manifest = true;
                        var expectedWrites = request.Plan.Writes.Select(w => Path.GetFullPath(w.DestinationPath)).ToArray();
                        var savedWrites = saved.WrittenPaths.Select(Path.GetFullPath).ToArray();
                        if (saved.PlanId != request.Plan.PlanId || saved.Fingerprint != request.Plan.Game.Fingerprint
                            || !Path.GetFullPath(saved.DeployDirectory).Equals(Path.GetFullPath(request.Plan.Game.DeployDirectory), StringComparison.OrdinalIgnoreCase)
                            || savedWrites.Length > expectedWrites.Length
                            || !savedWrites.SequenceEqual(expectedWrites.Take(savedWrites.Length), StringComparer.OrdinalIgnoreCase))
                            throw new InvalidDataException("Worker manifest completed prefix does not match this job.");
                        steps = saved.WrittenPaths.ToArray();
                    }
                    else usedLegacyProgress = true;
                }
                else usedLegacyProgress = true;
            }
            catch { usedLegacyProgress = !foundV1Manifest; }
            if (usedLegacyProgress)
            {
                var progressPath = Path.Combine(Path.GetDirectoryName(manifest)!, "progress.json");
                try
                {
                    ElevationPolicy.RejectReparseAncestors(progressPath);
                    if (File.Exists(progressPath) && new FileInfo(progressPath).Length <= 1024 * 1024)
                        steps = DeployerJson.Deserialize<Deployer.Core.Domain.DeploymentProgress>(File.ReadAllText(progressPath)).CompletedSteps.ToArray();
                }
                catch { }
            }
            return new(job.JobId, false, "WorkerCrashed", manifest, steps, "Worker exited without result; inspect the recovery manifest.", true);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { return new(job.JobId, false, "Declined", null, Array.Empty<string>(), "UAC was declined.", true); }
    }
}

public static class ElevationPolicy
{
    public static void ValidateWorkerPath(string workerPath)
    {
        using var _ = WorkerTrust.Acquire(workerPath);
    }

    public static void ValidateJob(ElevationJob job)
    {
        ValidateJobDirectory(job);
        if (job.JobId == Guid.Empty || job.PlanSha256.Length != 64 || job.PlanSha256.Any(c => !Uri.IsHexDigit(c))) throw new InvalidDataException("Invalid job identity.");
        var dir = Path.GetFullPath(job.JobDirectory); var plan = Path.GetFullPath(job.PlanPath);
        if (!Directory.Exists(dir) || !string.Equals(Path.GetFileName(dir), job.JobId.ToString(), StringComparison.OrdinalIgnoreCase) || !plan.Equals(Path.Combine(dir, "plan.json"), StringComparison.OrdinalIgnoreCase) || !File.Exists(plan) || new FileInfo(plan).Length > 16 * 1024 * 1024) throw new InvalidDataException("Invalid job directory or plan path.");
        if (HasReparse(dir) || File.GetAttributes(plan).HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException("Reparse points are not allowed.");
        var actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(plan))).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(actual), Encoding.ASCII.GetBytes(job.PlanSha256.ToLowerInvariant()))) throw new InvalidDataException("Plan checksum mismatch.");
    }
    public static void ValidateTarget(string path, string root)
    {
        var full = Path.GetFullPath(path); var basePath = Path.GetFullPath(root);
        if (!Path.IsPathFullyQualified(path) || !Path.IsPathFullyQualified(root) || !IsContained(basePath, full)
            || full[2..].Contains(':') || full.Split(Path.DirectorySeparatorChar).Skip(1).Any(p => p.EndsWith('.') || p.EndsWith(' '))) throw new InvalidDataException("Target path escapes approved root or has an unsafe Windows name.");
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (full.Equals(windows, StringComparison.OrdinalIgnoreCase) || full.StartsWith(windows + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || full.Split(Path.DirectorySeparatorChar).Any(p => p.Equals("WindowsApps", StringComparison.OrdinalIgnoreCase)) || full.Equals(Path.GetPathRoot(full), StringComparison.OrdinalIgnoreCase) || full.StartsWith("\\\\", StringComparison.Ordinal)) throw new InvalidDataException("Protected or remote path.");
        RejectReparseAncestors(full);
    }
    public static void ValidateJobDirectory(ElevationJob job)
    {
        if (job.JobId == Guid.Empty || !Path.IsPathFullyQualified(job.JobDirectory) || !Directory.Exists(job.JobDirectory)
            || !Path.GetFileName(Path.TrimEndingDirectorySeparator(job.JobDirectory)).Equals(job.JobId.ToString("D"), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Invalid job directory.");
        RejectReparseAncestors(job.JobDirectory);
        foreach (var path in Directory.EnumerateFileSystemEntries(job.JobDirectory)) RejectReparseAncestors(path);
    }
    private static bool IsContained(string root, string child) => child.Equals(root, StringComparison.OrdinalIgnoreCase) || child.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    public static void RejectReparseAncestors(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            try { if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException("Reparse points are not allowed."); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            current = Path.GetDirectoryName(current);
        }
    }
    private static bool HasReparse(string path)
    {
        RejectReparseAncestors(path);
        foreach (var child in Directory.EnumerateFileSystemEntries(path))
        {
            if (File.GetAttributes(child).HasFlag(FileAttributes.ReparsePoint)) return true;
            if (Directory.Exists(child) && HasReparse(child)) return true;
        }
        return false;
    }
}

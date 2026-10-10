using Deployer.Core.Domain;

namespace Deployer.Core.Deployment;

public interface IBackupStore
{
    string DirectoryFor(string fingerprint, Guid planId);
    string ManifestPathFor(string fingerprint, Guid planId);
    string ProgressPathFor(string fingerprint, Guid planId);
    BackupEntry Backup(BackupEntry entry);
    void WriteManifest(DeploymentManifest manifest);
    DeploymentManifest? ReadManifest(string manifestPath);
    void WriteProgress(DeploymentProgress progress);
    DeploymentProgress? ReadProgress(string progressPath);
    IReadOnlyList<Guid> ListPlanIds(string fingerprint);
    IReadOnlyList<Guid> Prune(string fingerprint, int keep);
}

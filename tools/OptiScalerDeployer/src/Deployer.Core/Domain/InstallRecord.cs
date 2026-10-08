namespace Deployer.Core.Domain;

/// <summary>台账条目。ledger.json 只存这个索引，明细在各 PlanId 的 manifest.json 里。</summary>
public sealed record InstallRecord
{
    public required string Fingerprint { get; init; }
    public required string GameTitle { get; init; }
    public required string DeployDirectory { get; init; }
    public required Guid LatestPlanId { get; init; }
    public required string BackupRoot { get; init; }
    public required InstallState State { get; init; }
    public required string PayloadVersion { get; init; }
    public required string IdentityFileName { get; init; }
    public required DateTime InstalledUtc { get; init; }
}

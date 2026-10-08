namespace Deployer.Core.Domain;

public sealed record DeploymentPlan
{
    public required Guid PlanId { get; init; }
    public required GameTarget Game { get; init; }
    public required PayloadInfo Payload { get; init; }
    public required IdentityDecision Identity { get; init; }
    public required UpscalerProfile Profile { get; init; }
    public required DirectoryWritability Writability { get; init; }
    public required ElevationRequirement Elevation { get; init; }

    /// <summary>有序。身份 DLL 必须是最后一项（spec §5.4）。</summary>
    public required IReadOnlyList<FileWrite> Writes { get; init; }

    public required IReadOnlyList<BackupEntry> Backups { get; init; }
    public required IReadOnlyList<IniEdit> IniEdits { get; init; }
    public required IReadOnlyList<PlanWarning> Warnings { get; init; }

    public bool HasBlockingWarning =>
        Warnings.Any(w => w.Severity == WarningSeverity.Blocking);
}

public sealed record IdentityDecision(
    DllIdentity Chosen,
    string FileName,
    IdentityStatus Status,
    IReadOnlyList<DllIdentity> SkippedDueToConflict,
    string? UserPromptMessage);

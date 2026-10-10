using Deployer.Core.Domain;

namespace Deployer.Core.Planning;

public sealed record PlanRequest
{
    public required GameTarget Game { get; init; }
    public required InspectionSummary Inspection { get; init; }
    public required PayloadBundle Payload { get; init; }
    public required UpscalerProfile Profile { get; init; }
    public required DirectoryWritability Writability { get; init; }
    public required string BackupRoot { get; init; }
    /// <summary>Recommendations from a real inspection; empty keeps the Plan A/manual API compatible.</summary>
    public IReadOnlyList<IniEdit> RecommendedInputEdits { get; init; } = Array.Empty<IniEdit>();
    /// <summary>Inspection diagnostics propagated into the reviewed plan.</summary>
    public IReadOnlyList<PlanWarning> InspectionWarnings { get; init; } = Array.Empty<PlanWarning>();
    public bool EnableFrameGen { get; init; }
    public bool EnableLogging { get; init; } = true;
    public bool AllowOverwriteGameFile { get; init; }
    public bool PreferAsiIdentity { get; init; }
    public Guid? PlanId { get; init; }
}

public interface IDeploymentPlanner
{
    DeploymentPlan Plan(PlanRequest request);
}

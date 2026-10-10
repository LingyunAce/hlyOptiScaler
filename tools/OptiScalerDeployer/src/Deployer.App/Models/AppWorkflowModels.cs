using Deployer.Core.Domain;

namespace Deployer.App.Models;

public sealed record PlanBuildOptions(
    DiscoveredGame Game,
    string ExecutablePath,
    UpscalerProfile Profile,
    string BackupRoot,
    bool EnableFrameGen = false,
    bool AllowOverwriteGameFile = false,
    bool PreferAsiIdentity = false,
    string? PayloadDirectory = null,
    bool EnableLogging = true);

public sealed record PlanBuildResult(GameTarget Target, InspectionReport Inspection, DeploymentPlan Plan);

public sealed record ApplyWorkflowResult(
    bool Success,
    bool Cancelled,
    bool LedgerUpdated,
    string ManifestPath,
    IReadOnlyList<string> CompletedSteps,
    string? ErrorMessage,
    string? DiagnosticDetails);

public sealed record RollbackWorkflowResult(
    bool Success,
    bool LedgerUpdated,
    string ManifestPath,
    string? ErrorMessage,
    string? DiagnosticDetails);

public sealed record HistoryItem(DeploymentManifest Manifest, string ManifestPath, InstallState? State, bool IsLatest);

public sealed record ProfileCatalog(IReadOnlyList<UpscalerProfile> Profiles, string? LoadError);

public sealed record OperationFailure(string Message, string? SuggestedAction = null, string? DiagnosticDetails = null);

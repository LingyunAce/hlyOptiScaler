using Deployer.App.Models;
using Deployer.Core.Domain;

namespace Deployer.App.Services;

/// <summary>Single test seam for all effects used by the desktop workflow.</summary>
public interface IDeployerAppService
{
    IReadOnlyList<string> Warnings { get; }
    void ConfigureStoragePaths(string backupRoot, string ledgerPath, string profilesPath);
    Task<IReadOnlyList<DiscoveredGame>> GetGamesAsync(bool refresh, CancellationToken cancellationToken = default);
    Task AddManualFolderAsync(string title, string folder, CancellationToken cancellationToken = default);
    Task RemoveManualFolderAsync(string folder, CancellationToken cancellationToken = default);
    Task<PlanBuildResult> BuildPlanAsync(PlanBuildOptions options, CancellationToken cancellationToken = default);
    Task<ApplyWorkflowResult> ApplyAsync(DeploymentPlan plan, bool reviewAcknowledged, CancellationToken cancellationToken = default);
    Task<Deployer.Core.HealthCheck.HealthCheckResult> CheckHealthAsync(DeploymentPlan plan, string expectedBackend, bool launchGame, CancellationToken cancellationToken = default);
    Task<RollbackWorkflowResult> RollbackAsync(string manifestPath, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<HistoryItem>> GetHistoryAsync(CancellationToken cancellationToken = default);
    Task<ProfileCatalog> GetProfilesAsync(CancellationToken cancellationToken = default);
    Task SaveProfileAsync(UpscalerProfile profile, CancellationToken cancellationToken = default);
    Task<bool> DeleteProfileAsync(string id, CancellationToken cancellationToken = default);
}

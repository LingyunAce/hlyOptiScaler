using Deployer.Core.Domain;

namespace Deployer.Core.Deployment;

public sealed record DeploymentResult(
    bool Success,
    string ManifestPath,
    IReadOnlyList<string> CompletedSteps,
    string? ErrorMessage,
    bool RolledBack);

public interface IDeploymentExecutor
{
    DeploymentResult Execute(DeploymentPlan plan, CancellationToken ct);
}

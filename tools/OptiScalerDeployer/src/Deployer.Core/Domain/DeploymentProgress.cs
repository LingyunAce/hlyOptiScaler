namespace Deployer.Core.Domain;

/// <summary>执行期进度镜像。新清单的恢复状态以 manifest 为准，避免两个文件的非原子同步导致误判。</summary>
public sealed record DeploymentProgress
{
    public required Guid PlanId { get; init; }
    public required string ManifestPath { get; init; }
    public required IReadOnlyList<string> CompletedSteps { get; init; }
    public PendingDeploymentWrite? PendingWrite { get; init; }

    public static DeploymentProgress Empty(Guid planId, string manifestPath) =>
        new() { PlanId = planId, ManifestPath = manifestPath, CompletedSteps = Array.Empty<string>() };
}

public sealed record RollbackResult(
    bool Success,
    IReadOnlyList<string> RestoredPaths,
    IReadOnlyList<string> DeletedPaths,
    string? ErrorMessage);

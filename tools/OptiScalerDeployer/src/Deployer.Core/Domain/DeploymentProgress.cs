namespace Deployer.Core.Domain;

/// <summary>
/// 执行期落盘的进度。每完成一步就重写一次，
/// 进程被强杀后 RollbackExecutor 据此接管（spec §5.5）。
/// </summary>
public sealed record DeploymentProgress
{
    public required Guid PlanId { get; init; }
    public required string ManifestPath { get; init; }
    public required IReadOnlyList<string> CompletedSteps { get; init; }

    public static DeploymentProgress Empty(Guid planId, string manifestPath) =>
        new() { PlanId = planId, ManifestPath = manifestPath, CompletedSteps = Array.Empty<string>() };
}

public sealed record RollbackResult(
    bool Success,
    IReadOnlyList<string> RestoredPaths,
    IReadOnlyList<string> DeletedPaths,
    string? ErrorMessage);

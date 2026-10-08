namespace Deployer.Core.Domain;

/// <summary>
/// 影子备份目录内的 manifest.json。RollbackExecutor 只认这个文件，
/// 绝不扫描目录猜测该删什么。
/// </summary>
public sealed record DeploymentManifest
{
    public required Guid PlanId { get; init; }
    public required DateTime CreatedUtc { get; init; }
    public required string Fingerprint { get; init; }
    public required string GameTitle { get; init; }
    public required string DeployDirectory { get; init; }
    public required PayloadInfo Payload { get; init; }
    public required IdentityDecision Identity { get; init; }
    public required UpscalerProfile Profile { get; init; }
    public required IReadOnlyList<BackupEntry> Backups { get; init; }

    /// <summary>本次新增/覆盖的全部目标路径。无对应 BackupEntry 的即为「纯新增」，回滚时直接删。</summary>
    public required IReadOnlyList<string> WrittenPaths { get; init; }
}

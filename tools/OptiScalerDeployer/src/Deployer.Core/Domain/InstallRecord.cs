namespace Deployer.Core.Domain;

/// <summary>台账条目。ledger.json 只存这个索引，明细在各 PlanId 的 manifest.json 里。</summary>
/// <remarks>
/// 与 <see cref="DeploymentManifest"/> 同属跨版本长寿命持久化类型，适用同一条约定：
/// v1 发布之后新增字段必须可空或带默认值，禁止 <c>required</c>，
/// 否则旧 ledger.json 在新版工具下会反序列化失败。
/// </remarks>
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

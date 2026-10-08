namespace Deployer.Core.Domain;

/// <summary>
/// 影子备份目录内的 manifest.json。RollbackExecutor 只认这个文件，
/// 绝不扫描目录猜测该删什么。
/// </summary>
/// <remarks>
/// 本类型跨版本长寿命持久化：安装时写盘，可能数月后被更高版本的工具读回，
/// 而「任何时候都能回滚」正是本工具的核心承诺。System.Text.Json 在反序列化
/// 缺失 <c>required</c> 成员的 JSON 时会抛异常——一旦某次升级给本类型加了
/// <c>required</c> 字段，磁盘上旧版本写下的 manifest 就会读不回来，用户将
/// 无法回滚当初装过的游戏。因此 v1 发布之后，对本类型的任何新增字段
/// **必须是可空类型或带默认值**，禁止使用 <c>required</c>；
/// 若确实需要破坏性变更，先引入 SchemaVersion 并写兼容分支。
/// </remarks>
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

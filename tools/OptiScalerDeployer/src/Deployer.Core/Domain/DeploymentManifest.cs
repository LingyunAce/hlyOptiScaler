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

    /// <summary>Recovery contract 1 下已经完成的连续写入前缀；旧清单中表示其记录的写入路径。</summary>
    public required IReadOnlyList<string> WrittenPaths { get; init; }

    /// <summary>Recovery contract 1: completed prefix is authoritative; this optional write is write-ahead intent.</summary>
    public int RecoveryStateVersion { get; init; }
    public PendingDeploymentWrite? PendingWrite { get; init; }

    /// <summary>部署期间新建的目录，回滚时仅在目录为空时删除；缺失表示旧版清单未记录目录。</summary>
    public IReadOnlyList<string> CreatedDirectories { get; init; } = Array.Empty<string>();
}

public sealed record PendingDeploymentWrite(string Path, string Sha256);

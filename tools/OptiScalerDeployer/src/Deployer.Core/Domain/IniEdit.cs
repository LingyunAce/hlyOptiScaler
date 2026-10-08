namespace Deployer.Core.Domain;

/// <summary>
/// 一条 INI 覆盖。OldValue 在 planner 阶段为 null（此时还没读文件），
/// 由 IniPatcher 落地时回填实际旧值，供 UI 预演 diff。
/// </summary>
public sealed record IniEdit(string Section, string Key, string? OldValue, string NewValue)
{
    public IniEdit WithOldValue(string? oldValue) => this with { OldValue = oldValue };
}

/// <summary>一次文件写入。DestinationPath 为绝对路径。</summary>
public sealed record FileWrite(string SourcePath, string DestinationPath, long Length, string Sha256);

/// <summary>
/// 一条影子备份。Sha256 在 planner 阶段为 null（planner 不哈希游戏自带文件——太慢，
/// 也不是它的职责），由 BackupStore.Backup 在真正拷贝的那一刻回填。与 IniEdit.OldValue
/// 是同一个模式：计划期不可知的值，落地期补上。
/// </summary>
/// <remarks>
/// 为什么**可空**而不是 required：本 record 会进 manifest.json 长期落盘，受
/// DeploymentManifest 的前向兼容约定管辖；且哈希在计划阶段确实不可知。
///
/// 为什么值得记：回滚时它是唯一的**独立**参照物。备份件在存放期间可能被磁盘错误、
/// 杀软或云占位符水合失败改动过；没有哈希就只能盲拷回游戏目录并宣称回滚成功——
/// 那正是本工具承诺绝不发生的事。注意这**不是**「校验它等于校验自己」：哈希是备份
/// 当时记下的，与回滚那一刻的磁盘状态分属两个时间点，相互独立。
/// </remarks>
public sealed record BackupEntry(string OriginalPath, string BackupPath, BackupReason Reason, string? Sha256 = null)
{
    public BackupEntry WithSha256(string? sha256) => this with { Sha256 = sha256 };
}

public sealed record PlanWarning(WarningSeverity Severity, string Message);

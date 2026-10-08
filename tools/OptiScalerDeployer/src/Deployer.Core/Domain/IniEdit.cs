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

public sealed record BackupEntry(string OriginalPath, string BackupPath, BackupReason Reason);

public sealed record PlanWarning(WarningSeverity Severity, string Message);

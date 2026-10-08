namespace Deployer.Core.Errors;

/// <summary>所有 Deployer 异常的基类。</summary>
public abstract class DeployerException : Exception
{
    protected DeployerException(string userMessage, string diagnostic, Exception? inner = null)
        : base(diagnostic, inner)
    {
        UserMessage = userMessage;
        Diagnostic = diagnostic;
    }

    /// <summary>给用户看的一句中文话，不含路径细节以外的技术黑话。</summary>
    public string UserMessage { get; }

    /// <summary>给日志看的技术细节。</summary>
    public string Diagnostic { get; }
}

public sealed class PayloadNotFoundException : DeployerException
{
    public PayloadNotFoundException(string diagnostic)
        : base("找不到 OptiScaler 产物。请先用 VS2022 构建一次 OptiScaler.sln，或在设置里手动指定产物目录。",
               diagnostic) { }
}

public sealed class GameNotWritableException : DeployerException
{
    public GameNotWritableException(string path, string diagnostic)
        : base($"游戏目录不可写：{path}", diagnostic) => Path = path;

    public string Path { get; }
}

public sealed class IdentityConflictException : DeployerException
{
    public IdentityConflictException(string deployDirectory, string diagnostic)
        : base($"所有候选 DLL 身份都与 {deployDirectory} 里的游戏自带文件冲突，需要你决定如何处理。",
               diagnostic) { }
}

public sealed class ChecksumMismatchException : DeployerException
{
    public ChecksumMismatchException(string path, string expected, string actual)
        : base($"文件校验失败：{path}。可能是磁盘空间不足或杀毒软件拦截。",
               $"expected SHA256 {expected}, actual {actual}, path {path}")
    {
        Path = path;
        Expected = expected;
        Actual = actual;
    }

    public string Path { get; }
    public string Expected { get; }
    public string Actual { get; }
}

public sealed class ElevationDeclinedException : DeployerException
{
    public ElevationDeclinedException(string diagnostic)
        : base("你取消了管理员授权。该游戏位于受系统保护的 WindowsApps 目录，不提权无法部署。",
               diagnostic) { }
}

public sealed class HealthCheckFailedException : DeployerException
{
    public HealthCheckFailedException(string logExcerpt)
        : base("OptiScaler 启动后报告了错误。日志摘要已保留，可一键回滚。", logExcerpt)
        => LogExcerpt = logExcerpt;

    public string LogExcerpt { get; }
}

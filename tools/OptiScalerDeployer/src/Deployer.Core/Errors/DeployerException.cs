namespace Deployer.Core.Errors;

/// <summary>所有 Deployer 异常的基类。</summary>
public abstract class DeployerException : Exception
{
    protected DeployerException(string userMessage, string diagnostic, Exception? inner = null)
        : base(userMessage, inner)
    {
        UserMessage = userMessage;
        Diagnostic = diagnostic;
    }

    /// <summary>给用户看的一句中文话，不含路径细节以外的技术黑话。</summary>
    public string UserMessage { get; }

    /// <summary>给日志看的技术细节。</summary>
    public string Diagnostic { get; }

    /// <summary>
    /// <see cref="Exception.Message"/> 只带用户话术，所以这里把 <see cref="Diagnostic"/>
    /// 追加回来，保证日志与崩溃转储里技术细节不丢。
    /// </summary>
    public override string ToString() =>
        $"{base.ToString()}{Environment.NewLine}诊断：{Diagnostic}";
}

public sealed class PayloadNotFoundException : DeployerException
{
    public PayloadNotFoundException(string diagnostic)
        : base("无法获取 OptiScaler 文件。请检查网络后重试，或在设置中指定包含 OptiScaler.dll 和 OptiScaler.ini 的官方下载解压目录。",
               diagnostic) { }
}

/// <remarks>
/// 可写性在计划 A 里是**值**不是异常：<c>DirectoryWritability</c> 枚举 → <c>ElevationRequirement</c>
/// → <c>PlanWarning</c>，探测结果一路带到 UI。本类型只保留给执行阶段**真的**写失败
/// （已提权仍被拒、目录被独占等）。
/// </remarks>
public sealed class GameNotWritableException : DeployerException
{
    public GameNotWritableException(string path, string diagnostic)
        : base($"游戏目录不可写：{path}", diagnostic) => FilePath = path;

    public string FilePath { get; }
}

/// <remarks>
/// 计划 A/B **不会**抛本异常。「全部候选身份都冲突」是以值的形式流转的：
/// <c>DllIdentitySelector.Select</c> 返回 <c>IdentityStatus.RequiresUserDecision</c> 加一段
/// <c>UserPromptMessage</c>，planner 把它转成 Caution 级 <c>PlanWarning</c>，UI 再给用户三个选项。
/// 只有计划 C 需要表达「三个选项用户全拒了」时才考虑本类型，且要先确认不与上述值路径重复。
/// </remarks>
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
        FilePath = path;
        Expected = expected;
        Actual = actual;
    }

    public string FilePath { get; }
    public string Expected { get; }
    public string Actual { get; }
}

/// <remarks>
/// 计划 D 专用。**用户取消 UAC 时绝不能抛本异常** —— spec §10 的失败点表明写「UAC 被拒
/// **不算错误**」，那条路径要把结果记成 <c>ElevationRequirement.Declined</c>，并说明
/// 「该游戏在受保护的 WindowsApps 里」；抛异常会让 executor 报 <c>Success=false</c>，正是 spec 禁止的。
/// 本类型只留给提权**通道本身**失败的情形：runas 起不来、Worker 崩溃等。
/// </remarks>
public sealed class ElevationDeclinedException : DeployerException
{
    public ElevationDeclinedException(string diagnostic)
        : base("你取消了管理员授权。该游戏位于受系统保护的 WindowsApps 目录，不提权无法部署。",
               diagnostic) { }
}

/// <remarks>
/// 计划 D 专用，由 <c>IHealthCheckRunner</c> 抛出。<see cref="LogExcerpt"/> 带着
/// <c>OptiScaler.log</c> 的摘要，UI 把它显示在「一键回滚」按钮旁边（spec §10）。
/// </remarks>
public sealed class HealthCheckFailedException : DeployerException
{
    public HealthCheckFailedException(string logExcerpt)
        : base("OptiScaler 启动后报告了错误。日志摘要已保留，可一键回滚。", logExcerpt)
        => LogExcerpt = logExcerpt;

    public string LogExcerpt { get; }
}

using Deployer.Core.Errors;
using Xunit;

namespace Deployer.Core.Tests;

/// <remarks>
/// 本文件钉住 <c>DeployerException</c> **基类**的两条契约，而不是某个具体异常的语义：
/// ① <c>base(userMessage, inner)</c> 让 <c>Exception.Message</c> 承载用户话术；
/// ② <c>ToString()</c> 把 <c>Diagnostic</c> 追加回来，日志与崩溃转储里技术细节不丢。
///
/// 为什么值得单独一个文件：这两条被全部 6 个具体异常继承，在基类测一次杠杆最高。
/// 而在补上本测试之前它们的覆盖面是**零**——把 <c>base(userMessage, inner)</c> 改回
/// <c>base(diagnostic, inner)</c>，原有 30 个测试全绿（已由两位复核者独立实证）。
/// 这不是理论风险：Task 15 的 CLI 顶层就是 <c>Fail(ex.Message)</c>，一旦 <c>Message</c>
/// 退化成裸诊断串，用户就看不到 spec §10 承诺的那两条出路。
///
/// **护栏**：把基类的 <c>: base(userMessage, inner)</c> 改成 <c>: base(diagnostic, inner)</c>，
/// 或把 <c>ToString()</c> 简化成 <c>base.ToString()</c>，本测试会**变红**。
/// 觉得它多余想删之前，请先亲手做一次上述变异。
/// </remarks>
public class DeployerExceptionTests
{
    // 一眼可辨的假诊断串：必须与用户话术毫不重叠，才能证明两者确实分流了
    private const string FakeDiagnostic = "payloadRoot=D:\\repo\\x64\\Release\\a not found";

    [Fact]
    public void Message_carries_the_user_guidance_not_the_diagnostic()
    {
        var ex = new PayloadNotFoundException(FakeDiagnostic);

        // 这一条正好杀死 base(diagnostic, inner) 那个变异体
        Assert.Equal(ex.UserMessage, ex.Message);

        // 用户话术还得是**可操作**的指引：告诉用户怎么修，而不是甩一串技术黑话
        Assert.Contains("官方下载解压目录", ex.Message);
        Assert.Contains("OptiScaler.dll", ex.Message);
        Assert.DoesNotContain("VS2022", ex.Message);
        Assert.DoesNotContain(FakeDiagnostic, ex.Message);
    }

    [Fact]
    public void ToString_appends_the_diagnostic()
    {
        var ex = new PayloadNotFoundException(FakeDiagnostic);

        Assert.Contains(ex.Diagnostic, ex.ToString());
    }

    [Fact]
    public void InnerException_is_null()
    {
        // 现有 6 个具体异常都不传 inner；基类的 inner 参数留给 Task 10/11 的包装场景
        var ex = new PayloadNotFoundException(FakeDiagnostic);

        Assert.Null(ex.InnerException);
    }
}

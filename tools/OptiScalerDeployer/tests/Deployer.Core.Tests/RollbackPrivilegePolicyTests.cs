using Deployer.Core.Deployment;
using Xunit;

namespace Deployer.Core.Tests;

public sealed class RollbackPrivilegePolicyTests
{
    [Fact]
    public void Elevated_administrator_is_rejected_with_actionable_message()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => RollbackPrivilegePolicy.EnsureAllowed(isWindows: true, isElevatedAdministrator: true));

        Assert.Contains("管理员权限", error.Message);
        Assert.Contains("普通用户", error.Message);
    }

    [Fact]
    public void Non_elevated_process_is_allowed()
    {
        RollbackPrivilegePolicy.EnsureAllowed(isWindows: true, isElevatedAdministrator: false);
    }

    [Fact]
    public void Unsupported_platform_is_rejected_in_a_controlled_way()
    {
        var error = Assert.Throws<PlatformNotSupportedException>(
            () => RollbackPrivilegePolicy.EnsureAllowed(isWindows: false, isElevatedAdministrator: false));

        Assert.Contains("无法安全确认", error.Message);
    }
}

using System.Runtime.CompilerServices;
using System.Security;
using System.Security.Principal;

[assembly: InternalsVisibleTo("Deployer.Core.Tests")]

namespace Deployer.Core.Deployment;

/// <summary>
/// Prevents path-based rollback while the current process has an elevated administrator token.
/// </summary>
public static class RollbackPrivilegePolicy
{
    private const string ElevatedMessage =
        "已拒绝在管理员权限进程中执行路径型回滚。为避免 junction 或符号链接让恢复写入越出目标目录，请关闭管理员终端/程序并以普通用户启动后重试。若普通权限无法恢复，请保留清单与备份；本版本暂不支持安全的管理员恢复。";

    private const string UnsupportedMessage =
        "当前平台无法安全确认 Windows 管理员令牌状态，已拒绝执行路径型回滚。请在受支持的 Windows 普通用户会话中运行。";

    /// <summary>
    /// Fails closed unless the current Windows process is not running with an elevated
    /// administrator token. Call before opening a recovery manifest or touching files.
    /// </summary>
    public static void EnsureAllowedForCurrentProcess()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(UnsupportedMessage);

        bool isElevatedAdministrator;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            isElevatedAdministrator = new WindowsPrincipal(identity)
                .IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException
                                   or PlatformNotSupportedException or System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException(
                $"{UnsupportedMessage} 无法读取当前令牌：{ex.Message}", ex);
        }

        EnsureAllowed(OperatingSystem.IsWindows(), isElevatedAdministrator);
    }

    // Isolated deterministic seam for policy tests. Production callers must use the
    // parameterless method above so the decision always comes from the real process token.
    internal static void EnsureAllowed(bool isWindows, bool isElevatedAdministrator)
    {
        if (!isWindows)
            throw new PlatformNotSupportedException(UnsupportedMessage);

        if (isElevatedAdministrator)
            throw new InvalidOperationException(ElevatedMessage);
    }
}

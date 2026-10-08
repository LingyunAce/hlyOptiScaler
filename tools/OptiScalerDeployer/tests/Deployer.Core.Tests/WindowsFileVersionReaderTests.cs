using Deployer.Core.Abstractions;
using Xunit;

namespace Deployer.Core.Tests;

// 本文件是整套测试里唯一碰真文件系统的，这是正确且无法回避的：FileVersionInfo 走的是
// Win32 版本资源 API，作用不到 MockFileSystem 上 —— 这正是 IFileVersionReader 存在的理由。
// 只测否定路径（仓库里没有 PE 二进制夹具，正向路径无从造起）。否定路径是确定性的，
// 也恰好就是任务 7 保守分支依赖的契约：「读不出来就返回 null、绝不抛」→ 该槽位被当作
// 游戏自带文件，宁可收进影子备份，也不静默覆盖。
public class WindowsFileVersionReaderTests
{
    private readonly WindowsFileVersionReader reader = new();

    [Fact]
    public void Missing_file_returns_null_instead_of_throwing()
    {
        // 目录本身也不存在，因此 File.Exists 必为 false
        var missing = Path.Combine(Path.GetTempPath(), "opti-deployer-tests-no-such-dir", "no-such-file.dll");

        Assert.Null(reader.GetOriginalFilename(missing));
        Assert.Null(reader.GetFileVersion(missing));
    }

    [Fact]
    public void Non_PE_file_returns_null_instead_of_throwing()
    {
        var notAPe = Path.Combine(Path.GetTempPath(), $"opti-deployer-tests-{Guid.NewGuid():N}.txt");

        try
        {
            File.WriteAllText(notAPe, "这不是 PE 文件，只是一段普通文本。");

            Assert.Null(reader.GetOriginalFilename(notAPe));
            Assert.Null(reader.GetFileVersion(notAPe));
        }
        finally
        {
            // 断言失败也要清干净，别在 temp 里留垃圾
            File.Delete(notAPe);
        }
    }
}

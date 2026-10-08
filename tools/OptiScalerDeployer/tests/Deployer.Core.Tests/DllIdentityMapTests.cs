using Deployer.Core.Domain;
using Xunit;

namespace Deployer.Core.Tests;

public class DllIdentityMapTests
{
    [Theory]
    [InlineData(DllIdentity.Dxgi, "dxgi.dll")]
    [InlineData(DllIdentity.Winmm, "winmm.dll")]
    [InlineData(DllIdentity.Version, "version.dll")]
    [InlineData(DllIdentity.Dbghelp, "dbghelp.dll")]
    [InlineData(DllIdentity.D3d12, "d3d12.dll")]
    [InlineData(DllIdentity.Wininet, "wininet.dll")]
    [InlineData(DllIdentity.Winhttp, "winhttp.dll")]
    [InlineData(DllIdentity.Asi, "OptiScaler.asi")]
    public void FileName_matches_optiscaler_convention(DllIdentity identity, string expected)
    {
        Assert.Equal(expected, identity.FileName());
    }

    [Fact]
    public void FileName_covers_every_enum_member()
    {
        foreach (var identity in Enum.GetValues<DllIdentity>())
        {
            Assert.False(string.IsNullOrWhiteSpace(identity.FileName()), $"{identity} 缺少文件名映射");
        }
    }
}

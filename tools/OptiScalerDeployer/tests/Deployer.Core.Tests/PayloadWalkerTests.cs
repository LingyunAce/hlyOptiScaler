using System.IO.Abstractions.TestingHelpers;
using Deployer.Core.Planning;
using Deployer.TestData;
using Xunit;

namespace Deployer.Core.Tests;

public class PayloadWalkerTests
{
    private const string Root = @"D:\repo\x64\Release\a";

    private static (PayloadWalker Walker, MockFileSystem FileSystem) Build()
    {
        var fs = new MockFileSystem();
        SyntheticPayloadFactory.Create(fs, Root);
        return (new PayloadWalker(fs), fs);
    }

    [Fact]
    public void Skips_installer_artifacts_and_root_dll_ini()
    {
        var (walker, _) = Build();
        var paths = walker.Walk(Root).Select(file => file.RelativePath).ToArray();
        foreach (var excluded in SyntheticPayloadFactory.SkippedFileNames.Append("OptiScaler.dll").Append("OptiScaler.ini"))
        {
            Assert.DoesNotContain(paths, path => string.Equals(path, excluded, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void Keeps_runtime_and_both_root_and_nested_licenses()
    {
        var (walker, _) = Build();
        var paths = walker.Walk(Root).Select(file => file.RelativePath).ToArray();
        Assert.Contains(@"OptiScaler\amdxc64.dll", paths);
        Assert.Contains(@"OptiScaler\D3D12_OptiScaler\d3d12core.dll", paths);
        Assert.Contains(@"OptiScaler\Licenses\FidelityFX_v1_LICENSE.md", paths);
        Assert.Contains(@"Licenses\FidelityFX_v2_LICENSE.md", paths);
    }

    [Fact]
    public void Orders_runtime_before_root_licenses_before_other_files()
    {
        var fs = new MockFileSystem();
        SyntheticPayloadFactory.Create(fs, Root);
        fs.AddFile($@"{Root}\Extra\readme.txt", new MockFileData("extra"));
        var paths = new PayloadWalker(fs).Walk(Root).Select(file => file.RelativePath).ToArray();
        var lastRuntime = Array.FindLastIndex(paths, path => path.StartsWith("OptiScaler\\", StringComparison.OrdinalIgnoreCase));
        var firstLicense = Array.FindIndex(paths, path => path.StartsWith("Licenses\\", StringComparison.OrdinalIgnoreCase));
        var extra = Array.FindIndex(paths, path => path.StartsWith("Extra\\", StringComparison.OrdinalIgnoreCase));
        Assert.True(lastRuntime >= 0 && lastRuntime < firstLicense && firstLicense < extra);
    }

    [Fact]
    public void Populates_length_and_content_sha256()
    {
        var (walker, fs) = Build();
        var file = walker.Walk(Root).Single(entry => entry.RelativePath == @"OptiScaler\amdxc64.dll");
        Assert.True(file.Length > 0);
        Assert.Matches("^[0-9a-f]{64}$", file.Sha256);
        Assert.Equal(file.Sha256, Deployer.Core.Util.Hashing.Sha256OfFile(fs, file.FullPath));
    }

    [Fact]
    public void Ordering_is_deterministic_across_calls()
    {
        var (walker, _) = Build();
        Assert.Equal(walker.Walk(Root), walker.Walk(Root));
    }

    [Fact]
    public void Missing_root_throws_payload_not_found()
    {
        var walker = new PayloadWalker(new MockFileSystem());
        Assert.Throws<Deployer.Core.Errors.PayloadNotFoundException>(() => walker.Walk(@"D:\missing"));
    }
}

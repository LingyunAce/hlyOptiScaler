using System.IO.Abstractions.TestingHelpers;
using Deployer.Core.Domain;
using Deployer.Core.Payload;
using Deployer.TestData;
using Xunit;

namespace Deployer.Core.Tests;

public class LocalBuildPayloadSourceTests
{
    private const string RepoRoot = @"D:\repo\hlyOptiScaler";
    private static readonly string PayloadRoot = $@"{RepoRoot}\x64\Release\a";

    private static LocalBuildPayloadSource Build(bool withPayload)
    {
        var fs = new MockFileSystem();
        if (withPayload)
        {
            SyntheticPayloadFactory.Create(fs, PayloadRoot);
        }

        var versions = withPayload
            ? SyntheticPayloadFactory.CreateVersionReader(PayloadRoot, fs)
            : new FakeFileVersionReader();
        return new LocalBuildPayloadSource(RepoRoot, fs, versions);
    }

    [Fact]
    public async Task Returns_bundle_when_local_build_exists()
    {
        var source = Build(withPayload: true);

        var bundle = await source.TryGetAsync(CancellationToken.None);

        Assert.NotNull(bundle);
        Assert.Equal(PayloadRoot, bundle!.RootDirectory);
        Assert.Equal(PayloadOrigin.LocalBuild, bundle.Origin);
        Assert.Equal(SyntheticPayloadFactory.DefaultVersion, bundle.Version);
    }

    [Fact]
    public async Task Returns_null_when_release_folder_missing() =>
        Assert.Null(await Build(withPayload: false).TryGetAsync(CancellationToken.None));

    [Fact]
    public async Task Returns_null_when_optiscaler_dll_missing()
    {
        var fs = new MockFileSystem();
        SyntheticPayloadFactory.Create(fs, PayloadRoot);
        fs.File.Delete($@"{PayloadRoot}\OptiScaler.dll");
        var source = new LocalBuildPayloadSource(RepoRoot, fs, new FakeFileVersionReader());

        Assert.Null(await source.TryGetAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Returns_null_when_ini_missing()
    {
        var fs = new MockFileSystem();
        SyntheticPayloadFactory.Create(fs, PayloadRoot);
        fs.File.Delete($@"{PayloadRoot}\OptiScaler.ini");
        var source = new LocalBuildPayloadSource(RepoRoot, fs, new FakeFileVersionReader());

        Assert.Null(await source.TryGetAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Falls_back_to_unknown_version_when_dll_has_no_version_resource()
    {
        var fs = new MockFileSystem();
        SyntheticPayloadFactory.Create(fs, PayloadRoot);
        var source = new LocalBuildPayloadSource(RepoRoot, fs, new FakeFileVersionReader());

        var bundle = await source.TryGetAsync(CancellationToken.None);

        Assert.Equal("unknown", bundle!.Version);
    }

    [Fact]
    public void Description_mentions_the_expected_path() =>
        Assert.Contains("x64", Build(withPayload: false).Description, StringComparison.OrdinalIgnoreCase);

    [Fact]
    public async Task Honors_cancellation_before_inspecting_the_filesystem()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Build(withPayload: false).TryGetAsync(cts.Token));
    }
}

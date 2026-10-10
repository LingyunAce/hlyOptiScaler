using Deployer.Core.Domain;
using Deployer.Core.Errors;
using Deployer.Core.Payload;
using System.IO.Abstractions.TestingHelpers;
using Deployer.TestData;
using Xunit;

namespace Deployer.Core.Tests;

public class PayloadSourceChainTests
{
    private sealed class StubSource(string description, PayloadBundle? result) : IPayloadSource
    {
        public string Description { get; } = description;
        public PayloadBundle? Result { get; } = result;
        public int Calls { get; private set; }

        public Task<PayloadBundle?> TryGetAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(Result);
        }
    }

    private sealed class ThrowingSource(string description) : IPayloadSource
    {
        public string Description { get; } = description;

        public Task<PayloadBundle?> TryGetAsync(CancellationToken ct) =>
            Task.FromException<PayloadBundle?>(new IOException("source read failed"));
    }

    private static PayloadBundle Bundle(string root) => new(root, "1.0", PayloadOrigin.LocalBuild);

    [Fact]
    public async Task First_available_source_wins_and_later_ones_are_not_called()
    {
        var first = new StubSource("A", null);
        var second = new StubSource("B", Bundle(@"C:\b"));
        var third = new StubSource("C", Bundle(@"C:\c"));
        var chain = new PayloadSourceChain(new IPayloadSource[] { first, second, third });

        var bundle = await chain.GetOrThrowAsync(CancellationToken.None);

        Assert.Equal(@"C:\b", bundle.RootDirectory);
        Assert.Equal(1, first.Calls);
        Assert.Equal(1, second.Calls);
        Assert.Equal(0, third.Calls);
    }

    [Fact]
    public async Task GetOrThrow_lists_every_source_in_the_diagnostic()
    {
        var chain = new PayloadSourceChain(new IPayloadSource[]
        {
            new StubSource("本地构建 D:\\repo\\x64\\Release\\a", null),
            new StubSource("手动指定目录 D:\\downloads\\opti", null),
        });

        var ex = await Assert.ThrowsAsync<PayloadNotFoundException>(
            () => chain.GetOrThrowAsync(CancellationToken.None));

        Assert.Contains("本地构建", ex.Diagnostic);
        Assert.Contains("手动指定", ex.Diagnostic);
        Assert.Contains("VS2022", ex.UserMessage);
    }

    [Fact]
    public async Task TryGetAsync_returns_null_instead_of_throwing()
    {
        var chain = new PayloadSourceChain(new IPayloadSource[] { new StubSource("A", null) });

        Assert.Null(await chain.TryGetAsync(CancellationToken.None));
    }

    [Fact]
    public void Empty_chain_is_rejected_at_construction() =>
        Assert.Throws<ArgumentException>(() => new PayloadSourceChain(Array.Empty<IPayloadSource>()));

    [Fact]
    public void Description_joins_sources_with_arrow()
    {
        var chain = new PayloadSourceChain(new IPayloadSource[]
        {
            new StubSource("A", null), new StubSource("B", null),
        });

        Assert.Equal("A → B", chain.Description);
    }

    [Fact]
    public async Task Cancellation_propagates_without_trying_any_source()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var first = new StubSource("A", null);
        var chain = new PayloadSourceChain(new IPayloadSource[] { first });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => chain.TryGetAsync(cts.Token));
        Assert.Equal(0, first.Calls);
    }

    [Fact]
    public async Task Local_build_precedes_manual_directory_and_manual_directory_is_an_arbitrary_path()
    {
        const string repoRoot = @"D:\repo";
        const string manualRoot = @"E:\user selected\download";
        var fs = new MockFileSystem();
        SyntheticPayloadFactory.Create(fs, manualRoot);
        var local = new LocalBuildPayloadSource(repoRoot, fs, new FakeFileVersionReader());
        var manual = new ManualDirectoryPayloadSource(manualRoot, fs, new FakeFileVersionReader());
        var chain = new PayloadSourceChain(new IPayloadSource[] { local, manual });

        var bundle = await chain.GetOrThrowAsync(CancellationToken.None);

        Assert.Equal(manualRoot, bundle.RootDirectory);
        Assert.Equal(PayloadOrigin.ManualDirectory, bundle.Origin);
    }

    [Fact]
    public async Task Manual_directory_requires_both_dll_and_ini()
    {
        const string root = @"E:\downloads\opti";
        var fs = new MockFileSystem();
        fs.AddFile($@"{root}\OptiScaler.ini", new MockFileData("ini"));

        var source = new ManualDirectoryPayloadSource(root, fs, new FakeFileVersionReader());
        Assert.Null(await source.TryGetAsync(CancellationToken.None));

        fs.AddFile($@"{root}\OptiScaler.dll", new MockFileData("dll"));
        var bundle = await source.TryGetAsync(CancellationToken.None);
        Assert.Equal(PayloadOrigin.ManualDirectory, bundle!.Origin);
        Assert.Equal("unknown", bundle.Version);

        fs.File.Delete($@"{root}\OptiScaler.ini");
        Assert.Null(await source.TryGetAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Relative_manual_path_is_resolved_against_current_directory_for_version_lookup()
    {
        const string currentDirectory = @"C:\app";
        const string absoluteRoot = @"C:\app\payload";
        var fs = new MockFileSystem(new Dictionary<string, MockFileData>(), currentDirectory);
        SyntheticPayloadFactory.Create(fs, absoluteRoot);
        var versions = SyntheticPayloadFactory.CreateVersionReader(absoluteRoot, fs, "2.3.4.5");
        var source = new ManualDirectoryPayloadSource("payload", fs, versions);

        var bundle = await source.TryGetAsync(CancellationToken.None);

        Assert.Equal(absoluteRoot, bundle!.RootDirectory);
        Assert.Equal("2.3.4.5", bundle.Version);
        Assert.Equal(PayloadOrigin.ManualDirectory, bundle.Origin);
    }

    [Fact]
    public async Task Manual_directory_honors_cancellation_before_inspecting_files()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var source = new ManualDirectoryPayloadSource(
            @"E:\downloads\opti", new MockFileSystem(), new FakeFileVersionReader());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => source.TryGetAsync(cts.Token));
    }

    [Fact]
    public async Task Io_failure_from_a_source_propagates_instead_of_falling_back()
    {
        var fallback = new StubSource("fallback", Bundle(@"C:\fallback"));
        var chain = new PayloadSourceChain(new IPayloadSource[]
        {
            new ThrowingSource("broken"), fallback,
        });

        var ex = await Assert.ThrowsAsync<IOException>(
            () => chain.GetOrThrowAsync(CancellationToken.None));

        Assert.Equal("source read failed", ex.Message);
        Assert.Equal(0, fallback.Calls);
    }
}

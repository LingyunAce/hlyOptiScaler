using Deployer.Core.Domain;
using Deployer.Core.GameDiscovery;
using System.Threading;
using Xunit;

namespace Deployer.Core.Tests;

public class GameLibraryTests
{
    [Fact]
    public async Task Merges_normalized_roots_prefers_Xbox_metadata_and_shipping_candidates()
    {
        var steam = new FakeScanner("Steam", [Game("C:\\games\\foo", GamePlatform.Steam, ["C:\\games\\foo\\launcher.exe", "C:\\games\\foo\\game.exe"]) ]);
        var xbox = new FakeScanner("Xbox", [Game("c:\\games\\foo\\", GamePlatform.Xbox, ["C:\\games\\foo\\Game-WinGDK-Shipping.exe"], "family", "family!App")]);
        var library = new GameLibrary([steam, xbox]);
        var games = await library.GetGamesAsync();
        var game = Assert.Single(games);
        Assert.Equal(GamePlatform.Xbox, game.Platform);
        Assert.Equal("family", game.PackageFamilyName);
        Assert.Equal("family!App", game.AppId);
        Assert.Equal("C:\\games\\foo\\Game-WinGDK-Shipping.exe", game.ExecutableCandidates[0]);
    }

    [Fact]
    public async Task Caches_results_can_refresh_and_reports_independent_source_errors()
    {
        var good = new FakeScanner("Manual", [Game("C:\\good", GamePlatform.Manual, [])]);
        var failed = new FakeScanner("Steam", [], new IOException("fixture failure"));
        var library = new GameLibrary([good, failed]);
        Assert.Single(await library.GetGamesAsync());
        Assert.Single(library.Warnings);
        await library.GetGamesAsync(); Assert.Equal(1, good.Calls);
        await library.GetGamesAsync(refresh: true); Assert.Equal(2, good.Calls);
    }

    [Fact]
    public async Task Cancellation_is_not_converted_to_a_warning()
    {
        using var cts = new CancellationTokenSource(); cts.Cancel();
        var library = new GameLibrary([new FakeScanner("Manual", [], cancellationToken: cts.Token)]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => library.GetGamesAsync(ct: cts.Token));
    }

    [Fact]
    public async Task Starts_independent_scanners_concurrently()
    {
        var allStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        var scanners = Enumerable.Range(0, 2).Select(i => (IGameScanner)new BarrierScanner($"scanner-{i}", () =>
        {
            if (Interlocked.Increment(ref started) == 2) allStarted.TrySetResult(true);
            return release.Task;
        })).ToArray();
        var scan = new GameLibrary(scanners).GetGamesAsync();
        try { await allStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { release.TrySetResult(true); }
        await scan;
    }

    private static DiscoveredGame Game(string root, GamePlatform platform, IReadOnlyList<string> exes, string? family = null, string? appId = null) => new()
    { Title = "Game", Platform = platform, InstallRoot = root, ExecutableCandidates = exes, Writability = DirectoryWritability.Writable, ScannerSource = platform.ToString(), PackageFamilyName = family, AppId = appId };

    private sealed class FakeScanner(string name, IReadOnlyList<DiscoveredGame> games, Exception? error = null, CancellationToken cancellationToken = default) : IGameScanner
    {
        public string PlatformName => name;
        public IReadOnlyList<string> Warnings => [];
        public int Calls { get; private set; }
        public Task<IReadOnlyList<DiscoveredGame>> ScanAsync(CancellationToken ct)
        { Calls++; ct.ThrowIfCancellationRequested(); cancellationToken.ThrowIfCancellationRequested(); if (error is not null) throw error; return Task.FromResult(games); }
    }

    private sealed class BarrierScanner(string name, Func<Task> wait) : IGameScanner
    {
        public string PlatformName => name;
        public IReadOnlyList<string> Warnings => [];
        public async Task<IReadOnlyList<DiscoveredGame>> ScanAsync(CancellationToken ct)
        { await wait().WaitAsync(ct); return []; }
    }
}

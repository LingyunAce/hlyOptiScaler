using System.IO.Abstractions.TestingHelpers;
using Deployer.Core.Domain;
using Deployer.Core.GameDiscovery;
using Xunit;

namespace Deployer.Core.Tests;

public class XboxScannerTests
{
    [Fact]
    public async Task Merges_package_metadata_and_discovers_marked_package_roots()
    {
        var fs = new MockFileSystem();
        fs.AddFile(@"C:\XboxGames\A Game\Content\A-WinGDK-Shipping.exe", new MockFileData("exe"));
        fs.AddFile(@"C:\Store\OnlyGame\MicrosoftGame.config", new MockFileData("game"));
        fs.AddFile(@"C:\Store\OnlyGame\Only-WinGDK-Shipping.exe", new MockFileData("exe"));
        fs.AddDirectory(@"C:\WindowsApps");
        var packages = new FakePackages([
            new(@"C:\XboxGames\A Game\Content", "A Metadata", "a_family", "a_family!Game"),
            new(@"C:\Store\OnlyGame", "Only Game", "only_family", "only_family!Game")
        ]);
        var scanner = new XboxScanner(fs, new DirectoryWritabilityProbe(fs), packages, [@"C:\"]);
        var games = await scanner.ScanAsync(default);
        Assert.Equal(2, games.Count);
        var installed = Assert.Single(games, g => g.PackageFamilyName == "a_family");
        Assert.Equal("A Metadata", installed.Title);
        Assert.Equal("a_family!Game", installed.AppId);
        Assert.Contains(games, g => g.InstallRoot.EndsWith("OnlyGame", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Package_query_failure_keeps_directory_discoveries_and_adds_warning()
    {
        var fs = new MockFileSystem(); fs.AddFile(@"C:\XboxGames\A\Content\Game-WinGDK-Shipping.exe", new MockFileData("exe"));
        fs.AddDirectory(@"C:\WindowsApps");
        var scanner = new XboxScanner(fs, new DirectoryWritabilityProbe(fs), new FakePackages(error: new IOException("fixture failure")), [@"C:\"]);
        Assert.Single(await scanner.ScanAsync(default));
        Assert.NotEmpty(scanner.Warnings);
    }

    [Fact]
    public async Task WindowsApps_access_denial_keeps_other_roots_and_emits_warning()
    {
        var fs = new MockFileSystem(); fs.AddFile(@"C:\XboxGames\A\Content\Game-WinGDK-Shipping.exe", new MockFileData("exe"));
        fs.AddDirectory(@"C:\WindowsApps");
        IEnumerable<string> Enumerate(string path)
        {
            if (path.EndsWith("WindowsApps", StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException("fixture access denied");
            return fs.Directory.GetDirectories(path);
        }
        var packages = new FakePackages([new(@"C:\XboxGames\A\Content", "A metadata", "a_family", "Game")]);
        var scanner = new XboxScanner(fs, new DirectoryWritabilityProbe(fs), packages, [@"C:\"], Enumerate);
        var game = Assert.Single(await scanner.ScanAsync(default));
        Assert.Equal("a_family", game.PackageFamilyName);
        Assert.Equal(1, packages.Calls);
        Assert.Contains(scanner.Warnings, warning => warning.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Resolves_fixture_junction_to_external_game_root_without_duplicate()
    {
        var fs = new MockFileSystem();
        fs.AddDirectory(@"C:\WindowsApps\LegacyLink");
        fs.File.SetAttributes(@"C:\WindowsApps\LegacyLink", FileAttributes.Directory | FileAttributes.ReparsePoint);
        fs.AddFile(@"D:\XboxGames\Legacy\Content\Game-WinGDK-Shipping.exe", new MockFileData("exe"));
        var scanner = new XboxScanner(fs, new DirectoryWritabilityProbe(fs), new FakePackages(), [@"C:\"],
            linkTargetResolver: _ => @"D:\XboxGames\Legacy\Content");
        var game = Assert.Single(await scanner.ScanAsync(default));
        Assert.Equal(@"D:\XboxGames\Legacy\Content", game.InstallRoot);
        Assert.Equal(GamePlatform.Xbox, game.Platform);
    }

    private sealed class FakePackages(IReadOnlyList<XboxPackage>? packages = null, Exception? error = null) : IXboxPackageSource
    {
        public int Calls { get; private set; }
        public Task<IReadOnlyList<XboxPackage>> GetCurrentUserPackagesAsync(CancellationToken ct)
        { Calls++; ct.ThrowIfCancellationRequested(); if (error is not null) throw error; return Task.FromResult(packages ?? (IReadOnlyList<XboxPackage>)[]); }
    }
}

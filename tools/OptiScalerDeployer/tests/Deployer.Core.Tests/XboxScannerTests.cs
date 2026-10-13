using System.Buffers.Binary;
using System.Text;
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

    [Fact]
    public async Task GamingRoot_v1_discovers_custom_library_games_and_keeps_default_library()
    {
        var fs = new MockFileSystem();
        fs.AddFile(@"D:\.GamingRoot", new MockFileData(GamingRoot("Xboxgame")));
        AddGame(fs, @"D:\Xboxgame\Balatro", "Balatro-WinGDK-Shipping.exe", "MicrosoftGame.config");
        AddGame(fs, @"D:\Xboxgame\Kingdom Rush Origins", "Kingdom-WinGDK-Shipping.exe", "appxmanifest.xml");
        AddGame(fs, @"D:\XboxGames\Default Game", "Default-WinGDK-Shipping.exe", "MicrosoftGame.config");

        var games = await new XboxScanner(fs, new DirectoryWritabilityProbe(fs), new FakePackages(), [@"D:\"])
            .ScanAsync(default);

        Assert.Equal(3, games.Count);
        Assert.Equal(GamePlatform.Xbox, Assert.Single(games, g => g.Title == "Balatro").Platform);
        Assert.Single(games, g => g.Title == "Kingdom Rush Origins");
        Assert.Single(games, g => g.Title == "Default Game");
        Assert.Contains(games, g => g.InstallRoot == @"D:\Xboxgame\Balatro\Content");
    }

    [Fact]
    public async Task GamingRoot_that_points_to_default_XboxGames_does_not_duplicate_games()
    {
        var fs = new MockFileSystem();
        fs.AddFile(@"D:\.GamingRoot", new MockFileData(GamingRoot("XboxGames")));
        AddGame(fs, @"D:\XboxGames\One Game", "Game-WinGDK-Shipping.exe", "MicrosoftGame.config");

        var games = await new XboxScanner(fs, new DirectoryWritabilityProbe(fs), new FakePackages(), [@"D:\"])
            .ScanAsync(default);

        Assert.Single(games);
    }

    [Theory]
    [InlineData("..\\Outside", 1)]
    [InlineData("D:\\Outside", 1)]
    [InlineData("Xboxgame", 2)]
    public async Task Invalid_or_escaping_GamingRoot_is_not_scanned(string relative, uint version)
    {
        var fs = new MockFileSystem();
        fs.AddFile(@"D:\.GamingRoot", new MockFileData(GamingRoot(relative, version)));
        AddGame(fs, @"D:\Outside\Ghost", "Ghost-WinGDK-Shipping.exe", "MicrosoftGame.config");
        AddGame(fs, @"D:\Xboxgame\Ghost", "Ghost-WinGDK-Shipping.exe", "MicrosoftGame.config");

        var scanner = new XboxScanner(fs, new DirectoryWritabilityProbe(fs), new FakePackages(), [@"D:\"]);
        var games = await scanner.ScanAsync(default);

        Assert.Empty(games);
        Assert.Contains(scanner.Warnings, warning => warning.Contains(".GamingRoot", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GamingRoot_with_reparse_ancestor_is_not_scanned()
    {
        var fs = new MockFileSystem();
        fs.AddFile(@"D:\.GamingRoot", new MockFileData(GamingRoot(@"Library\Xboxgame")));
        AddGame(fs, @"D:\Library\Xboxgame\Game", "Game-WinGDK-Shipping.exe", "MicrosoftGame.config");
        fs.File.SetAttributes(@"D:\Library", FileAttributes.Directory | FileAttributes.ReparsePoint);

        var scanner = new XboxScanner(fs, new DirectoryWritabilityProbe(fs), new FakePackages(), [@"D:\"]);
        Assert.Empty(await scanner.ScanAsync(default));
        Assert.Contains(scanner.Warnings, warning => warning.Contains("重解析点", StringComparison.OrdinalIgnoreCase));
    }

    private static byte[] GamingRoot(string relative, uint version = 1)
    {
        var path = Encoding.Unicode.GetBytes(relative + '\0');
        var bytes = new byte[8 + path.Length];
        "RGBX"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), version);
        path.CopyTo(bytes, 8);
        return bytes;
    }

    private static void AddGame(MockFileSystem fs, string gameRoot, string exe, string manifest)
    {
        var content = Path.Combine(gameRoot, "Content");
        fs.AddFile(Path.Combine(content, manifest), new MockFileData("fixture"));
        fs.AddFile(Path.Combine(content, exe), new MockFileData(new byte[64]));
    }

    private sealed class FakePackages(IReadOnlyList<XboxPackage>? packages = null, Exception? error = null) : IXboxPackageSource
    {
        public int Calls { get; private set; }
        public Task<IReadOnlyList<XboxPackage>> GetCurrentUserPackagesAsync(CancellationToken ct)
        { Calls++; ct.ThrowIfCancellationRequested(); if (error is not null) throw error; return Task.FromResult(packages ?? (IReadOnlyList<XboxPackage>)[]); }
    }
}

using System.IO.Abstractions.TestingHelpers;
using Deployer.Core.Deployment;
using Deployer.Core.Domain;
using Deployer.Core.GameDiscovery;
using Xunit;

namespace Deployer.Core.Tests;

public class ManualFolderStoreTests
{
    [Fact]
    public async Task Explicit_add_remove_roundtrip_and_scanner_does_not_mutate_config()
    {
        var fs = new MockFileSystem(); const string config = @"C:\config\manual.json";
        fs.AddDirectory(@"C:\Games\Manual"); fs.AddFile(@"C:\Games\Manual\game.exe", new MockFileData("exe"));
        var store = Store(fs, config);
        store.Add("My Game", @"C:\Games\Manual");
        var beforeScan = fs.File.ReadAllText(config);
        var games = await new ManualFolderScanner(fs, new DirectoryWritabilityProbe(fs), store).ScanAsync(default);
        Assert.Equal("My Game", Assert.Single(games).Title);
        Assert.Equal(beforeScan, fs.File.ReadAllText(config));
        store.Remove(@"C:\Games\Manual");
        Assert.Empty(store.Load());
    }

    [Fact]
    public void Corrupt_config_is_preserved_and_never_overwritten_by_add()
    {
        var fs = new MockFileSystem(); const string config = @"C:\config\manual.json";
        fs.AddFile(config, new MockFileData("{ broken"));
        var store = Store(fs, config);
        Assert.Throws<InvalidDataException>(() => store.Add("Game", @"C:\Games\Game"));
        Assert.Equal("{ broken", fs.File.ReadAllText(config));
    }

    [Fact]
    public async Task One_off_scan_does_not_need_or_write_a_manual_config()
    {
        var fs = new MockFileSystem(); fs.AddFile(@"D:\OneOff\Game.exe", new MockFileData("exe"));
        var scanner = new ManualFolderScanner(fs, new DirectoryWritabilityProbe(fs), [new("One Off", @"D:\OneOff")]);
        var game = Assert.Single(await scanner.ScanAsync(default));
        Assert.Equal("One Off", game.Title);
        Assert.Equal(GamePlatform.Manual, game.Platform);
    }

    [Fact]
    public async Task Manual_custom_Xbox_library_splits_games_and_merges_with_Xbox_discovery_without_config_mutation()
    {
        var fs = new MockFileSystem(); const string config = @"C:\config\manual.json";
        fs.AddFile(@"D:\.GamingRoot", new MockFileData(GamingRoot("Xboxgame")));
        AddXboxGame(fs, @"D:\Xboxgame\Balatro", "Balatro-WinGDK-Shipping.exe");
        AddXboxGame(fs, @"D:\Xboxgame\Kingdom Rush Origins", "Kingdom-WinGDK-Shipping.exe");
        var store = Store(fs, config);
        store.Add("My Xbox library", @"D:\Xboxgame");
        var beforeScan = fs.File.ReadAllText(config);
        var probe = new DirectoryWritabilityProbe(fs);
        var library = new GameLibrary([
            new ManualFolderScanner(fs, probe, store),
            new XboxScanner(fs, probe, new EmptyPackages(), [@"D:\"])
        ]);

        var games = await library.GetGamesAsync();

        Assert.Equal(2, games.Count);
        Assert.All(games, game => Assert.Equal(GamePlatform.Xbox, game.Platform));
        Assert.Single(games, game => game.Title == "Balatro");
        Assert.Single(games, game => game.Title == "Kingdom Rush Origins");
        Assert.Equal(beforeScan, fs.File.ReadAllText(config));
    }

    [Fact]
    public async Task Manual_Xbox_library_with_top_level_executable_keeps_single_manual_entry()
    {
        var fs = new MockFileSystem();
        fs.AddFile(@"D:\.GamingRoot", new MockFileData(GamingRoot("Xboxgame")));
        fs.AddFile(@"D:\Xboxgame\Library.exe", new MockFileData("exe"));
        AddXboxGame(fs, @"D:\Xboxgame\Child Game", "Child-WinGDK-Shipping.exe");
        var scanner = new ManualFolderScanner(fs, new DirectoryWritabilityProbe(fs), [new("Whole library", @"D:\Xboxgame")]);

        var game = Assert.Single(await scanner.ScanAsync(default));
        Assert.Equal("Whole library", game.Title);
        Assert.Equal(GamePlatform.Manual, game.Platform);
        Assert.Equal(@"D:\Xboxgame", game.InstallRoot);
    }

    [Fact]
    public async Task Invalid_one_off_manual_entry_warns_and_does_not_stop_later_entries()
    {
        var fs = new MockFileSystem();
        fs.AddFile(@"D:\Valid\Game.exe", new MockFileData("exe"));
        var scanner = new ManualFolderScanner(fs, new DirectoryWritabilityProbe(fs), [
            new("Invalid", null!), new("Valid", @"D:\Valid")
        ]);

        var game = Assert.Single(await scanner.ScanAsync(default));
        Assert.Equal("Valid", game.Title);
        Assert.NotEmpty(scanner.Warnings);
    }

    [Fact]
    public async Task Candidate_order_prefers_GDK_then_Win64_and_skips_engine_tools_and_launchers()
    {
        var fs = new MockFileSystem();
        fs.AddFile(@"D:\Order\Engine\Binaries\UE4Editor.exe", new MockFileData(new byte[100]));
        fs.AddFile(@"D:\Order\CrashBandicoot.exe", new MockFileData(new byte[1]));
        fs.AddFile(@"D:\Order\GameLauncher.exe", new MockFileData(new byte[100]));
        fs.AddFile(@"D:\Order\Binaries\Other.exe", new MockFileData(new byte[2]));
        fs.AddFile(@"D:\Order\Binaries\Game-Win64-Shipping.exe", new MockFileData(new byte[1]));
        fs.AddFile(@"D:\Order\Binaries\Game-WinGDK-Shipping.exe", new MockFileData(new byte[1]));
        var scanner = new ManualFolderScanner(fs, new DirectoryWritabilityProbe(fs), [new("Order", @"D:\Order")]);
        var candidates = Assert.Single(await scanner.ScanAsync(default)).ExecutableCandidates;
        Assert.EndsWith("Game-WinGDK-Shipping.exe", candidates[0]);
        Assert.EndsWith("Game-Win64-Shipping.exe", candidates[1]);
        Assert.Contains(candidates, p => p.EndsWith("CrashBandicoot.exe", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(candidates, p => p.Contains("UE4Editor", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(candidates, p => p.Contains("GameLauncher", StringComparison.OrdinalIgnoreCase));
    }

    private static ManualFolderStore Store(MockFileSystem fs, string path) => new(fs, new AtomicFileWriter(fs), path);

    private static byte[] GamingRoot(string relative)
    {
        var path = System.Text.Encoding.Unicode.GetBytes(relative + '\0');
        var bytes = new byte[8 + path.Length];
        "RGBX"u8.CopyTo(bytes);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), 1);
        path.CopyTo(bytes, 8);
        return bytes;
    }

    private static void AddXboxGame(MockFileSystem fs, string root, string exe)
    {
        var content = Path.Combine(root, "Content");
        fs.AddFile(Path.Combine(content, "MicrosoftGame.config"), new MockFileData("fixture"));
        fs.AddFile(Path.Combine(content, exe), new MockFileData(new byte[64]));
    }

    private sealed class EmptyPackages : IXboxPackageSource
    {
        public Task<IReadOnlyList<XboxPackage>> GetCurrentUserPackagesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<XboxPackage>>([]);
    }
}

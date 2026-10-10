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
}

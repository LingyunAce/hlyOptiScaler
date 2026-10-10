using System.IO.Abstractions.TestingHelpers;
using Deployer.Core.GameDiscovery;
using Xunit;

namespace Deployer.Core.Tests;

public class EpicScannerTests
{
    [Fact]
    public async Task Reads_installation_list_and_reports_corrupt_json()
    {
        var fs = new MockFileSystem();
        const string manifest = @"C:\ProgramData\Epic\LauncherInstalled.dat";
        fs.AddFile(manifest, new MockFileData("""{"InstallationList":[{"AppName":"Fortnite","InstallLocation":"C:\\Games\\Fortnite"}]}"""));
        fs.AddFile(@"C:\Games\Fortnite\FortniteClient-Win64-Shipping.exe", new MockFileData("exe"));
        var scanner = new EpicScanner(fs, new DirectoryWritabilityProbe(fs), manifest);
        var games = await scanner.ScanAsync(default);
        Assert.Equal("Fortnite", Assert.Single(games).Title);
        Assert.EndsWith("FortniteClient-Win64-Shipping.exe", games[0].ExecutableCandidates[0]);
        fs.File.WriteAllText(manifest, "{");
        Assert.Empty(await scanner.ScanAsync(default));
        Assert.NotEmpty(scanner.Warnings);
    }
}

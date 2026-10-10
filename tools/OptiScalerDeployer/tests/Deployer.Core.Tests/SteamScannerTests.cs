using System.IO.Abstractions.TestingHelpers;
using Deployer.Core.GameDiscovery;
using Xunit;

namespace Deployer.Core.Tests;

public class SteamScannerTests
{
    [Theory]
    [InlineData("}")]
    [InlineData("\"a\" { \"b\" { }")]
    public async Task Rejects_stray_or_missing_nested_closing_braces(string malformed)
    {
        var fs = new MockFileSystem();
        fs.AddFile(@"C:\Steam\steamapps\libraryfolders.vdf", new MockFileData(malformed));
        var scanner = new SteamScanner(fs, new DirectoryWritabilityProbe(fs), () => @"C:\Steam");
        await scanner.ScanAsync(default);
        Assert.NotEmpty(scanner.Warnings);
    }

    [Fact]
    public async Task Reads_old_and_new_libraryfolders_without_crossing_nested_keys()
    {
        var fs = new MockFileSystem();
        fs.AddFile(@"C:\Steam\steamapps\libraryfolders.vdf", new MockFileData("""
            "libraryfolders"
            {
              "0" "C:\\Steam"
              "1" { "path" "D:\\SteamLibrary" "apps" { "123" "installed" } }
              "ContentStatsID" "should-not-be-a-library"
            }
            """));
        AddGame(fs, @"C:\Steam", "One", "1"); AddGame(fs, @"D:\SteamLibrary", "Two", "2");
        var scanner = new SteamScanner(fs, new DirectoryWritabilityProbe(fs), () => @"C:\Steam");
        var games = await scanner.ScanAsync(default);
        Assert.Equal(new[] { "One", "Two" }, games.Select(g => g.Title).Order().ToArray());
        Assert.Contains(games, g => g.AppId == "2" && g.ExecutableCandidates.Single().EndsWith("two.exe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Rejects_install_directory_escape_and_keeps_main_library_when_library_index_is_bad()
    {
        var fs = new MockFileSystem();
        fs.AddFile(@"C:\Steam\steamapps\libraryfolders.vdf", new MockFileData("bad {"));
        AddGame(fs, @"C:\Steam", "Safe", "3");
        fs.AddFile(@"C:\Steam\steamapps\appmanifest_4.acf", new MockFileData("""
            "AppState" { "appid" "4" "name" "Escape" "installdir" "..\\outside" }
            """));
        fs.AddDirectory(@"C:\Steam\steamapps\common\outside");
        var scanner = new SteamScanner(fs, new DirectoryWritabilityProbe(fs), () => @"C:\Steam");
        var games = await scanner.ScanAsync(default);
        Assert.Contains(games, g => g.Title == "Safe");
        Assert.DoesNotContain(games, g => g.Title == "Escape");
        Assert.NotEmpty(scanner.Warnings);
    }

    [Fact]
    public async Task Missing_library_index_still_scans_main_install_manifests()
    {
        var fs = new MockFileSystem(); AddGame(fs, @"C:\Steam", "Main", "9");
        var scanner = new SteamScanner(fs, new DirectoryWritabilityProbe(fs), () => @"C:\Steam");
        Assert.Equal("Main", Assert.Single(await scanner.ScanAsync(default)).Title);
        Assert.NotEmpty(scanner.Warnings);
    }

    private static void AddGame(MockFileSystem fs, string library, string name, string id)
    {
        var safe = name.ToLowerInvariant();
        fs.AddFile($@"{library}\steamapps\appmanifest_{id}.acf", new MockFileData($"\"AppState\" {{ \"appid\" \"{id}\" \"name\" \"{name}\" \"installdir\" \"{safe}\" }}"));
        fs.AddFile($@"{library}\steamapps\common\{safe}\{safe}.exe", new MockFileData("exe"));
    }
}

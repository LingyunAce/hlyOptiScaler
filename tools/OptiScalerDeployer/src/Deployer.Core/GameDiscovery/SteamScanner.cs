using System.IO.Abstractions;
using Deployer.Core.Domain;
using Microsoft.Win32;

namespace Deployer.Core.GameDiscovery;

public sealed class SteamScanner(IFileSystem fs, DirectoryWritabilityProbe probe, Func<string?>? steamPath = null)
    : ScannerBase(fs, probe)
{
    public override string PlatformName => "Steam";

    public override Task<IReadOnlyList<DiscoveredGame>> ScanAsync(CancellationToken ct)
    {
        ClearWarnings();
        ct.ThrowIfCancellationRequested();
        var result = new List<DiscoveredGame>();
        var path = steamPath is null ? ReadSteamPath() : steamPath();
        if (string.IsNullOrWhiteSpace(path)) return Task.FromResult<IReadOnlyList<DiscoveredGame>>(result);
        try
        {
            var main = Fs.Path.GetFullPath(path);
            var librariesFile = Fs.Path.Combine(main, "steamapps", "libraryfolders.vdf");
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { main };
            try
            {
                if (!Fs.File.Exists(librariesFile)) throw new FileNotFoundException("Steam library 文件不存在。", librariesFile);
                var root = KeyValuesParser.Parse(Fs.File.ReadAllText(librariesFile));
                if (!root.TryGetValue("libraryfolders", out var item) || item is not Dictionary<string, object> libraries)
                    throw new InvalidDataException("libraryfolders.vdf 缺少 libraryfolders 块。");
                foreach (var (key, entry) in libraries)
                {
                    if (!int.TryParse(key, out _)) continue;
                    var value = entry is string s ? s : entry is Dictionary<string, object> block ? KeyValuesParser.String(block, "path") : null;
                    if (!string.IsNullOrWhiteSpace(value)) paths.Add(value.Replace("\\\\", "\\", StringComparison.Ordinal).Replace("\\", Fs.Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal));
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
            { Warn($"Steam 库列表不可用，将继续扫描主库：{e.Message}"); }
            foreach (var library in paths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var steamapps = Fs.Path.Combine(library, "steamapps");
                    if (!Fs.Directory.Exists(steamapps)) continue;
                    foreach (var manifest in Fs.Directory.GetFiles(steamapps, "appmanifest_*.acf"))
                    {
                        ct.ThrowIfCancellationRequested();
                        try
                        {
                        var data = KeyValuesParser.Parse(Fs.File.ReadAllText(manifest));
                        if (!data.TryGetValue("AppState", out var app) || app is not Dictionary<string, object> state) continue;
                        var installDir = KeyValuesParser.String(state, "installdir");
                        var title = KeyValuesParser.String(state, "name");
                        if (string.IsNullOrWhiteSpace(installDir) || string.IsNullOrWhiteSpace(title)) continue;
                        var common = Fs.Path.GetFullPath(Fs.Path.Combine(steamapps, "common"));
                        var game = Fs.Path.GetFullPath(Fs.Path.Combine(common, installDir));
                        if (!IsWithin(Fs, common, game)) { Warn($"Steam 游戏目录越界，已忽略：{manifest}"); continue; }
                        var found = Create(title, GamePlatform.Steam, game, "Steam", ct, appId: KeyValuesParser.String(state, "appid"));
                        if (found is not null) result.Add(found);
                        }
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
                        { Warn($"Steam 清单 {manifest}: {e.Message}"); }
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
                { Warn($"Steam 库 {library}: {e.Message}"); }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        { Warn($"Steam 扫描失败：{e.Message}"); }
        return Task.FromResult<IReadOnlyList<DiscoveredGame>>(result);
    }

    private static string? ReadSteamPath()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try { using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"); return key?.GetValue("SteamPath") as string; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static bool IsWithin(IFileSystem fs, string parent, string child)
    {
        var relative = fs.Path.GetRelativePath(parent, child);
        return relative != ".." && !relative.StartsWith(".." + fs.Path.DirectorySeparatorChar, StringComparison.Ordinal) && !fs.Path.IsPathRooted(relative);
    }
}


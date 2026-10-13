using System.IO.Abstractions;
using Deployer.Core.Domain;

namespace Deployer.Core.GameDiscovery;

public sealed class ManualFolderScanner : ScannerBase
{
    private readonly ManualFolderStore? _store;
    private readonly IReadOnlyList<ManualFolderEntry>? _entries;

    public ManualFolderScanner(IFileSystem fs, DirectoryWritabilityProbe probe, ManualFolderStore store) : base(fs, probe)
    { _store = store; }

    /// <summary>Scans a one-off folder without persisting it to the manual-folder configuration.</summary>
    public ManualFolderScanner(IFileSystem fs, DirectoryWritabilityProbe probe, IEnumerable<ManualFolderEntry> entries) : base(fs, probe)
    { _entries = entries.ToArray(); }

    public override string PlatformName => "Manual";
    public override Task<IReadOnlyList<DiscoveredGame>> ScanAsync(CancellationToken ct)
    {
        ClearWarnings();
        var result = new List<DiscoveredGame>();
        foreach (var entry in _store?.Load() ?? _entries ?? [])
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var root = Fs.Path.GetFullPath(entry.Folder);
                if (!XboxGameLibrary.IsRecognizedLibraryRoot(Fs, root)
                    || XboxGameLibrary.HasTopLevelExecutable(Fs, root))
                {
                    var game = Create(entry.Title, GamePlatform.Manual, root, "Manual", ct);
                    if (game is not null) AddUnique(result, game);
                    continue;
                }

                foreach (var gameDirectory in Fs.Directory.GetDirectories(root))
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        if ((Fs.File.GetAttributes(gameDirectory) & FileAttributes.ReparsePoint) != 0) continue;
                        var content = Fs.Path.Combine(gameDirectory, "Content");
                        if (!XboxGameLibrary.IsGameContent(Fs, gameDirectory, content, ct, Warn)) continue;
                        var game = Create(Fs.Path.GetFileName(gameDirectory), GamePlatform.Xbox, content, "Manual Xbox library", ct);
                        if (game is not null) AddUnique(result, game);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                    { Warn($"无法扫描手动 Xbox 库条目 {gameDirectory}: {ex.Message}"); }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            { Warn($"无法扫描手动目录 {entry.Folder}: {ex.Message}"); }
        }
        return Task.FromResult<IReadOnlyList<DiscoveredGame>>(result);
    }

    private bool AddUnique(List<DiscoveredGame> result, DiscoveredGame game)
    {
        if (result.Any(existing => string.Equals(GameLibrary.Normalize(existing.InstallRoot),
                GameLibrary.Normalize(game.InstallRoot), StringComparison.OrdinalIgnoreCase))) return false;
        result.Add(game);
        return true;
    }
}

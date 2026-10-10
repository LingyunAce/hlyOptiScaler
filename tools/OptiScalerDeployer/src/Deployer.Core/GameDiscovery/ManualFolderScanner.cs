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
            var game = Create(entry.Title, GamePlatform.Manual, entry.Folder, "Manual", ct);
            if (game is not null) result.Add(game);
        }
        return Task.FromResult<IReadOnlyList<DiscoveredGame>>(result);
    }
}

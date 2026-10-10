using Deployer.Core.Domain;

namespace Deployer.Core.GameDiscovery;

public sealed class GameLibrary(IEnumerable<IGameScanner> scanners)
{
    private readonly IGameScanner[] _scanners = scanners.ToArray();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<DiscoveredGame>? _cache;
    private string[] _warnings = [];
    public IReadOnlyList<string> Warnings => _warnings;

    public async Task<IReadOnlyList<DiscoveredGame>> GetGamesAsync(bool refresh = false, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cache is not null && !refresh) return _cache;
            var tasks = _scanners.Select(scanner => Task.Run(async () =>
            {
                try { return (Games: await scanner.ScanAsync(ct).ConfigureAwait(false), Warning: (string?)null); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception e) { return (Games: (IReadOnlyList<DiscoveredGame>)Array.Empty<DiscoveredGame>(), Warning: $"{scanner.PlatformName}: {e.Message}"); }
            }, ct)).ToArray();
            var results = await Task.WhenAll(tasks).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            _warnings = results.SelectMany((r, i) =>
                _scanners[i].Warnings.Concat(r.Warning is null ? [] : [r.Warning])).ToArray();
            _cache = Merge(results.SelectMany(r => r.Games));
            return _cache;
        }
        finally { _gate.Release(); }
    }

    private static IReadOnlyList<DiscoveredGame> Merge(IEnumerable<DiscoveredGame> games)
    {
        var result = new List<DiscoveredGame>();
        var indexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var game in games)
        {
            var root = Normalize(game.InstallRoot);
            if (!indexes.TryGetValue(root, out var index))
            {
                indexes[root] = result.Count;
                result.Add(game with { InstallRoot = root });
                continue;
            }
            var prior = result[index];
            var preferred = game.Platform == GamePlatform.Xbox && prior.Platform != GamePlatform.Xbox ? game : prior;
            result[index] = preferred with
            {
                PackageFamilyName = preferred.PackageFamilyName ?? prior.PackageFamilyName ?? game.PackageFamilyName,
                AppId = preferred.AppId ?? prior.AppId ?? game.AppId,
                ExecutableCandidates = preferred.ExecutableCandidates.Concat((ReferenceEquals(preferred, prior) ? game : prior).ExecutableCandidates)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                Writability = MergeWritability(prior.Writability, game.Writability)
            };
        }
        return result;
    }

    private static DirectoryWritability MergeWritability(DirectoryWritability a, DirectoryWritability b)
    {
        if (a == DirectoryWritability.Blocked || b == DirectoryWritability.Blocked) return DirectoryWritability.Blocked;
        if (a == DirectoryWritability.NeedsElevation || b == DirectoryWritability.NeedsElevation) return DirectoryWritability.NeedsElevation;
        if (a == DirectoryWritability.Unknown || b == DirectoryWritability.Unknown) return DirectoryWritability.Unknown;
        return DirectoryWritability.Writable;
    }

    internal static string Normalize(string path)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return full.Length == 2 && full[1] == ':' ? full + Path.DirectorySeparatorChar : full;
    }
}

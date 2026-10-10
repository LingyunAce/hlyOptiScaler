using System.IO.Abstractions;
using Deployer.Core.Domain;

namespace Deployer.Core.GameDiscovery;

public abstract class ScannerBase(IFileSystem fs, DirectoryWritabilityProbe probe) : IGameScanner
{
    protected IFileSystem Fs { get; } = fs;
    protected DirectoryWritabilityProbe Probe { get; } = probe;
    private readonly List<string> _warnings = [];
    public abstract string PlatformName { get; }
    public IReadOnlyList<string> Warnings => _warnings;
    protected void Warn(string message) => _warnings.Add(message);
    protected void ClearWarnings() => _warnings.Clear();
    public abstract Task<IReadOnlyList<DiscoveredGame>> ScanAsync(CancellationToken ct);

    protected DiscoveredGame? Create(string title, GamePlatform platform, string root, string source, CancellationToken ct,
        string? family = null, string? appId = null)
    {
        try
        {
            var full = Fs.Path.GetFullPath(root);
            if (!Fs.Directory.Exists(full)) return null;
            return new() { Title = string.IsNullOrWhiteSpace(title) ? Fs.Path.GetFileName(full) : title.Trim(), Platform = platform,
                InstallRoot = full, ExecutableCandidates = ExecutableFinder.Find(Fs, full, ct, Warn), Writability = Probe.Probe(full),
                ScannerSource = source, PackageFamilyName = family, AppId = appId };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { Warn($"{source} {root}: {e.Message}"); return null; }
    }
}

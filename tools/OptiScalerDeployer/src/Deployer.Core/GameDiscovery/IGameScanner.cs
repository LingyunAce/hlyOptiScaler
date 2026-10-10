using Deployer.Core.Domain;

namespace Deployer.Core.GameDiscovery;

public interface IGameScanner
{
    string PlatformName { get; }
    IReadOnlyList<string> Warnings { get; }
    Task<IReadOnlyList<DiscoveredGame>> ScanAsync(CancellationToken ct);
}

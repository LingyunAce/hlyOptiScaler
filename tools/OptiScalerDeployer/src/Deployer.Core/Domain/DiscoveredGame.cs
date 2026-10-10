using Deployer.Core.Domain;

namespace Deployer.Core.Domain;

/// <summary>A game candidate reported by platform discovery before an executable is selected.</summary>
public sealed record DiscoveredGame
{
    public required string Title { get; init; }
    public required GamePlatform Platform { get; init; }
    public string? PackageFamilyName { get; init; }
    public string? AppId { get; init; }
    public required string InstallRoot { get; init; }
    /// <summary>Full paths, ordered from most likely launch executable to fallback.</summary>
    public required IReadOnlyList<string> ExecutableCandidates { get; init; }
    public required DirectoryWritability Writability { get; init; }
    public required string ScannerSource { get; init; }
}

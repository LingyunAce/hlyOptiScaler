namespace Deployer.Core.GameDiscovery;

public sealed record XboxPackage(string InstallRoot, string DisplayName, string PackageFamilyName, string? AppId);

public interface IXboxPackageSource
{
    Task<IReadOnlyList<XboxPackage>> GetCurrentUserPackagesAsync(CancellationToken ct);
}

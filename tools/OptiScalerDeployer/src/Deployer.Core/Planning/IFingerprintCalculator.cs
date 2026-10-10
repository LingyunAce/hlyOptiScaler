using Deployer.Core.Domain;

namespace Deployer.Core.Planning;

public interface IFingerprintCalculator
{
    /// <summary>Returns the first eight lowercase SHA256 hex digits for a stable game identity.</summary>
    string Compute(GamePlatform platform, string? packageFamilyName, string installRoot, string executableName);
}

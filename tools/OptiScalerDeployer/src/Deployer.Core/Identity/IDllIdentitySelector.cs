using Deployer.Core.Domain;

namespace Deployer.Core.Identity;

/// <summary>Inputs for identity selection; choosing a file name does not mutate the target directory.</summary>
public sealed record IdentityContext(
    GamePlatform Platform,
    bool IsVulkanOnly,
    string DeployDirectory,
    bool AllowOverwriteGameFile,
    bool PreferAsiIdentity);

public interface IDllIdentitySelector
{
    IdentityDecision Select(IdentityContext context);
}

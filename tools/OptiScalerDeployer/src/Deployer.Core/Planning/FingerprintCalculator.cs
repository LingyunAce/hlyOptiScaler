using Deployer.Core.Domain;
using Deployer.Core.Util;

namespace Deployer.Core.Planning;

public sealed class FingerprintCalculator : IFingerprintCalculator
{
    public string Compute(
        GamePlatform platform, string? packageFamilyName, string installRoot, string executableName)
    {
        if (string.IsNullOrWhiteSpace(packageFamilyName) && string.IsNullOrWhiteSpace(installRoot))
        {
            throw new ArgumentException("installRoot 与 packageFamilyName 不能同时为空", nameof(installRoot));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(executableName);

        var gameIdentity = string.IsNullOrWhiteSpace(packageFamilyName)
            ? NormalizePath(installRoot)
            : packageFamilyName.Trim().ToLowerInvariant();
        var input = $"{platform.ToString().ToLowerInvariant()}|{gameIdentity}|{executableName.Trim().ToLowerInvariant()}";
        return Hashing.ShortHash(input, 8);
    }

    private static string NormalizePath(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full);
        if (!string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
        {
            full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        return full.ToLowerInvariant();
    }
}

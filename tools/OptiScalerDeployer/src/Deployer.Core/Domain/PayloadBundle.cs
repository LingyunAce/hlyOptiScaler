namespace Deployer.Core.Domain;

/// <summary>一份可用的 OptiScaler 产物目录。RootDirectory 即 x64\Release\a\ 或解压后的缓存目录。</summary>
public sealed record PayloadBundle(string RootDirectory, string Version, PayloadOrigin Origin);

/// <summary>写进 plan 与 manifest 的 payload 快照。</summary>
public sealed record PayloadInfo(PayloadOrigin Origin, string Version, string RootDirectory)
{
    public static PayloadInfo From(PayloadBundle bundle) =>
        new(bundle.Origin, bundle.Version, bundle.RootDirectory);
}

namespace Deployer.Core.Domain;

/// <summary>
/// 决策后的部署目标。与扫描阶段的 DiscoveredGame（计划 B）区分：
/// GameTarget 有唯一的 DeployDirectory 与唯一的 ExecutableName。
/// </summary>
public sealed record GameTarget(
    string Title,
    GamePlatform Platform,
    string? PackageFamilyName,
    string? AppId,
    string InstallRoot,
    string DeployDirectory,
    string ExecutableName,
    string Fingerprint);

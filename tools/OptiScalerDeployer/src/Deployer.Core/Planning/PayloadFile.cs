namespace Deployer.Core.Planning;

/// <summary>A payload file and its normalized path relative to the payload root.</summary>
public sealed record PayloadFile(string RelativePath, string FullPath, long Length, string Sha256);

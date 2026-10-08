namespace Deployer.Core.Abstractions;

/// <summary>读取 PE 版本资源。身份冲突判定依赖 OriginalFilename。</summary>
public interface IFileVersionReader
{
    /// <summary>返回版本资源里的 OriginalFilename；无版本资源或读取失败返回 null。</summary>
    string? GetOriginalFilename(string path);

    /// <summary>返回 FileVersion（形如 "0.9.1.0"）；无版本资源返回 null。</summary>
    string? GetFileVersion(string path);
}

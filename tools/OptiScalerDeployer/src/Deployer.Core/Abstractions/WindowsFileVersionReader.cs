using System.Diagnostics;

namespace Deployer.Core.Abstractions;

public sealed class WindowsFileVersionReader : IFileVersionReader
{
    public string? GetOriginalFilename(string path) => Read(path)?.OriginalFilename;

    public string? GetFileVersion(string path) => Read(path)?.FileVersion;

    private static FileVersionInfo? Read(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            // FileVersionInfo 对无版本资源的文件返回全 null 字段的对象，而不是抛异常
            return string.IsNullOrEmpty(info.OriginalFilename) && string.IsNullOrEmpty(info.FileVersion)
                ? null
                : info;
        }
        catch (Exception)
        {
            // 32/64 位不符、文件被占用、非 PE 文件 —— 一律视为「读不出版本信息」
            return null;
        }
    }
}

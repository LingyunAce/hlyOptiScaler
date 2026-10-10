using System.IO.Abstractions;
using Deployer.Core.Errors;
using Deployer.Core.Util;

namespace Deployer.Core.Planning;

/// <summary>Enumerates deployable payload files in deterministic deployment order.</summary>
public sealed class PayloadWalker
{
    private static readonly HashSet<string> ExcludedRootFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "OptiScaler.dll",
        "OptiScaler.ini",
        "setup_windows.bat",
        "setup_linux.sh",
        "!! EXTRACT ALL FILES TO GAME FOLDER !!",
    };

    private readonly IFileSystem _fs;

    public PayloadWalker(IFileSystem fs) => _fs = fs;

    public IReadOnlyList<PayloadFile> Walk(string payloadRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadRoot);
        if (!_fs.Directory.Exists(payloadRoot))
        {
            throw new PayloadNotFoundException($"payload 根目录不存在：{payloadRoot}");
        }

        var root = _fs.Path.GetFullPath(payloadRoot);
        var files = new List<PayloadFile>();
        foreach (var enumeratedPath in _fs.Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var fullPath = _fs.Path.GetFullPath(enumeratedPath);
            var relative = ToRelative(root, fullPath);
            if (IsExcluded(relative))
            {
                continue;
            }

            var info = _fs.FileInfo.New(fullPath);
            files.Add(new PayloadFile(relative, fullPath, info.Length, Hashing.Sha256OfFile(_fs, fullPath)));
        }

        return files
            .OrderBy(file => Rank(file.RelativePath))
            .ThenBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(file => file.RelativePath, StringComparer.Ordinal)
            .ToArray();
    }

    private static int Rank(string relativePath)
    {
        var separator = relativePath.IndexOfAny(new[] { '\\', '/' });
        var topLevel = separator < 0 ? relativePath : relativePath[..separator];
        return topLevel.ToLowerInvariant() switch
        {
            "optiscaler" => 0,
            "licenses" => 1,
            _ => 2,
        };
    }

    private static bool IsExcluded(string relativePath) =>
        !relativePath.Contains('\\') && !relativePath.Contains('/') && ExcludedRootFiles.Contains(relativePath);

    private string ToRelative(string root, string fullPath)
    {
        var separator = _fs.Path.DirectorySeparatorChar;
        var prefix = root.EndsWith(separator) ? root : root + separator;
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"payload 文件位于根目录之外：{fullPath}");
        }

        var relative = fullPath[prefix.Length..];
        if (relative.Length == 0 || _fs.Path.IsPathRooted(relative)
            || relative.Split(new[] { '\\', '/' }).Any(part => part is ".." or "."))
        {
            throw new InvalidDataException($"payload 文件相对路径无效：{fullPath}");
        }

        return relative;
    }
}

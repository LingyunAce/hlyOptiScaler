using System.IO.Abstractions;
using Deployer.Core.Abstractions;
using Deployer.Core.Domain;

namespace Deployer.Core.Payload;

/// <summary>Uses a user-selected directory after checking for the two required payload files.</summary>
public sealed class ManualDirectoryPayloadSource : IPayloadSource
{
    private readonly string _directory;
    private readonly IFileSystem _fs;
    private readonly IFileVersionReader _versions;

    public ManualDirectoryPayloadSource(string directory, IFileSystem fs, IFileVersionReader versions)
    {
        _directory = directory;
        _fs = fs;
        _versions = versions;
    }

    public string Description => $"手动指定目录 {_directory}";

    public Task<PayloadBundle?> TryGetAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var root = _fs.Path.GetFullPath(_directory);
        var dll = _fs.Path.Combine(root, "OptiScaler.dll");
        var ini = _fs.Path.Combine(root, "OptiScaler.ini");

        if (!_fs.File.Exists(dll) || !_fs.File.Exists(ini))
        {
            return Task.FromResult<PayloadBundle?>(null);
        }

        var version = _versions.GetFileVersion(dll) ?? "unknown";
        return Task.FromResult<PayloadBundle?>(
            new PayloadBundle(root, version, PayloadOrigin.ManualDirectory));
    }
}

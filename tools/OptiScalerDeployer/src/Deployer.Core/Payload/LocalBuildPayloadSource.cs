using System.IO.Abstractions;
using Deployer.Core.Abstractions;
using Deployer.Core.Domain;

namespace Deployer.Core.Payload;

/// <summary>Reads the local post-build output at &lt;repoRoot&gt;\x64\Release\a\.</summary>
public sealed class LocalBuildPayloadSource : IPayloadSource
{
    private const string RelativePayloadPath = @"x64\Release\a";

    private readonly string _repoRoot;
    private readonly IFileSystem _fs;
    private readonly IFileVersionReader _versions;

    public LocalBuildPayloadSource(string repoRoot, IFileSystem fs, IFileVersionReader versions)
    {
        _repoRoot = repoRoot;
        _fs = fs;
        _versions = versions;
    }

    public string PayloadRoot => _fs.Path.GetFullPath(_fs.Path.Combine(_repoRoot, RelativePayloadPath));

    public string Description => $"本地构建 {PayloadRoot}";

    public Task<PayloadBundle?> TryGetAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var root = PayloadRoot;
        var dll = _fs.Path.Combine(root, "OptiScaler.dll");
        var ini = _fs.Path.Combine(root, "OptiScaler.ini");

        if (!_fs.Directory.Exists(root) || !_fs.File.Exists(dll) || !_fs.File.Exists(ini))
        {
            return Task.FromResult<PayloadBundle?>(null);
        }

        var version = _versions.GetFileVersion(dll) ?? "unknown";
        return Task.FromResult<PayloadBundle?>(new PayloadBundle(root, version, PayloadOrigin.LocalBuild));
    }
}

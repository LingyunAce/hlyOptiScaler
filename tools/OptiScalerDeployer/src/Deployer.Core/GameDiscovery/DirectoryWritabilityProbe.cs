using System.IO.Abstractions;
using Deployer.Core.Domain;

namespace Deployer.Core.GameDiscovery;

public sealed class DirectoryWritabilityProbe(IFileSystem fs, Func<Guid>? idFactory = null)
{
    public DirectoryWritability Probe(string directory)
    {
        string? probe = null;
        var owned = false;
        try
        {
            var path = fs.Path.GetFullPath(directory);
            probe = fs.Path.Combine(path, $".optideploy-probe-{(idFactory?.Invoke() ?? Guid.NewGuid()):N}.tmp");
            using (fs.FileStream.New(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { owned = true; }
            fs.File.Delete(probe);
            owned = false;
            return DirectoryWritability.Writable;
        }
        catch (UnauthorizedAccessException)
        {
            if (owned && probe is not null) TryDeleteOwned(probe);
            return DirectoryWritability.NeedsElevation;
        }
        catch (IOException)
        {
            if (owned && probe is not null) TryDeleteOwned(probe);
            return DirectoryWritability.Blocked;
        }
        catch
        {
            if (owned && probe is not null) TryDeleteOwned(probe);
            return DirectoryWritability.Unknown;
        }
    }

    private void TryDeleteOwned(string path)
    {
        try { if (fs.File.Exists(path)) fs.File.Delete(path); }
        catch { }
    }
}

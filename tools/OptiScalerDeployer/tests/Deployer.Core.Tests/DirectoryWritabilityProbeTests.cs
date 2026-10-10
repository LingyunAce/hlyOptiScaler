using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using Deployer.Core.Domain;
using Deployer.Core.GameDiscovery;
using Xunit;

namespace Deployer.Core.Tests;

public class DirectoryWritabilityProbeTests
{
    [Fact]
    public void Probe_creates_and_removes_only_its_unique_file()
    {
        var fs = new MockFileSystem(); fs.AddDirectory(@"C:\game");
        var result = new DirectoryWritabilityProbe(fs, () => Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")).Probe(@"C:\game");
        Assert.Equal(DirectoryWritability.Writable, result);
        Assert.Empty(fs.Directory.GetFiles(@"C:\game"));
    }

    [Fact]
    public void CreateNew_collision_never_deletes_the_existing_file()
    {
        var root = Path.Combine(Path.GetTempPath(), "optideploy-probe-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var id = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
            var existing = Path.Combine(root, $".optideploy-probe-{id:N}.tmp");
            File.WriteAllText(existing, "keep");
            var result = new DirectoryWritabilityProbe(new FileSystem(), () => id).Probe(root);
            Assert.Equal(DirectoryWritability.Blocked, result);
            Assert.Equal("keep", File.ReadAllText(existing));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}

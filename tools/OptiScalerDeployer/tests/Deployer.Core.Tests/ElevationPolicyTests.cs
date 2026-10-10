using System.Security.Cryptography;
using System.Text;
using Deployer.Core.Elevation;
using Xunit;

namespace Deployer.Core.Tests;

public sealed class ElevationPolicyTests
{
    [Fact]
    public void RejectsChecksumMismatchAndPathEscape()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()); Directory.CreateDirectory(root);
        var plan = Path.Combine(root, "plan.json"); File.WriteAllText(plan, "{}");
        var job = new ElevationJob(Guid.Parse(Path.GetFileName(root)), plan, new string('0', 64), root);
        Assert.Throws<InvalidDataException>(() => ElevationPolicy.ValidateJob(job));
        Assert.Throws<InvalidDataException>(() => ElevationPolicy.ValidateTarget(Path.Combine(root, "..", "outside"), root));
        Directory.Delete(root, true);
    }

    [Fact]
    public void AcceptsReviewedPlanHash()
    {
        var id = Guid.NewGuid(); var root = Path.Combine(Path.GetTempPath(), id.ToString()); Directory.CreateDirectory(root);
        var plan = Path.Combine(root, "plan.json"); File.WriteAllText(plan, "{}");
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(plan))).ToLowerInvariant();
        ElevationPolicy.ValidateJob(new ElevationJob(id, plan, hash, root));
        Directory.Delete(root, true);
    }
}

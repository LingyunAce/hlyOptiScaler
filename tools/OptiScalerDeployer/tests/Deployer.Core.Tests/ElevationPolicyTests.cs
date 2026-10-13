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

    [Fact]
    public void Rejects_runas_worker_from_user_writable_location()
    {
        var root = Path.Combine(Path.GetTempPath(), "OptiScalerWorkerTrust", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var worker = Path.Combine(root, "Deployer.Worker.exe");
        File.WriteAllBytes(worker, [0]);
        try
        {
            Assert.Throws<InvalidDataException>(() => ElevationPolicy.ValidateWorkerPath(worker));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Worker_trust_policy_allows_system_owned_acl_without_untrusted_write_grants()
    {
        WorkerTrust.ValidateAclRules("S-1-5-18",
        [
            new WorkerTrustAce("S-1-5-18", 0x10000000, true),
            new WorkerTrustAce("S-1-5-32-544", 0x10000000, true),
            new WorkerTrustAce("S-1-5-32-545", 0x1, true),
        ]);
    }

    [Fact]
    public void Worker_trust_policy_rejects_user_write_grants_and_untrusted_owners()
    {
        Assert.Throws<InvalidDataException>(() => WorkerTrust.ValidateAclRules("S-1-5-18",
            [new WorkerTrustAce("S-1-5-32-545", 0x40000000, true)]));
        Assert.Throws<InvalidDataException>(() => WorkerTrust.ValidateAclRules("S-1-5-21-1-2-3-1001", []));
    }

    [Fact]
    public void Worker_trust_policy_checks_write_attributes_and_ignores_inherit_only_entries()
    {
        Assert.Throws<InvalidDataException>(() => WorkerTrust.ValidateAclRules("S-1-5-18",
            [new WorkerTrustAce("S-1-5-32-545", 0x100, true)]));
        WorkerTrust.ValidateAclRules("S-1-5-18",
            [new WorkerTrustAce("S-1-3-0", 0x10000000, true, InheritOnly: true)]);
        Assert.Throws<InvalidDataException>(() => WorkerTrust.ValidateAclRules("S-1-5-18",
            [new WorkerTrustAce("S-1-3-0", 0x10000000, true)]));
    }

    [Fact]
    public void Worker_trust_policy_allows_create_directory_only_on_locked_ancestors()
    {
        WorkerTrust.ValidateAclRules("S-1-5-18", [new WorkerTrustAce("S-1-5-11", 0x4, true)], allowCreateEntries: true);
        Assert.Throws<InvalidDataException>(() => WorkerTrust.ValidateAclRules("S-1-5-18",
            [new WorkerTrustAce("S-1-5-11", 0x4, true)]));
    }

    [Theory]
    [InlineData("bad-worker-path", true)]
    [InlineData("missing-worker.exe", false)]
    public async Task Runas_transport_never_launches_an_untrusted_or_missing_worker(string workerPath, bool invalidPath)
    {
        var id = Guid.NewGuid();
        var jobDirectory = Path.Combine(Path.GetTempPath(), id.ToString("D"));
        Directory.CreateDirectory(jobDirectory);
        var planPath = Path.Combine(jobDirectory, "plan.json");
        await File.WriteAllTextAsync(planPath, "{}");
        var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(planPath))).ToLowerInvariant();
        var job = new ElevationJob(id, planPath, hash, jobDirectory);
        var launchCalled = false;
        try
        {
            var transport = new RunAsJobTransport(Path.Combine(Path.GetTempPath(), workerPath), _ =>
            {
                launchCalled = true;
                return null;
            });
            if (invalidPath) await Assert.ThrowsAsync<InvalidDataException>(() => transport.ExecuteAsync(job, CancellationToken.None));
            else await Assert.ThrowsAsync<FileNotFoundException>(() => transport.ExecuteAsync(job, CancellationToken.None));
            Assert.False(launchCalled);
        }
        finally { Directory.Delete(jobDirectory, true); }
    }
}

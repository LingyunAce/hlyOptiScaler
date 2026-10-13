using System.IO.Abstractions;
using Deployer.Core.Deployment;
using Deployer.Core.Domain;
using Deployer.Core.Elevation;
using Deployer.Core.Identity;
using Deployer.Core.Ini;
using Deployer.Core.Planning;
using Deployer.Core.Profiles;
using Deployer.Core.Serialization;
using Deployer.TestData;
using Xunit;

namespace Deployer.Core.Tests;

public sealed class ElevationWorkerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "OptiScalerWorkerTests", Guid.NewGuid().ToString("N"));
    private DeploymentPlan Plan()
    {
        var fs = new FileSystem(); var game = Path.Combine(_root, "game"); var payload = Path.Combine(_root, "payload");
        Directory.CreateDirectory(game); Directory.CreateDirectory(payload);
        File.WriteAllText(Path.Combine(game, "Game.exe"), "game");
        File.WriteAllText(Path.Combine(payload, "OptiScaler.dll"), "new-dll");
        File.WriteAllText(Path.Combine(payload, "OptiScaler.ini"), SyntheticPayloadFactory.SampleIniContent);
        File.WriteAllText(Path.Combine(game, "OptiScaler.ini"), "old-ini");
        return new DeploymentPlanner(fs, new DllIdentitySelector(fs, new FakeFileVersionReader()), new PayloadWalker(fs), new IniPatcher())
            .Plan(new PlanRequest
            {
                Game = new("Test", GamePlatform.Manual, null, null, game, game, "Game.exe", "0123abcd"),
                Inspection = new() { GraphicsApis = [GraphicsApi.Dx12], DetectedSdks = [UpscalerSdk.Dlss], HasEngineFolder = false, IsVulkanOnly = false },
                Payload = new(payload, "0.9.1", PayloadOrigin.ManualDirectory), Profile = BuiltinProfiles.AmdRdna4,
                Writability = DirectoryWritability.NeedsElevation, BackupRoot = Path.Combine(_root, "backups"),
            });
    }
    private ElevationJob Job(DeploymentPlan plan) => ElevationJobStore.Create(plan, Path.Combine(_root, "backups"), Path.Combine(_root, "jobs"));

    [Fact]
    public async Task Reviewed_job_executes_real_files_and_manifest_can_restore_originals()
    {
        var plan = Plan(); var job = Job(plan);
        var result = await ElevationWorker.ExecuteAsync(job);
        Assert.True(result.Success, result.Error);
        Assert.Equal("new-dll", File.ReadAllText(Path.Combine(plan.Game.DeployDirectory, plan.Identity.FileName)));
        Assert.True(File.Exists(Path.Combine(job.JobDirectory, "result.json")));
        var fs = new FileSystem(); var store = new BackupStore(fs, Path.Combine(_root, "backups"));
        Assert.True(new RollbackExecutor(fs, new AtomicFileWriter(fs), store).Rollback(result.ManifestPath!).Success);
        Assert.Equal("old-ini", File.ReadAllText(Path.Combine(plan.Game.DeployDirectory, "OptiScaler.ini")));
    }

    [Fact]
    public async Task Tampered_plan_is_rejected_without_game_writes()
    {
        var plan = Plan(); var job = Job(plan); File.AppendAllText(job.PlanPath, " ");
        var result = await ElevationWorker.ExecuteAsync(job);
        Assert.False(result.Success); Assert.Equal("Rejected", result.Status);
        Assert.Equal("old-ini", File.ReadAllText(Path.Combine(plan.Game.DeployDirectory, "OptiScaler.ini")));
    }

    [Fact]
    public async Task Escaping_destination_and_payload_source_are_rejected()
    {
        foreach (var escapeSource in new[] { false, true })
        {
            var plan = Plan(); var first = plan.Writes[0];
            var bad = escapeSource ? first with { SourcePath = Path.Combine(_root, "private.txt") } : first with { DestinationPath = Path.Combine(_root, "outside.ini") };
            var result = await ElevationWorker.ExecuteAsync(Job(plan with { Writes = [bad, .. plan.Writes.Skip(1)] }));
            Assert.False(result.Success); Assert.Equal("Rejected", result.Status);
            Assert.False(File.Exists(Path.Combine(_root, "outside.ini")));
        }
    }

    [Fact]
    public async Task Cancel_marker_stops_before_writes_and_replay_is_refused()
    {
        var plan = Plan(); var job = Job(plan); File.WriteAllText(Path.Combine(job.JobDirectory, "cancel"), "");
        var result = await ElevationWorker.ExecuteAsync(job);
        Assert.Equal("Cancelled", result.Status); Assert.False(result.Success);
        Assert.Equal("old-ini", File.ReadAllText(Path.Combine(plan.Game.DeployDirectory, "OptiScaler.ini")));
        var again = await ElevationWorker.ExecuteAsync(job);
        Assert.False(again.Success);
        Assert.Equal("Cancelled", DeployerJson.Deserialize<ElevationResult>(File.ReadAllText(Path.Combine(job.JobDirectory, "result.json"))).Status);
    }

    [Fact]
    public void Windows_root_is_not_an_approved_game_target()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Assert.Throws<InvalidDataException>(() => ElevationPolicy.ValidateTarget(windows, windows));
    }

    [Fact]
    public async Task Forged_manifest_cannot_authorize_elevated_deletion_outside_reviewed_plan()
    {
        var plan = Plan(); var root = Path.Combine(_root, "backups");
        var validate = ElevationWorker.CreateRecoveryValidator(new(plan, root));
        var result = await ElevationWorker.ExecuteAsync(Job(plan)); Assert.True(result.Success, result.Error);
        var outside = Path.Combine(_root, "outside"); Directory.CreateDirectory(outside);
        var victim = Path.Combine(outside, "keep.txt"); File.WriteAllText(victim, "keep");
        var fs = new FileSystem(); var store = new BackupStore(fs, root); var manifest = store.ReadManifest(result.ManifestPath!)!;
        store.WriteManifest(manifest with { DeployDirectory = outside, WrittenPaths = [victim], Backups = [], CreatedDirectories = [] });
        var rollback = new RollbackExecutor(fs, new AtomicFileWriter(fs), store, validate).Rollback(result.ManifestPath!);
        Assert.False(rollback.Success); Assert.Equal("keep", File.ReadAllText(victim));
    }

    [Fact]
    public async Task Partial_worker_recovery_accepts_only_next_pending_write_and_restores_existing_ini()
    {
        var plan = Plan();
        var root = Path.Combine(_root, "backups");
        var validate = ElevationWorker.CreateRecoveryValidator(new(plan, root));
        var result = await ElevationWorker.ExecuteAsync(Job(plan));
        Assert.True(result.Success, result.Error);

        var ini = Path.Combine(plan.Game.DeployDirectory, "OptiScaler.ini");
        var iniIndex = plan.Writes.ToList().FindIndex(write =>
            Path.GetFullPath(write.DestinationPath).Equals(Path.GetFullPath(ini), StringComparison.OrdinalIgnoreCase));
        Assert.True(iniIndex >= 0);
        var manifestPath = result.ManifestPath!;
        var fs = new FileSystem();
        var store = new BackupStore(fs, root);
        var manifest = store.ReadManifest(manifestPath)!;
        var prefix = plan.Writes.Take(iniIndex).Select(write => write.DestinationPath).ToArray();
        var pending = new PendingDeploymentWrite(ini, plan.Writes[iniIndex].Sha256);
        store.WriteManifest(manifest with
        {
            WrittenPaths = prefix,
            PendingWrite = pending,
            RecoveryStateVersion = 1,
        });

        var rollback = new RollbackExecutor(fs, new AtomicFileWriter(fs), store, validate).Rollback(manifestPath);

        Assert.True(rollback.Success, rollback.ErrorMessage);
        Assert.Equal("old-ini", File.ReadAllText(ini));

        var forged = manifest with
        {
            WrittenPaths = Array.Empty<string>(),
            PendingWrite = new PendingDeploymentWrite(plan.Game.DeployDirectory + Path.DirectorySeparatorChar + "not-in-plan.dll", new string('0', 64)),
            RecoveryStateVersion = 1,
        };
        Assert.Throws<InvalidDataException>(() => validate(forged));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}

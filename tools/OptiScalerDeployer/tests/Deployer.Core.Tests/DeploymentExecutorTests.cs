using System.IO.Abstractions.TestingHelpers;
using Deployer.Core.Deployment;
using Deployer.Core.Domain;
using Deployer.Core.Identity;
using Deployer.Core.Ini;
using Deployer.Core.Planning;
using Deployer.Core.Profiles;
using Deployer.TestData;
using Xunit;

namespace Deployer.Core.Tests;

public class DeploymentExecutorTests
{
    private const string PayloadRoot = @"D:\repo\x64\Release\a";
    private const string BackupRoot = @"C:\backup-root";
    private readonly MockFileSystem _fs = new();
    private readonly FakeFileVersionReader _versions = new();

    private DeploymentPlan BuildPlan(Guid? planId = null, bool existingIdentity = false)
    {
        SyntheticPayloadFactory.Create(_fs, PayloadRoot);
        var game = FakeGameFactory.CreateUeGame(_fs);
        if (existingIdentity)
        {
            var existing = Path.Combine(game.DeployDirectory, "dxgi.dll");
            _fs.AddFile(existing, new MockFileData("game-owned original"));
            _versions.SetOriginalFilename(existing, "OptiScaler.dll");
        }
        var planner = new DeploymentPlanner(_fs, new DllIdentitySelector(_fs, _versions),
            new PayloadWalker(_fs), new IniPatcher());
        var platform = GamePlatform.Steam;
        var target = new GameTarget(game.Title, platform, null, null, game.InstallRoot, game.DeployDirectory,
            game.ExecutableName, new FingerprintCalculator().Compute(platform, null, game.InstallRoot, game.ExecutableName));
        return planner.Plan(new PlanRequest
        {
            Game = target,
            Inspection = game.DefaultInspection,
            Payload = new PayloadBundle(PayloadRoot, SyntheticPayloadFactory.DefaultVersion, PayloadOrigin.LocalBuild),
            Profile = BuiltinProfiles.AmdRdna4,
            Writability = DirectoryWritability.Writable,
            BackupRoot = BackupRoot,
            PlanId = planId,
        });
    }

    private DeploymentExecutor Executor(IBackupStore? store = null)
    {
        var backups = store ?? new BackupStore(_fs, BackupRoot);
        var writer = new AtomicFileWriter(_fs);
        return new DeploymentExecutor(_fs, writer, backups, new RollbackExecutor(_fs, writer, backups));
    }

    private sealed class HookBackupStore : IBackupStore
    {
        private readonly IBackupStore _inner;
        public Action? BeforeManifest { get; init; }
        public Action<DeploymentManifest>? ManifestHook { get; init; }
        public Func<BackupEntry, BackupEntry>? BackupHook { get; init; }
        public Action<DeploymentProgress>? AfterProgress { get; init; }

        public HookBackupStore(IBackupStore inner) => _inner = inner;
        public string DirectoryFor(string f, Guid id) => _inner.DirectoryFor(f, id);
        public string ManifestPathFor(string f, Guid id) => _inner.ManifestPathFor(f, id);
        public string ProgressPathFor(string f, Guid id) => _inner.ProgressPathFor(f, id);
        public BackupEntry Backup(BackupEntry entry) => BackupHook is null ? _inner.Backup(entry) : BackupHook(entry);
        public void WriteManifest(DeploymentManifest manifest)
        {
            BeforeManifest?.Invoke();
            ManifestHook?.Invoke(manifest);
            _inner.WriteManifest(manifest);
        }
        public DeploymentManifest? ReadManifest(string path) => _inner.ReadManifest(path);
        public void WriteProgress(DeploymentProgress progress)
        {
            _inner.WriteProgress(progress);
            AfterProgress?.Invoke(progress);
        }
        public DeploymentProgress? ReadProgress(string path) => _inner.ReadProgress(path);
        public IReadOnlyList<Guid> ListPlanIds(string f) => _inner.ListPlanIds(f);
        public IReadOnlyList<Guid> Prune(string f, int keep) => _inner.Prune(f, keep);
    }

    [Fact]
    public void Successful_deployment_writes_ini_without_bom_and_identity_last()
    {
        var plan = BuildPlan();

        var result = Executor().Execute(plan, CancellationToken.None);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(plan.Writes.Count, result.CompletedSteps.Count);
        Assert.Equal(plan.Identity.FileName, Path.GetFileName(result.CompletedSteps[^1]));
        var ini = _fs.File.ReadAllBytes(Path.Combine(plan.Game.DeployDirectory, "OptiScaler.ini"));
        Assert.False(ini.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }));
        Assert.Contains("Dx12Upscaler=ffx", System.Text.Encoding.UTF8.GetString(ini));
    }

    [Fact]
    public void Existing_game_file_is_backed_up_then_restored_on_manual_rollback()
    {
        var planId = Guid.NewGuid();
        var game = FakeGameFactory.CreateUeGame(_fs);
        var existing = Path.Combine(game.DeployDirectory, "dxgi.dll");
        _fs.AddFile(existing, new MockFileData("game-owned bytes"));
        _versions.SetOriginalFilename(existing, "OptiScaler.dll");
        var plan = BuildPlan(planId);
        var store = new BackupStore(_fs, BackupRoot);
        var executor = Executor(store);

        var result = executor.Execute(plan, CancellationToken.None);
        var rollback = new RollbackExecutor(_fs, new AtomicFileWriter(_fs), store)
            .Rollback(result.ManifestPath);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(rollback.Success, rollback.ErrorMessage);
        Assert.Equal("game-owned bytes", _fs.File.ReadAllText(existing));
    }

    [Fact]
    public void Mid_deployment_checksum_failure_rolls_back_new_files()
    {
        var plan = BuildPlan();
        var writes = plan.Writes.ToList();
        var index = Math.Max(0, writes.Count / 2);
        writes[index] = writes[index] with { Sha256 = new string('0', 64) };
        var broken = plan with { Writes = writes };

        var result = Executor().Execute(broken, CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(result.RolledBack);
        Assert.False(_fs.File.Exists(Path.Combine(plan.Game.DeployDirectory, plan.Identity.FileName)));
        Assert.True(_fs.File.Exists(Path.Combine(plan.Game.DeployDirectory, plan.Game.ExecutableName)));
    }

    [Fact]
    public void Recovery_manifest_never_authorizes_a_future_plan_path()
    {
        var plan = BuildPlan();
        var inner = new BackupStore(_fs, BackupRoot);
        var laterPath = plan.Writes[^2].DestinationPath;
        var failedAfterFirstProgress = false;
        var store = new HookBackupStore(inner)
        {
            AfterProgress = progress =>
            {
                if (!failedAfterFirstProgress && progress.CompletedSteps.Count == 1)
                {
                    failedAfterFirstProgress = true;
                    _fs.AddFile(laterPath, new MockFileData("created by another process"));
                }
            },
        };

        var result = Executor(store).Execute(plan, CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(result.RolledBack);
        Assert.Equal("created by another process", _fs.File.ReadAllText(laterPath));
    }

    [Fact]
    public void Pending_write_recovers_file_when_completed_manifest_publish_fails_after_atomic_write()
    {
        var plan = BuildPlan(existingIdentity: true);
        var original = plan.Backups[0].OriginalPath;
        var inner = new BackupStore(_fs, BackupRoot);
        var target = plan.Writes.Single(write => Path.GetFullPath(write.DestinationPath)
            .Equals(Path.GetFullPath(original), StringComparison.OrdinalIgnoreCase)).DestinationPath;
        var failed = false;
        var store = new HookBackupStore(inner)
        {
            ManifestHook = manifest =>
            {
                if (!failed && manifest.WrittenPaths.Contains(target, StringComparer.OrdinalIgnoreCase))
                {
                    failed = true;
                    throw new IOException("simulated crash after game write before completed manifest");
                }
            },
        };

        var result = Executor(store).Execute(plan, CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(result.RolledBack);
        Assert.Equal("game-owned original", _fs.File.ReadAllText(original));
        Assert.True(_fs.File.Exists(target));
    }

    [Fact]
    public void Rollback_does_not_delete_a_file_created_after_manifest_before_first_write()
    {
        var plan = BuildPlan();
        var inner = new BackupStore(_fs, BackupRoot);
        var laterPath = plan.Writes[^2].DestinationPath;
        var laterDirectory = Path.Combine(plan.Game.DeployDirectory, "OptiScaler");
        var emptyProgressWrites = 0;
        var store = new HookBackupStore(inner)
        {
            AfterProgress = progress =>
            {
                if (progress.CompletedSteps.Count == 0 && ++emptyProgressWrites == 2)
                {
                    _fs.AddFile(laterPath, new MockFileData("created after manifest"));
                    _fs.AddDirectory(laterDirectory);
                    throw new IOException("simulated interruption before first write");
                }
            },
        };

        var result = Executor(store).Execute(plan, CancellationToken.None);
        Assert.False(result.Success);
        Assert.True(_fs.File.Exists(result.ManifestPath));
        var savedManifest = inner.ReadManifest(result.ManifestPath)!;
        var savedProgress = inner.ReadProgress(inner.ProgressPathFor(savedManifest.Fingerprint, savedManifest.PlanId))!;
        Assert.Empty(savedProgress.CompletedSteps);
        Assert.Empty(savedManifest.WrittenPaths);
        Assert.Contains(laterDirectory, savedManifest.CreatedDirectories);
        Assert.True(_fs.Directory.Exists(laterDirectory));

        var rollback = new RollbackExecutor(_fs, new AtomicFileWriter(_fs), inner).Rollback(result.ManifestPath);

        Assert.True(rollback.Success, rollback.ErrorMessage);
        Assert.Equal("created after manifest", _fs.File.ReadAllText(laterPath));
        Assert.True(_fs.Directory.Exists(laterDirectory));
    }

    [Fact]
    public void Stale_plan_never_overwrites_a_file_that_appeared_after_planning()
    {
        var plan = BuildPlan();
        var lateFile = plan.Writes.First(w => w.InlineContent is null).DestinationPath;
        _fs.AddFile(lateFile, new MockFileData("user file created after planning"));

        var result = Executor().Execute(plan, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("user file created after planning", _fs.File.ReadAllText(lateFile));
        Assert.False(_fs.File.Exists(Path.Combine(plan.Game.DeployDirectory, plan.Identity.FileName)));
    }

    [Fact]
    public void Existing_temp_artifact_is_rejected_before_any_manifest_or_game_change()
    {
        var plan = BuildPlan();
        var first = plan.Writes[0].DestinationPath;
        var temp = first + ".optideploy.tmp";
        _fs.AddFile(temp, new MockFileData("user file"));
        var store = new BackupStore(_fs, BackupRoot);

        var result = Executor(store).Execute(plan, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("user file", _fs.File.ReadAllText(temp));
        Assert.False(_fs.File.Exists(store.ManifestPathFor(plan.Game.Fingerprint, plan.PlanId)));
    }

    [Fact]
    public void Backup_failure_leaves_game_unchanged_and_manifest_non_destructive()
    {
        var plan = BuildPlan(existingIdentity: true);
        var original = plan.Backups[0].OriginalPath;
        var before = _fs.File.ReadAllText(original);
        var inner = new BackupStore(_fs, BackupRoot);
        var store = new HookBackupStore(inner) { BackupHook = _ => throw new IOException("backup failed") };

        var result = Executor(store).Execute(plan, CancellationToken.None);
        var manifest = inner.ReadManifest(inner.ManifestPathFor(plan.Game.Fingerprint, plan.PlanId));

        Assert.False(result.Success);
        Assert.False(result.RolledBack);
        Assert.Equal(before, _fs.File.ReadAllText(original));
        Assert.Empty(manifest!.WrittenPaths);
        Assert.Empty(manifest.Backups);
        var rollback = new RollbackExecutor(_fs, new AtomicFileWriter(_fs), inner)
            .Rollback(inner.ManifestPathFor(plan.Game.Fingerprint, plan.PlanId));
        Assert.True(rollback.Success, rollback.ErrorMessage);
        Assert.Equal(before, _fs.File.ReadAllText(original));
    }

    [Fact]
    public void First_manifest_failure_is_returned_without_throwing_or_touching_game()
    {
        var plan = BuildPlan();
        var store = new HookBackupStore(new BackupStore(_fs, BackupRoot))
        {
            BeforeManifest = () => throw new IOException("manifest disk failure"),
        };

        var result = Executor(store).Execute(plan, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("manifest disk failure", result.ErrorMessage);
        Assert.Empty(result.CompletedSteps);
        Assert.False(_fs.File.Exists(Path.Combine(plan.Game.DeployDirectory, plan.Identity.FileName)));
    }

    [Fact]
    public void Cancellation_after_a_write_rolls_back_the_game()
    {
        var plan = BuildPlan();
        using var cts = new CancellationTokenSource();
        var store = new HookBackupStore(new BackupStore(_fs, BackupRoot))
        {
            AfterProgress = progress =>
            {
                if (progress.CompletedSteps.Count == 1) cts.Cancel();
            },
        };

        Assert.ThrowsAny<OperationCanceledException>(() => Executor(store).Execute(plan, cts.Token));

        Assert.False(_fs.File.Exists(Path.Combine(plan.Game.DeployDirectory, plan.Identity.FileName)));
        Assert.False(_fs.File.Exists(plan.Writes[0].DestinationPath));
    }

    [Fact]
    public void Backup_outside_this_plan_directory_is_rejected_before_manifest()
    {
        var plan = BuildPlan(existingIdentity: true);
        var outsidePlan = plan with
        {
            Backups = plan.Backups.Select(entry => entry with
            {
                BackupPath = Path.Combine(BackupRoot, plan.Game.Fingerprint, Guid.NewGuid().ToString("D"), "dxgi.dll"),
            }).ToList(),
        };
        var store = new BackupStore(_fs, BackupRoot);

        var result = Executor(store).Execute(outsidePlan, CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(_fs.File.Exists(store.ManifestPathFor(plan.Game.Fingerprint, plan.PlanId)));
        Assert.Equal("game-owned original", _fs.File.ReadAllText(plan.Backups[0].OriginalPath));
    }

    [Fact]
    public void Identity_file_must_remain_the_final_write()
    {
        var plan = BuildPlan();
        var reordered = plan with { Writes = plan.Writes.Skip(1).Append(plan.Writes[0]).ToList() };
        var store = new BackupStore(_fs, BackupRoot);

        var result = Executor(store).Execute(reordered, CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(_fs.File.Exists(store.ManifestPathFor(plan.Game.Fingerprint, plan.PlanId)));
        Assert.False(_fs.File.Exists(Path.Combine(plan.Game.DeployDirectory, plan.Identity.FileName)));
    }

    [Fact]
    public void Reusing_plan_id_does_not_replace_the_original_backup()
    {
        var planId = Guid.NewGuid();
        var game = FakeGameFactory.CreateUeGame(_fs);
        var existing = Path.Combine(game.DeployDirectory, "dxgi.dll");
        _fs.AddFile(existing, new MockFileData("game-owned original"));
        _versions.SetOriginalFilename(existing, "OptiScaler.dll");
        var plan = BuildPlan(planId);
        var store = new BackupStore(_fs, BackupRoot);
        var executor = Executor(store);
        var first = executor.Execute(plan, CancellationToken.None);
        var backup = Assert.Single(plan.Backups);
        var before = _fs.File.ReadAllText(backup.BackupPath);

        var second = executor.Execute(plan, CancellationToken.None);
        var rollback = new RollbackExecutor(_fs, new AtomicFileWriter(_fs), store).Rollback(first.ManifestPath);

        Assert.True(first.Success, first.ErrorMessage);
        Assert.False(second.Success);
        Assert.Equal(before, _fs.File.ReadAllText(backup.BackupPath));
        Assert.True(rollback.Success, rollback.ErrorMessage);
        Assert.Equal("game-owned original", _fs.File.ReadAllText(existing));
    }

    [Fact]
    public void Requires_user_decision_identity_is_rejected_before_manifest_write()
    {
        var plan = BuildPlan();
        var blocked = plan with
        {
            Identity = plan.Identity with { Status = IdentityStatus.RequiresUserDecision },
        };
        var store = new BackupStore(_fs, BackupRoot);

        var result = Executor(store).Execute(blocked, CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(_fs.File.Exists(store.ManifestPathFor(plan.Game.Fingerprint, plan.PlanId)));
    }

    [Fact]
    public void Blocking_warning_does_not_write_manifest_or_game_files()
    {
        var plan = BuildPlan();
        var blocked = plan with
        {
            Warnings = plan.Warnings.Append(new PlanWarning(WarningSeverity.Blocking, "blocked")).ToList(),
        };
        var store = new BackupStore(_fs, BackupRoot);

        var result = Executor(store).Execute(blocked, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Empty(result.CompletedSteps);
        Assert.False(_fs.File.Exists(store.ManifestPathFor(plan.Game.Fingerprint, plan.PlanId)));
    }
}

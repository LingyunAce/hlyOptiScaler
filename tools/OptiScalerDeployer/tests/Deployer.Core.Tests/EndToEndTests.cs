using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using Deployer.Core.Deployment;
using Deployer.Core.Domain;
using Deployer.Core.Identity;
using Deployer.Core.Ini;
using Deployer.Core.Persistence;
using Deployer.Core.Planning;
using Deployer.Core.Profiles;
using Deployer.TestData;
using Xunit;

namespace Deployer.Core.Tests;

/// <summary>Exercises planning, deployment, and rollback together using only MockFileSystem.</summary>
public class EndToEndTests
{
    private const string PayloadRoot = @"D:\repo\x64\Release\a";
    private const string BackupRoot = @"C:\backup-root";
    private readonly MockFileSystem _fs = new();
    private readonly FakeFileVersionReader _versions = new();

    private sealed record Snapshot(IReadOnlyDictionary<string, string> Files, IReadOnlyList<string> Directories);

    private sealed class CancellingBackupStore(IBackupStore inner, CancellationTokenSource cts) : IBackupStore
    {
        public string DirectoryFor(string fingerprint, Guid planId) => inner.DirectoryFor(fingerprint, planId);
        public string ManifestPathFor(string fingerprint, Guid planId) => inner.ManifestPathFor(fingerprint, planId);
        public string ProgressPathFor(string fingerprint, Guid planId) => inner.ProgressPathFor(fingerprint, planId);
        public BackupEntry Backup(BackupEntry entry) => inner.Backup(entry);
        public void WriteManifest(DeploymentManifest manifest) => inner.WriteManifest(manifest);
        public DeploymentManifest? ReadManifest(string path) => inner.ReadManifest(path);
        public void WriteProgress(DeploymentProgress progress)
        {
            inner.WriteProgress(progress);
            if (progress.CompletedSteps.Count > 0)
            {
                cts.Cancel();
            }
        }
        public DeploymentProgress? ReadProgress(string path) => inner.ReadProgress(path);
        public IReadOnlyList<Guid> ListPlanIds(string fingerprint) => inner.ListPlanIds(fingerprint);
        public IReadOnlyList<Guid> Prune(string fingerprint, int keep) => inner.Prune(fingerprint, keep);
    }

    private Snapshot Take(string root)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in _fs.Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            files[path] = Deployer.Core.Util.Hashing.Sha256OfFile(_fs, path);
        }

        var directories = _fs.Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(path => path, StringComparer.Ordinal)
            .ToArray();
        return new Snapshot(files, directories);
    }

    private static void AssertSnapshotEqual(Snapshot expected, Snapshot actual)
    {
        Assert.Equal(expected.Files.Count, actual.Files.Count);
        foreach (var (path, hash) in expected.Files)
        {
            Assert.True(actual.Files.TryGetValue(path, out var currentHash), $"回滚后文件丢失：{path}");
            Assert.Equal(hash, currentHash);
        }
        Assert.Equal(expected.Directories, actual.Directories);
    }

    private (DeploymentPlan Plan, DeploymentExecutor Executor, RollbackExecutor Rollback, BackupStore Store)
        Arrange(
            FakeGame game,
            GamePlatform platform,
            UpscalerProfile? profile = null,
            bool frameGen = false,
            bool preferAsi = false,
            Guid? planId = null)
    {
        if (!_fs.Directory.Exists(PayloadRoot))
        {
            SyntheticPayloadFactory.Create(_fs, PayloadRoot);
        }

        var store = new BackupStore(_fs, BackupRoot);
        var writer = new AtomicFileWriter(_fs);
        var rollback = new RollbackExecutor(_fs, writer, store);
        var executor = new DeploymentExecutor(_fs, writer, store, rollback);
        var planner = new DeploymentPlanner(
            _fs, new DllIdentitySelector(_fs, _versions), new PayloadWalker(_fs), new IniPatcher());
        var fingerprint = new FingerprintCalculator().Compute(
            platform, null, game.InstallRoot, game.ExecutableName);
        var target = new GameTarget(game.Title, platform, null, null, game.InstallRoot,
            game.DeployDirectory, game.ExecutableName, fingerprint);
        var plan = planner.Plan(new PlanRequest
        {
            Game = target,
            Inspection = game.DefaultInspection,
            Payload = new PayloadBundle(PayloadRoot, SyntheticPayloadFactory.DefaultVersion, PayloadOrigin.LocalBuild),
            Profile = profile ?? BuiltinProfiles.AmdRdna4,
            Writability = DirectoryWritability.Writable,
            BackupRoot = BackupRoot,
            EnableFrameGen = frameGen,
            AllowOverwriteGameFile = true,
            PreferAsiIdentity = preferAsi,
            PlanId = planId,
        });
        return (plan, executor, rollback, store);
    }

    public static TheoryData<string> GameKinds => new() { "ue", "gdk", "conflict" };

    private static FakeGame MakeGame(MockFileSystem fs, string kind) => kind switch
    {
        "ue" => FakeGameFactory.CreateUeGame(fs),
        "gdk" => FakeGameFactory.CreateGdkGame(fs),
        "conflict" => FakeGameFactory.CreateConflictGame(fs),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static GamePlatform PlatformFor(string kind) => kind switch
    {
        "gdk" => GamePlatform.Xbox,
        "conflict" => GamePlatform.Manual,
        _ => GamePlatform.Steam,
    };

    [Theory]
    [MemberData(nameof(GameKinds))]
    public void Deploy_then_rollback_restores_files_and_directories_exactly(string kind)
    {
        var game = MakeGame(_fs, kind);
        var before = Take(game.InstallRoot);
        var (plan, executor, rollback, _) = Arrange(game, PlatformFor(kind));

        var deployed = executor.Execute(plan, CancellationToken.None);
        Assert.True(deployed.Success, deployed.ErrorMessage);
        var rolledBack = rollback.Rollback(deployed.ManifestPath);

        Assert.True(rolledBack.Success, rolledBack.ErrorMessage);
        AssertSnapshotEqual(before, Take(game.InstallRoot));
    }

    [Theory]
    [MemberData(nameof(GameKinds))]
    public void Deployment_lands_identity_dll_as_the_final_ignition_switch(string kind)
    {
        var game = MakeGame(_fs, kind);
        var (plan, executor, _, _) = Arrange(game, PlatformFor(kind));

        var result = executor.Execute(plan, CancellationToken.None);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(plan.Writes[^1].DestinationPath, result.CompletedSteps[^1]);
        var identityPath = _fs.Path.Combine(plan.Game.DeployDirectory, plan.Identity.FileName);
        Assert.Equal("SYNTHETIC::OptiScaler.dll::v0.9.1.0", _fs.File.ReadAllText(identityPath));
    }

    [Fact]
    public void Rollback_removes_the_new_identity_and_restores_game_owned_candidates()
    {
        var game = FakeGameFactory.CreateConflictGame(_fs);
        var paths = new[] { "version.dll", "winmm.dll", "d3d12.dll" }
            .Select(name => _fs.Path.Combine(game.DeployDirectory, name)).ToArray();
        var before = paths.ToDictionary(path => path, path => Deployer.Core.Util.Hashing.Sha256OfFile(_fs, path));
        var (plan, executor, rollback, _) = Arrange(game, GamePlatform.Manual);

        var deployed = executor.Execute(plan, CancellationToken.None);
        Assert.True(deployed.Success, deployed.ErrorMessage);
        Assert.True(_fs.File.Exists(_fs.Path.Combine(game.DeployDirectory, plan.Identity.FileName)));

        var result = rollback.Rollback(deployed.ManifestPath);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.False(_fs.File.Exists(_fs.Path.Combine(game.DeployDirectory, plan.Identity.FileName)));
        foreach (var (path, expectedHash) in before)
        {
            Assert.Equal(expectedHash, Deployer.Core.Util.Hashing.Sha256OfFile(_fs, path));
        }
    }

    [Fact]
    public void Reusing_a_plan_id_is_rejected_without_replacing_the_first_backup()
    {
        var game = FakeGameFactory.CreateUeGame(_fs);
        var before = Take(game.InstallRoot);
        var planId = Guid.NewGuid();
        var (plan, executor, rollback, _) = Arrange(game, GamePlatform.Steam, planId: planId);

        var first = executor.Execute(plan, CancellationToken.None);
        Assert.True(first.Success, first.ErrorMessage);
        var deployed = Take(game.InstallRoot);
        _versions.SetOriginalFilename(
            _fs.Path.Combine(game.DeployDirectory, plan.Identity.FileName), "OptiScaler.dll");
        var secondPlan = Arrange(game, GamePlatform.Steam, planId: planId).Plan;
        var second = executor.Execute(secondPlan, CancellationToken.None);
        Assert.False(second.Success);
        Assert.Contains("PlanId", second.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        AssertSnapshotEqual(deployed, Take(game.InstallRoot));

        var restored = rollback.Rollback(first.ManifestPath);
        Assert.True(restored.Success, restored.ErrorMessage);
        AssertSnapshotEqual(before, Take(game.InstallRoot));
    }

    [Fact]
    public void Four_sequential_plans_keep_all_manifests_and_backups_for_reverse_rollback()
    {
        var game = FakeGameFactory.CreateUeGame(_fs);
        var asiPath = _fs.Path.Combine(game.DeployDirectory, "OptiScaler.asi");
        var profiles = new[]
        {
            BuiltinProfiles.AmdRdna4,
            BuiltinProfiles.NvidiaRtx,
            BuiltinProfiles.AmdRdna4Native,
            BuiltinProfiles.AmdRdna4,
        };
        var manifests = new List<(string Path, Snapshot After)>(profiles.Length);
        var firstBefore = Take(game.InstallRoot);
        BackupStore? sharedStore = null;

        foreach (var profile in profiles)
        {
            if (_fs.File.Exists(asiPath))
            {
                _versions.SetOriginalFilename(asiPath, "OptiScaler.dll");
            }

            var (plan, executor, _, store) = Arrange(
                game, GamePlatform.Steam, profile: profile, preferAsi: true, planId: Guid.NewGuid());
            sharedStore = store;
            var result = executor.Execute(plan, CancellationToken.None);
            Assert.True(result.Success, result.ErrorMessage);
            manifests.Add((result.ManifestPath, Take(game.InstallRoot)));
        }

        var storeForPlans = sharedStore ?? throw new InvalidOperationException("No plan was executed.");
        var fingerprint = storeForPlans.ReadManifest(manifests[0].Path)!.Fingerprint;
        Assert.Equal(profiles.Length, storeForPlans.ListPlanIds(fingerprint).Count);
        foreach (var (path, _) in manifests)
        {
            var manifest = storeForPlans.ReadManifest(path)!;
            Assert.True(_fs.File.Exists(path), $"manifest 不应被自动清理：{path}");
            Assert.All(manifest.Backups, backup => Assert.True(_fs.File.Exists(backup.BackupPath), backup.BackupPath));
        }

        // 每次回滚只能还原到前一个已部署版本，最终才回到最初目录；PlanId 的历史备份均须保留。
        for (var index = manifests.Count - 1; index >= 0; index--)
        {
            var result = new RollbackExecutor(_fs, new AtomicFileWriter(_fs), storeForPlans)
                .Rollback(manifests[index].Path);
            Assert.True(result.Success, result.ErrorMessage);
            var expected = index == 0 ? firstBefore : manifests[index - 1].After;
            AssertSnapshotEqual(expected, Take(game.InstallRoot));
        }
    }

    [Fact]
    public void Mid_deployment_failure_restores_the_original_directory()
    {
        var game = FakeGameFactory.CreateUeGame(_fs);
        var before = Take(game.InstallRoot);
        var (plan, executor, _, _) = Arrange(game, GamePlatform.Steam);
        var writes = plan.Writes.ToArray();
        var index = Math.Max(1, writes.Length / 2);
        writes[index] = writes[index] with { Sha256 = new string('0', 64) };

        var result = executor.Execute(plan with { Writes = writes }, CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(result.RolledBack);
        AssertSnapshotEqual(before, Take(game.InstallRoot));
    }

    [Fact]
    public void Mid_deployment_cancellation_restores_the_original_directory()
    {
        var game = FakeGameFactory.CreateUeGame(_fs);
        var before = Take(game.InstallRoot);
        var (plan, _, rollback, store) = Arrange(game, GamePlatform.Steam);
        using var cts = new CancellationTokenSource();
        var cancelAfterFirstWrite = new CancellingBackupStore(store, cts);
        var writer = new AtomicFileWriter(_fs);
        var executor = new DeploymentExecutor(_fs, writer, cancelAfterFirstWrite,
            new RollbackExecutor(_fs, writer, store));

        Assert.Throws<OperationCanceledException>(() => executor.Execute(plan, cts.Token));
        AssertSnapshotEqual(before, Take(game.InstallRoot));
    }

    [Fact]
    public void Rollback_preserves_unrelated_mod_and_temporary_artifacts()
    {
        var game = FakeGameFactory.CreateUeGame(_fs);
        var mod = _fs.Path.Combine(game.InstallRoot, "CommunityMod", "keep.dat");
        var temp = _fs.Path.Combine(game.DeployDirectory, "user-note.optideploy.tmp");
        _fs.AddFile(mod, new MockFileData("user mod bytes"));
        _fs.AddFile(temp, new MockFileData("user temp bytes"));
        var before = Take(game.InstallRoot);
        var (plan, executor, rollback, _) = Arrange(game, GamePlatform.Steam);

        var deployed = executor.Execute(plan, CancellationToken.None);
        Assert.True(deployed.Success, deployed.ErrorMessage);
        var result = rollback.Rollback(deployed.ManifestPath);

        Assert.True(result.Success, result.ErrorMessage);
        AssertSnapshotEqual(before, Take(game.InstallRoot));
        Assert.Equal("user mod bytes", _fs.File.ReadAllText(mod));
        Assert.Equal("user temp bytes", _fs.File.ReadAllText(temp));
    }

    [Fact]
    public void Two_games_have_independent_backup_and_ledger_entries()
    {
        var ue = FakeGameFactory.CreateUeGame(_fs);
        var conflict = FakeGameFactory.CreateConflictGame(_fs);
        var conflictBefore = Take(conflict.InstallRoot);
        var (uePlan, ueExecutor, ueRollback, _) = Arrange(ue, GamePlatform.Steam);
        var (conflictPlan, conflictExecutor, _, _) = Arrange(conflict, GamePlatform.Manual);

        Assert.NotEqual(uePlan.Game.Fingerprint, conflictPlan.Game.Fingerprint);
        var ueResult = ueExecutor.Execute(uePlan, CancellationToken.None);
        var conflictResult = conflictExecutor.Execute(conflictPlan, CancellationToken.None);
        Assert.True(ueResult.Success, ueResult.ErrorMessage);
        Assert.True(conflictResult.Success, conflictResult.ErrorMessage);
        var conflictDeployed = Take(conflict.InstallRoot);
        Assert.NotEqual(_fs.Path.GetDirectoryName(ueResult.ManifestPath),
            _fs.Path.GetDirectoryName(conflictResult.ManifestPath));

        var ledger = new JsonInstallLedger(_fs, @"C:\ledger\ledger.json");
        ledger.Save(RecordFor(uePlan));
        ledger.Save(RecordFor(conflictPlan));
        Assert.NotNull(ledger.Get(uePlan.Game.Fingerprint));
        Assert.NotNull(ledger.Get(conflictPlan.Game.Fingerprint));

        Assert.True(ledger.Remove(uePlan.Game.Fingerprint));
        Assert.Null(ledger.Get(uePlan.Game.Fingerprint));
        Assert.NotNull(ledger.Get(conflictPlan.Game.Fingerprint));
        Assert.True(ueRollback.Rollback(ueResult.ManifestPath).Success);
        AssertSnapshotEqual(conflictDeployed, Take(conflict.InstallRoot));
        Assert.NotEqual(conflictBefore.Files.Count, conflictDeployed.Files.Count);
    }

    [Fact]
    public void Nvidia_profile_and_frame_generation_are_written_through_executor()
    {
        var game = FakeGameFactory.CreateGdkGame(_fs);
        var (plan, executor, _, _) = Arrange(game, GamePlatform.Xbox,
            profile: BuiltinProfiles.NvidiaRtx, frameGen: true);

        var result = executor.Execute(plan, CancellationToken.None);

        Assert.True(result.Success, result.ErrorMessage);
        var ini = _fs.File.ReadAllText($@"{game.DeployDirectory}\OptiScaler.ini");
        Assert.Contains("Dx12Upscaler=dlss", ini);
        Assert.Contains("Enabled=true", ini);
        Assert.Contains("FGInput=upscaler", ini);
        Assert.Contains("FGOutput=fsrfg", ini);
        Assert.Contains("HUDFix=true", ini);
    }

    private static InstallRecord RecordFor(DeploymentPlan plan) => new()
    {
        Fingerprint = plan.Game.Fingerprint,
        GameTitle = plan.Game.Title,
        DeployDirectory = plan.Game.DeployDirectory,
        LatestPlanId = plan.PlanId,
        BackupRoot = BackupRoot,
        State = InstallState.Installed,
        PayloadVersion = plan.Payload.Version,
        IdentityFileName = plan.Identity.FileName,
        InstalledUtc = new DateTime(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc),
    };
}

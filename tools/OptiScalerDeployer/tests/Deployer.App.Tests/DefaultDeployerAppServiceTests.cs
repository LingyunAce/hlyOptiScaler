using System.Security.Cryptography;
using System.Text;
using System.IO.Abstractions;
using Deployer.App.Models;
using Deployer.App.Services;
using Deployer.Core.Domain;
using Deployer.Core.GameDiscovery;
using Deployer.Core.Persistence;
using Deployer.TestData;
using Deployer.Core.Elevation;
using Deployer.Core.HealthCheck;
using Xunit;

namespace Deployer.App.Tests;

public sealed class DefaultDeployerAppServiceTests
{
    [Fact]
    public async Task Elevation_uses_bounded_job_and_updates_ledger_only_after_worker_success()
    {
        using var workspace = new AppWorkspace(); var (game, executable) = workspace.CreateGame(); var jobs = 0;
        var transport = new FakeJobTransport((job, ct) => { jobs++; return ElevationWorker.ExecuteAsync(job, ct); });
        var health = new FakeHealthRunner(HealthCheckStatus.Healthy);
        var service = workspace.CreateService(jobTransport: transport, healthRunner: health, probe: _ => DirectoryWritability.NeedsElevation);
        var profile = Assert.Single((await service.GetProfilesAsync()).Profiles, x => x.Id == "amd-rdna4");
        var plan = (await service.BuildPlanAsync(new PlanBuildOptions(game, executable, profile, workspace.BackupRoot))).Plan
            with { Writability = DirectoryWritability.NeedsElevation, Elevation = ElevationRequirement.Required };
        var result = await service.ApplyAsync(plan, true);
        Assert.True(result.Success, result.ErrorMessage); Assert.True(result.LedgerUpdated); Assert.Equal(1, jobs);
        Assert.True(File.Exists(result.ManifestPath)); Assert.Equal(0, health.Calls);
        Assert.False(Directory.Exists(Path.Combine(workspace.Root, "jobs", plan.PlanId.ToString("D"))));
    }

    [Fact]
    public async Task Declined_elevation_leaves_game_and_ledger_untouched()
    {
        using var workspace = new AppWorkspace(); var (game, executable) = workspace.CreateGame(); var before = Snapshot(workspace.GameRoot);
        var transport = new FakeJobTransport((job, ct) => Task.FromResult(new ElevationResult(job.JobId, false, "Declined", null, [], "UAC declined", false)));
        var service = workspace.CreateService(jobTransport: transport, probe: _ => DirectoryWritability.NeedsElevation);
        var profile = Assert.Single((await service.GetProfilesAsync()).Profiles, x => x.Id == "amd-rdna4");
        var plan = (await service.BuildPlanAsync(new PlanBuildOptions(game, executable, profile, workspace.BackupRoot))).Plan
            with { Writability = DirectoryWritability.NeedsElevation, Elevation = ElevationRequirement.Required };
        var result = await service.ApplyAsync(plan, true);
        Assert.True(result.Cancelled); Assert.False(result.Success); AssertTreeEqual(before, Snapshot(workspace.GameRoot));
        Assert.False(File.Exists(workspace.LedgerPath));
    }

    [Fact]
    public async Task Health_timeout_never_rolls_back_and_wait_does_not_relaunch()
    {
        using var workspace = new AppWorkspace(); var (game, executable) = workspace.CreateGame();
        var health = new FakeHealthRunner(HealthCheckStatus.TimedOut); var service = workspace.CreateService(healthRunner: health);
        var profile = Assert.Single((await service.GetProfilesAsync()).Profiles, x => x.Id == "amd-rdna4");
        var plan = (await service.BuildPlanAsync(new PlanBuildOptions(game, executable, profile, workspace.BackupRoot))).Plan;
        var applied = await service.ApplyAsync(plan, true); Assert.True(applied.Success); var deployed = Snapshot(workspace.GameRoot);
        await service.CheckHealthAsync(plan, "FSR 3.X", true);
        Assert.True(health.Requests[0].LaunchGame);
        await service.CheckHealthAsync(plan, "FSR 3.X", false);
        Assert.False(health.Requests[1].LaunchGame); AssertTreeEqual(deployed, Snapshot(workspace.GameRoot));
        Assert.Equal(InstallState.Installed, new JsonInstallLedger(new FileSystem(), workspace.LedgerPath).Get(plan.Game.Fingerprint)!.State);
    }

    [Fact]
    public async Task Healthy_log_marks_ledger_and_rolled_back_plan_cannot_launch()
    {
        using var workspace = new AppWorkspace(); var (game, executable) = workspace.CreateGame();
        var health = new FakeHealthRunner(HealthCheckStatus.Healthy); var service = workspace.CreateService(healthRunner: health);
        var profile = Assert.Single((await service.GetProfilesAsync()).Profiles, x => x.Id == "amd-rdna4");
        var plan = (await service.BuildPlanAsync(new PlanBuildOptions(game, executable, profile, workspace.BackupRoot))).Plan;
        var result = await service.ApplyAsync(plan, true); Assert.True(result.Success);
        await service.CheckHealthAsync(plan, "FSR 3.X", true);
        Assert.Equal(InstallState.Healthy, new JsonInstallLedger(new FileSystem(), workspace.LedgerPath).Get(plan.Game.Fingerprint)!.State);
        Assert.True((await service.RollbackAsync(result.ManifestPath)).Success);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CheckHealthAsync(plan, "FSR 3.X", true));
        Assert.Equal(1, health.Calls);
    }
    private sealed class FakeHealthRunner(HealthCheckStatus status) : IHealthCheckRunner
    {
        public int Calls => Requests.Count;
        public List<HealthCheckRequest> Requests { get; } = [];
        public Task<HealthCheckResult> RunAsync(HealthCheckRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(new HealthCheckResult(status, "0.9.1", "FSR 3.X", "test log", null, status == HealthCheckStatus.TimedOut) { LogStartOffset = request.LogStartOffset ?? 0 });
        }
    }
    [Fact]
    public async Task Protected_recovery_preserves_manifest_and_backups_without_modifying_deployment()
    {
        using var workspace = new AppWorkspace(); var (game, executable) = workspace.CreateGame();
        var service = workspace.CreateService();
        var profile = Assert.Single((await service.GetProfilesAsync()).Profiles, x => x.Id == "amd-rdna4");
        var plan = (await service.BuildPlanAsync(new PlanBuildOptions(game, executable, profile, workspace.BackupRoot))).Plan;
        var installed = await service.ApplyAsync(plan, true); Assert.True(installed.Success);
        var before = Snapshot(workspace.GameRoot);
        var protectedService = workspace.CreateService(probe: _ => DirectoryWritability.NeedsElevation);
        var recovery = await protectedService.RollbackAsync(installed.ManifestPath);
        Assert.False(recovery.Success); Assert.Contains("管理员权限", recovery.ErrorMessage);
        Assert.Contains("暂不支持安全的管理员恢复", recovery.DiagnosticDetails);
        Assert.Contains("保留", recovery.DiagnosticDetails);
        Assert.Contains(installed.ManifestPath, recovery.DiagnosticDetails); Assert.Contains(workspace.BackupRoot, recovery.DiagnosticDetails);
        AssertTreeEqual(before, Snapshot(workspace.GameRoot));
    }
    [Fact]
    public async Task Plan_apply_duplicate_apply_and_rollback_round_trip_only_inside_test_workspace()
    {
        using var workspace = new AppWorkspace();
        var service = workspace.CreateService();
        var (game, executable) = workspace.CreateGame();
        var before = Snapshot(workspace.GameRoot);
        var profile = Assert.Single((await service.GetProfilesAsync()).Profiles, x => x.Id == "amd-rdna4");

        var built = await service.BuildPlanAsync(new PlanBuildOptions(game, executable, profile, workspace.BackupRoot));
        Assert.Equal("Game.exe", built.Target.ExecutableName);
        AssertTreeEqual(before, Snapshot(workspace.GameRoot));

        var applied = await service.ApplyAsync(built.Plan, reviewAcknowledged: true);
        Assert.True(applied.Success, applied.ErrorMessage);
        Assert.True(applied.LedgerUpdated);
        AssertTreeNotEqual(before, Snapshot(workspace.GameRoot));
        var deployedTree = Snapshot(workspace.GameRoot);

        var duplicate = await service.ApplyAsync(built.Plan, reviewAcknowledged: true);
        Assert.False(duplicate.Success);
        AssertTreeEqual(deployedTree, Snapshot(workspace.GameRoot));

        var rollback = await service.RollbackAsync(applied.ManifestPath);
        Assert.True(rollback.Success, rollback.ErrorMessage);
        Assert.True(rollback.LedgerUpdated);
        AssertTreeEqual(before, Snapshot(workspace.GameRoot));
    }

    [Fact]
    public async Task Apply_requires_review_and_rejects_nonwritable_or_unresolved_plans_without_touching_game()
    {
        using var workspace = new AppWorkspace();
        var service = workspace.CreateService();
        var (game, executable) = workspace.CreateGame();
        var profile = Assert.Single((await service.GetProfilesAsync()).Profiles, x => x.Id == "amd-rdna4");
        var plan = (await service.BuildPlanAsync(new PlanBuildOptions(game, executable, profile, workspace.BackupRoot))).Plan;
        var before = Snapshot(workspace.GameRoot);

        var unreviewed = await service.ApplyAsync(plan, reviewAcknowledged: false);
        Assert.False(unreviewed.Success);
        Assert.Contains("确认", unreviewed.ErrorMessage);
        AssertTreeEqual(before, Snapshot(workspace.GameRoot));

        foreach (var blocked in new[]
        {
            plan with { Writability = DirectoryWritability.Unknown },
            plan with { Writability = DirectoryWritability.Blocked, Elevation = ElevationRequirement.Blocked },
            plan with { Identity = plan.Identity with { Status = IdentityStatus.RequiresUserDecision } },
        })
        {
            var result = await service.ApplyAsync(blocked, reviewAcknowledged: true);
            Assert.False(result.Success);
            AssertTreeEqual(before, Snapshot(workspace.GameRoot));
        }
    }

    [Fact]
    public async Task Corrupt_ledger_blocks_apply_and_preserves_original_bytes()
    {
        using var workspace = new AppWorkspace();
        const string corrupt = "{ not a ledger; preserve this text exactly }";
        File.WriteAllText(workspace.LedgerPath, corrupt, new UTF8Encoding(false));
        var service = workspace.CreateService();
        var (game, executable) = workspace.CreateGame();
        var profile = Assert.Single((await service.GetProfilesAsync()).Profiles, x => x.Id == "amd-rdna4");
        var plan = (await service.BuildPlanAsync(new PlanBuildOptions(game, executable, profile, workspace.BackupRoot))).Plan;
        var before = Snapshot(workspace.GameRoot);

        var result = await service.ApplyAsync(plan, reviewAcknowledged: true);

        Assert.False(result.Success);
        Assert.Equal(corrupt, File.ReadAllText(workspace.LedgerPath));
        AssertTreeEqual(before, Snapshot(workspace.GameRoot));
    }

    [Fact]
    public async Task Rollback_restores_game_when_ledger_is_corrupt_and_keeps_bad_ledger()
    {
        using var workspace = new AppWorkspace();
        var service = workspace.CreateService();
        var (game, executable) = workspace.CreateGame();
        var before = Snapshot(workspace.GameRoot);
        var profile = Assert.Single((await service.GetProfilesAsync()).Profiles, x => x.Id == "amd-rdna4");
        var plan = (await service.BuildPlanAsync(new PlanBuildOptions(game, executable, profile, workspace.BackupRoot))).Plan;
        var applied = await service.ApplyAsync(plan, reviewAcknowledged: true);
        Assert.True(applied.Success, applied.ErrorMessage);

        const string corrupt = "damaged ledger must remain recoverable";
        File.WriteAllText(workspace.LedgerPath, corrupt, new UTF8Encoding(false));
        var rollback = await service.RollbackAsync(applied.ManifestPath);

        Assert.True(rollback.Success, rollback.ErrorMessage);
        Assert.False(rollback.LedgerUpdated);
        Assert.Equal(corrupt, File.ReadAllText(workspace.LedgerPath));
        AssertTreeEqual(before, Snapshot(workspace.GameRoot));
    }

    [Fact]
    public async Task Builtin_profiles_cannot_be_overwritten_or_deleted_but_a_clone_can_be_saved()
    {
        using var workspace = new AppWorkspace();
        var service = workspace.CreateService();
        var builtin = Assert.Single((await service.GetProfilesAsync()).Profiles, x => x.Id == "amd-rdna4");

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveProfileAsync(builtin));
        Assert.False(await service.DeleteProfileAsync(builtin.Id));

        var clone = builtin with { Id = "my-amd-rdna4", DisplayName = "My AMD profile", IsBuiltin = false };
        await service.SaveProfileAsync(clone);
        Assert.Contains((await service.GetProfilesAsync()).Profiles, x => x.Id == clone.Id && !x.IsBuiltin);
    }

    [Fact]
    public async Task Corrupt_profile_file_is_preserved_and_blocks_profile_mutations()
    {
        using var workspace = new AppWorkspace();
        const string corrupt = "invalid profile data";
        File.WriteAllText(workspace.ProfilesPath, corrupt, new UTF8Encoding(false));
        var service = workspace.CreateService();
        var catalog = await service.GetProfilesAsync();
        var builtin = Assert.Single(catalog.Profiles, x => x.Id == "amd-rdna4");

        Assert.False(string.IsNullOrWhiteSpace(catalog.LoadError));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.SaveProfileAsync(
            builtin with { Id = "custom", IsBuiltin = false }));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.DeleteProfileAsync("custom"));
        Assert.Equal(corrupt, File.ReadAllText(workspace.ProfilesPath));
    }

    [Fact]
    public async Task Profile_id_cannot_impersonate_builtin_even_when_flag_is_changed()
    {
        using var workspace = new AppWorkspace();
        var service = workspace.CreateService();
        var builtin = Assert.Single((await service.GetProfilesAsync()).Profiles, x => x.Id == "amd-rdna4");

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveProfileAsync(builtin with
        {
            IsBuiltin = false,
            DisplayName = "Spoofed built-in override",
        }));

        Assert.DoesNotContain((await service.GetProfilesAsync()).Profiles,
            profile => profile.Id == builtin.Id && profile.DisplayName == "Spoofed built-in override");
    }

    [Fact]
    public async Task Apply_ledger_write_failure_keeps_deployed_game_and_recovery_manifest()
    {
        using var workspace = new AppWorkspace();
        var service = workspace.CreateService();
        var (game, executable) = workspace.CreateGame();
        var before = Snapshot(workspace.GameRoot);
        var profile = Assert.Single((await service.GetProfilesAsync()).Profiles, x => x.Id == "amd-rdna4");
        var plan = (await service.BuildPlanAsync(new PlanBuildOptions(game, executable, profile, workspace.BackupRoot))).Plan;
        Directory.CreateDirectory(workspace.LedgerPath); // Directory at the file path makes the post-deployment atomic save fail.

        var result = await service.ApplyAsync(plan, reviewAcknowledged: true);

        Assert.True(result.Success);
        Assert.False(result.LedgerUpdated);
        Assert.True(File.Exists(result.ManifestPath));
        AssertTreeNotEqual(before, Snapshot(workspace.GameRoot));
        Assert.Contains("台账", result.ErrorMessage ?? string.Empty);

        var rollback = await service.RollbackAsync(result.ManifestPath);
        Assert.True(rollback.Success, rollback.ErrorMessage);
        AssertTreeEqual(before, Snapshot(workspace.GameRoot));
    }

    [Fact]
    public async Task Rolling_back_older_plan_does_not_relabel_the_newer_ledger_plan()
    {
        using var workspace = new AppWorkspace();
        var service = workspace.CreateService();
        var (game, executable) = workspace.CreateGame();
        var profile = Assert.Single((await service.GetProfilesAsync()).Profiles, x => x.Id == "amd-rdna4");

        var firstPlan = (await service.BuildPlanAsync(new PlanBuildOptions(game, executable, profile, workspace.BackupRoot))).Plan;
        var first = await service.ApplyAsync(firstPlan, reviewAcknowledged: true);
        Assert.True(first.Success, first.ErrorMessage);
        var secondPlan = (await service.BuildPlanAsync(new PlanBuildOptions(game, executable, profile, workspace.BackupRoot))).Plan;
        Assert.NotEqual(firstPlan.PlanId, secondPlan.PlanId);
        var second = await service.ApplyAsync(secondPlan, reviewAcknowledged: true);
        Assert.True(second.Success, second.ErrorMessage);

        var olderRollback = await service.RollbackAsync(first.ManifestPath);

        Assert.True(olderRollback.Success, olderRollback.ErrorMessage);
        Assert.False(olderRollback.LedgerUpdated);
        var history = await service.GetHistoryAsync();
        var newest = Assert.Single(history, row => row.Manifest.PlanId == secondPlan.PlanId);
        Assert.True(newest.IsLatest);
        Assert.Equal(InstallState.Installed, newest.State);
    }

    [Fact]
    public async Task Selected_manual_payload_directory_is_the_source_for_the_plan()
    {
        using var workspace = new AppWorkspace();
        var (game, executable) = workspace.CreateGame();
        var selectedPayload = Path.Combine(workspace.Root, "selected-payload");
        workspace.CopyPayload(selectedPayload);
        var service = workspace.CreateService(payloadDirectory: null);
        var profile = Assert.Single((await service.GetProfilesAsync()).Profiles, x => x.Id == "amd-rdna4");

        var result = await service.BuildPlanAsync(new PlanBuildOptions(game, executable, profile, workspace.BackupRoot,
            PayloadDirectory: selectedPayload));

        Assert.Equal(Path.GetFullPath(selectedPayload), Path.GetFullPath(result.Plan.Payload.RootDirectory));
    }

    [Fact]
    public async Task Scan_warning_is_visible_and_prevents_false_needs_reinstall_marking()
    {
        using var workspace = new AppWorkspace();
        var (game, executable) = workspace.CreateGame();
        var ledger = new JsonInstallLedger(new FileSystem(), workspace.LedgerPath);
        ledger.Save(new InstallRecord
        {
            Fingerprint = "0123abcd", GameTitle = game.Title,
            DeployDirectory = Path.GetDirectoryName(executable)!, LatestPlanId = Guid.NewGuid(),
            BackupRoot = workspace.BackupRoot, State = InstallState.Installed,
            PayloadVersion = "test", IdentityFileName = "dxgi.dll", InstalledUtc = DateTime.UtcNow,
        });
        var scanner = new WarningGameScanner(game with { ExecutableCandidates = [executable] }, "injected scanner unavailable");
        var service = workspace.CreateService(scanners: [scanner]);

        var found = await service.GetGamesAsync(refresh: true);

        Assert.Single(found);
        Assert.Contains(service.Warnings, warning => warning.Contains("injected scanner unavailable", StringComparison.Ordinal));
        Assert.Contains(service.Warnings, warning => warning.Contains("未根据不完整结果", StringComparison.Ordinal));
        Assert.Equal(InstallState.Installed, new JsonInstallLedger(new FileSystem(), workspace.LedgerPath)
            .Get("0123abcd")!.State);
    }

    [Fact]
    public async Task History_reports_corrupt_ledger_and_manifest_instead_of_silently_hiding_them()
    {
        using var workspace = new AppWorkspace();
        Directory.CreateDirectory(workspace.BackupRoot);
        const string fingerprint = "0123abcd";
        var invalidManifest = Path.Combine(workspace.BackupRoot, fingerprint, Guid.NewGuid().ToString("D"), "manifest.json");
        Directory.CreateDirectory(Path.GetDirectoryName(invalidManifest)!);
        File.WriteAllText(invalidManifest, "{ broken", new UTF8Encoding(false));
        File.WriteAllText(workspace.LedgerPath, "invalid ledger", new UTF8Encoding(false));
        var service = workspace.CreateService();

        var history = await service.GetHistoryAsync();

        Assert.Empty(history);
        Assert.Contains(service.Warnings, warning => warning.Contains("台账不可读", StringComparison.Ordinal));
        Assert.Contains(service.Warnings, warning => warning.Contains("无法读取历史清单", StringComparison.Ordinal));
        Assert.Equal("invalid ledger", File.ReadAllText(workspace.LedgerPath));
    }

    private sealed class WarningGameScanner(DiscoveredGame game, string warning) : IGameScanner
    {
        public string PlatformName => "TestScanner";
        public IReadOnlyList<string> Warnings => [warning];
        public Task<IReadOnlyList<DiscoveredGame>> ScanAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<DiscoveredGame>>([game]);
    }

    private static TreeSnapshot Snapshot(string root)
    {
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(path => Path.GetRelativePath(root, path), path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), StringComparer.OrdinalIgnoreCase);
        var directories = Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new TreeSnapshot(files, directories);
    }

    private static void AssertTreeEqual(TreeSnapshot expected, TreeSnapshot actual)
    {
        Assert.Equal(expected.Files.Keys.Order(StringComparer.OrdinalIgnoreCase), actual.Files.Keys.Order(StringComparer.OrdinalIgnoreCase));
        foreach (var (path, hash) in expected.Files) Assert.Equal(hash, actual.Files[path]);
        Assert.True(expected.Directories.SetEquals(actual.Directories), "Directory set changed during the round trip.");
    }

    private static void AssertTreeNotEqual(TreeSnapshot expected, TreeSnapshot actual)
    {
        var filesEqual = expected.Files.Count == actual.Files.Count
            && expected.Files.All(entry => actual.Files.TryGetValue(entry.Key, out var hash) && hash == entry.Value);
        Assert.False(filesEqual && expected.Directories.SetEquals(actual.Directories), "Expected deployment to change the game tree.");
    }

    private sealed record TreeSnapshot(IReadOnlyDictionary<string, string> Files, IReadOnlySet<string> Directories);

    private sealed class AppWorkspace : IDisposable
    {
        private static readonly string TempParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "OptiScalerDeployer.AppTests"));
        private readonly string _id = Guid.NewGuid().ToString("N");

        public AppWorkspace()
        {
            Root = Path.GetFullPath(Path.Combine(TempParent, _id));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }
        public string GameRoot => Path.Combine(Root, "game");
        public string PayloadRoot => Path.Combine(Root, "payload");
        public string BackupRoot => Path.Combine(Root, "backups");
        public string LedgerPath => Path.Combine(Root, "ledger.json");
        public string ProfilesPath => Path.Combine(Root, "profiles.json");

        public DefaultDeployerAppService CreateService(string? payloadDirectory = null, IEnumerable<IGameScanner>? scanners = null,
            IJobTransport? jobTransport = null, IHealthCheckRunner? healthRunner = null, Func<string, DirectoryWritability>? probe = null) => new(new DeployerAppOptions
        {
            RepositoryRoot = Path.Combine(Root, "repo"),
            BackupRoot = BackupRoot,
            LedgerPath = LedgerPath,
            ProfilesPath = ProfilesPath,
            ManualFoldersPath = Path.Combine(Root, "manual-folders.json"),
            PayloadDirectory = payloadDirectory ?? PayloadRoot,
            JobsRoot = Path.Combine(Root, "jobs"), PayloadCacheRoot = Path.Combine(Root, "payload-cache"),
        }, new FileSystem(), scanners ?? [], jobTransport, healthRunner, probe);

        public (DiscoveredGame Game, string Executable) CreateGame()
        {
            var deploy = Path.Combine(GameRoot, "Binaries", "Win64");
            Directory.CreateDirectory(deploy);
            Directory.CreateDirectory(Path.Combine(GameRoot, "Mods"));
            Directory.CreateDirectory(PayloadRoot);
            var executable = Path.Combine(deploy, "Game.exe");
            File.WriteAllBytes(executable, PeFixtureBuilder.BuildAmd64(["d3d12.dll", "nvngx_dlss.dll"]));
            File.WriteAllText(Path.Combine(deploy, "OptiScaler.ini"), "original ini\r\n", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(GameRoot, "Mods", "unrelated.bin"), "preserve this mod", new UTF8Encoding(false));
            CopyPayload(PayloadRoot);

            return (new DiscoveredGame
            {
                Title = "Isolated Test Game",
                Platform = GamePlatform.Manual,
                InstallRoot = GameRoot,
                ExecutableCandidates = [executable],
                Writability = DirectoryWritability.Writable,
                ScannerSource = "App test fixture",
            }, executable);
        }

        public void CopyPayload(string destination)
        {
            foreach (var relative in SyntheticPayloadFactory.RelativeFiles)
            {
                var target = Path.Combine(destination, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var content = relative == SyntheticPayloadFactory.IniFileName
                    ? SyntheticPayloadFactory.SampleIniContent
                    : $"TEST_PAYLOAD::{relative}::{SyntheticPayloadFactory.DefaultVersion}";
                File.WriteAllText(target, content, new UTF8Encoding(false));
            }
        }

        public void Dispose()
        {
            var fullRoot = Path.GetFullPath(Root);
            if (!string.Equals(Path.GetDirectoryName(fullRoot), TempParent, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Path.GetFileName(fullRoot), _id, StringComparison.Ordinal)
                || !Guid.TryParseExact(_id, "N", out _))
                return;

            if (Directory.Exists(fullRoot)) Directory.Delete(fullRoot, recursive: true);
        }
    }
}

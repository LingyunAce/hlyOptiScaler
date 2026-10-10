using Deployer.App.Models;
using Deployer.App.Services;
using Deployer.App.ViewModels;
using Deployer.Core.Domain;
using Xunit;

namespace Deployer.App.Tests;

public sealed class MainViewModelTests
{
    [Fact]
    public async Task Initialize_does_not_scan_games_until_an_explicit_refresh()
    {
        var service = new FakeAppService();
        var vm = new MainViewModel(service, new DeployerAppOptions());
        await vm.InitializeAsync();

        Assert.Equal(0, service.GameScanCalls);
        vm.RefreshCommand.Execute(null);
        await WaitForIdle(vm);
        Assert.Equal([true], service.RefreshArguments);
    }

    [Fact]
    public async Task Initialize_keeps_profile_load_error_visible_after_history_load()
    {
        var service = new FakeAppService { ProfileLoadError = "损坏的 Profile 文件" };
        var vm = new MainViewModel(service, new DeployerAppOptions());

        await vm.InitializeAsync();

        Assert.Equal("损坏的 Profile 文件", vm.ProfileLoadError);
        Assert.Contains("损坏的 Profile 文件", vm.ErrorMessage);
        Assert.NotNull(vm.SuggestedAction);
        Assert.NotNull(vm.DiagnosticDetails);
    }

    [Fact]
    public async Task Scanner_warnings_are_shown_to_the_user_after_refresh()
    {
        var service = new FakeAppService { WarningMessages = ["Steam scan failed: access denied"] };
        var vm = new MainViewModel(service, new DeployerAppOptions());
        await vm.InitializeAsync();
        vm.RefreshCommand.Execute(null);
        await WaitForIdle(vm);

        Assert.Contains("Steam scan failed: access denied", vm.ServiceWarnings);
    }

    [Fact]
    public async Task Review_acknowledgement_is_required_and_settings_changes_invalidate_plan_and_ack()
    {
        var service = new FakeAppService();
        var vm = await CreateInitialized(service);
        vm.SelectedGame = vm.Games[0];

        vm.GeneratePlanCommand.Execute(null);
        await WaitForIdle(vm);

        Assert.NotNull(vm.CurrentPlan);
        Assert.False(vm.PlanReviewAcknowledged);
        Assert.False(vm.CanExecutePlan);
        vm.PlanReviewAcknowledged = true;
        Assert.True(vm.CanExecutePlan);

        vm.EnableFrameGen = !vm.EnableFrameGen;
        Assert.Null(vm.CurrentPlan);
        Assert.False(vm.PlanReviewAcknowledged);
        Assert.False(vm.CanExecutePlan);
        Assert.Equal(1, service.BuildPlanCalls);
    }

    [Fact]
    public async Task Blocking_unknown_and_unresolved_identity_plans_cannot_be_executed()
    {
        foreach (var transform in new Func<DeploymentPlan, DeploymentPlan>[]
        {
            plan => plan with { Warnings = [new PlanWarning(WarningSeverity.Blocking, "blocked")] },
            plan => plan with { Writability = DirectoryWritability.Unknown },
            plan => plan with { Writability = DirectoryWritability.Blocked, Elevation = ElevationRequirement.Blocked },
            plan => plan with { Identity = plan.Identity with { Status = IdentityStatus.RequiresUserDecision } },
        })
        {
            var service = new FakeAppService { PlanTransform = transform };
            var vm = await CreateInitialized(service);
            vm.SelectedGame = vm.Games[0];
            vm.GeneratePlanCommand.Execute(null);
            await WaitForIdle(vm);
            vm.PlanReviewAcknowledged = true;

            Assert.False(vm.CanExecutePlan);
            Assert.False(vm.ExecuteCommand.CanExecute(null));
            Assert.Equal(0, service.ApplyCalls);
        }
    }

    [Fact]
    public async Task Execute_block_reason_explains_why_a_reviewed_blocked_plan_cannot_run()
    {
        var service = new FakeAppService
        {
            PlanTransform = plan => plan with { Warnings = [new PlanWarning(WarningSeverity.Blocking, "payload 不兼容")] },
        };
        var vm = await CreateInitialized(service);
        vm.SelectedGame = vm.Games[0];
        vm.GeneratePlanCommand.Execute(null);
        await WaitForIdle(vm);
        vm.PlanReviewAcknowledged = true;

        Assert.False(vm.CanExecutePlan);
        Assert.Contains("阻塞", vm.ExecuteBlockReason);
        Assert.Contains("payload 不兼容", vm.ExecuteBlockReason);
    }

    [Fact]
    public async Task Reviewed_elevation_plan_can_be_executed_and_does_not_launch_game_automatically()
    {
        var service = new FakeAppService { PlanTransform = p => p with { Writability = DirectoryWritability.NeedsElevation, Elevation = ElevationRequirement.Required } };
        var vm = await CreateInitialized(service); vm.SelectedGame = vm.Games[0];
        vm.GeneratePlanCommand.Execute(null); await WaitForIdle(vm); vm.PlanReviewAcknowledged = true;
        Assert.True(vm.CanExecutePlan);
        vm.ExecuteCommand.Execute(null); await WaitForIdle(vm);
        Assert.Equal(1, service.ApplyCalls); Assert.Equal(0, service.HealthCalls);
        Assert.True(vm.CanCheckHealth);
        vm.HealthCheckCommand.Execute(null); await WaitForIdle(vm);
        Assert.Equal(1, service.HealthCalls);
        Assert.True(service.LastHealthLaunch);
        vm.WaitHealthCommand.Execute(null); await WaitForIdle(vm);
        Assert.Equal(2, service.HealthCalls); Assert.False(service.LastHealthLaunch);
    }

    [Fact]
    public async Task Disable_logging_replans_original_deployment_without_applying_or_using_changed_selection()
    {
        var edits = new[] { new IniEdit("FrameGen", "Enabled", null, "true"), new IniEdit("Inputs", "EnableDlss", null, "true"), new IniEdit("Log", "LogToFile", null, "true") };
        var service = new FakeAppService { HealthStatus = Deployer.Core.HealthCheck.HealthCheckStatus.Healthy, PlanTransform = p => p with { IniEdits = edits } };
        var vm = await CreateInitialized(service);
        vm.PayloadDirectory = "original-payload"; vm.EnableFrameGen = true;
        vm.GeneratePlanCommand.Execute(null); await WaitForIdle(vm);
        var original = service.LastPlanOptions!;
        vm.PlanReviewAcknowledged = true; vm.ExecuteCommand.Execute(null); await WaitForIdle(vm);
        vm.HealthCheckCommand.Execute(null); await WaitForIdle(vm);
        vm.SelectedGame = vm.Games[1]; vm.SelectedProfile = FakeAppService.Profile with { Id = "different" };
        vm.PayloadDirectory = "other-payload"; vm.EnableFrameGen = false; vm.BackupRoot = "other-backups";
        Assert.True(vm.CanDisableLogging);
        await vm.DisableLoggingAsync();
        var next = service.LastPlanOptions!;
        Assert.Equal(original.Game, next.Game); Assert.Equal(original.ExecutablePath, next.ExecutablePath);
        Assert.Equal(original.BackupRoot, next.BackupRoot); Assert.Equal("original-payload", next.PayloadDirectory);
        Assert.False(next.EnableLogging); Assert.False(next.EnableFrameGen);
        Assert.Contains(next.Profile.Edits, e => e.Section == "FrameGen" && e.NewValue == "true");
        Assert.Contains(next.Profile.Edits, e => e.Section == "Inputs" && e.NewValue == "true");
        Assert.DoesNotContain(next.Profile.Edits, e => e.Section == "Log");
        Assert.False(vm.PlanReviewAcknowledged); Assert.False(vm.CanExecutePlan); Assert.Equal(1, service.ApplyCalls);
    }

    [Fact]
    public async Task Failed_deployment_exposes_manifest_recovery_without_enabling_game_launch()
    {
        var service = new FakeAppService { NextApply = Task.FromResult(new ApplyWorkflowResult(false, false, false, "recovery-manifest.json", [], "Worker crashed", null)) };
        var vm = await CreateInitialized(service);
        vm.GeneratePlanCommand.Execute(null); await WaitForIdle(vm); vm.PlanReviewAcknowledged = true;
        vm.ExecuteCommand.Execute(null); await WaitForIdle(vm);
        Assert.True(vm.HealthRollbackCommand.CanExecute(null)); Assert.False(vm.CanCheckHealth);
    }

    [Fact]
    public async Task Changing_game_during_plan_generation_discards_stale_completion()
    {
        var gate = new TaskCompletionSource<PlanBuildResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FakeAppService { NextBuild = gate.Task };
        var vm = await CreateInitialized(service);
        vm.SelectedGame = vm.Games[0];
        vm.GeneratePlanCommand.Execute(null);
        await WaitUntil(() => vm.IsBusy);
        var otherGame = vm.Games[1];
        vm.SelectedGame = otherGame;

        gate.SetResult(FakeAppService.BuildResult(vm.Games[0].Game, "first-game.exe"));
        await WaitForIdle(vm);

        Assert.Same(otherGame, vm.SelectedGame);
        Assert.Null(vm.CurrentPlan);
        Assert.False(vm.PlanReviewAcknowledged);
    }

    [Fact]
    public async Task Changing_selected_executable_invalidates_plan_and_review()
    {
        var service = new FakeAppService();
        var vm = await CreateInitialized(service);
        vm.SelectedGame = vm.Games[0];
        vm.GeneratePlanCommand.Execute(null);
        await WaitForIdle(vm);
        vm.PlanReviewAcknowledged = true;

        vm.SelectedGame.SelectedExecutable = "D:\\Games\\One\\alternate.exe";

        Assert.Null(vm.CurrentPlan);
        Assert.False(vm.PlanReviewAcknowledged);
        Assert.False(vm.ExecuteCommand.CanExecute(null));
    }

    [Fact]
    public async Task Closing_cancels_work_and_is_deferred_until_operation_finishes()
    {
        var service = new FakeAppService { WaitForBuildCancellation = true };
        var vm = await CreateInitialized(service);
        vm.SelectedGame = vm.Games[0];
        var closeCount = 0;
        vm.CloseRequested += (_, _) => closeCount++;
        vm.GeneratePlanCommand.Execute(null);
        await WaitUntil(() => vm.IsBusy);

        vm.RequestClose();

        Assert.False(vm.CanClose);
        Assert.Equal(0, closeCount);
        await WaitUntil(() => !vm.IsBusy);
        Assert.Equal(1, closeCount);
        Assert.Null(vm.CurrentPlan);
    }

    [Fact]
    public async Task Cancel_command_notifies_enabled_while_busy_and_cancels_pending_plan()
    {
        var service = new FakeAppService { WaitForBuildCancellation = true };
        var vm = await CreateInitialized(service);
        var busyCancelStates = new List<bool>();
        vm.CancelCommand.CanExecuteChanged += (_, _) =>
        {
            if (vm.IsBusy) busyCancelStates.Add(vm.CancelCommand.CanExecute(null));
        };

        vm.GeneratePlanCommand.Execute(null);
        await WaitUntil(() => vm.IsBusy && service.BuildPlanCalls > 0);

        Assert.Contains(true, busyCancelStates);
        Assert.True(busyCancelStates[^1]);
        Assert.True(vm.CancelCommand.CanExecute(null));

        vm.CancelCommand.Execute(null);
        await WaitForIdle(vm);

        Assert.True(service.BuildCancellationObserved);
        Assert.False(vm.CancelCommand.CanExecute(null));
        Assert.Null(vm.CurrentPlan);
    }

    [Fact]
    public async Task Apply_command_runs_once_only_after_review_and_reports_three_part_error()
    {
        var applyGate = new TaskCompletionSource<ApplyWorkflowResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FakeAppService { NextApply = applyGate.Task };
        var vm = await CreateInitialized(service);
        vm.SelectedGame = vm.Games[0];
        vm.GeneratePlanCommand.Execute(null);
        await WaitForIdle(vm);

        Assert.False(vm.ExecuteCommand.CanExecute(null));
        vm.PlanReviewAcknowledged = true;
        Assert.True(vm.ExecuteCommand.CanExecute(null));
        vm.ExecuteCommand.Execute(null);
        await WaitUntil(() => vm.IsBusy);
        vm.ExecuteCommand.Execute(null);
        Assert.Equal(1, service.ApplyCalls);
        Assert.False(vm.CanClose);

        applyGate.SetResult(new ApplyWorkflowResult(false, false, false, "manifest.json", [], "user failure", "diagnostic trace"));
        await WaitForIdle(vm);
        Assert.Equal("user failure", vm.ErrorMessage);
        Assert.False(string.IsNullOrWhiteSpace(vm.SuggestedAction));
        Assert.Equal("diagnostic trace", vm.DiagnosticDetails);
        Assert.Null(vm.CurrentPlan);
        Assert.False(vm.PlanReviewAcknowledged);
        Assert.False(vm.ExecuteCommand.CanExecute(null));
    }

    [Fact]
    public async Task Cancelled_apply_reports_manifest_and_does_not_claim_ledger_was_updated()
    {
        var service = new FakeAppService
        {
            NextApply = Task.FromResult(new ApplyWorkflowResult(false, true, false,
                "test-backups\\0123abcd\\00000000-0000-0000-0000-000000000001\\manifest.json",
                ["runtime"], "部署已取消并尝试回滚，清单：test-backups\\0123abcd\\00000000-0000-0000-0000-000000000001\\manifest.json", "rollback attempted")),
        };
        var vm = await CreateInitialized(service);
        var historyCalls = service.HistoryCallsAfterInitialize;
        vm.GeneratePlanCommand.Execute(null);
        await WaitForIdle(vm);
        vm.PlanReviewAcknowledged = true;
        vm.ExecuteCommand.Execute(null);
        await WaitForIdle(vm);

        Assert.Contains("已取消", vm.ErrorMessage);
        Assert.Contains("manifest.json", vm.ErrorMessage);
        Assert.Equal("rollback attempted", vm.DiagnosticDetails);
        Assert.Null(vm.CurrentPlan);
        Assert.False(vm.PlanReviewAcknowledged);
        Assert.Equal(historyCalls, service.HistoryCallsAfterInitialize);
    }

    [Fact]
    public async Task Profile_clone_can_be_saved_and_storage_load_error_is_explained()
    {
        var service = new FakeAppService { ProfileLoadError = "配置 JSON 已损坏" };
        var vm = new MainViewModel(service, new DeployerAppOptions());
        await vm.InitializeAsync();
        var builtin = Assert.Single(vm.Profiles);
        vm.SelectedProfile = builtin;
        vm.CloneProfileCommand.Execute(null);

        Assert.NotSame(builtin, vm.SelectedProfile);
        Assert.False(vm.SelectedProfile!.IsBuiltin);
        Assert.False(vm.SaveProfileCommand.CanExecute(null));
        Assert.Equal("配置 JSON 已损坏", vm.ProfileLoadError);

        Assert.Contains("配置 JSON 已损坏", vm.ErrorMessage);
        Assert.NotNull(vm.SuggestedAction);
        Assert.NotNull(vm.DiagnosticDetails);
        Assert.Equal(0, service.SaveProfileCalls);
    }

    [Fact]
    public async Task Generate_plan_uses_the_executable_selected_for_the_game()
    {
        var service = new FakeAppService();
        var vm = await CreateInitialized(service);
        vm.SelectedGame = vm.Games[0];
        vm.SelectedGame.SelectedExecutable = "D:\\Games\\One\\alternate.exe";
        vm.EnableFrameGen = true;
        vm.GeneratePlanCommand.Execute(null);
        await WaitForIdle(vm);

        Assert.Equal("D:\\Games\\One\\alternate.exe", service.LastPlanOptions!.ExecutablePath);
        Assert.True(service.LastPlanOptions.EnableFrameGen);
        Assert.Equal(FakeAppService.Profile.Id, service.LastPlanOptions.Profile.Id);
    }

    [Fact]
    public async Task Save_plan_rejects_game_payload_and_persistent_state_paths_without_changing_files()
    {
        using var workspace = new ViewModelWorkspace();
        var gameRoot = Path.Combine(workspace.Root, "game");
        var payloadRoot = Path.Combine(workspace.Root, "payload");
        var deploy = Path.Combine(gameRoot, "Binaries", "Win64");
        var executable = Path.Combine(deploy, "Game.exe");
        Directory.CreateDirectory(deploy);
        Directory.CreateDirectory(payloadRoot);
        Directory.CreateDirectory(workspace.BackupRoot);
        File.WriteAllText(executable, "test executable");
        File.WriteAllText(Path.Combine(deploy, "unrelated.bin"), "keep game bytes");
        File.WriteAllText(Path.Combine(payloadRoot, "OptiScaler.ini"), "keep payload bytes");
        File.WriteAllText(workspace.LedgerPath, "keep ledger bytes");
        File.WriteAllText(workspace.ProfilesPath, "keep profile bytes");
        var service = new FakeAppService
        {
            DiscoveredGames = [new DiscoveredGame
            {
                Title = "Temp Save Target", Platform = GamePlatform.Manual, InstallRoot = gameRoot,
                ExecutableCandidates = [executable], Writability = DirectoryWritability.Writable, ScannerSource = "temp test",
            }],
        };
        var vm = new MainViewModel(service, new DeployerAppOptions
        {
            RepositoryRoot = workspace.Root,
            BackupRoot = workspace.BackupRoot,
            LedgerPath = workspace.LedgerPath,
            ProfilesPath = workspace.ProfilesPath,
            PayloadDirectory = payloadRoot,
        });
        await vm.InitializeAsync();
        vm.RefreshCommand.Execute(null);
        await WaitForIdle(vm);
        vm.SelectedGame = vm.Games[0];
        vm.GeneratePlanCommand.Execute(null);
        await WaitForIdle(vm);
        Assert.Equal(Path.GetFullPath(payloadRoot), Path.GetFullPath(vm.CurrentPlan!.Payload.RootDirectory));

        var forbiddenOutputs = new[]
        {
            Path.Combine(gameRoot, "deployment-plan.json"),
            Path.Combine(payloadRoot, "deployment-plan.json"),
            workspace.LedgerPath,
            workspace.ProfilesPath,
        };
        foreach (var output in forbiddenOutputs)
        {
            vm.SavePlanCommand.Execute(output);
            await WaitForIdle(vm);
            Assert.False(string.IsNullOrWhiteSpace(vm.ErrorMessage), $"Unsafe plan output was accepted: {output}");
        }

        Assert.False(File.Exists(forbiddenOutputs[0]));
        Assert.False(File.Exists(forbiddenOutputs[1]));
        Assert.Equal("keep ledger bytes", File.ReadAllText(workspace.LedgerPath));
        Assert.Equal("keep profile bytes", File.ReadAllText(workspace.ProfilesPath));
        Assert.Equal("keep game bytes", File.ReadAllText(Path.Combine(deploy, "unrelated.bin")));
        Assert.Equal("keep payload bytes", File.ReadAllText(Path.Combine(payloadRoot, "OptiScaler.ini")));
    }

    [Fact]
    public async Task Changing_storage_paths_while_busy_does_not_reconfigure_active_service()
    {
        var gate = new TaskCompletionSource<PlanBuildResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FakeAppService { NextBuild = gate.Task };
        var vm = await CreateInitialized(service);
        vm.SelectedGame = vm.Games[0];
        vm.GeneratePlanCommand.Execute(null);
        await WaitUntil(() => vm.IsBusy);
        var configureCalls = service.ConfigureStorageCalls;
        var currentBackupRoot = vm.BackupRoot;

        vm.BackupRoot = "changed-while-busy";
        await vm.UpdateStoragePathsAsync("batch-backup", "batch-ledger.json", "batch-profiles.json");

        Assert.Equal(configureCalls, service.ConfigureStorageCalls);
        Assert.Equal(currentBackupRoot, vm.BackupRoot);
        Assert.NotEqual("batch-ledger.json", vm.LedgerPath);
        gate.SetResult(FakeAppService.BuildResult(vm.Games[0].Game, "first-game.exe"));
        await WaitForIdle(vm);
    }

    [Fact]
    public async Task Storage_path_batch_reconfigures_all_paths_together_and_reloads_state()
    {
        var service = new FakeAppService();
        var vm = await CreateInitialized(service);
        var beforeProfileLoads = service.ProfileLoadCalls;
        var beforeHistoryLoads = service.HistoryCallsAfterInitialize;

        await vm.UpdateStoragePathsAsync("backup-next", "ledger-next.json", "profiles-next.json");

        Assert.Equal(2, service.ConfigureStorageCalls);
        Assert.Equal(("backup-next", "ledger-next.json", "profiles-next.json"), service.ConfiguredPaths[^1]);
        Assert.Equal("backup-next", vm.BackupRoot);
        Assert.Equal("ledger-next.json", vm.LedgerPath);
        Assert.Equal("profiles-next.json", vm.ProfilesPath);
        Assert.Equal(beforeProfileLoads + 1, service.ProfileLoadCalls);
        Assert.Equal(beforeHistoryLoads + 1, service.HistoryCallsAfterInitialize);
    }

    [Fact]
    public async Task Deleting_user_profile_removes_it_from_view_model_catalog()
    {
        var service = new FakeAppService();
        var vm = await CreateInitialized(service);
        var userProfile = vm.SelectedProfile!;
        vm.DeleteProfileCommand.Execute(null);
        await WaitForIdle(vm);

        Assert.Equal(1, service.DeleteProfileCalls);
        Assert.DoesNotContain(vm.Profiles, profile => profile.Id == userProfile.Id);
        Assert.NotSame(userProfile, vm.SelectedProfile);
    }

    private static async Task<MainViewModel> CreateInitialized(FakeAppService service)
    {
        var vm = new MainViewModel(service, new DeployerAppOptions
        {
            BackupRoot = "test-backups",
            LedgerPath = "test-ledger.json",
            ProfilesPath = "test-profiles.json",
        });
        await vm.InitializeAsync();
        vm.RefreshCommand.Execute(null);
        await WaitForIdle(vm);
        vm.SelectedGame = vm.Games[0];
        vm.SelectedExecutable = vm.Games[0].ExecutableCandidates[0];
        return vm;
    }

    private static async Task WaitForIdle(MainViewModel vm)
    {
        await WaitUntil(() => !vm.IsBusy);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("The view-model operation did not complete.");
            await Task.Delay(10);
        }
    }

    private sealed class FakeAppService : IDeployerAppService
    {
        public int BuildPlanCalls { get; private set; }
        public int ApplyCalls { get; private set; }
        public int SaveProfileCalls { get; private set; }
        public int DeleteProfileCalls { get; private set; }
        public int HistoryCallsAfterInitialize { get; private set; }
        public int ConfigureStorageCalls { get; private set; }
        public int GameScanCalls { get; private set; }
        public int ProfileLoadCalls { get; private set; }
        public List<(string BackupRoot, string LedgerPath, string ProfilesPath)> ConfiguredPaths { get; } = [];
        public List<bool> RefreshArguments { get; } = [];
        public PlanBuildOptions? LastPlanOptions { get; private set; }
        public Func<DeploymentPlan, DeploymentPlan>? PlanTransform { get; init; }
        public Task<PlanBuildResult>? NextBuild { get; init; }
        public Task<ApplyWorkflowResult>? NextApply { get; init; }
        public bool WaitForBuildCancellation { get; init; }
        public bool BuildCancellationObserved { get; private set; }
        public string? ProfileLoadError { get; init; }
        public IReadOnlyList<string> WarningMessages { get; init; } = [];
        public IReadOnlyList<DiscoveredGame>? DiscoveredGames { get; init; }
        private readonly List<UpscalerProfile> _profiles = [Profile];

        public IReadOnlyList<string> Warnings => WarningMessages;
        public int HealthCalls { get; private set; }
        public bool LastHealthLaunch { get; private set; }
        public Deployer.Core.HealthCheck.HealthCheckStatus HealthStatus { get; init; } = Deployer.Core.HealthCheck.HealthCheckStatus.TimedOut;
        public Task<Deployer.Core.HealthCheck.HealthCheckResult> CheckHealthAsync(DeploymentPlan plan, string expectedBackend, bool launchGame, CancellationToken cancellationToken = default)
        { HealthCalls++; LastHealthLaunch = launchGame; return Task.FromResult(new Deployer.Core.HealthCheck.HealthCheckResult(HealthStatus, null, null, "waiting", null, HealthStatus == Deployer.Core.HealthCheck.HealthCheckStatus.TimedOut)); }
        public void ConfigureStoragePaths(string backupRoot, string ledgerPath, string profilesPath)
        {
            ConfigureStorageCalls++;
            ConfiguredPaths.Add((backupRoot, ledgerPath, profilesPath));
        }
        public Task<IReadOnlyList<DiscoveredGame>> GetGamesAsync(bool refresh, CancellationToken cancellationToken = default)
        {
            GameScanCalls++;
            RefreshArguments.Add(refresh);
            return Task.FromResult<IReadOnlyList<DiscoveredGame>>(DiscoveredGames ??
        [
            Game("One", "D:\\Games\\One", ["D:\\Games\\One\\Game.exe", "D:\\Games\\One\\alternate.exe"]),
            Game("Two", "D:\\Games\\Two", ["D:\\Games\\Two\\Other.exe"]),
        ]);
        }
        public Task AddManualFolderAsync(string title, string folder, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveManualFolderAsync(string folder, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public async Task<PlanBuildResult> BuildPlanAsync(PlanBuildOptions options, CancellationToken cancellationToken = default)
        {
            BuildPlanCalls++;
            LastPlanOptions = options;
            if (WaitForBuildCancellation)
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                catch (OperationCanceledException)
                {
                    BuildCancellationObserved = true;
                    throw;
                }
            }
            if (NextBuild is not null) return await NextBuild;
            var result = BuildResult(options.Game, options.ExecutablePath, PlanTransform);
            return result with { Plan = result.Plan with { Payload = result.Plan.Payload with { RootDirectory = options.PayloadDirectory ?? "payload" } } };
        }
        public async Task<ApplyWorkflowResult> ApplyAsync(DeploymentPlan plan, bool reviewAcknowledged, CancellationToken cancellationToken = default)
        {
            ApplyCalls++;
            if (NextApply is not null) return await NextApply;
            return new ApplyWorkflowResult(true, false, true, "manifest.json", [], null, null);
        }
        public Task<RollbackWorkflowResult> RollbackAsync(string manifestPath, CancellationToken cancellationToken = default) => Task.FromResult(new RollbackWorkflowResult(true, true, manifestPath, null, null));
        public Task<IReadOnlyList<HistoryItem>> GetHistoryAsync(CancellationToken cancellationToken = default)
        {
            HistoryCallsAfterInitialize++;
            return Task.FromResult<IReadOnlyList<HistoryItem>>([]);
        }
        public Task<ProfileCatalog> GetProfilesAsync(CancellationToken cancellationToken = default)
        {
            ProfileLoadCalls++;
            return Task.FromResult(new ProfileCatalog(_profiles.ToArray(), ProfileLoadError));
        }
        public Task SaveProfileAsync(UpscalerProfile profile, CancellationToken cancellationToken = default)
        {
            SaveProfileCalls++;
            return ProfileLoadError is null ? Task.CompletedTask : Task.FromException(new InvalidDataException(ProfileLoadError));
        }
        public Task<bool> DeleteProfileAsync(string id, CancellationToken cancellationToken = default)
        {
            DeleteProfileCalls++;
            _profiles.RemoveAll(profile => profile.Id == id);
            return Task.FromResult(true);
        }

        public static UpscalerProfile Profile { get; } = new()
        {
            Id = "test-profile", DisplayName = "Test profile", Description = "for tests", IsBuiltin = false, Edits = [],
        };

        public static PlanBuildResult BuildResult(DiscoveredGame game, string executable, Func<DeploymentPlan, DeploymentPlan>? transform = null)
        {
            var target = new GameTarget(game.Title, game.Platform, game.PackageFamilyName, game.AppId,
                game.InstallRoot, Path.GetDirectoryName(executable)!, Path.GetFileName(executable), "0123abcd");
            var plan = new DeploymentPlan
            {
                PlanId = Guid.NewGuid(), Game = target,
                Payload = new PayloadInfo(PayloadOrigin.ManualDirectory, "test", "payload"),
                Identity = new IdentityDecision(DllIdentity.Dxgi, "dxgi.dll", IdentityStatus.Resolved, [], null, null),
                Profile = Profile, Writability = DirectoryWritability.Writable, Elevation = ElevationRequirement.NotRequired,
                Writes = [new FileWrite("payload/OptiScaler.dll", "D:\\Games\\One\\dxgi.dll", 1, "00")],
                Backups = [], IniEdits = [], Warnings = [],
            };
            if (transform is not null) plan = transform(plan);
            var inspection = new InspectionReport
            {
                Summary = InspectionSummary.Empty, Writability = DirectoryWritability.Writable,
                Warnings = [], RecommendedInputEdits = [], IsSupported = true,
            };
            return new PlanBuildResult(target, inspection, plan);
        }

        private static DiscoveredGame Game(string title, string root, IReadOnlyList<string> exes) => new()
        {
            Title = title, Platform = GamePlatform.Manual, InstallRoot = root,
            ExecutableCandidates = exes, Writability = DirectoryWritability.Writable, ScannerSource = "test",
        };
    }

    private sealed class ViewModelWorkspace : IDisposable
    {
        private static readonly string TempParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "OptiScalerDeployer.ViewModelTests"));
        private readonly string _id = Guid.NewGuid().ToString("N");
        public ViewModelWorkspace()
        {
            Root = Path.GetFullPath(Path.Combine(TempParent, _id));
            Directory.CreateDirectory(Root);
        }
        public string Root { get; }
        public string BackupRoot => Path.Combine(Root, "backups");
        public string LedgerPath => Path.Combine(Root, "ledger.json");
        public string ProfilesPath => Path.Combine(Root, "profiles.json");
        public void Dispose()
        {
            var fullRoot = Path.GetFullPath(Root);
            if (!string.Equals(Path.GetDirectoryName(fullRoot), TempParent, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Path.GetFileName(fullRoot), _id, StringComparison.Ordinal)
                || !Guid.TryParseExact(_id, "N", out _)) return;
            if (Directory.Exists(fullRoot)) Directory.Delete(fullRoot, recursive: true);
        }
    }
}

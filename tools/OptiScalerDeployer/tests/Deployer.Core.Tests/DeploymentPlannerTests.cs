using System.IO.Abstractions.TestingHelpers;
using System.Text;
using Deployer.Core.Domain;
using Deployer.Core.Identity;
using Deployer.Core.Ini;
using Deployer.Core.Planning;
using Deployer.Core.Profiles;
using Deployer.Core.Serialization;
using Deployer.TestData;
using Xunit;

namespace Deployer.Core.Tests;

public class DeploymentPlannerTests
{
    private const string PayloadRoot = @"D:\repo\hlyOptiScaler\x64\Release\a";
    private const string BackupRoot = @"C:\users\tester\AppData\Local\OptiScalerDeployer\backups";
    private static readonly Guid FixedPlanId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    private readonly MockFileSystem _fs = new();
    private readonly FakeFileVersionReader _versions = new();
    private readonly FingerprintCalculator _fingerprints = new();

    private DeploymentPlanner Planner() => new(
        _fs, new DllIdentitySelector(_fs, _versions), new PayloadWalker(_fs), new IniPatcher());

    private FakeGame PrepareUeGame()
    {
        SyntheticPayloadFactory.Create(_fs, PayloadRoot);
        return FakeGameFactory.CreateUeGame(_fs);
    }

    [Fact]
    public void Stable_payload_uses_its_advertised_fsr31_aliases_for_amd_profile()
    {
        var game = PrepareUeGame();
        // Official v0.9.4 Upscalers section; unrelated ffx comments must not select the newer schema.
        _fs.File.WriteAllText(_fs.Path.Combine(PayloadRoot, "OptiScaler.ini"), "[Upscalers]\n; fsr31 (also for FSR4), dlss\nDx12Upscaler=auto\n; fsr31_12 (dx11on12, FSR4)\nDx11Upscaler=auto\n; fsr31_12 (VKon12, FSR4)\nVulkanUpscaler=auto\n[Other]\n; ffx-api documentation\n");
        var plan = Planner().Plan(Request(game));
        Assert.Equal("fsr31", plan.IniEdits.Single(e => e.Section == "Upscalers" && e.Key == "Dx12Upscaler").NewValue);
        Assert.Equal("fsr31_12", plan.IniEdits.Single(e => e.Section == "Upscalers" && e.Key == "Dx11Upscaler").NewValue);
        Assert.Equal("fsr31_12", plan.IniEdits.Single(e => e.Section == "Upscalers" && e.Key == "VulkanUpscaler").NewValue);
        Assert.Contains("Dx12Upscaler=fsr31", plan.Writes.Single(w => w.DestinationPath.EndsWith("OptiScaler.ini")).InlineContent);
        Assert.False(plan.HasBlockingWarning);
    }

    [Fact]
    public void Newer_payload_keeps_ffx_aliases()
    {
        var game = PrepareUeGame();
        var plan = Planner().Plan(Request(game));
        Assert.Equal("ffx", plan.IniEdits.Single(e => e.Section == "Upscalers" && e.Key == "Dx12Upscaler").NewValue);
        Assert.Equal("ffx_12", plan.IniEdits.Single(e => e.Section == "Upscalers" && e.Key == "Dx11Upscaler").NewValue);
    }

    private PlanRequest Request(
        FakeGame game,
        GamePlatform platform = GamePlatform.Steam,
        bool frameGen = false,
        bool allowOverwrite = false,
        bool preferAsi = false,
        bool enableLogging = true,
        InspectionSummary? inspection = null,
        DirectoryWritability writability = DirectoryWritability.Writable,
        UpscalerProfile? profile = null)
    {
        var target = new GameTarget(
            game.Title, platform, null, null, game.InstallRoot, game.DeployDirectory, game.ExecutableName,
            _fingerprints.Compute(platform, null, game.InstallRoot, game.ExecutableName));
        return new PlanRequest
        {
            Game = target,
            Inspection = inspection ?? game.DefaultInspection,
            Payload = new PayloadBundle(PayloadRoot, "0.9.1.0", PayloadOrigin.LocalBuild),
            Profile = profile ?? BuiltinProfiles.AmdRdna4,
            Writability = writability,
            BackupRoot = BackupRoot,
            EnableFrameGen = frameGen,
            AllowOverwriteGameFile = allowOverwrite,
            PreferAsiIdentity = preferAsi,
            EnableLogging = enableLogging,
            PlanId = FixedPlanId,
        };
    }

    [Fact]
    public void Plans_runtime_and_license_files_and_skips_installer_artifacts()
    {
        var game = PrepareUeGame();
        var plan = Planner().Plan(Request(game));
        var destinations = plan.Writes.Select(write => write.DestinationPath).ToArray();

        Assert.Contains($@"{game.DeployDirectory}\OptiScaler\amdxc64.dll", destinations);
        Assert.Contains($@"{game.DeployDirectory}\OptiScaler\D3D12_OptiScaler\d3d12core.dll", destinations);
        Assert.Contains($@"{game.DeployDirectory}\Licenses\FidelityFX_v2_LICENSE.md", destinations);
        Assert.DoesNotContain(plan.Writes, write => SyntheticPayloadFactory.SkippedFileNames
            .Any(name => write.DestinationPath.EndsWith(name, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Every_target_is_absolute_and_contained_by_deploy_directory()
    {
        var game = PrepareUeGame();
        var plan = Planner().Plan(Request(game));
        var root = _fs.Path.GetFullPath(game.DeployDirectory).TrimEnd('\\') + "\\";

        Assert.All(plan.Writes, write =>
        {
            Assert.True(_fs.Path.IsPathRooted(write.DestinationPath));
            Assert.StartsWith(root, write.DestinationPath, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void Plans_identity_dll_last_and_renames_payload_copy()
    {
        var game = PrepareUeGame();
        var plan = Planner().Plan(Request(game));
        var last = plan.Writes[^1];

        Assert.Equal(DllIdentity.Dxgi, plan.Identity.Chosen);
        Assert.Equal($@"{game.DeployDirectory}\dxgi.dll", last.DestinationPath);
        Assert.Equal($@"{PayloadRoot}\OptiScaler.dll", last.SourcePath);
        Assert.Null(last.InlineContent);
        Assert.False(last.IsInline);
    }

    [Fact]
    public void Writes_runtime_tree_then_ini_then_identity_dll()
    {
        var game = PrepareUeGame();
        var destinations = Planner().Plan(Request(game)).Writes.Select(write => write.DestinationPath).ToArray();
        var iniIndex = Array.FindIndex(destinations, path => path.EndsWith("OptiScaler.ini", StringComparison.OrdinalIgnoreCase));
        var identityIndex = Array.FindIndex(destinations, path => path.EndsWith("dxgi.dll", StringComparison.OrdinalIgnoreCase));
        var lastRuntime = Array.FindLastIndex(destinations, path =>
            path.Contains(@"\OptiScaler\", StringComparison.OrdinalIgnoreCase)
            || path.Contains(@"\Licenses\", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(destinations.Length - 2, iniIndex);
        Assert.Equal(destinations.Length - 1, identityIndex);
        Assert.True(lastRuntime < iniIndex);
    }

    [Fact]
    public void Ini_write_carries_patched_utf8_content_with_matching_hash_and_byte_length()
    {
        var game = PrepareUeGame();
        var plan = Planner().Plan(Request(game));
        var ini = plan.Writes[^2];
        var content = Assert.IsType<string>(ini.InlineContent);
        var bytes = Encoding.UTF8.GetBytes(content);

        Assert.True(ini.IsInline);
        Assert.Equal($@"{PayloadRoot}\OptiScaler.ini", ini.SourcePath);
        Assert.Contains("Dx12Upscaler=ffx", content);
        Assert.DoesNotContain("Dx12Upscaler=auto", content);
        Assert.False(content.StartsWith('\uFEFF'));
        Assert.Equal(bytes.Length, ini.Length);
        Assert.Equal(Deployer.Core.Util.Hashing.Sha256OfBytes(bytes), ini.Sha256);
    }

    [Fact]
    public void Ini_patch_preserves_original_comment_lines()
    {
        var game = PrepareUeGame();
        var original = _fs.File.ReadAllText($@"{PayloadRoot}\OptiScaler.ini");
        var patched = Planner().Plan(Request(game)).Writes[^2].InlineContent!;
        var patchedLines = patched.Split("\r\n");

        foreach (var comment in original.Split("\r\n").Where(line => line.TrimStart().StartsWith(';')))
        {
            Assert.Contains(comment, patchedLines);
        }
        Assert.True(patchedLines.Length >= original.Split("\r\n").Length);
    }

    [Fact]
    public void Reports_ini_keys_that_will_be_appended_when_missing_from_payload()
    {
        var game = PrepareUeGame();
        var iniPath = $@"{PayloadRoot}\OptiScaler.ini";
        _fs.File.WriteAllText(iniPath, _fs.File.ReadAllText(iniPath).Replace("LogToFile=auto\r\n", ""));

        var plan = Planner().Plan(Request(game));

        Assert.Contains(plan.IniEdits, edit => edit.Section == "Log" && edit.Key == "LogToFile");
        Assert.Contains(plan.Warnings, warning => warning.Severity == WarningSeverity.Info
            && warning.Message.Contains("Log.LogToFile", StringComparison.Ordinal));
    }

    [Fact]
    public void Process_filter_and_file_logging_are_forced_for_each_game()
    {
        var game = PrepareUeGame();
        var plan = Planner().Plan(Request(game));

        Assert.Contains(plan.IniEdits, edit => edit.Section == "ProcessFilter"
            && edit.Key == "TargetProcessName" && edit.NewValue == game.ExecutableName);
        Assert.Contains("TargetProcessName=" + game.ExecutableName, plan.Writes[^2].InlineContent);
        Assert.Contains(plan.IniEdits, edit => edit.Section == "Log" && edit.Key == "LogToFile" && edit.NewValue == "true");
        Assert.Contains(plan.IniEdits, edit => edit.Section == "Log" && edit.Key == "LogLevel" && edit.NewValue == "2");
    }

    [Fact]
    public void Logging_can_be_disabled_only_in_a_new_reviewed_plan()
    {
        var game = PrepareUeGame();
        var plan = Planner().Plan(Request(game, enableLogging: false));

        Assert.Contains(plan.IniEdits, edit => edit.Section == "Log" && edit.Key == "LogToFile" && edit.NewValue == "false");
        Assert.Contains(plan.IniEdits, edit => edit.Section == "Log" && edit.Key == "LogLevel" && edit.NewValue == "2");
        Assert.Contains("LogToFile=false", plan.Writes[^2].InlineContent);
    }

    [Fact]
    public void Frame_generation_is_opt_in_and_uses_streamline_input_when_detected()
    {
        var game = PrepareUeGame();
        var off = Planner().Plan(Request(game));
        Assert.Equal("false", off.IniEdits.Single(edit => edit.Section == "FrameGen" && edit.Key == "Enabled").NewValue);

        var inspection = game.DefaultInspection with
        {
            DetectedSdks = new[] { UpscalerSdk.Dlss, UpscalerSdk.Streamline },
        };
        var on = Planner().Plan(Request(game, frameGen: true, inspection: inspection));
        Assert.Equal("true", on.IniEdits.Single(edit => edit.Section == "FrameGen" && edit.Key == "Enabled").NewValue);
        Assert.Equal("dlssg", on.IniEdits.Single(edit => edit.Section == "FrameGen" && edit.Key == "FGInput").NewValue);
        Assert.DoesNotContain(on.IniEdits, edit => edit.Section == "OptiFG" && edit.Key == "HUDFix");
    }

    [Fact]
    public void Frame_generation_without_streamline_uses_upscaler_input_and_hudfix()
    {
        var game = PrepareUeGame();
        var plan = Planner().Plan(Request(game, frameGen: true));
        Assert.Equal("upscaler", plan.IniEdits.Single(edit => edit.Section == "FrameGen" && edit.Key == "FGInput").NewValue);
        Assert.Contains(plan.IniEdits, edit => edit.Section == "OptiFG" && edit.Key == "HUDFix" && edit.NewValue == "true");
    }

    [Fact]
    public void Existing_ini_and_previous_identity_are_planned_for_correct_backup_reasons()
    {
        var game = PrepareUeGame();
        var iniPath = $@"{game.DeployDirectory}\OptiScaler.ini";
        var identityPath = $@"{game.DeployDirectory}\dxgi.dll";
        _fs.AddFile(iniPath, new MockFileData("game ini"));
        _fs.AddFile(identityPath, new MockFileData("old OptiScaler"));
        _versions.SetOriginalFilename(identityPath, "OptiScaler.dll");

        var plan = Planner().Plan(Request(game));

        Assert.Contains(plan.Backups, backup => backup.OriginalPath == iniPath && backup.Reason == BackupReason.IniModified);
        Assert.Contains(plan.Backups, backup => backup.OriginalPath == identityPath
            && backup.Reason == BackupReason.OverwritePreviousOpti);
        Assert.DoesNotContain(plan.Warnings, warning => warning.Severity == WarningSeverity.Caution);
    }

    [Fact]
    public void Game_owned_payload_file_is_backed_up_and_plan_does_not_mutate_disk()
    {
        var game = PrepareUeGame();
        var payloadDll = $@"{PayloadRoot}\OptiScaler\amdxc64.dll";
        var destination = $@"{game.DeployDirectory}\OptiScaler\amdxc64.dll";
        _fs.AddFile(destination, new MockFileData("GAME MOD"));
        var originalPayload = _fs.File.ReadAllText(payloadDll);

        var plan = Planner().Plan(Request(game));

        Assert.Contains(plan.Backups, backup => backup.OriginalPath == destination
            && backup.Reason == BackupReason.OverwriteGameFile);
        Assert.Equal("GAME MOD", _fs.File.ReadAllText(destination));
        Assert.Equal(originalPayload, _fs.File.ReadAllText(payloadDll));
    }

    [Fact]
    public void All_conflicting_identity_slots_block_execution_and_plan_backup()
    {
        var game = PrepareUeGame();
        foreach (var identity in DllIdentitySelector.XboxPriority)
        {
            var path = _fs.Path.Combine(game.DeployDirectory, identity.FileName());
            _fs.AddFile(path, new MockFileData("GAME OWN"));
            _versions.SetOriginalFilename(path, "Game.dll");
        }

        var plan = Planner().Plan(Request(game, platform: GamePlatform.Xbox));

        Assert.Equal(IdentityStatus.RequiresUserDecision, plan.Identity.Status);
        Assert.True(plan.HasBlockingWarning);
        Assert.Contains(plan.Warnings, warning => warning.Severity == WarningSeverity.Blocking
            && warning.Message.Contains("OptiScaler.asi", StringComparison.Ordinal));
        Assert.Contains(plan.Backups, backup => backup.OriginalPath.EndsWith("winmm.dll", StringComparison.OrdinalIgnoreCase)
            && backup.Reason == BackupReason.OverwriteGameFile);
    }

    [Fact]
    public void Explicit_game_file_overwrite_resolves_identity_but_retains_backup_entry()
    {
        var game = PrepareUeGame();
        foreach (var identity in DllIdentitySelector.XboxPriority)
        {
            var path = _fs.Path.Combine(game.DeployDirectory, identity.FileName());
            _fs.AddFile(path, new MockFileData("GAME OWN"));
            _versions.SetOriginalFilename(path, "Game.dll");
        }

        var plan = Planner().Plan(Request(game, platform: GamePlatform.Xbox, allowOverwrite: true));

        Assert.Equal(IdentityStatus.Resolved, plan.Identity.Status);
        Assert.Contains(plan.Backups, backup => backup.OriginalPath.EndsWith("winmm.dll", StringComparison.OrdinalIgnoreCase)
            && backup.Reason == BackupReason.OverwriteGameFile);
        Assert.Contains(plan.Warnings, warning => warning.Severity == WarningSeverity.Caution
            && warning.Message.Contains("已有游戏文件", StringComparison.Ordinal));
    }

    [Fact]
    public void No_upscaler_sdk_produces_blocking_warning()
    {
        var game = PrepareUeGame();
        var plan = Planner().Plan(Request(game, inspection: game.NoSdkInspection));
        Assert.True(plan.HasBlockingWarning);
    }

    [Fact]
    public void Asi_choice_produces_caution()
    {
        var game = PrepareUeGame();
        var plan = Planner().Plan(Request(game, preferAsi: true));
        Assert.Equal(DllIdentity.Asi, plan.Identity.Chosen);
        Assert.Contains(plan.Warnings, warning => warning.Severity == WarningSeverity.Caution
            && warning.Message.Contains("ASI Loader", StringComparison.Ordinal));
    }

    [Fact]
    public void Maps_directory_writability_and_warnings()
    {
        var game = PrepareUeGame();
        var planner = Planner();
        var needsElevation = planner.Plan(Request(game, writability: DirectoryWritability.NeedsElevation));
        var blocked = planner.Plan(Request(game, writability: DirectoryWritability.Blocked));
        Assert.Equal(ElevationRequirement.Required, needsElevation.Elevation);
        Assert.Contains(needsElevation.Warnings, warning => warning.Severity == WarningSeverity.Caution);
        Assert.Equal(ElevationRequirement.Blocked, blocked.Elevation);
        Assert.True(blocked.HasBlockingWarning);
    }

    [Fact]
    public void Warns_when_ue_game_directory_is_not_binaries_win64()
    {
        var game = PrepareUeGame();
        var wrongTarget = new GameTarget(game.Title, GamePlatform.Steam, null, null,
            game.InstallRoot, game.InstallRoot, game.ExecutableName,
            _fingerprints.Compute(GamePlatform.Steam, null, game.InstallRoot, game.ExecutableName));
        var plan = Planner().Plan(Request(game) with { Game = wrongTarget });
        Assert.Contains(plan.Warnings, warning => warning.Severity == WarningSeverity.Caution
            && warning.Message.Contains("Binaries\\Win64", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Interop_notice_requires_matching_graphics_api_and_backend_key()
    {
        var game = PrepareUeGame();
        var dx11 = game.DefaultInspection with { GraphicsApis = new[] { GraphicsApi.Dx11 } };
        var vulkan = game.DefaultInspection with { GraphicsApis = new[] { GraphicsApi.Vulkan }, IsVulkanOnly = true };
        var ordinaryDx12 = game.DefaultInspection;

        Assert.Contains(Planner().Plan(Request(game, inspection: dx11)).Warnings,
            warning => warning.Severity == WarningSeverity.Info && warning.Message.Contains("DX11", StringComparison.Ordinal));
        Assert.Contains(Planner().Plan(Request(game, inspection: vulkan)).Warnings,
            warning => warning.Severity == WarningSeverity.Info && warning.Message.Contains("Vulkan", StringComparison.Ordinal));
        Assert.DoesNotContain(Planner().Plan(Request(game, inspection: ordinaryDx12)).Warnings,
            warning => warning.Message.Contains("interop", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Gdk_layout_uses_executable_directory_and_xbox_identity_priority()
    {
        SyntheticPayloadFactory.Create(_fs, PayloadRoot);
        var game = FakeGameFactory.CreateGdkGame(_fs);
        var plan = Planner().Plan(Request(game, platform: GamePlatform.Xbox));

        Assert.Equal(DllIdentity.Winmm, plan.Identity.Chosen);
        Assert.All(plan.Writes, write => Assert.StartsWith(game.DeployDirectory, write.DestinationPath, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(plan.Writes, write => _fs.Path.GetDirectoryName(write.DestinationPath) == game.InstallRoot);
    }

    [Fact]
    public void Same_fixed_request_produces_identical_serializable_plan()
    {
        var game = PrepareUeGame();
        var planner = Planner();
        var first = planner.Plan(Request(game, frameGen: true));
        var second = planner.Plan(Request(game, frameGen: true));
        JsonAssert.Equivalent(first, second);
        JsonAssert.Equivalent(first, DeployerJson.Deserialize<DeploymentPlan>(DeployerJson.Serialize(first)));
    }

    [Fact]
    public void Rejects_invalid_fingerprint_before_computing_backup_paths()
    {
        var game = PrepareUeGame();
        var request = Request(game) with { Game = Request(game).Game with { Fingerprint = @"..\escape" } };
        Assert.Throws<ArgumentException>(() => Planner().Plan(request));
    }
}

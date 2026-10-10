using Deployer.Cli;
using Deployer.Core.Deployment;
using Deployer.Core.Domain;
using Deployer.Core.Persistence;
using Deployer.Core.Serialization;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace Deployer.Core.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CliTestCollection
{
    public const string Name = "CLI tests";
}

[Collection(CliTestCollection.Name)]
public sealed class CliTests
{
    [Fact]
    public void Plan_apply_ledger_and_rollback_work_on_an_isolated_real_filesystem()
    {
        using var workspace = new TempWorkspace();
        var paths = workspace.CreateGame();
        var gameBefore = SnapshotGame(paths.InstallRoot);
        var planFile = workspace.At("plan.json");
        var ledgerFile = workspace.At("ledger.json");
        var backupRoot = workspace.At("backups");

        var plan = Run(PlanArgs(paths, planFile, backupRoot));
        Assert.Equal(0, plan.ExitCode);
        Assert.Contains("体检        : 受支持", plan.Output);
        var parsedPlan = DeployerJson.Deserialize<DeploymentPlan>(File.ReadAllText(planFile));
        Assert.Equal(DllIdentity.Winmm, parsedPlan.Identity.Chosen);
        Assert.Contains("Dx12", plan.Output);
        Assert.Contains("Dlss", plan.Output);
        Assert.Contains(parsedPlan.IniEdits, edit => edit.Section == "Inputs" && edit.Key == "EnableDlssInputs" && edit.NewValue == "true");
        Assert.Contains(parsedPlan.IniEdits, edit => edit.Section == "Inputs" && edit.Key == "EnableXeSSInputs" && edit.NewValue == "false");
        Assert.Contains(parsedPlan.Warnings, warning => warning.Message.Contains("dxgi.dll", StringComparison.OrdinalIgnoreCase));

        var apply = Run("apply", "--plan", planFile, "--backup-root", backupRoot, "--ledger", ledgerFile);
        Assert.Equal(0, apply.ExitCode);
        var identityPath = Path.Combine(paths.DeployDirectory, parsedPlan.Identity.FileName);
        Assert.True(File.Exists(identityPath));
        Assert.NotEqual(paths.OriginalIni, File.ReadAllText(Path.Combine(paths.DeployDirectory, "OptiScaler.ini")));
        var deployedIni = File.ReadAllBytes(Path.Combine(paths.DeployDirectory, "OptiScaler.ini"));
        Assert.False(deployedIni.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }));
        Assert.Equal(paths.PayloadIni.Split("\r\n").Count(line => line.StartsWith(';'))
                + (paths.PayloadIni.Contains("[Inputs]", StringComparison.OrdinalIgnoreCase) ? 0 : 1),
            Encoding.UTF8.GetString(deployedIni).Split("\r\n").Count(line => line.StartsWith(';')));
        Assert.Equal("game dxgi bytes", File.ReadAllText(Path.Combine(paths.DeployDirectory, "dxgi.dll")));
        Assert.Equal("unrelated mod bytes", File.ReadAllText(Path.Combine(paths.InstallRoot, "Mods", "mod.bin")));

        var installed = new JsonInstallLedger(new System.IO.Abstractions.FileSystem(), ledgerFile)
            .Get(parsedPlan.Game.Fingerprint)!;
        Assert.Equal(Path.GetFullPath(backupRoot), installed.BackupRoot);
        Assert.Equal(parsedPlan.PlanId, installed.LatestPlanId);
        var expectedManifest = Path.Combine(backupRoot, installed.Fingerprint, installed.LatestPlanId.ToString("D"), "manifest.json");
        var listing = Run("ledger", "--ledger", ledgerFile);
        Assert.Equal(0, listing.ExitCode);
        Assert.Contains(expectedManifest, listing.Output);

        var manifestPath = Path.Combine(backupRoot, installed.Fingerprint, installed.LatestPlanId.ToString("D"), "manifest.json");
        var rollback = Run("rollback", "--manifest", manifestPath, "--backup-root", backupRoot, "--ledger", ledgerFile);
        Assert.Equal(0, rollback.ExitCode);
        Assert.Equal("original ini\r\n", File.ReadAllText(Path.Combine(paths.DeployDirectory, "OptiScaler.ini")));
        Assert.False(File.Exists(identityPath));
        Assert.True(File.Exists(Path.Combine(paths.DeployDirectory, "Game.exe")));
        var gameAfter = SnapshotGame(paths.InstallRoot);
        Assert.Equal(gameBefore.Files.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase),
            gameAfter.Files.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase));
        Assert.Equal(gameBefore.Directories.OrderBy(path => path, StringComparer.OrdinalIgnoreCase),
            gameAfter.Directories.OrderBy(path => path, StringComparer.OrdinalIgnoreCase));
        Assert.Equal(InstallState.RolledBack,
            new JsonInstallLedger(new System.IO.Abstractions.FileSystem(), ledgerFile).Get(installed.Fingerprint)!.State);
    }

    [Fact]
    public void Plan_requires_an_executable_filename_that_exists_in_the_deploy_directory()
    {
        using var workspace = new TempWorkspace();
        var paths = workspace.CreateGame();
        var args = PlanArgs(paths, workspace.At("plan.json"), workspace.At("backups"));
        var exeIndex = Array.IndexOf(args, "--exe");
        args[exeIndex + 1] = @"..\Game.exe";

        var result = Run(args);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("单个 .exe 文件名", result.Error);
        Assert.False(File.Exists(workspace.At("plan.json")));
    }

    [Fact]
    public void Plan_rejects_output_inside_game_or_payload_and_deploy_directory_outside_game()
    {
        using var workspace = new TempWorkspace();
        var paths = workspace.CreateGame();
        var outputArgs = PlanArgs(paths, Path.Combine(paths.DeployDirectory, "plan.json"), workspace.At("backups"));
        var output = Run(outputArgs);
        var outsideDeployDir = workspace.At("outside-deploy");
        Directory.CreateDirectory(outsideDeployDir);
        var deployArgs = PlanArgs(paths, workspace.At("elsewhere.json"), workspace.At("backups"));
        deployArgs[Array.IndexOf(deployArgs, "--deploy-dir") + 1] = outsideDeployDir;

        var deploy = Run(deployArgs);

        Assert.Equal(1, output.ExitCode);
        Assert.Contains("--out 不能位于", output.Error);
        Assert.False(File.Exists(Path.Combine(paths.DeployDirectory, "plan.json")));
        Assert.Equal(1, deploy.ExitCode);
        Assert.Contains("部署目录必须位于游戏根目录内", deploy.Error);
    }

    [Fact]
    public void Plan_displays_profile_load_error_while_using_builtin_profile()
    {
        using var workspace = new TempWorkspace();
        var paths = workspace.CreateGame();
        File.WriteAllText(paths.ProfilesFile, "not json");
        var result = Run(PlanArgs(paths, workspace.At("plan.json"), workspace.At("backups")));

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Profile 配置读取提示", result.Error);
    }

    [Fact]
    public void Blocking_plan_cannot_be_applied_and_force_is_not_an_option()
    {
        using var workspace = new TempWorkspace();
        var paths = workspace.CreateGame();
        var planFile = workspace.At("blocked-plan.json");
        var args = PlanArgs(paths, planFile, workspace.At("backups")).Append("--no-sdk").ToArray();

        var plan = Run(args);
        Assert.Equal(2, plan.ExitCode);
        Assert.Contains("Blocking", plan.Output);
        var apply = Run("apply", "--plan", planFile, "--backup-root", workspace.At("backups"), "--ledger", workspace.At("ledger.json"));

        Assert.Equal(1, apply.ExitCode);
        Assert.Contains("阻塞级警告", apply.Error);
        Assert.False(File.Exists(Path.Combine(paths.DeployDirectory, "winmm.dll")));
        Assert.False(File.Exists(workspace.At("ledger.json")));

        var force = Run("apply", "--plan", planFile, "--force");
        Assert.Equal(1, force.ExitCode);
        Assert.Contains("未知参数：--force", force.Error);
    }

    [Fact]
    public void Corrupt_ledger_blocks_apply_before_game_files_change()
    {
        using var workspace = new TempWorkspace();
        var paths = workspace.CreateGame();
        var planFile = workspace.At("plan.json");
        var backupRoot = workspace.At("backups");
        Assert.Equal(0, Run(PlanArgs(paths, planFile, backupRoot)).ExitCode);
        var ledgerFile = workspace.At("ledger.json");
        File.WriteAllText(ledgerFile, "broken ledger");

        var result = Run("apply", "--plan", planFile, "--backup-root", backupRoot, "--ledger", ledgerFile);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("台账", result.Error);
        Assert.False(File.Exists(Path.Combine(paths.DeployDirectory, "winmm.dll")));
        Assert.Equal("broken ledger", File.ReadAllText(ledgerFile));
    }

    [Fact]
    public void Rollback_restores_game_even_when_corrupt_ledger_cannot_be_updated()
    {
        using var workspace = new TempWorkspace();
        var paths = workspace.CreateGame();
        var gameBefore = SnapshotGame(paths.InstallRoot);
        var planFile = workspace.At("plan.json");
        var backupRoot = workspace.At("backups");
        var ledgerFile = workspace.At("ledger.json");
        Assert.Equal(0, Run(PlanArgs(paths, planFile, backupRoot)).ExitCode);
        var plan = DeployerJson.Deserialize<DeploymentPlan>(File.ReadAllText(planFile));
        Assert.Equal(0, Run("apply", "--plan", planFile, "--backup-root", backupRoot, "--ledger", ledgerFile).ExitCode);
        File.WriteAllText(ledgerFile, "corrupt but retained");

        var manifest = Path.Combine(backupRoot, plan.Game.Fingerprint, plan.PlanId.ToString("D"), "manifest.json");
        var rollback = Run("rollback", "--manifest", manifest, "--backup-root", backupRoot, "--ledger", ledgerFile);

        Assert.Equal(1, rollback.ExitCode);
        Assert.Contains("回滚已成功，但台账未更改", rollback.Error);
        Assert.Equal("corrupt but retained", File.ReadAllText(ledgerFile));
        var after = SnapshotGame(paths.InstallRoot);
        Assert.Equal(gameBefore.Files.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase),
            after.Files.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase));
        Assert.Equal(gameBefore.Directories.OrderBy(path => path, StringComparer.OrdinalIgnoreCase),
            after.Directories.OrderBy(path => path, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void Rolling_back_an_older_plan_does_not_change_a_newer_ledger_record()
    {
        using var workspace = new TempWorkspace();
        var paths = workspace.CreateGame();
        var planFile = workspace.At("plan.json");
        var backupRoot = workspace.At("backups");
        var ledgerFile = workspace.At("ledger.json");
        Assert.Equal(0, Run(PlanArgs(paths, planFile, backupRoot)).ExitCode);
        var plan = DeployerJson.Deserialize<DeploymentPlan>(File.ReadAllText(planFile));
        Assert.Equal(0, Run("apply", "--plan", planFile, "--backup-root", backupRoot, "--ledger", ledgerFile).ExitCode);
        var ledger = new JsonInstallLedger(new System.IO.Abstractions.FileSystem(), ledgerFile);
        var installed = ledger.Get(plan.Game.Fingerprint)!;
        var newerPlanId = Guid.NewGuid();
        ledger.Save(installed with { LatestPlanId = newerPlanId });
        var manifest = Path.Combine(backupRoot, plan.Game.Fingerprint, plan.PlanId.ToString("D"), "manifest.json");

        var rollback = Run("rollback", "--manifest", manifest, "--backup-root", backupRoot, "--ledger", ledgerFile);

        Assert.Equal(0, rollback.ExitCode);
        Assert.Contains("台账指向其他计划", rollback.Output);
        var after = new JsonInstallLedger(new System.IO.Abstractions.FileSystem(), ledgerFile).Get(plan.Game.Fingerprint)!;
        Assert.Equal(newerPlanId, after.LatestPlanId);
        Assert.Equal(InstallState.Installed, after.State);
    }

    [Fact]
    public void Ledger_write_failure_reports_successful_deployment_and_keeps_manifest()
    {
        using var workspace = new TempWorkspace();
        var paths = workspace.CreateGame();
        var planFile = workspace.At("plan.json");
        var backupRoot = workspace.At("backups");
        Assert.Equal(0, Run(PlanArgs(paths, planFile, backupRoot)).ExitCode);
        var ledgerPathAsDirectory = workspace.At("ledger-directory");
        Directory.CreateDirectory(ledgerPathAsDirectory);

        var apply = Run("apply", "--plan", planFile, "--backup-root", backupRoot, "--ledger", ledgerPathAsDirectory);

        Assert.Equal(1, apply.ExitCode);
        Assert.Contains("部署已成功，manifest 已保留", apply.Error);
        Assert.Contains("台账更新失败", apply.Error);
        Assert.True(File.Exists(Path.Combine(paths.DeployDirectory, "winmm.dll")));
        Assert.True(File.Exists(Path.Combine(backupRoot,
            DeployerJson.Deserialize<DeploymentPlan>(File.ReadAllText(planFile)).Game.Fingerprint,
            DeployerJson.Deserialize<DeploymentPlan>(File.ReadAllText(planFile)).PlanId.ToString("D"), "manifest.json")));
    }

    [Fact]
    public void Parser_rejects_unknown_and_duplicate_options()
    {
        using var workspace = new TempWorkspace();
        var ledgerPath = workspace.At("ledger.json");

        var unknown = Run("ledger", "--unknown");
        var duplicate = Run("ledger", "--ledger", ledgerPath, "--ledger", ledgerPath);

        Assert.Equal(1, unknown.ExitCode);
        Assert.Contains("未知参数", unknown.Error);
        Assert.Equal(1, duplicate.ExitCode);
        Assert.Contains("参数不能重复", duplicate.Error);
    }

    [Fact]
    public void Vulkan_override_is_rejected_because_api_must_come_from_inspection()
    {
        using var workspace = new TempWorkspace();
        var paths = workspace.CreateGame();
        var planFile = workspace.At("vulkan-plan.json");
        var result = Run(PlanArgs(paths, planFile, workspace.At("backups")).Append("--vulkan").ToArray());

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("--vulkan 已移除", result.Error);
        Assert.False(File.Exists(planFile));
    }

    [Fact]
    public void Unsupported_executable_generates_blocking_plan_and_cannot_be_applied()
    {
        using var workspace = new TempWorkspace();
        var paths = workspace.CreateGame();
        File.WriteAllText(Path.Combine(paths.DeployDirectory, "Game.exe"), "not a PE image");
        var planFile = workspace.At("unsupported-plan.json");
        var backups = workspace.At("backups");

        var planResult = Run(PlanArgs(paths, planFile, backups));
        Assert.Equal(2, planResult.ExitCode);
        Assert.Contains("不支持/需处理", planResult.Output);
        Assert.Contains("有效 PE", planResult.Output);
        var apply = Run("apply", "--plan", planFile, "--backup-root", backups, "--ledger", workspace.At("ledger.json"));

        Assert.Equal(1, apply.ExitCode);
        Assert.Contains("阻塞级警告", apply.Error);
        Assert.False(File.Exists(Path.Combine(paths.DeployDirectory, "winmm.dll")));
    }

    [Fact]
    public void Manual_scan_inspect_plan_apply_rollback_uses_selected_candidate()
    {
        using var workspace = new TempWorkspace();
        var paths = workspace.CreateGame();
        var before = SnapshotGame(paths.InstallRoot);
        var scan = Run("scan", "--manual-root", paths.InstallRoot);
        Assert.Equal(0, scan.ExitCode);
        Assert.Contains(Path.Combine(paths.DeployDirectory, "Game.exe"), scan.Output);
        Assert.DoesNotContain("扫描警告", scan.Error);

        var inspect = Run("inspect", "--install-root", paths.InstallRoot, "--deploy-dir", paths.DeployDirectory,
            "--exe", "Game.exe", "--platform", "Manual");
        Assert.Equal(0, inspect.ExitCode);
        Assert.Contains("受支持", inspect.Output);
        var planFile = workspace.At("scan-plan.json");
        var backups = workspace.At("scan-backups");
        Assert.Equal(0, Run(PlanArgs(paths, planFile, backups)).ExitCode);
        var parsed = DeployerJson.Deserialize<DeploymentPlan>(File.ReadAllText(planFile));
        var ledgerFile = workspace.At("scan-ledger.json");
        Assert.Equal(0, Run("apply", "--plan", planFile, "--backup-root", backups, "--ledger", ledgerFile).ExitCode);
        var manifest = Path.Combine(backups, parsed.Game.Fingerprint, parsed.PlanId.ToString("D"), "manifest.json");
        Assert.Equal(0, Run("rollback", "--manifest", manifest, "--backup-root", backups, "--ledger", ledgerFile).ExitCode);
        var after = SnapshotGame(paths.InstallRoot);
        Assert.Equal(before.Files.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase),
            after.Files.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase));
        Assert.Equal(before.Directories.OrderBy(path => path, StringComparer.OrdinalIgnoreCase),
            after.Directories.OrderBy(path => path, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void Cli_patches_real_repository_ini_keys_without_losing_comments_or_utf8_format()
    {
        using var workspace = new TempWorkspace();
        var paths = workspace.CreateGame();
        var upstreamIni = Path.Combine(FindRepositoryRoot(), "OptiScaler.ini");
        var sourceText = Encoding.UTF8.GetString(File.ReadAllBytes(upstreamIni));
        File.Copy(upstreamIni, Path.Combine(paths.PayloadDirectory, "OptiScaler.ini"), overwrite: true);
        var before = SnapshotGame(paths.InstallRoot);
        var planFile = workspace.At("upstream-plan.json");
        var backupRoot = workspace.At("backups");
        var ledgerFile = workspace.At("ledger.json");

        Assert.Equal(0, Run(PlanArgs(paths, planFile, backupRoot)).ExitCode);
        var plan = DeployerJson.Deserialize<DeploymentPlan>(File.ReadAllText(planFile));
        Assert.Equal(0, Run("apply", "--plan", planFile, "--backup-root", backupRoot, "--ledger", ledgerFile).ExitCode);

        var deployedBytes = File.ReadAllBytes(Path.Combine(paths.DeployDirectory, "OptiScaler.ini"));
        var deployedText = Encoding.UTF8.GetString(deployedBytes);
        Assert.False(deployedBytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }));
        Assert.Equal(CountComments(sourceText), CountComments(deployedText));
        Assert.Equal("ffx", Setting(deployedText, "Upscalers", "Dx12Upscaler"));
        Assert.Equal("ffx_12", Setting(deployedText, "Upscalers", "Dx11Upscaler"));
        Assert.Equal("Game.exe", Setting(deployedText, "ProcessFilter", "TargetProcessName"));
        Assert.Equal("true", Setting(deployedText, "Log", "LogToFile"));

        var manifest = Path.Combine(backupRoot, plan.Game.Fingerprint, plan.PlanId.ToString("D"), "manifest.json");
        Assert.Equal(0, Run("rollback", "--manifest", manifest, "--backup-root", backupRoot, "--ledger", ledgerFile).ExitCode);
        var after = SnapshotGame(paths.InstallRoot);
        Assert.Equal(before.Files.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase),
            after.Files.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase));
        Assert.Equal(before.Directories.OrderBy(path => path, StringComparer.OrdinalIgnoreCase),
            after.Directories.OrderBy(path => path, StringComparer.OrdinalIgnoreCase));
    }

    private static string[] PlanArgs(GamePaths paths, string planFile, string backupRoot) => new[]
    {
        "plan", "--payload", paths.PayloadDirectory,
        "--install-root", paths.InstallRoot,
        "--deploy-dir", paths.DeployDirectory,
        "--exe", "Game.exe",
        "--profiles", paths.ProfilesFile,
        "--backup-root", backupRoot,
        "--out", planFile,
    };

    private static RunResult Run(params string[] args)
    {
        var oldOut = Console.Out;
        var oldError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            var exitCode = Program.Main(args);
            return new RunResult(exitCode, output.ToString(), error.ToString());
        }
        finally
        {
            Console.SetOut(oldOut);
            Console.SetError(oldError);
        }
    }

    private static TreeSnapshot SnapshotGame(string root)
    {
        var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(root, path),
                path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), StringComparer.OrdinalIgnoreCase);
        var directories = Directory.GetDirectories(root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new TreeSnapshot(files, directories);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "OptiScaler.sln"))
                && File.Exists(Path.Combine(directory.FullName, "OptiScaler.ini")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("无法从测试输出目录定位仓库根目录。");
    }

    private static int CountComments(string ini) => ini.Split("\r\n", StringSplitOptions.None)
        .Count(line => line.TrimStart().StartsWith(';'));

    private static string? Setting(string ini, string section, string key)
    {
        var inSection = false;
        foreach (var line in ini.Split("\r\n", StringSplitOptions.None))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                inSection = string.Equals(trimmed[1..^1], section, StringComparison.OrdinalIgnoreCase);
            }
            else if (inSection && trimmed.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed[(key.Length + 1)..];
            }
        }

        return null;
    }

    private sealed record RunResult(int ExitCode, string Output, string Error);
    private sealed record TreeSnapshot(IReadOnlyDictionary<string, string> Files, IReadOnlySet<string> Directories);
    private sealed record GamePaths(string InstallRoot, string DeployDirectory, string PayloadDirectory, string ProfilesFile, string OriginalIni, string PayloadIni);

    private sealed class TempWorkspace : IDisposable
    {
        private static readonly string TempParent = Path.Combine(Path.GetTempPath(), "OptiScalerDeployer.CliTests");
        private readonly string _id = Guid.NewGuid().ToString("N");

        public TempWorkspace()
        {
            Root = Path.GetFullPath(Path.Combine(TempParent, _id));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string At(params string[] paths) => paths.Aggregate(Root, Path.Combine);

        public GamePaths CreateGame()
        {
            var install = At("game");
            var deploy = Path.Combine(install, "Binaries", "Win64");
            var payload = At("payload");
            Directory.CreateDirectory(deploy);
            Directory.CreateDirectory(payload);
            Directory.CreateDirectory(At("profiles"));
            Directory.CreateDirectory(Path.Combine(install, "Mods"));
            File.WriteAllBytes(Path.Combine(deploy, "Game.exe"), Deployer.TestData.PeFixtureBuilder.BuildAmd64(["d3d12.dll", "nvngx_dlss.dll"]));
            File.WriteAllText(Path.Combine(deploy, "dxgi.dll"), "game dxgi bytes");
            File.WriteAllText(Path.Combine(install, "Mods", "mod.bin"), "unrelated mod bytes");
            const string originalIni = "original ini\r\n";
            File.WriteAllText(Path.Combine(deploy, "OptiScaler.ini"), originalIni);
            File.WriteAllText(Path.Combine(payload, "OptiScaler.dll"), "synthetic dll bytes");
            var payloadIni = Deployer.TestData.SyntheticPayloadFactory.SampleIniContent;
            File.WriteAllText(Path.Combine(payload, "OptiScaler.ini"), payloadIni, new UTF8Encoding(false));
            Directory.CreateDirectory(Path.Combine(payload, "OptiScaler"));
            File.WriteAllText(Path.Combine(payload, "OptiScaler", "runtime.dat"), "synthetic runtime");
            return new GamePaths(install, deploy, payload, At("profiles", "profiles.json"), originalIni, payloadIni);
        }

        public void Dispose()
        {
            var fullRoot = Path.GetFullPath(Root);
            if (!string.Equals(Path.GetDirectoryName(fullRoot), Path.GetFullPath(TempParent), StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Path.GetFileName(fullRoot), _id, StringComparison.Ordinal)
                || !Guid.TryParseExact(_id, "N", out _))
            {
                return;
            }

            if (Directory.Exists(fullRoot))
            {
                Directory.Delete(fullRoot, recursive: true);
            }
        }
    }
}

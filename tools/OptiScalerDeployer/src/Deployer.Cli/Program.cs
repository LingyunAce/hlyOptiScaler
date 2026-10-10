// See https://aka.ms/new-console-template for more information
using System.IO.Abstractions;
using System.Text;
using Deployer.Core.Abstractions;
using Deployer.Core.Deployment;
using Deployer.Core.Domain;
using Deployer.Core.GameDiscovery;
using Deployer.Core.Identity;
using Deployer.Core.Ini;
using Deployer.Core.Inspection;
using Deployer.Core.Payload;
using Deployer.Core.Persistence;
using Deployer.Core.Planning;
using Deployer.Core.Profiles;
using Deployer.Core.Serialization;
using Deployer.Core.Util;

namespace Deployer.Cli;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "plan" => Plan(args[1..]),
                "scan" => Scan(args[1..]),
                "inspect" => Inspect(args[1..]),
                "manual-add" => ManualAdd(args[1..]),
                "manual-remove" => ManualRemove(args[1..]),
                "apply" => Apply(args[1..]),
                "rollback" => Rollback(args[1..]),
                "ledger" => Ledger(args[1..]),
                _ => Fail($"未知子命令：{args[0]}"),
            };
        }
        catch (Exception ex)
        {
            return Fail(ex.Message, ex);
        }
    }

    private static int Plan(string[] args)
    {
        var o = Options.Parse(args,
            new[] { "--payload", "--install-root", "--deploy-dir", "--exe", "--title", "--platform", "--profile", "--profiles", "--backup-root", "--out", "--app-id", "--package-family-name" },
            new[] { "--framegen", "--allow-overwrite", "--no-sdk", "--vulkan" });
        var fs = new FileSystem();
        var payloadRoot = ExistingDirectory(o.Require("--payload"), "payload");
        var installRoot = ExistingDirectory(o.Require("--install-root"), "游戏根目录");
        var deployDir = ExistingDirectory(o.Get("--deploy-dir") ?? installRoot, "部署目录");
        if (!IsWithin(deployDir, installRoot))
        {
            throw new ArgumentException("部署目录必须位于游戏根目录内。");
        }

        var exeName = o.Require("--exe");
        if (Path.GetFileName(exeName) != exeName || exeName.Contains('/') || exeName.Contains('\\')
            || exeName.Contains('\r') || exeName.Contains('\n') || !exeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("--exe 必须是单个 .exe 文件名，不能包含目录或换行符。");
        }

        if (!File.Exists(Path.Combine(deployDir, exeName)))
        {
            throw new FileNotFoundException($"部署目录中找不到可执行文件：{exeName}", Path.Combine(deployDir, exeName));
        }

        var platform = ParsePlatform(o.Get("--platform"));

        var backupRoot = Path.GetFullPath(o.Get("--backup-root") ?? BackupStore.DefaultRoot);
        var versions = new WindowsFileVersionReader();
        var profileStore = new JsonProfileStore(fs, Path.GetFullPath(o.Get("--profiles") ?? JsonProfileStore.DefaultPath));
        if (profileStore.LoadError is { } profileError)
        {
            Console.Error.WriteLine($"Profile 配置读取提示：{profileError}");
        }

        var profileId = o.Get("--profile") ?? BuiltinProfiles.AmdRdna4Id;
        var profile = profileStore.Find(profileId)
            ?? throw new ArgumentException($"未知的 profile：{profileId}。可用：{string.Join(", ", profileStore.All().Select(p => p.Id))}");
        if (o.Has("--vulkan")) throw new ArgumentException("--vulkan 已移除：图形 API 必须来自 PE 体检，不能由参数覆盖。");

        var payload = new ManualDirectoryPayloadSource(payloadRoot, fs, versions)
            .TryGetAsync(CancellationToken.None).GetAwaiter().GetResult()
            ?? throw new Deployer.Core.Errors.PayloadNotFoundException(
                $"{payloadRoot} 里找不到 OptiScaler.dll + OptiScaler.ini");
        var title = o.Get("--title") ?? Path.GetFileName(installRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(title))
        {
            title = "Unknown";
        }

        var packageFamily = o.Get("--package-family-name");
        var appId = o.Get("--app-id");
        var target = new GameTarget(
            title, platform, packageFamily, appId, installRoot, deployDir, exeName,
            new FingerprintCalculator().Compute(platform, packageFamily, installRoot, exeName));
        var inspector = new GameInspector(fs);
        var report = inspector.InspectAsync(target, CancellationToken.None).GetAwaiter().GetResult();
        Console.WriteLine($"体检        : {(report.IsSupported ? "受支持" : "不支持/需处理")}");
        Console.WriteLine($"图形 API    : {string.Join(", ", report.Summary.GraphicsApis)}");
        Console.WriteLine($"超分 SDK    : {string.Join(", ", report.Summary.DetectedSdks)}");
        Console.WriteLine($"可写性      : {report.Writability}");
        foreach (var warning in report.Warnings) Console.WriteLine($"  [体检/{warning.Severity}] {warning.Message}");
        var inspection = report.Summary;
        var inspectionWarnings = report.Warnings.ToList();
        if (!report.IsSupported && !inspectionWarnings.Any(warning => warning.Severity == WarningSeverity.Blocking))
        {
            inspectionWarnings.Add(new PlanWarning(WarningSeverity.Blocking, "游戏体检未将该程序标记为受支持，CLI 已阻止部署。"));
        }
        if (report.Writability != DirectoryWritability.Writable)
        {
            inspectionWarnings.Add(new PlanWarning(WarningSeverity.Blocking,
                $"当前 CLI 尚不支持 {report.Writability} 目录部署；已阻止计划执行。"));
        }
        if (o.Has("--no-sdk"))
        {
            inspectionWarnings.Add(new PlanWarning(WarningSeverity.Blocking, "--no-sdk 强制将此计划标记为阻塞。"));
        }
        var plan = new DeploymentPlanner(fs,
            new DllIdentitySelector(fs, versions), new PayloadWalker(fs), new IniPatcher())
            .Plan(new PlanRequest
            {
                Game = target,
                Inspection = inspection,
                RecommendedInputEdits = report.RecommendedInputEdits,
                InspectionWarnings = inspectionWarnings,
                Payload = payload,
                Profile = profile,
                Writability = report.Writability,
                BackupRoot = backupRoot,
                EnableFrameGen = o.Has("--framegen"),
                AllowOverwriteGameFile = o.Has("--allow-overwrite"),
            });

        PrintPlan(plan);
        if (o.Get("--out") is { } requestedOut)
        {
            var outPath = Path.GetFullPath(requestedOut);
            if (IsWithin(outPath, installRoot) || IsWithin(outPath, payloadRoot))
            {
                throw new ArgumentException("--out 不能位于游戏目录或 payload 目录中。");
            }

            var json = DeployerJson.Serialize(plan);
            new AtomicFileWriter(fs).Write(new FileWrite(
                string.Empty, outPath, Encoding.UTF8.GetByteCount(json), Hashing.Sha256OfString(json), json));
            Console.WriteLine($"\n计划已写入 {outPath}");
        }

        return plan.HasBlockingWarning ? 2 : 0;
    }

    private static int Scan(string[] args)
    {
        var o = Options.Parse(args, new[] { "--manual-root", "--manual-config", "--ledger" }, Array.Empty<string>());
        var fs = new FileSystem();
        var probe = new DirectoryWritabilityProbe(fs);
        var store = new ManualFolderStore(fs, new AtomicFileWriter(fs), Path.GetFullPath(o.Get("--manual-config") ?? ManualFoldersDefaultPath));
        IReadOnlyList<DiscoveredGame> games;
        IReadOnlyList<string> scanWarnings;
        if (o.Get("--manual-root") is { } root)
        {
            var fullFolder = ExistingDirectory(root, "手动游戏目录");
            if (o.Has("--manual-config")) throw new ArgumentException("--manual-config 仅用于读取持久手动目录；--manual-root 是一次性扫描。");
            var title = Path.GetFileName(fullFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var scanner = new ManualFolderScanner(fs, probe, new[] { new ManualFolderEntry(title, fullFolder) });
            games = scanner.ScanAsync(CancellationToken.None).GetAwaiter().GetResult();
            scanWarnings = scanner.Warnings;
        }
        else
        {
            var library = new GameLibrary(new IGameScanner[] { new SteamScanner(fs, probe), new EpicScanner(fs, probe), new XboxScanner(fs, probe, new PowerShellXboxPackageSource()), new ManualFolderScanner(fs, probe, store) });
            games = library.GetGamesAsync(refresh: true, CancellationToken.None).GetAwaiter().GetResult();
            scanWarnings = library.Warnings;
        }
        if (games.Count == 0) Console.WriteLine("未发现游戏。");
        foreach (var game in games)
        {
            Console.WriteLine($"{game.Title} [{game.Platform}] {game.InstallRoot}");
            Console.WriteLine($"  PackageFamilyName: {game.PackageFamilyName ?? "<none>"}; AppId: {game.AppId ?? "<none>"}; Writability: {game.Writability}");
            foreach (var candidate in game.ExecutableCandidates) Console.WriteLine($"  EXE: {candidate}");
            if (game.ExecutableCandidates.Count == 0) Console.WriteLine("  未找到候选可执行文件");
        }
        foreach (var warning in scanWarnings) Console.Error.WriteLine($"扫描警告：{warning}");

        if (o.Get("--ledger") is { } ledgerPath)
        {
            var ledger = new JsonInstallLedger(fs, Path.GetFullPath(ledgerPath));
            EnsureLedgerReadable(ledger);
            var reconciliation = new InstallLedgerReconciler(fs, ledger).MarkMissingIdentities(games, scanWarnings);
            if (reconciliation.MarkedNeedsReinstall > 0) Console.WriteLine($"已将 {reconciliation.MarkedNeedsReinstall} 个缺少身份 DLL 的安装标为 NeedsReinstall。");
            foreach (var warning in reconciliation.Warnings) Console.Error.WriteLine($"台账对账提示：{warning}");
        }
        return scanWarnings.Count > 0 ? 1 : 0;
    }

    private static int Inspect(string[] args)
    {
        var o = Options.Parse(args, new[] { "--install-root", "--deploy-dir", "--exe", "--title", "--platform", "--app-id", "--package-family-name" }, Array.Empty<string>());
        var fs = new FileSystem();
        var root = ExistingDirectory(o.Require("--install-root"), "游戏根目录");
        var deploy = ExistingDirectory(o.Get("--deploy-dir") ?? root, "部署目录");
        if (!IsWithin(deploy, root)) throw new ArgumentException("--deploy-dir 必须位于 --install-root 内。");
        var exeName = o.Require("--exe");
        if (Path.GetFileName(exeName) != exeName || exeName.Contains('/') || exeName.Contains('\\')
            || !exeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("--exe 必须是部署目录内的单个 .exe 文件名。");
        var exePath = Path.GetFullPath(Path.Combine(deploy, exeName));
        var platform = ParsePlatform(o.Get("--platform"));
        var exe = exeName;
        var packageFamily = o.Get("--package-family-name");
        var game = new GameTarget(o.Get("--title") ?? Path.GetFileName(root), platform, packageFamily, o.Get("--app-id"), root, deploy, exe,
            new FingerprintCalculator().Compute(platform, packageFamily, root, exe));
        var report = new GameInspector(fs).InspectAsync(game, CancellationToken.None).GetAwaiter().GetResult();
        Console.WriteLine($"{game.Title}: {(report.IsSupported ? "受支持" : "不支持/需处理")} [{report.Writability}]");
        Console.WriteLine($"图形 API: {string.Join(", ", report.Summary.GraphicsApis)}; 超分 SDK: {string.Join(", ", report.Summary.DetectedSdks)}");
        foreach (var warning in report.Warnings) Console.WriteLine($"[{warning.Severity}] {warning.Message}");
        return report.IsSupported && report.Writability == DirectoryWritability.Writable
            && !report.Warnings.Any(warning => warning.Severity == WarningSeverity.Blocking) ? 0 : 2;
    }


    private static int ManualAdd(string[] args)
    {
        var o = Options.Parse(args, new[] { "--title", "--folder", "--manual-config" }, Array.Empty<string>());
        var fs = new FileSystem();
        var store = new ManualFolderStore(fs, new AtomicFileWriter(fs), Path.GetFullPath(o.Get("--manual-config") ?? ManualFoldersDefaultPath));
        store.Add(o.Require("--title"), ExistingDirectory(o.Require("--folder"), "手动游戏目录"));
        Console.WriteLine("手动游戏目录已保存。运行 scan 查看候选程序。");
        return 0;
    }

    private static int ManualRemove(string[] args)
    {
        var o = Options.Parse(args, new[] { "--folder", "--manual-config" }, Array.Empty<string>());
        var fs = new FileSystem();
        var store = new ManualFolderStore(fs, new AtomicFileWriter(fs), Path.GetFullPath(o.Get("--manual-config") ?? ManualFoldersDefaultPath));
        store.Remove(Path.GetFullPath(o.Require("--folder")));
        Console.WriteLine("手动游戏目录已移除。");
        return 0;
    }

    private static int Apply(string[] args)
    {
        var o = Options.Parse(args, new[] { "--plan", "--backup-root", "--ledger" }, Array.Empty<string>());
        var fs = new FileSystem();
        var planPath = Path.GetFullPath(o.Require("--plan"));
        var plan = DeployerJson.Deserialize<DeploymentPlan>(fs.File.ReadAllText(planPath));
        if (plan.HasBlockingWarning)
        {
            return Fail("计划包含阻塞级警告，不能执行。请先处理警告并重新生成计划。");
        }

        var backupRoot = Path.GetFullPath(o.Get("--backup-root") ?? BackupStore.DefaultRoot);
        var ledger = new JsonInstallLedger(fs, Path.GetFullPath(o.Get("--ledger") ?? JsonInstallLedger.DefaultPath));
        EnsureLedgerReadable(ledger);
        var store = new BackupStore(fs, backupRoot);
        var writer = new AtomicFileWriter(fs);
        var result = new DeploymentExecutor(fs, writer, store, new RollbackExecutor(fs, writer, store))
            .Execute(plan, CancellationToken.None);
        Console.WriteLine(result.Success
            ? $"部署成功。manifest: {result.ManifestPath}\n共写入 {result.CompletedSteps.Count} 个文件。"
            : $"部署失败：{result.ErrorMessage}\n已回滚：{result.RolledBack}");
        if (!result.Success)
        {
            return 1;
        }

        try
        {
            ledger.Save(new InstallRecord
            {
                Fingerprint = plan.Game.Fingerprint,
                GameTitle = plan.Game.Title,
                DeployDirectory = plan.Game.DeployDirectory,
                LatestPlanId = plan.PlanId,
                BackupRoot = backupRoot,
                State = InstallState.Installed,
                PayloadVersion = plan.Payload.Version,
                IdentityFileName = plan.Identity.FileName,
                InstalledUtc = DateTime.UtcNow,
            });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"部署已成功，manifest 已保留在 {result.ManifestPath}，但台账更新失败：{ex.Message}");
            Console.Error.WriteLine("游戏文件保持已部署状态；可使用该 manifest 和相同备份根执行 rollback。");
            return 1;
        }

        Console.WriteLine("台账已更新。");
        return 0;
    }

    private static int Rollback(string[] args)
    {
        var o = Options.Parse(args, new[] { "--manifest", "--backup-root", "--ledger" }, Array.Empty<string>());
        var fs = new FileSystem();
        var manifestPath = Path.GetFullPath(o.Require("--manifest"));
        var backupRoot = Path.GetFullPath(o.Get("--backup-root") ?? BackupStore.DefaultRoot);
        var store = new BackupStore(fs, backupRoot);
        var manifest = store.ReadManifest(manifestPath)
            ?? throw new FileNotFoundException($"找不到备份清单：{manifestPath}", manifestPath);
        var result = new RollbackExecutor(fs, new AtomicFileWriter(fs), store).Rollback(manifestPath);
        Console.WriteLine(result.Success
            ? $"回滚成功。还原 {result.RestoredPaths.Count} 个原件，删除 {result.DeletedPaths.Count} 个新增文件。"
            : $"回滚失败：{result.ErrorMessage}");
        if (!result.Success)
        {
            return 1;
        }

        var ledger = new JsonInstallLedger(fs, Path.GetFullPath(o.Get("--ledger") ?? JsonInstallLedger.DefaultPath));
        try
        {
            var current = ledger.Get(manifest.Fingerprint);
            if (current?.LatestPlanId == manifest.PlanId)
            {
                ledger.SetState(manifest.Fingerprint, InstallState.RolledBack);
                Console.WriteLine("匹配的台账记录已标记为 RolledBack。");
            }
            else
            {
                Console.WriteLine("台账指向其他计划，未更改台账状态。");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"回滚已成功，但台账未更改：{ex.Message}");
            return 1;
        }

        return 0;
    }

    private static int Ledger(string[] args)
    {
        var o = Options.Parse(args, new[] { "--ledger" }, Array.Empty<string>());
        var ledger = new JsonInstallLedger(new FileSystem(), Path.GetFullPath(o.Get("--ledger") ?? JsonInstallLedger.DefaultPath));
        IReadOnlyList<InstallRecord> all;
        try
        {
            all = ledger.All();
        }
        catch (Exception ex)
        {
            return Fail(ledger.LoadError ?? $"无法读取安装台账：{ex.Message}");
        }

        if (ledger.LoadError is { } loadError)
        {
            return Fail(loadError);
        }
        if (all.Count == 0)
        {
            Console.WriteLine("台账为空。");
            return 0;
        }

        foreach (var record in all)
        {
            Console.WriteLine($"{record.Fingerprint}  {record.State,-14} {record.IdentityFileName,-16} v{record.PayloadVersion,-10} {record.GameTitle}");
            Console.WriteLine($"            {record.DeployDirectory}");
            Console.WriteLine($"            manifest: {Path.Combine(record.BackupRoot, record.Fingerprint, record.LatestPlanId.ToString("D"), "manifest.json")}");
        }

        return 0;
    }

    private static void EnsureLedgerReadable(JsonInstallLedger ledger)
    {
        try
        {
            _ = ledger.All();
        }
        catch (Exception ex)
        {
            throw new InvalidDataException(ledger.LoadError ?? $"无法读取安装台账：{ex.Message}", ex);
        }

        if (ledger.LoadError is { } loadError)
        {
            throw new InvalidDataException(loadError);
        }
    }

    private static string ExistingDirectory(string path, string label)
    {
        var full = Path.GetFullPath(path);
        if (!Directory.Exists(full))
        {
            throw new DirectoryNotFoundException($"{label}不存在：{full}");
        }

        return full;
    }

    private static string ManualFoldersDefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OptiScalerDeployer", "manual-folders.json");

    private static GamePlatform ParsePlatform(string? value)
    {
        if (!Enum.TryParse<GamePlatform>(value ?? "Manual", true, out var platform) || !Enum.IsDefined(platform))
            throw new ArgumentException($"未知平台：{value}");
        return platform;
    }

    private static bool IsWithin(string path, string root)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return relative == "." || (relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal)
            && !Path.IsPathRooted(relative));
    }

    private static void PrintPlan(DeploymentPlan plan)
    {
        Console.WriteLine($"=== 部署计划 {plan.PlanId:D} ===");
        Console.WriteLine($"游戏        : {plan.Game.Title}  [{plan.Game.Platform}]");
        Console.WriteLine($"部署目录    : {plan.Game.DeployDirectory}");
        Console.WriteLine($"可执行文件  : {plan.Game.ExecutableName}");
        Console.WriteLine($"指纹        : {plan.Game.Fingerprint}");
        Console.WriteLine($"Payload     : {plan.Payload.Origin} v{plan.Payload.Version}  ({plan.Payload.RootDirectory})");
        Console.WriteLine($"Profile     : {plan.Profile.Id} - {plan.Profile.DisplayName}");
        Console.WriteLine($"身份 DLL    : {plan.Identity.FileName}  [{plan.Identity.Status}]"
                        + (plan.Identity.ExistingFileReason is { } reason ? $"  将覆盖（{reason}）" : "  纯新增"));
        Console.WriteLine($"提权        : {plan.Elevation}");
        Console.WriteLine($"\n将写入 {plan.Writes.Count} 个文件（按落盘顺序）：");
        foreach (var write in plan.Writes)
        {
            Console.WriteLine($"  {(write.IsInline ? "[patch]" : "[copy] ")} {write.DestinationPath}");
        }

        Console.WriteLine($"\n将备份 {plan.Backups.Count} 个原件：");
        foreach (var backup in plan.Backups)
        {
            Console.WriteLine($"  {backup.OriginalPath}  ->  {backup.BackupPath}  ({backup.Reason})");
        }

        Console.WriteLine($"\nINI 覆盖 {plan.IniEdits.Count} 项：");
        foreach (var edit in plan.IniEdits)
        {
            Console.WriteLine($"  [{edit.Section}] {edit.Key}: {edit.OldValue ?? "<新增>"} -> {edit.NewValue}");
        }

        if (plan.Warnings.Count > 0)
        {
            Console.WriteLine($"\n警告 {plan.Warnings.Count} 条：");
            foreach (var warning in plan.Warnings)
            {
                Console.WriteLine($"  [{warning.Severity}] {warning.Message}");
            }
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            opti-deploy —— OptiScaler Deployer CLI（Plan A/B：Core 与游戏扫描体检）
            子命令：scan / inspect / manual-add / manual-remove / plan / apply / rollback / ledger
            scan 参数：[--manual-root <dir>] [--manual-config <file>] [--ledger <file>]
            inspect 参数：--install-root <dir> --exe <name> [--deploy-dir <dir>] [--title <name>] [--platform Xbox|Steam|Epic|Manual]
            manual-add 参数：--title <name> --folder <dir> [--manual-config <file>]
            manual-remove 参数：--folder <dir> [--manual-config <file>]
            plan 参数：--payload <dir> --install-root <dir> --exe <name> [--deploy-dir <dir>]
              [--title <name>] [--platform Xbox|Steam|Epic|Manual] [--package-family-name <name>] [--app-id <id>]
              [--profile <id>] [--profiles <file>] [--backup-root <dir>] [--out <file>] [--framegen] [--allow-overwrite] [--no-sdk]
            apply 参数：--plan <file> [--backup-root <dir>] [--ledger <file>]
            rollback 参数：--manifest <file> [--backup-root <dir>] [--ledger <file>]
            ledger 参数：[--ledger <file>]
            阻塞警告不能绕过；退出码：0 成功 | 1 失败 | 2 计划含 Blocking 警告
            """);
    }

    private static int Fail(string message, Exception? ex = null)
    {
        Console.Error.WriteLine($"错误：{message}");
        if (ex is not null)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine(ex.ToString());
        }

        return 1;
    }

    private sealed class Options
    {
        private readonly Dictionary<string, string?> _values = new(StringComparer.OrdinalIgnoreCase);

        public static Options Parse(string[] args, IReadOnlyCollection<string> valueKeys, IReadOnlyCollection<string> flagKeys)
        {
            var values = new HashSet<string>(valueKeys, StringComparer.OrdinalIgnoreCase);
            var flags = new HashSet<string>(flagKeys, StringComparer.OrdinalIgnoreCase);
            var options = new Options();
            for (var i = 0; i < args.Length; i++)
            {
                var key = args[i];
                if (!key.StartsWith("--", StringComparison.Ordinal))
                {
                    throw new ArgumentException($"不接受位置参数：{key}");
                }

                var takesValue = values.Contains(key);
                if (!takesValue && !flags.Contains(key))
                {
                    throw new ArgumentException($"未知参数：{key}");
                }

                if (options._values.ContainsKey(key))
                {
                    throw new ArgumentException($"参数不能重复：{key}");
                }

                string? value = null;
                if (takesValue)
                {
                    if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        throw new ArgumentException($"参数缺少值：{key}");
                    }

                    value = args[++i];
                }

                options._values.Add(key, value);
            }

            return options;
        }

        public bool Has(string key) => _values.ContainsKey(key);
        public string? Get(string key) => _values.GetValueOrDefault(key);
        public string Require(string key) => !string.IsNullOrWhiteSpace(Get(key))
            ? Get(key)!
            : throw new ArgumentException($"缺少必需参数：{key}");
    }
}

using System.Collections.Concurrent;
using System.IO;
using System.IO.Abstractions;
using Deployer.App.Models;
using Deployer.Core.Abstractions;
using Deployer.Core.Deployment;
using Deployer.Core.Domain;
using Deployer.Core.GameDiscovery;
using Deployer.Core.Identity;
using Deployer.Core.Ini;
using Deployer.Core.Inspection;
using Deployer.Core.Planning;
using Deployer.Core.Payload;
using Deployer.Core.Profiles;
using Deployer.Core.Persistence;
using Deployer.Core.Elevation;
using Deployer.Core.HealthCheck;
using System.Net.Http;

namespace Deployer.App.Services;

/// <summary>Physical-file-system composition for the desktop application.</summary>
public sealed class DefaultDeployerAppService : IDeployerAppService
{
    private readonly IFileSystem _fs;
    private readonly DeployerAppOptions _options;
    private readonly ManualFolderStore _manualFolders;
    private readonly GameLibrary _library;
    private readonly IGameInspector _inspector;
    private readonly IDeploymentPlanner _planner;
    private readonly IFingerprintCalculator _fingerprints = new FingerprintCalculator();
    private IInstallLedger _ledger;
    private IProfileStore? _profiles;
    private string _backupRoot;
    private string _ledgerPath, _profilesPath;
    private string _activeLedgerPath, _activeProfilesPath;
    private readonly IFileVersionReader _versions = new WindowsFileVersionReader();
    private readonly ConcurrentDictionary<Guid, string> _planBackupRoots = new();
    private readonly SemaphoreSlim _applyGate = new(1, 1);
    private readonly IJobTransport _jobs;
    private readonly IHealthCheckRunner _health;
    private readonly Func<string, DirectoryWritability> _probe;
    private readonly ConcurrentDictionary<Guid, long> _healthOffsets = new();
    private static readonly HttpClient ReleaseHttp = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(5) };
    private IReadOnlyList<string> _scanWarnings = Array.Empty<string>();
    private IReadOnlyList<string> _historyWarnings = Array.Empty<string>();

    public DefaultDeployerAppService(DeployerAppOptions? options = null, IFileSystem? fileSystem = null, IEnumerable<IGameScanner>? scanners = null,
        IJobTransport? jobTransport = null, IHealthCheckRunner? healthRunner = null, Func<string, DirectoryWritability>? writabilityProbe = null)
    {
        _options = options ?? new DeployerAppOptions();
        _backupRoot = Path.GetFullPath(_options.BackupRoot);
        _ledgerPath = Path.GetFullPath(_options.LedgerPath);
        _profilesPath = Path.GetFullPath(_options.ProfilesPath);
        _activeLedgerPath = _ledgerPath; _activeProfilesPath = string.Empty;
        _fs = fileSystem ?? new FileSystem();
        var writer = new AtomicFileWriter(_fs);
        _manualFolders = new ManualFolderStore(_fs, writer, _options.ManualFoldersPath);
        var probe = new DirectoryWritabilityProbe(_fs);
        _probe = writabilityProbe ?? probe.Probe;
        _jobs = jobTransport ?? new RunAsJobTransport(_options.WorkerPath);
        _health = healthRunner ?? new HealthCheckRunner(new ProcessGameLauncher(), new FileHealthLogReader());
        var configuredScanners = scanners?.ToArray() ??
        [
            new SteamScanner(_fs, probe),
            new EpicScanner(_fs, probe),
            new XboxScanner(_fs, probe, new PowerShellXboxPackageSource()),
            new ManualFolderScanner(_fs, probe, _manualFolders),
        ];
        _library = new GameLibrary(configuredScanners);
        _inspector = new GameInspector(_fs);
        _planner = new DeploymentPlanner(_fs, new DllIdentitySelector(_fs, _versions), new PayloadWalker(_fs), new IniPatcher());
        _ledger = new JsonInstallLedger(_fs, _ledgerPath);
    }

    public void ConfigureStoragePaths(string backupRoot, string ledgerPath, string profilesPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(ledgerPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(profilesPath);
        if (!_applyGate.Wait(0)) throw new InvalidOperationException("部署或回滚期间不能修改存储路径。");
        try
        {
            var nextBackupRoot = _fs.Path.GetFullPath(backupRoot);
            var nextLedgerPath = _fs.Path.GetFullPath(ledgerPath);
            var nextProfilesPath = _fs.Path.GetFullPath(profilesPath);
            _backupRoot = nextBackupRoot; _ledgerPath = nextLedgerPath; _profilesPath = nextProfilesPath;
        }
        finally { _applyGate.Release(); }
    }

    public string? LedgerLoadError => (_ledger as JsonInstallLedger)?.LoadError;
    public IReadOnlyList<string> Warnings => _scanWarnings.Concat(_historyWarnings).Distinct(StringComparer.Ordinal).ToArray();

    public async Task<IReadOnlyList<DiscoveredGame>> GetGamesAsync(bool refresh, CancellationToken cancellationToken = default)
    {
        var games = await _library.GetGamesAsync(refresh, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return await Task.Run(() =>
        {
            EnsureLedgerStore();
            var warnings = _library.Warnings.ToList();
            try
            {
                var reconciled = new InstallLedgerReconciler(_fs, _ledger).MarkMissingIdentities(games, warnings);
                warnings.AddRange(reconciled.Warnings);
            }
            catch (Exception e) { warnings.Add($"台账核对失败，未根据扫描结果更改状态：{e.Message}"); }
            _scanWarnings = warnings;
            return games;
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task AddManualFolderAsync(string title, string folder, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_fs.Directory.Exists(folder)) throw new DirectoryNotFoundException($"目录不存在：{folder}");
        _manualFolders.Add(title, folder);
    }, cancellationToken);

    public Task RemoveManualFolderAsync(string folder, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        _manualFolders.Remove(folder);
    }, cancellationToken);

    public async Task<PlanBuildResult> BuildPlanAsync(PlanBuildOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return await Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var executablePath = _fs.Path.GetFullPath(options.ExecutablePath);
            var deployDirectory = _fs.Path.GetDirectoryName(executablePath)
                ?? throw new ArgumentException("无法解析游戏可执行文件目录。");
            var installRoot = _fs.Path.GetFullPath(options.Game.InstallRoot);
            EnsureWithin(installRoot, deployDirectory);
            var executableName = _fs.Path.GetFileName(executablePath);
            var target = new GameTarget(options.Game.Title, options.Game.Platform, options.Game.PackageFamilyName,
                options.Game.AppId, installRoot, deployDirectory, executableName,
                _fingerprints.Compute(options.Game.Platform, options.Game.PackageFamilyName, installRoot, executableName));
            var inspection = await _inspector.InspectAsync(target, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var payload = await ResolvePayloadAsync(options.PayloadDirectory, cancellationToken).ConfigureAwait(false);
            var plan = _planner.Plan(new PlanRequest
            {
                Game = target,
                Inspection = inspection.Summary,
                Payload = payload,
                Profile = options.Profile,
                Writability = inspection.Writability,
                BackupRoot = options.BackupRoot,
                RecommendedInputEdits = inspection.RecommendedInputEdits,
                InspectionWarnings = inspection.Warnings,
                EnableFrameGen = options.EnableFrameGen,
                EnableLogging = options.EnableLogging,
                AllowOverwriteGameFile = options.AllowOverwriteGameFile,
                PreferAsiIdentity = options.PreferAsiIdentity,
            });
            _planBackupRoots[plan.PlanId] = _fs.Path.GetFullPath(options.BackupRoot);
            return new PlanBuildResult(target, inspection, plan);
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task<ApplyWorkflowResult> ApplyAsync(DeploymentPlan plan, bool reviewAcknowledged, CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        ArgumentNullException.ThrowIfNull(plan);
        var manifestPath = ManifestPath(plan);
        if (!_applyGate.Wait(0)) return new ApplyWorkflowResult(false, false, false, manifestPath, Array.Empty<string>(), "已有部署操作正在执行。", null);
        try
        {
            EnsureLedgerStore();
            ValidateApply(plan, reviewAcknowledged);
            _ = _ledger.All(); // A corrupt index must stop before the executor touches the game.
            var backupRoot = _planBackupRoots.TryGetValue(plan.PlanId, out var knownRoot)
                ? knownRoot
                : InferBackupRoot(plan);
            var writable = _probe(plan.Game.DeployDirectory);
            DeploymentResult result;
            if (writable == DirectoryWritability.NeedsElevation && plan.Elevation == ElevationRequirement.Required)
            {
                var job = ElevationJobStore.Create(plan, backupRoot, _options.JobsRoot);
                var elevated = await _jobs.ExecuteAsync(job, cancellationToken).ConfigureAwait(false);
                result = new DeploymentResult(elevated.Success, elevated.ManifestPath ?? manifestPath, elevated.CompletedSteps, elevated.Error, false);
                if (elevated.Success || elevated.Status == "Declined")
                { try { ElevationJobStore.Cleanup(job); } catch { /* Retained jobs remain diagnosable. */ } }
                if (elevated.Status is "Declined" or "Cancelled") return new ApplyWorkflowResult(false, true, false, result.ManifestPath, result.CompletedSteps,
                    elevated.Status == "Declined" ? "已取消管理员授权，游戏未部署。" : "已取消部署；请检查清单和恢复状态。", elevated.Error);
            }
            else if (writable == DirectoryWritability.Writable)
                result = CreateExecutor(backupRoot).Execute(plan, cancellationToken);
            else throw new InvalidOperationException("写入权限已变化；请重新生成计划并检查后执行。");
            if (!result.Success)
            {
                return new ApplyWorkflowResult(false, false, false, result.ManifestPath, result.CompletedSteps,
                    result.ErrorMessage, result.ErrorMessage);
            }

            try
            {
                _ledger.Save(new InstallRecord
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
                return new ApplyWorkflowResult(true, false, true, result.ManifestPath, result.CompletedSteps, null, null);
            }
            catch (Exception e)
            {
                return new ApplyWorkflowResult(true, false, false, result.ManifestPath, result.CompletedSteps,
                    $"游戏文件已部署，但台账保存失败；部署清单保留在 {result.ManifestPath}。", e.ToString());
            }
        }
        catch (OperationCanceledException e)
        {
            return new ApplyWorkflowResult(false, true, false, manifestPath, Array.Empty<string>(),
                $"操作已取消。部署核心在已开始写入时尝试了恢复；请检查清单并可手动回滚：{manifestPath}", e.ToString());
        }
        catch (Exception e)
        {
            return new ApplyWorkflowResult(false, false, false, manifestPath, Array.Empty<string>(), e.Message, e.ToString());
        }
        finally { _applyGate.Release(); }
    }, CancellationToken.None);

    public async Task<HealthCheckResult> CheckHealthAsync(DeploymentPlan plan, string expectedBackend, bool launchGame, CancellationToken cancellationToken = default)
    {
        if (!await _applyGate.WaitAsync(0, cancellationToken)) throw new InvalidOperationException("已有部署、回滚或健康检查正在执行。");
        try
        {
            EnsureLedgerStore();
            var current = _ledger.Get(plan.Game.Fingerprint);
            if (current?.LatestPlanId != plan.PlanId || current.State is InstallState.RolledBack or InstallState.NeedsReinstall) throw new InvalidOperationException("此部署已过期或已回滚，请重新部署后检查。");
            var log = Path.Combine(plan.Game.DeployDirectory, "OptiScaler.log");
            if (launchGame)
            {
                try { _healthOffsets[plan.PlanId] = _fs.File.Exists(log) ? _fs.FileInfo.New(log).Length : 0; }
                catch { _healthOffsets[plan.PlanId] = 0; }
            }
            if (!_healthOffsets.TryGetValue(plan.PlanId, out var offset)) throw new InvalidOperationException("请先明确启动游戏，再继续等待日志。");
            var result = await _health.RunAsync(new HealthCheckRequest
            {
                Game = plan.Game, ExpectedVersion = plan.Payload.Version.TrimStart('v'), ExpectedBackend = expectedBackend,
                LaunchGame = launchGame, LogStartOffset = offset,
            }, cancellationToken).ConfigureAwait(false);
            _healthOffsets[plan.PlanId] = result.LogStartOffset;
            if (result.Healthy)
            {
                try { if (_ledger.Get(plan.Game.Fingerprint)?.LatestPlanId == plan.PlanId) _ledger.SetState(plan.Game.Fingerprint, InstallState.Healthy); }
                catch (Exception e) { return result with { ErrorMessage = "健康检查通过，但台账更新失败：" + e.Message }; }
            }
            return result;
        }
        finally { _applyGate.Release(); }
    }

    public Task<RollbackWorkflowResult> RollbackAsync(string manifestPath, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        var fullManifestPath = _fs.Path.GetFullPath(manifestPath);
        if (!_applyGate.Wait(0)) return new RollbackWorkflowResult(false, false, fullManifestPath, "已有部署或回滚操作正在执行。", null);
        try
        {
            EnsureLedgerStore();
            cancellationToken.ThrowIfCancellationRequested();
            var root = _fs.Path.GetDirectoryName(_fs.Path.GetDirectoryName(_fs.Path.GetDirectoryName(fullManifestPath))!);
            if (string.IsNullOrWhiteSpace(root)) throw new InvalidDataException("无法从清单路径确定备份根目录。");
            var backups = new BackupStore(_fs, root);
            var recoveryManifest = backups.ReadManifest(fullManifestPath);
            if (recoveryManifest is not null && _probe(recoveryManifest.DeployDirectory) == DirectoryWritability.NeedsElevation)
                return new RollbackWorkflowResult(false, false, fullManifestPath,
                    "此目录恢复需要管理员权限；桌面回滚尚未执行。",
                    $"在管理员终端运行 CLI：rollback --manifest \"{fullManifestPath}\" --backup-root \"{root}\" --ledger \"{_ledgerPath}\"。先检查清单与备份，按时间从新到旧恢复。");
            var rollback = new RollbackExecutor(_fs, new AtomicFileWriter(_fs), backups).Rollback(fullManifestPath);
            if (!rollback.Success)
                return new RollbackWorkflowResult(false, false, fullManifestPath, rollback.ErrorMessage, rollback.ErrorMessage);

            try
            {
                var manifest = backups.ReadManifest(fullManifestPath);
                if (manifest is null) return new RollbackWorkflowResult(true, false, fullManifestPath, null, "回滚完成。清单读取不到，台账状态未变。");
                var current = _ledger.Get(manifest.Fingerprint);
                var updated = current?.LatestPlanId == manifest.PlanId
                    && _ledger.SetState(manifest.Fingerprint, InstallState.RolledBack);
                return new RollbackWorkflowResult(true, updated, fullManifestPath, null,
                    updated ? "此清单是最新部署，台账已标记回滚。较早部署需继续按时间从新到旧逐份恢复。"
                        : "回滚完成。此清单不是台账中的最新部署或台账不可读；台账未改动。若仍有较早部署，请继续按时间从新到旧逐份恢复。 ");
            }
            catch (Exception e)
            {
                return new RollbackWorkflowResult(true, false, fullManifestPath,
                    "游戏文件已按清单恢复，但台账未更新。", e.ToString());
            }
        }
        catch (Exception e)
        {
            return new RollbackWorkflowResult(false, false, fullManifestPath, e.Message, e.ToString());
        }
        finally { _applyGate.Release(); }
    }, CancellationToken.None);

    public Task<IReadOnlyList<HistoryItem>> GetHistoryAsync(CancellationToken cancellationToken = default) => Task.Run<IReadOnlyList<HistoryItem>>(() =>
    {
        EnsureLedgerStore();
        var diagnostics = new List<string>();
        var records = new Dictionary<string, InstallRecord>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var record in _ledger.All()) records[record.Fingerprint] = record;
        }
        catch (Exception e) { diagnostics.Add($"台账不可读，仍可按清单手动回滚：{e.Message}"); }
        cancellationToken.ThrowIfCancellationRequested();
        var rows = new List<HistoryItem>();
        var roots = records.Values.Select(r => r.BackupRoot).Append(_backupRoot)
            .Select(path => { try { return _fs.Path.GetFullPath(path); } catch { return string.Empty; } })
            .Where(path => !string.IsNullOrEmpty(path)).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            if (!_fs.Directory.Exists(root)) continue;
            string[] fingerprints;
            try { fingerprints = _fs.Directory.GetDirectories(root); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                diagnostics.Add($"无法读取备份根目录 {root}：{e.Message}"); continue;
            }
            foreach (var fingerprintDirectory in fingerprints)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fingerprint = _fs.Path.GetFileName(fingerprintDirectory);
                if (fingerprint.Length != 8 || fingerprint.Any(ch => !Uri.IsHexDigit(ch))) continue;
                string[] planDirectories;
                try { planDirectories = _fs.Directory.GetDirectories(fingerprintDirectory); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                { diagnostics.Add($"无法读取历史目录 {fingerprintDirectory}：{e.Message}"); continue; }
                foreach (var planDirectory in planDirectories)
                {
                    var planName = _fs.Path.GetFileName(planDirectory);
                    if (!Guid.TryParseExact(planName, "D", out _)) continue;
                    var manifestPath = _fs.Path.Combine(planDirectory, "manifest.json");
                    if (!_fs.File.Exists(manifestPath)) continue;
                    try
                    {
                        var manifest = new BackupStore(_fs, root).ReadManifest(manifestPath);
                        if (manifest is null) { diagnostics.Add($"清单为空：{manifestPath}"); continue; }
                        records.TryGetValue(manifest.Fingerprint, out var record);
                        rows.Add(new HistoryItem(manifest, _fs.Path.GetFullPath(manifestPath),
                            record?.LatestPlanId == manifest.PlanId ? record.State : null,
                            record?.LatestPlanId == manifest.PlanId));
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
                    { diagnostics.Add($"无法读取历史清单 {manifestPath}：{e.Message}"); }
                }
            }
        }
        _historyWarnings = diagnostics;
        return rows.OrderByDescending(x => x.Manifest.CreatedUtc).ToArray();
    }, cancellationToken);

    public Task<ProfileCatalog> GetProfilesAsync(CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        EnsureProfileStore();
        cancellationToken.ThrowIfCancellationRequested();
        var profiles = _profiles!;
        return new ProfileCatalog(profiles.All(), profiles.LoadError);
    }, cancellationToken);

    public Task SaveProfileAsync(UpscalerProfile profile, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        EnsureProfileStore();
        cancellationToken.ThrowIfCancellationRequested();
        var profiles = _profiles!;
        if (profiles.LoadError is not null) throw new InvalidDataException($"用户配置损坏，保存已禁用：{profiles.LoadError}");
        if (profile.IsBuiltin || BuiltinProfiles.All.Any(x => string.Equals(x.Id, profile.Id, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("内置 Profile 不可覆盖；请克隆后使用新的 ID 保存为用户档。");
        profiles.Save(profile);
    }, cancellationToken);

    public Task<bool> DeleteProfileAsync(string id, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        EnsureProfileStore();
        cancellationToken.ThrowIfCancellationRequested();
        var profiles = _profiles!;
        if (profiles.LoadError is not null) throw new InvalidDataException($"用户配置损坏，删除已禁用：{profiles.LoadError}");
        if (profiles.Find(id)?.IsBuiltin == true) return false;
        return profiles.Delete(id);
    }, cancellationToken);

    private async Task<PayloadBundle> ResolvePayloadAsync(string? payloadDirectory, CancellationToken ct)
    {
        var sources = new List<IPayloadSource>
        {
            new LocalBuildPayloadSource(_options.RepositoryRoot, _fs, _versions),
        };
        payloadDirectory = string.IsNullOrWhiteSpace(payloadDirectory) ? _options.PayloadDirectory : payloadDirectory;
        if (!string.IsNullOrWhiteSpace(payloadDirectory))
            return await new ManualDirectoryPayloadSource(payloadDirectory, _fs, _versions).TryGetAsync(ct).ConfigureAwait(false)
                ?? throw new FileNotFoundException($"所选 payload 目录缺少 OptiScaler.dll 或 OptiScaler.ini：{payloadDirectory}");
        sources.Add(new GitHubReleasePayloadSource(ReleaseHttp, _options.PayloadCacheRoot, _options.ReleaseChannel, versions: _versions));
        return await new PayloadSourceChain(sources).GetOrThrowAsync(ct).ConfigureAwait(false);
    }

    private DeploymentExecutor CreateExecutor(string backupRoot)
    {
        var backups = new BackupStore(_fs, backupRoot);
        var writer = new AtomicFileWriter(_fs);
        return new DeploymentExecutor(_fs, writer, backups, new RollbackExecutor(_fs, writer, backups));
    }

    private void EnsureLedgerStore()
    {
        if (!_activeLedgerPath.Equals(_ledgerPath, StringComparison.OrdinalIgnoreCase))
        {
            _ledger = new JsonInstallLedger(_fs, _ledgerPath);
            _activeLedgerPath = _ledgerPath;
        }
    }

    private void EnsureProfileStore()
    {
        if (_profiles is null || !_activeProfilesPath.Equals(_profilesPath, StringComparison.OrdinalIgnoreCase))
        {
            _profiles = new JsonProfileStore(_fs, _profilesPath);
            _activeProfilesPath = _profilesPath;
        }
    }

    private static void ValidateApply(DeploymentPlan plan, bool reviewed)
    {
        if (!reviewed) throw new InvalidOperationException("必须先检查计划并明确确认后才能部署。");
        if (plan.HasBlockingWarning || plan.Identity.Status != IdentityStatus.Resolved
            || !((plan.Writability == DirectoryWritability.Writable && plan.Elevation == ElevationRequirement.NotRequired)
                || (plan.Writability == DirectoryWritability.NeedsElevation && plan.Elevation == ElevationRequirement.Required)))
            throw new InvalidOperationException("计划包含阻塞项、身份未解决或部署目录并非可写状态，不能部署。");
        if (plan.Writes.Count == 0) throw new InvalidOperationException("计划没有文件写入。");
    }

    private string ManifestPath(DeploymentPlan plan)
    {
        var root = _planBackupRoots.TryGetValue(plan.PlanId, out var known) ? known : InferBackupRoot(plan);
        return new BackupStore(_fs, root).ManifestPathFor(plan.Game.Fingerprint, plan.PlanId);
    }

    private string InferBackupRoot(DeploymentPlan plan)
    {
        var entry = plan.Backups.FirstOrDefault();
        if (entry is not null)
        {
            var planDir = _fs.Path.GetDirectoryName(entry.BackupPath)!;
            var fingerprintDir = _fs.Path.GetDirectoryName(planDir)!;
            return _fs.Path.GetDirectoryName(fingerprintDir)!;
        }
        return _backupRoot;
    }

    private static void EnsureWithin(string root, string path)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || Path.IsPathRooted(relative)) throw new InvalidDataException("可执行文件部署目录超出游戏根目录。");
    }
}

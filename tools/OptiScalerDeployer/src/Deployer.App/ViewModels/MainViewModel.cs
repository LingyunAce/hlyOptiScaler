using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Deployer.App.Commands;
using Deployer.App.Models;
using Deployer.App.Services;
using Deployer.Core.Domain;
using Deployer.Core.HealthCheck;

namespace Deployer.App.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly IDeployerAppService _service;
    private readonly List<GameListItem> _allGames = [];
    private CancellationTokenSource? _operationCts;
    private bool _isBusy, _closeRequested, _enableFrameGen, _reviewAcknowledged, _allowOverwrite, _preferAsi;
    private bool _operationCancellable = true;
    private long _revision;
    private string _filterText = "", _platformFilter = "全部", _backupRoot, _ledgerPath, _profilesPath, _progressMessage = "", _profileDisplayName = "", _profileDescription = "";
    private string? _payloadDirectory, _errorMessage, _suggestedAction, _diagnosticDetails;
    private string? _profileLoadError;
    private GameListItem? _selectedGame;
    private UpscalerProfile? _selectedProfile;
    private DeploymentPlan? _currentPlan;
    private InspectionReport? _currentInspection;
    private DeploymentPlan? _lastDeployment;
    private PlanBuildOptions? _currentBuildOptions, _lastBuildOptions;
    private string? _lastManifest;
    private HealthCheckResult? _healthResult;
    private string _healthMessage = "部署后可主动启动游戏并检查日志。", _expectedBackend = "";

    public MainViewModel(IDeployerAppService service, DeployerAppOptions? options = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        options ??= new DeployerAppOptions();
        _backupRoot = options.BackupRoot; _ledgerPath = options.LedgerPath;
        _profilesPath = options.ProfilesPath; _payloadDirectory = options.PayloadDirectory;
        _service.ConfigureStoragePaths(_backupRoot, _ledgerPath, _profilesPath);
        RefreshCommand = new AsyncRelayCommand(_ => RefreshAsync(), _ => !IsBusy);
        AddManualFolderCommand = new AsyncRelayCommand(p => p is ManualFolderInput input ? AddManualFolderAsync(input.Title, input.Folder) : Task.CompletedTask, _ => !IsBusy);
        RemoveManualFolderCommand = new AsyncRelayCommand(_ => RemoveSelectedManualFolderAsync(), _ => !IsBusy && SelectedGame?.Platform == GamePlatform.Manual);
        GeneratePlanCommand = new AsyncRelayCommand(_ => GeneratePlanAsync(false, false, true), CanGeneratePlan);
        ResolveOverwriteCommand = new AsyncRelayCommand(_ => GeneratePlanAsync(true, false, true), CanGeneratePlan);
        PreferAsiCommand = new AsyncRelayCommand(_ => GeneratePlanAsync(false, true, true), CanGeneratePlan);
        CancelPlanCommand = new RelayCommand(_ => InvalidatePlan("计划已取消。"), _ => CurrentPlan is not null && !IsBusy);
        SavePlanCommand = new AsyncRelayCommand(SavePlanAsync, _ => CurrentPlan is not null && !IsBusy);
        ExecuteCommand = new AsyncRelayCommand(_ => ExecutePlanAsync(), _ => CanExecutePlan);
        HealthCheckCommand = new AsyncRelayCommand(_ => CheckHealthAsync(true), _ => CanCheckHealth);
        WaitHealthCommand = new AsyncRelayCommand(_ => CheckHealthAsync(false), _ => CanCheckHealth && _healthResult is not null);
        HealthRollbackCommand = new AsyncRelayCommand(_ => RollbackAsync(_lastManifest), _ => !IsBusy && _lastManifest is not null);
        RollbackCommand = new AsyncRelayCommand(RollbackAsync, p => p is string && !IsBusy);
        CancelCommand = new RelayCommand(_ => _operationCts?.Cancel(), _ => IsBusy && _operationCts is not null && _operationCancellable);
        ReloadHistoryCommand = new AsyncRelayCommand(_ => ReloadHistoryAsync(), _ => !IsBusy);
        SaveProfileCommand = new AsyncRelayCommand(p => p is UpscalerProfile profile ? SaveProfileAsync(profile) : SaveSelectedProfileAsync(), _ => !IsBusy && SelectedProfile is not null && ProfileLoadError is null);
        DeleteProfileCommand = new AsyncRelayCommand(p => p is string id ? DeleteProfileAsync(id) : DeleteSelectedProfileAsync(), _ => !IsBusy && SelectedProfile is { IsBuiltin: false });
        CloneProfileCommand = new RelayCommand(_ => CloneSelectedProfile(), _ => !IsBusy && SelectedProfile is not null);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? CloseRequested;
    public ObservableCollection<GameListItem> Games { get; } = [];
    public ObservableCollection<UpscalerProfile> Profiles { get; } = [];
    public ObservableCollection<HistoryItem> History { get; } = [];
    public ObservableCollection<ProfileEditRow> ProfileEdits { get; } = [];
    public ObservableCollection<string> ServiceWarnings { get; } = [];
    public IReadOnlyList<string> PlatformFilters { get; } = new[] { "全部", "Xbox", "Steam", "Epic", "Manual" };
    public RelayCommand CancelPlanCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand CloneProfileCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand AddManualFolderCommand { get; }
    public AsyncRelayCommand RemoveManualFolderCommand { get; }
    public AsyncRelayCommand GeneratePlanCommand { get; }
    public AsyncRelayCommand ResolveOverwriteCommand { get; }
    public AsyncRelayCommand PreferAsiCommand { get; }
    public AsyncRelayCommand SavePlanCommand { get; }
    public AsyncRelayCommand ExecuteCommand { get; }
    public AsyncRelayCommand HealthCheckCommand { get; }
    public AsyncRelayCommand WaitHealthCommand { get; }
    public AsyncRelayCommand HealthRollbackCommand { get; }
    public bool CanCheckHealth => !IsBusy && _lastDeployment is not null
        && !_lastDeployment.IniEdits.Any(e => e.Section.Equals("Log", StringComparison.OrdinalIgnoreCase) && e.Key.Equals("LogToFile", StringComparison.OrdinalIgnoreCase) && e.NewValue == "false");
    public bool CanDisableLogging => !IsBusy && _healthResult?.Healthy == true && _lastDeployment is not null && _lastBuildOptions is not null;
    public string RecoveryManifest => _lastManifest ?? "";
    public string HealthMessage { get => _healthMessage; private set => Set(ref _healthMessage, value); }
    public string ExpectedBackend { get => _expectedBackend; set { if (!IsBusy) Set(ref _expectedBackend, value ?? ""); } }
    public string HealthLog => _healthResult?.LogExcerpt ?? "";
    public string HealthTargetText => _lastDeployment is null ? "尚无本次部署" : _lastDeployment.Game.Title + " · " + _lastDeployment.Game.DeployDirectory;
    public AsyncRelayCommand RollbackCommand { get; }
    public AsyncRelayCommand ReloadHistoryCommand { get; }
    public AsyncRelayCommand SaveProfileCommand { get; }
    public AsyncRelayCommand DeleteProfileCommand { get; }

    public GameListItem? SelectedGame
    {
        get => _selectedGame;
        set
        {
            var previous = _selectedGame;
            if (!Set(ref _selectedGame, value)) return;
            if (previous is not null) previous.PropertyChanged -= OnSelectedGamePropertyChanged;
            if (value is not null) value.PropertyChanged += OnSelectedGamePropertyChanged;
            InvalidatePlan(); OnPropertyChanged(nameof(SelectedExecutable)); RaiseCommandStates();
        }
    }
    public string? SelectedExecutable { get => SelectedGame?.SelectedExecutable; set { if (SelectedGame is not null) SelectedGame.SelectedExecutable = value; } }
    public UpscalerProfile? SelectedProfile
    {
        get => _selectedProfile;
        set { if (!Set(ref _selectedProfile, value)) return; ProfileEdits.Clear(); if (value is not null) foreach (var edit in value.Edits) ProfileEdits.Add(new ProfileEditRow(edit)); ProfileDisplayName = value?.DisplayName ?? ""; ProfileDescription = value?.Description ?? ""; InvalidatePlan(); RaiseCommandStates(); }
    }
    public bool EnableFrameGen { get => _enableFrameGen; set { if (Set(ref _enableFrameGen, value)) InvalidatePlan(); } }
    public bool PlanReviewAcknowledged { get => _reviewAcknowledged; set { if (Set(ref _reviewAcknowledged, value)) RaiseCommandStates(); } }
    public bool AllowOverwriteGameFile { get => _allowOverwrite; set { if (Set(ref _allowOverwrite, value)) InvalidatePlan(); } }
    public bool PreferAsiIdentity { get => _preferAsi; set { if (Set(ref _preferAsi, value)) InvalidatePlan(); } }
    public string FilterText { get => _filterText; set { if (Set(ref _filterText, value ?? "")) RebuildVisibleGames(); } }
    public string PlatformFilter { get => _platformFilter; set { if (Set(ref _platformFilter, value ?? "全部")) RebuildVisibleGames(); } }
    public string ProfileDisplayName { get => _profileDisplayName; set => Set(ref _profileDisplayName, value ?? ""); }
    public string ProfileDescription { get => _profileDescription; set => Set(ref _profileDescription, value ?? ""); }
    public bool IsBusy { get => _isBusy; private set { if (Set(ref _isBusy, value)) RaiseCommandStates(); } }
    public bool CanClose => !IsBusy;
    public bool IsIndeterminate => IsBusy;
    public string ProgressMessage { get => _progressMessage; private set => Set(ref _progressMessage, value); }
    public string? ErrorMessage { get => _errorMessage; private set => Set(ref _errorMessage, value); }
    public string? SuggestedAction { get => _suggestedAction; private set => Set(ref _suggestedAction, value); }
    public string? DiagnosticDetails { get => _diagnosticDetails; private set => Set(ref _diagnosticDetails, value); }
    public string? ProfileLoadError { get => _profileLoadError; private set { if (Set(ref _profileLoadError, value)) SaveProfileCommand.RaiseCanExecuteChanged(); } }
    public DeploymentPlan? CurrentPlan { get => _currentPlan; private set { if (Set(ref _currentPlan, value)) { OnPropertyChanged(nameof(PlanWarnings)); OnPropertyChanged(nameof(PlanPayloadText)); OnPropertyChanged(nameof(PlanIdentityText)); OnPropertyChanged(nameof(PlanDestination)); OnPropertyChanged(nameof(WriteCount)); OnPropertyChanged(nameof(BackupCount)); OnPropertyChanged(nameof(IniEditCount)); RaiseCommandStates(); } } }
    public InspectionReport? CurrentInspection { get => _currentInspection; private set { if (Set(ref _currentInspection, value)) { OnPropertyChanged(nameof(GraphicsApisText)); OnPropertyChanged(nameof(DetectedSdksText)); } } }
    public IReadOnlyList<PlanWarning> PlanWarnings => CurrentPlan?.Warnings ?? Array.Empty<PlanWarning>();
    public string GraphicsApisText => CurrentInspection is null ? "未体检" : string.Join("、", CurrentInspection.Summary.GraphicsApis);
    public string DetectedSdksText => CurrentInspection is null ? "未体检" : string.Join("、", CurrentInspection.Summary.DetectedSdks);
    public string PlanPayloadText => CurrentPlan is null ? "" : $"{CurrentPlan.Payload.Version}（{CurrentPlan.Payload.Origin}）";
    public string PlanIdentityText => CurrentPlan is null ? "" : CurrentPlan.Identity.UserPromptMessage ?? CurrentPlan.Identity.FileName;
    public string PlanDestination => CurrentPlan?.Game.DeployDirectory ?? "";
    public int WriteCount => CurrentPlan?.Writes.Count ?? 0;
    public int BackupCount => CurrentPlan?.Backups.Count ?? 0;
    public int IniEditCount => CurrentPlan?.IniEdits.Count ?? 0;
    public string BackupRoot { get => _backupRoot; set => UpdateStoragePath(ref _backupRoot, value ?? "", nameof(BackupRoot)); }
    public string LedgerPath { get => _ledgerPath; set => UpdateStoragePath(ref _ledgerPath, value ?? "", nameof(LedgerPath)); }
    public string ProfilesPath { get => _profilesPath; set => UpdateStoragePath(ref _profilesPath, value ?? "", nameof(ProfilesPath)); }
    public string? PayloadDirectory { get => _payloadDirectory; set { if (!IsBusy && Set(ref _payloadDirectory, value)) InvalidatePlan(); } }
    public bool CanExecutePlan => !IsBusy && CurrentPlan is not null && PlanReviewAcknowledged
        && !CurrentPlan.HasBlockingWarning && CurrentPlan.Identity.Status == IdentityStatus.Resolved
        && ((CurrentPlan.Writability == DirectoryWritability.Writable && CurrentPlan.Elevation == ElevationRequirement.NotRequired)
            || (CurrentPlan.Writability == DirectoryWritability.NeedsElevation && CurrentPlan.Elevation == ElevationRequirement.Required));

    public string ExecuteBlockReason
    {
        get
        {
            if (IsBusy) return "当前正在处理其他操作。";
            if (CurrentPlan is null) return "请先生成 / 更新计划。";
            if (!PlanReviewAcknowledged) return "请先勾选“我已检查上方全部目标路径、备份、INI 变更和警告”确认框。";
            var blocking = CurrentPlan.Warnings.Where(w => w.Severity == WarningSeverity.Blocking).Select(w => w.Message).ToArray();
            if (blocking.Length > 0) return "计划存在阻塞项：" + string.Join("；", blocking);
            if (CurrentPlan.Identity.Status != IdentityStatus.Resolved) return "身份 DLL 尚未解决，请处理身份冲突后重新生成计划。";
            if (CurrentPlan.Writability == DirectoryWritability.Unknown) return "目标目录写入权限未知，请重新扫描并生成计划。";
            if (CurrentPlan.Writability == DirectoryWritability.Blocked || CurrentPlan.Elevation == ElevationRequirement.Blocked) return "目标目录不可写，当前计划不能执行。";
            if (CurrentPlan.Writability == DirectoryWritability.NeedsElevation && CurrentPlan.Elevation != ElevationRequirement.Required) return "目标目录需要管理员权限，请重新生成计划。";
            if (CurrentPlan.Writability == DirectoryWritability.Writable && CurrentPlan.Elevation != ElevationRequirement.NotRequired) return "目标目录权限状态已变化，请重新生成计划。";
            return string.Empty;
        }
    }

    public async Task InitializeAsync() { await LoadProfilesAsync(); await ReloadHistoryAsync(); if (ProfileLoadError is not null) ReportFailure(ProfileLoadError, "修复或备份损坏的 Profile 文件后再保存用户档。", ProfileLoadError); }
    public async Task UpdateStoragePathsAsync(string backupRoot, string ledgerPath, string profilesPath)
    {
        if (IsBusy) return;
        var previous = (_backupRoot, _ledgerPath, _profilesPath);
        try
        {
            _service.ConfigureStoragePaths(backupRoot, ledgerPath, profilesPath);
            _backupRoot = backupRoot; _ledgerPath = ledgerPath; _profilesPath = profilesPath;
            OnPropertyChanged(nameof(BackupRoot)); OnPropertyChanged(nameof(LedgerPath)); OnPropertyChanged(nameof(ProfilesPath));
            InvalidatePlan();
            await ReloadStorageStateAsync();
        }
        catch (Exception e)
        {
            _backupRoot = previous._backupRoot; _ledgerPath = previous._ledgerPath; _profilesPath = previous._profilesPath;
            OnPropertyChanged(nameof(BackupRoot)); OnPropertyChanged(nameof(LedgerPath)); OnPropertyChanged(nameof(ProfilesPath));
            try { _service.ConfigureStoragePaths(_backupRoot, _ledgerPath, _profilesPath); } catch { }
            ReportFailure(e.Message, "检查存储路径；原设置已恢复。", e.ToString());
        }
    }
    public async Task AddManualFolderAsync(string title, string folder) => await RunOperationAsync("保存手动目录并刷新列表…", async ct => { await _service.AddManualFolderAsync(title, folder, ct); await RefreshCoreAsync(true, ct); });
    public void RequestClose() { _closeRequested = true; if (IsBusy) { if (_operationCancellable) _operationCts?.Cancel(); } else CloseRequested?.Invoke(this, EventArgs.Empty); }

    private async Task RefreshAsync() => await RunOperationAsync("正在扫描游戏…", ct => RefreshCoreAsync(true, ct));
    private async Task RefreshCoreAsync(bool refresh, CancellationToken ct)
    {
        var games = await _service.GetGamesAsync(refresh, ct); ct.ThrowIfCancellationRequested();
        _allGames.Clear(); foreach (var game in games) _allGames.Add(new GameListItem(game));
        ServiceWarnings.Clear(); foreach (var warning in _service.Warnings) ServiceWarnings.Add(warning);
        RebuildVisibleGames(); if (SelectedGame is not null && !Games.Contains(SelectedGame)) SelectedGame = null;
    }
    private void RebuildVisibleGames()
    {
        var selected = SelectedGame; Games.Clear();
        foreach (var item in _allGames.Where(g => (string.IsNullOrWhiteSpace(FilterText) || g.Title.Contains(FilterText, StringComparison.OrdinalIgnoreCase) || g.InstallRoot.Contains(FilterText, StringComparison.OrdinalIgnoreCase)) && (PlatformFilter == "全部" || g.Platform.ToString().Equals(PlatformFilter, StringComparison.OrdinalIgnoreCase)))) Games.Add(item);
        if (selected is not null && Games.Contains(selected)) SelectedGame = selected;
        else if (selected is not null) SelectedGame = null;
    }
    private async Task RemoveSelectedManualFolderAsync()
    {
        var game = SelectedGame; if (game?.Platform != GamePlatform.Manual) return;
        await RunOperationAsync("移除手动目录并刷新列表…", async ct => { await _service.RemoveManualFolderAsync(game.InstallRoot, ct); await RefreshCoreAsync(true, ct); });
    }
    private bool CanGeneratePlan(object? _) => !IsBusy && SelectedGame is not null && SelectedProfile is not null && !string.IsNullOrWhiteSpace(SelectedGame.SelectedExecutable);
    private async Task GeneratePlanAsync(bool overwrite, bool asi, bool enableLogging)
    {
        if (!CanGeneratePlan(null) || SelectedGame is null || SelectedProfile is null) return;
        var game = SelectedGame; var profile = SelectedProfile; var exe = game.SelectedExecutable!; var revision = _revision;
        await RunOperationAsync("正在体检游戏并生成部署计划…", async ct =>
        {
            var options = new PlanBuildOptions(game.Game, exe, profile, BackupRoot, EnableFrameGen, overwrite, asi, PayloadDirectory, enableLogging);
            var result = await _service.BuildPlanAsync(options, ct);
            ct.ThrowIfCancellationRequested(); if (revision != _revision) return; CurrentInspection = result.Inspection; CurrentPlan = result.Plan; PlanReviewAcknowledged = false;
            _currentBuildOptions = options;
            _allowOverwrite = overwrite; _preferAsi = asi; OnPropertyChanged(nameof(AllowOverwriteGameFile)); OnPropertyChanged(nameof(PreferAsiIdentity));
            ProgressMessage = result.Plan.Identity.Status == IdentityStatus.RequiresUserDecision ? "身份 DLL 存在冲突，请选择覆盖并备份、改用 ASI，或取消计划。" : "计划已生成。请检查文件、备份、INI 修改与警告，并明确确认。";
        });
    }
    public async Task DisableLoggingAsync()
    {
        if (!CanDisableLogging || _lastBuildOptions is null || _lastDeployment is null) return;
        var original = _lastDeployment;
        var options = _lastBuildOptions with
        {
            PayloadDirectory = original.Payload.RootDirectory,
            Profile = original.Profile with { Edits = original.IniEdits.Where(e => !e.Section.Equals("Log", StringComparison.OrdinalIgnoreCase) && !e.Section.Equals("ProcessFilter", StringComparison.OrdinalIgnoreCase)).ToArray() },
            EnableFrameGen = false, EnableLogging = false,
        };
        await RunOperationAsync("正在生成本次部署的关闭日志计划…", async ct =>
        {
            InvalidatePlan();
            var result = await _service.BuildPlanAsync(options, ct); ct.ThrowIfCancellationRequested();
            CurrentInspection = result.Inspection; CurrentPlan = result.Plan; _currentBuildOptions = options; PlanReviewAcknowledged = false;
            ProgressMessage = "已为本次部署生成关闭日志计划，请检查目标路径和 INI 变更，重新确认后执行。";
        });
    }
    private async Task ExecutePlanAsync()
    {
        var plan = CurrentPlan; if (plan is null || !CanExecutePlan) return;
        var reviewed = PlanReviewAcknowledged;
        await RunOperationAsync("正在部署…", async ct =>
        {
            try
            {
                var result = await _service.ApplyAsync(plan, reviewed, ct);
                if (!result.Success && !string.IsNullOrWhiteSpace(result.ManifestPath))
                { _lastManifest = result.ManifestPath; _lastDeployment = null; _lastBuildOptions = null; _healthResult = null; HealthMessage = "部署未完成。已保留清单路径，请检查备份后恢复。"; OnPropertyChanged(nameof(RecoveryManifest)); OnPropertyChanged(nameof(HealthTargetText)); }
                if (result.Success)
                {
                    _lastDeployment = plan; _lastBuildOptions = _currentBuildOptions; _lastManifest = result.ManifestPath; _healthResult = null;
                    OnPropertyChanged(nameof(RecoveryManifest));
                    var apiKey = CurrentInspection?.Summary.GraphicsApis.Contains(GraphicsApi.Dx12) == true ? "Dx12Upscaler"
                        : CurrentInspection?.Summary.GraphicsApis.Contains(GraphicsApi.Dx11) == true ? "Dx11Upscaler" : "VulkanUpscaler";
                    var backend = plan.IniEdits.FirstOrDefault(e => e.Section == "Upscalers" && e.Key == apiKey)?.NewValue ?? "";
                    _expectedBackend = backend.ToLowerInvariant() switch { "ffx" or "ffx_12" => "FSR 3.X", "fsr21" or "fsr21_12" => "FSR 2.1.2", "fsr22" or "fsr22_12" => "FSR 2.2.1", "fsr31" or "fsr31_12" => "fsr31", "xess" or "xess_12" => "XeSS", "dlss" or "dlss_12" => "DLSS", _ => backend };
                    OnPropertyChanged(nameof(ExpectedBackend)); OnPropertyChanged(nameof(HealthTargetText));
                    HealthMessage = "部署完成。点击启动检查前，请确认预期超分名称；失败或超时可继续等待、查看日志或按清单恢复。";
                    OnPropertyChanged(nameof(HealthLog));
                }
                if (result.Cancelled) ReportFailure(result.ErrorMessage ?? "操作已取消。", RecoveryAction(plan, result.ManifestPath), result.DiagnosticDetails);
                else if (!result.Success) ReportFailure(result.ErrorMessage ?? "部署失败。", RecoveryAction(plan, result.ManifestPath), result.DiagnosticDetails);
                else if (!result.LedgerUpdated) { ReportFailure(result.ErrorMessage ?? "游戏已部署，台账保存失败。", "清单已保留；修复台账路径，不要重复执行同一计划。", result.DiagnosticDetails); ProgressMessage = "游戏已部署；台账保存失败。"; }
                else { ClearError(); ProgressMessage = "部署完成。清单：" + result.ManifestPath; try { await ReloadHistoryCoreAsync(CancellationToken.None); } catch (Exception e) { ReportFailure("部署已完成，但历史刷新失败。", "重新载入历史。", e.ToString()); } }
            }
            finally { InvalidatePlan(); }
        });
    }
    private async Task CheckHealthAsync(bool launch)
    {
        var plan = _lastDeployment; if (plan is null || !CanCheckHealth) return;
        await RunOperationAsync(launch ? "正在启动游戏并等待日志…" : "继续等待游戏日志…", async ct =>
        {
            _healthResult = await _service.CheckHealthAsync(plan, ExpectedBackend, launch, ct);
            HealthMessage = _healthResult.Status switch
            {
                HealthCheckStatus.Healthy => $"健康检查通过：{_healthResult.ActualVersion} / {_healthResult.ActualBackend}",
                HealthCheckStatus.TimedOut => "60 秒内未看到完整启动日志。部署保留，可继续等待、查看日志或手动恢复。",
                HealthCheckStatus.LaunchFailed => "游戏启动失败。部署保留，请查看诊断。",
                _ => "游戏日志报告错误。部署保留，请查看日志或手动恢复。",
            };
            OnPropertyChanged(nameof(HealthLog));
            if (_healthResult.ErrorMessage is not null) ReportFailure(_healthResult.ErrorMessage, "查看日志；超时不会自动回滚。", _healthResult.LogExcerpt);
            ProgressMessage = HealthMessage;
            OnPropertyChanged(nameof(CanDisableLogging));
        });
    }
    private async Task RollbackAsync(object? parameter)
    {
        if (parameter is not string manifest || string.IsNullOrWhiteSpace(manifest)) return;
        await RunOperationAsync("正在按所选清单恢复文件…", async ct =>
        {
            var result = await _service.RollbackAsync(manifest, ct);
            if (!result.Success) ReportFailure(result.ErrorMessage ?? "回滚失败。", "检查清单与备份文件。", result.DiagnosticDetails);
            else
            {
                if (string.Equals(manifest, _lastManifest, StringComparison.OrdinalIgnoreCase))
                { _lastDeployment = null; _lastBuildOptions = null; _lastManifest = null; _healthResult = null; HealthMessage = "部署已恢复。"; OnPropertyChanged(nameof(RecoveryManifest)); OnPropertyChanged(nameof(HealthLog)); OnPropertyChanged(nameof(HealthTargetText)); }
                ClearError(); ProgressMessage = result.DiagnosticDetails ?? "回滚完成。"; if (result.ErrorMessage is not null) ReportFailure(result.ErrorMessage, "游戏已恢复；请检查台账状态。", result.DiagnosticDetails); try { await ReloadHistoryCoreAsync(CancellationToken.None); } catch (Exception e) { ReportFailure("游戏已恢复，但历史刷新失败。", "重新载入历史。", e.ToString()); }
            }
        }, cancellable: false);
    }
    private async Task SavePlanAsync(object? parameter)
    {
        if (parameter is not string path || CurrentPlan is null) return;
        var plan = CurrentPlan;
        await RunOperationAsync("正在保存计划文件…", async ct =>
        {
            var destination = Path.GetFullPath(path);
            var forbiddenRoots = new[] { plan.Game.InstallRoot, plan.Game.DeployDirectory, plan.Payload.RootDirectory, BackupRoot, LedgerPath, ProfilesPath }
                .Where(root => !string.IsNullOrWhiteSpace(root)).Select(Path.GetFullPath).ToArray();
            if (forbiddenRoots.Any(root => IsWithinPath(root, destination))
                || History.Any(item => string.Equals(Path.GetFullPath(item.ManifestPath), destination, StringComparison.OrdinalIgnoreCase))
                || HasReparseAncestor(Path.GetDirectoryName(destination)!))
                throw new UnauthorizedAccessException("计划输出路径位于游戏、payload、备份或配置文件范围内，或包含 reparse point；请选择安全的独立目录。");
            await File.WriteAllTextAsync(destination, JsonSerializer.Serialize(plan, new JsonSerializerOptions { WriteIndented = true }), ct);
            ProgressMessage = "计划已保存：" + destination; ClearError();
        });
    }
    public async Task ReloadHistoryAsync() => await RunOperationAsync("正在读取部署历史…", ReloadHistoryCoreAsync);
    private async Task ReloadHistoryCoreAsync(CancellationToken ct) { var rows = await _service.GetHistoryAsync(ct); ct.ThrowIfCancellationRequested(); History.Clear(); foreach (var row in rows) History.Add(row); SyncWarnings(); }
    private async Task LoadProfilesAsync() => await RunOperationAsync("正在读取 Profile…", LoadProfilesCoreAsync);
    private async Task LoadProfilesCoreAsync(CancellationToken ct)
    {
        var catalog = await _service.GetProfilesAsync(ct); ct.ThrowIfCancellationRequested(); Profiles.Clear(); foreach (var profile in catalog.Profiles) Profiles.Add(profile);
        ProfileLoadError = catalog.LoadError;
        SyncWarnings();
        if (SelectedProfile is null || !Profiles.Any(profile => string.Equals(profile.Id, SelectedProfile.Id, StringComparison.OrdinalIgnoreCase)))
            SelectedProfile = Profiles.FirstOrDefault();
        if (catalog.LoadError is not null) ReportFailure(catalog.LoadError, "修复或备份损坏的 Profile 文件后再保存用户档。", catalog.LoadError);
    }
    private async Task SaveSelectedProfileAsync()
    {
        var current = SelectedProfile; if (current is null || current.IsBuiltin) return;
        await SaveProfileAsync(current with { DisplayName = ProfileDisplayName, Description = ProfileDescription, Edits = ProfileEdits.Select(row => row.ToIniEdit()).ToArray() });
    }
    public async Task SaveProfileAsync(UpscalerProfile profile)
    {
        if (ProfileLoadError is not null) { ReportFailure(ProfileLoadError, "修复损坏的 Profile 文件后再保存。", ProfileLoadError); return; }
        if (profile.IsBuiltin || Deployer.Core.Profiles.BuiltinProfiles.All.Any(x => string.Equals(x.Id, profile.Id, StringComparison.OrdinalIgnoreCase))) { ReportFailure("内置档无法覆盖。", "克隆该档后使用新的 ID 保存。", null); return; }
        await RunOperationAsync("正在保存用户 Profile…", async ct => { await _service.SaveProfileAsync(profile, ct); await LoadProfilesCoreAsync(ct); SelectedProfile = Profiles.FirstOrDefault(x => x.Id == profile.Id); ProgressMessage = "Profile 已保存。"; ClearError(); });
    }
    private async Task DeleteSelectedProfileAsync()
    {
        var current = SelectedProfile; if (current is null || current.IsBuiltin) return;
        await DeleteProfileAsync(current.Id);
    }
    public async Task DeleteProfileAsync(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        await RunOperationAsync("正在删除用户 Profile…", async ct => { await _service.DeleteProfileAsync(id, ct); await LoadProfilesCoreAsync(ct); SelectedProfile = Profiles.FirstOrDefault(); ProgressMessage = "用户 Profile 已删除。"; });
    }
    private void CloneSelectedProfile()
    {
        if (SelectedProfile is not { } source) return;
        var copy = source with { Id = "user-" + Guid.NewGuid().ToString("N"), DisplayName = source.DisplayName + "（副本）", IsBuiltin = false };
        Profiles.Add(copy); SelectedProfile = copy; ProgressMessage = "已创建副本；编辑后保存。";
    }
    private void UpdateStoragePath(ref string field, string value, string propertyName)
    {
        if (IsBusy || field.Equals(value, StringComparison.Ordinal)) return;
        var previous = field; field = value; OnPropertyChanged(propertyName);
        try
        {
            _service.ConfigureStoragePaths(BackupRoot, LedgerPath, ProfilesPath);
            InvalidatePlan();
            _ = ReloadStorageStateAsync();
        }
        catch (Exception e)
        {
            field = previous; OnPropertyChanged(propertyName);
            try { _service.ConfigureStoragePaths(BackupRoot, LedgerPath, ProfilesPath); } catch { }
            ReportFailure(e.Message, "检查存储路径；原设置已保留。", e.ToString());
        }
    }
    private async Task ReloadStorageStateAsync()
    {
        await RunOperationAsync("正在载入新的存储路径…", async ct => { await LoadProfilesCoreAsync(ct); await ReloadHistoryCoreAsync(ct); });
    }
    private void SyncWarnings() { ServiceWarnings.Clear(); foreach (var warning in _service.Warnings) ServiceWarnings.Add(warning); }
    private async Task RunOperationAsync(string message, Func<CancellationToken, Task> operation, bool cancellable = true)
    {
        if (IsBusy) return;
        _operationCts = new CancellationTokenSource(); _operationCancellable = cancellable; IsBusy = true; ProgressMessage = message; ClearError();
        try { await operation(_operationCts.Token); }
        catch (OperationCanceledException) when (_operationCts.IsCancellationRequested) { ProgressMessage = "操作已取消。"; PlanReviewAcknowledged = false; }
        catch (Exception e) { ReportFailure(e.Message, "检查路径、配置和详细诊断后重试。", e.ToString()); ProgressMessage = "操作失败。"; }
        finally { _operationCts.Dispose(); _operationCts = null; _operationCancellable = true; IsBusy = false; if (_closeRequested) CloseRequested?.Invoke(this, EventArgs.Empty); }
    }
    private void InvalidatePlan(string? message = null)
    {
        _revision++;
        CurrentPlan = null; _currentBuildOptions = null; CurrentInspection = null; PlanReviewAcknowledged = false; if (message is not null) ProgressMessage = message; RaiseCommandStates();
    }
    private void OnSelectedGamePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(GameListItem.SelectedExecutable)) return;
        OnPropertyChanged(nameof(SelectedExecutable)); InvalidatePlan(); RaiseCommandStates();
    }
    private void ReportFailure(string message, string? action, string? diagnostic) { ErrorMessage = message; SuggestedAction = action; DiagnosticDetails = diagnostic; }
    private string RecoveryAction(DeploymentPlan plan, string manifest)
        => plan.Elevation == ElevationRequirement.Required
            ? $"桌面回滚保持普通权限；如需恢复，请以管理员身份运行 CLI：rollback --manifest \"{manifest}\" --backup-root \"{BackupRoot}\" --ledger \"{LedgerPath}\"。"
            : "检查清单与备份文件；需要时按本次清单恢复。";
    private static bool IsWithinPath(string root, string path)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return relative == "." || (relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative));
    }
    private static bool HasReparseAncestor(string directory)
    {
        try
        {
            var current = new DirectoryInfo(Path.GetFullPath(directory));
            while (current is not null)
            {
                if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0) return true;
                current = current.Parent;
            }
            return false;
        }
        catch { return true; }
    }
    private void ClearError() { ErrorMessage = null; SuggestedAction = null; DiagnosticDetails = null; }
    private void RaiseCommandStates()
    {
        RefreshCommand.RaiseCanExecuteChanged(); AddManualFolderCommand.RaiseCanExecuteChanged(); RemoveManualFolderCommand.RaiseCanExecuteChanged();
        GeneratePlanCommand.RaiseCanExecuteChanged(); ResolveOverwriteCommand.RaiseCanExecuteChanged(); PreferAsiCommand.RaiseCanExecuteChanged();
        CancelPlanCommand.RaiseCanExecuteChanged(); SavePlanCommand.RaiseCanExecuteChanged(); ExecuteCommand.RaiseCanExecuteChanged();
        RollbackCommand.RaiseCanExecuteChanged(); CancelCommand.RaiseCanExecuteChanged(); ReloadHistoryCommand.RaiseCanExecuteChanged();
        SaveProfileCommand.RaiseCanExecuteChanged(); DeleteProfileCommand.RaiseCanExecuteChanged(); CloneProfileCommand.RaiseCanExecuteChanged();
        HealthCheckCommand.RaiseCanExecuteChanged(); WaitHealthCommand.RaiseCanExecuteChanged(); HealthRollbackCommand.RaiseCanExecuteChanged(); OnPropertyChanged(nameof(CanCheckHealth)); OnPropertyChanged(nameof(CanDisableLogging));
        OnPropertyChanged(nameof(CanExecutePlan)); OnPropertyChanged(nameof(ExecuteBlockReason)); OnPropertyChanged(nameof(CanClose)); OnPropertyChanged(nameof(IsIndeterminate)); OnPropertyChanged(nameof(SelectedExecutable));
        OnPropertyChanged(nameof(PlatformFilter));
    }
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null) { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; OnPropertyChanged(name); return true; }
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed record ManualFolderInput(string Title, string Folder);

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Deployer.App.Models;
using Deployer.App.Services;
using Deployer.App.ViewModels;
using Deployer.Core.Domain;

namespace Deployer.App;

/// <summary>Creates deterministic screenshots without constructing or calling a real app service.</summary>
internal static class PreviewRenderer
{
    public static async Task RenderAsync(string outputPath)
    {
        if (!Path.IsPathFullyQualified(outputPath)) throw new ArgumentException("Preview 输出路径必须是绝对路径。", nameof(outputPath));
        var options = new DeployerAppOptions { RepositoryRoot = Path.GetTempPath(), BackupRoot = Path.Combine(Path.GetTempPath(), "preview-backups"), LedgerPath = Path.Combine(Path.GetTempPath(), "preview-ledger.json"), ProfilesPath = Path.Combine(Path.GetTempPath(), "preview-profiles.json") };
        var sample = SampleData.Create();
        var vm = new MainViewModel(new PreviewService(sample.Game, sample.Profile), options);
        await vm.InitializeAsync();
        var game = new GameListItem(sample.Game) { SelectedExecutable = sample.Game.ExecutableCandidates[0] };
        vm.Games.Add(game);
        vm.SelectedGame = game;
        vm.SelectedProfile = sample.Profile;
        SetPrivate(vm, nameof(MainViewModel.CurrentInspection), sample.Inspection);
        SetPrivate(vm, nameof(MainViewModel.CurrentPlan), sample.Plan);
        typeof(MainViewModel).GetProperty(nameof(MainViewModel.ProgressMessage))!.GetSetMethod(true)!.Invoke(vm, ["计划已生成。请检查文件、备份、INI 修改与警告，并明确确认。"]);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await Render(vm, outputPath, 1320, 860);
        var compact = Path.Combine(Path.GetDirectoryName(outputPath)!, Path.GetFileNameWithoutExtension(outputPath) + "-compact.png");
        await Render(vm, compact, 1020, 650, scrollToEnd: true);
    }

    private static void SetPrivate(MainViewModel vm, string property, object value) =>
        typeof(MainViewModel).GetProperty(property)!.GetSetMethod(true)!.Invoke(vm, [value]);

    private static async Task Render(MainViewModel vm, string path, int width, int height, bool scrollToEnd = false)
    {
        var window = new MainWindow(vm) { Width = width, Height = height, WindowStyle = WindowStyle.None, ShowInTaskbar = false };
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(width, height));
        content.Arrange(new Rect(0, 0, width, height));
        content.UpdateLayout();
        await window.Dispatcher.InvokeAsync(content.UpdateLayout, DispatcherPriority.ContextIdle);
        if (scrollToEnd && window.FindName("PlanScrollViewer") is ScrollViewer scrollViewer) { scrollViewer.ScrollToEnd(); content.UpdateLayout(); await window.Dispatcher.InvokeAsync(content.UpdateLayout, DispatcherPriority.ContextIdle); }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        using var stream = File.Create(path);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(stream);
    }
    private sealed class PreviewService(DiscoveredGame game, UpscalerProfile profile) : IDeployerAppService
    {
        public IReadOnlyList<string> Warnings => Array.Empty<string>();
        public void ConfigureStoragePaths(string backupRoot, string ledgerPath, string profilesPath) { }
        public Task<IReadOnlyList<DiscoveredGame>> GetGamesAsync(bool refresh, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<DiscoveredGame>>([game]);
        public Task AddManualFolderAsync(string title, string folder, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveManualFolderAsync(string folder, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<PlanBuildResult> BuildPlanAsync(PlanBuildOptions options, CancellationToken cancellationToken = default) { var sample = SampleData.Create(); return Task.FromResult(new PlanBuildResult(sample.Plan.Game, sample.Inspection, sample.Plan)); }
        public Task<ApplyWorkflowResult> ApplyAsync(DeploymentPlan plan, bool reviewAcknowledged, CancellationToken cancellationToken = default) => throw new NotSupportedException("Preview never deploys.");
        public Task<Deployer.Core.HealthCheck.HealthCheckResult> CheckHealthAsync(DeploymentPlan plan, string expectedBackend, bool launchGame, CancellationToken cancellationToken = default) => throw new NotSupportedException("Preview never launches a game.");
        public Task<RollbackWorkflowResult> RollbackAsync(string manifestPath, CancellationToken cancellationToken = default) => throw new NotSupportedException("Preview never rolls back.");
        public Task<IReadOnlyList<HistoryItem>> GetHistoryAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<HistoryItem>>(Array.Empty<HistoryItem>());
        public Task<ProfileCatalog> GetProfilesAsync(CancellationToken cancellationToken = default) => Task.FromResult(new ProfileCatalog([profile], null));
        public Task SaveProfileAsync(UpscalerProfile value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> DeleteProfileAsync(string id, CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    private static class SampleData
    {
        public static (DiscoveredGame Game, UpscalerProfile Profile, InspectionReport Inspection, DeploymentPlan Plan) Create()
        {
            const string root = @"C:\Games\Example";
            var game = new DiscoveredGame { Title = "示例游戏", Platform = GamePlatform.Steam, AppId = "123456", InstallRoot = root, ExecutableCandidates = [Path.Combine(root, "Game.exe"), Path.Combine(root, "Binaries", "Win64", "Game-Win64-Shipping.exe")], Writability = DirectoryWritability.Writable, ScannerSource = "预览样例" };
            var profile = new UpscalerProfile { Id = "builtin-native", DisplayName = "原生性能优先", Description = "适用于 DX11、DX12 与 Vulkan 的原生性能优化配置。", IsBuiltin = true, Edits = [new IniEdit("OptiScaler", "LogLevel", null, "2"), new IniEdit("FSR2", "Enable", null, "true")] };
            var summary = new InspectionSummary { GraphicsApis = [GraphicsApi.Dx12], DetectedSdks = [UpscalerSdk.Dlss], HasEngineFolder = true, IsVulkanOnly = false };
            var inspection = new InspectionReport { Summary = summary, Writability = DirectoryWritability.Writable, Warnings = [new PlanWarning(WarningSeverity.Info, "检测到 DLSS；已为 FSR 输入生成配置覆盖。")], RecommendedInputEdits = profile.Edits, IsSupported = true };
            var target = new GameTarget(game.Title, game.Platform, null, game.AppId, root, root, "Game.exe", "ab12cd34");
            var payload = new PayloadInfo(PayloadOrigin.LocalBuild, "预览构建", @"C:\OptiScaler\x64\Release\a");
            var plan = new DeploymentPlan
            {
                PlanId = Guid.Parse("e744b557-f052-4fa0-bdd0-107bd4f9b66b"), Game = target, Payload = payload,
                Identity = new IdentityDecision(DllIdentity.Dxgi, "dxgi.dll", IdentityStatus.Resolved, Array.Empty<DllIdentity>(), null, "已选择 dxgi.dll"),
                Profile = profile, Writability = DirectoryWritability.Writable, Elevation = ElevationRequirement.NotRequired,
                Writes = [new FileWrite(@"C:\OptiScaler\x64\Release\a\OptiScaler.asi", Path.Combine(root, "OptiScaler.asi"), 184320, "a0a0a0a0"), new FileWrite(@"C:\OptiScaler\x64\Release\a\dxgi.dll", Path.Combine(root, "dxgi.dll"), 65536, "b1b1b1b1")],
                Backups = [new BackupEntry(Path.Combine(root, "dxgi.dll"), @"D:\Backups\ab12cd34\plan\dxgi.dll", BackupReason.OverwriteGameFile)],
                IniEdits = [new IniEdit("OptiScaler", "LogLevel", "1", "2"), new IniEdit("FSR2", "Enable", null, "true")],
                Warnings = [new PlanWarning(WarningSeverity.Info, "Detected DLSS SDK; generated FSR input overrides."), new PlanWarning(WarningSeverity.Caution, "计划会在覆盖前备份现有 dxgi.dll。")]
            };
            return (game, profile, inspection, plan);
        }
    }
}











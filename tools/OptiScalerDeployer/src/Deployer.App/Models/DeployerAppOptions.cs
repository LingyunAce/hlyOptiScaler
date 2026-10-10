using System.IO;

namespace Deployer.App.Models;

public sealed record DeployerAppOptions
{
    public string RepositoryRoot { get; init; } = FindRepositoryRoot();
    public string BackupRoot { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OptiScalerDeployer", "backups");
    public string LedgerPath { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OptiScalerDeployer", "ledger.json");
    public string ProfilesPath { get; init; } = Deployer.Core.Profiles.JsonProfileStore.DefaultPath;
    public string ManualFoldersPath { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OptiScalerDeployer", "manual-folders.json");
    public string? PayloadDirectory { get; init; }
    public string ReleaseChannel { get; init; } = "stable";
    public string PayloadCacheRoot { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OptiScalerDeployer", "payload-cache");
    public string JobsRoot { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OptiScalerDeployer", "jobs");
    public string WorkerPath { get; init; } = Path.Combine(AppContext.BaseDirectory, "worker", "Deployer.Worker.exe");

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "OptiScaler.sln"))) return current.FullName;
            current = current.Parent;
        }
        return AppContext.BaseDirectory;
    }
}

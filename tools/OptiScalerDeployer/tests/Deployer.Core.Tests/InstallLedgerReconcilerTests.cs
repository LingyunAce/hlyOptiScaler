using System.IO.Abstractions;
using Deployer.Core.Domain;
using Deployer.Core.Persistence;
using Xunit;

namespace Deployer.Core.Tests;

public sealed class InstallLedgerReconcilerTests
{
    [Fact]
    public void Marks_missing_identity_only_for_confirmed_matching_writable_install()
    {
        using var workspace = new TempDirectory();
        var deploy = Path.Combine(workspace.Root, "game", "Binaries", "Win64");
        Directory.CreateDirectory(deploy);
        File.WriteAllText(Path.Combine(deploy, "Game.exe"), "exe");
        var ledger = new JsonInstallLedger(new FileSystem(), Path.Combine(workspace.Root, "ledger.json"));
        var record = Record("missing01", deploy, InstallState.Installed);
        ledger.Save(record);
        var game = Discovered(deploy, DirectoryWritability.Writable);

        var result = new InstallLedgerReconciler(new FileSystem(), ledger)
            .MarkMissingIdentities([game], Array.Empty<string>());

        Assert.Equal(1, result.MarkedNeedsReinstall);
        Assert.Equal(InstallState.NeedsReinstall, ledger.Get(record.Fingerprint)!.State);
    }

    [Theory]
    [InlineData(DirectoryWritability.NeedsElevation)]
    [InlineData(DirectoryWritability.Unknown)]
    public void Never_calls_a_permission_limited_directory_a_missing_reinstall(DirectoryWritability writability)
    {
        using var workspace = new TempDirectory();
        var deploy = Path.Combine(workspace.Root, "game");
        Directory.CreateDirectory(deploy);
        File.WriteAllText(Path.Combine(deploy, "Game.exe"), "exe");
        var ledger = new JsonInstallLedger(new FileSystem(), Path.Combine(workspace.Root, "ledger.json"));
        var record = Record("missing02", deploy, InstallState.Installed);
        ledger.Save(record);

        var result = new InstallLedgerReconciler(new FileSystem(), ledger)
            .MarkMissingIdentities([Discovered(deploy, writability)], Array.Empty<string>());

        Assert.Equal(0, result.MarkedNeedsReinstall);
        Assert.Equal(InstallState.Installed, ledger.Get(record.Fingerprint)!.State);
    }

    [Fact]
    public void Skips_all_absence_inference_if_any_scanner_failed()
    {
        using var workspace = new TempDirectory();
        var deploy = Path.Combine(workspace.Root, "game");
        Directory.CreateDirectory(deploy);
        File.WriteAllText(Path.Combine(deploy, "Game.exe"), "exe");
        var ledger = new JsonInstallLedger(new FileSystem(), Path.Combine(workspace.Root, "ledger.json"));
        var record = Record("missing03", deploy, InstallState.Healthy);
        ledger.Save(record);

        var result = new InstallLedgerReconciler(new FileSystem(), ledger)
            .MarkMissingIdentities([Discovered(deploy, DirectoryWritability.Writable)], ["Steam scan failed"]);

        Assert.Equal(0, result.MarkedNeedsReinstall);
        Assert.Equal(InstallState.Healthy, ledger.Get(record.Fingerprint)!.State);
        Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public void Leaves_rolled_back_install_unchanged_even_when_identity_is_absent()
    {
        using var workspace = new TempDirectory();
        var deploy = Path.Combine(workspace.Root, "game");
        Directory.CreateDirectory(deploy);
        File.WriteAllText(Path.Combine(deploy, "Game.exe"), "exe");
        var ledger = new JsonInstallLedger(new FileSystem(), Path.Combine(workspace.Root, "ledger.json"));
        var record = Record("missing04", deploy, InstallState.RolledBack);
        ledger.Save(record);

        var result = new InstallLedgerReconciler(new FileSystem(), ledger)
            .MarkMissingIdentities([Discovered(deploy, DirectoryWritability.Writable)], Array.Empty<string>());

        Assert.Equal(0, result.MarkedNeedsReinstall);
        Assert.Equal(InstallState.RolledBack, ledger.Get(record.Fingerprint)!.State);
    }

    private static InstallRecord Record(string fingerprint, string deploy, InstallState state) => new()
    {
        Fingerprint = fingerprint,
        GameTitle = "Fixture Game",
        DeployDirectory = deploy,
        LatestPlanId = Guid.NewGuid(),
        BackupRoot = Path.Combine(Path.GetTempPath(), "deployer-test-backups"),
        State = state,
        PayloadVersion = "test",
        IdentityFileName = "winmm.dll",
        InstalledUtc = DateTime.UtcNow,
    };

    private static DiscoveredGame Discovered(string deploy, DirectoryWritability writability) => new()
    {
        Title = "Fixture Game",
        Platform = GamePlatform.Steam,
        InstallRoot = Path.GetFullPath(deploy),
        ExecutableCandidates = [Path.Combine(deploy, "Game.exe")],
        Writability = writability,
        ScannerSource = "test",
    };

    private sealed class TempDirectory : IDisposable
    {
        private readonly string _parent = Path.Combine(Path.GetTempPath(), "OptiScalerLedgerReconcileTests");
        private readonly string _id = Guid.NewGuid().ToString("N");
        public string Root { get; }
        public TempDirectory() { Root = Path.GetFullPath(Path.Combine(_parent, _id)); Directory.CreateDirectory(Root); }
        public void Dispose()
        {
            if (!string.Equals(Path.GetDirectoryName(Root), Path.GetFullPath(_parent), StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Path.GetFileName(Root), _id, StringComparison.Ordinal) || !Guid.TryParseExact(_id, "N", out _)) return;
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }
}

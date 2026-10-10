using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using Deployer.Core.Deployment;
using Deployer.Core.Domain;
using Deployer.Core.Profiles;
using Deployer.Core.Util;
using Xunit;

namespace Deployer.Core.Tests;

public class RollbackExecutorTests
{
    private const string Root = @"C:\backup-root";
    private const string Fingerprint = "a1b2c3d4";
    private const string Deploy = @"C:\game\Binaries\Win64";
    private readonly MockFileSystem _fs = new();
    private readonly BackupStore _store;
    private readonly RollbackExecutor _rollback;

    public RollbackExecutorTests()
    {
        _store = new BackupStore(_fs, Root);
        _rollback = new RollbackExecutor(_fs, new AtomicFileWriter(_fs), _store);
    }

    private DeploymentManifest SaveManifest(Guid id, IReadOnlyList<BackupEntry> backups,
        IReadOnlyList<string> written, IReadOnlyList<string>? directories = null)
    {
        var manifest = new DeploymentManifest
        {
            PlanId = id,
            CreatedUtc = DateTime.UtcNow,
            Fingerprint = Fingerprint,
            GameTitle = "Game",
            DeployDirectory = Deploy,
            Payload = new PayloadInfo(PayloadOrigin.LocalBuild, "1", @"D:\payload"),
            Identity = new IdentityDecision(DllIdentity.Dxgi, "dxgi.dll", IdentityStatus.Resolved,
                Array.Empty<DllIdentity>(), null, null),
            Profile = BuiltinProfiles.AmdRdna4,
            Backups = backups,
            WrittenPaths = written,
            CreatedDirectories = directories ?? Array.Empty<string>(),
        };
        _store.WriteManifest(manifest);
        return manifest;
    }

    private static string BackupPath(Guid id, string name) => $@"{Root}\{Fingerprint}\{id:D}\{name}";

    [Fact]
    public void Removes_only_new_files_listed_in_manifest()
    {
        var added = Deploy + @"\OptiScaler\new.dll";
        var unrelated = Deploy + @"\game.optideploy.tmp";
        _fs.AddFile(added, new MockFileData("new"));
        _fs.AddFile(unrelated, new MockFileData("user data"));
        var manifest = SaveManifest(Guid.NewGuid(), Array.Empty<BackupEntry>(), new[] { added });

        var result = _rollback.Rollback(_store.ManifestPathFor(Fingerprint, manifest.PlanId));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.False(_fs.File.Exists(added));
        Assert.Equal("user data", _fs.File.ReadAllText(unrelated));
    }

    [Fact]
    public void Restores_valid_shadow_backup_and_verifies_hash()
    {
        var id = Guid.NewGuid();
        var original = Deploy + @"\version.dll";
        var backupPath = BackupPath(id, "version.dll");
        _fs.AddFile(original, new MockFileData("Opti replacement"));
        _fs.AddFile(backupPath, new MockFileData("Game original"));
        var entry = new BackupEntry(original, backupPath, BackupReason.OverwriteGameFile,
            Hashing.Sha256OfFile(_fs, backupPath));
        SaveManifest(id, new[] { entry }, new[] { original });

        var result = _rollback.Rollback(_store.ManifestPathFor(Fingerprint, id));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("Game original", _fs.File.ReadAllText(original));
        Assert.Contains(original, result.RestoredPaths);
    }

    [Fact]
    public void Corrupt_backup_does_not_overwrite_or_delete_original()
    {
        var id = Guid.NewGuid();
        var original = Deploy + @"\version.dll";
        var backupPath = BackupPath(id, "version.dll");
        _fs.AddFile(original, new MockFileData("Opti replacement"));
        _fs.AddFile(backupPath, new MockFileData("Game original"));
        var hash = Hashing.Sha256OfFile(_fs, backupPath);
        SaveManifest(id, new[] { new BackupEntry(original, backupPath, BackupReason.OverwriteGameFile, hash) },
            new[] { original });
        _fs.File.WriteAllText(backupPath, "corrupted");

        var result = _rollback.Rollback(_store.ManifestPathFor(Fingerprint, id));

        Assert.False(result.Success);
        Assert.Equal("Opti replacement", _fs.File.ReadAllText(original));
        Assert.Contains(original, result.ErrorMessage);
    }

    [Fact]
    public void Missing_backup_does_not_make_backed_up_path_eligible_for_deletion()
    {
        var id = Guid.NewGuid();
        var original = Deploy + @"\version.dll";
        var absentBackup = BackupPath(id, "version.dll");
        _fs.AddFile(original, new MockFileData("keep this"));
        SaveManifest(id, new[] { new BackupEntry(original, absentBackup, BackupReason.OverwriteGameFile) },
            new[] { original });

        var result = _rollback.Rollback(_store.ManifestPathFor(Fingerprint, id));

        Assert.False(result.Success);
        Assert.Equal("keep this", _fs.File.ReadAllText(original));
    }

    [Fact]
    public void Rejects_manifest_paths_outside_deployment_directory_without_deleting_them()
    {
        var outside = @"C:\game\important.dll";
        _fs.AddFile(outside, new MockFileData("do not delete"));
        var manifest = SaveManifest(Guid.NewGuid(), Array.Empty<BackupEntry>(), new[] { outside });

        var result = _rollback.Rollback(_store.ManifestPathFor(Fingerprint, manifest.PlanId));

        Assert.False(result.Success);
        Assert.Equal("do not delete", _fs.File.ReadAllText(outside));
    }

    [Fact]
    public void Rejects_backup_entry_from_another_plan()
    {
        var id = Guid.NewGuid();
        var otherPlan = Guid.NewGuid();
        var original = Deploy + @"\version.dll";
        var backupPath = BackupPath(otherPlan, "version.dll");
        _fs.AddFile(original, new MockFileData("keep"));
        _fs.AddFile(backupPath, new MockFileData("foreign backup"));
        var manifest = SaveManifest(id,
            new[] { new BackupEntry(original, backupPath, BackupReason.OverwriteGameFile) }, new[] { original });

        var result = _rollback.Rollback(_store.ManifestPathFor(Fingerprint, manifest.PlanId));

        Assert.False(result.Success);
        Assert.Equal("keep", _fs.File.ReadAllText(original));
    }

    [Fact]
    public void Rejects_manifest_loaded_from_a_different_plan_path()
    {
        var manifest = SaveManifest(Guid.NewGuid(), Array.Empty<BackupEntry>(), Array.Empty<string>());
        var otherPath = _store.ManifestPathFor(Fingerprint, Guid.NewGuid());
        _fs.AddFile(otherPath, new MockFileData(Deployer.Core.Serialization.DeployerJson.Serialize(manifest)));

        var result = _rollback.Rollback(otherPath);

        Assert.False(result.Success);
        Assert.Contains("路径与清单", result.ErrorMessage);
    }

    [Fact]
    public void Removes_only_empty_directories_created_by_manifest()
    {
        var made = Deploy + @"\OptiScaler";
        _fs.AddFile(made + @"\new.dll", new MockFileData("x"));
        _fs.AddFile(made + @"\user-mod.dll", new MockFileData("keep"));
        var manifest = SaveManifest(Guid.NewGuid(), Array.Empty<BackupEntry>(), new[] { made + @"\new.dll" },
            new[] { made });

        _rollback.Rollback(_store.ManifestPathFor(Fingerprint, manifest.PlanId));

        Assert.True(_fs.Directory.Exists(made));
        Assert.True(_fs.File.Exists(made + @"\user-mod.dll"));
    }

    private Dictionary<string, string> Snapshot(string root)
    {
        var snapshot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!_fs.Directory.Exists(root)) return snapshot;
        foreach (var file in _fs.Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            snapshot[file] = Hashing.Sha256OfFile(_fs, file);
        foreach (var dir in _fs.Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
            snapshot[dir + "\\"] = "<dir>";
        return snapshot;
    }

    [Fact]
    public void Rollback_of_empty_manifest_does_not_change_game_tree()
    {
        _fs.AddFile(Deploy + @"\Game.exe", new MockFileData("exe"));
        _fs.AddFile(Deploy + @"\Game.pak", new MockFileData("pak"));
        var before = Snapshot(Deploy);
        var manifest = SaveManifest(Guid.NewGuid(), Array.Empty<BackupEntry>(), Array.Empty<string>());

        var result = _rollback.Rollback(_store.ManifestPathFor(Fingerprint, manifest.PlanId));

        Assert.True(result.Success);
        Assert.Equal(before, Snapshot(Deploy));
    }
}

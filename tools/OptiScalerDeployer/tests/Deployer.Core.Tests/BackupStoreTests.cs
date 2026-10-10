using System.IO.Abstractions.TestingHelpers;
using Deployer.Core.Deployment;
using Deployer.Core.Domain;
using Deployer.Core.Profiles;
using Deployer.Core.Util;
using Xunit;

namespace Deployer.Core.Tests;

public class BackupStoreTests
{
    private const string Root = @"C:\backup-root";
    private const string Fingerprint = "a1b2c3d4";
    private readonly MockFileSystem _fs = new();
    private BackupStore Store() => new(_fs, Root);
    private static string PlanDir(Guid id) => $@"{Root}\{Fingerprint}\{id:D}";

    [Fact]
    public void Backup_copies_bytes_and_returns_the_original_hash()
    {
        var id = Guid.NewGuid();
        const string original = @"C:\game\version.dll";
        var backup = PlanDir(id) + @"\version.dll";
        _fs.AddFile(original, new MockFileData("game dll"));

        var result = Store().Backup(new BackupEntry(original, backup, BackupReason.OverwriteGameFile));

        Assert.Equal("game dll", _fs.File.ReadAllText(backup));
        Assert.Equal(Hashing.Sha256OfFile(_fs, original), result.Sha256);
        Assert.Equal("game dll", _fs.File.ReadAllText(original));
    }

    [Fact]
    public void Backup_refuses_missing_original_or_existing_backup()
    {
        var id = Guid.NewGuid();
        var entry = new BackupEntry(@"C:\game\file.dll", PlanDir(id) + @"\file.dll", BackupReason.OverwriteGameFile);
        Assert.Throws<FileNotFoundException>(() => Store().Backup(entry));
        _fs.AddFile(entry.OriginalPath, new MockFileData("original"));
        _fs.AddFile(entry.BackupPath, new MockFileData("keep existing"));
        Assert.Throws<IOException>(() => Store().Backup(entry));
        Assert.Equal("keep existing", _fs.File.ReadAllText(entry.BackupPath));
    }

    [Fact]
    public void Rejects_backup_and_fingerprint_paths_outside_store_root()
    {
        Assert.Throws<ArgumentException>(() => Store().DirectoryFor("../../escape", Guid.NewGuid()));
        var entry = new BackupEntry(@"C:\game\file.dll", @"C:\outside\file.dll", BackupReason.OverwriteGameFile);
        Assert.Throws<InvalidDataException>(() => Store().Backup(entry));
    }

    [Fact]
    public void Manifest_and_progress_roundtrip_atomically()
    {
        var store = Store();
        var id = Guid.NewGuid();
        var manifest = new DeploymentManifest
        {
            PlanId = id,
            CreatedUtc = DateTime.UtcNow,
            Fingerprint = Fingerprint,
            GameTitle = "Game",
            DeployDirectory = @"C:\game",
            Payload = new PayloadInfo(PayloadOrigin.LocalBuild, "1", @"D:\payload"),
            Identity = new IdentityDecision(DllIdentity.Dxgi, "dxgi.dll", IdentityStatus.Resolved,
                Array.Empty<DllIdentity>(), null, null),
            Profile = BuiltinProfiles.AmdRdna4,
            Backups = Array.Empty<BackupEntry>(),
            WrittenPaths = Array.Empty<string>(),
        };
        store.WriteManifest(manifest);
        JsonAssert.Equivalent(manifest, store.ReadManifest(store.ManifestPathFor(Fingerprint, id)));
        var progress = DeploymentProgress.Empty(id, store.ManifestPathFor(Fingerprint, id));
        store.WriteProgress(progress);
        JsonAssert.Equivalent(progress, store.ReadProgress(store.ProgressPathFor(Fingerprint, id)));
    }

    [Fact]
    public void Existing_corrupt_manifest_is_reported_with_inner_exception()
    {
        var store = Store();
        var path = store.ManifestPathFor(Fingerprint, Guid.NewGuid());
        _fs.AddFile(path, new MockFileData("{ broken"));

        var error = Assert.Throws<InvalidDataException>(() => store.ReadManifest(path));
        Assert.NotNull(error.InnerException);
    }

    [Fact]
    public void Prune_rejects_negative_keep_and_keeps_newest_plans()
    {
        var store = Store();
        Assert.Throws<ArgumentOutOfRangeException>(() => store.Prune(Fingerprint, -1));
        Assert.Empty(store.Prune(Fingerprint, 3));
    }
}

using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using System.Reflection;
using Deployer.Core.Domain;
using Deployer.Core.Persistence;
using Deployer.Core.Serialization;
using Xunit;

namespace Deployer.Core.Tests;

public class JsonInstallLedgerTests
{
    private const string LedgerPath = @"C:\users\tester\AppData\Local\OptiScalerDeployer\ledger.json";

    private readonly MockFileSystem _fs = new();

    private JsonInstallLedger Ledger() => new(_fs, LedgerPath);

    private static InstallRecord Record(string fingerprint, string title = "Game A") => new()
    {
        Fingerprint = fingerprint,
        GameTitle = title,
        DeployDirectory = $@"C:\Games\{title}\Binaries\Win64",
        LatestPlanId = Guid.NewGuid(),
        BackupRoot = $@"C:\backup-root\{fingerprint}",
        State = InstallState.Installed,
        PayloadVersion = "0.9.1.0",
        IdentityFileName = "dxgi.dll",
        InstalledUtc = new DateTime(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc),
    };

    [Fact]
    public void All_is_empty_when_ledger_file_missing()
    {
        Assert.Empty(Ledger().All());
    }

    [Fact]
    public void Save_then_Get_roundtrips()
    {
        var record = Record("aaaa1111");
        Ledger().Save(record);

        Assert.Equal(record, new JsonInstallLedger(_fs, LedgerPath).Get("aaaa1111"));
    }

    [Fact]
    public void Save_replaces_an_existing_entry_instead_of_duplicating_it()
    {
        var ledger = Ledger();
        ledger.Save(Record("aaaa1111", "Game A"));
        ledger.Save(Record("AAAA1111", "Game A renamed"));

        var reloaded = new JsonInstallLedger(_fs, LedgerPath);
        Assert.Single(reloaded.All());
        Assert.Equal("Game A renamed", reloaded.Get("aaaa1111")!.GameTitle);
    }

    [Fact]
    public void Two_games_coexist_without_shared_state()
    {
        var ledger = Ledger();
        ledger.Save(Record("aaaa1111", "Game A"));
        ledger.Save(Record("bbbb2222", "Game B"));

        var reloaded = new JsonInstallLedger(_fs, LedgerPath);
        Assert.Equal(2, reloaded.All().Count);
        Assert.NotEqual(reloaded.Get("aaaa1111")!.BackupRoot, reloaded.Get("bbbb2222")!.BackupRoot);
    }

    [Fact]
    public void Previously_created_instances_do_not_overwrite_each_others_saves()
    {
        var first = Ledger();
        var second = Ledger();

        first.Save(Record("aaaa1111", "Game A"));
        second.Save(Record("bbbb2222", "Game B"));

        Assert.NotNull(first.Get("aaaa1111"));
        Assert.NotNull(first.Get("bbbb2222"));
        Assert.NotNull(second.Get("aaaa1111"));
        Assert.NotNull(second.Get("bbbb2222"));
    }

    [Fact]
    public void Remove_deletes_only_the_named_entry()
    {
        var ledger = Ledger();
        ledger.Save(Record("aaaa1111"));
        ledger.Save(Record("bbbb2222"));

        Assert.True(new JsonInstallLedger(_fs, LedgerPath).Remove("aaaa1111"));

        var reloaded = new JsonInstallLedger(_fs, LedgerPath);
        Assert.Null(reloaded.Get("aaaa1111"));
        Assert.NotNull(reloaded.Get("bbbb2222"));
    }

    [Fact]
    public void Remove_unknown_fingerprint_returns_false_without_rewriting()
    {
        var record = Record("aaaa1111");
        Ledger().Save(record);
        var before = _fs.File.ReadAllText(LedgerPath);

        Assert.False(Ledger().Remove("deadbeef"));
        Assert.Equal(before, _fs.File.ReadAllText(LedgerPath));
    }

    [Fact]
    public void SetState_changes_only_the_state()
    {
        var record = Record("aaaa1111");
        Ledger().Save(record);

        Assert.True(new JsonInstallLedger(_fs, LedgerPath).SetState("aaaa1111", InstallState.RolledBack));

        var updated = new JsonInstallLedger(_fs, LedgerPath).Get("aaaa1111")!;
        Assert.Equal(InstallState.RolledBack, updated.State);
        Assert.Equal(record.LatestPlanId, updated.LatestPlanId);
        Assert.Equal(record.GameTitle, updated.GameTitle);
    }

    [Fact]
    public void SetState_on_unknown_fingerprint_returns_false()
    {
        Assert.False(Ledger().SetState("deadbeef", InstallState.Failed));
    }

    [Fact]
    public void Fingerprint_matching_is_case_insensitive()
    {
        Ledger().Save(Record("AaAa1111"));

        Assert.NotNull(new JsonInstallLedger(_fs, LedgerPath).Get("aaaa1111"));
    }

    [Fact]
    public void Corrupt_ledger_is_reported_and_never_overwritten()
    {
        const string corrupt = "<<< not json >>>";
        _fs.AddFile(LedgerPath, new MockFileData(corrupt));
        var ledger = Ledger();

        Assert.Throws<InvalidDataException>(() => ledger.All());
        Assert.Contains("无效", ledger.LoadError);
        Assert.Throws<InvalidDataException>(() => ledger.Save(Record("aaaa1111")));
        Assert.Equal(corrupt, _fs.File.ReadAllText(LedgerPath));
    }

    [Fact]
    public void Invalid_records_are_not_silently_dropped_or_overwritten()
    {
        const string invalid = "[null]";
        _fs.AddFile(LedgerPath, new MockFileData(invalid));
        var ledger = Ledger();

        Assert.Throws<InvalidDataException>(() => ledger.All());
        Assert.Contains("Fingerprint", ledger.LoadError);
        Assert.Throws<InvalidDataException>(() => ledger.Remove("anything"));
        Assert.Equal(invalid, _fs.File.ReadAllText(LedgerPath));
    }

    [Fact]
    public void Failed_atomic_replace_keeps_disk_and_memory_unchanged()
    {
        var original = Record("aaaa1111");
        _fs.AddFile(LedgerPath, new MockFileData(DeployerJson.Serialize(new[] { original })));
        var fs = FailingReplaceFileSystem(_fs);
        var ledger = new JsonInstallLedger(fs, LedgerPath);

        Assert.Throws<IOException>(() => ledger.Save(Record("aaaa1111", "Changed")));

        Assert.Equal(original, ledger.Get("aaaa1111"));
        Assert.Equal(original, new JsonInstallLedger(_fs, LedgerPath).Get("aaaa1111"));
    }

    [Fact]
    public void Save_creates_the_parent_directory()
    {
        Ledger().Save(Record("aaaa1111"));

        Assert.True(_fs.File.Exists(LedgerPath));
    }

    private static IFileSystem FailingReplaceFileSystem(IFileSystem inner)
    {
        var fileProxy = DispatchProxy.Create<IFile, FaultingFile>();
        ((FaultingFile)(object)fileProxy).Inner = inner.File;

        var fileSystemProxy = DispatchProxy.Create<IFileSystem, ForwardingFileSystem>();
        var forwarder = (ForwardingFileSystem)(object)fileSystemProxy;
        forwarder.Inner = inner;
        forwarder.FileOverride = fileProxy;
        return fileSystemProxy;
    }

    public class FaultingFile : DispatchProxy
    {
        public IFile Inner { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name is "Replace" or "Move")
            {
                throw new IOException("Simulated atomic replace failure.");
            }

            return targetMethod!.Invoke(Inner, args);
        }
    }

    public class ForwardingFileSystem : DispatchProxy
    {
        public IFileSystem Inner { get; set; } = null!;
        public IFile FileOverride { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            return targetMethod!.Name == "get_File"
                ? FileOverride
                : targetMethod.Invoke(Inner, args);
        }
    }
}

using System.IO.Abstractions.TestingHelpers;
using Deployer.Core.Domain;
using Deployer.Core.Identity;
using Deployer.TestData;
using Xunit;

namespace Deployer.Core.Tests;

public class DllIdentitySelectorTests
{
    private const string Dir = @"C:\game\Binaries\Win64";
    private readonly MockFileSystem _fs = new();
    private readonly FakeFileVersionReader _versions = new();

    private DllIdentitySelector Selector() => new(_fs, _versions);

    private static IdentityContext Context(
        GamePlatform platform = GamePlatform.Steam,
        bool vulkanOnly = false,
        bool allowOverwrite = false,
        bool preferAsi = false) => new(platform, vulkanOnly, Dir, allowOverwrite, preferAsi);

    private void AddGameFile(DllIdentity identity, string? originalFilename = null)
    {
        var path = _fs.Path.Combine(Dir, identity.FileName());
        _fs.AddFile(path, new MockFileData("GAME OWN BINARY"));
        if (originalFilename is not null)
        {
            _versions.SetOriginalFilename(path, originalFilename);
        }
    }

    private void AddPreviousOptiScaler(DllIdentity identity)
    {
        var path = _fs.Path.Combine(Dir, identity.FileName());
        _fs.AddFile(path, new MockFileData("OPTISCALER BINARY"));
        _versions.SetOriginalFilename(path, "OptiScaler.dll");
    }

    [Fact]
    public void Empty_steam_directory_picks_dxgi_without_backup()
    {
        var decision = Selector().Select(Context());

        Assert.Equal(DllIdentity.Dxgi, decision.Chosen);
        Assert.Equal("dxgi.dll", decision.FileName);
        Assert.Equal(IdentityStatus.Resolved, decision.Status);
        Assert.Null(decision.ExistingFileReason);
        Assert.Empty(decision.SkippedDueToConflict);
    }

    [Fact]
    public void Xbox_prefers_winmm_and_vulkan_only_also_prefers_winmm()
    {
        Assert.Equal(DllIdentity.Winmm, Selector().Select(Context(GamePlatform.Xbox)).Chosen);
        Assert.Equal(DllIdentity.Winmm, Selector().Select(Context(vulkanOnly: true)).Chosen);
    }

    [Fact]
    public void Game_owned_candidate_without_version_resource_is_conflict_and_next_is_used()
    {
        AddGameFile(DllIdentity.Winmm);

        var decision = Selector().Select(Context(GamePlatform.Xbox));

        Assert.Equal(DllIdentity.Version, decision.Chosen);
        Assert.Equal(new[] { DllIdentity.Winmm }, decision.SkippedDueToConflict);
        Assert.Contains("winmm.dll", decision.UserPromptMessage);
        Assert.Null(decision.ExistingFileReason);
    }

    [Fact]
    public void Existing_optiscaler_file_is_selected_and_marked_for_previous_install_backup()
    {
        AddPreviousOptiScaler(DllIdentity.Winmm);
        _versions.SetOriginalFilename(_fs.Path.Combine(Dir, "winmm.dll"), "optiscaler.DLL");

        var decision = Selector().Select(Context(GamePlatform.Xbox));

        Assert.Equal(DllIdentity.Winmm, decision.Chosen);
        Assert.Equal(BackupReason.OverwritePreviousOpti, decision.ExistingFileReason);
        Assert.Equal(IdentityStatus.Resolved, decision.Status);
    }

    [Fact]
    public void Skips_conflicts_until_an_existing_previous_optiscaler_slot_is_found()
    {
        AddGameFile(DllIdentity.Winmm, "GameAudio.dll");
        AddPreviousOptiScaler(DllIdentity.Version);

        var decision = Selector().Select(Context(GamePlatform.Xbox));

        Assert.Equal(DllIdentity.Version, decision.Chosen);
        Assert.Equal(new[] { DllIdentity.Winmm }, decision.SkippedDueToConflict);
        Assert.Equal(BackupReason.OverwritePreviousOpti, decision.ExistingFileReason);
        Assert.Contains("跳过", decision.UserPromptMessage);
    }

    [Fact]
    public void All_game_owned_candidates_require_explicit_overwrite_decision()
    {
        foreach (var identity in DllIdentitySelector.XboxPriority)
        {
            AddGameFile(identity, "Other.dll");
        }

        var decision = Selector().Select(Context(GamePlatform.Xbox));

        Assert.Equal(IdentityStatus.RequiresUserDecision, decision.Status);
        Assert.Equal(DllIdentity.Winmm, decision.Chosen);
        Assert.Equal(BackupReason.OverwriteGameFile, decision.ExistingFileReason);
        Assert.Equal(DllIdentitySelector.XboxPriority, decision.SkippedDueToConflict);
        Assert.Contains("覆盖并备份", decision.UserPromptMessage);
        Assert.Contains("OptiScaler.asi", decision.UserPromptMessage);
        Assert.Contains("System32", decision.UserPromptMessage);
    }

    [Fact]
    public void Explicit_overwrite_resolves_all_conflicts_but_still_marks_backup_reason()
    {
        foreach (var identity in DllIdentitySelector.XboxPriority)
        {
            AddGameFile(identity, "Other.dll");
        }

        var decision = Selector().Select(Context(GamePlatform.Xbox, allowOverwrite: true));

        Assert.Equal(IdentityStatus.Resolved, decision.Status);
        Assert.Equal(BackupReason.OverwriteGameFile, decision.ExistingFileReason);
        Assert.DoesNotContain(DllIdentity.Winmm, decision.SkippedDueToConflict);
        Assert.Contains("已按你的设置选择覆盖 winmm.dll", decision.UserPromptMessage);
    }

    [Fact]
    public void Prefer_asi_bypasses_dll_priorities_but_checks_existing_asi_conflict()
    {
        foreach (var identity in DllIdentitySelector.XboxPriority)
        {
            AddGameFile(identity, "Other.dll");
        }

        var free = Selector().Select(Context(GamePlatform.Xbox, preferAsi: true));
        Assert.Equal(DllIdentity.Asi, free.Chosen);
        Assert.Equal("OptiScaler.asi", free.FileName);
        Assert.Equal(IdentityStatus.Resolved, free.Status);
        Assert.Null(free.ExistingFileReason);

        var asiPath = _fs.Path.Combine(Dir, "OptiScaler.asi");
        _fs.AddFile(asiPath, new MockFileData("OTHER ASI"));
        _versions.SetOriginalFilename(asiPath, "OtherMod.asi");
        var conflict = Selector().Select(Context(GamePlatform.Xbox, preferAsi: true));
        Assert.Equal(IdentityStatus.RequiresUserDecision, conflict.Status);
        Assert.Equal(BackupReason.OverwriteGameFile, conflict.ExistingFileReason);
    }

    [Fact]
    public void Platform_and_vulkan_priority_tables_match_spec()
    {
        Assert.Equal(
            new[] { DllIdentity.Winmm, DllIdentity.Version, DllIdentity.Dbghelp,
                DllIdentity.Wininet, DllIdentity.Winhttp, DllIdentity.Dxgi, DllIdentity.D3d12 },
            DllIdentitySelector.PriorityFor(GamePlatform.Xbox, false));
        Assert.Equal(
            new[] { DllIdentity.Winmm, DllIdentity.Version, DllIdentity.Dbghelp, DllIdentity.Dxgi },
            DllIdentitySelector.PriorityFor(GamePlatform.Steam, true));
        Assert.Equal(
            new[] { DllIdentity.Dxgi, DllIdentity.Winmm, DllIdentity.Version, DllIdentity.Dbghelp, DllIdentity.D3d12 },
            DllIdentitySelector.PriorityFor(GamePlatform.Epic, false));
    }

    [Fact]
    public void Rejects_empty_deploy_directory()
    {
        Assert.Throws<ArgumentException>(() => Selector().Select(Context() with { DeployDirectory = " " }));
    }
}
